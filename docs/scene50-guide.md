# Scene 50 — pilot's guide

The panel is ~70 knobs deep now. This is the map: what each group does, what *good*
looks like, and the quick tests that prove each system is alive.

```bash
tools/godot-mono.sh --path . res://scenes/50_boat_mna.tscn
```

Arrow keys drive (they never touch the UI). fps bottom-left. `Copy values` → paste to
Claude → gets baked as defaults.

## The mental model (one breath)

Composite **Gerstner** is the ocean everywhere (analytic, infinite, cheap). An 80 m
**CN-MNA sim window** rides the boat (512², toroidal clipmap) adding *reactive* ripples
the macro can't do — wakes that propagate and ring. **Foam** layers read the sim's
curvature and remember it (v1 flat · v2 material+lifetimes · v3 moves). A 128²
**swirl fluid** stirs the foam like milk in coffee. The **boat** rides the sum of
swell + its own wake. Everything melts into plain Gerstner at the window edge.

## Group by group

### Render / performance (top)
- **Upscaler + Render scale** — sim cost is resolution-independent; fullscreen fps is
  pure per-pixel work, so this is THE fullscreen lever. MetalFX spatial @ 0.4 = your
  preset. *Look for:* fps holding ~120 fullscreen; sharpness loss mostly in far detail.
- Physics interpolation is on project-wide — the boat looks smooth at ANY fps now.
  If motion ever stutters, check whether fps is dipping, not the boat code.

### Sea (Wave amplitude/speed ×)
Scales all 6 Gerstner bands together. *Look for:* amp ≈ 0.62 keeps the wake readable —
a big sea visually buries small reactive ripples.

### Boat feel
Two spring solvers (vertical + angular), every force term a slider:
- **Buoyancy k / Linear damp / Slam / Launch kick / Gravity** — the bob. High k + damp
  ≈ pressed into the sea (your 51.9/7.35); low = floaty cork.
- **Conform** — how much the hull tilts to match the wave normal (includes wake
  ripples!). 1.0 = glued to the surface shape.
- **Trim bow-up / lift / hinge** — throttle attitude. Hinge − = transom planted, bow
  kicks (your −1.725). *Look for:* nose easing up as you gun it, settling as you slow.
- **Turn bank / Turn pivot / Grip** — the carve. Pivot −2.5 = nose sweeps onto the new
  heading; grip low = drifty (momentum carves after the nose). *Look for:* bow-up
  HOLDING through a banked turn, symmetric left/right — that was a two-part spring bug
  (target axis + co-rotating spring state); if it ever regresses, that machinery is in
  `BoatRider.cs`.
- **Buoyancy feels wake × / Bob smoothing** — the hull riding its own wake (async
  readback, cross-faded). 0 = boat ignores the reactive layer entirely (good A/B for
  "is the wake feel real?").

