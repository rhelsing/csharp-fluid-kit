# MNA — Next Steps

Where the stamp-solver idea goes from here. Two frontiers, one engine: **audio** (a GPU
physical-modeling synth) and **graphics** (scenes that look impossibly natural and still
hit framerate). Both are the same matrix-free solver pushed to a limit.

## 0. What we know now (the foundation)

- One matrix-free stamp solver, `A x = b`, swap the stamp → wave / flow / heat / plate
  reverb / pressure-projection fluid. 2D and 3D (`GpuStampSolver`, `GpuStampSolver3D`).
- Jacobi / RBGS / CG / 2-level multigrid, all GPU-resident, predictable cost.
- **The Crank-Nicolson insight (the important one).** The implicit step is *numerically
  dissipative* — that's what buys unconditional stability, and it silently kills the
  ringing. A θ-blend (`cn`) slides backward-Euler → Crank-Nicolson (trapezoidal), which is
  non-dissipative and still stable. **This is the same integrator/oversampling tradeoff as
  a Moog ladder** — BE is the cheap integrator, CN is the trapezoidal one you oversample to
  approximate, and "mode cramping" at low oversampling is the spatial version of filter
  cramping. The audio world already solved this; we just imported it.

Everything below assumes CN-on: the medium *rings*, so it's a real resonator and a real
reactive fluid, not a decaying smear.

---

## 1. The GPU physical-modeling synth (audio-rate spatial MNA)

**Thesis:** a clamped plate (scene 06) is a modal resonator. Excite it, tap a listening
point, read its height over time → that IS an audio signal. The spatial cousin of the
ladder: instead of 4 nodes + feedback, ~4k nodes + a real 2D wave operator.

**Signal path in Godot:**
- Run the solver on a *small* grid (a plate is 64² or less) at audio rate.
- Excitation = a forcing term in the stamp (we already have the poke): **strike** = one-tick
  impulse, **mallet** = short gaussian pulse, **bow** = sustained force + friction. Position
  of the excitation changes timbre (which modes you hit).
- **Pickup** = read back one (or a few) cells per sample → a `float` stream.
- Output via `AudioStreamGenerator` → `AudioStreamGeneratorPlayback.PushFrame()` on an
  `AudioStreamPlayer`. Godot's real-time generated-audio API; push blocks of frames.

**Tuning (this is a physical instrument, so geometry = pitch):**
- Grid size + wave speed (tension) + boundary condition set the modal frequencies.
- Clamped Dirichlet edges (scene 06) → plate modes; Neumann → different spectrum.
- Add **stiffness** (a `∇⁴` biharmonic term) for the metallic/dispersive character of a real
  plate — that needs a wider stencil than our 5-point (a follow-up: two Laplacian passes,
  `∇⁴ = ∇²(∇²)`).
- Damping `a` = decay time (T60); `leak` = a low-frequency loss. CN keeps the *rest* lossless.

**Where GPU parallelism pays off — polyphony:**
- Batch N independent plates as N slices of a 3D texture (z = voice) → one dispatch solves
  all voices. A 32-voice poly synth is one `48³`-ish solve.
- Or one enormous plate with many pickups = a spatial reverb with per-tap outputs.

**The hard limit to find — latency vs batching.** Per-sample GPU→CPU readback is a stall;
audio wants ≤5–10 ms latency. Options, in order of realism:
1. **Block processing** — run K sim-substeps, read back K samples at once (one stall per
   block). Trades latency for throughput. Probably the sweet spot.
2. **Keep audio on CPU/SIMD** (small grids are cheap there, low latency) and reserve the GPU
   for the big *visual* fields. A hybrid.
3. Full GPU audio with a ring-buffer readback and a few frames of latency (fine for an
   effect/reverb, not for a playable instrument).

**First build:** fork scene 06 → `plate_synth`: keep the CN plate, add an `AudioStreamPlayer`
+ generator, a pickup readback, and strike-on-click. Prove one voice makes a tone whose pitch
tracks the tension slider. Then polyphony.

