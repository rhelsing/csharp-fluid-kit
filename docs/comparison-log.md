# GPU Stamp Solver — C# vs GDScript comparison log

Two parallel tracks build the same [plan](gpu-stamp-solver-plan.md):
- **C# track** — here (`godot-csharp-experiments`).
- **GDScript track** — `../water-kit` (scenes 20/25/26/27 already prove the idea).

The agents can't talk directly — record findings here; the user relays across.
The **GLSL kernels are shared verbatim**, so differences below are almost entirely
about the *host + CPU-side* code, which is the whole point of the experiment.

## Axes to compare (fill in as you go)

| Axis | C# (this repo) | GDScript (water-kit) |
|---|---|---|
| RD compute setup ergonomics | Extends ComputeSmoke cleanly — 4 textures + 7 uniform sets, all rd work in `CallOnRenderThread`, a barrier per Jacobi sweep. C#-isms only: `RDTextureFormat` dims are `uint` (cast), empty tex data is `Godot.Collections.Array<byte[]>` | scene 11/25/26 pattern |
| Push-constant / byte marshaling | `float[] → byte[]` via `Buffer.BlockCopy` (confirmed) | `PackedFloat32Array.to_byte_array()` (a touch terser) |
| Stamp-contract API (generics/interfaces) | _…_ | duck-typed / preload |
| Multigrid V-cycle orchestration | _…_ | _…_ |
| CG reductions | _…_ | _…_ |
| Iteration speed (edit→see) | build step (`dotnet build`) | hot reload, no build |
| Benchmark credibility (ms/tick) | _…_ | _…_ |
| Lines of host code for the same scene | ~290 (`MnaWave.cs`, ex-DemoUI) | ~300 (`scene_25_mna_wave.gd`) |
| First-time toolchain friction | arm64 / mono / build (see CLAUDE.md) | none |

## Log

- **2026-08-01** — Groundwork laid: shared kernels copied, plan adapted,
  `ComputeSmoke` (scene 02) RD-plumbing template built + **verified on screen**
  (`tools/shot_02_compute_smoke.png` shows the compute-written pattern). C# track
  ready for the agent to start at §7 (DemoUI → backbone).
  - **First C# vs GDScript finding — toolchain friction (corrected):** the RD *code*
    was a clean 1:1 of the GDScript pattern and compiled first try (`RDShaderFile.GetSpirV`,
    `CallOnRenderThread(Callable.From(...))`, `Buffer.BlockCopy` push-constants,
    `Texture2Drd.TextureRdRid`). The **only** real gotcha is env: a spawned Godot-mono
    doesn't read `~/.zshrc`, so it can't find `~/.dotnet` (→ "dotnet not found" / hostfxr,
    and a headless C#-init crash — same root cause). Fixed once with `tools/godot-mono.sh`
    (PATH + DOTNET_ROOT + arch). **Verified:** with PATH set, `--headless --import` works
    fine (~6 s, no crash) — the earlier "headless is broken for .NET" was an unproven guess;
    isolating the one variable (PATH) disproved it. Net: C# host code ≈ GDScript; iteration
    is heavier only by the build step.
- **2026-08-01 — First paired scene: scene 03 `mna_wave` (C# port of water-kit 25) verified on
  screen** (`tools/shot_03_mna_wave.png` — poke ripple with a crest + propagating rings, live
  `DemoUI` panel). The C# host reimplements scene_25's RD orchestration (4 storage textures, 7
  uniform sets, K Jacobi sweeps/tick ping-ponged with a barrier per sweep, two texture-copies
  rotating h_curr→h_prev→next) driving the **identical** `mna_wave.glsl`. Findings:
  - **RD host code ≈ 1:1** with the GDScript; compiled clean first try. Only C#-isms: `uint`
    casts on `RDTextureFormat` dims, `Godot.Collections.Array<byte[]>` for empty tex data.
  - **Built the reusable C# `DemoUI`** (`scripts/lib/DemoUI.cs`) — **instance-based** (getters
    live on the instance) vs the GDScript static + node-meta pattern; reads cleaner in C#.
  - **Host LOC ≈ parity** (~290 vs ~300). Per tick the render-thread call snapshots
    poke/beta/a/iters via a closure (small alloc) — GDScript uses `.bind()`, same idea.
  - **Not yet done:** the stamp contract is still the raw kernel — extracting `diag/conductance/
    rhs` into a `.glslinc` behind a compiler-checked C# contract is the next step toward the
    reusable `GpuStampSolver` (plan §3).
