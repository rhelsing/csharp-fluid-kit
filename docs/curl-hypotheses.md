# Curl hypotheses — how to make a wave break forward

Companion to `solver-ledger.md` (solvers) and `../shorewaves/docs/design/` (paths A–E).
This file is the **curl** thread: three ways to get an overhanging lip, three ways to find
where to put it, and what each has actually been shown to do.

**Scene 28 (`28_shore_curl`) is where all of this happens. Scene 26 is the reference and must
stay untouched** — same bed, same solver, so any curl claim is A/B-able against it. Forked at
fork time: `ShoreCurl.cs`, `water_curl.gdshader`. Shared and read-only: `ShallowWaterKp`,
`ShoreScenario`, `Bathymetry`, `sand_min.gdshader`, `debug_surface.gdshader`, `water.gdshader`.

---

## 0. The thing that makes this necessary

KP07 solves the 2D shallow-water equations: state is `Q = (w, hu, hv, hc)` on a 2D grid,
velocity has no vertical structure, pressure is hydrostatic. **One surface height per (x,z)
column.** The render mesh is a flat `PlaneMesh` (92.16 m, 766² subdivisions, 0.12 m quads)
whose vertex shader writes **only `VERTEX.y`**.

So `y = h(x,z)` is a graph of a function. An overhanging lip is not hard to compute here — it
is **unrepresentable**. No solver improvement and no resolution increase changes that. Every
hypothesis below is a way of escaping single-valuedness.

---

## 1. Three hypotheses for the curl

### A — Trochoidal displacement (make the sheet fold)

Write `VERTEX.xz` as well as `.y` in `water_curl.gdshader`, so points move in circles rather
than up and down. Past a steepness threshold the sheet self-intersects, and a self-intersecting
sheet is an overhang.

- **Cheapest possible.** No new geometry, no new sim, same draw call.
- **Limits, both hard:** a sheet has no *inside*, so a folded tube is see-through and backfaces
  show; and folding inverts triangles, so normals flip and it z-fights itself. At 0.12 m quads
  a 1–3 m tube is 8–25 quads across.
- **Honest scope:** good for the *lean* — crests pitching forward — everywhere outside the
  barrel. Not for the tube.
- **Note:** `gerstner_amp` in `water.gdshader` is NOT this. `gerstner_y` returns a vertical
  offset only, and fades out below 9 m depth (`smoothstep(3.0, 9.0, h)`) — i.e. it is disabled
  exactly where breaking happens.

### B — Moving 3D box, Jacobi-projected (real dynamics)

A 3D volume window riding the break line, pressure-projected, feeding energy back into the
surface.

