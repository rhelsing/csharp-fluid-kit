shader_type spatial;

// Stylized Water — ameye.dev (Étienne Meyer), ported STAGE BY STAGE from the URP
// Shader Graph asset (stylized-water.unitypackage). Each stage is a toggle below.
//   1 Gerstner surface (GerstnerWaves.hlsl)  2 depth fade  3 HSV shore color
//   (HSVLerp.hlsl)  4 foam  5 blended normals + screen refraction  6 toon lighting
// https://ameye.dev/notes/stylized-water-shader/

group_uniforms stages; // turn each stage on/off in the tuner
uniform bool enable_depth_fade = true;
uniform bool enable_shore_color = true;
uniform bool enable_foam = true;
uniform bool enable_normal_maps = true;
uniform bool enable_refraction = true;
uniform bool enable_lighting = true;

group_uniforms waves;
uniform float steepness : hint_range(0.0, 1.0, 0.01) = 0.15;
uniform float wavelength : hint_range(0.5, 40.0, 0.1) = 12.0;
uniform float wave_speed : hint_range(0.0, 4.0, 0.01) = 1.0;
uniform vec4 directions = vec4(0.0, 0.3, 0.55, 0.8);

group_uniforms color;
uniform vec3 water_color : source_color = vec3(0.11, 0.42, 0.60);
uniform vec3 shallow_color : source_color = vec3(0.40, 0.75, 0.72);
uniform float water_roughness : hint_range(0.0, 1.0, 0.01) = 0.12;
uniform float water_metallic : hint_range(0.0, 1.0, 0.01) = 0.0;
uniform float depth_fade_distance : hint_range(0.1, 20.0, 0.1) = 3.0;

group_uniforms foam;
uniform sampler2D foam_tex : repeat_enable, filter_linear;
uniform vec3 foam_color : source_color = vec3(1.0, 1.0, 1.0);
uniform float foam_distance : hint_range(0.0, 5.0, 0.01) = 0.9;
uniform float foam_scale : hint_range(0.0, 4.0, 0.01) = 0.5;
uniform float foam_speed : hint_range(0.0, 1.0, 0.01) = 0.04;
uniform float foam_crest : hint_range(0.0, 2.0, 0.01) = 0.5;

group_uniforms normals_refraction;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap;
uniform float normal_scale : hint_range(0.0, 2.0, 0.01) = 0.15;
uniform float normal_speed : hint_range(0.0, 1.0, 0.01) = 0.03;
uniform float normal_strength : hint_range(0.0, 2.0, 0.01) = 0.35;
uniform float refraction_strength : hint_range(0.0, 4.0, 0.01) = 1.0;

group_uniforms lighting;
uniform vec3 sun_direction = vec3(0.5, 0.6, 0.5); // to-light, set by the scene
uniform vec3 specular_color : source_color = vec3(1.0, 1.0, 0.95);
uniform float specular_smoothness : hint_range(0.0, 1.0, 0.01) = 0.7;
uniform float specular_hardness : hint_range(0.0, 1.0, 0.01) = 0.6;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;

varying vec3 world_pos;
varying vec3 wave_normal;
varying float wave_height;

// ---- Gerstner (GerstnerWaves.hlsl) ----
vec3 gerstner_wave(vec3 position, float steep, float wavelen, float spd, float direction, inout vec3 tangent, inout vec3 binormal) {
	direction = direction * 2.0 - 1.0;
	vec2 d = normalize(vec2(cos(3.14 * direction), sin(3.14 * direction)));
	float k = 2.0 * 3.14 / wavelen;
	float f = k * (dot(d, position.xz) - spd * TIME);
	float a = steep / k;
	tangent += vec3(-d.x * d.x * (steep * sin(f)), d.x * (steep * cos(f)), -d.x * d.y * (steep * sin(f)));
	binormal += vec3(-d.x * d.y * (steep * sin(f)), d.y * (steep * cos(f)), -d.y * d.y * (steep * sin(f)));
	return vec3(d.x * (a * cos(f)), a * sin(f), d.y * (a * cos(f)));
}

// ---- HSV lerp (HSVLerp.hlsl) ----
vec3 rgb2hsv(vec3 In) {
	vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
	vec4 P = mix(vec4(In.bg, K.wz), vec4(In.gb, K.xy), step(In.b, In.g));
	vec4 Q = mix(vec4(P.xyw, In.r), vec4(In.r, P.yzx), step(P.x, In.r));
	float D = Q.x - min(Q.w, Q.y);
	float eps = 1e-10;
	return vec3(abs(Q.z + (Q.w - Q.y) / (6.0 * D + eps)), D / (Q.x + eps), Q.x);
}
vec3 hsv2rgb(vec3 In) {
	vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
	vec3 P = abs(fract(In.xxx + K.xyz) * 6.0 - K.www);
	return In.z * mix(K.xxx, clamp(P - K.xxx, 0.0, 1.0), In.y);
}
vec3 hsv_lerp(vec3 a_rgb, vec3 b_rgb, float T) {
	vec3 A = rgb2hsv(a_rgb);
	vec3 B = rgb2hsv(b_rgb);
	float d = B.x - A.x;
	if (A.x > B.x) {
		float tmp = B.x; B.x = A.x; A.x = tmp;
		d = -d; T = 1.0 - T;
	}
	float hue = 0.0;
	if (d > 0.5) { A.x += 1.0; hue = mod(A.x + T * (B.x - A.x), 1.0); }
	if (d <= 0.5) hue = A.x + T * d;
	float sat = A.y + T * (B.y - A.y);
	float val = A.z + T * (B.z - A.z);
	return hsv2rgb(vec3(hue, sat, val));
}

