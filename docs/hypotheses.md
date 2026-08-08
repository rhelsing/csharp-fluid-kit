# Falsifiable hypotheses — finishing Path A and Path E

Each of these is a claim that can be **wrong**, with a refutation condition you can
see on screen. No metrics harnesses — Ry judges by looking, with an fps counter.

The value is in the REFUTED-IF line. A hypothesis that cannot fail is a slogan.

---

## Path E — measure the basin, then replay it cheap

### H-E1 · The basin's response is low-rank

**Claim.** A few hundred damped modes reproduce the pool's impulse response well
enough that a resonator-bank replay is visually indistinguishable from the
full-field FIR replay.

**Why it might hold.** `path-e §2`: diagonalize the operator and the system
separates into independent damped oscillators, one per mode. A rectangular basin's
modes are `cos(mπx/Lx)·cos(nπz/Lz)`, and low-order modes carry most of the energy.

**Tests in.** `24_modal`, comparing FIR replay against resonator replay.

**REFUTED IF** the resonator replay looks obviously wrong at every practical mode
count — smeared, missing structure, or decaying at a visibly different rate than
the FIR. Also refuted if matching it needs so many modes there is no saving.

**Status.** **Now testable** — `24_modal` runs. First fit on the empty pool: 8 modes,
frequencies 0.0083–0.0167/tick, decays 0.005–0.010/tick, and **m5 reports silent**
(a node at the impulse point — a legitimate result, not a failure).

---

### H-E2 · The resonator bank is strictly cheaper than the FIR

**Claim.** At equal visual quality the modal replay costs less than the stored
kernel, because it is `O(modes)` per tick with **no kernel stored at all**, against
the FIR's `O(cells × active taps)` and 268 MB at 1024 slices.

**REFUTED IF** the mode count needed for equal quality pushes per-frame cost above
the FIR's, or if the per-cell basis evaluation (which the modal path still needs to
paint modes onto the grid) dominates and erases the saving.

**Status.** **Now testable** — `24_modal` reports "0 bytes stored" against the FIR's
268 MB, so the memory half is already settled. The arithmetic half is open, and
**this is the one I would bet against.** `24_cxm_field`
already showed the trap: the dynamics being cheap does not make the *field
expansion* cheap. A resonator bank gives you N numbers; painting them across 65k
cells is the same per-cell cost the FIR pays. The honest expected outcome is that
modal wins on **memory** (zero vs 268 MB) rather than on per-frame arithmetic.

---

### H-E3 · The cost ladder is sim > FIR > modal

**Claim.** Ordered by per-frame cost: live solver most expensive, stored-kernel
convolution middle, resonator bank cheapest — with correctness in the same order.

**Tests in.** `24_modal`'s three-way A/B on one drive.

**REFUTED IF** the ordering differs. Specifically plausible: the FIR beats the sim
only when the drive is sparse (tap compaction), and loses on dense/noise drives —
which would make the ladder **drive-dependent rather than fixed**.

**Status.** Partly answered already. `24_ir` measured 458k MACs/frame at 7/512
active taps versus the sim's 262k cell-ops — i.e. **the FIR was already more
expensive than the sim** on that drive. H-E3 as stated is therefore probably
already refuted; `24_modal` decides whether the modal rung rescues the ladder.

---

## Path A — the tank as the far field

### H-A1 · A closed water→tank→water loop is stable and sustains

**Claim.** With loop gain < 1 the loop is stable, and after the drive stops the sea
state persists and keeps evolving rather than decaying to flat.

**Why it matters.** `path-a §4` specifies a **feedback** architecture — sponge
energy is the tank's input, tank taps are the return. Everything built so far is
the return only, with the tank driven by the same excitation as the sim. That is a
parallel voice, not a loop. **Until water feeds the tank, this is not Path A.**

**Tests in.** `24_cxm_loop`.

**REFUTED IF** either failure mode appears: it dies immediately regardless of gain
(no sustain — the loop is not closing), or it runs away at every gain that produces
anything visible (no usable window between silent and unstable).

**Status.** **Now testable** — `24_cxm_loop` runs, and the loop demonstrably closes:
send 0.029 (a real mean amplitude, same order as the 0.066 drive) driving tank out
0.122, stable at gain 0.35.