---

## 2. Graphics: the "how the f*** did they do that" scene

**The trick isn't one technique — it's that the *behavior* is a real coupled sim while the
*look* leans on Godot's renderer.** Real dynamics + good rendering = uncanny. And it stays
fast because the sim is matrix-free/multigrid (predictable cost) and the rendering is
rasterized (no ray tracing). Target shot: **a living wisp of fog resting on reactive water.**

### 2a. Fork & crank the water (scene 03 → an ocean)

Today's water is one 2D MNA grid displaced on a plane. To make it *ridiculously* natural,
**layer** — real ocean renderers are all layered function approximation:

- **Macro layer — a procedural ocean (the believable base).** Tessendorf **FFT ocean** (a
  compute-shader FFT of a Phillips spectrum) or, simpler to start, a stack of **Gerstner
  waves** (sum of directional sinusoids, sharp crests). This gives the statistically-correct
  big swell that reads as "real ocean." Compute it in the surface shader (Gerstner) or a
  compute pass (FFT) → height + normal.
- **Micro layer — the MNA sim on top (the reactive detail).** Add the MNA height field to the
  ocean height. This is the part FFT/Gerstner *can't* do: ripples that actually **propagate,
  reflect, and respond** — wakes, drips, object impacts, a hand in the water. **This is the
  "how is it reacting to that?!" moment.** Blend: `h = ocean(x) + mna(x)`, normals from the
  sum.
- **Macro choice — Gerstner (scene 08) over FFT, and it's not a compromise.** The MNA layer is
  additive (`h = macro + mna`) and *macro-agnostic* — toroidal follow, cascades, LOD, poke
  coupling all work identically on either. But Gerstner wins for us: (1) its height is an
  **analytic CPU function** (no GPU displacement-map readback — buoyancy & poke positions are a
  cheap function call, deleting scene 110's per-tick stall), (2) ~~water-kit's FFT is broken~~
  — **STALE, see `solver-ledger.md` §7e**: water-kit now has *four* FFT implementations and three
  of them work, including a fresh 1:1 `threejs-water-pro` port in `kit/waves/`. This is no longer
  a reason to prefer Gerstner; (1) and (3) still are. (3) **stylized water that carves real wakes** is
  arguably a *bigger* "how'd they do that" than photoreal. Only give-up: FFT's photoreal
  spectrum + free Jacobian foam — but ameye has its own foam stage, and FFT can swap in later as
  the macro without the MNA layer knowing. **Primary base: scene 08 / composite Gerstner.**
- **Reuse — `../water-kit` already has the pieces, and the GLSL/GDScript ports over:**
  - **Gerstner macro (primary):** `08 ameye_water` (stage-toggleable stylized shader),
    `100 composite_gerstner` (multi-band, "better"), `lib/gerstner_field.gd` (CPU analytic height
    matching the shader) + `09 buoyancy` (floats crates on it — the coupling hook, no readback).
  - **FFT macro (optional, later — and it WORKS now):** full inventory in `solver-ledger.md` §7e.
    Best available is **`kit/waves/`** — the `threejs-water-pro` 1:1 port (`PORT_PLAN.md` Phase 1),
    working IFFT chain with a per-stage probe. `scene_108_fft_ocean` = verbatim **GodotOceanWaves**
    (2Retr0, MIT), also working, photoreal spectrum + free Jacobian foam; `scene_110` adds
    CPU-readback buoyancy. `scene_109` is fixed but recorded as the less-good FFT. Still a *later*
    swap-in rather than the first build (the MNA layer is macro-agnostic, and Gerstner's analytic
    CPU height is the real advantage) — but no longer blocked. Single-band Gerstner (`scene_02`) is
    marked "SUCKS" — proof it's the *layering* (composite) that matters.
  - **The micro layer, already attempted:** `scene_103_boat_ripple_wake` — a boat carving a wake
    into a CPU wave-grid ripple sim (README: "runs, not good-looking, needs work"). That is §2a
    *one iteration early*: CPU (damped, no CN), no good macro underneath. The upgrade path is this
    doc — **composite-Gerstner macro + our CN-MNA micro (rings) + foam** — built in water-kit
    (reuse `gerstner_field` / `09` for the analytic-height coupling), CN ported from the C# lab.
- **Crank knobs (your list):**
  - **Size** — bigger MNA grid (512²/1024²) and/or a *camera-following window* so the ocean is
    huge; deep multigrid (§4) keeps it O(cells).
  - **Precision** — CN scheme on, more sweeps, watch float precision at scale (height in a
    separate high-precision buffer if needed).
  - **Oversampling** — sub-step the sim (several solver ticks/frame). Same idea as audio
    oversampling: it shrinks the numerical *dispersion*, so ripples travel at the right speed
    and don't smear — truthful wave propagation. Expose a "substeps" slider and watch the
    dispersion-vs-cost curve.
  - **Foam** — foam where the surface folds/whitecaps: compute a foam source from the MNA
    field's **curvature (Laplacian)** or the **Jacobian of the displacement** (Tessendorf's
    whitecap test). Accumulate it into a foam buffer that **advects + decays** (reuse the
    fluid advection kernels — we already built them), blend as a foam overlay. Foam that
    *reacts* because a real sim drives it.

