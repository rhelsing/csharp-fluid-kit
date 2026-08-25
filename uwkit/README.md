# uwkit — a modular underwater system

Drop-in underwater rendering for any water: fog, meniscus, caustics, full-frame warp,
particles, and the surface seen from below. Portable across projects and languages —
these files are **byte-identical** in `water-kit` (GDScript) and
`godot-csharp-experiments` (C#), and are meant to stay that way.

Two working examples ship with it:

| Scene | Water | Host |
|---|---|---|
| `water-kit / 212_backface_tag` | 01b's noise-octave sea (`map()` chain) | GDScript |
| `godot-csharp-experiments / 53_underwater_lab` | composite Gerstner + CN-MNA window | C# |

---

## The one idea

**Render the water volume as a mask. Multiply everything below-water by it. Never gate on
a boolean.**

Every underwater effect used to be switched by a camera test — `is the camera below the
surface?` — computed from a CPU re-implementation of the wave function. That is a *second
opinion* about a surface the GPU already evaluates exactly, and it disagrees near the
crossing. That disagreement is the popping, and no amount of phase/amplitude/offset tuning
removes it, because the error is structural rather than numeric.

Instead, a `SubViewport` renders the water **volume** — a box whose top face is displaced by
the *same* wave function the water uses — writing front faces black and back faces white.
Depth testing does the rest:

- **outside** the volume, the front face is nearer and wins → mask **0**
- **inside**, only back faces remain → mask **1**

That texture cannot drift from the water, because it *is* the water's geometry. It also
solves cases a boolean can't: looking into a trough from above, the nearest surface is still
a front face, so the mask reads 0 and the underside hides itself.

---

## The four invariants

Break any one and the mask silently stops matching — it will look like a tuning problem and
it is not.

**1 · ONE wave function.** The displacement lives in a shared `.gdshaderinc`, `#include`d by
both the water shader and the mask shader. Not copied — *included*. A copy drifts the moment
either side is touched.

**2 · ONE data push.** Every uniform the displacement reads goes to both materials in the
same call. If a push ever feeds only one of them, they describe different seas.

**3 · MATCHING vertex grids.** The mask volume's top face must match the water's footprint
**and** subdivisions. Two piecewise-linear surfaces agree only where their vertices coincide;
a coarser mask straight-lines across short waves and reads as *"a line instead of a curve"*,
worsening with every band you add.

**4 · SAME clock, SAME frame.** Push the time uniform to both materials in the same frame. A
one-frame skew is indistinguishable from a misaligned mask.

---

## Files

| File | Role |
|---|---|
| `uw_boundary.glslinc` + `uw_boundary_effect.gd` | `CompositorEffect`: masked fog, plus depth/column/mask debug views |
| `uw_underside.gdshaderinc` | the surface seen from below — Snell's window, TIR falloff |
| `uw_caustics.gdshaderinc` | caustics chunk for any submerged object |
| `uw_caustics_object.gdshader` | ready-made material using the above |
| `uw_overlay.gdshader` | 2D overlay: the meniscus, and a debug fill |
| `uw_particle.gdshader` | billboard particle material, alpha × mask |
| `uw_warp_effect.gd` + `uw_warp.glsl` | full-frame distortion, scaled by the mask |
| `providers/*.glslinc` | height providers — flat, ripple, gerstner, texture, fft |

**Per-water, not in the kit:** the mask shader (it includes *your* displacement) and the
provider choice. That's the whole integration surface.

---

## Wiring, step by step

### 1 · Share the wave function

Move your water's displacement into a `.gdshaderinc` and include it from the water shader.
Nothing should change visually — that's the check.

```glsl
// my_wave.gdshaderinc
uniform float wave_time;
float my_height(vec2 xz, float t) { /* ... */ }
```

### 2 · Write the mask shader

```glsl
shader_type spatial;
render_mode unshaded, cull_disabled;
#include "res://shaders/my_wave.gdshaderinc"

uniform float box_top = 0.0;   // the box's top face, in LOCAL y

void vertex() {
    if (VERTEX.y > box_top - 0.001) {
        vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        VERTEX.y += my_height(w.xz, wave_time);   // a DELTA — see the gotcha below
    }
}

void fragment() {
    ALBEDO = FRONT_FACING ? vec3(0.0) : vec3(1.0);   // ALBEDO, not EMISSION
}
```

### 3 · Render it in a SubViewport

- own `World3D`, containing only the mask box and a camera
- a black `Environment` — a fresh world has none, and a non-black clear reads as
  "masked everywhere"
- the camera **mirrors the main one** and has **physics interpolation OFF**
- the box's top face matches the water's footprint and subdivisions (invariant 3)

### 4 · Multiply everything below-water by it

```glsl
float m = texture(uw_mask_tex, SCREEN_UV).r;
col = mix(topside, underside, m);        // the underside
EMISSION = uw_caustics(pos, nrm, SCREEN_UV);   // caustics: mask is inside
ALPHA *= m;                              // particles, overlays, tint planes
```

For the compositor, hand it an RD texture:

```gdscript
uw.mask_texture = RenderingServer.texture_get_rd_texture(mask_vp.get_texture().get_rid())
uw.use_mask = true
```

### 5 · Delete the booleans

Anything of the form `effect.visible = is_underwater` is now wrong. Node-level things that
genuinely can't be per-pixel (Godot's `Environment` fog) should be **off**, with the
compositor supplying a masked equivalent.

