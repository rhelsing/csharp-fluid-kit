# Artifacts 270+ — telemetry as the instrument

**Extension of `artifacts-250.md`. The base plan (250–269, Blocks A/B/C) is frozen — nothing
here modifies it.** This is the *later phases to try*, seeded by `science-boi-lift.md`.

## The join — why these are one series and not two

250's thesis: **the solver's error is the material.** The lift list's meta-finding:
**a field's extracted structure is the signal.** Same move at two layers — the byproduct is
the product. 250 instruments the *residual*; 270 instruments the *readout*.

What makes this a genuine 250-extension and not a bolt-on: **a detector has an error term
too.** A 2×2 plaquette winding number is a discretized contour integral. A Clauset exponent
is an estimator with a fitting window. A coarsening law truncated mid-relaxation leaves
junction-angle error. Each has knobs, an accurate reference you can toggle to, and a
residual you can render in `#E23D6D`. That is the series contract, unchanged:

> one artifact per scene · knobs · **reference A/B** · **artifact view** in the signature color.

Everything else carries over verbatim: standard panel order, TIME scale, grid dropdown,
Copy-values baked as defaults, README row per scene, and the verification rule —
**agents never launch scenes**; build-clean + template-followed is the handoff, Ryan's eyes
are the render check.

## Already landed — reframe, do not re-port

The lift list's shortlist is partly in the repo already. A 270-scene *reuses these kernels*
and adds the artifact dial; it does not rebuild them.

| Lift item | Status | Where it lives |
|---|---|---|
| 2×2 winding-number defect detector | **landed ×3** | `shaders/stamp/singularity_mask.glslinc` (51, swell defects), `eddy_mask.glslinc` (51, fluid vortex cores), `fhn_tips.glslinc` (49, spiral tips) |
| FitzHugh–Nagumo excitable media + tip tracking | **landed** | scene 49 `FhnSpirals.cs` + `fhn_step/fhn_seed/fhn_tips.glslinc` + `fhn_plate.gdshader` |
| Analytic vortex primitives (Kirchhoff lattice) | **landed** | `shaders/stamp/vortex_inject.glslinc` (51) |
| defect_soc on a random wave field | **landed (field + extraction)** | 51 — the Gerstner band sum *is* their random field; the missing piece is the *statistics*, not the detector |
| Foam cascade | **partial** | `foam_update_v2.glslinc` = 3-bin lifetime cascade (whitecap→lace→subsurface, fixed exponential transfer shares). Von Neumann **coarsening** — area-driven, junction-topological — is the upgrade, not the cascade itself |
| 1D transform primitive (for spectral work) | present | `shaders/stamp/dct_1d.glslinc` — precedent for 257's 2D FFT |

## The routing rule — the one judgment call

Two destinations, and mixing them is the failure mode:

- **270-series** ⇐ the item has (a) a knobbed error term and (b) an accurate reference you
  can A/B against. The residual is renderable.
- **Main numbered series (53+)** ⇐ it's a beautiful new *field* with no residual to dial.

Skyrmions, BKT/XY, Kuramoto, Penrose/phason cascades, gyroid/TPMS, Gray-Scott
parameter-as-texture, hyperbolic tilings, persistent-homology QA — all excellent, none of
them artifact scenes. They go to the main series. Putting them in 270 dilutes the contract
that makes the series mean anything.

---

## Block D — detector artifacts (270–273)

*The extractor is a filter, and its error is a material.*

| # | Name | Artifact = material | Formula | Tuners |
|---|---|---|---|---|
| 270 | **flicker** | detector residual = temporal incoherence | winding sum over a p×p plaquette; wrapped-phase threshold; frame-to-frame match with hysteresis | p (2–8 cells), unwrap threshold, match radius, hysteresis frames |
| 271 | **census** | estimator error = pacing bias | Clauset power-law MLE (`defect_soc.rs:95`) over event sizes; x_min fitting window | x_min, sample count, drive rate |
| 272 | **relax** | truncated coarsening = frozen junctions | von Neumann area law + junction relaxation toward 120°, truncated at K | K sweeps, surface tension, min-cell threshold |
| 273 | **comb** | under-converged nematic field = wandering cowlicks | Q-tensor relaxation (doubled angle) truncated; Poincaré–Hopf fixes the defect **count** | sweeps, anchoring strength |

