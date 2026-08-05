# Scene 17 — Foam & Boat Feel: a tour

A guided walk through the two systems built up on the reactive ocean
(`scenes/17_reactive_ocean.tscn` · `scripts/ReactiveOcean.cs` ·
`scripts/lib/BoatRider.cs` · `shaders/ameye_water_reactive.gdshader` ·
`shaders/stamp/foam_update.glslinc`). Every layer is an independent toggle with its
own knobs; every default preserves the tuned look/feel until dialed.

---

## Part 1 · The foam stack

Foam here is **one r32f buffer** riding the same 80u camera-follow window as the MNA
wave field. It scrolls with the boat (world-anchored), accumulates from sources,
decays every tick, and the water shader draws it. Everything below is a layer on
that one buffer — sources feed it in the compute kernel, looks shape it in the shader.

### 1a. The sim kernel (`foam_update.glslinc`) — three sources, one buffer

Runs right after the MNA solve each tick:

| Stage | What it does | Knobs | Off state |
|---|---|---|---|
| **drift** | semi-Lagrangian back-trace along the wave-propagation direction `−∂h/∂t·∇h/\|∇h\|²` → foam *rides* the waves instead of decaying in place | `Foam v3 · drift` toggle + gain | gain 0 = decay in place |
| **curvature source** | `gain · max(\|∇²h\| − thresh, 0)` — the folding/whitecap test on the reactive field | `Foam · gain / threshold` | gain 0 |
| **trail deposit** | gaussian foam stamped at the hull, amount ∝ speed → the tight speedboat track | `Foam v3 · trail` toggle + amount + radius | amount 0 |

then `foam = drifted·decay + curvature + deposit`, clamped.

**The two calibration insights (learned the hard way):**

1. **The dipole poke is a curvature-shaped source.** Scene 17's zero-net-volume
   dipole poke has a far hotter Laplacian than a plain gaussian, so scene 50's
   threshold (0.015) whites out here. Scene 17 runs `thresh 0.15 · gain 1.0`.
2. **On an undamped field, curvature foam = "everywhere ripples still ring."**
   With `damping 0 · leak 0.0015` the wake ripples live ~10s, continuously
   re-sourcing foam — you get a churned apron, not a trail. The threshold carves
   the fresh track out of it; the **trail deposit** is the source to use when you
   want a clean speedboat wake (its length is governed purely by decay).

### 1b. Foam v1 — the raw ramp (from scene 50)

```glsl
mfoam = smoothstep(0.08, 0.6, foam_buffer)   // that's it
```

No foam-texture masking — the cellular `Foam 5.png` pattern at full white read as a
**doily**, not foam (both sessions independently rediscovered this). The shape and
breakup come from the sim itself. `Foam · reactive wake foam` toggles the whole layer.

### 1c. Foam v2 — v1 + toggleable extras (`Foam v2 · enable`; off = exact v1)

| Extra | What it adds | Why it works |
|---|---|---|
| **matte** | roughness → ~0.9 where foam sits | foam is diffuse; without this it specular-shines like wet plastic |
| **edge normals** | 4-tap gradient of the foam buffer → raised lips | patches read as sitting *on* the water with thickness |
| **age bands** | foam value = age (decay!) → descending-value bands | old foam laces into streaks as it fades — lace from physics, not texture. The bands trace equal-age contours = the expanding wavefronts, which is why the trail reads as radiating streamers |
| **churn** | sampling UV perturbed by the live ripple slope | deterministic, self-animating (the slope field IS the moving wake) |
| **micro normal** | the ameye normal texture re-scoped inside the foam mask | churny micro-bump, off by default |
| **puff** | vertex lift where foam is dense | silhouette thickness, off by default |

### 1d. Macro layers (independent of the wake)

- **Whitecaps** — the Tessendorf folding test: the horizontal Jacobian determinant
  of the Gerstner sum (computed from the vertex tangent/binormal, passed as a
  varying). Where `J < fold start`, crests cap white. *Physical*: at the tuned
  steepness 0.042 the sea is too gentle to fold — raise steepness or the fold-start
  slider to bring caps in. Respects v2 matte. This is the far-field foam story the
  cascade/LOD plan assumes.
- **Shore band** — depth-thickness foam lap hugging the island beaches (reuses the
  tuned `foam_distance` for reach). No stage-4 carpet.
- **Bow spray** — 600 ballistic billboards thrown from the bow at speed: inherit
  boat velocity, kick up/outward, fall under gravity, die at the waterline.
  Rate · kick · size · alpha.

### 1e. Where scene 50 went meanwhile

Scene 50 grew a parallel, richer track worth porting later (`docs/scene50-foam.md`):
a multi-state foam kernel (`foam_update_v2` — wake/lace/spume with transfer rates)
and a **swirl layer** — a 128² incompressible Stam fluid with a propwash jet whose
pressure projection sheds real vortex pairs, advecting the foam (milk-in-coffee wakes).

