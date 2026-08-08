# Solver Ledger — status, blockers, and the facts you need before touching any of it

Companion to `gpu-stamp-solver-plan.md` (the *intent*) and `mna-next-steps.md` (the roadmap).
This file is the **state**: what exists, what is broken, why, and what "done" means.

Written after a long session on scene 25 (`WaveTankMna`) in which most of the time went to
rediscovering §4. Read §4 first. Seriously.

**Current position:** **9 solvers in 2D, 4 in 3D.** §7a/b/c are built (`MgDeepSolver`,
`SchwarzSolver`, `SpectralSolver`). §7d's **nD contract is done** — every solver kernel in the
repo is dimension-generic and there is a real 3D benchmark column (§10); **tiled storage, the
other half of §7d, is still open**. The instrument was rebuilt on a local RenderingDevice (§9),
which is what finally made any of these numbers trustworthy.

**Before touching Phase 2, read §8 and §9a.** §8a (CG reports a *recursive* residual, everyone
else a *recomputed* one) and §8c (everything bottoms out at the float32 floor ~5e-08) both still
bind. §9a adds two more: the local device needs a double `Step`-Sync to read a residual at all,
and **ms/step is ±35% noise while residual is bit-exact** — so rank on residual. §1a is the
other section that matters — it names the reference the GPU port drifted away from.

**The headline result so far (§10d):** in 3D, relaxation falls off a cliff between 64³ and 96³
and deep multigrid does not. At 128³ MgDeep3D and Jacobi3D cost the same 3.07 ms and MgDeep's
residual is 2178× better.

---

## 1. The contract

A stamp is a `.glslinc` providing exactly three functions plus its own push constant:

```glsl
float st_diag(ivec2 c);                  // diagonal
float st_conductance(ivec2 c, ivec2 n);  // off-diagonal to neighbour n
float st_rhs(ivec2 c);                   // companion source
```

Solve bodies (`solve_*.glslinc`) are generic over any stamp; the host supplies `iter_in` (set 2)
and `iter_out` (set 3), the stamp declares `h_curr` (set 0) / `h_prev` (set 1).
`IStampSolver` is the C# surface: `Step(pc, iters, measureResidual)`, `HeightRid`, `PrevRid`,
`LastResidual`, `ModeName`.

**The push constant MUST begin with `vec2 size`.**

### 1a. The origin contract — and what the GPU port lost

The stamp idea did not start here. It started in
`../neptunely_standalone/js/audio/cmajor/lib/mna-solver.cmajor`, and **that version is
dimension-free.** Read `stampResistor`:

```cmajor
void stampResistor (System& sys, int node_a, int node_b, float64 G)
{
    addA (sys, node_a, node_a,  G);   addA (sys, node_a, node_b, -G);
    addA (sys, node_b, node_a, -G);   addA (sys, node_b, node_b,  G);
}
```

**Two arbitrary integer node ids.** No lattice, no `ivec2`, no neighbour table. Adjacency is
*data* — a struct field — not *code*. A 2D plate, a 3D volume, a 4D lattice, a Rhodes tine, and
an actual guitar pedal are all the **same solver**; only the stamping loop differs.

It is the same operator we run on the GPU. `A[a,a] += G` accumulated over incident edges **is**
`st_diag = 1 + a + leak + Σ_faces β_face`; `A[a,b] -= G` **is** `st_conductance(c, n) = β_face`.
The GPU stamp is the cmajor stamp with the edge loop **unrolled over a fixed 4-neighbour lattice**
and `A` never materialized (matrix-free).

**The unroll is the whole problem.** `const ivec2 offs[4]` hardcodes dimension *and* regularity
into every solve body (§3e). That is why `shaders/stamp3d/solve_jacobi_3d.glslinc` is a **copy**
with its own `ivec3 offs[6]` and its own host, not a generalization — and why 4D, irregular
networks, and tiled/streamed storage are all blocked behind the same one-line decision.

**Two more things the cmajor file has that the GPU side does not:**

- **`stampCapacitorTrap` is Crank–Nicolson by construction** — `Geq = 2C/dt` in parallel with a
  history current source `Ieq = Geq·v_prev + i_prev` is the trapezoidal rule. Our `cn` θ-blend
  (`mna-next-steps.md` §0) is the same insight, arrived at separately and bolted on. In MNA it
  falls out of the component model.
- **`stampVoltageSource(..., extra_idx, Vs)` — the bordered constraint row.** MNA handles an
  exact constraint by *growing the system*: an extra unknown (the source current) with `±1`
  couplings. That is the principled answer to the κ problem in §4 — a leak spring is a *penalty*
  and imposes a Klein–Gordon cutoff; a constraint row imposes zero-mean (or a prescribed paddle
  displacement) **exactly, with no cutoff**. The `volume balance` and `boundary-only` DC modes
  are hand-rolled approximations of a bordered system.
- **`solve()` is a 32×32 dense LU with partial pivoting** over a flat `float64[1024]` — 4 KB.
  That is the item-4 block-dense kernel, already written, and 4 KB fits any LDS trivially (§7b).

---

## 2. Index — both repos

| Solver | C# repo | water-kit | Status |
|---|---|---|---|
| Jacobi | `solve_jacobi.glslinc` | `mna_wave.glsl` | ✅ works |
| Red-black GS | `solve_rbgs`, `solve_rbgs_wrap` (toroidal) | `solver_gs.glsl` | ✅ works |
| Conjugate Gradient | `cg_{residual,spmv,dot,dotbuf,calc,saxpy,saxpybuf}` | `cg_{rhs,prod,spmv,vec_update}` | ✅ **unblocked** — scene 50 re-verify owed (§3a) |
| Multigrid V-cycle (2-level) | `mg_{rhs,smooth,residual,restrict,prolong}` + `MgvSolver` | same + scene 40 | ⚠️ **the CONTROL** — scalar-β operator, 2 levels. Deliberately kept & do-not-edit (§3b, §7a). Cost readout is wrong (§8d) |
| Multigrid deep | `mgd_{smooth,residual}` + `MgDeepSolver` | — | ✅ **new** — stamp operator, full pyramid to 8² (§7a) |
| ADI (line/Thomas) | `solve_adi.glslinc` + `AdiStampSolver` | — | ✅ residual + barrier fix in; occupancy poor (§3c) |
| Block-dense Schwarz | `solve_schwarz.glslinc` + `SchwarzSolver` | — | ✅ **new** — 8×4 tiles, dense LU in LDS (§7b). 256²-only (§8d) |
| Spectral DCT | `dct_1d`, `dct_scale` + `SpectralSolver` | — | ✅ **new** — exact for uniform depth (§7c). 256²-only (§8d) |
| 3D stamp | `GpuStampSolver3D` + `stamp3d/solve_jacobi_3d.glslinc` | — | ⚠️ a **copy**, not a generalization (§1a, §3e) |
| Tiled / streamed storage | `solve_rbgs_wrap` (2D toroidal only) | — | ⛔ **the one left** (§7d) |
| Pressure/Poisson | — | `pf_solve`, `pf_reduce_max` | not indexed yet |

