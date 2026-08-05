# science_boi → real-time graphics: the lift list

Synthesis of three parallel scout reports over `~/Projects/science_boi` (sim-core map ·
rendering audit · game-liftability ranking). All paths below are relative to that repo.

## What it is

An LLM-driven scientific hypothesis sandbox: **263 self-contained Rust binaries**
(92k LOC) + ~70 Python scripts, each a one-shot physics experiment with a markdown
paper. **No shared library** — lifting means extracting from one named file, not
importing. No git commits. Several experiments already run on Apple-Silicon GPU
(`mlx-rs`/Metal) as branch-free gather-stencil array ops — structurally identical to
compute shaders, which is why the porting story is good.

**It renders nothing in real time** (zero shaders; offline PNG only). It is a *field
generator* codebase. Our pipeline supplies exactly what it lacks (lighting, normals,
displacement, real-time loop); it supplies fields we don't have.

## The one primitive that unlocks six systems

**The 2×2 plaquette winding-number defect detector** — sum wrapped phase differences
around each 2×2 cell; nonzero winding = a topological defect, sign = chirality.
Appears independently in `chiral_spirals.rs:63` (spiral tips), `defect_soc.rs:38`
(wave-field singularities), `bkt_wolff.rs:29` (XY vortices), `topo_polarimetry.rs`
(polarization C-points), `thermo_shell.rs`, `qec_stim/nematic_sphere_qtensor.py:250`
(half-charge nematic defects, via doubled angle). **Written once as a fragment pass,
it also extracts vortex cores from our swirl fluid and phase singularities from our
Gerstner ocean.** Highest-leverage single shader in the whole exercise.

## The shortlist (first sprint)

1. **FitzHugh–Nagumo excitable media + spiral-tip tracking** —
   `src/bin/chiral_spirals.rs` (reference) / `chiral_spirals_mlx.rs` (GPU 1000²) /
   `cardiac_smooth_3d_mlx.rs` (GPU 192×192×64 **3D**). Two-channel ping-pong texture,
   5-point stencil, explicit — our CN scaffolding with a different right-hand side,
   no solver needed. Yields rotating spiral waves (living stone / alien flesh /
   corrupted magic) whose defect cores are *queryable spawn points*. The 3D variant is
   a real-time-viable `image3D` pass. Anisotropic diffusion masks = carveable "fiber
   direction" (muscle, wood grain, ley lines). Port: trivial–moderate.

2. **`defect_soc.rs` bolted onto our Gerstner ocean.** Their random wave field IS our
   band sum — we already compute it. Add the winding pass → physically-real chaos
   points for whitecaps/spray/rogue-wave events, temporally coherent. Their result:
   tiny smooth parameter drift causes power-law-sized reorganizations — free
   "mostly calm, occasionally dramatic" event pacing, no scripting. Port: the field
   is free; extraction pass trivial; frame-to-frame matching moderate.

3. **Foam coarsening (von Neumann law)** — `src/bin/foam_reactor.rs`. Smallest cell
   merges into its smallest neighbor, area-weighted attribute blending: **foam that
   ages** — fresh froth consolidating into fewer, larger bubbles. Direct structural
   upgrade to our v2 cascade (we have generation + lifetimes; this is *structure*).
   CPU-side sparse mesh events, cheap. Port: moderate.

## Tier A — strong follow-ups

- **Analytic vortex primitives** — `vortex_memory.rs` (2D Kirchhoff),
  `vortex_3d.rs` (closed-form Biot-Savart filament), `vortex_rings.rs` (64-segment
  rings), `vortex_flow_gate.rs` (potential flow + walls, zero solve). Divergence-free,
  art-directable, gridless velocity for particles (smoke/leaves/embers) — a *better
  curl noise*. Superpose onto our Stam field as authored persistent swirl. Trivial.
- **Gray-Scott with parameter-as-texture** — `turing_interface.rs`. Spatially varying
  kill rate = art-directable Turing patterns (stripes↔spots across one surface).
  Advect through our velocity field → flow-aligned striations. Trivial; needs baked
  ICs (patterns take ~10⁵ steps).
- **Sandpile/SOC family** — `chiral_sandpile.rs` (programmable topple direction =
  slope-biased collapse), `sandpile_topology.rs` (torus/Möbius/Klein wraps — a study
  of exactly our toroidal-window cheat, with measured statistical cost), plus the
  Clauset power-law MLE (`defect_soc.rs:95`) to *tune* our foam cascade to a target
  heavy tail instead of eyeballing. Trivial naive / moderate GPU-parallel.