---

## Gotchas, all of them found the hard way

- **No stencil.** `RenderSceneBuffersRD` exposes colour, depth and velocity only — a
  `CompositorEffect` can never read a stencil tag. Crest's mask approach is unavailable in
  Godot; the SubViewport is the workaround.
- **Alpha doesn't survive either.** Tagging back faces via the colour buffer's alpha was
  tested and failed.
- **A `Camera3D`'s Compositor overrides the `WorldEnvironment`'s.** Two Compositor objects on
  two nodes is *not* additive — one silently wins. Put every effect in one array.
- **`unshaded` does not put `EMISSION` in the buffer.** Write the mask to `ALBEDO`, or it
  renders black on black and looks like nothing at all.
- **Absolute vs delta displacement.** `VERTEX.y = h` only works if the mesh shares the
  water's origin. The mask box sits at `-depth/2`, so it needs `box_top - map(...)`.
  `VERTEX += offset` is immune. Getting this wrong puts the mask a half-depth below the
  water and *every* masked effect multiplies by zero.
- **Mask cameras need interpolation off**, or they're smoothed toward a different transform
  than the one they're supposed to mirror.
- **New `.glsl` files need Godot's import pass** before they exist as `RDShaderFile`.
- **Godot rejects a `varying` passed as an `out` parameter** in `vertex()`. Use locals, then
  assign.
- **Unset `sampler2D` reads white**, so a missing mask means "everywhere is underwater"
  rather than a black screen. Convenient, but it hides wiring mistakes.

---

## Bringing it to the FFT scene (211)

Everything above transfers. One piece is genuinely new.

**The problem.** `water_faithful` renders a **clipmap** — camera-synced LOD rings whose
vertex density falls off with distance — plus a horizon fade that flattens displacement far
away. Invariant 3 says the mask must match the water's vertex grid, and a fixed-density mask
can only match *one* ring.

**Why it's tractable anyway.** The waterline is always at the camera, and the clipmap's
highest-density ring is always at the camera. Distant coarse rings never contain a waterline,
so the mask doesn't have to match them. It needs to cover the near field only.

**The plan.**

1. **Camera-following mask volume**, sized to a few times the inner ring and subdivided to
   that ring's density. Not the whole ocean.
2. **Mask shader samples the same cascades.** The FFT provider already does exactly this —
   three displacement textures, `fract(xz / cascade_scale)`, plus one inverse-displacement
   iteration. Reuse that as the mask's vertex displacement, since FFT moves sideways as well
   as up.
3. **Feed all three cascades** (invariant 2). Feeding only the largest gives a straight
   waterline — a ~4501 m swell does not vary across a 2 m span.
4. **Same frame** (invariant 4) — cascade textures update per frame; the mask must read the
   same ones the surface did.
5. **Then wire, don't build:** fog, meniscus, underside, caustics, warp and particles are
   all kit components already.

**Known risk.** The clipmap applies `displacement_scale` and a **horizon fade** in its vertex
shader. Near the camera both are ~1, so a mask that ignores them should match where it
matters — but if the waterline drifts at distance, that's the cause, and the fix is to apply
the same fade in the mask.
