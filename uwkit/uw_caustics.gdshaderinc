// uwkit — UNDERWATER CAUSTICS, as a chunk any submerged object can include.
//
// Lifted from water-kit's shaders/caustics_object.gdshader (01b), which is itself scene 07's
// OpenSimplex2 double-pan plus scene 05's 32-frame flipbook. Same maths, one behavioural
// change, and it is the same change every other piece of this kit needed:
//
//   BEFORE:  float underwater = step(world_pos.y, water_level);
//   AFTER:   the VOLUME MASK, sampled per pixel
//
// The original tests each fragment against a FLAT water level. On any real sea that is wrong
// by the wave height — caustics appear on rock above a trough and vanish under a crest — and
// it is a hard step, so it pops. The mask is rasterised from the actual water geometry, so it
// knows where the surface is including waves.
//
// HOST SHADER:
//   1. #include "res://uwkit/uw_caustics.gdshaderinc"
//   2. varyings for world position and world normal (see uw_caustics_object.gdshader)
//   3. EMISSION += uw_caustics(world_pos, world_normal, SCREEN_UV);
//
// The host supplies uw_mask_tex; with no mask bound an unset sampler reads white, i.e.
// "everywhere is underwater", which is the old flat behaviour rather than a black screen.

// Volume mask: 1 where this pixel is inside the water. See uwkit/uw_boundary.glslinc.
uniform sampler2D uw_mask_tex : filter_linear;

group_uniforms caustics;
uniform float caustic_intensity : hint_range(0.0, 4.0, 0.01) = 0.9;
uniform float caustic_scale : hint_range(0.02, 2.0, 0.001) = 0.28; // world XZ -> caustic space
uniform float caustic_speed : hint_range(0.0, 2.0, 0.01) = 0.5;
uniform vec3 caustic_tint : source_color = vec3(1.0, 0.98, 0.9);
uniform float water_level = 0.0;
uniform float depth_fade : hint_range(0.0, 1.0, 0.001) = 0.12;     // per-metre falloff below surface

group_uniforms flipbook;
uniform sampler2DArray caustics_frames : source_color, repeat_enable, filter_linear;
uniform int frame_count = 32;
uniform float fps : hint_range(0.0, 60.0, 0.5) = 24.0;

varying vec3 world_pos;
varying vec3 world_normal;

// ---- OpenSimplex2 caustics (VERBATIM from caustics_floor.gdshader / Malidos) ----
// Permutation polynomial hash credit Stefan Gustavson
vec4 permute(vec4 t) {
	return t * (t * 34.0 + 133.0);
}

// Gradient set is a normalized expanded rhombic dodecahedron
vec3 grad(float hash) {
	vec3 cube = mod(floor(hash / vec3(1.0, 2.0, 4.0)), 2.0) * 2.0 - 1.0;
	vec3 cuboct = cube;
	cuboct[int(hash / 16.0)] = 0.0;
	float type = mod(floor(hash / 8.0), 2.0);
	vec3 rhomb = (1.0 - type) * cube + type * (cuboct + cross(cube, cuboct));
	vec3 grad = fma(cuboct, vec3(1.22474487139), rhomb);
	grad *= fma(-0.042942436724648037, type, 1.0) * 3.5946317686139184;
	return grad;
}