- **2026-08-01 — Backbone landed (plan §3): `GpuStampSolver`; scene 03 refactored onto it,
  renders identically** (`tools/shot_03_mna_wave.png`). The wave stamp is now
  `shaders/stamp/stamp_wave.glslinc` (`st_diag`/`st_conductance`/`st_rhs` + its state images +
  push constant); the relaxation is a generic `solve_jacobi.glslinc`. Swap the stamp, keep the
  solver. Design calls (flagged for review):
  - **Shader assembled in C#, not Godot `#include`.** The solver reads stamp + solve `.glslinc`
    via `FileAccess`, concatenates behind a fixed header (iter_in/out @ sets 2/3), and compiles
    with `ShaderCompileSpirVFromSource` + `GetStageCompileError`. Sidesteps Godot's
    `#include`/import path, gives explicit compile errors, and makes "which stamp" a C# choice
    (the contract's point). Trade-off: `.glslinc` read from `res://` at runtime — fine from
    source, would need packaging for an export build.
  - **Contract = GLSL funcs + a `Mode` enum, not a full C# interface yet.** Extracting
    `IStampProblem` from one impl is premature; add it when the 2nd stamp (heat, Phase 3) gives
    something to abstract against.
  - **Solver state is wave-shaped (h_curr/h_prev).** The generic state seam waits for a non-wave
    problem — same "two examples before abstracting" reasoning.
  - **Ergonomics:** runtime source assembly in C# is clean (string concat + one compile call +
    an error string). Net win vs GDScript: C# owns stamp composition + typed compile-error handling.