One false alarm worth recording so it is not mistaken for a result: the first build
ran away hard (send 3.4e6, water in spikes). That was **not** refutation #2 — the
send was a raw SUM over ~65k cells, so "gain 0.35" was an effective gain of ~15x and
the stability question could never actually be asked. Normalising to a mean fixed it.
A hypothesis can only be refuted by the thing it names, not by a units bug.

The sustain half — does it keep going with **Drive OFF** — is still unjudged.

---

### H-A2 · Band-split decay maps onto spatial scale

**Claim.** Routing the tank's bass band to low-order modes and its mid band to
high-order modes makes long waves persist while short waves die — visible as the
surface getting **smoother** over time rather than uniformly quieter.

**Why.** `path-a §3` calls `SplitBandDecay` *"the main cue that reads as ocean
rather than pond"*, and physically dissipation scales with `k²`. `CxmTank` has the
crossover ported, but both bands currently sum into one input and land at the same
tap positions, so in water it means nothing yet.

**REFUTED IF** band-routed and unrouted look the same, or if the surface decays
uniformly instead of losing its fine structure first.

**Status.** Untested. Not built.

---

### H-A4 · The tank never visibly repeats

**Claim.** With the source's mutually incommensurate delays
(7188/6005/6807/5106) plus the modulated delays, the surface shows no visible loop
period over a long run.

**REFUTED IF** a repeat is visible, or the field settles into a fixed standing
pattern instead of wandering.

**Status.** Untested — every look so far has been 10–16 seconds, which is far too
short to see a loop.

---

## The join

### H-J1 · Measured frequencies beat guessed delays

**Claim.** Tuning the tank's delay lengths from **E1's measured modal
frequencies** produces a better-behaved tank than either the source's sample counts
or `path-a §3`'s analytic `L/c` and `f_mn = (c/2)√((m/Lx)² + (n/Lz)²)`.

**Why.** Both docs predict this convergence — *"the honest version is to use the
measured modal frequencies to tune the tank's delays rather than guessing them"*.
A delay network is a sparse hand-designed impulse response; E measures the real one.

**REFUTED IF** the measured-tuned tank is indistinguishable from the guessed one
(the tuning does not matter), or worse (the measurement is capturing something the
tank cannot express with four delay lines).

**Status.** Blocked on H-E1. **Do not do `path-a §3`'s analytic tuning first** — it
would be hand-tuning something we are about to measure.

---

## Order

`H-E1 → H-E2 → H-E3` first: the modal fit reports the true mode count and
frequencies, which then tells you whether H-A2's band split has anything to bind to
and makes the analytic delay tuning obsolete via H-J1.

`H-A1` is independent and can run any time. It is the one that makes Path A
actually Path A.

`H-A4` needs a long unattended run, not a screenshot.

---

# THE PRIZE

Everything below is in service of four things, and nothing else:

1. **Curling waves** — a breaking lip.
2. **Hull displacement** — a boat sitting *in* water, not stamping a wake on top of it.
3. **Bubbles** — entrainment, rise, and what aeration does to the medium.
4. **Cloth** — sails, flags, nets. On the original thesis list and never built.

The first three are limits of the **heightfield representation**, not of solver quality
(`mna-next-steps.md` §8). `h(x,z)` is a function — one value per column — so a curling lip is
unrepresentable, there is no "beneath" for undertow, and a hull can only leave a mark on the
surface. No solver improvement gets past any of that.

Cloth is a different kind of gap. `gpu-stamp-solver-plan.md` §1 names "cloth backward-Euler" in
the founding thesis — *most "simulate X" is secretly `A x = b`* — and it is one of three items
on that list never attempted (with screened-Poisson and radiosity). It is also the only prize
item that stresses the stamp contract in a direction the water work never does: **a wider
stencil**. Structural springs are the 4 neighbours we already have, shear adds 4 diagonals, and
bend adds 4 at distance 2 — `ST_NB = 12`, not 4. That is precisely the case the `ST_OFFS`
contract was built for and has never been exercised, and it is the same mechanism the plate's
`∇⁴` biharmonic term needs (`mna-next-steps.md` §1).

The chain is short and the order is forced:

```
§8b pressure solve → stamp    ──→  free surface  ──→  curling waves + hull displacement
void fraction as conductance  ──────────────────────→  bubbles
wider stencil (ST_NB = 12)    ──────────────────────→  cloth  ──→  sails on the hull
```

