# Shorewaves — Separation of Concerns & Approximation Approaches

**Purpose.** The water is not one thing — it's several **independent concerns** stacked
together. Today they're fused into one heavy path (the KP07 finite-volume solver + a mesh
render). To evolve it (cheaper, more natural, more controllable) we **isolate each concern
and swap or fake it one at a time**, each in its own numbered scene (repo convention), with
scene 17 (KP07 artifact) and scene 18 (lite render) as the reference baselines.

**Guiding bet.** Approximate the expensive *nonlinear* KP07 core with a cheap, **predictable,
stable** *linear* wave core (MNA implicit, or Hugo-Elias/Wallace explicit ripple — both
already in our ecosystem), then **fake** the nonlinear/natural parts (shoaling, breaking,
foam, fine chop) on top. Render however we like — ray tracing is optional and fully
decoupled from the fluid.

---

## The concerns (independent axes)

Each row can be varied without touching the others. That's the whole point.

| # | Concern | Today (KP07 path) | Cheaper / faked options | Isolate as |
|---|---------|-------------------|-------------------------|------------|
| C1 | **Wave core** (propagation) | KP07 explicit FV, 12 substeps/frame, CFL-bound | MNA **implicit** damped-wave (unconditionally stable, big dt); **Hugo-Elias / Wallace explicit ripple**; virtual-pipes | solver behind an interface / its own scene |
| C2 | **Shoaling** (bed → waves) | native (mass conserved over rising bed) | spatially-varying wave speed **c = √(g·depth)** fed from bathymetry | one term in the core |
| C3 | **Wet/dry shoreline** | native positivity-preserving KP07 | height **clamp to bed** + wet mask | a clamp/mask pass |
| C4 | **Breaking & foam** | physical (Kennedy dη/dt, Froude bore, swash) | **heuristic**: steepness/velocity threshold → foam + amplitude clamp | a foam pass (reuse `pass_derived`/`pass_ground` ideas) |
| C5 | **Forcing** (generation) | 4-component wavemaker + solitary | broaden the spectrum (more/random components); **interactive pokes/drops** | boundary/injection module |
| C6 | **Sub-grid detail** (naturalness) | full shader's detail normals + Voronoi lace | **flow-advected FBM/Perlin normals** (aperiodic, sub-5 cm) | fragment shader only |
| C7 | **Render path** | mesh + screen-space refraction/foam **or** lite | lite (shine/fresnel/smoothed normals); **raytraced (optional)** | material swap |
| C8 | **Resolution & perf** | 608² @ 5 cm, mesh subdiv, render scale, no AA | grid size, substeps, render scale, FXAA/TAA | config |

The current sliders in scene 18 already expose parts of C5/C6-render/C7/C8. C1–C4 are where
the big structural bets live.

---

## Wave cores (C1) — pick one; mutually exclusive

A core is *just* the height-field advance. Everything that makes it look like **surf**
(shoaling, wet/dry, breaking) is a separate add-on in the next section — those were previously
bundled (wrongly) into the MNA/ripple writeups.

- **Idea A1 · KP07** — nonlinear finite-volume (current baseline, scene 17). Accurate; genuinely
  breaks and does wet/dry natively; heavy (12 CFL-bound substeps).
- **Idea A2 · MNA implicit damped-wave** — matrix-free GPU (Jacobi/RBGS/CG/multigrid),
  **unconditionally stable** → big timesteps, cheap. Linear. Repo already has it:
  `GpuStampSolver`/`MnaWave`/`MgvSolver` behind `IStampSolver`. *Isolate as* `19 · mna_beach`.
- **Idea A3 · Hugo-Elias explicit ripple** — `h_new = (Σ 4-neighbors)/2 − h_old`, damped; the
  cheapest. Linear, explicit (CFL-bound but light). water-kit `11 · compute_ripples`.
  *Isolate as* `20 · ripple_pool`.
- **Idea A4 · Wallace "WebGL Water" explicit ripple** — same family, but the state texture packs
  height+velocity+normal and it ships with **built-in interaction** (draggable sphere). The ray
  tracing is only its render — **keep the fluid, drop the raytracer.** water-kit `15/27`.