- **270** is the cheapest true formula swap in the whole extension — three proven winding
  kernels already exist. A/B = subpixel-refined / larger-loop reference; artifact view =
  spurious *and* missed defects in `#E23D6D`. Its knob p is cell-locked by construction →
  base doc's design response #2 (grid dropdown is an aesthetic knob, label it in the panel).
- **273** has a property worth the whole scene: the defect count is *topologically
  protected*, so under-convergence can move defects but can **never create or destroy
  them**. An artifact with a conservation law — the purest thing in either document.

## Block E — discretization character (274–277)

*Second backends whose **native** artifact differs from Stam's. 252 crossfade generalizes
from solver pairs to backend pairs.*

| # | Name | Artifact | Formula | Tuners |
|---|---|---|---|---|
| 274 | **mach** | intrinsic compressibility = squish from a different cause | LBM D2Q9, **no pressure solve**; error ∝ Ma². Ref: `flow_switch.rs`, `benard_phononic.rs` | τ, u_max, forcing |
| 275 | **seam** | wrong Laplacian at a stiffness gradient = spurious reflection | conservation-form variable-coefficient Laplacian vs naive averaging of c (`acoustic_membrane_2d.rs:38`) | contrast, gradient width, form toggle |
| 276 | **edge** | boundary residual = room tone | Mur 1st-order ABC (`tpms_waveguide.rs:145`) vs sponge vs hard wall | incidence angle, sponge width |
| 277 | **warp** | domain topology as material | gather-index-table stencils → torus / Möbius / Klein wraps (`sandpile_topology.rs`) with the measured statistical cost | topology dropdown, window size |

- **274** is the sharpest pairing in the extension: 250's squish is compressibility by
  *truncation choice*; LBM's is compressibility by *construction*. Same material, two
  origins, one A/B.
- **275/276** have free references — a huge domain (276) or the conservation form (275) —
  and the artifact view writes itself: subtract the incident wave, render what's left.
- **277** is a study of *our own* toroidal-window cheat (scene 50) with its cost measured
  rather than assumed. The gather-index-table pattern is also the engineering enabler for
  254 macro-honest and 256 plaid on masked domains.

## Block F — memory & threshold (278–279)

*253 memory is hysteresis in the **unknown**. This is hysteresis in the **coefficient**.*

| # | Name | Artifact | Formula | Tuners |
|---|---|---|---|---|
| 278 | **callus** | coefficient hysteresis = work-hardening | one accumulator channel on the wave stamp: c² (or tension) permanently rises where \|strain\| > yield | yield, hardening rate, **anneal**, cap |
| 279 | **latch** | threshold + memory = commitment | bistable/percolation snap; the return path differs from the outgoing one | threshold, hysteresis width, drive |

**278 is the `self_wiring.rs` negative result waiting to happen.** Current-directed "Hebbian"
healing was *worse than random* because greedy reinforcement exploits and never explores.
278's hardening loop is exactly that loop: it will carve ruts and never leave them. The
**anneal** knob is not a nicety, it's the fix — and the A/B (hardening off) is the honest
control that proves whether the material is interesting or merely stuck.

## Block G — telemetry as control (281–282, then 289)

*The meta-finding, made into scenes. 269 mixer already introduces patch cables; this is what
gets patched into them.*