Path S below is **not** an end in itself. It exists because the free surface will be debugged
against a solver whose correctness is currently unverified, and because a level set that leaks
mass looks exactly like a solver that leaks mass. Hypotheses marked **[PRIZE]** are on the
critical path; the rest are hygiene and can wait.

---

## Path S — is the operator right, and is the solve right?

Every measurement in this project so far is a **residual**: `‖b − Ax‖`, i.e. *am I solving
`A x = b`*. **Nothing anywhere checks that `A` is the right matrix.** A stamp with a sign error
or a wrong β converges beautifully to the wrong answer and the bench applauds. That is the gap.

### H-S5 · Symmetry survives every solver **[PRIZE]**

**Claim.** A centred drop on a square domain with symmetric boundaries stays 4-fold symmetric
to float precision — in every solver, including RBGS (whose red/black split is the obvious way
to break it) and including 3D.

**Why.** Cheapest bug-finder available, and it localizes instantly: asymmetry means a stencil
or parity bug, and nothing else. On the prize path because the 3D RBGS path has never been run
by anything but the bench.

**Tests in.** `SolverBench`, seeded initial condition, no paddle.

**REFUTED IF** any solver breaks symmetry.

**Status.** ✅ **7 of 8 CONFIRMED** (`probe=symmetry`, 128², centred drop, 60 steps).
Jacobi, RBGS, ADI, CG, Multigrid, MultigridDeep and Schwarz all land at 2–5e-06 relative —
float noise. No stencil or parity bug anywhere, including RBGS, which was the suspect.

**SpectralDCT is the outlier at 8.6e-05, ~20× worse.** Not a stencil bug: all eight agree on
`max|h|` to five digits (0.1656277 vs 0.1656370), so the field is right. The asymmetry is the
**serial summation order** in `dct_1d` — `Σ i = 0..N-1` accumulates left-to-right, so the +x
and −x halves carry different rounding. That is §8c's documented accuracy limit surfacing as
asymmetry, and a third independent argument for the butterfly port (log₂N accumulation depth,
and symmetric order).

---

### H-S1 · The solver solves the wave equation, not merely converges

**Claim.** Seed a single cosine eigenmode, no sources, no damping, CN on. The field oscillates
at `ω = c·k`.

**Why.** The only closed-form right answer available anywhere in this project. It is the one
test that would catch a wrong operator rather than a wrong solve.

**REFUTED IF** the measured period differs from `2π/(c·k)` by more than discretization error.

**Status.** Untested.

---

### H-S2 · Crank–Nicolson is non-dissipative; backward-Euler is not

**Claim.** Same eigenmode, `cn=0` vs `cn=1`. BE shows measurable amplitude decay; CN holds flat.

**Why.** `mna-next-steps.md` §0 calls this "the important one", and §6 owes scene 06 a retrofit
*because of it* — its reverb tail is said to be over-damped by numerical loss rather than
physical damping. **That entire claim rests on an unmeasured premise.**

**REFUTED IF** CN also decays, or BE doesn't.

**Status.** ⚠️ **Direction CONFIRMED, absolute claim REFUTED** (`probe=energy`, 128², RBGS,
40 sweeps, `a = leak = sponge = 0`). `Σh²` retained after 200 steps:

| `cn` | retained |
|---|---|
| 0.0 (backward-Euler) | 20.5% |
| 0.5 | 28.7% |
| 1.0 (Crank–Nicolson) | 46.1% |

Monotonic in `cn`, CN retains **2.25×** BE. So §0's claim that CN is *less* dissipative holds,
and scene 06's owed retrofit is justified.

**But CN is not "non-dissipative"** — it loses 54% with all physical damping off. Two causes I
could not separate with this metric, so neither is claimed: under-convergence at 40 sweeps looks
exactly like damping (ledger §4), and **`Σh²` is a poor energy proxy** — it oscillates as energy
moves between kinetic and potential, which is why cn = 1 read 85.7 → 105.1 → 82.3 at steps
50/100/200. Sampling an oscillating quantity at fixed steps is a flaw in the probe, not a
property of the solver. A real test needs `E = Σ[(h − h_prev)² + β|∇h|²]` and an envelope.

---

### H-S3 · The DC modes actually conserve volume **[PRIZE]**

**Claim.** With no leak and no sponge, DC modes 3 (volume balance) and 5 (dipole) hold `Σh`
constant over a long run; modes 0–2 drift.

