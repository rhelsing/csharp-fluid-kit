# Scene 50 — Foam plan (v1 → v4)

Foam generations are **additive layers, not exclusive versions** (Ryan's rule): each
has its own toggle, its own buffers, its own knobs. v1 off / v2 on / both on are all
legal states — every generation stays a live baseline to A/B the next one against.
The wave sim is untouched by all of this; foam only *reads* the height field.

The pattern behind every stage (and every foam system in games): one field says
**where the event happens** (MNA curvature), one says **where things go** (velocity),
one **remembers** (the foam population buffer), and the shader decides **what memory
looks like** (material + erosion). v1 has the first and third — born right, then inert.
Each version adds one of the missing pieces.

## v1 — baseline (built, frozen)

- **Source:** `|∇²h|` of the MNA field above an ambient-ringing threshold
  (`foam_update.glslinc`). Thresh must sit ABOVE the CN ringing floor or foam blankets.
- **Memory:** single R32F accumulate+decay buffer, toroidal like the height field.
- **Look:** flat white through a smoothstep gate. No texture (the cellular foam
  texture read as a doily — body-by-texture is banned), no material change, no motion.
- Tuned: gain 3 · decay 0.96 · thresh 0.015 · intensity 0.59. Frozen as the control.

## v2 — foam becomes a material (look + lifetimes, zero motion)

Own RGBA16F buffer pair + own kernel (`foam_update_v2.glslinc`); v1 untouched.

- **Lifetime cascade** — a 3-bin approximation of the real bubble-size spectrum
  (population dynamics: big bubbles die fast into small ones that linger):
  - `R = whitecap` — fast decay (~0.3 s), fed by the curvature source.
  - `G = lace` — slow decay (~4 s), fed by a share of the whitecap's decayed mass.
  - `B = subsurface` — the milky in-water tint, fed by decaying lace.
- **Material response** (the category change — foam is optically snow, not water):
  roughness pushed diffuse under foam, faint blue-gray tint at low density,
  small vertex height puff so light breaks over it.
- **Edge erosion** — subtract-then-threshold (NOT multiply): procedural 2-octave
  value noise scaled by (1 − density) subtracted before the gate. Dense foam stays
  solid; thin foam perforates; dying patches tear into streaks instead of ghosting.
- Knobs: per-channel decay ×3, transfer ×2, erosion scale/strength, foam roughness,
  puff height, subsurface tint strength, v2 intensity.
- Verify: mouse-poke rings + wake trail, A/B on the v1/v2 toggles, same wake.

## v3 — foam moves (v2 + transport)

Two motion sources, independently toggleable inside v3:

- **Swell drift:** semi-Lagrangian backtrace in the foam kernel by the ANALYTIC
  Gerstner orbital velocity (no new sim) — foam breathes and sloshes with the swell.
  Semi-Lagrangian = per cell, trace backward along velocity and *sample* (never
  scatter): unconditionally stable, and its slight blur flatters transported foam.
- **Swirl layer (milk-in-coffee):** a coarse 128² Stam fluid (scene 07's `FluidSim`,
  whose pressure projection is itself a stamp solve) driven by the hull — momentum
  along the track + a counter-rotating vortex pair at the stern — advecting the LACE
  channel. Incompressibility turns pushes into rotation; rotation stretches a passive
  tracer exponentially → filaments and spirals behind a turning boat. Optional bonus:
  the same field flow-maps the water normal UVs so un-foamed water streaks too.
- Knobs: drift strength, swirl injection, vortex-pair spacing, swirl dissipation.
- Verify: drive donuts — the lace must spiral.

## v4 — spray (sketch, unscheduled)

GPU particles born where the source spikes past a high threshold, ballistic,
depositing back into the whitecap channel on landing. Garnish after v3 proves out.

## Perf ledger

- v2: same dispatch count as v1; buffers R32F → RGBA16F (2 MB → 8 MB at 512²) — free.
- v3 drift: folded into the v2 kernel (one backtrace sample) — free.
- v3 swirl: one 128² Stam step/tick — negligible next to the 512² wave solve.
- All display work rides the existing water fragment shader (scales with render
  resolution, so the MetalFX 0.4 scale already discounts it).
