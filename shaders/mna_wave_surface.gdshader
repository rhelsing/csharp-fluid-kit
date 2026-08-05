// Scene 21 surface — reads the GPU stamp-solver's height field (a Texture2DRD,
// height in .r) for vertex displacement + gradient normals. A colormap toggle
// visualizes the raw height so the solver's behaviour / stability is legible.
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D height_tex : filter_linear, repeat_disable;
uniform vec2 tex_size = vec2(256.0);
uniform float plane_size = 6.0;
uniform float displacement = 0.35;
uniform float normal_strength = 1.5;
uniform float show_heightmap = 0.0;                 // 0 = water, 1 = height colormap
uniform float colormap_gain = 1.0;                  // scales height into the colormap (1 = raw)
uniform vec3 shallow_color : source_color = vec3(0.12, 0.48, 0.72);
uniform vec3 deep_color : source_color = vec3(0.02, 0.12, 0.26);

varying vec2 uv;
varying float vheight;

float sample_h(vec2 u) {
	return texture(height_tex, u).r;
}

vec3 colormap(float t) {
	t = clamp(t * 0.5 + 0.5, 0.0, 1.0);   // divergent blue -> white -> red
	vec3 lo = vec3(0.10, 0.22, 0.85);
	vec3 mid = vec3(0.92, 0.96, 1.0);
	vec3 hi = vec3(0.92, 0.28, 0.10);
	return (t < 0.5) ? mix(lo, mid, t * 2.0) : mix(mid, hi, (t - 0.5) * 2.0);
}

void vertex() {
	uv = VERTEX.xz / plane_size + 0.5;
	vheight = sample_h(uv);
	VERTEX.y += vheight * displacement;
}

void fragment() {
	vec2 texel = 1.0 / tex_size;
	float hL = sample_h(uv - vec2(texel.x, 0.0));
	float hR = sample_h(uv + vec2(texel.x, 0.0));
	float hD = sample_h(uv - vec2(0.0, texel.y));
	float hU = sample_h(uv + vec2(0.0, texel.y));
	vec3 n = normalize(vec3((hL - hR) * normal_strength, 1.0, (hD - hU) * normal_strength));
	NORMAL = (VIEW_MATRIX * vec4(n, 0.0)).xyz;

	if (show_heightmap > 0.5) {
		ALBEDO = colormap(vheight * colormap_gain);
		ROUGHNESS = 0.8;
		METALLIC = 0.0;
	} else {
		ALBEDO = mix(deep_color, shallow_color, clamp(vheight * 0.5 + 0.5, 0.0, 1.0));
		ROUGHNESS = 0.06;      // low roughness -> Godot env gives the sky/fresnel reflection
		METALLIC = 0.0;
		SPECULAR = 0.6;
	}
}