- **2026-08-01 — Step 2: GPU-reduction primitive + residual readout verified** (plan §1's "only
  non-trivial parallel primitive"). `reduce_residual.glslinc` computes r = b − Ax per cell, squares
  it, and shared-memory tree-reduces Σr² to one partial per workgroup (partials SSBO); the solver
  sums the partials (`BufferGetData`, low-rate sync) → ‖b−Ax‖₂. On screen:
  `‖b−Ax‖ 1.235E-003 · 30 sweeps · 256×256` — a real, small residual, proving the reduction is
  sensible end-to-end (not stuck at zero). Findings:
  - **`BufferGetData` works on the GLOBAL device** from `CallOnRenderThread` — a deliberate
    low-rate (every 12th tick) sync readback of 1024 floats; no crash. This was THE load-bearing
    unknown (`ComputeSmoke` only ever WROTE an image); reading a scalar back from C# is now proven.
  - **Reduction reuses the stamp** — the residual is stamp-generic (same st_diag/rhs/conductance),
    so every future problem gets a residual for free, and it's the same shared-memory tree the CG
    dot-products will use.
  - **Norm = L2, not RMS** — RMS over 65k cells washed a localized ripple down to 0.0000; L2 +
    scientific notation stays legible at any scale.
  - **Honest gap:** "ms/tick" isn't isolated GPU time yet (frame is vsync-capped; readout shows fps);
    true GPU timing needs timestamp queries — a later refinement. Today's predictable-cost knob is
    sweeps × cells (deterministic) + the achieved residual.
- **2026-08-01 — DC-drift fix (user's call): a leak-to-ground stamp element.** Closed-pool Neumann
  walls give the Laplacian zero row-sums → the mean/DC height mode has eigenvalue exactly 1, so
  damping kills ripples but all-positive pokes keep injecting volume that never drains → the surface
  creeps upward forever. Fix (physical + on-thesis): add `leak`, a conductance from every node to the
  rest datum (an MNA element), to the stamp DIAGONAL only — `st_diag = 1 + a + 4β + leak` — making
  the DC eigenvalue < 1 so the mean relaxes to flat. One line in `stamp_wave.glslinc`; verified on
  screen (surface settles flat, `‖b−Ax‖ 3.45E-005`, new `Rest leak κ` slider).
  - **⚠️ APPLIES TO THE GDSCRIPT TRACK TOO** — `../water-kit/shaders/mna_wave.glsl` (scenes 25/26/27
    share the operator) needs the same `+ leak` on the diagonal AND its host push-constant + a slider
    updated. It's a coordinated shader+host change, so it's left for the GDScript agent (relay).
  - **Two build gotchas hit (worth knowing):** (1) an incremental `dotnet build` reported success but
    the shot still ran the OLD assembly — needed `rm -rf bin obj .godot/mono` + rebuild to propagate.
    (2) Push constants must be a **multiple of 16 bytes**: adding `leak` made it 9 floats (36B) but the
    pipeline requires 48 — pad the C# array to 12 floats. (The old 8-float/32B layout worked only
    because 32 is already a 16-multiple.)
- **2026-08-01 — Step 3a: RBGS solve mode (Red-Black Gauss-Seidel).** Second solver behind the same
  stamp — `solve_rbgs.glslinc` is a checkerboard half-sweep (red then black per sweep); **parity is
  injected as a compile-time constant** (two pipelines, so no push-constant change and no in-place
  read/write hazard), reusing the Jacobi ping-pong + residual plumbing untouched. Verified on the same
  scene at 30 sweeps: **Jacobi ‖b−Ax‖ 3.45E-005 → RBGS 1.62E-006 (~21× lower)** — the ladder is legible
  (one stamp, swap the solver, convergence jumps). Fixed a self-inflicted double-free (had aliased
  `_shader = _shaderRed`, so `Free()` freed the same RID twice).

- **2026-08-01 — Step 3b: CG attempt CRASHED — naive GPU-CG is readback-bound.** Wrote full
  matrix-free CG (residual, SpMV, two-texture dot reduction, fused saxpy — 4 kernels, ~19 uniform
  sets, 4 vectors). Compiles and runs, but per tick it does ~2·iters **synchronous `BufferGetData`
  readbacks** (α, β pulled to the CPU each iteration). At 60 ticks/s that readback storm saturates
  the render thread until Godot's message queue overflows → `CallQueue::push_callablep` abort — the
  latency-bound-CG hazard, confirmed on screen (well, in a crash log). Viable fix: keep α/β
  **GPU-resident** (tiny kernels write them to a buffer; saxpy reads the scalar from the buffer, not a
  push constant) → zero per-iteration syncs. Reverted scene 03 to RBGS (known-good); CG code kept for
  the rework. ⚠️ Same trap awaits the GDScript track if it tries per-iteration-readback CG.
- **2026-08-01 — Step 3b DONE: CG working, GPU-resident scalars.** α/β now live in a scalar SSBO —
  dots reduce to a buffer slot (single-workgroup grid-stride), a 1-thread kernel does the α/β math,
  saxpy reads its coefficients from the buffer. The whole solve is ONE barrier-chained compute list,
  **zero per-iteration readbacks** (fixed iteration budget = predictable cost); one low-rate readback
  only for the on-screen residual. Two bugs fixed to get there:
  1. **UI resort loop** (the real cause of the "first CG crash") — the readout Label had no fixed
     width and was set every frame, so the container re-sorted every frame → message-queue overflow
     abort. Fixed: fixed `CustomMinimumSize` + autowrap + throttle the text to measure ticks. **⚠️
     GDScript track: same trap — fixed-width readout labels.** (Thanks to the user for spotting this.)
  2. **Stripped-set segfault** — `cg_spmv` included the stamp, which declares h_curr/h_prev images it
     never uses → compiler strips them → sets 0/1 vanish → creating/binding them segfaults. Fixed by
     making cg_spmv standalone (inline the wave operator, no state images). **Lesson: don't let a
     compute kernel declare uniforms it doesn't reference.**
  On screen: **Cg ‖b−Ax‖ 2.13E-015 @ 30 iters, 104 fps** (vs Jacobi 3.45E-005, RBGS 1.62E-006). The
  solver ladder is complete.
- **2026-08-01 — Task #3: Multigrid V-cycle (2-level) working.** A separate `MgvSolver` (behind a
  shared `IStampSolver` interface so the dropdown can recreate across solver families) runs a 2-level
  V-cycle on the inlined wave operator: fine RHS → pre-smooth (Jacobi) → residual → 2×2 restrict →
  coarse correction A_c e = r (β scales ×¼ per level) → bilinear-prolong + correct → post-smooth, a
  couple cycles/tick. Renders correct, stable water (drift fix holds), 120 fps, no crash — 5 new
  kernels, ~20 uniform sets, and cross-list ordering via texture_copy/clear between compute lists
  (the global device serialized them; no manual barriers needed). Honest caveats: 2-level (not a deep
  pyramid), residual ~7E-4 at 2 cycles (not machine-precision like CG). The "flat iteration count vs
  grid resolution" money-shot sweep is a further refinement, not built yet.
