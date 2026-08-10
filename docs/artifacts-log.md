# Artifacts log — what building 250–256 actually taught us

Running findings log for the artifact series. The plans stay frozen
([`artifacts-250.md`](artifacts-250.md), [`artifacts-270.md`](artifacts-270.md)); this is
where results go — the things that were only learnable by building the scenes and putting
them in front of Ryan. Same spirit as [`solver-ledger.md`](solver-ledger.md) and
[`comparison-log.md`](comparison-log.md).

---

## §1 · The material-vs-noise law — **the central finding so far**

> A residual reads as a **material** only if it is (a) **low spatial frequency** and
> (b) **convects with the fluid**. A residual that is grid-frequency, or pinned to the
> lattice while the material slides past it, reads as **noise** — as a fault in the
> display rather than a property of the substance.

**How it was found.** Ryan's verdict *inverted* between two scenes with the same fluid,
the same palette and the same A/B structure:

| scene | artifact | verdict |
|---|---|---|
| 250b squish 3D | truncated Jacobi | *"whatever B is sucks horribly"* — the **artifact wins**, the accurate multigrid reference loses |
| 256 plaid | RBGS at 1–2 sweeps | *"we like B more"* — the **reference wins**, the artifact loses |

Nothing about the fluid changed. Only the *shape* of the leftover error did.

**Why.** Two mechanisms, and the second is the stronger one.

1. **Where the error lives in frequency.** Jacobi and Gauss-Seidel are *smoothers*: they
   annihilate high-frequency error quickly and low-frequency error slowly. So truncating
   them leaves error that is smooth and domain-scale — which reads as **bulk compliance**,
   a believable property of a soft substance. Plaid is the opposite extreme: the red-black
   sublattice difference is a **two-cell checkerboard, Nyquist**, the highest frequency the
   grid can represent. Nothing physical varies at exactly two cells, and the eye has a
   strong prior that grid-aligned Nyquist patterns are artifacts of the *medium* — screen
   door, moiré, dead pixels.
2. **What the error is attached to.** Squish's residual **convects**: it is carried inside
   the plume, deforms with it, belongs to the fluid. Plaid's checkerboard is **welded to
   the cell lattice** and the dye slides underneath it. Anything that does not move with
   the material reads as being *on the glass*, not *in the fluid*.

**This is not a tuning failure of 256.** That scene does its job: `pixel_exact` sampling at
256² makes its artifact maximally visible, and what it reveals is that this particular
artifact is ugly. A scene that successfully shows an artifact nobody wants is a result.

### §1a · What the law predicts

- **255 grain is in danger.** ADI leaves directional sweep-line striping — grid-aligned,
  pinned to the lattice, not convecting. The law says it fails the same way plaid does.
  **Build it as a falsification test**, not hoping it looks good: if grain reads as a
  material, the law is wrong, and that is worth more than a fourth pretty scene.
- **253 memory should work.** Pressure hysteresis is low-frequency and convects with the
  fluid. It is the cheap positive control for the law.
- **257 eq is the resolution — but only with the shelf facing the right way.** It shapes
  the residual's *spectrum* directly (`p̂(k) = W(k)·p̂_exact(k)`), so it can keep the
  low-frequency compliance that makes squish good **and** exclude the grid frequencies
  that make plaid bad. **This needs a HIGHPASS.** The base plan names `W = lowpass(knee,
  slope) × notch(...)`, and a lowpass is the mirror image: rigid at large scale,
  compressible at small — i.e. plaid's shape, the one that lost. Built as specified, 257
  could produce the material 256 was rejected for and not the one 250 was praised for.
  The shipped scenes take a **tilt** parameter (0 lowpass ↔ 1 highpass, defaulting to
  highpass) so the curve can face either way. Caught while writing the verification
  protocol, not by running it.