### 2b. Fog that lives, on top of the water

- A thin layer of `FluidSim3D` fog hugging the surface.
- **Reactive coupling** — feed the water's vertical surface velocity (`∂h/∂t`) into the fog as
  an updraft, so mist *rises where the water heaves* and drags where it moves. That coupling
  is what makes it feel alive rather than scrolled.
- Render: particles (they beat FogVolume for contrast in an open lit scene — see the
  comparison log) or a thin FogVolume for the soft volumetric read, plus **god rays** from a
  low sun through the volumetric fog.

### 2c. The rendering that sells it (Godot specifics)

- Forward+, **SSR** (screen-space reflections) on the water for the sky/scene mirror.
- **Volumetric fog** for the mist + god rays; a low warm key light.
- Normals from the *summed* height (ocean+MNA+foam) — most of the realism is in the normals.
- AgX tonemap, subtle bloom on the sun glints. No ray tracing — all rasterized.

---

## 2.5 The camera-following sim window (how "unconstrained" actually works)

The MNA (and fog) sim is a **fixed-cost box that rides with the camera** — area-of-interest /
camera-relative simulation. The FFT ocean is the global macro (procedural, everywhere, no
per-cell sim); the MNA box is local reactive detail near the viewer. Past the box there's
nothing to see anyway (the FFT swell still covers it), so we simply **stop caring** at the
boundary. This is the real answer to "unconstrained": a moving window over an infinite
procedural backdrop, not an infinite grid.

Three make-or-break details:
1. **Absorbing (sponge) edges — the critical one.** Reflective (clamped/Neumann) walls would
   make ripples *bounce off the invisible frustum edge* as the box slides — the tell that kills
   it. Put a **damping ramp** in the outer ~N cells so waves propagate out and vanish. "Stop
   caring" = absorption, not reflection.
2. **Cell-snapped scrolling via TOROIDAL ADDRESSING** (the clipmap technique — Losasso & Hoppe
   geometry clipmaps; a.k.a. a 2D wrap-around/circular buffer). The field is **world-anchored**;
   the box is a sliding window over it. Address the grid **mod N**, keep a world-origin offset,
   and on camera motion **don't move the data** — bump the offset and **re-init only the thin
   newly-exposed edge strip** (the cells that scrolled off wrap around to become it). Cost is
   **O(edge), not O(area)**; sample at world P via `mod(P − offset, N)`. Without this, ripples
   are glued to the camera; with it they stay put in world space. The wrap **seam sits exactly
   at the absorbing sponge** (below) so the discontinuity is always in the damped zone and the
   stencil never propagates physics across it. Each cascade ring is its own toroidal grid.
3. **Edge fade.** Fade the MNA contribution to 0 near the border so there's no hard line where
   reactive detail stops — it melts into pure FFT.

