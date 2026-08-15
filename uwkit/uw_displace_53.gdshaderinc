// SCENE 53's SURFACE, defined ONCE. Included by the water shader AND by the mask volume,
// so the two cannot disagree about where the water is.
//
// This is the rule scene 213 established the hard way: every misalignment traced to a second
// evaluation of a surface that was already being evaluated elsewhere. A mask that copies the
// displacement drifts the moment either copy is touched; a mask that INCLUDES it cannot.
//
// Three things displace this surface, and the mask needs all three:
//   1. composite Gerstner — note it moves XZ as well as Y, so it is not a heightfield
//   2. the CN-MNA window, sampled at the DISPLACED xz (sampling at rest draws ripples up to
//      ~2 m off the visual surface, because of the bands' sideways sway)
//   3. the foam-v2 puff, a small vertical lift under dense foam
//
// The uniforms live here rather than in the water shader for the same reason as the
// function: one declaration, two consumers.

group_uniforms waves;
uniform float wave_time = 0.0;
const int MAX_WAVES = 16;
uniform int wave_count = 0;
uniform float wavelengths[MAX_WAVES];
uniform float steepnesses[MAX_WAVES];
uniform float speeds[MAX_WAVES];
uniform float dirs[MAX_WAVES];

group_uniforms mna;
uniform sampler2D mna_tex : filter_linear, repeat_enable; // toroidal: hardware wrap does the mod
uniform vec2 mna_origin = vec2(-24.0, -24.0);
uniform vec2 mna_tex_offset = vec2(0.0);
uniform float mna_window = 48.0;
uniform float mna_displacement = 1.0;
uniform float mna_edge_fade = 0.15;
uniform bool foam2_enabled = true;
uniform sampler2D mna_foam2_tex : filter_linear, repeat_enable;
uniform float foam2_puff = 0.06;

vec3 gerstner_wave(vec3 position, float steep, float wavelen, float spd, float direction, inout vec3 tangent, inout vec3 binormal) {
	direction = direction * 2.0 - 1.0;
	vec2 d = normalize(vec2(cos(3.14159265 * direction), sin(3.14159265 * direction)));
	float k = 2.0 * 3.14159265 / wavelen;
	float f = k * (dot(d, position.xz) - spd * wave_time);
	float a = steep / k;
	tangent += vec3(-d.x * d.x * (steep * sin(f)), d.x * (steep * cos(f)), -d.x * d.y * (steep * sin(f)));
	binormal += vec3(-d.x * d.y * (steep * sin(f)), d.y * (steep * cos(f)), -d.y * d.y * (steep * sin(f)));
	return vec3(d.x * (a * cos(f)), a * sin(f), d.y * (a * cos(f)));
}

// The whole displacement, exactly as the water applies it. `out` params hand back what the
// water shader still needs for shading; the mask ignores them.
vec3 uw53_displace(vec3 pos, out vec3 tangent, out vec3 binormal, out vec2 out_mna_uv, out float out_fade) {
	vec3 offset = vec3(0.0);
	tangent = vec3(1.0, 0.0, 0.0);
	binormal = vec3(0.0, 0.0, 1.0);
	for (int i = 0; i < wave_count && i < MAX_WAVES; i++) {
		offset += gerstner_wave(pos, steepnesses[i], wavelengths[i], speeds[i], dirs[i], tangent, binormal);
	}
	out_mna_uv = (pos.xz + offset.xz - mna_origin) / mna_window;
	vec2 e = min(out_mna_uv, 1.0 - out_mna_uv);
	out_fade = smoothstep(0.0, mna_edge_fade, min(e.x, e.y));
	if (out_fade > 0.0) {
		offset.y += texture(mna_tex, out_mna_uv + mna_tex_offset).r * mna_displacement * out_fade;
		if (foam2_enabled && foam2_puff > 0.0) {
			vec2 f2v = texture(mna_foam2_tex, out_mna_uv + mna_tex_offset).rg;
			offset.y += foam2_puff * min(f2v.r + f2v.g * 0.5, 1.5) * out_fade;
		}
	}
	return offset;
}
