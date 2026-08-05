shader_type spatial;

// REACTIVE VARIANT of ameye_water_boat.gdshader (scene 17). Identical stylized water,
// plus a world-anchored MNA ripple layer: `ripple_tex` (a Texture2DRD from a GPU
// Crank-Nicolson wave grid — scripts/lib/ReactiveWaveField.cs) is sampled by WORLD
// position over the [ripple_origin, ripple_origin+ripple_size] window and ADDED to the
// Gerstner height, with the normal perturbed by its slope. ripple_gain = 0 → pure ameye
// (the macro is untouched). The boat pokes the field → real wakes ride the swell.
// https://ameye.dev/notes/stylized-water-shader/

group_uniforms stages;
uniform bool enable_depth_fade = true;
uniform bool enable_shore_color = true;
uniform bool enable_foam = true;
uniform bool enable_normal_maps = true;
uniform bool enable_refraction = true;
uniform bool enable_lighting = true;

group_uniforms waves;
uniform float wave_time = 0.0;
uniform float steepness : hint_range(0.0, 1.0, 0.01) = 0.15;
uniform float wavelength : hint_range(0.5, 40.0, 0.1) = 12.0;
uniform float wave_speed : hint_range(0.0, 4.0, 0.01) = 1.0;
uniform vec4 directions = vec4(0.0, 0.3, 0.55, 0.8);

group_uniforms reactive; // world-anchored MNA ripple window
uniform sampler2D ripple_tex : filter_linear, repeat_disable;
uniform vec2 ripple_origin = vec2(0.0);   // window min corner (world x,z)
uniform float ripple_size = 80.0;         // window extent (world units, square)
uniform float ripple_gain = 0.0;          // 0 = pure Gerstner
uniform float ripple_texel = 0.00390625;  // 1 / grid resolution (for the slope)
uniform float ripple_edge_fade = 0.08;    // border fade width (0..0.5 of the window) = soft sponge
uniform sampler2D wake_foam_tex : filter_linear, repeat_disable; // reactive foam buffer (accumulate+decay)
uniform float wake_foam_intensity = 0.0;  // 0 = wake-foam overlay off (optional layer)

group_uniforms foam_v2; // v2 = v1 + toggleable extras; v2 off → exact v1 path
uniform bool wake_foam_v2 = false;
uniform bool foam_matte = true;           // kill gloss where foam sits (foam is diffuse)
uniform float foam_matte_rough : hint_range(0.3, 1.0) = 0.9;
uniform bool foam_edge_normals = true;    // raised-lip normals from the foam buffer gradient
uniform float foam_normal_strength : hint_range(0.0, 8.0) = 3.0;
uniform bool foam_age_bands = true;       // foam value = age → old foam laces into streaks
uniform float foam_band_count : hint_range(1.0, 8.0) = 3.0;
uniform float foam_band_soft : hint_range(0.05, 1.0) = 0.35;
uniform bool foam_churn = true;           // smear sampling along the live ripple slope
uniform float foam_churn_amount : hint_range(0.0, 0.05) = 0.01;
uniform bool foam_micro_normal = false;   // ameye normal texture re-scoped inside the foam
uniform float foam_micro_scale : hint_range(0.2, 4.0) = 1.2;
uniform float foam_micro_strength : hint_range(0.0, 2.0) = 0.5;
uniform bool foam_puff = false;           // dense foam lifts the surface (silhouette thickness)
uniform float foam_puff_amount : hint_range(0.0, 0.25) = 0.06;

group_uniforms whitecaps; // macro-layer foam: Gerstner folding test (independent of the wake)
uniform bool enable_whitecaps = false;
uniform float whitecap_start : hint_range(0.0, 1.0) = 0.75;   // J below this → capping
uniform float whitecap_feather : hint_range(0.01, 0.5) = 0.15;
uniform float whitecap_intensity : hint_range(0.0, 1.0) = 0.8;

group_uniforms shore_band; // depth-based beach lap (reuses tuned foam_distance for reach)
uniform bool enable_shore_band = false;
uniform float shore_band_intensity : hint_range(0.0, 1.0) = 0.8;

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
uniform vec3 sun_direction = vec3(0.5, 0.6, 0.5);
uniform vec3 specular_color : source_color = vec3(1.0, 1.0, 0.95);
uniform float specular_smoothness : hint_range(0.0, 1.0, 0.01) = 0.7;
uniform float specular_hardness : hint_range(0.0, 1.0, 0.01) = 0.6;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;