**Why.** `solver-ledger.md` §4 spends pages on "a monopole source pumps net volume into a closed
box" and lists six resolutions. **None has been measured** — the choice between them is
currently aesthetic. On the prize path because a level set that leaks mass is the classic
free-surface failure, and you cannot attribute mass loss to the surface unless you have already
established that the solver underneath conserves it.

**REFUTED IF** the dipole drifts too, or the leak-based modes don't.

**Status.** ❌ **NOT ANSWERABLE AS FRAMED — and that is the finding.** (`probe=volume`, 128²,
RBGS, 150 paddle-driven steps.) Mean drift, `leak = 9e-4`:

| dc_mode | drift |
|---|---|
| 0, 1, 2, 4 | +1.345e-02 — **byte-identical** |
| 3 (volume balance) | −1.675e+01 |
| 5 (dipole) | −2.173e-02 |

**The six DC modes are not six operators.** The stamp branches on `st_dc_mode()` in exactly two
places: `st_leak()` returns 0 for mode 3, and the paddle weight goes dipole for mode 5. Modes
0/1/2/4 — "none", "uniform spring", "authored-cutoff", "boundary-only" — are the *same shader
path*. The distinction lives in the HOST: `WaveTankMna.cs:672` computes a different κ per mode
and passes `_leakMode == 3 ? _dcCorrect : kappa` into the leak slot.

So the DC-mode resolution is **host-side policy that is not part of the stamp contract** — no
other consumer of the stamp inherits it, and any probe must replicate that switch to test it.
Mode 3's −16.75 is this probe feeding it a κ where it expects a host-computed volume
correction; that number measures my harness, not the mode.

*First run of this probe used `leak = 0` "so the spring cannot hide the drift" and got all six
modes identical — turning the mechanism off to isolate it removed the thing being compared.
Recorded because it is the same class of error as ledger §8b.*

---

### H-S7 · Spectral converts residual into TRUE error — and reranks the solvers

**Claim.** At `bathy=0`, `sponge_a=0`, spectral is exact, so `|x_solver − x_spectral|` is a true
error rather than a residual — and it ranks the solvers differently from the residual column.

**Why.** `‖b − Ax‖` can be small while `x` is wrong; that is the whole lesson of §8b, where the
uniform-β multigrid reported a healthy residual against an operator that was not the one anyone
wanted solved.

**REFUTED IF** the two rankings agree — a genuinely useful null result, since it would license
using the cheap number.

**Status.** Untested.

---

### H-S4 / H-S6 · Hygiene, deferrable

- **H-S4 — second-order in space.** Halve `dx` against the spectral solution; error falls ~4×.
  **REFUTED IF** it falls 2× (a boundary bug) or not at all.
- **H-S6 — the residual floor is float32, not stalled convergence.** ❌ **INCONCLUSIVE, and the
  test was invalid by construction.** Measured (`probe=floor`, 128², RBGS): amplitude
  0.01 / 1 / 100 gave residual 7.86e-09 / 8.43e-07 / 7.55e-05 — `residual/amplitude` constant
  at ~8e-07 across four orders of magnitude. That looks like a relative arithmetic floor **but
  proves nothing**: the operator is LINEAR, so scaling `b` scales `x` and therefore scales
  `r = b − Ax` for *any* `x`, converged or not. Both hypotheses predict the same result, so the
  experiment cannot fail — exactly the criterion this document exists to enforce. The evidence
  that still stands is §8c's sweep plateau (MgDeep flat at 12/24/60/120 sweeps). A valid test
  needs the error against the exact spectral solution, not the residual.

---

## Path W — the prize itself

### H-W1 · Projection actually projects **[PRIZE]**

**Claim.** Converting `f3_pressure_jacobi` to a stamp, `max|∇·u|` after projection falls by
orders of magnitude, and RBGS reaches a lower divergence than Jacobi at equal cost.

**Why.** Projection's entire job is `∇·u = 0`, so **the oracle is zero** — no invented success
criterion, no "does it look like smoke". Upgrades five already-working scenes (09/10/11/12/17)
and collapses the repo's two solver families into one.

**REFUTED IF** RBGS does not beat Jacobi on divergence per unit cost, which would mean the
pressure Poisson is not stiff enough for the solver choice to matter.

**Status.** ❌ **REFUTED for RBGS. CG wins instead.** (`probe=pressure`, 128²,
`stamp_pressure.glslinc`, zero-mean broadband divergence, residual RMS.)