- **Lattice Boltzmann D2Q9 + thermal Bénard convection** — `flow_switch.rs` (clean
  minimal), `benard_phononic.rs` (double-distribution + Guo-forcing Boussinesq
  buoyancy). A second fluid backend beside Stam: no pressure solve at all, density
  for free, complex walls via bounce-back. Convection = heat shimmer / lava lamps /
  boiling. Moderate.
- **XY model (BKT)** — `bkt_mlx.rs` (GPU checkerboard Metropolis). One temperature
  dial: combed laminar direction field ↔ boiling vortex chaos, with a real phase
  transition. Wind/fur/corruption fields. Trivial.
- **Skyrmions (LLG + DMI)** — `skyrmion_memory.rs`. Topologically-protected
  swirl-blobs that drift, repel, and can only die by annihilation. Sigils, shields,
  soul particles. Striking spin-texture→RGB visuals. Trivial–moderate.
- **Kuramoto sync** — `kuramoto_spectral.rs` (hierarchical sync plateaus from graph
  spectra — crowds/fireflies locking group-by-group), `phi_modelock.rs` (golden-ratio
  de-sync for ambient layers that never lockstep). Trivial on grids.
- **de Bruijn Penrose tilings + phason cascades** — `aperiodic_cam.rs` (30-line
  generator), `phason_soc.rs` (avalanche rearrangement). Analytic-from-position
  aperiodic tiling (infinite, seamless) that reconfigures in power-law bursts when
  struck. Moderate.
- **Gyroid / TPMS level set** — `tpms_waveguide.rs:37`. One-line implicit field →
  raymarch or isosurface: alien lattice / bone / coral. Its "impedance grading"
  continuous blend = a ready soft-edge material factor. Trivial for geometry.
- **Nematic combing on meshes** — `qec_stim/nematic_sphere_qtensor.py`. Bake-time
  relaxation that combs a headless direction field over any closed mesh with
  topologically-honest cowlicks (four +½ defects on a sphere, zero on a torus).
  Fur/scales/flow-maps/anisotropy tangents. Moderate, bake-time.

## Engineering patterns worth stealing regardless

- **Gather-index-table stencils** (`chiral_spirals_mlx.rs:205`,
  `cardiac_smooth_3d_mlx.rs:195`): boundary conditions baked into precomputed
  neighbor-index buffers → branch-free kernels, arbitrary masked/wrapped domains.
- **Conservation-form variable-coefficient Laplacian** (`acoustic_membrane_2d.rs:38`,
  `membrane_processor.rs:37`): the correct discretization for spatially varying
  stiffness/wave speed — exactly what a per-cell-c wave stamp needs; repo has both
  the explicit and the relaxation version of the same operator to diff against.
- **Mur first-order absorbing boundaries** (`tpms_waveguide.rs:145`) — an alternative
  to our sponge worth A/B-ing (absorbs by wave equation, not damping).
- **"Material that learns"** (`acoustic_membrane_2d.rs`) — our wave solver + ONE
  accumulator channel: tension permanently rises where strain exceeds yield. Armor
  that work-hardens, membranes that develop resonant channels. ~One day of work.
- **Their `mna_solver.rs`** is a literal MNA circuit stamper with backward-Euler
  companion models — the same stamp-then-solve lineage as our GPU stamp solvers.
  Shared vocabulary; nothing to lift, everything to compare.
- The README's **mlx decision table + failure notes** (ψ-ω with 10 SOR iters never
  converged; GPU loses to Rayon for QEC decoding) — honest benchmarks worth reading
  before any GPU-vs-CPU call.

## Explicitly NOT there (we're ahead — trade flows both ways)

No ocean/Gerstner/FFT water, no Stam fluids, no implicit grid stepping (no CN, CG,
multigrid), no SPH, no free surface, no noise libraries, no boids/L-systems, no
spatial acceleration structures (all neighbor search is O(N²)), no marching cubes,
no real-time anything. The `curl_field_*.rs` files are ML gradient regularization,
not curl noise. The `mr_foam` headline result was withdrawn by the author
(`revolutionary.md`) — machinery fine, claim dead.

## Where the FINDINGS go — applications of the papers themselves

Beyond lifting code: what the scientific results suggest when applied to game /
graphics / 3D-simulation problems, grouped by the underlying insight.

- **Self-organized criticality → event-pacing engines.** Slowly-driven systems tune
  to a critical point; event sizes go power-law. That's a *drama generator*, not a
  visual: long quiet then an avalanche — earthquakes, market crashes, crime waves,
  structural collapse, rogue waves — an "AI director" whose tension curve is
  physically emergent. Their Clauset MLE lets you TUNE a game's catastrophe
  distribution to a measured target instead of eyeballing.
