# Scene 51 — the knob tour

Every control in `51_singularity_sea`, in panel order, with what to reach for.
The scene is a stack of independent, toggleable layers over scene 50's boat + water;
any layer off = gone without residue. `Copy values` → paste → I bake.

## Render / performance (top)
- **Upscaler + Render scale** — fullscreen fps lever (sim cost is resolution-blind).
  Your preset: MetalFX spatial @ 0.4. fps bottom-left, always on.
- **MSAA / TAA / FXAA / Debanding** — standard AA; skip TAA when a temporal upscaler is on.

## Sea & camera
- **Wave amplitude / speed ×** — scales all six Gerstner bands. Amp up buries small
  reactive detail; your 0.62 keeps the wake readable.
- **Cam smooth / follow** — chase-cam feel only.

## Boat feel
- **Max speed/turn, Hull offset, Buoyancy k, Linear damp, Slam, Launch kick,
  Gravity(+always), Conform, Lookahead, Ang stiffness/damp/slam** — the two spring
  solvers. Your preset presses the hull into the sea (k 51.9, gravity-always).
- **Turn bank / Grip / Trim bow-up / lift / hinge / Turn pivot** — the carve rig:
  bank leans in, grip low = drifty, trim raises the nose with throttle (hinged at the
  stern), pivot swings the nose onto the heading. Bank/trim cross-coupling is fixed
  (two-part: heading-axis bank + co-rotating spring) — bow-up must hold through turns.
- **Buoyancy feels wake × / Bob smoothing** — hull rides swell + its own wake
  (async readback, cross-faded). 0 = ignore the reactive layer (clean A/B).

## Water look (scene 13's stages)
Stage toggles (depth fade / shore color / old shore-foam / normal maps / refraction /
toon light) + Roughness, Foam distance/crest, Normal map strength, Refraction,
Specular. The old texture shore-foam stays OFF — superseded by the v2 stack.

## Wake rig (the interactor)
- **Wake strength** — per-tick hull forcing, ×speed². No wake at low throttle is
  by design. **Radius** in sim px.
- **Bow poke** toggle + **angle / distance / strength / spread** — the twin V-split
  pokes (your preset: troughs, −0.68, 2.25 m apart). **Stern angle/distance/strength**
  — the tail poke (+0.92 at 4.2 m). **Side offset** — global lateral trim.
- **Mouse click poke** — off; on = hand-splat the sim.

## MNA solver (the ripple physics, inner 80 m window @ 512²)
- **Scheme BE 0 → 1 CN** — damped-soft ↔ lossless-ringing (yours: 0.375).
- **Wave speed c / sim dt / substeps** — propagation speed & temporal crispness.
- **Damping / rest leak** — losses (leak also drains DC mounds).
- **Sweeps** — solver convergence (13 is converged; readout shows residual).
- **Displacement / normal strength** — geometry vs lighting share of the ripples.
- **Poke radius/strength** — the mouse poke. **Edge fade** — melt into pure Gerstner.
- **Sponge width / damping** — the absorbing border (never poke inside it).

## Foam (v1 → v3 — additive generations, docs/scene50-foam-guide.md has the deep tour)
- **v1 / v2 toggles**, shared **gain**, per-version **thresholds** (v2's 0.008 fires
  at cruise), **v1 decay/intensity**.
- **v2 cascade**: whitecap/lace/milk decays + the two transfer shares — lace decay is
  THE trail-length knob (0.9995 ≈ 30 s wakes).
- **v2 look**: intensity, erosion + scale (edge tearing), foam roughness (matte),
  puff, milk strength, and the new pair —
  **v2 opacity (max over water)**: a true transparency cap; water breathes through.
  **v2 softness (alpha ramp)**: crisp cutout (0.05) ↔ soft gradient washes (1.5).
- **v3 swell drift ×** — foam rides the two big bands' orbital motion.

## The coffee (v3b swirl — the incompressible fluid)
- **v3 swirl toggle**, **carries foam ×** (how much foam obeys the fluid).
- **Jet / jet radius** — stern propwash (the projection rolls it into the vortex
  pair). **Turn shed** — rotation flung per rad/s of steering.
- **Dissipation** — eddy lifetime (→1.0 = coffee that keeps spinning). Note it's
  per-STEP: raising substeps decays slightly faster — compensate toward 1.0.
- **Pressure iters** — projection quality. YOUR FINDING: more Jacobi = decisively
  better filamentation (divergence kills the stretching). Now goes to 60.
- **Swirl resolution (cells)** — 64…512, LIVE (state wipes + re-stirs at the new
  granularity). 512 matches the ripple grid: 15.6 cm eddies.
- **Swirl substeps** — temporal fidelity: chain runs S× at dt/S; filaments stay thin.
- **Swirl time scale ×** — pure clock speed: slow-mo latte 0.3 ↔ churn 3.
- Max-coffee recipe: res 512 · substeps 3–4 · iters 40–60 · dissip 0.998 — and watch
  fps; iters are the cheapest thing to give back.

## Outer cascade (50.8 — reactive reach)
- **Outer cascade toggle** — a second coarse ring (256 over 224 m, backward-Euler,
  half rate) fed the same wake pokes, shown only where the inner window fades: wakes
  live on to ~110 m behind you. **Outer sweeps** (8 is plenty), **Outer
  displacement ×**. Crossfade only — no ring-to-ring physics (by design).

## Ambient life (the telemetry layers)
- **Singularity whitecaps (51)** + **gain / winding thresh / amp flatten** — the sea
  foams at its own band-cancellation points. **Amp flatten IS the sea-state dial**:
  0.5 lone caps · 0.75 scattered · 1.0 storm chowder.
- **Eddy-core foam (51)** + **gain / min speed** — foam collars on real rotation
  centres of the swirl fluid. Raise min speed if weak swirl foams too much.
- **Ambient vortices (51)** + **Γ / spacing / radius** — the infinite Kirchhoff
  lattice: micro-currents everywhere, even parked.
- **Foam bubbles — von Neumann (51.1)** + **seed thresh / life / merge cadence /
  wall width / wall bright / age dim** — the foam's cellular skeleton: froth
  consolidating into fewer, larger cells as it ages. Wall width ~0.5 = loud graphic
  cells; merge cadence fast = aggressive consolidation.

## Debug & demo
- **MNA debug colormap + gain** — the raw ripple field (ground truth).
- **Auto-cruise** — hands-free demo drive (arrows always override).
- **Mesh follows boat** — A/B for render-mesh artifacts.
- **Residual readout** — solver convergence + fps.

## Quick sanity tests
1. Park: singularity caps + vortex stirring + eddy foam keep the sea alive.
2. Full-throttle straight: wake + bubbles coarsening down the trail, outer cascade
   carrying it past the inner fade — no seam band at ~40 m.
3. Donuts through your own foam: the lace must WIND (raise jet/shed/dissip if not).
4. Flip any layer off: the scene must lose exactly that one thing.