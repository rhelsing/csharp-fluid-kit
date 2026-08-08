# Artifacts 250+ — solver character as the instrument

Thesis: the error term of a numerical scheme is a material property. Dissipation = viscosity,
dispersion = detuned wave speeds, under-converged projection = compressibility, constraint
leakage = compliance. Audio already knows this — reverbs, distortion, delays ARE their
artifacts. This series treats residuals as the product: name each artifact, give it knobs,
isolate it in one scene, then layer.

Proven instances that seeded this: Jacobi-vs-multigrid in scene 32 (Jacobi's squish won),
the Cn dial (BE dissipation vs CN ring), substeps (dispersion).

**Extension → [`artifacts-270.md`](artifacts-270.md)** — Blocks D–G (270–289): detector
artifacts, second-backend discretization character, coefficient hysteresis, and telemetry
as control, seeded by [`science-boi-lift.md`](science-boi-lift.md). *This* document is the
frozen base — 270 appends later phases (order of work steps 8–13) and modifies nothing here.
Same contract in both: one artifact per scene · knobs · reference A/B · artifact view in
`#E23D6D`.

## Orientation — a fresh agent starts here

Repo: `godot-csharp-experiments` (Godot 4.6.2 **.NET** — `/Applications/Godot-mono.app`,
arm64 only; build with `~/.dotnet/dotnet build`; see CLAUDE.md for the arch trap).

**Do NOT open scenes — not live, not the screenshot harness.** Verification for this series
is: `~/.dotnet/dotnet build` clean, template followed exactly. Ryan reviews every scene
visually himself; agents never launch anything. The scaffold and the first two scenes
(250/250b) were built together with him and are the trusted template — after that, a new
scene is a **formula swap** into that template, not a from-scratch build. Because kernels
compile at runtime, a kernel typo won't surface until Ryan opens the scene: so keep kernel
diffs minimal, mirror the proven pc-layout patterns byte-for-byte, and flag anything risky
in the handoff note instead of testing it yourself.

The toolbox, by block:
- **2D fluid** (Block A): `scripts/lib/FluidSim.cs` + `shaders/fluid/fs_*.glslinc`
  (add/advect/divergence/jacobi/gradient/dye; extras: `fs_maccormack`, `fs_diffuse_vel`;
  ctor takes `addKernel` + `extras`). Reference scenes: 07 (`StampFluid.cs`), 18 (`MilkCoffee.cs`).
- **3D fluid**: `scripts/lib/FluidSim3D.cs` + `shaders/fluid3d/f3_*.glslinc` (+ `f3_confine`,
  `f3_maccormack`, `f3_diffuse_vel`; deep-MG pressure path via `EnableMultigrid`/`UseMultigrid`;
  `extraAdds` for extra source passes). Reference scene: 32 (`Milk3D.cs`) — TIME scale, camera
  orbit sliders, stir/jostle, and the raymarch renderer `shaders/milk_glass.gdshader`.
- **Wave/stamp** (Block B/C): `scripts/lib/GpuStampSolver.cs` (+3D), stamps in
  `shaders/stamp/*.glslinc` behind the `st_diag / st_conductance / st_rhs` contract,
  topology via `nd_2d`/`nd_3d`. Reference scenes: 03, 50, 52 (Batty fractions).
- **Panel**: `scripts/lib/DemoUI.cs` — `AddSlider/AddToggle/AddOptions/AddReadout/AddSection`;
  the header auto-adds FPS + upscaler/render-scale/AA. All controls are `FocusMode.None`
  (arrow keys drive scenes — never let the panel eat them).
- **Scene pattern**: `scenes/NNN_name.tscn` (5-line script wrapper) + `scripts/Name.cs`
  building camera/light/mesh/sim/UI in code. Copy an existing pair.
- **Kernels** compile from source at runtime (`ShaderCompileSpirVFromSource`); the push
  constant must match the C# `float[]` byte-for-byte (std430: `vec3` pads to 16B — use
  flat floats; this has bitten twice).

House rules: tuned Copy-values get baked as defaults · every A/B stays reachable forever ·
README row per scene · debug by proving (state-change logs), not guessing — and not by
launching scenes.

## Ground rules

- One artifact per scene. Numbering starts at **250**.
- **2D first, 512² default grid** (with a grid dropdown — see Grid & similarity). 3D later —
  solver-level artifacts lift for free (nd_2d → nd_3d, fs_* → f3_*); display lifts via the
  milk-glass volume march.
- Display: **flat plane + gradient normals** (2D) or **density-texture raymarch** (3D,
  the scene-32 technique). No raytracing, no isosurface/goo.
- Every scene has: the artifact's knobs · an **accurate-reference A/B toggle** (the
  experiment IS the toggle) · an **artifact view** (render the residual field itself,
  always in the signature color).
- Copy-values → baked defaults after tuning (house rule).

## Palette (shared, all 250-scenes)