- **Topological protection → conservation as mechanic.** Skyrmions/BKT vortices die
  only by annihilating an anti-partner; total charge is a law of the world. Gameplay:
  entities (souls, curses, storms) whose COUNT is invariant — economy/puzzle
  constraints that feel profound because they're mathematically real. Graphics:
  effects that can never pop, only drift and annihilate — maximal temporal coherence.
  Poincaré–Hopf as design law: a fur/flow tool that exposes the sphere's mandatory
  four cowlicks as first-class handles.
- **The cardiac line → level design by optimization.** Spiral waves self-sustain in
  excitable media, and their GA evolves geometry that KILLS them (defibrillation by
  design). Inverted for games: fire/plague/hordes/panic all propagate as excitable
  media, so the same tooling answers "shape this map so the fire can't loop forever"
  — or so it can. Fitness-function level design against emergent dynamics: a genuinely
  new authoring workflow.
- **Coarsening → believable maps and aging.** Von Neumann's law is universal: small
  cells die into big, junctions relax to 120°. Strategy-game territories evolved
  under coarsening produce the organic border shapes real history does — empires and
  soap bubbles obey the same junction math. Also: city parcels, mudcracks, drying paint.
- **Hysteresis + sharp transitions → mechanics with commitment.** Catenoid collapse,
  fluidic latches, percolation: one dial, a qualitative snap, and MEMORY — it won't
  return the way it came. Honest integrity meters (the bridge stands until the
  cluster truly disconnects — "one more cut" tension is real math), portals that pop
  at threshold and won't reform until far below, machines whose state lives IN the
  physics — discoverable and abusable with zero scripting.
- **Sync/desync → organic choreography and audio.** Kuramoto plateaus: groups lock
  before the whole locks — how fireflies/applause/factories should look and sound.
  Golden-ratio mode-locking: detune ambient loops by φ and they provably never fall
  into audible lockstep; Arnold tongues are a map of which rhythm ratios entrain.
- **Plasticity → objects with biographies.** Work-hardening lattices + the membrane
  that LEARNS the waves driven through it: armor stiffening where struck, floors
  developing resonance along habitual paths, and the PUF idea — every object's dents
  and patina unique-yet-deterministic from seed, zero storage.
- **Quasicrystals + hyperbolic growth → impossible architecture.** Aperiodic order
  evaluated analytically from position (no tiling repetition, ever); phason
  avalanches = crystal surfaces that violently reorganize when struck; {5,4}
  hyperbolic tilings grow exponentially with radius — dungeons with more interior
  than their boundary permits, from local rules.
- **Offline topology tools → content validation.** Persistent homology + linking
  integrals as QA: does the procedural cave system actually connect? How many
  independent loops has this dungeon? Are these ropes genuinely entangled or just
  visually crossed? Structure-checking for generated content is an unsolved tooling
  gap and the math is built.
- **THE META-FINDING (the real prize).** Nearly every paper is a version of
  *"simulate a field, then extract its structure and treat the structure as the
  signal."* We already simulate fields (ocean, ripples, swirl, foam) and throw their
  structure away as pixels. The winding/avalanche/spectral machinery proposes a new
  engine layer: **simulation telemetry as design material** — defects become spawn
  points, cascade statistics become pacing, phase coherence becomes audio,
  connectivity becomes stakes. Bigger than any single file.
- **Two negative results worth framing.** `self_wiring.rs`: current-directed
  "Hebbian" healing is WORSE than random (greedy reinforcement exploits, never
  explores) — a sharp warning for adaptive-difficulty systems that reinforce observed
  player behavior. And the withdrawn `mr_foam` headline: verify an emergent mechanic
  exists before designing a game around it — a discipline their repo models by
  documenting the withdrawal.

## Shot list (their prettiest outputs, for taste calibration)

`output/turing_universality.png` (stripes→spots interface) ·
`output/thermo_shell_mollweide.png` (spherical current sheets) ·
`output/qc_storage_01_tiling.png` (Penrose with defect markings) ·
`output/defect_soc_singularity_map.png` (speckle + singularities) ·
`qec_stim` pinwheel sphere renders (cortical-map labyrinths) ·
`output/cardiac_smooth_3d.html` (the three.js instanced viewer — our MultiMesh
port template) · `papers/cardiac_spiral.md` (the GA-evolved "ventricular trabecular"
branching-channel generator — procedural organic geometry with a fitness function).