Keep the box **translate-only, axis-aligned** (rotating with the camera forces resampling).
Size it to cover the interactive near-field (where the boat is).

**Shape + distance LOD (frustum-fit, cheaper with range).** Spend sim where the eye resolves
it — a ripple far out is subpixel. Fit the region to the line-of-sight (a trapezoid) and make
it *coarser/cheaper with distance*, the same philosophy the FFT ocean's own cascades use.
- **Do it with nested cascades**, not a warped grid: concentric axis-aligned grids centered/
  biased ahead of the camera, each ~2× the cell size of the one inside.
- **LOD ladder — everything ramps with distance / cascade index:**

  | Axis | Near | Far |
  |---|---|---|
  | Cell size | fine | coarse (2×/ring) |
  | Sweeps / iters | many | few (loose convergence is invisible far) |
  | Substeps (oversampling) | high | 1 |
  | **Update rate** | every frame | every 2nd–4th (far waves crawl in screen space — often the biggest win) |
  | Scheme | CN (rings) | cheap BE (damping is invisible far) |
  | Reactive coupling / foam | full MNA | none — FFT-Jacobian foam only |

  - **Foam:** the FFT ocean already spawns whitecaps from its displacement **Jacobian** (folding
    test — GodotOceanWaves `whitecap`/`foam_amount`), so the far field gets believable foam for
    free. Only layer MNA-reactive foam (wakes, impacts) in the near cascades. Past the outermost
    cascade: **no sim at all** — pure FFT + Jacobian foam.
  - **Pick the boundaries by screen texel density** (like mipmap selection): once a world-cell
    covers < ~1px, stop simming it → that's the outermost edge.
  - **Budget payoff:** geometric cascades (2× area & cell/ring) + ramped sweeps/rate ⇒ each ring
    costs ≈ the one inside or less ⇒ the whole stack sums to ~**O(near cascade)**, not O(area)
    (same reason a mipmap chain is only ~⅓ over the base). Horizon-spanning reactive ocean at
    near-field price.
  - **Crossfade transitions** or a "cheapness ring" pops as features sweep past it (same
    machinery as cascade coupling).
- **The trap that dictates the architecture:** a *stateful* sim (`h_curr`/`h_prev`) hates a
  warping/rotating grid — every reproject *resamples* the field → diffusion + drift, accruing
  each frame. The projected-grid ocean trick works only because FFT is *stateless*. So: **keep
  the SIM grid stable** (axis-aligned, cell-snapped, cascaded) and **decouple it from the render
  surface** — the FFT clipmap can be as projective/frustum-fit as it likes; it just *samples*
  the sim grids. Sim grid stable, render grid projective.
- **The hard part (flagged early):** coupling waves across cascade boundaries (fine↔coarse)
  without reflection or seams — AMR territory; expect most of the tuning to land here.

**Sequencing:** boat (user-provided) → MNA camera-window layer (absorbing edges), poked by the
boat → port FFT **108** (GodotOceanWaves) to C#. Port note: the GLSL compute
(spectrum/butterfly/FFT/inversion) ports verbatim; the host (`water.gd`, `wave_generator`,
cascades, clipmap mesh, material) is a real C# reimplement — budget it as substantial. Payoff:
FFT + MNA + fog in one C# engine.

## 3. How to fork the water scene (concrete build order)

New scene `NN_ocean` (fork of scene 03), built up in stages so each is verifiable on screen:
1. MNA water as-is (CN on) → confirm reactive ripples.
2. Add a Gerstner base layer in the surface shader (2–4 octaves) → looks like open water.
3. Add the MNA field on top → poke it, watch real ripples ride the swell.
4. Foam pass: curvature/Jacobian → foam source → advect+decay buffer → blend overlay.
5. Thin fog layer (FluidSim3D) coupled to `∂h/∂t`, rendered as particles → living mist.
6. Rendering polish: SSR, volumetric god rays, tonemap.
7. Expose all crank knobs (grid size, substeps/oversampling, CN θ, ocean spectrum, foam
   threshold, fog density).