- **Jacobi, not multigrid, and that is load-bearing.** Jacobi is fine-grid only, so the
  sampled-mask coarsening failure that stalled scene 27 structurally cannot arise
  (`solver-ledger.md` §10, and §7a's warning that the no-Galerkin shortcut holds only while the
  stamp's geometry is analytic). Same reason CG would work.
- **The moving window is precedented:** `GpuStampSolver.Scroll(sx, sy)` already does
  world-anchored clipmap shifting for scene 50's boat window in 2D.
- **The hard part is NOT the solve — it is the seam.** Two-way coupling between a
  depth-averaged 2D solver and a 3D volume has to conserve mass across the boundary, and the
  box edges need absorbing treatment or the window rings.
- **Prior art in-repo:** scene 27 (`WaveSim3D`) has the projection, masking, BCs and raymarch
  working; only the multigrid coarsening was wrong. Much of it is reusable.

### E — SDF barrel carve (no topology limit)

A ridged capsule smooth-subtracted from the water so the surface wraps a tube-shaped void.

- **Ported from `../water-kit` scene 210** (`shaders/moana_water_barrel.gdshader`, itself a fork
  of the 204 Wallis oracle). Shape carried over verbatim in structure: capsule + `abs(fbm_4)`
  ridge in the spun angular frame, `sdSmoothSubtraction` for the lip feather.
- **The port's one real change:** 210 carves an analytic sin/cos swell; `barrel_carve.gdshader`
  carves KP07's **real free surface**, sampled from the same `tx_state`/`tx_bottom` the mesh
  water uses. The tube is cut into the wave the solver actually made.
- **No topology limit, no mesh resolution ceiling.** Cost is per-pixel — resolution-independent,
  which is the property that makes it worth having.
- **Status: shape works, placement does not.** See §3.

---

## 2. Three ways to track the wave — and they are not competing

Most of what a tracker needs is **already computed and currently discarded**:

```
tx_derived:   r = n.x    g = n.z    b = foamVis    a = aeration
```

`n.x`/`n.z` are the surface gradient, so **steepness** and **facing direction** are already
there per texel, and `aeration` is KP07's own breaking intensity.

| | Method | Cost | Produces | Suits |
|---|---|---|---|---|
| **1** | **Break-curve reduction** — reduce `pass_step`'s `brk`/`bore`/`swash` (currently spent entirely on foam) to a per-row table of crest x / height / strength | One N²→N reduction pass + a small texture | An explicit **curve** | **A** — mesh displacement moves specific vertices along a specific line, and a field is not enough |
| **2** | **Normals / steepness** — read `tx_derived` directly; steepness from gradient magnitude, peel direction from gradient direction | **Zero extra passes.** The data is already bound | A **field** | **E** — makes the carve a field operation: modulate strength by steepness, orient by gradient. No tracker to be wrong |
| **3** | **Scattered probes** — sample height/velocity at N points, pick the strongest breaker | Tiny (small buffer or readback) | A **point** (+ heading) | **B** — a moving box needs one centre, not a field. Same shape as `BoatRider`/`ReactiveWaveField` sampling, and the 24-series tank taps where 8 numbers per tick drive everything |

**✅ Method 2 is VERIFIED ON SCREEN** — the band tracks the breaking front and moves with it.
Tuned gate: **`steep_min = 0.515`, `steep_max = 0.76`** (aeration gain 1.08). Note how far that
is from the 0.06 first guessed: a breaking front is an order of magnitude steeper than ordinary
swell, so a low gate lights the whole surf zone and tells you nothing. **That number is the
barrel spawn threshold** — it is the one measured result the whole curl thread now rests on.
Channel isolation (steepness / aeration / direction, each alone with alpha = the value) was
what made it tunable; the blended view could not be.

**E wants a field, B wants a point, A wants a curve.** That is why there is no single right
tracker, and why the method must be selectable rather than chosen once.

**Rule: a tracker that is OFF costs nothing.** Not "cheap" — nothing. Implemented by hiding the
debug mesh (Godot skips hidden geometry entirely) and gating any tracker pass on the selected
method, so switching to Off removes the dispatch, not just the pixels.

---

## 3. Status — what is actually shown to work

| Item | State |
|---|---|
| Scene 28 forked, 26 untouched | ✅ 120 fps, sim ×1.00, nan 0, shore intact |
| `barrel_carve.gdshader` compiles and composites over the mesh water | ✅ discards on miss, so 26's look shows through |
| Carve samples KP07's real surface (not an analytic swell) | ✅ |
| Barrel **placement** | ❌ `br_center` is a compile-time constant. **Nothing connects the sim to the carve.** It cannot move with the waves — not untuned, unwired |
| `br_debug` ghost | ❌ **Wrong.** 210 ghost-renders the carve *solid*; mine tints whatever the raymarch already hit, so it paints the box's screen footprint flat magenta and reveals nothing about where the capsule is |
| Carve localisation | ❌ `WaterField()` clamps its UVs, so the water SDF is defined across the whole domain and beyond. The box limits which *pixels draw*, not where the carve *exists* — hence it reads as a slab |
| A (trochoidal) | Not started |
| B (moving box) | Not started; scene 27's projection/mask/BC/raymarch reusable |

**The lesson from the first E attempt:** the shape was the easy half and it went in fine. The
half that matters is placement, and porting a shape without porting what places it produces
something that renders and means nothing. **Verify the tracker on screen before wiring it to a
carve** — which is why §2's debug visuals come first.

---

## 3b. E, redesigned — DISKS ALONG THE CREST (the current plan)

The first E attempt placed one capsule at a compile-time constant and failed. The redesign
does not place anything: the tube is a **stack of thin slices laid along the crest**, each
born from the field, each with its own age. Nothing spawns, nothing is authored.

**Each slice/capsule:**
- sits in the plane perpendicular to the local gradient (`tx_derived.rg` gives that direction free);
- carries its own **ridge seed** and **spin phase**, decorrelated per slice — this is what kills
  the repetition a single capsule has;
- has **its own age**, so it drives progressively into the wave face and collapses on its own
  schedule, rather than the whole tube appearing and vanishing at once.

**THE PEEL FALLS OUT, and it is the reason this design is right.** The shoreline curves, so the
wave does not cross the birth threshold along the whole crest simultaneously — columns trip at
different times. Slices are therefore born *in sequence down the line*, so the barrel opens
progressively: a point break, straight out of `ShoreScenario`'s bathymetry. No other hypothesis
produces that, and nothing has to be animated to get it.

Per-slice age also means a **closeout can propagate along the tube** instead of the whole thing
collapsing uniformly.

**Three parts; only the first is new:**
1. **Break-age buffer** — per column: 0 on threshold crossing, advances while breaking, decays
   after. `pass_ground` is already exactly this pattern (wetness / stranded / impact are all
   decaying per-cell memories), but its four channels are spoken for, so this wants a small
   additional texture. *This is the only genuinely new machinery, and everything time-dependent
   depends on it — steepness and gradient are instantaneous and cannot express "started when".*
2. **Slice identity** — quantize position along the local crest direction; the index seeds ridge
   noise and spin phase. Shader-only, free.
3. **Slice SDF** — capsule/disk in the ⊥ plane, radius and penetration as functions of age,
   ridged and spun, `sdSmoothSubtraction`ed as in 210. Replaces `BarrelCarveField`'s fixed capsule.

**Two timing modes, both built, toggleable** (they are not equivalent and neither is obviously
right): **wall-clock** — age advances in seconds, crash rate is a slider, simple and directly
tunable; **phase-driven** — age advances with the wave's own phase, so a slow swell barrels
slowly and a fast one crashes fast. More correct, but couples the crash to `WavePeriod`.

**Everything controllable**, per the brief: slice spacing, radius, penetration-vs-age curve,
spin speed, spin variability, ridge seed variability, birth threshold, crash rate, timing mode.

**What still does NOT fall out:** grouping. A local field cannot distinguish one 30 m barrel
from three 10 m ones, so genuine *per-barrel* behaviour (this one holds, that one shuts down
early) would need connected-component labelling — several passes, and real work. Not needed for
the look; needed the moment a barrel must be an *object*.

---

## 4. Order of work

1. **Tracker debug visuals** — method selector + visual toggle, zero cost when off. Prove on
   screen that each method finds the breaking front before anything consumes it.
2. **E as §3b** — break-age buffer, then slices along the crest. Method 2 is verified and its
   threshold measured (0.515), so the birth gate is already known. Build the age buffer FIRST
   and visualise it the same way the tracker was visualised — age is the only part that cannot
   be checked by eye from a still frame, and an unverified memory buffer is exactly the kind of
   thing that produced the magenta square.
3. **A** as the cheap lean outside the barrel, blended into E where it actually breaks.
4. **B** last — it is the only one that needs the seam solved.