- **Idea A5 · Virtual-pipes shallow water** — real volume transport (flows downhill, pools).
  water-kit `13 · pipe_water`.

**Honest limits of the linear cores (A2/A3/A4):** no nonlinear advection → crests won't
steepen/curl (breaking is cosmetic); implicit MNA also adds numerical damping (ripples decay
faster — Kass-Miller tradeoff, water-kit scene 12). They recover *propagation + shoaling +
swash* cheaply and stably; curl/whitewater is decoration.

## Core add-on modules — bolt onto ANY linear core; compose freely

What a linear core needs to behave like a beach. Core-agnostic; each shippable alone.

- **Idea B1 · Varying-c shoaling · [C2]** — spatially-varying wave speed `c = √(g·depth)` from
  the bed → waves slow in the shallows, shorten, grow, and **pile up at the shore.** The single
  most important add-on; without it a linear core is just a flat pond.
- **Idea B2 · Wet/dry clamp · [C3]** — clamp height to the bed (+ a wet mask) for a moving
  waterline.
- **Idea B3 · Heuristic breaking + foam · [C4]** — steepness/velocity threshold → spawn foam +
  clamp amplitude (same detector *shape* as KP07's Kennedy/Froude, but cosmetic).

## Forcing (C5)

- **Idea B4 · Interactive pokes / drops** — objects or the mouse inject ripples (Wallace's
  sphere is the reference). The interaction loop; independent of core choice.
- **Idea E · Broadened / incommensurate wavemaker** — see below (irrational component ratios;
  more components) — the fix for periodic swell.

---

## Cross-domain source — CXM 1978 reverb DSP (`../neptunely_standalone`)

Ideas C–G are **separate techniques** mined from the owner's Cmajor reverb — **CXM 1978**
(Chase Bliss / Lexicon-224 style), a **Dattorro figure-of-eight plate/tank** reverb
(`js/audio/cmajor/fx/cxm-1978/cxm-1978.cmajor`), its fractional-delay interp kernels
(`patches/generation-loss-mkii/cubic-delay.cmajor`, `patches/fx-warpedvinylmkii/lagrange-delay.cmajor`),
and a literal **`ShallowWater`** node with a "K-field" modulator (`lib/neptunely-dsp.cmajor:2999`).
**Not ported:** the delay-network as a wave *core* — a reverb is 1-D causal delay-line
propagation; our sim is a 2-D PDE that already disperses. Each idea below stands alone.

### Idea C — Bicubic fractional sampling · [C8 / C2]
The reverb reads delay lines at moving non-integer positions with a **4-tap cubic Catmull-Rom**
kernel. Water analog: sample `tx_state`/`tx_derived` with a **manual bicubic** fetch instead of
bilinear → smoother sub-5 cm surface/normal reads (fakes resolution), and read detail-noise at
**fractionally advected** positions. Solver-agnostic, a few extra taps.
*Isolate as:* a shader-only change to `water_lite` — no new scene.

### Idea D — K-field aperiodic modulation · [C6 / C5]
Instead of a periodic LFO: hold a random target for an *irregular* interval and ease toward it;
target = a sum of sines at **incommensurate ratios (1 : 1.7 : 2.3)** so it never repeats. Drive
detail-normal phase and/or forcing amplitude with it → the surface reads "alive," not tiled. The
recipe is already written in the `ShallowWater` node — lift it.
*Isolate as:* a small modulator function feeding the detail/forcing layer.

### Idea E — Incommensurate / prime forcing spacing · [C5]
Give the wavemaker's components **irrational period/angle ratios** (the reverb uses prime delay
lengths) so crests never phase-align into a visible tiling pattern — the direct fix for the
"predictable corduroy swell." Sim-side; pairs with widening the component count.
*Isolate as:* the wavemaker/forcing module (needs the △glsl irregular-sea plumb).

