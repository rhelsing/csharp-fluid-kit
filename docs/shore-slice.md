# Shore slice — a wave that can actually curl (2D vertical cross-section)

Scenes **290+**. Built with the artifact series' machinery — `Scene250Base`, the palette,
the standard panel, reference A/B, residual in `#E23D6D` — and the stamp contract for the
physics. It is a separate doc because it does not share the series' contract: these scenes
are a **ladder toward one result**, not one isolated artifact each.

Numbering starts at 290 because [`artifacts-250.md`](artifacts-250.md) reserves 261–264 for
Block C and [`artifacts-270.md`](artifacts-270.md) owns 270–289.

## The goal, and why the existing shore work cannot reach it

**An ocean meeting a beach, in cross-section, with the wave curling and crashing.**

Every shore scene before this one is a **height field** — KP07 (26), the Gerstner+MNA
composites (50–52), the breaker experiments (27–31). All of them are depth-averaged and
single-valued, and a curling wave has **three water surfaces above one x**. `h(x)` can
steepen without limit and can never overturn. This is not a resolution problem or a tuning
problem; overturning is outside what those solvers can represent.

So the interface has to stop being a graph and become a **volumetric field**: ρ on a 2D
vertical slice, water and air as one scalar with a large ratio. That is the whole departure,
and everything in this ladder follows from it.

## What carried over from the artifact series

- **The geometry.** Every 2D artifact scene is already a vertical slice with gravity in-plane
  ([`artifacts-log.md`](artifacts-log.md) §6). A beach cross-section is that frame exactly.
- **The stamp contract.** Variable-density projection is a weighted Laplacian —
  `st_conductance = 1/ρ_face` — which is the same operator shape `stamp_wave_batty.glslinc`
  already uses for sub-grid solid fractions. **One face can carry both weights**
  (`open_fraction × 1/ρ`), which is how bathymetry arrives in 293 without a new solver.
- **The diagnostics.** Most of this work is asking *"is that overturn real, or is my
  projection lying?"* — and 250/257 built exactly the ability to tell an under-converged
  solve from a soft material, rendered in a colour already readable at a glance.
- **260 ghost's readout** is the acceptance test for 291: interface smearing is measured
  with `|∇ρ|`, the same probe.

## The ladder

| # | scene | the artifact | reference | status |
|---|---|---|---|---|
| 290 | `slice` | spurious hydrostatic currents | converged solve | ✅ verified |
| 291 | `sharp` | interface thickness in cells | MacCormack | — |
| 292 | `heavy` | where the ρ-ratio destabilises | low ratio | — |
| 293 | `shelf` | shoaling error vs **Green's law `H ∝ h^−¼`** | analytic | — |
| 294 | `curl` | the break itself, `γ = H/h ≈ 0.78` | — | — |

---

## §290 · slice — verified

Water and air as one ρ field; gravity uniform on every cell (the variable-density momentum
equation carries no density factor on `g` — the difference enters entirely through `1/ρ` on
the pressure gradient, so weighting `g` by ρ as well would double-count it); projection by
`fs_pressure_rho`, a red-black weighted Laplacian with `1/ρ_face` conductance.

**Result:** *"it looks like water, has some surface perturbs but mostly flat."* The
hydrostatic test passes — a flat waterline holds, and the perturbation at the surface is the
artifact this scene exists to render, not residue from a bug.

### Two bugs it cost, both worth not repeating

1. **`fs_gradient_rho` computed the wrong operator.** It had
   `∇·(w∇p)` — the projection's *left-hand side* — where the velocity correction needs
   `v −= (1/ρ)∇p`, a **first** difference. Subtracting a second difference from velocity
   does not misbehave gently: it threw the water into wisps, which is what the first
   screenshot showed. Easy to write when both operators live in one file and both involve
   `w` and neighbours.
2. **Push-constant padding, for the fourth time.** 40 bytes declared, 48 demanded, rejected
   at dispatch, invisible to the build. `tools/pc_audit.py` now prints every kernel's
   declared size against what the pipeline will demand.

### The known inconsistency — and why it is the artifact

On a **collocated** grid the cell-centred `1/ρ` in the gradient subtract is not the exact
adjoint of the face-weighted Laplacian in the projection. The pair is consistent to first
order and no better, and that mismatch is a genuine source of the spurious currents. It is
therefore *part of the artifact* rather than a defect hiding behind it.

The test that separates the two causes: **turn the reference on.** Truncation error
converges away; a discretisation inconsistency does not. If the surface perturbation
survives 400 sweeps it is the collocated mismatch, and the fix is a **staggered (MAC)
velocity** — the honest upgrade path, and a real piece of work rather than a tuning pass.

### §290b · what actually made scale work — three fixes, only one of which I guessed right

Two hypotheses were wrong before measurement, and both looked identical on screen: water
churning into froth. Adding a 1 Hz readback of `|v|max`, `|div|max`, ρ range and pressure at
three heights settled it in one run each. **Numbers or nothing** — that is the standing rule
for this series now, and the readout stays in the scene deliberately.

| hypothesis | verdict |
|---|---|
| projection never sees the walls, so hydrostatic pressure cannot exist | real bug, insufficient |
| hydrostatic mode is domain-scale and under-converged | real concern, not the cause |
| **cell-centred `1/ρ` in the gradient subtract** | **the detonator** |

**The detonator.** `fs_gradient_rho` weighted the pressure gradient by the CELL's `1/ρ`
instead of the FACE's. For an air cell one above the interface that is `1/ρ = 100` applied
to a pressure difference belonging to the *water* below, ~100× steeper than any air-scale
gradient — a spurious kick of ~−3.5 cells/tick where gravity contributed −0.07. Fifty times
the physics, on the interface, every frame. At a 100:1 density ratio this is not first-order
error; it is an explosion. Averaging the two one-sided FACE gradients pairs every pressure
difference with the density of the face it crosses, which is also what makes the operator
the adjoint of the projection rather than merely similar to it.