void vertex() {
	vec3 pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 offset = vec3(0.0);
	vec3 tangent = vec3(1.0, 0.0, 0.0);
	vec3 binormal = vec3(0.0, 0.0, 1.0);
	offset += gerstner_wave(pos, steepness, wavelength, wave_speed, directions.x, tangent, binormal);
	offset += gerstner_wave(pos, steepness, wavelength, wave_speed, directions.y, tangent, binormal);
	offset += gerstner_wave(pos, steepness, wavelength, wave_speed, directions.z, tangent, binormal);
	offset += gerstner_wave(pos, steepness, wavelength, wave_speed, directions.w, tangent, binormal);
	VERTEX += offset;
	wave_normal = normalize(cross(binormal, tangent));
	NORMAL = wave_normal;
	world_pos = pos + offset;
	wave_height = offset.y;
}

void fragment() {
	// ---- depth / thickness ----
	float raw = texture(depth_tex, SCREEN_UV).x;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(vec3(SCREEN_UV * 2.0 - 1.0, raw), 1.0);
	float scene_depth = -vp.z / vp.w;
	float surface_depth = -VERTEX.z;
	float thickness = max(0.0, scene_depth - surface_depth);
	float df = enable_depth_fade ? clamp(thickness / depth_fade_distance, 0.0, 1.0) : 1.0;

	// ---- Stage 5: blended panning normal maps ----
	vec3 nrm = vec3(0.0, 0.0, 1.0);
	if (enable_normal_maps) {
		vec2 uv1 = world_pos.xz * normal_scale + TIME * normal_speed * vec2(1.0, 0.3);
		vec2 uv2 = world_pos.xz * normal_scale * 0.7 - TIME * normal_speed * vec2(0.3, 1.0);
		vec3 n1 = texture(normal_tex, uv1).xyz * 2.0 - 1.0;
		vec3 n2 = texture(normal_tex, uv2).xyz * 2.0 - 1.0;
		nrm = normalize(n1 + n2);
		NORMAL_MAP = nrm * 0.5 + 0.5;
		NORMAL_MAP_DEPTH = normal_strength;
	}

	// ---- Stage 5: screen refraction ----
	vec2 ref_off = enable_refraction
		? nrm.xy * refraction_strength * 0.04 * smoothstep(0.0, depth_fade_distance, thickness)
		: vec2(0.0);
	vec3 scene = texture(screen_tex, SCREEN_UV + ref_off).rgb;

	// ---- Stages 2/3: depth fade + (HSV) shore color ----
	vec3 shore = enable_shore_color
		? hsv_lerp(shallow_color, water_color, df)
		: mix(shallow_color, water_color, df);
	vec3 underwater = scene * shore;          // tint the refracted bed by shallow color
	vec3 col = mix(underwater, water_color, df); // shallow -> tinted bed, deep -> water color

	// ---- Stage 4: foam at shores + wave crests ----
	if (enable_foam) {
		float shore_foam = 1.0 - smoothstep(0.0, foam_distance, thickness);
		float crest_foam = smoothstep(foam_crest, foam_crest + 0.25, wave_height);
		float mask = clamp(shore_foam + crest_foam, 0.0, 1.0);
		float ftex = texture(foam_tex, world_pos.xz * foam_scale + TIME * foam_speed).r;
		float foam = mask * smoothstep(0.35, 0.7, ftex);
		col = mix(col, foam_color, clamp(foam, 0.0, 1.0));
	}

	ALBEDO = col;
	ROUGHNESS = water_roughness;
	METALLIC = water_metallic;

	// ---- Stage 6: toon specular (Lighting.hlsl, reconstructed) ----
	if (enable_lighting) {
		vec3 Nw = normalize(wave_normal);
		vec3 V = normalize(CAMERA_POSITION_WORLD - world_pos);
		vec3 L = normalize(sun_direction);
		vec3 H = normalize(L + V);
		float smooth_exp = exp2(10.0 * specular_smoothness + 1.0);
		float spec_soft = pow(max(dot(Nw, H), 0.0), smooth_exp);
		float spec_hard = smoothstep(0.005, 0.01, spec_soft);
		float spec = mix(spec_soft, spec_hard, specular_hardness);
		EMISSION += spec * specular_color;
	}
}