- **254 macro-honest is subsumed.** "Project levels ≥ ℓ only, fine scales stay
  compressible" IS the lowpass end of 257's shelf, with a continuous cutoff instead of a
  dyadic one. It is a preset of 257, the way 256 turned out to be a preset of 251.
- **252 crossfade is unblocked.** It needed an honest side to blend against and was
  waiting on the 2D multigrid hookup; the spectral solve is *exact*, which is strictly
  better. Neither remaining Block A scene depends on multigrid any more.

### §1b · 257 verified — the curve makes materials, and the reference really is exact

Checked live at 512². **Reference on ⇒ the artifact view goes essentially black**, which is
the test that matters: `W(k) = 1` is the exact projection, so a correct DCT-II/DCT-III pair
must leave no divergence. A broken transform gives a blown-up or frozen fluid, not a nearly
clean frame — so the pair inverts.

The *tiny* red that survives has two benign sources, distinguishable by where it sits:

- **a thin frame at the edges** — `fs_gradient_sub` zeroes velocity on the border ring
  AFTER the gradient subtract (no-slip walls), reintroducing divergence the projection
  never saw. Structural, and present in every scene in the block.
- **faint speckle across the field** — float32 accumulation depth. The direct transform
  sums N terms deep rather than log₂N; `dct_1d.glslinc` notes the same effect holds that
  solver ~10× above the float floor. The butterfly port would shrink it.

Ryan's verdict on the knee and tilt: *"it works... looks cool, different types of fluids,
hard to explain."* **That is the scene succeeding** — one curve producing several distinct
materials, none of which has a name yet. Naming them is the next step, and it is the
series' own method turned on itself: the artifacts were found by eye first and described
after. Capture them as panel PRESETS rather than as prose, and 269 mixer inherits the list.

It also changes what 252 crossfade should blend. Interpolating "exact ↔ some curve" is far
less interesting than interpolating between two materials that were actually chosen.

---

## §1c · Where this is going next — ocean/beach in a 2D vertical slice

Ryan's direction, injected after 260: **water + air + friction + shoaling in 2D — an ocean
next to a beach, in cross-section — with the accurate CURL of a breaking wave as the thing
that matters most.** 2D first; 3D follows once the curl is right.

What Block A already contributes, and it is more than it looks:

- **The geometry is already correct.** Every 2D scene here is a VERTICAL SLICE with gravity
  in-plane (§6, the side-view decision). A beach cross-section is exactly that frame — the
  orientation argument was settled for the artifact series and it happens to be the one
  shoaling needs.
- **Two-phase by density is already in the repo's vocabulary.** `f3_add_gravity` states it:
  *"water, oil and fog are one field holding rho, and (rho − rho_ambient)·g stratifies them
  without any per-phase logic."* Water/air is that with a large density ratio — no per-phase
  branching, one field. There is no 2D equivalent yet; `fs_add_milk`'s drift is the same
  shape of term with a small ratio.
- **The projection work transfers directly.** A breaking wave is where the free surface goes
  vertical and then overturns, which is exactly where a truncated projection stops being an
  aesthetic choice and starts being wrong. 257's exact spectral solve and the residual view
  are the instruments for telling those apart.

What is genuinely MISSING and cannot be dialled in from here:

1. **A free surface.** The Stam dye field is a passive scalar — it cannot overturn, and
   nothing in Block A tracks an interface. Curl needs a level set or VOF, or a large density
   ratio in a two-phase momentum formulation.