- **2026-08-01 — Phase 2: circuit sources on the grid (scene 04 `stamp_flow`).** The plan's Phase 2
  ("on-grid ideal sources") is a pure STAMP swap — no solver change. New `shaders/stamp/stamp_flow.glslinc`
  places both MNA sources on the grid: a **faucet** = ideal current source (persistent Gaussian added to
  `st_rhs`) and a **drain** = ideal voltage source = Dirichlet PIN (`st_diag=1`, `st_conductance=0`,
  `st_rhs=level` inside the drain disk; neighbours read the held level each sweep, so no symmetric
  elimination is needed for a relaxation solver). Same `GpuStampSolver`, damped-wave medium, and surface
  shader as 03 — "keep the solver, change the circuit." Relaxation settles to the steady flow field: a
  tilted gradient from the faucet mound down into the drain pit. On screen: **RBGS ‖b−Ax‖ 4.95E-6 @ 30
  sw, 120 fps.** Two lessons: (1) the stripped-set trap again — the flow stamp must keep referencing
  h_curr/h_prev (kept the leapfrog companion) or sets 0/1 vanish and the host's uniform creation fails;
  (2) with `leak≈0` the drain must sink the injected current, so faucet strength has to be small (~0.04,
  not 0.4) — a big current on a near-Laplace field builds an unbounded geyser. Solver dropdown is
  **Jacobi/RBGS only**: CG's SpMV and the multigrid kernels inline the WAVE operator, so they can't solve
  this pinned system — a stamp-based SpMV (→ symmetric Dirichlet handling, so CG stays SPD) is the natural
  follow-up. **GDScript track: the current-source-into-rhs + Dirichlet-pin pattern ports verbatim.**
- **2026-08-01 — Phase 3a: implicit heat / diffusion (scene 05 `stamp_heat`).** The cheapest generality
  proof — a pure stamp swap, no host change. Backward-Euler heat: `st_diag = 1 + 4κ + leak`,
  `st_conductance = κ`, `st_rhs = h_curr + inertia·(h_curr − h_prev) + source`. On screen: a hot source
  blob spreading MONOTONELY (no ripple) with a blue Dirichlet cold-pin, on the divergent temperature
  colormap — **RBGS ‖b−Ax‖ 6.17E-6 @ 24 sw, 120 fps.** Two findings worth carrying: (1) the set-1
  stripping trap bites ANY one-history stamp (heat needs only h_curr) — fixed cleanly by making the
  second history earn its keep as a real `inertia` (thermal-memory) knob whose push-constant coefficient
  stops the compiler stripping the h_prev read even at 0. (2) heat and flow share the SAME steady
  screened-Poisson operator; only the rhs time term differs (monotone-diffusive vs damped-oscillatory
  transient) — a clean way to see "the stamp picks the physics, the solver stays put." **GDScript track:
  backward-Euler heat is the same three-line stamp.**
- **2026-08-01 — Phase 3b: 2D plate reverb (scene 06 `stamp_reverb`).** The literal audio-DSP analog —
  and it's the SAME damped-wave stamp as scene 03 with two edits that turn a pool into a plate: (1) a
  clamped Dirichlet-0 border ring (`st_edge` → diag 1 / no coupling / rhs 0) so reflections build the
  plate's standing modes instead of Neumann-leaking, and (2) very low damping → a long ringing tail.
  Strikes (one-tick impulses) excite many modes that interfere and decay. On screen: a dense +/- modal
  field with white nodal lines, bounded by the frame — **RBGS ‖b−Ax‖ 1.08E-5 @ 24 sw, 110 fps.** Visual
  lesson worth carrying to the GDScript track: a reverb field's energy is spread over ~254² cells, so its
  per-cell amplitude is tiny and the divergent colormap washes to white. Fixed by adding a
  `colormap_gain` uniform to the shared surface shader (default 1.0 → scenes 03/04/05 untouched; reverb
  sets 6.0) — a cheap, additive way to make a low-amplitude field legible. **GDScript track: the
  clamped-edge damped wave + the gain uniform both port verbatim.**
