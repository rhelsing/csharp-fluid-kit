# Scene 17 · Shorewaves — Modification Ideas

A living backlog of directions for the **C# KP07 shallow-water port** (scene 17).
Add, reorder, and cut freely — this is a working doc, not a spec.

**Where things stand (M0–M3 done):** `scripts/lib/ShallowWaterKp.cs` (solver) +
`scripts/lib/BeachScenario.cs` (sloped bathymetry) drive `scripts/Shorewaves.cs`.
The scene renders through the full `water.gdshader` (screen-space refraction + foam
lace) over a wet-sand shore, with a free-fly camera, SPACE to fire a solitary wave,
and a live perf panel (Debug/Full surface, SSR/SSAO/SSIL/Glow toggles, resolution
scale). Foam is a `hc` tracer field sourced by breaking/bore/swash and rendered as
Voronoi lace — see the walkthrough in the session notes.

---

## Summary

| # | Idea | Kind | Fidelity ↔ Perf | Effort | Status |
|---|------|------|-----------------|--------|--------|
| **R1** | **Chris Wallis SDF-raytraced ocean (verbatim)** | render | **fidelity** | large | referenced ↓ |
| R2 | Cheap analytic/screen-space raytraced surface (no SSR) | render | perf | med | discussed |
| R3 | Caustics projected on the wet bed | render | neutral | small | deferred (M3) |
| S1 | Click / drop interaction — objects source waves + foam | sim | — | med | discussed |
| S2 | Custom breaking criterion / paintable foam | sim | — | small | idea |
| S3 | Wall / ocean / dam-break scenarios on the same C# solver | sim | — | med | idea |
| P1 | Perf lab: FSR2, grid-res + substep sliders | perf | perf | small | partial |
| V1 | Polish: foam noise, wet-sand strength, domain-edge horizon | look | — | small | idea |
| V2 | Optional PBR terrain / boulders (drop the asset-free rule) | look | — | med | idea |

---

## R1 — Chris Wallis SDF-raytraced ocean ★ (verbatim reference)

- **Source:** <https://www.shadertoy.com/view/wlsyzH> — Chris Wallis (@chriskwallis).
- **Verbatim copy:** [`refs/chris_wallis_raytraced_ocean.glsl`](refs/chris_wallis_raytraced_ocean.glsl) (vendored unedited).
- **License caveat:** Shadertoy's default is **CC BY-NC-SA 3.0** unless the author
  says otherwise — the **NC clause** matters if this project ever goes commercial.
  Verify + cite in the shader header when we port it (repo convention).

**What it is.** A single-pass, per-pixel **SDF raymarcher**. The water is a signed
distance field (`QueryOceanDistanceField` = 2 sines + fbm noise + a plane at
`WATER_LEVEL`), with a carved noisy sphere for the wave hollow and a unioned ground.
Per ray: intersect the opaque scene (sand plane + 5 coral SDF spheres) → sphere-trace
to the water surface (`IntersectVolumetric` + binary refine) → at the surface, Fresnel
**split**: reflect the sky, refract in → **march through the volume** accumulating
Beer-Lambert absorption + sun/ambient in-scatter → exit refraction (+ optional
secondary reflection) → shade the floor beneath. Extras: **white water** and
**caustics** from `smoothVoronoi`, raymarched fbm **clouds**, **wet sand** (darken +
POM + sky reflection), and live-tunable `WaterIor / WaterTurbulence / WaterAbsorption /
WaterColor` (stored in a 1-pixel buffer).

**Why we want it.** It's the *true* raytraced look — real refraction through the water
body, depth-correct absorption, and caustics — entirely self-contained (no SSR/SSAO/
SSIL stack). It's the fidelity ceiling for this project.

**The port that actually matters (KP07-driven, not analytic).** The shader's surface is
`sin + fbm`. Swap it for **our sim**:
- Replace `QueryOceanDistanceField(pos)` with `pos.y - sampleSurfaceHeight(pos.xz)`,
  where the height comes from `tx_state.r` (the free surface `w`) — i.e. the SDF of the
  *real* KP07 fluid.