| Role | Color | Use |
|---|---|---|
| paper | `#E8E2D5` | background field / zero level |
| ink | `#16213E` | scalar field body (dye, height shading) |
| plus | `#35C4B5` | signed field + |
| minus | `#E0A458` | signed field − |
| **artifact** | `#E23D6D` | THE residual, in every scene, always this color |
| room | `#171310` | scene background |

One shared display shader family (`artifact_view.gdshader`): modes = ink scalar ·
signed two-tone · lit height plane (normal from gradient, one light, matte) ·
artifact overlay (residual × `#E23D6D`, additive, intensity slider).

## Standard panel — every 250-scene, same order

1. *(auto header: FPS, upscaler, render scale, AA)*
2. **TIME scale** (0.05–1.5) — multiplies dt in every pass; per-tick fades become
   `fade^timeScale` (learned in scene 32: else slow-mo drowns in stale haze).
3. **Grid** dropdown (256²/512²/1024² where feasible; default 512²) — live rebuild.
4. Sim knobs (scene-specific: sources, drift, viscosity…).
5. **ARTIFACT knobs** — the point of the scene, under an `AddSection` header.
6. **Reference A/B** toggle (the accurate solve) · **Artifact view** toggle + intensity.
7. Render knobs.

## Proof of concept — 250 · squish, 2D then 3D

Milk-in-coffee re-skinned in the series palette (ink in paper — NOT coffee brown), Jacobi
as the test artifact. Proves palette + standard panel + artifact-view plumbing + the 2D↔3D
lift before any new solver is written.

- **250_squish (2D, 512²)**: scene 18's pipeline; display through `artifact_view` modes;
  Jacobi K is the artifact dial. Reference A/B = K=400 (or 2D MG when hooked). Artifact
  view = |∇·v| in `#E23D6D` (the divergence texture already exists in the pipeline —
  expose `DivRid` and draw it).
- **250b_squish3d**: scene 32's machinery verbatim (raymarch, TIME, camera orbit, stir) in
  the palette; the existing deep-MG/Jacobi toggle IS the reference A/B. Artifact view =
  the 3D `_div` texture as a second raymarch channel in `#E23D6D` (expose `DivRid` on
  `FluidSim3D` — additive one-liner).

## Grid & similarity — how knobs must move with N (and when they shouldn't)

Kernels run in **cell units**: velocities in cells/tick, forces in cells/tick², ν in
cells²/tick. Changing N over the same world size W changes the meaning of every raw number.
To keep similar physics when N doubles:

| Quantity | cell-units scaling | N ×2 ⇒ |
|---|---|---|
| world velocity (stir, pour push, advection) | `v_cells = v_world·N/W` | ×2 |
| body force / buoyancy / drift | `a_cells = a_world·N/W` | ×2 |
| source / poke radius | `r_cells = r_world·N/W` | ×2 |
| viscosity `a = ν·dt` | `ν_cells = ν_world·(N/W)²` | ×4 |
| per-tick fades | keep per sim-time: `fade^(timeScale)` | unchanged |
| Jacobi/GS iterations (matched convergence of the largest modes) | `K ∝ N²` | ×4 |
| SOR at optimal ω | `K ∝ N`, `ω* ≈ 2/(1+sin(π/N))` | ×2 |
| multigrid | +1 level per doubling; character ≈ invariant (its whole point) | +1 level |
| vorticity confinement ε | empirical; retune ≈ `∝ W/N` (ε 2.5 was a hurricane at 56³) | ×0.5 |
| MacCormack | dimensionless | unchanged |

Precedent in the repo: scene 52's `SpeedScale = grid/256` is exactly the velocity row,
applied to the wave speed. The scene scaffold should apply these rows automatically on the
grid dropdown so sliders keep their **world-unit** meaning.

The honest caveat — **some artifacts are grid-locked by nature**: plaid's checkerboard is
2 cells, grain's stripes are sweep lines, squish's residual correlation length is measured
in cells. Refining the grid refines the artifact itself. Two design responses:
1. Prefer **world-unit knobs** when defining an artifact — eq's knee is a world wavenumber,
   fully grid-proof; that's the gold standard.
2. When an artifact is inherently cell-scaled, treat the **grid dropdown as one of its
   aesthetic knobs** and say so in the panel — resolution is part of the instrument, the
   same way sample rate is part of a bitcrusher.

## Block A — projection/constraint residuals (Stam fluid, 2D)

| # | Name | Artifact = material | Formula | Tuners |
|---|---|---|---|---|
| 250 | **squish** | residual divergence = compressibility | Jacobi `p←(Σ₄p_nb − div)/4`, truncated at K | K (4–200), dt, fades |
| 251 | **bounce** | over-relaxation overshoot = elasticity | SOR `p←p+ω(p_GS−p)`; ω<1 pillow · 1..2 spatial ringing | ω (0.3–1.95), K |
| 252 | **crossfade** | dial between honest and squishy | `p = α·p_MG + (1−α)·p_Jac` | α, K_Jac |
| 253 | **memory** | pressure hysteresis = viscoelasticity | warm start `p₀ ← μ·p_prev` | μ (0–1.05; >1 = self-exciting, clamp) |
| 254 | **macro-honest** | fine-scale-only compressibility | V-cycle with fine smooths = 0; project levels ≥ ℓ only | cutoff level ℓ, coarse sweeps |
| 255 | **grain** | directional residual = anisotropic stiffness | ADI: alternating x/y line relaxations, truncated | sweeps/axis, axis alternation period |
| 256 | **plaid** | checkerboard-correlated residual = woven texture | RBGS at low sweeps | K (1–8) |
| 257 | **eq** | designed residual spectrum — the instrument | spectral projection `p̂(k) = W(k)·p̂_exact(k)`; W = lowpass(knee k₀, slope) × notch(k_c, width, depth) | knee, slope, notch center/width/depth |