- **2026-08-01 — Phase 3c: pressure-projection fluid (scene 07 `stamp_fluid`).** The honest edge of the
  thesis — a Stam stable fluid is NOT one `A x = b`, so it gets a purpose-built `scripts/lib/FluidSim.cs`
  (velocity `rg32f` + dye + pressure + divergence textures, 6 kernels) rather than a stamp swap. BUT the
  projection step is literally the same matrix-free Jacobi relaxation the stamp solvers use — ∇²p = div,
  warm-started across frames, ping-ponged in one compute list. Pipeline per tick: add force+dye+buoyancy
  → semi-Lagrangian advect velocity → divergence → K pressure iters → subtract ∇p → advect dye. On
  screen: curling buoyant smoke with clear vortices — **256², 40 pressure iters, 120 fps.** Two bugs the
  rendered frame caught (numbers looked fine both times): (1) closed-box **flooding** — continuous dye
  injection + weak dissipation saturates the whole domain; fixed with strong dye dissipation (0.97) +
  less injection. (2) ragged **wall pile-up** — clamped-but-nonzero border velocity smears fluid along
  the walls; fixed with **no-slip walls** (zero the border velocity in gradient-subtract). Thesis
  takeaway: the stamp solver's Poisson relaxation is the reusable core *even when* the surrounding
  problem (advection, multiple fields) isn't a stamp — "same solver, bigger circuit." **GDScript track:
  FluidSim ports as-is; the pressure Jacobi is the scene-03 relaxation with div as the RHS.**
- **2026-08-01 — Phase 4a: the stamp solver in 3D → marching-tets blob (scene 08 `blob3d`).** The 2D
  solver lifted to a volume is nearly free: `GpuStampSolver3D` is image2D→image3D, ivec2→ivec3, 5-point→
  7-point, dispatch 4×4×4; the stamp contract is unchanged (just add the z-neighbours). The interesting
  half is DISPLAY without ray tracing — read the volume back each frame and polygonize on the CPU. Chose
  marching **tetrahedra** over the 256-case marching-cubes table: the per-tet logic is tiny, unambiguous,
  and watertight, and per-vertex gradient normals + a double-sided material make triangle winding
  irrelevant. On screen: a lit green blob wobbling from 3D pokes — fmax 2.39, iso 0.55, ~18k tris, **21
  fps**. Note the cost is the per-frame readback + CPU polygonization + `ArrayMesh` marshalling, NOT the
  solver (the 3D Jacobi is trivial) — a GPU marching-cubes writing straight to a vertex buffer would lift
  it, but the readback→C# path is the honest, C#-strength version. **GDScript track: the 3D solver ports;
  the CPU polygonizer is where C# actually earns its keep.**
- **2026-08-01 — Phase 4b: 3D fluid → GPU particles (scene 09 `fluid3d`).** `FluidSim3D` = the scene-07
  Stam pipeline lifted to a volume (rgba32f velocity, 6 kernels, 4×4×4 dispatch); the projection is the
  same Jacobi relaxation solving ∇²p = div on a 7-point 3D stencil. Display without ray tracing: read the
  velocity volume back and CPU-advect 12k MultiMesh billboard particles → a buoyant 3D plume with vortex
  structure, **~115 fps**. Two look lessons: (1) additive blending saturates dense cores to white, so
  keep per-particle alpha low (~0.1) and colour by age (warm plume palette) or it's a flat cream blob;
  (2) hard QuadMesh billboards read as confetti — a 32² soft radial-gradient texture turns them into
  smoke puffs. The per-frame velocity readback is cheap enough that it never dented framerate. The shared
  `FluidSim3D` density field also feeds scene 10 (FogVolume). **GDScript track: ports; MultiMesh buffer
  set from the readback each frame.**
- **2026-08-01 — Phase 4c: 3D fluid → volumetric FogVolume (scene 10 `fog3d`).** The third display path
  for the SAME `FluidSim3D`: wrap the density RID in a `Texture3DRD` and sample it from a `shader_type
  fog` shader inside a Godot `FogVolume` (Box) — real engine froxel volumetrics, **no readback and no
  custom ray marching** (the sim's RD texture feeds the renderer directly, the reverse of scenes 08/09
  which read back to the CPU). The stamper still does the 3D pressure projection. Result: soft,
  self-shadowed volumetric smoke showing the plume — softer than particles (froxel grid is coarse and not
  readily exposed) but the least-code route to a true volumetric look. Gotchas: `Environment
  .VolumetricFogGiInject` isn't bound in this 4.6 build (compile error → dropped it); same closed-box
  flooding as before, so faster dye dissipation (0.965) keeps a defined plume. **Three 3D display paths
  now span the space: isosurface mesh (08), instanced particles (09), volumetric fog (10) — same solver,
  three ways to see a volume without ray tracing.** GDScript track: all three port; `Texture3DRD` → fog
  is the least-code volumetric option.