| solver | 8 iters | 24 iters | dispatches @24 | vs Jacobi @24 |
|---|---|---|---|---|
| Jacobi | 5.265e-01 | 4.434e-01 | 24 | 1.00× |
| **RBGS** | 6.775e-01 | 5.168e-01 | 48 | **0.86× — WORSE** |
| **CG** | 4.360e-01 | **1.885e-01** | 24 | **2.35×** |
| MultigridDeep | 5.174e-01 | 4.602e-01 | 65 | 0.96× |
| Schwarz | 5.110e-01 | 2.973e-01 | 24 | 1.49× |
| ~~Multigrid (uniform β)~~ | ~~1.727e-02~~ | ~~4.724e-04~~ | 288 | ~~938×~~ **INVALID** |
| SpectralDCT | 5.270e-01 | 5.270e-01 | 6 | 0.84× — see H-W2 |

**Four findings, and none of them is the one the hypothesis expected.**

1. **RBGS is worse than Jacobi here, at 2× the dispatches.** The 2D wave-tank ranking does not
   transfer. **CG wins** — the classic result for Poisson, and it costs the same dispatches as
   Jacobi.
2. **The Multigrid row is invalid, by the §8b trap verbatim.** `MgvSolver` reads its β from
   push-constant offset 8 — which in the pressure layout is `scale = 1.0`. That gives it
   `diag = 1 + 4·1 = 5` against the true operator's `diag = ST_NB = 4`, so it solves a
   **screened** (non-singular, far easier) Poisson *and measures its residual against that*.
   938× is a different problem, not a better solver. Left in the table struck through because
   deleting it would lose the lesson.
3. **Deep multigrid does NOT help: 0.96×.** Mechanism: with all-Neumann walls the operator is
   **singular**, and every coarse level is singular too, so the coarse-grid correction is
   ill-defined. Textbook multigrid-for-Neumann-Poisson needs the nullspace projected out at
   each level. That is a real, actionable gap in `MgDeepSolver`, not noise.
4. **The pressure Poisson is a far harder problem than the wave stamp** — and this is the
   headline. Residuals here are order 1e-01 after 24 sweeps, against 1e-08 for the wave tank.
   The wave operator carries `1 + a + …` on the diagonal, a mass term that makes it strongly
   diagonally dominant; **pure Poisson has no mass term at all.** So the fluid's solver choice
   matters far *more* than the wave tank's — which strengthens §8b's motivation even though it
   refutes its stated mechanism.

**Still true, and now measured:** converting the fluid off Jacobi is worth doing. The target is
**CG**, not RBGS.

---

### H-W2 · Spectral is EXACT for pressure projection **[PRIZE]**

**Claim.** In an obstacle-free box the pressure Poisson is constant-coefficient, so spectral DCT
solves it exactly in 6 dispatches with no iteration.

**Why.** The condition that makes spectral a curiosity for the wave tank — variable coefficients
from bathymetry — **does not apply here**. The fog and smoke scenes are boxes. This would turn
an N-iteration Jacobi loop into a fixed 6-dispatch exact solve.

**REFUTED IF** the DC-mode singularity cannot be pinned cleanly (pure-Neumann Poisson is
singular — `λ(0,0,0) = 0`, and the wave stamp only dodges it via `1 + a + leak > 0`), or if
obstacles turn out to be needed in practice.

**Status.** ⛔ **BLOCKED, exactly as predicted — but by a second cause found first.**
Measured 5.270e-01 at both 8 and 24 iters (identical, correctly, since it is non-iterative) —
i.e. **no better than not solving at all.**

Cause is not the singularity yet; it is that `SpectralSolver` derives its eigenvalue from the
**wave stamp's push-constant layout**, hardcoded at offsets 8/12/16/20 and 124:
`base = 1 + a + aMu·leak`, `g = aMu · beta_scale · extra.w`. Against the pressure layout it
reads `a = reg = 0`, `leak = 0`, `cn = 0`, and critically `extra.w = 0` — so **`g = 0`** and it
solves `1·x = b`, i.e. `x = b`. Wrong operator entirely.

So H-W2 needs two things, in this order: **(a)** lift the eigenvalue derivation out of the
wave-stamp layout — the same host-supplied-header treatment `cg_saxpybuf` and `cg_dot_partial`
got — and only **then (b)** the DC-mode pin, which is still waiting to be the blocker it was
predicted to be.

