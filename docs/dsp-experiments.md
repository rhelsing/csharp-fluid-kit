# DSP experiments — one isolated variable per scene

**Goal:** integrate audio DSP already built in
`../neptunely_standalone/js/audio/cmajor` into a 3D GPU harness, and find where
complex interactions come out of simple things.

**How these get judged:** by looking at them, with an fps counter. Not by metrics.
The build side's job is to isolate exactly one variable per scene, confirm the
scene launches and renders without errors, and stop there. Ry judges.

**Not in play:** the MNA stamp solver. Every scene runs the FULL SIM
(`WebgpuWaterSolver` — Wallace's explicit heightfield). Where MNA might earn its
way back in is the follow-up at the bottom, to be revisited *after* there is
something to look at.

---

## The frozen baseline

Isolation only means something if everything else is identical. Held constant
across every scene:

- scene 24's pool, ball, camera, cubemap, tiles (forked from `RaytracedPoolMna.cs`)
- grid 256², `WebgpuWaterSolver`, same `DropRadius`, same `DampingFor(256)`
- same drive: amplitude, interval, source position
- **raytrace OFF by default** — the caustics SubViewport is a second 1024² render
  every frame and the `ww_*` shaders re-trace per pixel. That cost lands on the
  fps being judged and visually confounds what is being looked at. Toggle stays.
- same readout format: `fps · <variable state> · <cost>`

### One trap already hit

`BuildPoolMesh()` in scene 24 is **not a pool**. Its five faces are x=±1, z=±1 and
**y=+1 — a lid, with no floor.** It was only ever a proxy volume for
`ww_pool.gdshader` to raymarch. It cannot be rasterized. The plain (raytrace-off)
path uses `BuildPlainPoolMesh()` instead: floor plus four walls, normals inward,
so default back-face culling drops the near walls and leaves the interior visible.

Same class of trap: the `ww_*` shaders read `water_tex` as `R=height, B=normal.x,
A=normal.z`. Any path that writes the field must write normals too, or the surface
renders as a featureless blob no matter how correct the heights are.

---

## The series

Hard-split — one scene per variable, so each can be shown on its own.

### Geometry / field

| Scene | Isolated variable | Control |
|---|---|---|
| `24_base` | — (reference) | nothing added |
| `24_col` | one column | **shape** (cylinder / square / blade), **hard↔soft as a slider**, radius, position |
| `24_cols` | column **count** | 1 → 8, layout preset |
| `24_src` | source **count** | 1 / 2 / 3, spacing |
| `24_move` | object motion, **as a source** | speed, path, on/off |
| `24_scat` | object motion, **as a scatterer** | static ↔ moving |

Hard/soft is a slider, not a switch: 0 = pure absorber, 1 = perfect reflector.
The interesting region is wherever it stops reading as a hole and starts reading
as a pillar.

`24_move` vs `24_scat` is the important pair. An object that *pushes* water is a
moving source (scene 24's ball already does this via `volume_in_sphere`). An
object that *blocks* water is geometry, and a moving one changes the operator
every step. Different cost, different failure mode.

### Nonlinear — where interaction actually comes from

Superposition holds for any linear network no matter how elaborate. Feeding two
signals into one tank does not make them interact. **Only a nonlinearity in a
feedback path does.** Each of these is a different nonlinearity in a different
place.

| Scene | Isolated variable | Source |
|---|---|---|
| `24_diode` | threshold loss — knee, Is/Vt | Shockley shape from `lib/mna-solver.cmajor` |
| `24_clip` | nonlinearity **position** — off / after-loop / in-loop | MangledVerb `Softclip` / `Overdrive` |
| `24_speed` | amplitude-dependent speed, coupling 0→1 | `c = √(g(h+η))`; Thermae's crossfade |

**`24_diode`** — `Gd = (Is/Vt)·exp(Vd0/Vt)` is a conductance that is ~zero below a
knee and exponential above it. Used here as a per-cell loss term: **no matrix, no
solve, no stamp assembly, just the exponential.** The demo to aim for is two waves
that each do nothing alone and only do something where they cross.

**`24_clip`** — MangledVerb's architecture is explicitly *distortion AFTER reverb*.
Same clipper `x/(1+|x|+0.28x²)` in two positions; outside the loop it cannot make
signals interact, inside it can. Only scene that forks `webgpu_sim.glsl`.

**`24_speed`** — Dattorro's tank already has a modulated delay with cubic Hermite
reads; the modulation source becomes amplitude instead of an LFO. Thermae's
`PitchShiftDelay` already solves the click problem with a two-reader crossfade
and explicit pitch-time coupling.

### Reverb topology / operator

| Scene | Isolated variable | Notes |
|---|---|---|
| `24_cxm` ✅ | tank on/off | built. Tuned delays 7188/6005/6807/5106 intact |
| `24_cxm_clock` | tank **clock** 300 → 48000 Hz | the only adaptation made to the source; at 48000 it is the audio patch bit-for-bit |
| `24_cxm_taps` | **tap count + position** 4 → 7 | in audio the extra Dattorro taps only decorrelate stereo; here each tap is a *place* |
| `24_cxm_type` | the tuned presets | Room/Plate/Hall × Diffusion × Tank mod — the source's own tables |
| `24_zita` | **topology** | figure-of-eight ↔ Zita FDN (Hadamard, `DelayWithFeedback`). Same drive, same taps, same field |
| `24_ir` ✅ | solver vs convolution | built |
| `24_irlen` | **kernel length** 64 → 1024 slices | 1024 ≈ 268 MB at 256², the practical ceiling |

---

## Follow-up (do NOT act on until the series has been looked at)

**Where MNA could help.** Deferred deliberately — the point is to be true to the
source material first and see what the explicit sim actually does. Revisit each of
these only if the matching symptom actually shows up on screen.

| Watch for, in | Symptom | What a stamp offers |
|---|---|---|
| `24_col` | hard/soft slider feels ad-hoc; absorber still reflects | A boundary IS a conductance. Soft = resistor to ground, hard = Dirichlet pin. The slider becomes a real impedance instead of a damping mask. A perfectly absorbing column is the matched-impedance value `G = 1/√(gh)` |
| `24_diode` | explicit version blows up at high drive, or the knee has to be detuned to stay stable | The diode is *native* MNA vocabulary — Newton-linearized `Gd`/`Ieq` recomputed at the operating point, unconditionally stable. This is the textbook reason to go implicit |
| `24_speed` | amplitude coupling has to be kept small to avoid instability | `β_face = g·dt²·h_face/dx²` — depth-varying conductance is free in the stamp, and amplitude-dependent speed is just a conductance that depends on the solution, re-linearized per step. Structurally the same pattern as the diode |
| `24_scat` | moving scatterer forces the timestep down, or ghosts/rings | Re-stamping a moving boundary each step is cheap. Implicit removes the `√(gh)` timestep tax that a moving obstacle makes worse |
| `24_irlen` | kernels too long / too heavy to store per source | Spectral (DST/DCT) diagonalizes the Helmholtz into mode space — independent damped oscillators, one per mode. Replaces a stored FIR with a resonator bank |
| any open-domain test | edges reflect and the box is visible | Matched-impedance stamp at the boundary, a handful of cells instead of a wide sponge |

The honest framing: MNA is a change to **how the fast term is stepped**, not to
the physics. It buys stability and a principled way to express boundaries and
amplitude-dependent conductance. It costs a solve. Nothing above is worth paying
for until something on screen actually asks for it.

See also `../shorewaves/docs/design/path-b-mna-stamp-solver.md`.