### Water look (scene 13's stages)
Six toggles = the ameye shader stage by stage — flip them to see what each buys.
Foam (shore/crest) stays OFF (that's the old texture foam — superseded).
Roughness/refraction/normal-map sliders = the surface finish.

### Wake pokes (the interactor)
The hull writes into the sim as Gaussian pokes, all boat-local and steerable:
- **Wake strength** — per-tick forcing, scales with speed² (no wake while crawling —
  intentional). **Radius** in sim px (2.7 ≈ 40 cm).
- **Bow poke pair** — toggle + angle/distance/strength/spread. Negative strength digs
  troughs (your preset: −0.68 at 1.44 m, 2.25 m apart = the V split).
- **Stern poke** — angle/distance/strength (your +0.92 at 4.2 m = a rising tail).
- **Side offset** — global left/right trim if the wake ever looks off-axis.
- *Look for:* the wake carving from the actual hull line at full throttle; rings that
  keep propagating after you pass (that's CN ringing — the whole point).

### MNA solver (the physics)
- **Scheme BE 0 → 1 CN** — 0 = damped/soft (numerical loss eats short waves), 1 =
  lossless ringing. Your 0.375 is a middle voice.
- **Wave speed c / sim dt / substeps** — ripple propagation speed & crispness.
  Substeps shrink dispersion (ripples travel truer) at linear cost.
- **Damping / rest leak** — physical losses. Leak also drains any DC mound.
- **Sweeps** — solver convergence per tick (13 is plenty; residual readout confirms).
- **Displacement / normal strength** — how the field SHOWS (geometry vs lighting).
  Normals carry most of it; displacement is the silhouette.
- **Sponge width/damping** — the absorbing border. Never poke inside it (the scene
  already refuses); it eats waves so the window edge never reflects.
- **Edge fade** — reactive detail melting into plain Gerstner. *Look for:* no visible
  rectangle anywhere, ever.

### Foam (v1 → v3, ADDITIVE — stack or solo them)
Shared source: **gain** + per-version **threshold** on the sim's |curvature|.
Threshold must clear the ambient CN ringing or foam blankets the window —
v1 at 0.015 (conservative), v2 at 0.008 (fires while cruising).
- **v1 (flat white)** — the frozen baseline. decay + intensity.
- **v2 (material + cascade)** — whitecap→lace→milk lifetimes (the bubble-size
  spectrum), transfer shares, erosion (edges tear instead of fading), foam roughness
  (matte = the category change), puff, milk strength. *Look for:* fresh foam bright +
  solid-cored, dying foam perforating into streaks, a faint milky glow under churned
  water.
- **v3a swell drift ×** — foam rides the two big Gerstner bands' orbital velocity.
  *Look for:* patches breathing/sloshing with the swell, not pinned.
- **v3b swirl** — the milk-in-coffee fluid. Jet (propwash, throttle-scaled), turn
  shed (rotation per rad/s of steering), dissipation (eddy lifetime — push toward
  1.0 for coffee that keeps spinning), carries-foam ×, pressure iters (quality).

## Prove-it tests (60 seconds each)

1. **Window follow** — drive hard 200+ m in one direction. Water always under you,
   wake trail behind you the whole way, no seams sweeping past.
2. **Sponge** — MNA debug colormap ON, gain up, poke near the window edge (mouse
   poke toggle): rings must DIE at the border, never bounce back.
3. **CN ringing** — scheme to 1.0, damping 0: one poke should ring visibly for many
   seconds. Scheme to 0: same poke dies in ~1 s. The slider is the whole thesis.
4. **Foam cascade** — mouse-poke a patch: bright core → lacy edges over ~1 s →
   long-lived lace → milk haze after it. Toggle v2 off/on mid-decay to compare v1.
5. **THE DONUT TEST (swirl)** — lay foam down (hard run or mouse pokes), then carve
   tight circles through it. The lace must WIND — filaments stretching into spirals
   behind the stern. If it only smears straight, raise jet/turn shed or dissipation.
6. **Wake feel** — Buoyancy-feels-wake 0 vs 1 while crossing your own wake: at 1 the
   hull should kick over the ridge you made a moment ago.

## Debug tools
- **MNA debug colormap + gain** — the sim field raw (blue −, white 0, red +). The
  ground truth for "is the physics doing what I think."
- **Residual readout** (foam group header) — solver convergence + fps.
- **Mesh follows boat** — A/B for render-mesh artifacts (off = frozen 200 m plane).
- **Mouse click poke** — off by default; turn on to hand-splat the sim for tests.
- **Auto-cruise / auto-poke** — hands-free demo stimulus for screenshots.

## Gotchas worth remembering
- No wake at low throttle is by design (speed² forcing). Foam needs even more speed
  than ripples do (curvature threshold).
- Both foam versions ON doubles the white where they overlap — fine for A/B, dial
  intensities if running both for looks.
- Pressure iters is the only silently-expensive slider in the foam group (×2 lists
  per step) — 22 is converged at 128²; 40 buys nothing visible.
- The wrap seam and the sponge share the window border ON PURPOSE — if you ever see
  a hard line mid-window, Copy values and report; that combination has regressed.
