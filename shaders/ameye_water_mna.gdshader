shader_type spatial;

// Scene 50 — ameye_water_composite.gdshader + the MNA reactive micro-layer
// (docs/mna-next-steps.md §2a): `h = gerstner + mna`. The CN stamp solver's height
// texture (a Texture2DRD) covers a world-anchored window [mna_origin .. +mna_window];
// its height is ADDED to the Gerstner sum in vertex() and its per-pixel gradient is
// folded into the blended normals (the 400 m plane's vertices are far coarser than
// the 256² sim cells, so the ripple detail lives in the normals + refraction, like
// the panning normal maps do). An edge fade melts the MNA contribution into pure
// Gerstner at the window border. Everything else is byte-identical to the composite
// shader. https://ameye.dev/notes/stylized-water-shader/

group_uniforms stages; // turn each stage on/off in the tuner
uniform bool enable_depth_fade = true;
uniform bool enable_shore_color = true;
uniform bool enable_foam = true;
uniform bool enable_normal_maps = true;
uniform bool enable_refraction = true;
uniform bool enable_lighting = true;

group_uniforms waves;
uniform float wave_time = 0.0;
const int MAX_WAVES = 16;
uniform int wave_count = 0;
uniform float wavelengths[MAX_WAVES];
uniform float steepnesses[MAX_WAVES];
uniform float speeds[MAX_WAVES];
uniform float dirs[MAX_WAVES];

group_uniforms mna; // reactive CN-MNA micro-layer (docs/mna-next-steps.md)
uniform sampler2D mna_tex : filter_linear, repeat_enable; // toroidal: hardware wrap does the mod
uniform vec2 mna_tex_size = vec2(256.0);
uniform vec2 mna_origin = vec2(-24.0, -24.0); // world XZ of the sim window's min corner
uniform vec2 mna_tex_offset = vec2(0.0);      // toroidal scroll offset (uv units)
uniform float mna_window = 48.0;              // world metres the window covers
uniform float mna_displacement = 1.0;         // world height per unit of sim h
uniform float mna_normal_strength = 3.0;      // per-pixel gradient gain
uniform float mna_edge_fade = 0.15;           // fade band (fraction of window)
uniform float mna_debug = 0.0;                // 1 = divergent height colormap overlay
uniform float mna_debug_gain = 10.0;          // height -> colormap scale
uniform sampler2D mna_foam_tex : filter_linear, repeat_enable; // v1 foam buffer (accumulate+decay)
uniform float mna_foam_intensity = 1.0;       // v1 overlay strength
uniform bool foam1_enabled = true;            // foam generations are ADDITIVE layers (docs/scene50-foam.md)
uniform bool foam2_enabled = true;
uniform sampler2D mna_foam2_tex : filter_linear, repeat_enable; // v2 cascade: R whitecap · G lace · B milk
uniform float foam2_intensity = 1.0;
uniform float foam2_roughness : hint_range(0.0, 1.0) = 0.7;  // foam is optically snow, not water
uniform float foam2_puff = 0.06;              // vertex lift under dense foam (m)
uniform float foam2_erosion = 0.65;           // edge demolition on thin foam
uniform float foam2_erosion_scale = 0.35;     // world-space noise scale
uniform float foam2_milk = 0.5;               // subsurface bubble-haze strength
uniform vec3 foam2_tint : source_color = vec3(0.93, 0.97, 1.0);

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
varying vec2 mna_uv;
varying float mna_fade;

