# Scene 50 — the foam tour

A hands-on guide to the three foam generations and their knobs: what each one is,
what to try, and what you should see. (Architecture doc: `scene50-foam.md`.)

## Setup for foam play (do this first)

1. Turn **Mouse click poke ON** — hand-splatting the water is the fastest way to make
   foam exactly where you're looking.
2. Foam is born from the sim's *curvature*, so it needs violence: a mouse poke, full
   throttle, or hard turns. Cruising gently makes almost none — that's the threshold
   doing its job, not a bug.
3. The debug pair (**MNA debug colormap** + gain) shows the ripple field itself if you
   ever want to see what the foam is reading.

## The three generations (additive — any combination)

**Foam v1 — flat white.** The frozen baseline: curvature → one buffer → white gate.
Born in the right places, then inert. Keep it as the control group: whenever v2/v3
tuning goes somewhere weird, flip v2 off / v1 on and you're back at known-good.

**Foam v2 — material + lifetimes.** Three populations in one buffer:
*whitecap* (bright, dies in ~a third of a second) decays *into lace* (the long-lived
trail) which decays *into milk* (a haze IN the water, not on it). Plus the material
change — foam goes matte, erodes at the edges, puffs the surface slightly.

**Foam v3 — motion.** Two movers, independent:
*swell drift* (foam rides the big Gerstner bands' orbital motion — breathes with the
sea) and the *swirl fluid* (a real incompressible 128² sim stirred by your stern —
the milk-in-coffee machine).

## The tour — run these in order

### 1 · v1 vs v2, same poke (30 s)
v1 ON, v2 OFF → poke once. A white ring blooms and evenly fades. Now v1 OFF, v2 ON →
poke again. Bright solid core, edges tearing into streaks as it thins, and a faint
milky glow lingering after the white is gone. That difference is the whole v2 thesis:
foam as a *material with a lifecycle*, not paint.

### 2 · Play the cascade like a mixer (2 min)
One poke at a time, watching what each slider does to the lifecycle:
- **v2 whitecap decay** ↓ 0.90 → the bright phase becomes a flashbulb. ↑ 0.99 → it
  lingers bright (storm-sea look).
- **v2 lace decay** — THE trail-length knob. 0.996 ≈ 4 s. Try **0.9995** → wakes
  persist for ~30 s and you can read your whole path across the sea.
- **v2 whitecap → lace** ↓ 0.2 → foam dies without leaving trails. ↑ 1.0 → everything
  the whitecap loses becomes trail.
- **v2 lace → milk** + **milk strength** — how much ghost the trail leaves *in* the
  water. Milk strength 2 = very visible aqua haze; 0 = off.

### 3 · Erosion — the edge character (1 min)
- **v2 erosion 0** → smooth solid blobs (looks like shaving cream — you'll hate it).
- **1.2** → aggressively shredded, almost sea-spray.
- **v2 erosion scale** small (0.1) = big continents tearing; large (1.0) = fine lace.
The trick under the hood: noise is *subtracted* only where foam is thin, so dense
cores stay solid while edges perforate — dying patches tear instead of ghosting.

### 4 · Material — why it stopped looking like paint (30 s)
- **v2 foam roughness** 0 → glossy white lacquer (the old wrongness, on demand).
  0.7 → matte, reads as bubbles. This one slider is most of the realism.
- **v2 puff** 0.2 → foam visibly sits ON the water and catches light at its edge.
- **v2 intensity** — overall opacity, last resort.

### 5 · Make the cruising wake foam (1 min)
At your current wake strength, foam only fires on hard maneuvers. To get a persistent
trail at cruise: **v2 threshold** down to ~0.004 (watch that ambient chop doesn't
start foaming — if the whole window whitens, you went too low), or **Foam gain** up.
Then set lace decay long and drive: a wake you can read for hundreds of metres.

### 6 · v3a — drift (30 s)
Lay a patch, then **v3 swell drift ×**: at 0 it's pinned to the world; at 1 it
breathes with the swell; at 3 it visibly sloshes and smears along the wave direction.
Watch a patch near a big crest — it should surge forward on the face and ease back in
the trough (that's real orbital motion, not a scroll).

### 7 · v3b — THE DONUT TEST (the payoff)
Lay foam down (a few pokes or a hard straight run), then carve tight circles through
it at speed. The lace must **wind** — stretched into filaments that curl into the
eddies your stern sheds. If it only smears straight:
- **Swirl jet** ↑ 5–6 (stronger propwash),
- **Swirl turn shed** ↑ 4+ (more rotation per steering),
- **Swirl dissipation** ↑ 0.999 (eddies live ~10× longer — "coffee that keeps
  spinning"),
- **Swirl carries foam ×** ↑ 2 (foam obeys the fluid more).
Then park and watch: the eddies keep stirring the trail after you stop. That's the
incompressible fluid doing what no scroll or pan can fake.

## Recipes (starting points, not endings)

- **Clean powerboat wake** — v2 only: thresh 0.004 · lace 0.999 · erosion 0.8 ·
  roughness 0.7 · milk 0.3 · drift 1 · swirl ON, jet 3, shed 2, dissip 0.997.
- **Storm chowder** — whitecap decay 0.99 · gain 6 · thresh 0.006 · erosion 0.5 ·
  milk 1.5 · drift 2. Everything churns.
- **Coffee-art mode** (park & stir) — mouse-poke a big patch, then throttle-blip in
  circles around it: jet 6 · shed 5 · dissip 0.9995 · carries 2.5 · lace 0.9995.
  Latte physics.

## Knob reference

| Knob | Default | What it does |
|---|---|---|
| Foam v1 / v2 toggles | on/on | additive layers — solo or stack |
| Foam gain | 3 | curvature → foam, feeds BOTH versions |
| v1 threshold | 0.015 | v1's dead-zone (conservative) |
| v1 decay / intensity | 0.96 / 0.59 | the baseline's two knobs |
| v2 threshold | 0.008 | v2's dead-zone — the "foam while cruising" lever |
| v2 whitecap decay | 0.94 | bright-phase lifetime (~0.3 s) |
| v2 lace decay | 0.996 | **trail length** (~4 s; 0.9995 ≈ 30 s) |
| v2 milk decay | 0.99 | haze lifetime |
| v2 whitecap→lace / lace→milk | 0.7 / 0.5 | how much death becomes the next layer |
| v2 intensity | 1.0 | overall opacity |
| v2 erosion / scale | 0.65 / 0.35 | edge tearing amount / pattern size |
| v2 foam roughness | 0.7 | matte-ness (the realism slider) |
| v2 puff | 0.06 | vertex lift under foam (m) |
| v2 milk strength | 0.5 | in-water haze visibility |
| v3 swell drift × | 1.0 | orbital sloshing (0 = pinned) |
| v3 swirl toggle | on | the milk-in-coffee fluid |
| Swirl carries foam × | 1.0 | how much foam obeys the fluid |
| Swirl jet / radius | 2.5 / 1.6 | propwash strength (m/s) / size (m) |
| Swirl turn shed | 2.0 | rotation flung per rad/s of steering |
| Swirl dissipation | 0.995 | eddy lifetime (→1.0 = spins forever) |
| Swirl pressure iters | 22 | solve quality — leave it |