- Replace the ground plane/`SandHeightMap` with `tx_bottom.r` (our actual bed).
- Drive **white water** from `tx_derived.b` (our foam field) instead of the height-based
  Voronoi, so the raytraced foam matches the physics.
- Keep Wallis's Fresnel / volume-march / caustics / cloud machinery intact.
- Result: a **raytraced render of the real breaking-wave sim**.

**Godot architecture.** It's a *camera* raymarch, not a mesh surface — implement as a
**fullscreen pass** (a `CanvasLayer` full-rect `ShaderMaterial`, or a quad in front of
the camera), reconstructing the ray from `INV_PROJECTION_MATRIX` + camera transform, and
feeding it the sim `Texture2Drd`s + sun/env uniforms. The Shadertoy `iChannel`/`iMouse`/
`iTime`/keyboard-buffer plumbing all gets replaced with Godot uniforms + our DemoUI.

**Honest tradeoff.** Raymarching is a **heavy per-pixel cost** — this is the *fidelity*
path, **not** the "raytracing for performance" idea (that's R2). Its `PERFORMANCE_MODE`/
`ULTRA_MODE` exist precisely to trade steps for framerate. It *is* a fixed cost
independent of the SSR/SSAO stack, so keep it as a **switchable surface mode** next to the
mesh path (or its own scene 18), and profile it against Full/Debug on the perf panel.

**Effort:** large. Suggested slices: (1) vendor + get it rendering its *own* analytic
ocean as a fullscreen Godot shader (proves the raymarch/camera plumbing), (2) swap the
surface SDF to `tx_state`, (3) swap the floor to `tx_bottom` + foam to `tx_derived`,
(4) wire the tunables to DemoUI.

---

## R2 — Cheap analytic / screen-space raytraced surface (the perf path)
The *other* "raytraced" idea: replace the SSR/SSAO/SSIL stack with in-shader analytic
sky reflection + the existing screen-space refraction (water.gdshader already refracts;
sky reflection comes from Fresnel + the environment). Optionally graft water-kit's
Evan-Wallace flat-pool raytracer — but note it assumes a **flat floor**, which fights our
sloped bed, so it's a better fit as its own pool scene. This is the "more performant"
direction; R1 is the "more beautiful" one.

## R3 — Caustics on the wet bed
Project animated caustics onto the terrain under shallow water (shorewaves ships a
`caustics.png` + the `sand.gdshader` caustics pass; we already copied the texture).
Small add to `sand_min.gdshader`, gated by water depth from `tx_state`.

## S1 — Click / drop interaction
Mouse-pick or dropped `RigidBody3D`s poke the sim (a stamp into `hc`/momentum, like the
solitary source) and read height back to float — and **source foam** at the impact via
the same breaking terms. Natural hooks: `pass_solitary`-style injection, and the foam
`inject` term in `pass_step`.

## S2 — Custom breaking criterion / paintable foam
Expose the breaking/bore/swash weights (`pass_step.glsl:241`) as tunables, or paint a
foam mask — cheap knobs on the physics we already ported.

## S3 — Wall / ocean / dam-break scenarios
Port the other `SimController.CFG` presets onto the same C# solver (wall = quay impact,
ocean = 2048² archipelago, dam-break = validation). Mostly config + bathymetry; the
solver is scenario-agnostic.

## P1 — Perf lab expansion
FSR2 mode (not just bilinear scale), plus grid-resolution and substep-budget sliders, so
every axis of the sim/render cost is a live A/B. Extends the existing panel.

## V1 / V2 — Polish & assets
Tame the mid-field foam-lace noise, strengthen wet-sand contrast, hide the finite
30.4 m domain edge (fog/skirt), and — if we drop the asset-free rule — bring over
shorewaves' PBR sand/rock sets and boulder props for the hero look.

---

### How we work this doc
Pick an entry, spin a slice, verify with `tools/shoot.tscn` (render, never blind), and
tick it off. Verbatim ports get their source vendored under `docs/refs/` (or a shader
header) with the URL + license, per repo convention.