---

## 4. The enabler — deep multigrid

We're on a **2-level** V-cycle (256→128), and its operator is still scalar-β — so it solves a
*uniform-depth approximation*, not the stamp everything else solves. A **deep pyramid**
(256→128→64→…→8) over the real stamp operator is true O(cells) with ~constant cycles regardless
of resolution — the prerequisite for every "crank the size" experiment (512²/1024² water,
128³/256³ fog).

**Current plan of record: `solver-ledger.md` §6/§7.** Build all four remaining solvers first
(multigrid §7a, block-dense Schwarz §7b, spectral §7c, nD + tiled storage §7d), *then* profile.
The **flat iteration-count vs resolution** money-shot sweep — the whole predictable-cost thesis,
never plotted — is Phase 2 there, deliberately deferred until the dropdown stops changing.

**The nD note (`solver-ledger.md` §1a) matters to this document specifically:** the 3D fog and
4D-lattice ambitions above assume the solver generalizes. It does not yet — `GpuStampSolver3D`
is a *copy* of the 2D path, not a dimension-agnostic one. The fix restores a property the
original cmajor MNA (`../neptunely_standalone/js/audio/cmajor/lib/mna-solver.cmajor`) always
had: adjacency is data, so dimension is a parameter.

---

## 5. Limits to go find (the experiments)

- **Scale:** 512²/1024² water + deep multigrid; 128³/256³ fog. Where does 60fps break?
- **Oversampling:** substeps vs dispersion error — the truthfulness-vs-cost curve.
- **Coupling stability:** water ↔ fog ↔ foam coupled — does it stay stable, and at what θ?
- **Audio:** audio-rate plate, block-size vs latency, how many polyphonic voices per frame.

---

## 6. Retrofit owed

Scene 06 (plate reverb) was built *before* the CN insight — its tail is silently
over-damped by the numerical loss, not just the physical damping. Retrofitting the θ slider
there (it's the same wave stamp) is the on-ramp to §1 (the synth). Do this first.

---

## 7. Fluid-in-fluid: dye, milk, sand (don't forget)

Foam (§2a) and mist (§2b) are one kind of passenger riding a sim. The third family:
**a fluid moving *inside* a fluid** — milk blooming in coffee, sand swirling in water,
ink dropped in a glass. The "how'd they do that" here is the *mixing*: filaments, curls,
billows that keep folding forever.

**We mostly already have it.** This IS the smoke/fog dynamic — Stam stable fluids with a
dye field (scene 07 `StampFluid` 2D, scenes 09/10/12 `FluidSim3D` — `sf_advect_dye` /
`f3_advect_dye`). Smoke = dye + positive buoyancy. The variations:

- **Milk in coffee** — dye with its own *density* feeding back into the force term
  (Boussinesq: `f.y ∝ (ρ_ambient − ρ_dye)`); milk curls because it's heavier-then-lighter
  as it mixes, and viscosity contrast keeps the filaments sharp. Mostly `f3_add_source`'s
  existing `buoy·d` term with sign/curvature options + higher-order advection (MacCormack /
  BFECC) so the swirls don't diffuse to mush in ten frames.
- **Sand in water** — dye with **negative** buoyancy (settling velocity) + deposition:
  falls out of the flow onto a floor/height field, gets **resuspended** where flow speed
  exceeds a pickup threshold. That deposit field is a natural stamp-solver citizen —
  and it couples straight into the ocean scenes: *the boat's wake stirring sand off the
  seabed* (scene 17 already has the seabed + caustics; the wake's `∂h/∂t` is the stir).
- **Rendering** — dye density → color absorption in the water shader (coffee/tea look),
  or particles seeded ∝ dye for sand grains; the fog-machine comparison-log lessons apply.

**First build:** fork the 2D stamp_fluid → `milk_coffee`: one dye with signed buoyancy +
a viscosity slider + MacCormack advection, poured from a click. Then 3D sand with
settle/deposit/resuspend, then marry it to 17's seabed.