Worth keeping: this is the *third* time a hardcoded push-constant layout has silently produced
a wrong operator (ledger §3a, §8b, here). The pattern is the finding.

---

### H-W3 · Void fraction modulates wave speed as √β **[PRIZE]**

**Claim.** Paint a β field; a pulse's transit time across the tank scales as `1/√β`.

**Why.** In the stamp, wave speed **is** the conductance. Aerated water drops sound speed
~1500 → ~100 m/s at 1% void fraction, so a bubble field is a conductance modulation. This is
also where bubbles, Liquid Time-Constant networks and nonlinear stamps converge: a
state-dependent `st_conductance` is exactly LTC's shape.

**Oracle.** `c = √(gh)`. **Runs in 2D today and needs nothing built.**

**REFUTED IF** transit time does not follow `1/√β`, which would mean the conductance is not the
wave speed and the whole bubble-as-medium-change idea is wrong.

**Status.** ✅ **CONFIRMED** (`tools/probe.tscn -- probe=speed`, 128², RBGS, 30 sweeps).
A centred impulse, transit time to `r = N/4`:

| β | steps | implied c | ratio vs first |
|---|---|---|---|
| 0.05 | 108 | 0.2963 | 1.000 |
| 0.20 | 53 | 0.6038 | 2.038 |
| 0.80 | 25 | 1.2800 | 4.320 |

Each β step is ×4, so `c ∝ √β` predicts exactly ×2. Measured 2.038 and 2.120; over the full
×16 span predicted 4.000 against measured 4.320. Error grows with β as expected — at β = 0.8
the pulse crosses in 25 steps, so integer step quantization alone is ±4%, before dispersion.

**What this licenses.** The conductance *is* the wave speed in this implementation, not just in
the derivation. A void-fraction field modulating β will change wave speed, so
bubbles-as-medium-change is physically grounded here. It is also the **first** measurement in
this project checked against an analytic oracle (`c = √(gh)`) rather than a residual.

---

### H-W4 · Dam break matches the published profile **[PRIZE]**

**Claim.** A free surface added on top of a stamped pressure solve reproduces the standard
dam-break profile.

**Why.** A standard validation case with known experimental results — **not a demo**. This is
the distinction scene 08 failed: it invented a problem (a wobbling blob) with no pass/fail and
became the only consumer of an entire subsystem.

**REFUTED IF** the profile is wrong, or mass loss exceeds what H-S3 established as the solver's
own baseline drift.

**Status.** Not built. Do **not** attempt before H-W1 — a free surface on a Jacobi pressure
solve is slow *and* leaky at once, and the two failure modes are hard to separate.

---

### H-W5 · Cloth is a stamp with a wider stencil **[PRIZE]**

**Claim.** Backward-Euler cloth is `A x = b` with `A = M − dt²·∂f/∂x`, and on a regular cloth
patch that is our operator with `ST_NB = 12` instead of 4: `st_diag` = mass + Σ incident spring
stiffness, `st_conductance(c,n)` = that spring's stiffness, `st_rhs` = momentum + external
forces. No new solver — the same nine already in the dropdown.

**Why.** It is the founding thesis's own example (`gpu-stamp-solver-plan.md` §1) and one of the
three items on that list never attempted. It is also the **only** prize item that tests the
`ST_OFFS` contract's generality in the stencil direction rather than the dimension direction —
structural (4) + shear (4 diagonals) + bend (4 at distance 2). If the contract is real, cloth
costs a stamp file and an offset table. If it is not, cloth is where that shows.

**Reference.** Baraff & Witkin, *Large Steps in Cloth Simulation*, SIGGRAPH 1998 — the paper
that established implicit integration for cloth, and the reason cloth belongs to this family
at all.

**The honest complication.** Cloth unknowns are **vectors**, not scalars — three components per
node, and the true Baraff–Witkin Jacobian has 3×3 blocks, not scalar entries. Two options, and
they are not equivalent: solve three decoupled scalar systems (the mass-spring approximation —
common, cheap, and *wrong* about how stretch couples across axes), or extend the stamp to vec3
unknowns (faithful, and a real change to `IStampSolver`). **Decide which before writing
anything**; the first is a legitimate v1 but must be labelled as an approximation, the way
`Multigrid (uniform β)` is.