- **2026-08-01 — Smoke drifting in a world (scene 11 `smoke_world`).** "Can the 3D fluid float around a
  bigger scene naturally?" → yes, *within the fixed grid box*: embed the box in a lit world (dusk sky,
  ground, low warm sun, a varying breeze at the source) and the plume wafts and curls naturally. Tried it
  first as a FogVolume (scene-10 tech) in the open scene → **muddy**: engine froxel volumetrics are too
  soft and low-contrast against a lit sky+ground. Pivoted to the scene-09 particles → glowing embers pop
  against a dark dusk sky, 14k @ 80 fps. **Finding: for smoke IN a scene, instanced particles beat the
  FogVolume on contrast and definition; FogVolume is better for a contained, dim volume.** Honest limit
  restated: the smoke only exists in the 48³ grid box — a scene-spanning cloud needs a bigger/coarser
  grid or a grid that FOLLOWS the plume (advect the sim origin with the smoke's centre of mass), which is
  the natural next build if we want a cloud that travels across a whole level.
- **2026-08-01 — Movable fog machine (scene 12 `fog_machine`).** Answering "bigger box + a fog machine on
  the floor + a box I can move around": parent the machine mesh + the particle MultiMesh (rig-local
  coords) + a wireframe box outline under one movable `Node3D`. The sim stays in its fixed 48³ grid and
  the rig transform just places the volume, so dragging the rig (left-drag → floor raycast) carries the
  whole fog box through the world — no sim change. Bigger world box (8u) on the same grid. **Perf gotcha
  the frame caught: a first pass at 16k × 0.07 additive billboards in the big screen-filling box ran at 2
  fps — pure OVERDRAW** (a dense additive column blended hundreds of times per pixel), not particle count
  or the readback. Cutting to 9k × 0.04 → 60 fps. Lesson for particle fluids: overdraw is the fill-rate
  wall; keep billboards small once the volume fills the screen.

- **2026-08-02 — Backward-Euler↔Crank-Nicolson scheme slider (scene 03).** The user caught that ripples
  die almost immediately even at damping = 0 — and was right. The implicit wave step is *numerically
  dissipative*: that's exactly what buys "unconditionally stable at any dt," and it worsens with
  β = dt²c² (so cranking wave speed made propagation SHORTER). Zeroing the physical γ∂h/∂t term doesn't
  help — the integrator is the loss. Fix: a θ-blend `cn` in `stamp_wave` that splits the stiffness across
  time levels — cn=0 = fully implicit (backward-Euler, the original, damped); cn=1 = Crank-Nicolson
  (stiffness shared symmetrically with the known past level t−dt, so the scheme is time-reversal
  symmetric → NO numerical damping → ripples ring, and still unconditionally stable). Proven on screen
  with a controlled RBGS before/after (identical pokes): cn=0 near-flat (only the last poke's smear
  survives) vs cn=1 alive with propagating/reflecting waves. Now honoured by Jacobi, RBGS **and Multigrid** —
  θ was threaded through the V-cycle (`aμ = 1−½cn` scales the smoother/residual operator on both levels;
  `mg_rhs` adds the `½cn·D(hₚ)` past-level term) so it rings on MG too (211 fps). Only CG is left out
  (its `cg_spmv` inlines the wave operator); threading θ there is the last loose end. Caveat: at cn=1 with damping 0 the medium is lossless, so repeated pokes
  accumulate energy without bound — dial cn a hair below 1 (reintroduces a little dissipation) for a slow,
  natural decay. (Corrected an earlier wrong "damping 0 = lossless" claim of mine — the scheme was the
  loss, not the physics.)

## Handoff to the C# agent — start here

Everything's staged; the hardest unknown (RD-compute from C#) is proven on screen (scene 02).
1. Launch ONLY via `tools/godot-mono.sh` (fixes PATH + arch). Build: `~/.dotnet/dotnet build`.
2. Copy `scripts/ComputeSmoke.cs` (scene 02) as the RD-compute pattern.
3. Reuse `shaders/mna_wave.glsl` + `mna_pack_normal.glsl` verbatim (shared portable core).
4. Follow `docs/gpu-stamp-solver-plan.md` §7: C# `DemoUI` (scripts/lib) → the `GpuStampSolver`
   backbone → Phase-1 solver ladder (Jacobi→RBGS→CG→multigrid) + residual/iters/ms readout.
   Read `../water-kit` scenes 25/26/27 as the reference — reimplement, don't copy.
5. Verify every scene with the harness (Read the PNG); append findings to this log.