### Idea F — Phase-decorrelated modulators · [C6]
Vary the modulator **phase across the field** (the tank's quadrature LFOs) so neighboring regions
don't crest together → diffuse motion instead of a coherent global pulse. Composes with D.
*Isolate as:* a per-position phase offset in whatever modulator C/D use.

### Idea G — Allpass phase-dispersion + cross-coupled feedback · [speculative]
The reverb's allpass diffusion scrambles phase without touching magnitude, and figure-8 feedback
builds echo density. Stylistic overlays only — the 2-D PDE already gives real dispersion, so low
priority. Parked for completeness.

---

## How we work it — one concern at a time

- Pick **one axis**, change only it, hold the rest fixed by reusing the existing harness
  (`ShallowWaterKp` / `BeachScenario` / `water_lite`) as the baseline.
- Each experiment is **its own numbered scene** (additive; never edit a frozen one).
  Scene 17 = KP07 reference, scene 18 = lite render reference.
- Judge on screen with `tools/shoot.tscn`, and against the reference scenes.

**Likely first isolations to prototype:**
1. C6 alone — flow-advected FBM detail normals on scene 18 (cheapest naturalness win, no
   solver change).
2. C1+C2 — `19 · mna_beach`: MNA core with varying-c shoaling vs the KP07 artifact.
3. C1 (interactive) — `20 · ripple_pool`: Wallace/Hugo-Elias sim, raytrace off, lite render.

Related: [`17_shorewaves_mods.md`](17_shorewaves_mods.md) holds the raw idea backlog (R1
raytraced ocean, etc.); this doc is the **axes** we vary those ideas along.

---

## Testing — isolate & hypothesize each idea

**Method.**
- **Same-scene on/off toggle is the gold standard.** If an idea is a shader/param, wire it as a
  DemoUI toggle and flip it — the only variable is the idea, and you read the FPS/look delta on
  one frame (this is exactly how SSR and lite-vs-full were measured).
- **Whole-core swaps** can't toggle, so it's **scene-vs-scene** with everything else held fixed
  (same bathymetry, forcing, camera, render, resolution, seed) and compared **at matched
  sim-time**.
- **Layered isolation** when an idea can't stand fully alone: a bare *core* on a flat pool can't
  make surf, so test the property it *can* show alone first (propagation: stability + cost +
  dispersion from a single poke), then the **minimal** combo for the target outcome (core + B1
  shoaling). State the confounds you're holding fixed.
- **Three evidence types:** *perf* = FPS + sim ×rate; *correctness/stability* = the debug
  readback (nan = 0, volume conserved, sane max_h); *visual* = screenshot at matched sim-time vs
  scene 17. Periodicity claims need **time capture** (record N s, eyeball or autocorrelate a
  height strip).
- **Controls:** scene 17 = physics truth, scene 18 = render baseline. Set the **pass threshold
  before** running.

**One hypothesis per idea** (`if → then`, falsifiable):

| Idea | Hypothesis | Isolate → evidence → pass |
|---|---|---|
| **A2** MNA core | *If* MNA + B1 shoaling replaces KP07, *then* shore pile-up/swash within visual tolerance at ≥3× FPS, stable at large dt | `19·mna_beach` (+ flat-pool poke for raw propagation), same bathy/forcing/cam/render → FPS·sim×, nan=0, shot@t vs 17 → **pass** if stable + ≥3× + shore reads |
| **A3** Hugo-Elias | *If* explicit ripple core, *then* stable propagation cheaper than KP07 | flat-pool poke, same harness → FPS, nan=0 → **pass** ≥2× & stable |
| **A4** Wallace | *If* Wallace core with raytrace off, *then* interactive ripples render fine on lite | `20·ripple_pool` → poke rings + FPS → **pass** rings radiate + cheap |
| **A5** pipes | *If* virtual-pipes, *then* water transports & pools downhill | pour test on a bumpy bed → shot → **pass** basins fill |
| **B1** shoaling | *If* `c=√(g·depth)` on a linear core, *then* crest amplitude grows shoreward; off → uniform | toggle in one scene → side shot / near-shore max_h → **pass** amplitude grows + slows |
| **B2** wet/dry | *If* clamp-to-bed, *then* a clean advancing/retreating waterline; off → clipping | toggle → watch shoreline → **pass** clean moving line |
| **B3** breaking | *If* steepness/vel heuristic, *then* foam appears in the same zones KP07 foams | toggle + foam-highlight → compare foam map vs 17@t → **pass** zones match |
| **B4** interactive | *If* a poke injects momentum, *then* a ring radiates & reflects | click in-scene → shot → **pass** rings radiate |
| **E** incommensurate | *If* component ratios are irrational, *then* the repeating corduroy pattern disappears | A/B rational vs incommensurate, record 60 s → eye / autocorrelate a strip → **pass** no visible period |
| **C** bicubic | *If* bicubic normal reads, *then* faceting gone vs bilinear, no FPS cliff | toggle bilinear/bicubic → zoom A/B shot + FPS → **pass** smooth + FPS≈ |
| **D** K-field | *If* K-field drives detail phase, *then* motion doesn't repeat vs a periodic LFO | A/B LFO vs K-field, record 30 s → **pass** no loop |
| **F** decorrelated | *If* per-region phase offset, *then* the surface stops pulsing coherently | A/B → **pass** motion spatially diffuse |
| **G** dispersion | *(speculative)* allpass adds incoherent spread | park until C/D/F land |

Rule of thumb: if you can't state the **toggle**, the **number/shot you'll read**, and the
**threshold that would make it fail**, the idea isn't isolated enough yet — split it further.

---

## Experiment harness (agent-ready)

**Proven template — Exp C (bicubic), already wired in scene 18.** A shader `uniform bool
use_bicubic` gates one path (`sample_h()`), and a DemoUI toggle in the **Experiments** section
sets it (default off = baseline). The judge flips it and reads FPS + nan on the panel. Every
other experiment is a copy of this shape.

**Three wiring kinds:**
- **Shader toggle** (same-scene A/B): add `uniform bool use_X` to `water_lite.gdshader`, gate the
  one path, add `ui.AddToggle("Exp X · …", false, v => _waterMat.SetShaderParameter("use_X", v))`
  under `// Experiments`; add sliders for its params next to it.
- **Solver field** (same-scene A/B): add a `public` field on `ShallowWaterKp`; a toggle/slider
  sets `_solver.X` (exactly like the Sea/Physics sliders already do).
- **New core** (scene-vs-scene): a new `NN_<name>.tscn` + host reusing `BeachScenario` +
  `water_lite`; compare against scene 17/18 at matched sim-time.

**Per-experiment wire card — hand one to an agent:**

| Idea | Kind | Wire point (file → symbol) | Control (default) |
|---|---|---|---|
| **C** bicubic | shader | `water_lite` → `use_bicubic` / `sample_h()` | toggle (off) — **DONE, this is the template** |
| **D** K-field | shader | `water_lite` → add K-field modulator fn → perturb normal | toggle (off) + rate/amount sliders |
| **F** decorrelated | shader | `water_lite` → per-`suv` phase offset into D's modulator | toggle (off) |
| **B3** breaking | shader | `water_lite` → steepness/vel from `tx_state` → foam color | toggle (off) |
| **E** incommensurate | △glsl | `pass_boundary` → `COMP_PER`/`COMP_ANG` via push-constant; irrational ratios | toggle rational↔incommensurate |
| **B1** shoaling | new scene | linear-core scene → `c=√(g·depth)` from `tx_bottom` | toggle (off) in that scene |
| **A2** MNA core | new scene | `19·mna_beach` → `GpuStampSolver` + B1 | scene-vs-17 |
| **A3/A4** ripple cores | new scene | `20·ripple_pool` → port water-kit 11 / 27 sim | scene-vs-18 |

**Agent checklist for one card:** implement the single wire point → `dotnet build` → `--import`
if a shader/asset is new → shoot the scene **off** then **on** (or scene-vs-ref) → hand the judge
the two PNGs + the FPS/nan from each. Never edit a frozen scene; it's a toggle or a new numbered
scene, nothing else.