2. **Bathymetry.** Shoaling is a depth gradient doing work on the wave. `Bathymetry.cs` and
   `ShoreScenario.cs` exist on the wave-stamp side (scene 26's KP07 shoreline); the fluid
   side has flat no-slip walls and nothing else.
3. **Friction.** Bottom drag is what turns shoaling into breaking rather than just steepening.

The honest read: Block A built the *solver bench and the diagnostics*, not the water. The
next step is a two-phase 2D slice with a real interface — and the artifact series' value
there is that we can now tell "the solver is lying" apart from "the material is soft",
which on a breaking wave is the whole difficulty.

---

## §2 · 256 has no 3D scene, deliberately

A volume raymarch integrates 56–96 samples along every ray. That is a **low-pass filter**,
and it averages away exactly the Nyquist structure that `pixel_exact` had to be added to
preserve in 2D. In 3D the parity is `(x+y+z)&1`, so the checkerboard alternates every cell
along *any* ray direction — there is no viewing angle that saves it. A 256b would be a
scene structurally incapable of displaying its own artifact.

**This is the first place the base doc's "solver-level artifacts are dimension-blind" rule
breaks — and it breaks on _display_, not on physics.** The artifact is genuinely there in
the 3D field; it cannot reach the screen. Worth remembering for every later 3D lift: the
question is not only "does the solver lift" but "does the *display* preserve the artifact's
length scale".

A slice view (one axis-aligned plane, sampled pixel-exact — the base doc's "slice or top
surface" clause) would show it honestly and is the fallback if a fine-structure 3D artifact
ever needs one. 255 grain in 3D would need exactly this.

### §2a · The useful corollary — **plaid is free in 3D**

If the march erases the checkerboard, then the checkerboard costs nothing. **RBGS at K = 1
is effectively free in 3D**: near-minimum projection work, and the artifact it leaves is
invisible by the time it reaches the screen. 250b and 251b can run the cheap projection at
no visual price. The artifact that ruins 2D is a non-issue one dimension up.

---

## §3 · Performance — viscosity dominates, not the pressure solve

Proved live at 96×144×96, not argued from bandwidth arithmetic (the first estimate was
right, but an earlier claim in the same area was wrong — see §3a):

- viscosity sweeps **113 → 10** is what bought the 96³ tier. The grid did not have to move.
- the pressure solve is comparatively cheap: it is `r32f` (4 B/cell) against velocity
  diffusion's `rgba32f` (16 B/cell), and at fewer sweeps.

**First dial to drop whenever a 3D grid tier stops being interactive: viscosity iterations.**
Under-converged diffusion reads as *slightly less viscous*, which is a cheap price.

Memory is not the constraint on unified memory — the 3D ladder is 19 / 96 / 226 / 442 /
764 MB at 72 B/cell for 56 / 96 / 128 / 160 / 192. **128 and above ran too slow to work in**
regardless, so the cost is compute, not storage.

### §3a · Correction: viscosity sweeps are *not* grid-independent

An earlier note claimed they were, on the grounds that `(I − a∇²)` is diagonally dominant.
That holds only at **fixed a**. The similarity table scales `a = ν·dt` by `(N/N_ref)²`, and
Jacobi's rate `ρ = 4a/(1+4a)` climbs toward 1 as `a` grows — at 128³ that is `a ≈ 2.9,
ρ ≈ 0.92` against `0.69` at 56³, so matched convergence would want ~4.5× the sweeps.

Deliberately **not** auto-inflated: 134 sweeps of an `rgba32f` stencil over 3.1 M cells is
the same mistake as the 1600-sweep reference below. The fluid reads slightly less viscous
at high grids and that is the accepted trade.

---

## §4 · A reference you cannot afford to toggle is not a reference

250's reference was `K ∝ N²` Jacobi, which is **1600 sweeps at 1024²** — a freeze, not a
comparison. It is now hard-capped at 400, and the readout prints `(REFERENCE · capped)`
when the cap bites, because a silently-capped reference misrepresents the A/B: part of what
you would be seeing is the cap rather than the material.

The deeper point: **Jacobi cannot be a good reference at high N at any affordable cost.**
The fix is a solver whose iteration count does not scale with the grid.

| scene | reference | honest? |
|---|---|---|
| 250 / 250b(2D-style) | more Jacobi, capped | no — under-converged either way |
| 251 | SOR at ω\* = 2/(1+sin(π/N)), `K ∝ N` | better |
| 250b / 251b | **deep multigrid**, grid-independent cycle count | yes, and needs no cap |
| 257 eq | **W(k) = 1 is the exact projection** | exact, by construction |

This is the standing argument for the **2D multigrid hookup**: it retro-fixes 250's weak
reference and is the only thing blocking 252 crossfade and 254 macro-honest.

---

## §5 · The basis is the boundary condition (257)

257 could have been built on a complex FFT — the base plan even calls it "2D FFT". It is
built on a **cosine transform** instead, and that is not an optimisation.

Every pass in this fluid addresses neighbours with `clamp(c + off, 0, mx)`, i.e.
`x(-1) = x(0)`: **Neumann** walls, zero flux. The DCT diagonalises exactly that operator.
An FFT diagonalises the **periodic** one — so an FFT-based 257 would wrap the left wall
into the right one and show it as a seam at every boundary. `shaders/stamp/dct_1d.glslinc`
already argues this at length for the wave stamp; it applies unchanged here.

With the right basis the payoff is the strongest reference in the series: the DCT-II/DCT-III
pair is *exactly* invertible, so **`W(k) = 1` IS the true projection**, not an approximation
of it. Every other Block A reference is "converged enough" (§4). This one is right by
construction, which is what makes 257 the scene that can settle §1's tension by design
rather than by taste.

Cost is the deliberate v1 call `dct_1d` made: direct, `O(N)` per output, `O(N²)` per axis
pass. ~5 ms of reads at 512² — cheaper than the 400-sweep Jacobi reference it replaces — and
~43 ms at 1024², which is why 257's grid ladder stops at 512. The `O(N log N)` upgrade is a
port of a working butterfly chain, not research:
`../water-kit/kit/waves/shaders/fft_butterfly_{h,v}.glsl`.

---

## §6 · Smaller things worth not rediscovering

- **Artifact intensity ≈ 33** is the real scale for a divergence field in this palette.
  250 landed on 32.6 and 250b on 33.6 *independently*; the initial guess of 6–8 was an
  order out in both dimensions. Start new scenes near 33.
- **`_div` is the solve's right-hand side, not the residual.** It is computed before the
  solve and never refreshed, so displaying it shows what the projection was *handed*, not
  what truncation *left*. Both sims now have an opt-in extra divergence pass
  (`MeasureResidual` / `MeasureDivergence`) that re-runs it after the gradient subtract.
  Without that flag the artifact view barely responds to K.
- **A two-cell pattern through a bilinear sampler is flat grey.** Not faint — gone. Any
  cell-scaled artifact needs `pixel_exact` (texelFetch) sampling or it is invisible for
  reasons that have nothing to do with the physics.
- **Grid rebuilds race the display.** Freeing sim textures on the render thread while
  `_PhysicsProcess` keeps assigning their RIDs to a `Texture2Drd` throws *"Attempted to
  free invalid ID"* once per display texture. Clear the display RIDs *before* queueing the
  free, and skip ticking until the new sim exists.
- **`SetProcess(false)` does not hold.** Godot enables processing for any script that
  overrides `_Process`, so a node parked before `AddChild` silently wakes on tree entry.
  Gate rigs with an explicit flag (`FreeCam.Enabled`), never with the process flag.
- **The drift sign is a taste, not a physical constant.** 250 sits at `+0.3` and rises;
  250b ended at `−0.0125`, buoyancy essentially off, driven by pour injection and stir
  instead. The two scenes share the term, not the tuning.
- **The 2D scenes are a vertical slice, viewed from the side.** `fs_add_milk`'s drift term
  is in-plane buoyancy, so rendering it top-down (as scene 18 does, cup rim and all) makes
  gravity an arbitrary sideways bias. Standing the slice up costs nothing and is what makes
  250 → 250b a genuine dimensional lift rather than a reinterpretation.