varying vec3 world_pos;
varying vec3 wave_normal;
varying float wave_height;
varying float ripple_h; // reactive height at this vertex (for wake foam)
varying vec2 ripple_uv;    // window UV at this vertex (wake-foam sampling)
varying float ripple_fade; // window edge fade, 0 outside the window
varying float gerst_j;     // horizontal Jacobian det of the Gerstner sum (1 = flat, →0 = crest folding)

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
	if (A.x > B.x) { float tmp = B.x; B.x = A.x; A.x = tmp; d = -d; T = 1.0 - T; }
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
	wave_normal = normalize(cross(binormal, tangent));
	gerst_j = tangent.x * binormal.z - tangent.z * binormal.x; // folding test (Tessendorf whitecaps)

	// --- reactive MNA ripple layer (world-anchored window, faded at the border) ---
	ripple_uv = (pos.xz - ripple_origin) / ripple_size;
	vec2 re = min(ripple_uv, vec2(1.0) - ripple_uv);
	ripple_fade = smoothstep(0.0, ripple_edge_fade, min(re.x, re.y)); // melt into pure Gerstner at edges
	ripple_h = 0.0;
	if (ripple_gain != 0.0 && ripple_fade > 0.0) {
		vec2 ruv = ripple_uv;
		float g = ripple_gain * ripple_fade;
		float t = ripple_texel;
		float hl = texture(ripple_tex, ruv - vec2(t, 0.0)).r;
		float hr = texture(ripple_tex, ruv + vec2(t, 0.0)).r;
		float hd = texture(ripple_tex, ruv - vec2(0.0, t)).r;
		float hu = texture(ripple_tex, ruv + vec2(0.0, t)).r;
		ripple_h = clamp(texture(ripple_tex, ruv).r * g, -3.0, 3.0); // safety clamp: a spike can't nuke the mesh
		offset.y += ripple_h;
		float cs = ripple_size * t; // world distance between sampled texels
		wave_normal = normalize(wave_normal + vec3((hl - hr) * g / (2.0 * cs), 0.0, (hd - hu) * g / (2.0 * cs)));
	}

	// foam v2 · puff: dense foam lifts the surface a touch
	if (wake_foam_v2 && foam_puff && ripple_fade > 0.0) {
		float pf = texture(wake_foam_tex, ripple_uv).r * ripple_fade * wake_foam_intensity;
		offset.y += foam_puff_amount * smoothstep(0.08, 0.6, pf);
	}

	VERTEX += offset;
	NORMAL = wave_normal;
	world_pos = pos + offset;
	wave_height = offset.y;
}