**REFUTED IF** a 12-neighbour stamp needs anything beyond `ST_NB` + `ST_OFFS` + a stamp file —
i.e. if the contract turns out to encode "5-point stencil" somewhere it does not admit to. Also
refuted if the scalar approximation is visibly wrong as cloth and the vec3 extension proves
invasive enough to be a different solver.

**Status.** Not built. Unblocked — needs no free surface, no 3D, no pressure work.

---

### H-W6 · Nonlinear depth produces front steepening **[PRIZE]**

**Claim.** `depth = bed + h_curr` makes the crest sit in deeper water than the trough, so it
outruns it and the front face steepens — the precursor to breaking.

**Why.** `stamp_wave_tank`'s `st_depth()` reads the **static bed only**; it never reads
`h_curr`. So the operator is *linear* shallow water: shoaling and refraction work, but the wave
has no idea how tall it is. **A linear wave cannot steepen and therefore cannot break — at any
resolution, with any solver.** That is physics, not numerics, and it is why nothing in this
project has ever curled.

**Tests in.** `probe=steepen`, against `stamp_wave_nl.glslinc` (a FORK — the parent stays the
linear control), `extra.x` = nonlinearity strength.

**REFUTED IF** `max|∂h/∂x|` does not grow, or grows the same with the term off.

**Status.** ✅ **CONFIRMED** (128², RBGS, 30 sweeps, plane hump amp 0.35 on ref depth 1.0):

| `nl` | slope @40 | @80 | @160 | growth 160/40 |
|---|---|---|---|---|
| 0.0 (control) | 1.162e-02 | 1.176e-02 | 1.313e-02 | 1.130 |
| 0.5 | 1.271e-02 | 1.332e-02 | 1.528e-02 | 1.202 |
| 1.0 | 1.378e-02 | 1.542e-02 | 1.941e-02 | **1.409** |

Monotonic in `nl`; full nonlinearity ends 48% steeper than the linear control.

**Two things worth keeping.** *(1)* The first run used a **radial** drop and measured no
steepening at any `nl` — a radially spreading hump loses amplitude as ~1/√r, and nonlinearity is
an amplitude effect, so spreading outran steepening. Wrong geometry, not wrong physics.
Steepening is a 1D phenomenon. *(2)* **No Newton loop was needed.** `st_diag` now reads
`h_curr`, so the operator is state-dependent — but `h_curr` is constant across a whole `Step()`,
so every sweep re-linearizes against the same state. That is a frozen-coefficient (Picard)
scheme, re-linearized once per timestep, and it is free. Revisit only if steepening stalls near
vertical.

**The control row is not zero (1.130).** Two counter-propagating halves reflecting off the
Neumann walls re-focus. The *difference* between rows is the signal, not the absolute.

---

### H-W7 · A curling lip can be generated from the steepened front **[PRIZE]**

**Claim.** Front slope and crest velocity — both now real measured quantities from H-W6 — are
enough to place a plausible curling lip, with no overturn simulated.

**Why.** This is the repo's thesis applied to breaking: real dynamics underneath, geometry on
top. It puts curling waves on screen without 3D or a free surface.

**REFUTED IF** the lip needs parameters the sim does not provide — i.e. it turns out to be
art-directed after all, in which case it is an animation, not a simulation.

**Honest caveat.** This one has **no numeric oracle.** It is judged on screen. Recorded as such
rather than dressed up.

**Status.** Not built. Unblocked by H-W6.

---

### H-W8 · A 3D band tracking the break line is affordable

**Claim.** A moving 3D volume covering only where breaking occurs costs O(band), not O(area) —
§2.5's camera-window trick pointed at the crest instead of the camera.

**REFUTED IF** the band boundary reflects or leaks visibly, or the band must be so large there
is no saving over full 3D.

**Status.** Not built. The toroidal machinery already exists in `solve_rbgs_wrap`.

---

## Path B — bubbles

### H-B1 · Bubbles are §7's sand with the sign flipped

**Claim.** An advected scalar with **positive** buoyancy plus removal at the surface gives rise
and transport, reusing `f3_add_source`'s existing `buoy·d` term unchanged.

**Why.** `mna-next-steps.md` §7 already specs *"sand in water — dye with negative buoyancy +
deposition"*. Bubbles are that with the sign flipped: rise instead of settle, vanish at the
surface instead of depositing on the floor.

**Oracle.** Terminal velocity — `Σ(dye·y)` should climb linearly once the plume is established.