257 is the grand one: an equalizer on incompressibility. 2D FFT is cheap; do it here,
not in 3D.

## Block B — scheme residuals (wave stamp, 2D)

| # | Name | Artifact | Formula | Tuners |
|---|---|---|---|---|
| 258 | **ring** | θ-scheme dissipation = reverb tail color | cn blend BE↔CN (existing stamp); pluck → tail | cn, damping, leak |
| 259 | **smear** | dispersion = wave chirp/detune | ring radius vs analytic `r = c·t` overlay; substeps shrink error | dt, substeps, c |
| 260 | **ghost** | advection diffusion + limiter chatter | SL vs MacCormack on a spinning test pattern | scheme toggle, dt, spin |

## Block C — the M (constraint elements; all SPD via companion/penalty — no saddle matrix)

| # | Name | What it is | Formula | Tuners |
|---|---|---|---|---|
| 261 | **rail** | voltage source as penalty; force readout = the multiplier | strip: `diag += G_r`, `rhs += G_r·s(t)`; force `F = Σ G_r(s−h)` | G_r (soft→stiff), drive freq/amp/shape |
| 262 | **leak** | Uzawa soft constraint = compliance | `λ ← λ + ρ(Bᵀx − c)`, truncated | ρ, K_uzawa |
| 263 | **pipe** | non-local inertance edge (netlist edge; water hammer / siphon) | branch `L·dI/dt + R·I = h_a − h_b`, trapezoidal companion: `G_eq = (dt/2L)/(1+R·dt/2L)`, `I_hist = [I_n(1−R·dt/2L) + (dt/2L)ΔV_n]/(1+R·dt/2L)`; stamps ±I at a,b | L, R, endpoints (draggable), n pipes |
| 264 | **tap** | lumped RLC resonator soldered to a node (tone circuit on the plate) | series RLC to ground via companions; `f₀ = 1/2π√(LC)`, Q from R | f₀, Q, tap position |

Exports from C (outside the series): constraint-based boat hull (reaction force =
buoyancy+drag, replaces the spring solver); resonator taps → the synth.

## Layering — feeding one into another

Per-tick pipeline: `[sources] → [scheme/advect] → [constraint solve] → [display]`.
Artifacts live at different stages, so they **compose by construction**:

- same-stage blends: crossfade (252) generalizes to any solver pair.
- cross-stage stacks: memory(253) + bounce(251) = viscoelastic-springy; eq(257) driving
  squish(250)'s dye; ghost(260)'s advection under any Block-A projection.
- cross-feeds (patch cables): one field sources another — wave height → dye source,
  heat → buoyancy, dye → wave forcing. Reserved for:
- **269 · mixer** — the pedalboard: one field, every Block-A/B knob live, plus two
  patch-cable routings. Built LAST, from tuned defaults of the isolated scenes.

## 3D path

- Solver-level artifacts (250–254, 256, 261–264) are dimension-blind — lift = swap
  nd_2d→nd_3d / fs_*→f3_* and re-tune constants.
- 255 grain gains a third axis (richer). 257 eq needs a 3D FFT — defer; 2D is the lab.
- Display in 3D = the milk-glass march (raymarch of the density texture — the scene-32
  renderer, reused; cylinder mask optional). Height fields stay flat-plane+normals even
  when the sim is 3D (slice or top surface).

## Order of work

1. `artifact_view.gdshader` + palette + a `Scene250Base`-style shared scaffold
   (standard panel, TIME scale, grid dropdown with the scaling table applied).
2. **The PoC, built WITH Ryan** (his eyes are the render check): 250 squish 2D at 512² →
   250b squish 3D. These two prove palette + plumbing on known physics and become the
   trusted template.
3. From then on: **formula swaps** into the template — 251 bounce → 253 memory (each is
   one kernel expression + artifact knobs), build-clean = done, Ryan opens and judges.
3. 252 crossfade, 254 macro-honest (needs 2D MG hookup), 256 plaid, 255 grain.
4. 257 eq (the instrument).
5. Block B (258–260) — mostly reframing existing stamp knobs into isolated scenes.
6. Block C (261 rail → 263 pipe → 264 tap; 262 leak when Uzawa is worth it).
7. 269 mixer.

→ steps 8–13 continue in [`artifacts-270.md`](artifacts-270.md) (Blocks D–G).