Measured effect: `|v|max` 6.5 × 10⁷ → 0.6.

**Also fixed en route, and still correct:**
- **Solid-wall divergence.** The clamped version tells the projection the border is OPEN, so
  uniform gravity yields a divergence-free field, the solve returns `p = const`, and nothing
  balances gravity. Necessary, not sufficient.
- **Flux form for that divergence.** With all-Neumann boundaries a solution only exists if
  `Σ div = 0`. Centred differences with zeros outside do not telescope to boundary fluxes;
  face-centred ones do, exactly.
- **Hydrostatic pressure seeded in closed form** at initialisation, so the domain-scale ramp
  is handed to the solver instead of discovered by it. `dt` belongs INSIDE the seeded
  pressure, because `fs_gradient_rho` subtracts `0.5·(1/ρ)·(p[U]−p[D])` and that must cancel
  `dt·g`. Off by that factor it reads as "nearly balanced" and drifts forever.

**Verified at scale:** 80 m × 20 m, ~0.078 m/cell, real gravity —
`p bot/mid/top = 11.39 / 2.19 / −0.002` against an analytic `11.18 / 2.22 / 0`.

### Small domains lie

The 6 m box "worked" before any of this. It was never in balance — gravity was simply 12×
weaker, so the free-fall and the incompatibility were both too slow to see over the seconds
anyone watched. **A small test domain can hide a formulation error entirely**, which is the
argument for 290b existing at all rather than tuning the small one until it looks right.

### §291 · interface sharpening — "it reads as heavy fog", quantified

Ryan's description of the working slice was *"still reads as dye in water… sort of does feel
like heavy fog"*, which is exactly right and turns out to be measurable. Nothing in the
pipeline opposes advective diffusion, so ρ drifts from *two phases* toward *a concentration*
and eventually stops describing water at all. MacCormack halves the rate; it does not change
the direction.

The readout now reports **interface thickness as the fraction of cells sitting mid-phase**
(0.15 < φ < 0.85), against the ideal of a one-cell-thick line. With `fs_rho_sharpen` at
strength 0.25 / power 2.4 it **holds at 0.8–1.1% against a 0.39% ideal** — 2–3 cells, stable
rather than growing. That number is the difference between water and fog.

Two other things carried most of the "reads as water" improvement:

- **Density ratio 100:1 → 500:1** (real is ~830:1). Anything past ~100:1 used to detonate on
  the cell-vs-face gradient bug; with that fixed a heavy ratio is affordable, and it is most
  of what makes water look like a liquid rather than a tint.
- **A SURFACE display mode.** Mapping ρ continuously to paper→ink is correct for dye and
  wrong for water — a field that is 90% water renders as mid grey. Thresholding at φ = 0.5
  shows where the boundary *is*. The threshold slider goes to 0 deliberately, showing the raw
  ρ and therefore the true smear: that toggle is the difference between fixing the problem
  and hiding it.

⚠ **The sharpening remap is NOT mass-conserving.** It buys sharpness by moving mass across
the interface, so long runs slowly gain or lose water depending on curvature. The
conservative form is Olsson–Kreiss compression, `∂φ/∂τ = ∇·(ε∇φ − φ(1−φ)n̂)`, which carries
the same steepening as a FLUX. That is the real 291; this is the dialable version that made
the problem visible. Also watch `|v|max`: it crept 0.17 → 1.04 where it previously plateaued
at 0.6, which is plausibly energy injected at the interface by the remap.

### §294 · the sweep that found the break — and the confound in the first one

Breadth-first across bed slope, with γ = H/h measured per frame rather than judged by eye.

**The first sweep was unreadable and I built the fault in.** Raising the slope while holding
the bed intercept drags the SHORELINE offshore too, so tanβ and surf-zone length varied
together and the runs could not be compared — at 1:5 the domain was mostly dry sand with the
sea squeezed into a corner. `BedY0` is now DERIVED from slope and a fixed shoreline fraction,
which makes slope the only variable.

| slope | ξ band | γ measured | reading |
|---|---|---|---|
| 1:20 | spilling | 0.55 | steepens, slumps |
| 1:10 | plunging edge | **0.85** | closest to the 0.78 breaking index |
| 1:5 | plunging | 1.59 | breaks too early, in water too shallow to resolve |

**1:10 lands at γ = 0.85 against a theoretical 0.78** — the first quantitative sign the break
is physical rather than decorative.

**Resolution was the other half, and it was decisive.** At 1024 (0.078 m/cell) the crest
steepens and slumps. At 2048 (0.039 m/cell) it throws: a curling front with detached spray.
An overturning lip is a thin, fast, small-scale structure — at 0.078 m/cell it is simply
below the grid, and no amount of solver work recovers a feature the mesh cannot hold.

⚠ **The pinned configuration is running hot.** At 2048, |v|max 72 and |div|max 5 mean the
Courant number is far above 1 even with substepping, so some of that spray is numerical
rather than physical. CFL is |v|·dt in CELLS, so halving the cell size doubles the Courant
number for the same physical speed — substeps now scale with the grid for that reason. The
picture is the best so far and the numbers say it is not yet trustworthy; both are true.

### What is still missing for curl

- **291** a sharp interface that survives advection. Without it everything downstream is fog.
- **293** bathymetry — shoaling is a depth gradient doing work on the wave.
- **294** bottom friction, which is what turns steepening into *breaking* rather than
  indefinite steepening.
