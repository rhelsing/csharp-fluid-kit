shader_type spatial;
render_mode unshaded, cull_back;

// artifact_volume.gdshader — the 3D half of the artifact-series display family, and the
// exact counterpart of artifact_view.gdshader (docs/artifacts-250.md: "Display in 3D =
// the milk-glass march"). A FORK of milk_glass.gdshader — scene 32 keeps its own shader
// untouched, the way 51 forked 50's foam kernel rather than editing it.
//
// Two changes from milk_glass, and they are the whole point:
//   1. THE PALETTE. Milk-white scatter over a coffee backdrop becomes ink over paper,
//      the same six colours as the 2D shader, mixed in sRGB and linearised once at the
//      end. Pair with a linear tonemap (Scene250Base sets one).
//   2. A SECOND CHANNEL. The divergence volume is marched alongside the dye and added
//      in #E23D6D — the residual, in the signature colour, in 3D. It accumulates
//      ADDITIVELY rather than alpha-compositing, so it reads as emission through the
//      material instead of a second fluid, matching the 2D overlay.
//
// The cylinder mask is off by default here: 250b is a box of fluid, not a glass. Turn it
// on for the scene-32 look.

const vec3 PAPER    = vec3(0.9098, 0.8863, 0.8353);   // #E8E2D5
const vec3 INK      = vec3(0.0863, 0.1294, 0.2431);   // #16213E
const vec3 ARTIFACT = vec3(0.8863, 0.2392, 0.4275);   // #E23D6D
const vec3 ROOM     = vec3(0.0902, 0.0745, 0.0627);   // #171310

uniform sampler3D dye_tex : filter_linear, repeat_disable;
uniform sampler3D artifact_tex : filter_linear, repeat_disable, hint_default_black;

uniform vec3 box_extents = vec3(1.4, 2.1, 1.4);   // volume half-extents (world; node at origin)
uniform float density : hint_range(1.0, 40.0) = 8.0;
uniform float knee : hint_range(0.0, 0.15) = 0.03;   // dilute dye below this stays haze
uniform float absorb : hint_range(0.0, 2.0) = 0.45;
uniform float shade : hint_range(0.0, 1.0) = 0.4;
uniform float steps : hint_range(16.0, 96.0) = 56.0;

uniform float artifact_gain : hint_range(0.0, 40.0) = 0.0;   // 0 = channel off
uniform bool artifact_solo = false;   // residual alone, on room — the 3D "mode 3"

uniform bool use_cylinder = false;
uniform float cyl_radius : hint_range(0.5, 1.0) = 0.94;

varying vec3 world_pos;

vec3 srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 ro = CAMERA_POSITION_WORLD;
	vec3 rd = normalize(world_pos - ro);

	// slab test against the axis-aligned volume box at the origin
	vec3 inv = 1.0 / rd;
	vec3 tA = (-box_extents - ro) * inv;
	vec3 tB = (box_extents - ro) * inv;
	vec3 tMin = min(tA, tB);
	vec3 tMax = max(tA, tB);
	float t0 = max(max(tMin.x, tMin.y), max(tMin.z, 0.0));
	float t1 = min(tMax.x, min(tMax.y, tMax.z));

	vec3 acc = vec3(0.0);
	float alpha = 0.0;
	float resid = 0.0;   // marched separately: emission, not occlusion

	if (t1 > t0) {
		float dt = (t1 - t0) / steps;
		float t = t0 + dt * 0.5;
		for (int i = 0; i < 96; i++) {
			if (float(i) >= steps || alpha > 0.985) { break; }
			vec3 p = ro + rd * t;
			vec3 uvw = p / (2.0 * box_extents) + 0.5;
			bool inside = true;
			if (use_cylinder) {
				inside = length((uvw.xz - 0.5) * 2.0) < cyl_radius;
			}
			if (inside) {
				if (artifact_gain > 0.0) {
					// |div|: the compressibility the solve failed to remove
					resid += abs(texture(artifact_tex, uvw).r) * dt;
				}
				float d = max(texture(dye_tex, uvw).r - knee, 0.0);
				if (d > 0.002) {
					float a = 1.0 - exp(-d * density * dt);
					float occ = texture(dye_tex, uvw + vec3(0.0, 0.05, 0.0)).r;  // dye above shades
					vec3 col = INK * (1.0 - shade * clamp(occ * 1.6, 0.0, 1.0));
					acc += (1.0 - alpha) * a * col;
					alpha += (1.0 - alpha) * a;
				}
			}
			t += dt;
		}
	}

	if (artifact_solo) {
		// the residual owns the frame — no dye, no paper
		ALBEDO = srgb_to_linear(ROOM + ARTIFACT * resid * max(artifact_gain, 1.0));
	} else {
		// paper is the zero level, dimmed with depth through the field (Beer–Lambert
		// over the in-box path) so the volume reads as having thickness
		vec3 through = exp(-(vec3(1.0) - PAPER) * absorb * max(t1 - t0, 0.0));
		vec3 col = acc + (1.0 - alpha) * PAPER * through;
		col += ARTIFACT * resid * artifact_gain;
		ALBEDO = srgb_to_linear(col);
	}
}