// BCC lattice split up into 2 cube lattices
vec4 os2NoiseWithDerivativesPart(vec3 X) {
	vec3 b = floor(X);
	vec4 i4 = vec4(X - b, 2.5);
	vec3 v1 = b + floor(dot(i4, vec4(.25)));
	vec3 v2 = b + vec3(1, 0, 0) + vec3(-1, 1, 1) * floor(dot(i4, vec4(-.25, .25, .25, .35)));
	vec3 v3 = b + vec3(0, 1, 0) + vec3(1, -1, 1) * floor(dot(i4, vec4(.25, -.25, .25, .35)));
	vec3 v4 = b + vec3(0, 0, 1) + vec3(1, 1, -1) * floor(dot(i4, vec4(.25, .25, -.25, .35)));
	vec4 hashes = permute(mod(vec4(v1.x, v2.x, v3.x, v4.x), 289.0));
	hashes = permute(mod(hashes + vec4(v1.y, v2.y, v3.y, v4.y), 289.0));
	hashes = mod(permute(mod(hashes + vec4(v1.z, v2.z, v3.z, v4.z), 289.0)), 48.0);
	vec3 d1 = X - v1; vec3 d2 = X - v2; vec3 d3 = X - v3; vec3 d4 = X - v4;
	vec4 a = max(0.75 - vec4(dot(d1, d1), dot(d2, d2), dot(d3, d3), dot(d4, d4)), 0.0);
	vec4 aa = a * a; vec4 aaaa = aa * aa;
	vec3 g1 = grad(hashes.x); vec3 g2 = grad(hashes.y);
	vec3 g3 = grad(hashes.z); vec3 g4 = grad(hashes.w);
	vec4 extrapolations = vec4(dot(d1, g1), dot(d2, g2), dot(d3, g3), dot(d4, g4));
	vec4 derivative = -8.0 * mat4(vec4(d1,0.), vec4(d2,0.), vec4(d3,0.), vec4(d4,0.)) * (aa * a * extrapolations)
		+ mat4(vec4(g1, 0.), vec4(g2, 0.), vec4(g3, 0.), vec4(g4, 0.)) * aaaa;
	return vec4(derivative.xyz, dot(aaaa, extrapolations));
}

vec4 os2NoiseWithDerivatives_Fallback(vec3 X) {
	X = dot(X, vec3(2.0/3.0)) - X;
	vec4 result = os2NoiseWithDerivativesPart(X) + os2NoiseWithDerivativesPart(X + 144.5);
	return vec4(dot(result.xyz, vec3(2.0/3.0)) - result.xyz, result.w);
}

// Two-tap caustic noise sample for a single planar 2D coord + time slice.
float caustic_sample(vec2 uv, float t) {
	vec3 X = vec3(uv, t);
	vec4 n = os2NoiseWithDerivatives_Fallback(X);
	n = os2NoiseWithDerivatives_Fallback(X - n.xyz / 16.0);
	return fma(n.w, 0.5, 0.5);
}


// Caustic contribution for one fragment. Add it to EMISSION so it survives shadow and
// ambient — caustics are light cast THROUGH the water, not a property of the surface.
vec3 uw_caustics(vec3 world_pos, vec3 world_normal, vec2 screen_uv) {
	float depth = max(0.0, water_level - world_pos.y);
	float upface = clamp(world_normal.y, 0.0, 1.0);   // flat-up faces catch most light
	float fade = exp(-depth * depth_fade);            // dimmer the deeper it is

	// THE MASK, not step(world_pos.y, water_level). Per pixel, wave-aware, and smooth.
	float underwater = texture(uw_mask_tex, screen_uv).r;

	vec2 p = world_pos.xz * caustic_scale;
	float caustics = 0.0;
	if (mode == 0) {
		// Procedural double-pan: two scrolling samples min'd -> the moving light web.
		float t = TIME * caustic_speed;
		float c1 = caustic_sample(p + vec2(t * 0.15, t * 0.10), t);
		float c2 = caustic_sample(p * 1.3 - vec2(t * 0.12, t * 0.16), t * 0.8 + 5.0);
		caustics = min(c1, c2);
		caustics = pow(clamp(caustics, 0.0, 1.0), 2.0);   // sharpen ridges into light lines
	} else {
		// Flipbook: blend two consecutive frames of the real caustics loop.
		float f = mod(TIME * fps, float(frame_count));
		float f0 = floor(f);
		float f1 = mod(f0 + 1.0, float(frame_count));
		float s0 = texture(caustics_frames, vec3(p, f0)).r;
		float s1 = texture(caustics_frames, vec3(p, f1)).r;
		caustics = mix(s0, s1, fract(f));
	}

	return caustic_tint * caustics * caustic_intensity * upface * fade * underwater;
}