Dropdown as shipped (`WaveTankMna.cs`): `Jacobi · RBGS · ADI (line) · CG · Multigrid (uniform β) ·
Multigrid (deep) · Schwarz (block-dense) · Spectral (uniform depth)`.
Every mode is reachable headless via `-- solver=N grid=N sweeps=N bathy=N bench=N`
(`bathy=` added this session — it is the variable that separates real-operator solvers from
uniform-depth ones, so it had to be settable without the GUI).

Reference scenes: water-kit **20** (CPU MNA network), **25** (GPU Jacobi wave), **26** (25 →
raytraced), **29** (solver ladder + residual readout), **40** (multigrid). C# repo: **50**
`BoatMna` (RBGS + toroidal window + sponge), **25** `WaveTankMna` (this session's fork).

**Not a Godot repo, but the primary reference:**
`../neptunely_standalone/js/audio/cmajor/lib/mna-solver.cmajor` — the topology-free stamp
grid + 32×32 dense LU. See §1a. This is the design authority for items §7b and §7d, and the
reason §3e is a *regression* rather than a missing feature.

**⚠️ `godot-csharp-experiments` is not a git repository.** No diff, no history, no revert.
Anything below marked ✅ was verified by reading the file, not by a passing test.

---

## 3. Blockers, with causes

### 3a. CG — push-constant size mismatch — ✅ FIXED
Was: `GpuStampSolver.StepCg` forwarded the **stamp's** push constant to `cg_dotbuf`, `cg_calc`
and `cg_saxpybuf`, which declare their **own** 48-byte and 16-byte layouts, so every dispatch
logged `This compute pipeline requires (48) bytes ... supplied: (128)`. It never surfaced
because scene 50 only ever ran RBGS.

Now: those three kernels each declare their own `layout(push_constant) uniform P`
(`cg_dotbuf:13`, `cg_calc:12`, `cg_saxpybuf:13`) built by the host, and `cg_spmv:21,24` calls
`st_diag`/`st_conductance` instead of the old inlined 48-byte wave operator — so CG inherits
whatever the stamp defines. In the dropdown.

**⚠️ Still owed: re-verify scene 50.** `GpuStampSolver` is shared with `BoatMna` (§5) and this
changed it. Nobody has run 50 since.

### 3b. Multigrid — rhs is stamp-generic; the operator is not, and it is still 2 levels
`mg_rhs:18` now calls `st_rhs(c)`, and `MgvSolver` compiles that one kernel against the stamp
source — so sources/paddle/drop terms reach the fine RHS correctly.

**But the operator it actually inverts is still uniform-depth.** `mg_smooth:15,31,35` and
`mg_residual:13,29,34` read a **scalar `pc.beta`** out of `MgvSolver`'s own
`SmoothPc(size, beta, a, leak, cn)`. So multigrid solves a *different problem* from the one
Jacobi/RBGS/CG solve the moment `extra.z` (bathymetry coupling) is nonzero. The dropdown label
`Multigrid (uniform β)` is honest about this; it is not a fix.

Depth: `MgvSolver.cs:46` builds exactly one coarse level (`grid/2`); `NuCoarse = 8` is a
smoother standing in for a coarse solve. `mna-next-steps.md` §4 wants **256→128→…→8**, the
prerequisite for every "crank the resolution" experiment.

Two invariants for the rebuild (§7a):
- **`beta_scale` contains `1/dx²`, so it must divide by 4 per coarsening level** (by 8 in 3D).
  The convention is already documented at `mg_smooth:3` (`beta_L = beta_0 / 4^L`); the host
  applies it once, at `MgvSolver.cs:98`.
- **Lucky break:** if the bed is analytic (as in `stamp_wave_tank`), `st_depth()` derives depth
  from `st_x(c)` and `pc.size`, so handing a coarse level **its own `size` in the stamp push
  constant** re-derives correct per-face conductances automatically. Variable-coefficient
  multigrid normally needs Galerkin coarsening; **this stamp coarsens itself.** That is what
  makes §7a a plumbing job rather than a numerics project.

### 3c. ADI — residual DONE, divergence fixed; occupancy still poor
`AdiStampSolver` now carries the residual pipeline (`ResidualHeader` + stamp +
`reduce_residual.glslinc` + SSBO readback, its own 8×8 shader separate from the 64×1 line
solve), so it can finally be ranked instead of eyeballed. Prints `residual=True` on init.

**Divergence fixed.** ADI was returning residual ~1e+11 at 1024². Cause: the Thomas scratch
images `scratch_cp` / `scratch_dp` were plain `uniform image2D`, so the back-substitution was
not reliably reading what the forward sweep wrote. Now `coherent`
(`solve_adi.glslinc:24-25`) plus a `memoryBarrierImage()` between the sweeps (`:91`).

**Still open:** one thread per line = ~N working threads, poor occupancy by construction —
~7.5 fps at 512². The upgrade is **parallel cyclic reduction** (a workgroup co-operates on one
line, log₂N steps).

*Gotcha found while wiring it:* in `Free()`, uniform sets must be released **before** the
textures they reference — Godot auto-frees dependent sets, so freeing textures first makes the
later set-frees invalid (`Attempted to free invalid ID`).

### 3e. The nD regression — `offs[4]` is hardcoded in every solve body — ✅ FIXED (see §10)
The stamp *functions* are dimension-agnostic; the **solve bodies are not**. Every one of these
declares its own `const ivec2 offs[4] = ivec2[4](ivec2(1,0), ivec2(-1,0), ivec2(0,1), ivec2(0,-1))`:

`solve_rbgs:25` · `solve_rbgs_wrap:25` · `cg_spmv:19` · `cg_residual:12` · `mg_smooth:29` ·
`mg_residual:27` · `reduce_residual:19` (and `solve_jacobi` / `solve_adi` by construction).

`shaders/stamp3d/solve_jacobi_3d.glslinc:13` responded to this by **copying the file** and
writing `ivec3 offs[6]`, with `GpuStampSolver3D.cs` as a parallel host. That is the regression:
the cmajor original (§1a) never had a neighbour table at all. Fix in §7d — it is the same edit
that unlocks 4D, irregular networks, and tiled storage, so it is worth doing once, properly.

### 3d. The instrument — PARTIALLY BUILT
`gpu-stamp-solver-plan.md` §4 already specifies the deliverable: *"one implicit-wave problem, a
solver dropdown, live **residual · iters · ms/tick · grid size** readout; sweep grid size to
show multigrid's flat iteration count."* Now in scene 25:
- **residual for every solver**, including ADI (§3c).
- **`PassesPerStep(iters)` on `IStampSolver`** — the honest cost unit, since a "sweep" is not
  comparable across solvers (Jacobi 1 dispatch, RBGS 2, ADI 2 line passes, MGV `nu1+nu2+coarse`).
- **ms/frame and ms/tick** in the readout. **Derived** (frame time ÷ ticks) and it *includes
  render* — fine for A/B at a fixed scene, not an absolute solver cost. `RenderingDevice`
  exposes no GPU timestamps, so a true per-dispatch number needs a different mechanism.
- **Grid dropdown 256²/512²/1024²**, rebuilding the solver on change — this is what makes the
  plan's "sweep grid size, show multigrid's flat iteration count" experiment possible.

**Still unwired:** `shaders/stamp/field_profile.glslinc` (per-bucket Σh² and max|h| across x —
shows *where* energy lives, which answers "the wave dies at X"). Needs a host class with an
SSBO + low-rate readback. And the **iteration-vs-resolution curve is still not plotted** —
`mna-next-steps.md` §4 calls that "the whole predictable-cost thesis, never plotted".

---

## 4. Numerics facts — the section that would have saved a day

**β is the Courant number squared.** `β = g·h·dt²/dx²`. For a tank spanning 2 pool units on an
N-cell grid, `dx = 2/N`, so **halving `dx` quadruples β**. Working scenes sit at β ≈ 0.1–0.65:

| scene | grid | β | iters |
|---|---|---|---|
| water-kit 25 / 26 | 256² | 0.64 | 30 Jacobi |
| water-kit 40 | 256² | 0.64 | 2 V-cycles |
| C# 50 `BoatMna` | 512² | 0.107 | 13 RBGS |

**Relaxation convergence collapses as β grows.** Jacobi's worst-mode factor is `4β/(1+4β)`:
β = 0.107 → 0.30 (13 sweeps ≈ 1e-7, solved). β = 7.5 → **0.968** (13 sweeps removes ~⅓).
An unconverged implicit step leaves `h_next` where `h_curr` was, because the iterate is seeded
from `h_curr` — so **under-convergence does not look like noise, it looks like frozen, viscous
water that no damping knob can fix.** Verified: dropping β 7.5 → 0.10 took the residual from
9.2e-03 to 2.5e-06 and the field went from dead-at-the-paddle to rings crossing the tank.

**Relaxation is a smoother, not a solver.** The Jacobi eigenvalue is
`λ(θ) = 2β(cos θx + cos θy)/(1+4β)`; it → 0 near θ = π/2 and → 4β/(1+4β) as θ → 0. Short
ripples converge in a sweep or two, **long waves essentially never**. Symptom: ripples
propagate happily while swell stalls and decays. That is the entire reason multigrid exists.

**A solver that converges better will look *worse* against tuning done on a bad one.** RBGS
made the water "disappear" in scene 25 — residual 3.6e-06, i.e. correct — because the gains had
been tuned against Jacobi's artificial damping. Re-tune per solver, or compare at matched
residual, never at matched sweeps.

**The leak term is a spring, not a damper.** `κ` turns the wave equation into Klein–Gordon:
`ω² = c²k² + κ`, giving a **cutoff** `f₀ = √κ · simHz / 2π` below which waves are evanescent
and cannot propagate at all. Measured in scene 25: κ = 9e-4 at 197 Hz → f₀ = 0.94 Hz, while the
paddle ran at 0.21 Hz — the waves were *mathematically forbidden* from leaving the paddle.
But κ is needed, because a monopole source pumps net volume into a closed box and the mean
level drifts. Resolutions (scene 25 has all six as a dropdown): none · uniform spring ·
authored-cutoff · **volume balance** · boundary-only · **dipole source**. The last two remove
drift without imposing a cutoff; prefer them.

**Sources: displacement vs rhs term.** The explicit kernel does `info.r += force` on `h_curr`,
so the source lands as a displacement *and* enters the velocity term `(hc − hp)` — that kick is
what launches a wave. Adding `force` as a standalone rhs term instead is a static push. Port as
`2·(hc + force) − hp`.

---

## 5. Environment gotchas (all cost real time)

- **Push constants are capped at 128 bytes.** Godot enforces it (`Push constants can't be
  bigger than 128 bytes to maintain compatibility`). `stamp_wave_tank` is at exactly 32 floats;
  new fields must be packed (its DC mode rides in the high bits of the sponge edge mask, and
  mode 3's correction reuses the `leak` slot since they are mutually exclusive).
- **`.glsl` edits do nothing until Godot re-imports.** `GD.Load<RDShaderFile>` reads compiled
  SPIR-V from `.godot/imported/`; launching a scene does not re-import. Run
  `tools/godot-mono.sh --headless --path . --import` and check the artifact mtime. `.glslinc`
  files compiled via `ShaderCompileSpirVFromSource` are read at runtime and do NOT need this.
- **`GpuStampSolver` is shared with scene 50.** Any change re-verifies 50.
- Verify with `tools/shoot.tscn`; `grep -c 'SHOOT saved'` to confirm the PNG was written.

---

## 6. Ordered work list

**Build everything first. Profile after.** Benchmarking a half-populated dropdown produces a
table you have to throw away the moment the next solver lands — and the numbers currently on
record were taken while the build was silently falling back to a stale assembly (§5), so they
are void regardless. **No profiling passes until §7d is done.**

### Phase 1 — build

| # | Item | New artifacts | Sketch | |
|---|---|---|---|---|
| 3 | **Multigrid deep — as a FORK** | `MgDeepSolver.cs`, `mgd_smooth`, `mgd_residual` | §7a | ✅ |
| 4 | Block-dense Schwarz in LDS | `SchwarzSolver.cs`, `solve_schwarz.glslinc` | §7b | ✅ |
| 5 | Spectral DCT (uniform depth) | `SpectralSolver.cs`, `dct_1d`, `dct_scale` | §7c | ✅ |
| 6 | nD contract + tiled storage — **2D/3D only** | edits to all 8 solve bodies; deletes `stamp3d/` | §7d | ⛔ **next** |

Ordering rationale: §7a/b/c are independent, each adds a dropdown entry, and **none of them
modify a working solver** — 7a is explicitly a fork (§7a), 7b and 7c are new files. §7d is the
only destructive one: it rewrites every solve body, so it goes **last** or it gets done twice.
§7d also subsumes the old "item 1" (nD contract) — a neighbour list and a tile's face set are the
same mechanism (§1a).

**Scope calls locked this session:** multigrid is a **fork, not a modification**; §7d covers
**2D and 3D only, no 4D**; §7c has a real working reference after all (§7e).

### Phase 2 — profile (blocked until Phase 1 lands)

1. Re-verify **scene 50** (owed since §3a touched shared `GpuStampSolver`).
2. Wire `shaders/stamp/field_profile.glslinc` — it exists with no host. Per-bucket Σh² and
   max|h| across x; needs an SSBO + low-rate readback. Answers "the wave dies at X".
3. The benchmark matrix — all solvers, matched stamp, matched grid, matched residual target.
4. **The money shot: iteration-count vs resolution, plotted.** `mna-next-steps.md` §4 calls
   this "the whole predictable-cost thesis, never plotted". It is the only artifact that turns
   "multigrid is O(cells)" from a claim into a measurement. Flat line for MG, rising for
   everything else, or the thesis is wrong.

**Definition of done for any solver:** appears in the dropdown, reports residual and ms/tick,
and reaches a target residual on the *same* stamp as the others at matched grid.
(The curve is Phase 2's job, not each solver's.)

---

## 7. Build sketches — the four remaining pieces

Enough detail to implement without re-deriving. Each ends with the observable that says it works.

### 7a. Multigrid deep — **a FORK, not a modification** (`MgDeepSolver`)

**Problem** (§3b): `mg_smooth` / `mg_residual` invert a scalar-β operator while every other
solver inverts the stamp's. And the pyramid is one level deep.

> **⚠️ DO NOT EDIT `MgvSolver.cs` OR `mg_smooth`/`mg_residual`.** This lands as a new solver
> next to the old one. `MgvSolver` works, is the only 2-level datapoint we have, and is what a
> broken deep pyramid gets A/B'd against. Both stay in the dropdown:
> `Multigrid (uniform β)` **and** `Multigrid (deep)`.
>
> New files: `scripts/lib/MgDeepSolver.cs`, `shaders/stamp/mgd_smooth.glslinc`,
> `shaders/stamp/mgd_residual.glslinc`.
> Reused **unchanged**: `mg_rhs` (already stamp-generic, §3b), `mg_restrict`, `mg_prolong`
> (pure geometry, take a `Pc16` size pair, no operator knowledge), `cg_dotbuf` (residual).

**Do this:**
1. **`mgd_smooth` / `mgd_residual` compile against the stamp**, exactly as `MgvSolver.cs:54`
   already does for `mg_rhs`. `pc.beta`/`pc.a`/`pc.leak` become `st_diag(c)` and
   `st_conductance(c, n)`. No `SmoothPc` — the fork never has one.
2. **Per-level push constant = the stamp's push constant with two fields rewritten:**
   `size = level size` (offset 0) and `beta_scale /= 4^L` (offset 8). Everything else rides
   through untouched — paddle, bed ramp, sponge, DC mode. `st_depth()` re-derives per-face
   conductance from the coarse `size` on its own (§3b lucky break). **No Galerkin coarsening.**
   Host-side: a `byte[]` clone + two `BitConverter.GetBytes` writes.
3. **Pyramid**: `Vector2I[] _lvl` halving until ≤ 8, with per-level texture arrays
   (`_x[]`, `_b[]`, `_r[]`, `_e[]`) and per-level uniform-set arrays. V-cycle = down-loop +
   up-loop over `_lvl`. This is the bulk of the work and it is mechanical.
4. `PassesPerStep` must **sum over levels**, not multiply by a constant, or the cost readout
   lies about MG specifically — and MG is the solver the whole thesis rests on.

**References:** `MgvSolver.cs:104-151` — the dispatch/barrier/ping-pong structure copies over
almost verbatim; that is the point of forking rather than rewriting. water-kit scene **40** for
the V-cycle shape.

**Watch for:** the coarsest level must actually *solve*, not smooth. At 8² a handful of Jacobi
sweeps is a solve; stop the pyramid at 64² and it is not — the V-cycle silently degrades into an
expensive smoother that still *looks* like it is converging.

**Observable — ⚠️ THE ONE I ORIGINALLY WROTE HERE IS INVALID. See §8b.** "Turn bathymetry up
and watch MgvSolver's residual diverge from CG's" cannot work: `MgvSolver` computes its residual
with `mg_residual`, i.e. **with the same wrong operator it solves**. At `bathy=1.0` it reports a
perfectly healthy 5.07e-08. A wrong operator is undetectable from its own residual. The valid
test is a **field comparison against CG**, which is what §8b actually ran.

### 7b. Block-dense Schwarz in LDS

**The idea:** stop iterating cell-by-cell. Cut the grid into tiles, **factor each tile's dense
submatrix in shared memory and solve it exactly**, then iterate only on the tile-to-tile
coupling (that outer iteration is additive Schwarz). Exact inside, relaxed between.

**Reference — this is a port, not a design:**
`../neptunely_standalone/js/audio/cmajor/lib/mna-solver.cmajor`, `mna::solve()` (line 285):
Gaussian elimination with partial pivoting over a flat row-major `float64[1024]`, forward
elimination storing L in place (`:340-344`), back substitution (`:354-360`). It is ~80 lines of
straight-line array arithmetic — **GLSL has all of it.** Change `float64` → `float`,
`.at(i)` → `[i]`, and `float64[1024] LU` → `shared float LU[1024]` (4 KB — trivial against a
32 KB LDS budget).

**Sizing:** `MAX_DIM = 32` ⇒ 32 unknowns per tile. On a 5-point stencil that is a small patch,
so start with the **variant that keeps the ports honest: one tile = one 32-cell line**, which
makes the dense LU a drop-in generalization of the ADI Thomas sweep (§3c) — same geometry,
general matrix instead of tridiagonal. Then widen to 2D tiles once the plumbing works.

**Build order:** assemble the tile's dense A from `st_diag`/`st_conductance` into LDS →
port `solve()` → write the interior back → outer Schwarz iteration over tiles with a one-cell
overlap. **Skip partial pivoting on the first pass**; our A is symmetric positive-definite and
diagonally dominant, so the pivot search (`:310-323`) is dead weight and it is the most
divergent part of the kernel. Add it back only if the residual misbehaves.

**Watch for:** the LU is *sequential*. One thread per tile wastes the workgroup (the §3c
occupancy trap again). Either accept it for v1 and measure, or cooperate across the workgroup on
the inner `j` loops (`:343-344`) from the start.

**Observable:** at matched grid, a Schwarz sweep should knock the residual down far harder than a
Jacobi sweep — that ratio is the whole point of the method.

### 7c. Spectral DST/DCT

**The idea:** a constant-coefficient Laplacian is *diagonal* in the sine basis. Transform,
divide by the eigenvalue, transform back — an **exact solve in two transforms**, no iteration.

**Eigenvalues** for our operator with Dirichlet edges:
`λ(p,q) = 1 + a + leak + 4β·(sin²(πp/2N) + sin²(πq/2N))`. Forward DST-II along x then z,
divide, DST-III back.

**Reference — `../water-kit/kit/waves/`, and it is working code, not a study.**
(An earlier note in `mna-next-steps.md` §2a called water-kit's FFT "broken". **That is stale.**
See §7e for the full inventory — there are four FFT implementations in that repo and three of
them work.) The one to lift is the `threejs-water-pro` port (`water-kit/PORT_PLAN.md` Phase 1):

| File | What it gives us |
|---|---|
| `kit/waves/shaders/fft_butterfly_h.glsl` | one radix-2 Cooley-Tukey stage along **x**, 3 components per dispatch, stage index + `butterflyHalf = 2^stage` via **push constant** |
| `kit/waves/shaders/fft_butterfly_v.glsl` | same along **y** — this is exactly the separable "transform x then z" structure DST needs |
| `kit/waves/shaders/time_evolve.glsl` | **bit-reversal fused into the write**, so stages run plain `0..numBits-1` with no separate reorder pass |
| `kit/waves/shaders/fft_normalize.glsl` | the 1/N² scaling pass |
| `kit/waves/shaders/spectrum_init.glsl` | how a spectrum texture gets built once and read every frame — our eigenvalue table `λ(p,q)` is the same shape |
| `kit/waves/wave_simulation.gd` | **the host harness**: numBits H stages + numBits V stages ping-ponging src↔dst, all 4 uniform sets per shader **pre-built at init and never mutated**, plus `probe_fft_stages()` — a per-stage readback that localizes a stage cliff |

**Three hard-won rules from that port, adopt them verbatim** (`PORT_PLAN.md` §"compute plumbing"):
1. **One binding index = one uniform, ever.** Scene 109 lost days to a set that declared
   `binding = 16` twice; Godot 4.6 resolved the collision to the wrong RID and a foam
   `imageStore` silently corrupted the static butterfly table after frame 0.
2. **Per-dispatch scalars (stage index, dt) go in push constants**, never in a rebuilt set.
3. **Build the stage probe before the chain**, not after it breaks. The failure signature is
   "correct on frame 0, flat afterwards" and it is invisible without a per-stage readback.

DST-II/DST-III are a real FFT plus a symmetric extension and a twiddle — so the butterfly chain
above is reused, not reimplemented. The genuinely new part is the extension + the eigenvalue
divide, which is small.

**The honest caveat, stated up front:** this is exact *only* for constant coefficients, i.e.
`extra.z = 0` (uniform depth). With a real bed it is **wrong as a solver**. Its actual value is
as a **preconditioner** — spectral-solve the uniform-depth problem, use it as the `M⁻¹` inside
CG (§3a) — where being approximately right is exactly the job. Weakest fit of the four, and the
one to cut if something has to give. Build it to have the comparison point, label it
`Spectral (uniform depth)` in the dropdown, and do not pretend otherwise.

**Observable:** at `extra.z = 0` the residual should be flat in the iteration count (it is —
`iters` does nothing, by construction) and should degrade sharply once bathymetry is turned on.
**Measured: 1.03e-06 at bathy 0 → 2.11e-04 at bathy 1.** The designed failure, visible.
The absolute floor is *not* float epsilon — see §8c for why, and why that is a v1 property of
the direct summation rather than an error in the eigenvalues.

### 7d. nD stamp contract + tiled storage — **2D + 3D only. 4D is out of scope.**

**These are one job.** Both come down to deleting `const ivec2 offs[4]` (§3e) and restoring the
cmajor property that **adjacency is data** (§1a).

> **Scope call (user, this session): do not build for 4D yet.** The contract should not *forbid*
> it — `ST_NB` is just a number — but nothing gets designed, sized, or complicated for it.
> Concretely this deletes the one real blocker: `ST_OFFS` is a **compile-time `const` in the
> stamp**, not a push-constant field, so the 128 B cap (§5) is untouched at 2D/3D. Ship it as a
> `const`; revisit only if a 4D or irregular case actually shows up.

**Step 1 — the neighbour list.** The stamp declares its own connectivity; solve bodies loop over
it and never name a dimension:

```glsl
// in the stamp, not the solve body:
#define ST_NB 4
const ivec2 ST_OFFS[ST_NB] = ...;   // 3D: ST_NB 6, ivec3
```

Then `for (int k = 0; k < ST_NB; ++k)` in each of the eight bodies listed in §3e. This
**deletes** `shaders/stamp3d/solve_jacobi_3d.glslinc` and folds `GpuStampSolver3D` back into
`GpuStampSolver` — the 3D path stops being a fork.

**Step 2 — tiled storage.** Once neighbours are data, a *tile* is just a node set with some faces
crossing its boundary, and the grid stops needing to be dense. The 2D special case already exists
and works: `solve_rbgs_wrap.glslinc:28` does toroidal addressing `(c + offs[k] + N) % N` for
scene 50's camera-following window, with the wrap seam parked inside the absorbing sponge so the
stencil never propagates physics across it (`mna-next-steps.md` §2.5, detail 2 — O(edge)
rescroll, not O(area)).

Generalize that to `ST_NB` axes: a `tex_off` world-origin offset per axis, `mod` addressing,
re-init only the newly-exposed slab. **This is what makes 3D affordable** — a dense 256³ field is
64 M cells, so without tiling the neighbour-list rewrite buys a 3D solver that cannot run at any
size worth looking at.

**References:** `BoatMna.cs` + `solve_rbgs_wrap.glslinc` (the working 2D case);
`GpuStampSolver3D.cs` (what gets deleted); `mna-next-steps.md` §2.5 (sponge/seam/LOD rules,
already worked out); `mna-solver.cmajor` §1a (the contract being restored).

**Observable:** one `GpuStampSolver`, one `solve_jacobi.glslinc`, and the *same file* producing a
correct 2D tank and a correct 3D blob with only `ST_NB` / `ST_OFFS` differing — plus scene 50
still correct, since it is the thing most likely to break.

> **✅ SHIPPED, with one prediction wrong.** The shader half landed exactly as written: one
> `solve_jacobi.glslinc`, one `solve_rbgs.glslinc`, one of everything, `stamp3d/solve_jacobi_3d.glslinc`
> deleted. **The host half did not, and should not have.** This section expected
> `GpuStampSolver3D` to *fold back into* `GpuStampSolver`; what shipped keeps them as separate
> hosts sharing one shader set — and `MgDeepSolver3D` follows the same pattern. The reason is
> that a host owns image *types* (`image2D` vs `image3D`), *push-constant layouts* (`vec2 size`
> vs `vec4 size`), and *dispatch shape*, none of which are expressible as one class without a
> runtime branch in every method. **Dimension is a property of the shaders, not of the host.**
> Merging the hosts would have bought one class and cost the byte-identical 2D path §10 is
> verified against. See §10.

---

### 7e. FFT inventory in `../water-kit` — four implementations, which to use

Recorded because `mna-next-steps.md` §2a said "the FFT is broken in this repo", that is **stale**,
and it is about to send someone to the wrong file.

| # | Where | Origin | Status |
|---|---|---|---|
| **1** | **`kit/waves/shaders/`** — `spectrum_init`, `time_evolve`, `fft_butterfly_h`, `fft_butterfly_v`, `fft_normalize`, `compute_normals` + host `kit/waves/wave_simulation.gd` | **`threejs-water-pro`** 1:1 port from `../surfing-main` (`PORT_PLAN.md` Phase 1) | ✅ **working, and the one to use.** Full IFFT chain: numBits H + numBits V stages, bit-reversal fused into time-evolve, pre-built uniform sets, push-constant stage index, built-in per-stage probe. **This is the §7c reference.** |
| **2** | `assets/shaders/compute/` — `spectrum_compute`, `spectrum_modulate`, `fft_compute`, `fft_butterfly`, `fft_unpack` — scene **108** | **GodotOceanWaves**, Ethan Truong (2Retr0), MIT 2024 | ✅ works. Was the kit's reference FFT ocean before the port. Photoreal spectrum + free Jacobian foam. `PORT_PLAN.md` §2 marks it **off limits for the port** (no reuse, no reference) — that rule is about port fidelity, not correctness. |
| **3** | `shaders/fft_ocean_109/` — `SpectrumUpdate`, `ButterflyTexture`, `FFT`, `FFTWater.gdshader` — scene **109** | Robert Davis (rdgh0st), MIT 2023 | ✅ **fixed**, and the debug writeup is worth more than the code: `docs/109-fft-displacement-debug.md`. Verdict recorded there — "the *less-good* FFT", inferior to 108. Keep as vendor study. |
| **4** | `shaders/fft/` — `fft_spectrum`, `fft_butterfly`, `fft_assemble` — scene **107** | in-repo build, CPU-precomputed butterfly-factors texture (`LOGN×N` of twiddle + top/bottom index) | works. Different structure from #1: reads indices from a **precomputed table** instead of deriving them per stage. Simpler to reason about; worth knowing it exists if the derived-index version misbehaves. |

**The single most valuable artifact of the four is a bug report.**
`docs/109-fft-displacement-debug.md`: FFT correct on frame 0, flat every frame after. Cause — one
uniform set listed two uniforms both declaring `binding = 16`; Godot 4.6 resolved the collision to
the butterfly-table RID, so a foam `imageStore` overwrote the static twiddle table. Worked on
Godot 4.2, broke on 4.6. `PORT_PLAN.md` hardened that into a rule (**one binding index = one
uniform, ever**) *and* a build-order rule (write the stage probe **before** the chain). Both apply
directly to §7c, and neither is discoverable without a per-stage readback.

---

## 8. Measured while building §7a/b/c — read this before Phase 2

Four things turned up that would have silently corrupted the Phase 2 benchmark table. None of
them are about the new solvers; three are about **the instrument**.

All runs: scene 25, 256², `sweeps=24`, via the new `bathy=` CLI arg (`WaveTankMna._Ready`).

### 8a. ⚠️ CG's residual is a DIFFERENT QUANTITY from everyone else's

| Solver | reported residual, bathy 0 | reported residual, bathy 1 |
|---|---|---|
| Jacobi | 6.71e-08 | 6.70e-08 |
| CG | **7.63e-17** | **7.19e-17** |
| Multigrid (uniform β) | 5.08e-08 | 5.07e-08 |
| Multigrid (deep) | 5.49e-08 | 5.93e-08 |
| Schwarz | 1.33e-07 | 1.47e-07 |
| Spectral | 1.03e-06 | 2.11e-04 |

CG looks nine orders better than everything else. **It is not.** `GpuStampSolver.StepCg` reports
`sqrt(rsnew)` where `R` was maintained *recursively* (`R -= alpha·AP`, a saxpy) and never
recomputed. Every other solver reports `‖b − Ax‖` **recomputed from the field** via
`reduce_residual.glslinc`. The recursive residual is the textbook CG pitfall: it drifts away from
the true residual and keeps shrinking after the true one has stopped.

**Consequence for Phase 2: any table that ranks solvers by this column is meaningless as it
stands.** Fix before benchmarking — give CG a recomputed-residual measurement pass (it already
has `cg_residual.glslinc`, which does exactly this; it just is not used for the readout).
This is also a retro-correction: the "CG 1.9e-16 vs everyone else 1e-07" line from the earlier
session was comparing two different things.

### 8b. The wrong-operator test cannot be a residual test

At `bathy=1.0`, `Multigrid (uniform β)` reports **5.07e-08** — healthier-looking than the deep
version's 5.93e-08 — while inverting a uniform-depth stand-in for the real operator. Because
`mg_residual` uses that same stand-in, its residual is self-consistent and cannot reveal the
error. The §7a observable as originally written was wrong; this is the corrected one.

**Field comparison at `bathy=1.0` (4 s, `tools/shoot.tscn`) does show it:** CG and
`Multigrid (deep)` both render a smooth surface; `Multigrid (uniform β)` carries a fine-grained
ripple texture across the whole tank that the other two do not have. Consistent with the deep
fork inverting the same operator as CG.

**Caveat, stated because the screenshots do not state it:** the three runs delivered different
tick counts per frame (CG 10, Mgv 2, MgDeep 1), so they are not at matched sim time. The
*character* difference is the evidence; it is suggestive, not conclusive. A controlled version
(fixed tick budget, same wall-clock sim time, per-cell field diff against CG) is Phase 2 work.

### 8c. Every recomputed-residual solver bottoms out at ~5e-08, and that is float32

Jacobi, Schwarz and both multigrids all plateau in the 5e-08 – 1.5e-07 band and **stop improving
no matter how many sweeps**: MgDeep gives 9.13e-08 / 5.89e-08 / 5.94e-08 / 5.86e-08 at
sweeps = 12 / 24 / 60 / 120 — i.e. it reaches the floor in ~2 V-cycles and then flat-lines.
Jacobi is at 7.92e-08 by 6 sweeps. Schwarz is at 1.38e-07 by 6 sweeps.

That is not convergence stalling, it is the **float32 arithmetic floor** of computing
`st_rhs + Σ st_conductance·x − st_diag·x` in `r32f`. Two consequences:
- **Convergence comparisons are only meaningful ABOVE ~1e-07.** Below it, everything ties, and a
  table of tied numbers reads as "all solvers are equivalent" when it actually means "the
  instrument ran out of bits". The iteration-vs-resolution curve (§6 Phase 2) must be plotted to
  a residual *target above the floor*, or it will plot noise.
- Spectral's 1.03e-06 at bathy 0 is ~10× above the floor, so it is a real error, not the floor.
  Cause: `dct_1d` sums N terms serially per output, so error grows ~√N per pass over 4 passes.
  **The `kit/waves` butterfly port (§7c/§7e) would make it more ACCURATE as well as faster** —
  log₂N accumulation depth instead of N. That is a stronger argument for the port than speed was.

### 8d. Two limits, one pre-existing bug

- **`Schwarz` and `Spectral` both blow the GPU fence at 1024²** (`ERROR: timeout waiting for
  fence`, repeated). Both are O(N)-per-output-per-cell by construction in v1 — Schwarz does a
  32×32 dense LU per 8×4 tile, Spectral does a direct O(N) DCT sum. Fine at 256²; 512² untested.
  Not a correctness bug, but **picking 1024² with either selected will hang the frame**, so treat
  those dropdown entries as 256²-only until the LU cooperates better / the butterfly lands.
- **`MgvSolver.PassesPerStep` overcounts by ~10×.** It returns `iters * (Nu1+Nu2+NuCoarse)` =
  288 at iters 24, but the solver runs `vc = iters/12` = **2** V-cycles, i.e. ~28 dispatches.
  `MgDeepSolver` counts honestly (77 for a 6-level pyramid), so **the control currently looks 10×
  more expensive than it is** — which flatters every other solver in any cost comparison.
  NOT FIXED: §7a marks `MgvSolver` do-not-edit and that was a deliberate instruction. Flagging
  for a decision, because Phase 2 cannot use that column until it is resolved.

### 8e. What is verified

`dotnet build` clean (0 errors). All three new solvers compile their GLSL at runtime, appear in
the dropdown, run scene 25, and report a residual. `MgDeepSolver` builds the full pyramid
(logged: `6 levels: 256x256 -> 128x128 -> 64x64 -> 32x32 -> 16x16 -> 8x8`, and
`8 levels` at 1024²). `SchwarzSolver` logs `8x4 tiles (32 unknowns, 4096 B LDS)`.
`SpectralSolver` warns correctly and unprompted when the constant-coefficient precondition is
violated. **Not verified: scene 50** (still owed since §3a), and no Phase 2 profiling was run —
the numbers above are convergence checks, deliberately not a benchmark table.

---

## 9. The instrument, rebuilt — `SolverBench` on a local device

§3d called the instrument PARTIALLY BUILT and §8 warned its numbers were compromised. Both were
understatements: **every figure the old method produced was invalid**, and all five defects had
one cause — the benchmark ran inside the render loop of a live scene (scene 25, reading its
on-screen ms/frame).

| Defect | What it looked like |
|---|---|
| **Saturation** | ADI, Schwarz, CG and Spectral all reported *exactly* 7.5 fps / 133.3 ms at several grids. That is `MaxTicksPerFrame` clamping, not a measurement — every solver slower than the frame budget reported the same number. |
| **Contention** | One Jacobi config measured 90, 188, 211 and 231 fps depending on what else was running, because each data point booted its own Godot. |
| **Frame time ≠ solver time** | ms/tick was frame time ÷ ticks, and included render and UI. |
| **Boot cost** | ~15–20 s per data point for ~100 ms of actual work. |
| **Unnormalized residual** | `LastResidual` is ‖r‖₂ = √(Σr²), which grows as N for the same per-cell error, so comparing grids was invalid. |

**`RenderingServer.CreateLocalRenderingDevice()` fixes all five at once.** It returns a device
with explicit `Submit()`/`Sync()`, isolated from the frame loop. Every solver already takes `rd`
as its first constructor argument, so they run on it unchanged — no solver was modified to be
benchable. That buys a real per-step GPU cost with no timestamp API (Sync, start clock, enqueue
K steps, Submit+Sync, stop, ÷K), no render, no UI, no tick cap, and one process for a whole sweep.

Residual is reported as **RMS** (‖r‖ ÷ √cells) so grids are comparable; the L2 figure is kept
beside it because every earlier measurement in this ledger is in those units.

```bash
tools/godot-mono.sh --path . res://tools/bench.tscn -- \
    solvers=0,1,2,3,4,5,6,7,8 grids=256,512 sweeps=24 reps=40
```

### 9a. ⚠️ Two traps in the instrument itself

**The local device needs `Step`-Sync-`Step`-Sync to read a residual.** Every solver calls
`BufferGetData` *inside* `Step()`. That is correct on the global device, where the work is
already in flight — but on a local device nothing executes until `Submit()`/`Sync()`, so the
first measured step reads a buffer the GPU has not written yet: stale on a warm buffer, **exactly
`0.00e+00` on a cold one**. That is what produced the spurious `Schwarz 512² residual 0.00e+00`
in the first sweep. Stepping, syncing, then stepping again means the second readback observes the
first step's completed result. Both `RunOne` and `RunOne3D` go through one shared `Measure()`
precisely so this cannot be implemented once and forgotten in the other.

**ms/step is noisy; residual is exact.** At `reps=40`, 2D Jacobi @256² measured 0.3874 / 0.6034 /
0.4379 / 0.4614 ms across four identical invocations — **±35%** — while its residual came back
`7.033419e-06` every single time. Consequence: **rank solvers on residual, treat a <1.5× ms gap
as noise**, and raise `reps` before believing one. This also makes residual the correct
*regression oracle* — a refactor that leaves it bit-identical is proven a no-op, which ms/step
could never show. §10 leans on this heavily.

### 9b. 2D baseline — 256², sweeps 24, reps 40

| Solver | dispatches | ms/step | residual RMS |
|---|---|---|---|
| Jacobi | 24 | 0.44 | 2.747429e-08 |
| Rbgs | 48 | 0.65 | 2.729618e-08 |
| Cg | 24 | 1.50 | 4.540783e-08 |
| Multigrid (uniform β) | 288 † | 0.43 | 2.025963e-08 |
| MultigridDeep | 77 | 1.31 | 2.048871e-08 |
| SpectralDCT | 6 | 0.62 | 3.209501e-07 |
| SpectralDST | 6 | 0.57 | 7.980398e-04 ‡ |

† still the §8d overcount — `MgvSolver.PassesPerStep` returns `iters·(Nu1+Nu2+NuCoarse)` for a
solver that runs 2 V-cycles. Unfixed, because §7a marks `MgvSolver` do-not-edit.
‡ DST is solving the wrong boundary problem for this stamp; it is in the table as a control.

**RBGS does not beat Jacobi at 256² — they tie.** This contradicts the working figure some of
this ledger was written against (RBGS 3.96e-08 vs Jacobi 2.15e-06, a 54× gap), and both are
right: **that gap is at 512², not 256².** The bench's own `Pc` sets β = 0.30·(n/256)², so β goes
0.30 → 1.20 and Jacobi's worst-mode factor 4β/(1+4β) goes 0.55 → 0.83 — the collapse §4 predicts,
arriving exactly on schedule. Quoting a relaxation residual without its grid is meaningless.

---

## 10. The 3D column

§7d made dimension a host argument for the *solve bodies*. This finishes the job: **every solver
kernel in the repo is now dimension-generic, and there is a real 3D benchmark column.** The
motivation is not tidiness — a shallow-water heightfield is single-valued in y and therefore
*cannot curl*, so overturning and barrelling waves need a genuine volume.

### 10a. What was still pinned to a plane, and why it was not obvious

§7d fixed the seven solve bodies. Six more kernels were still 2D, and they hid because they do
not look like solve bodies:

| Kernel | Why it was pinned |
|---|---|
| `cg_saxpybuf`, `cg_dot_partial`, `cg_dotbuf` | Standalone files carrying their **own** `#version`, `image2D`, `local_size` and `vec2 size` push constant — so they never saw the nd macros at all. |
| `mg_rhs` | Declared its own `image2D b0` and used raw `ivec2(gl_GlobalInvocationID.xy)`. |
| `mg_restrict` | 2×2 gather, hardcoded four `imageLoad`s. |
| `mg_prolong` | Bilinear, hardcoded four corners and two `mix`es. |

The fix is the one §7d already established — **the host supplies the declarations, the body uses
macros** — plus four new macros for the cases §7d did not cover:

```glsl
ST_TOTAL()        ST_FROM_LINEAR(t)     // elementwise kernels: no stencil, just cell count
ST_VEC  ST_EXTENT(v)  ST_CORNERS  ST_BIT_OFFSET(k)   // grid transfer: the 2^d corner set
```

`ST_BIT_OFFSET(k)` selects axis *i* with bit *i* of *k*, which gives restriction its gather set
and prolongation its interpolation stencil in any dimension.

### 10b. ⚠️ Prolongation must FOLD, not weight-sum — or the oracle dies

d-linear interpolation can be written two ways: a weighted sum of 2^d corner products, or a
corner gather followed by an axis-by-axis `mix` fold. They are algebraically identical. **Only
the fold is bit-identical to the 2D code it replaces**, because float addition is not
associative, and `mix(mix(a,b,tx), mix(d,e,tx), ty)` has a specific association that a weighted
sum does not reproduce. Folding axis 0 first pairs corners *k* and *k+1* (bit 0 = axis 0), which
is exactly the inner `mix` over x.

Writing it the natural way would have shifted the 2D multigrid residual in its last bits — not
enough to look like a bug, plenty to destroy the only regression oracle available (§9a).
Restriction had no such hazard: `0.25·(((a+b)+c)+d)` and an accumulate-then-÷4 loop agree
exactly, since `0.0+a == a` and both 4s are exact powers of two.

### 10c. The one genuinely dimension-dependent number — β per level

`MgDeepSolver.LevelPc` scales β by `shrink*shrink` = **/4 per level**. Two readings disagree
about 3D: β = dt²c²/dx² carries 1/dx² and dx doubles per level in *any* dimension (argues /4);
the Laplacian coarsens by 2^d (argues /8). **A wrong value here does not fail — it converges to
the wrong operator**, which is the worst failure mode in this ledger.

So it is a field (`MgDeepSolver3D.BetaCoarsenExp`), not a literal, and the bench decided it:

| MgDeep3D residual RMS | 48³ | 64³ |
|---|---|---|
| β /4 per level (exp 2) | 1.421561e-06 | 2.851423e-06 |
| **β /8 per level (exp 3)** | **1.290337e-06** | **1.867396e-06** |

**/8 wins, and the margin widens with depth** — 9% at 48³ (4 levels), 34% at 64³ (5 levels).
That widening is the tell: it is the coarse grids getting the correction right. The 1/dx²
argument is wrong because it reasons about a *coefficient* while the quantity that has to
coarsen correctly is the *operator*.

### 10d. Results — 3D, `stamp_blob_3d`, sweeps 24

> **These rows compare only to each other.** `stamp_blob_3d` is a different operator from the 2D
> `stamp_wave_tank` — central-source blob, no Crank-Nicolson term, no bathymetry, no sponge — so
> a 3D residual read against a 2D one is meaningless however well the units line up. The bench
> prints this warning at runtime whenever `grids3d=` is passed, because the units *do* line up
> and someone will otherwise read across.

**Residual RMS:**

| | 48³ | 64³ | 96³ | 128³ |
|---|---|---|---|---|
| Jacobi3D | 2.163e-06 | 4.018e-06 | 8.753e-04 | 4.975e-03 |
| Rbgs3D | 2.162e-06 | 2.179e-06 | 7.301e-06 | 5.315e-04 |
| Cg3D | 1.153e-06 | 1.728e-06 | 2.306e-06 | 4.061e-06 |
| **MgDeep3D** | **1.290e-06** | 1.867e-06 | **1.487e-06** | **2.284e-06** |

**ms/step** (48³/64³ at reps 40; 96³/128³ at reps 20, so looser — see §9a):

| | 48³ | 64³ | 96³ | 128³ |
|---|---|---|---|---|
| Jacobi3D | 0.60 | 0.70 | 1.49 | 3.07 |
| Rbgs3D | 0.77 | 1.31 | 3.04 | 6.62 |
| Cg3D | 1.88 | 2.40 | 4.97 | 10.21 |
| **MgDeep3D** | 1.24 | 1.25 | **1.76** | **3.07** |

**The cliff is between 64³ and 96³.** RBGS holds flat to 64³ — 2.162e-06 → 2.179e-06 — and then
goes 7.301e-06 → 5.315e-04. Jacobi goes over one grid earlier. This is §4's β collapse again,
one dimension up: β scales as n², and the 7-point stencil's worst-mode factor 6β/(1+6β) is
*worse* than 2D's 4β/(1+4β) at the same β.

**Multigrid's residual is flat across a 19× cell-count increase** (1.29e-06 → 2.28e-06), and its
cost rose only 2.5× — because the 3D pyramid is 1 + 1/8 + 1/64 + … = **8/7** of its fine grid,
against 4/3 in 2D. *Multigrid's overhead shrinks as dimension rises while its convergence
advantage does not.*

**The single cleanest number in the sweep: at 128³, MgDeep3D and Jacobi3D cost identically —
3.068 ms both — and MgDeep's residual is 2178× better.** Against CG at 128³ it is 1.8× better
residual at 30% of the cost.

This also answers the RBGS-vs-multigrid question §8 left open, in 3D at least: **they tie below
64³ and multigrid wins outright above it.** Below 64³ RBGS is the cheaper pick and MgDeep's
pyramid is pure overhead; the crossover, not the ranking, is the finding.

### 10e. What has no 3D form, and why — this is a closed question

Decided on the numbers in §9b, not on effort:

- **ADI** — second-worst in 2D (22 ms/step at 512²), one thread per line, and 3D needs *three*
  sweeps instead of two. It needs PCR before it deserves another dimension.
- **Schwarz** — the concrete reason: `TILE_N ≤ 32` forces an 8×4 tile to become 4×4×2. Boundary
  faces go 24 → 64 for the same 32 cells, so surface-to-volume nearly triples and more of the
  stencil leaks to the RHS. **The "exact inside" advantage shrinks precisely when you add the
  dimension**, and it is already the slowest solver in 2D at 50 ms/step. Worst candidate on the board.
- **Multigrid (uniform β)** — exists only as the deep-vs-shallow control in 2D. Duplicating a
  deliberately-approximate solver in 3D buys nothing.
- **Spectral DCT** — *maybe*, and only as ground truth. There is currently **no exact answer in
  3D**: every residual is self-reported against each solver's own operator, which is the §8b trap.
  An exact constant-coefficient solve would validate all four. But `dct_1d` is O(N)-per-output,
  already fatal at 1024²-2D, and 3D is three passes over N³. Only after the `kit/waves` butterfly
  port (§7c/§7e), or capped at ≤64³ purely as a reference.

### 10f. What is verified, and what is not

**Verified.** `dotnet build` clean. The 2D column is **bit-identical** before and after the whole
kernel lift — Jacobi `7.033419e-06`, Rbgs `6.987822e-06`, Cg `1.162441e-05`, Multigrid
`5.186465e-06`, every digit. That last row is the one that matters: **`MgvSolver` is the solver
that exercises all four lifted transfer kernels** (`mg_rhs`, `mg_restrict`, `mg_prolong`,
`cg_dotbuf`), so its being unchanged is the proof the lift was a no-op. Scene 08 still renders
(114 fps, solve 0.03 ms) on the 3-arg constructor and 2-arg `Step` — `GpuStampSolver3D` kept both,
plus `FieldRid`, so nothing downstream was touched.

**Not verified.** Scene 50, still owed since §3a. And no 3D solver has been run in a *scene* —
scene 08 uses Jacobi3D by default, so **`Rbgs3D`, `Cg3D` and all of `MgDeep3D` have only ever run
inside the bench.** The first scene to drive one will be the first real test.

**A trap left in place deliberately.** `Make3D`'s mode ids align with the 2D numbering, so `3` is
CG and **`2` is unmapped** (2D's 2 is ADI, which has no 3D form). An unmapped id used to print
`FAILED_INIT` with no distinguishing reason; it now prints `UNMAPPED_ID`. The alignment is worth
more than the gap costs, but the gap is real.