void fragment() {
	float raw = texture(depth_tex, SCREEN_UV).x;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(vec3(SCREEN_UV * 2.0 - 1.0, raw), 1.0);
	float scene_depth = -vp.z / vp.w;
	float surface_depth = -VERTEX.z;
	float thickness = max(0.0, scene_depth - surface_depth);
	float df = enable_depth_fade ? clamp(thickness / depth_fade_distance, 0.0, 1.0) : 1.0;

	vec3 nrm = vec3(0.0, 0.0, 1.0);
	if (enable_normal_maps) {
		vec2 uv1 = world_pos.xz * normal_scale + TIME * normal_speed * vec2(1.0, 0.3);
		vec2 uv2 = world_pos.xz * normal_scale * 0.7 - TIME * normal_speed * vec2(0.3, 1.0);
		vec3 n1 = texture(normal_tex, uv1).xyz * 2.0 - 1.0;
		vec3 n2 = texture(normal_tex, uv2).xyz * 2.0 - 1.0;
		nrm = normalize(n1 + n2);
		// NORMAL_MAP written at the end (foam v2 may blend its own normals in)
	}

	vec2 ref_off = enable_refraction
		? nrm.xy * refraction_strength * 0.04 * smoothstep(0.0, depth_fade_distance, thickness)
		: vec2(0.0);
	vec3 scene = texture(screen_tex, SCREEN_UV + ref_off).rgb;

	vec3 shore = enable_shore_color
		? hsv_lerp(shallow_color, water_color, df)
		: mix(shallow_color, water_color, df);
	vec3 underwater = scene * shore;
	vec3 col = mix(underwater, water_color, df);

	if (enable_foam) {
		float shore_foam = 1.0 - smoothstep(0.0, foam_distance, thickness);
		float crest_foam = smoothstep(foam_crest, foam_crest + 0.25, wave_height);
		float wake_foam = smoothstep(0.12, 0.4, abs(ripple_h)); // reactive wake foam
		float mask = clamp(shore_foam + crest_foam + wake_foam, 0.0, 1.0);
		float ftex = texture(foam_tex, world_pos.xz * foam_scale + TIME * foam_speed).r;
		float foam = mask * smoothstep(0.35, 0.7, ftex);
		col = mix(col, foam_color, clamp(foam, 0.0, 1.0));
	}

	// ---- MNA-reactive wake foam v1 (ported from scene 50): wake/ripple curvature
	// accumulated + decayed in a buffer, drawn RAW — no foam-texture masking (the
	// cellular pattern read as a doily, not wake foam); shape/breakup comes from
	// the sim itself. v2 adds toggleable extras; v2 off = exact v1. ----
	float mfoam = 0.0;
	vec3 foam_nrm = vec3(0.0);
	bool foam_nrm_on = false;
	if (ripple_fade > 0.001 && wake_foam_intensity > 0.0) {
		vec2 fuv = ripple_uv;
		if (wake_foam_v2 && foam_churn) {
			// churn: smear sampling along the live ripple slope — deterministic,
			// self-animating (the slope field IS the moving wake)
			float ct = ripple_texel;
			float chl = texture(ripple_tex, fuv - vec2(ct, 0.0)).r;
			float chr = texture(ripple_tex, fuv + vec2(ct, 0.0)).r;
			float chd = texture(ripple_tex, fuv - vec2(0.0, ct)).r;
			float chu = texture(ripple_tex, fuv + vec2(0.0, ct)).r;
			fuv += vec2(chr - chl, chu - chd) * foam_churn_amount * 10.0;
		}
		float mf = texture(wake_foam_tex, fuv).r * ripple_fade * wake_foam_intensity;
		mfoam = smoothstep(0.08, 0.6, mf);
		if (wake_foam_v2 && foam_age_bands) {
			// value = age (decay): old thin foam breaks into streak bands as it
			// fades; fresh dense foam stays solid — lace from physics, not texture
			float band = smoothstep(0.5 - foam_band_soft * 0.5, 0.5 + foam_band_soft * 0.5,
				fract(mf * foam_band_count));
			mfoam *= mix(band, 1.0, mfoam);
		}
		col = mix(col, foam_color, mfoam);
		if (wake_foam_v2 && mfoam > 0.001) {
			if (foam_edge_normals) {
				float ft = ripple_texel;
				float ffl = texture(wake_foam_tex, fuv - vec2(ft, 0.0)).r;
				float ffr = texture(wake_foam_tex, fuv + vec2(ft, 0.0)).r;
				float ffd = texture(wake_foam_tex, fuv - vec2(0.0, ft)).r;
				float ffu = texture(wake_foam_tex, fuv + vec2(0.0, ft)).r;
				foam_nrm += vec3(ffl - ffr, ffd - ffu, 0.0) * foam_normal_strength;
				foam_nrm_on = true;
			}
			if (foam_micro_normal) {
				vec3 n3 = texture(normal_tex, world_pos.xz * foam_micro_scale + TIME * 0.02).xyz * 2.0 - 1.0;
				foam_nrm += n3 * foam_micro_strength;
				foam_nrm_on = true;
			}
		}
	}

	// ---- macro whitecaps: crests whitecap where the Gerstner sum folds (J < start),
	// independent of the wake layer — the far-field foam story ----
	float wc = 0.0;
	if (enable_whitecaps) {
		wc = (1.0 - smoothstep(whitecap_start, whitecap_start + whitecap_feather, gerst_j)) * whitecap_intensity;
		col = mix(col, foam_color, wc);
	}

	// ---- shore band: depth-thickness foam lap hugging the beaches ----
	if (enable_shore_band) {
		float sb = 1.0 - smoothstep(0.0, foam_distance, thickness);
		float stex = texture(foam_tex, world_pos.xz * foam_scale + TIME * foam_speed).r;
		col = mix(col, foam_color, sb * smoothstep(0.35, 0.7, stex) * shore_band_intensity);
	}

	ALBEDO = col;
	float foam_cover = max(mfoam, wc); // matte applies to wake foam AND whitecaps
	ROUGHNESS = (wake_foam_v2 && foam_matte) ? mix(water_roughness, foam_matte_rough, foam_cover) : water_roughness;
	METALLIC = water_metallic;

	if (enable_normal_maps || foam_nrm_on) {
		vec3 out_nrm = enable_normal_maps ? nrm : vec3(0.0, 0.0, 1.0);
		if (foam_nrm_on) {
			out_nrm = normalize(mix(out_nrm, normalize(foam_nrm + vec3(0.0, 0.0, 1.0)), mfoam));
		}
		NORMAL_MAP = out_nrm * 0.5 + 0.5;
		NORMAL_MAP_DEPTH = enable_normal_maps ? normal_strength : 1.0;
	}

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
