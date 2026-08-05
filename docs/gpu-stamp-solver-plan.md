# GPU Stamp Solver — Plan (C# track)

> A reusable, GPU-parallel, **predictable-cost** linear-solve substrate. Define a
> problem by *stamping* per-cell elements into `A x = b`; one matrix-free solver
> relaxes it on the GPU. Swap the stamp → water, heat, pressure, reverb.
> "Keep the solver, change the circuit" — the spatial analog of how audio DSP
> models circuits (Wave Digital Filters / state-space / digital waveguides).

**This is the C# implementation of a two-track parallel experiment.** The GDScript
track lives in `../water-kit` (already has scenes 20/25/26 proving the idea). Both
tracks build to the SAME spec; we compare architecture, ergonomics, and perf, and
cross-pollinate via `docs/comparison-log.md`. Status: DRAFT for discussion.

---

## 0. Why two tracks, and what's shared

The valuable IP — the solver — is **GLSL compute** (`RenderingDevice` loads the
same `.glsl` from C# or GDScript). So the two tracks differ ONLY on **host glue +
CPU-side orchestration** (RD setup, ping-pong, and — the interesting part —
multigrid V-cycle recursion, CG reductions, benchmarking). That's exactly the axis
worth comparing.

- **Shared, already copied here:** `shaders/mna_wave.glsl` (GPU Jacobi implicit-wave
  stencil) and `shaders/mna_pack_normal.glsl` (height→normal packer). Reuse these
  verbatim so the comparison stays clean.
- **Shared spec:** this plan + the GDScript reference scenes in `../water-kit`
  (15 raytraced pool · 20 MNA network · 25 GPU wave · 26 MNA→raytraced · 27
  compute-fresh). Read them as the reference implementation; do NOT copy their
  GDScript — reimplement in idiomatic C#.
- **Cross-learning:** append findings (ergonomics, perf numbers, gotchas) to
  `docs/comparison-log.md`. The two agents can't talk directly; the user relays.

### Where C# is expected to shine (to be tested, not assumed)
Compiler-checked generics/interfaces for the stamp contract; zero-GC `struct`
value types in tight numeric loops; recursion-heavy **multigrid V-cycle**
orchestration; **Conjugate Gradient** numerics + reductions; credible
**benchmarking** (real timing, controllable GC); unit tests (xUnit). Prove the
durable, reusable `GpuStampSolver` here.

---

## 1. The thesis (predictable + GPU-parallel)

Most "simulate X" is secretly `A x = b`: implicit diffusion/heat, implicit waves,
pressure projection, cloth backward-Euler, screened-Poisson, radiosity. Stamp the
elements (a cell's diagonal + neighbor conductances + rhs) and one engine solves
them all.

- **Massively parallel, matrix-free.** The matrix is never stored — each cell reads
  4 neighbors, writes itself. No gather/scatter, coalesced, no divergence (bar a
  red-black parity split). Only non-trivial parallel primitive: the reduction (dot
  products) CG needs.
- **Predictable, dial-able cost.** `iters × cells × stencil` = a fixed FLOP budget
  you choose → deterministic frame time. Unconditionally stable (no CFL). Multigrid
  hits a target residual in ~constant V-cycles **independent of resolution** →
  `O(cells)`, cappable into an "anytime" solver. Predictable *and* scalable.

---

## 2. Constraints — build on the corpus, break nothing

**Reuse (read-only):** `shaders/mna_wave.glsl`, `shaders/mna_pack_normal.glsl`
(copied here); the `../water-kit` reference scenes (as spec, not to copy);
`tools/shoot.tscn` (the working screenshot harness — GDScript on purpose).

**Rules:**
1. **Additive only.** New numbered scenes (`scenes/NN_<name>.tscn` +
   `scripts/<PascalCase>.cs`) + new `scripts/lib/*.cs`. Never edit a working file
   to make a new one.
2. **C# gotchas** (see `CLAUDE.md`): classes are `partial : Node3D`; must build
   (`~/.dotnet/dotnet build`) before the scene runs; launch the harness with
   **`Godot-mono.app`**, **`arch -arm64`**, and **NO external `timeout`** — use the
   harness's own N-seconds self-quit (or `--quit-after`).
3. **Verify every scene on screen** via `tools/shoot.tscn`. New `.glsl` needs a
   one-time import (`arch -arm64 Godot-mono … --import --path .`).
4. **Prereq (Phase 1 of the repo ROADMAP): a C# `DemoUI`** slider/panel builder in
   `scripts/lib/` — the solver scenes need live sliders. Build it early (mirror
   `../water-kit/scripts/lib/demo_ui.gd`).
5. Numbers race with sibling sessions — grab next-free, don't clobber the README.

---

## 3. The backbone: `scripts/lib/GpuStampSolver.cs`

The one thing everything reuses. An RD compute harness (global device via
`RenderingServer.GetRenderingDevice()`, all rd work inside
`RenderingServer.CallOnRenderThread(Callable.From(...))`, storage-image ping-pong,
`Texture2Drd` output). See `scripts/ComputeSmoke.cs` (the proof-of-plumbing this
groundwork ships) for the verified C# RD pattern to copy.

Parameterize by:
- a **stamp include** (`.glslinc`) implementing a fixed contract —
  `float diag(ivec2 c)`, `float conductance(ivec2 c, ivec2 n)`, `float rhs(ivec2 c)`.
  In C#, model the problem set as an interface/enum so the stamp choice is
  compiler-checked.
- a **solve mode**: `Jacobi | Rbgs | Cg | Mgv` (Phase 1).
- **budget knobs**: iterations / V-cycles / target residual; expose achieved
  residual + effective iters + ms so the predictable-cost story is on screen.

---

## 4. Phase 1 — Solver quality (Jacobi → RBGS → CG → Multigrid)
Same problem, four solvers, convergence + cost made legible.
- **Jacobi** (baseline; `mna_wave.glsl` already is one).
- **Red-Black Gauss-Seidel** — two-pass checkerboard, ~2× Jacobi, GPU-trivial.
- **Conjugate Gradient** — matrix-free SpMV + GPU reductions; far fewer iters.
- **Geometric Multigrid (V-cycle)** — restrict → smooth → prolong; `O(cells)`,
  ~constant cycles vs resolution. The flagship (recursion-heavy — a C# strength).

**Deliverable:** `NN_stamp_solvers` — one implicit-wave problem, a solver dropdown,
live **residual · iters · ms/tick · grid size** readout. Sweep grid size to show
multigrid's flat iteration count.

## 5. Phase 2 — Circuit vocabulary (on-grid ideal sources)
Faucet = current source (rhs); drain/fixed-level = voltage source (Dirichlet pin —
the "modified" part at grid scale). **Deliverable:** `NN_stamp_flow` — pour in one
corner, drain in another, steady implicit flow. Unifies the network + grid views.

## 6. Phase 3 — New problems, same solver
Each = a stamp include + a thin C# scene reusing §3.
- **Heat / diffusion** (cheap generality proof).
- **Pressure-projection fluid** (Stam) — advect + Poisson-project; stirrable smoke.
- **2D plate reverb** — a damped membrane; the literal audio-DSP analog.

---

## 7. Sequencing
`DemoUI (C#)` → `ComputeSmoke` (done, RD plumbing verified) → §3 backbone + fold
the wave stamp behind the contract → Phase 1 solvers + readout → Phase 2 sources →
Phase 3 (heat → fluid or reverb). Multigrid slots into §3 whenever we want the "to
the limit" moment.

## 8. Open decisions (shared with the GDScript track)
- Backbone-first vs. spike a solver in a scene first?
- Phase-3 flagship: pressure fluid vs. plate reverb?
- How hard to chase multigrid now vs. RBGS/CG first?
- Expose the solver as a reusable `scripts/lib/` type (proposed: yes — that's the
  whole point of the C# track).