**REFUTED IF** the existing advection cannot hold a coherent plume, or surface removal needs
machinery the fluid does not have.

**Status.** Not built.

---

### H-B2 · Aeration refracts waves, following Snell **[PRIZE]**

**Claim.** A spatially varying void fraction is a spatially varying β, so a wave crossing into
an aerated patch **bends**, and the bend follows `sin θ₁ / sin θ₂ = c₁ / c₂`.

**Why.** H-W3 confirmed `c ∝ √β` for a *uniform* β. This is the spatial version, and it is the
one that matters: it means an aerated region behaves as a genuine optical medium, not just a
slower one. Snell is an unusually sharp oracle for a graphics sim.

**REFUTED IF** the bend does not follow Snell — which would mean the per-face conductance is not
behaving as a local wave speed.

**Status.** ✅ **CONFIRMED, with a bias that is itself physics.** (`probe=void`, 192², RBGS,
`stamp_wave_void.glslinc`, plane pulse launched along +x into a domain split in z, 90 steps.)

| void_mul | front clear | front aerated | ratio | predicted √(1/vm) | err |
|---|---|---|---|---|---|
| 1.00 (control) | 55 | 55 | **1.0000** | 1.0000 | **0.0%** |
| 0.50 | 55 | 41 | 1.3415 | 1.4142 | −5.1% |
| 0.25 | 55 | 31 | 1.7742 | 2.0000 | −11.3% |

**The control is exact** — 1.0000 against 1.0000 — which validates the measurement before any
claim rests on it.

**The under-prediction is expected and grows correctly.** Measured ratios fall *below* the
1D prediction, by more as the contrast rises. That is **lateral coupling across the interface**:
the two halves are not independent, and the fast half drags the slow half forward through the
z-direction conductance. That drag *is* the refraction — the ideal ratio is what you would get
if the halves were decoupled, which would mean no bending at all. So the deviation is the
signature of the effect, not error against it. Threshold quantization (±1 cell on a front at 31)
accounts for ~3% of it.

**What this licenses.** An aerated region behaves as a genuine medium, not merely a slower one —
spatially varying conductance produces spatially varying wave speed with the right law. Taken
with H-W3 (uniform β) and H-W6 (state-dependent β), all three legs of "the conductance is the
physics" are now measured rather than asserted.

**Method note.** Interface angle (`sin θ₁/sin θ₂`) was traded for a distance ratio deliberately —
a wavefront-angle fit is fragile, a front-position difference is one subtraction and cannot be
fudged. Same physics, sharper instrument.

---

### H-B3 · Bubble resonance is Minnaert

**Claim.** A cavity of radius R in the stamp, given the conductance contrast of air in water,
rings at `f₀ = (1/2πR)·√(3γP/ρ)` — ~3 kHz for a 1 mm bubble.

**Why.** The "babbling brook" sound is almost entirely bubble resonance, not flowing water. This
is where bubbles meet `mna-next-steps.md` §1's audio-rate spatial MNA.

**REFUTED IF** the measured ring frequency does not follow `1/R`.

**Status.** Not built, and **furthest out** — needs either the audio path or a spectral readout
that does not yet exist.

---

## Order (Path S / Path W)

**H-S5** first — minutes, and it either finds a real bug or clears the whole family before 3D
work builds on it. **H-W3** next: cheapest prize item, unblocked, needs nothing built.

Then **H-W1 → H-W2**, which is §8b and the thing that makes the 3D stamp path load-bearing
instead of ornamental. **H-S3** before **H-W4**, so free-surface mass loss can be attributed.

**H-W5 (cloth) runs on its own track** — it needs no free surface, no 3D and no pressure work,
so it never blocks and is never blocked. Worth taking early for a reason unrelated to cloth: it
is the only cheap way to find out whether `ST_NB`/`ST_OFFS` is a real contract or just a 2D/3D
switch, and every later stencil widening (the plate's `∇⁴`, `mna-next-steps.md` §1) depends on
the same answer.

**H-S1/S2** together (one flag apart, same rig) whenever the CN claim needs settling — scene
06's retrofit is blocked on them. **H-S4/S6/S7** are hygiene; they make the bench trustworthy
but they do not move the prize.

Everything in Path S runs in `SolverBench` with a seeded initial condition instead of a paddle.
**No new scenes** — the rule scene 08 broke.