---

## Part 2 · Boat feel

### 2a. Architecture — a kinematic hull with two spring solvers

`BoatRider` is NOT a rigidbody. Heading and throttle are direct (tutorial-style);
everything that *feels* physical is two small solvers:

- **Vertical**: a sum of named force terms — buoyancy spring (`KBuoy·depth`),
  viscous damp (`CLin`), quadratic slam (`CSlam`), wave launch kick (`KWave`),
  gravity. Soft/hard landings and bobbing emerge from the sum.
- **Angular**: the hull's up-axis chases a target (wave normal × `Conform`,
  probed `Lookahead` ahead of travel) through a spring (`AngStiffness`) with
  linear + quadratic damping.

The boat feels its own wake: buoyancy samples Gerstner **+ MNA** (`WakeInfluence`).

### 2b. The geometry quartet (each a station or angle along the hull)

| Knob | What it is | Tuned |
|---|---|---|
| **Trim** | bow-up pitch at top speed, eased through the angular spring | 0.18 rad |
| **Lift** | planing rise at top speed (added to the ride-height target) | 0.064 m |
| **Trim hinge** | *which end pays* for the tilt — stern hinge = transom planted, bow kicks; the hinge's height share is applied in the vertical solver so lift and hinge stay independent knobs | −2.5 (transom) |
| **Turn pivot** | the station the hull rotates about when steering — stern pivot = the nose sweeps onto the new heading while the tail holds its track | −2.45 (stern) |

### 2c. Bank & grip — and the two coupling bugs

- **Grip**: heading turns instantly; the *course* (velocity direction) chases it at
  `Grip`/s. Low = the nose leads while momentum carries straight — the drift.
- **Turn bank**: the target-up tilts around the hull's long axis with turn input,
  riding the existing spring so the lean eases in and settles.

**The bug story (worth remembering — it took two rounds):**

1. *Static*: banking around the **course** axis (slip-offset from the hull) leaks
   `≈ slip × bank` of trim into pitch — nose dives one way, rears inverted.
   Fix: bank around the **heading** axis (invariant under roll about itself).
2. *Dynamic*: the up-spring chases its target in **world** space while the hull
   yaws; in a steady turn the spring lags by ~ω·τ, and a sideways lean dragged
   behind a yawing frame acquires a fore-aft component — bank → pitch again,
   flipping with turn direction. Fix: co-rotate the spring state (`_up`, `_upVel`)
   with each yaw step, so hull-frame leans are lag-free in steady turns.

   Lesson: the static target math was *provably correct* and the boat still
   misbehaved — the bug lived in the chase, not the target.

### 2d. Speed-feel curves — the boat changes character with speed

Seven properties are now **pairs**: the existing slider is the value **@ top
speed**, a new `@rest` slider is the value at standstill, blended by
`pow(speedRatio, curve exp)`:

`Grip · Conform · Buoyancy k · Linear damp · Ang stiffness · Ang damp · Turn pivot`

(Trim, lift, and bank were already speed-scaled by construction.)

**Identity contract** (why the boat drives identically until dialed): every rest
value is seeded to the tuned top value — `lerp(x, x, s) = x` at any speed — the
`curves on` toggle short-circuits to the original path, and in the shared
`BoatRider` un-set rest values are a NaN sentinel meaning "same as base," so
scenes 15/16/50 are untouched.

**Suggested dial-in (the displacement → planing transition):**

1. `@rest Conform` up toward ~0.8 — at idle the hull *rides* the swell; on plane it
   keeps the tuned 0.26 and cuts through. The single biggest character knob.
2. `@rest Grip` down — loose and slewy at idle, hooks up with throttle.
3. `@rest Ang stiffness/damp` down — sloppy at rest, taut at speed.
4. `curve exp` ~2 — the transition bites late and reads as "coming onto plane."

### 2e. Current tuned feel (Copy-values, baked as defaults)

```
MaxSpeed 14.66 · MaxTurnSpeed 2.005 · HullOffset −0.099
KBuoy 42.9 · CLin 0.75 · CSlam 4.575 · KWave 3.725 · Gravity 8.1 (always)
Conform 0.26 · Lookahead 6.24 · AngStiffness 40.5 · AngDamp 1.35 · AngSlam 0.5
TurnBank 0.416 · Grip 10.445 · Trim 0.1802 · Lift 0.064
TrimPivot −2.5 · TurnPivot −2.45
```

---

*Verification culture for both systems: the rendered frame is ground truth (harness
shots at each step), state-change logs prove root causes before fixes (the stop-sink
mean-height A/B, the solid-red particle draw test), and every new capability defaults
to the identity so the tuned scene never shifts underfoot.*