// ---- foam v2 erosion noise: 2-octave value noise (procedural — no texture body) ----
float f2_hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}
float f2_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = f2_hash(i);
	float b = f2_hash(i + vec2(1.0, 0.0));
	float c = f2_hash(i + vec2(0.0, 1.0));
	float d = f2_hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// ---- Gerstner (GerstnerWaves.hlsl) ----
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
	// COMPOSITE: sum N bands (matches composite_gerstner.gd) instead of 4 fixed waves.
	for (int i = 0; i < wave_count && i < MAX_WAVES; i++) {
		offset += gerstner_wave(pos, steepnesses[i], wavelengths[i], speeds[i], dirs[i], tangent, binormal);
	}
	// MNA micro-layer: the reactive sim height rides the Gerstner sum (h = macro + mna),
	// faded to 0 at the window border so it melts into pure Gerstner. Sampled at the
	// DISPLACED position (pos + gerstner xz) — sampling at the rest position draws the
	// ripples up to ~2 m off the visual surface (the bands' sideways sway).
	mna_uv = (pos.xz + offset.xz - mna_origin) / mna_window;
	vec2 e = min(mna_uv, 1.0 - mna_uv);
	mna_fade = smoothstep(0.0, mna_edge_fade, min(e.x, e.y));
	if (mna_fade > 0.0) {
		offset.y += texture(mna_tex, mna_uv + mna_tex_offset).r * mna_displacement * mna_fade;
		// foam v2: dense foam puffs the surface up slightly so light breaks over it
		if (foam2_enabled && foam2_puff > 0.0) {
			vec2 f2v = texture(mna_foam2_tex, mna_uv + mna_tex_offset).rg;
			offset.y += foam2_puff * min(f2v.r + f2v.g * 0.5, 1.5) * mna_fade;
		}
	}
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

	// ---- MNA per-pixel gradient (the sim cells are far finer than the mesh verts,
	// so the reactive ripple detail reads through the normals + refraction) ----
	vec2 mna_slope = vec2(0.0);
	float mna_h = 0.0;
	vec2 tuv = mna_uv + mna_tex_offset;   // toroidal: repeat_enable wraps the taps
	if (mna_fade > 0.001) {
		vec2 texel = 1.0 / mna_tex_size;
		float hL = texture(mna_tex, tuv - vec2(texel.x, 0.0)).r;
		float hR = texture(mna_tex, tuv + vec2(texel.x, 0.0)).r;
		float hD = texture(mna_tex, tuv - vec2(0.0, texel.y)).r;
		float hU = texture(mna_tex, tuv + vec2(0.0, texel.y)).r;
		mna_h = texture(mna_tex, tuv).r;
		mna_slope = vec2(hL - hR, hD - hU) * mna_normal_strength * mna_fade;
	}

	// ---- Stage 5: blended panning normal maps (+ MNA gradient) ----
	vec3 nrm = vec3(0.0, 0.0, 1.0);
	if (enable_normal_maps) {
		vec2 uv1 = world_pos.xz * normal_scale + TIME * normal_speed * vec2(1.0, 0.3);
		vec2 uv2 = world_pos.xz * normal_scale * 0.7 - TIME * normal_speed * vec2(0.3, 1.0);
		vec3 n1 = texture(normal_tex, uv1).xyz * 2.0 - 1.0;
		vec3 n2 = texture(normal_tex, uv2).xyz * 2.0 - 1.0;
		nrm = normalize(vec3(n1.xy + n2.xy + mna_slope, n1.z + n2.z));
		NORMAL_MAP = nrm * 0.5 + 0.5;
		NORMAL_MAP_DEPTH = normal_strength;
	} else if (mna_fade > 0.001) {
		nrm = normalize(vec3(mna_slope, 1.0));
		NORMAL_MAP = nrm * 0.5 + 0.5;
		NORMAL_MAP_DEPTH = 1.0;
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

	// ---- Foam v1: the raw accumulate+decay buffer as flat white (frozen baseline) ----
	if (foam1_enabled && mna_fade > 0.001 && mna_foam_intensity > 0.0) {
		float mf = texture(mna_foam_tex, tuv).r * mna_fade * mna_foam_intensity;
		float mfoam = smoothstep(0.08, 0.6, mf);
		col = mix(col, foam_color, mfoam);
	}

	// ---- Foam v2 (docs/scene50-foam.md): subsurface milk haze, then the whitecap+lace
	// body carved by subtract-then-threshold erosion (dense foam solid, thin foam
	// perforates, dying patches tear into streaks) — material response via f2a below ----
	float f2a = 0.0;
	if (foam2_enabled && mna_fade > 0.001) {
		vec4 f2 = texture(mna_foam2_tex, tuv);
		float milk = clamp(f2.b * foam2_milk, 0.0, 1.0) * mna_fade;
		col = mix(col, vec3(0.75, 0.86, 0.90), milk * 0.5);
		float body = (f2.r + f2.g * 0.7) * foam2_intensity * mna_fade;
		vec2 np = world_pos.xz * foam2_erosion_scale;
		float ero = f2_noise(np) * 0.65 + f2_noise(np * 3.1 + 17.0) * 0.35;
		float carved = body - ero * foam2_erosion * clamp(1.0 - body, 0.0, 1.0);
		f2a = smoothstep(0.05, 0.45, carved);
		float fresh = clamp(f2.r * 2.0, 0.0, 1.0);       // whitecap share = brightness
		col = mix(col, foam2_tint * mix(0.82, 1.0, fresh), f2a);
	}

	// ---- MNA debug: divergent height colormap over the window (blue - white - red) ----
	if (mna_debug > 0.5) {
		float t = clamp(mna_h * mna_debug_gain * 0.5 + 0.5, 0.0, 1.0);
		vec3 dm = (t < 0.5)
			? mix(vec3(0.10, 0.22, 0.85), vec3(0.92, 0.96, 1.0), t * 2.0)
			: mix(vec3(0.92, 0.96, 1.0), vec3(0.92, 0.28, 0.10), (t - 0.5) * 2.0);
		col = mix(col, dm, mna_fade);
	}

	ALBEDO = col;
	ROUGHNESS = mix(water_roughness, foam2_roughness, f2a);   // the surface stops being wet under foam
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