| # | Name | What it is |
|---|---|---|
| 281 | **patch** | the residual becomes an input: defect field → dye/foam source, winding sign → force sign. One field, one cable, both directions toggleable |
| 282 | **pacing** | cascade statistics drive a global parameter — drive rate ↔ event size, tuned against 271's measured exponent, not eyeballed |
| **289** | **director** | the counterpart to 269 mixer: 269 is every knob live *by hand*; 289 is the same rig with the knobs driven by the fields' **own telemetry**. Built last of all, from tuned defaults |

**Gate on 282:** do not build it until 271 census shows a real power law *in our field*. That
is the `mr_foam` discipline — the headline result was withdrawn but the machinery was fine;
verify the emergent mechanic exists before designing around it.

---

## Grid & similarity — the extension rows

The base table still governs. Two additions, one of which is free verification:

| Quantity | scaling | N ×2 ⇒ |
|---|---|---|
| plaquette / detector radius p | cell-locked **by definition** — it *is* the discretization | unchanged (aesthetic knob) |
| junction relaxation sweeps (272) | diffusive relaxation, same as Jacobi | ×4 |
| accumulator yield strain (278) | strain is a gradient: `∂h/∂x` in cell units | ×0.5 |
| **SOC exponent (271/282)** | *should be scale-free* | **unchanged — and if it isn't, the criticality is fake** |
| **reflection coefficient (275/276)** | *should converge* | **→ 0 (or a constant); if it grows, the discretization is wrong, not the material** |

The last two rows are the good kind of honesty: the grid dropdown becomes a falsification
test, not just a resolution knob. Run it before believing either scene.

## Palette extension (additive — base table untouched)

| Telemetry kind | Color | Rule |
|---|---|---|
| magnitude (residual, cascade size, reflection) | `#E23D6D` artifact | as always |
| **signed / chiral** (winding sign, vortex chirality, defect charge) | `#35C4B5` plus / `#E0A458` minus | scene 49 already renders CW/CCW as two hues — this just formalizes it into the series palette |
| defect markers | artifact-color point, chirality hue by the row above | keeps "the residual is always `#E23D6D`" true for the *field*, while charge stays readable |

## Honest risk — what is NOT a formula swap

The base plan's promise ("after 250/250b, a scene is a formula swap into the template") does
**not** hold for four items. Each needs building *with* Ryan, the way 250/250b were, or
deferring:

1. **274 mach** — LBM is a whole second solver library (`scripts/lib/LbmSim.cs` +
   `shaders/lbm/lb_*.glslinc`), not a kernel expression. Biggest single build in the extension.
2. **271 census / 282 pacing** — need CPU-side event lists: GPU→CPU readback plus a CPU
   statistics pass. New plumbing, and readback stalls are a real-time concern worth a
   deliberate design (ring-buffered, N-frames-late is fine for pacing).
3. **272 relax** — sparse CPU-side mesh/junction events, not a grid kernel. Same category.
4. **273 comb** — bake-time, not real-time. Probably belongs in `tools/` as a field baker
   with a viewer scene, rather than a live 270-scene.

Spectral telemetry (phase coherence as a readout) rides on **257 eq's 2D FFT** — so 257 is a
prerequisite for that path, and `dct_1d.glslinc` is the existing transform precedent to copy
the layout from.

## Order of work (appended after the base plan's step 7)

8. **270 flicker** — three landed winding kernels make it the cheapest real scene here, and
   it proves the "detector residual" framing before anything expensive depends on it.
9. **278 callus** → **279 latch** — pure formula swaps into the wave stamp; Block F is the
   highest reward-per-line in the extension.
10. **275 seam** → **276 edge** → **277 warp** — free references, self-writing artifact views,
    and 277 pays back into 254/256.
11. **271 census** (plumbing) → gate → **272 relax**.
12. **274 mach** — with Ryan, as a template build, not a swap.
13. **281 patch** → **282 pacing** → **289 director**.

House rules unchanged: tuned Copy-values baked as defaults · every A/B reachable forever ·
README row per scene · prove by logs and rendered frames, never by guessing — and agents
never launch anything.
