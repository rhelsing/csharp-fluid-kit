shader_type spatial;
render_mode depth_draw_always, cull_back, specular_schlick_ggx;

// ---------------------------------------------------------------------------
// Shallow-water shore surface.
// Displaces a 608x608-vertex PlaneMesh (30.4 x 30.4 m, world X,Z in [0,30.4])
// with the sim heightfield and shades refraction, Beer-Lambert absorption,
// aeration milkiness and eroded foam lace.
// Renderer: Forward+ (Texture2DRD sources; Vulkan reverse-Z depth assumed).
// All sim textures may be black for the first frames; every path below is
// guarded so black input renders a flat, invisible surface (no NaN/inf).
// ---------------------------------------------------------------------------

uniform float DOMAIN_SIZE = 30.4; // metres covered by the sim grid
uniform float GRID_RES = 608.0;   // sim cells per side

// --- deep-water visual swell (Gerstner-style, vertical only) ---
// The sim carries the real long waves; this layer adds deep-water surface
// character where h is large, and fades out completely near shores so the
// solver's own swash physics is untouched. amp 0 = off (beach scenario).
uniform float gerstner_amp : hint_range(0.0, 3.0) = 0.0;
uniform vec3 deep_scatter = vec3(0.018, 0.075, 0.11); // open-ocean blue
uniform float detail_base : hint_range(0.0, 1.0) = 0.0;   // ripples even in still water (ocean)
// 0 = finite-difference lace ripples (beach, 6 taps); 1 = premade ripple
// normal texture (2 taps, cheaper and crisper at ocean scale)
uniform float detail_use_tex : hint_range(0.0, 1.0) = 0.0;
uniform sampler2D detail_normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float foam_breakup : hint_range(0.0, 1.0) = 0.0; // tears smooth foam fields into patches (0 = off, beach look unchanged)
uniform float breakup_scale : hint_range(1.0, 30.0) = 4.5; // metres per breakup cell
uniform float tuck_h : hint_range(0.001, 0.2) = 0.004;    // shoreline blend depth (scale with dx)

// four components: direction (radians around +X), wavelength (m), rel. amp
const vec4 GW_ANG = vec4(0.0, 0.42, -0.35, 0.19);
const vec4 GW_LEN = vec4(63.0, 38.0, 21.0, 13.0);
const vec4 GW_AMP = vec4(0.45, 0.28, 0.17, 0.10);

// returns height; accumulates d(height)/d(xz) into grad
float gerstner_y(vec2 wxz, float t, inout vec2 grad) {
	float y = 0.0;
	// unrolled; dynamic vec4 indexing is rejected on some pipeline paths
	{
		vec2 d = vec2(cos(GW_ANG.x), sin(GW_ANG.x));
		float k = 6.28318530718 / GW_LEN.x;
		float ph = k * dot(d, wxz) - sqrt(9.81 * k) * t;
		float a = GW_AMP.x * gerstner_amp;
		y += a * cos(ph);
		grad += -a * k * sin(ph) * d;
	}
	{
		vec2 d = vec2(cos(GW_ANG.y), sin(GW_ANG.y));
		float k = 6.28318530718 / GW_LEN.y;
		float ph = k * dot(d, wxz) - sqrt(9.81 * k) * t;
		float a = GW_AMP.y * gerstner_amp;
		y += a * cos(ph);
		grad += -a * k * sin(ph) * d;
	}
	{
		vec2 d = vec2(cos(GW_ANG.z), sin(GW_ANG.z));
		float k = 6.28318530718 / GW_LEN.z;
		float ph = k * dot(d, wxz) - sqrt(9.81 * k) * t;
		float a = GW_AMP.z * gerstner_amp;
		y += a * cos(ph);
		grad += -a * k * sin(ph) * d;
	}
	{
		vec2 d = vec2(cos(GW_ANG.w), sin(GW_ANG.w));
		float k = 6.28318530718 / GW_LEN.w;
		float ph = k * dot(d, wxz) - sqrt(9.81 * k) * t;
		float a = GW_AMP.w * gerstner_amp;
		y += a * cos(ph);
		grad += -a * k * sin(ph) * d;
	}
	return y;
}

// --- simulation inputs (Texture2DRD) ---
uniform sampler2D tx_state : filter_linear, repeat_disable;   // r=w  g=hu  b=hv  a=hc
uniform sampler2D tx_bottom : filter_linear, repeat_disable;  // r=B  g,b=faces  a=nb max
uniform sampler2D tx_derived : filter_linear, repeat_disable; // r=n.x g=n.z b=foamVis a=aeration

// --- static art inputs (tileable, data textures: no source_color) ---
uniform sampler2D lace_tex : filter_linear_mipmap, repeat_enable;       // Voronoi F2-F1 grayscale
uniform sampler2D flow_noise_tex : filter_linear_mipmap, repeat_enable; // RG phase noise

// --- screen access ---
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

// --- tunables ---
uniform vec3 absorption = vec3(1.8, 0.55, 0.9);       // Beer-Lambert, per metre (murky green-teal)
uniform vec3 scatter_color = vec3(0.045, 0.21, 0.155); // in-scatter tint
uniform vec3 foam_albedo = vec3(0.85);               // sensible range 0.6 - 0.9
uniform float refraction_strength : hint_range(0.0, 0.3) = 0.06;
uniform float detail_normal_strength : hint_range(0.0, 2.0) = 0.32;
uniform float detail_scale : hint_range(0.05, 2.0) = 0.4;      // metres per ripple tile
uniform float lace_scale_coarse : hint_range(0.1, 10.0) = 1.2; // metres per coarse hole
uniform float lace_scale_fine : hint_range(0.01, 2.0) = 0.12;  // metres, fine filaments
uniform float flow_speed_scale : hint_range(0.0, 4.0) = 1.0;
uniform float flow_period : hint_range(0.2, 10.0) = 2.0;       // seconds per flow cycle

varying vec3 v_world_pos;
varying float v_h;
varying vec2 v_gw_grad;   // gerstner surface gradient (for fragment normals)
varying float v_gw_fade;  // deep-water fade for the gerstner layer

// Uncharted-style double-phase flow sample of lace_tex.
// wxz: world XZ (m), scale_m: metres per texture tile,
// disp_m: capped flow displacement (m), t: phase time (cycles).
float flow_lace(vec2 wxz, float scale_m, vec2 disp_m, float t) {
	float p0 = fract(t);
	float p1 = fract(t + 0.5);
	float blend = abs(2.0 * p0 - 1.0); // 1 when layer 0 resets, 0 when layer 1 resets
	vec2 uv0 = (wxz - disp_m * (p0 - 0.5)) / max(scale_m, 1e-4);
	vec2 uv1 = (wxz - disp_m * (p1 - 0.5)) / max(scale_m, 1e-4);
	return mix(texture(lace_tex, uv0).r, texture(lace_tex, uv1).r, blend);
}

// Linear (view-space) depth from a raw depth-buffer value. Vulkan NDC, reverse-Z.
float linear_depth(float raw_depth, vec2 uv, mat4 inv_proj) {
	vec4 v = inv_proj * vec4(uv * 2.0 - 1.0, raw_depth, 1.0);
	return -v.z / max(v.w, 1e-6);
}

void vertex() {
	// Plane vertices sit on texel centres of the 608x608 grid: half-texel correction.
	vec2 suv = (UV * (GRID_RES - 1.0) + 0.5) / GRID_RES;
	float w = textureLod(tx_state, suv, 0.0).r;
	float bed = textureLod(tx_bottom, suv, 0.0).r;
	float h = max(w - bed, 0.0);
	// Dry cells tuck under the sand; wet cells ride the free surface.
	VERTEX.y = mix(bed - 0.04, w, smoothstep(0.0, tuck_h, h));
	// World position BEFORE the visual swell: sim sampling must stay exact.
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_h = h;
	v_gw_grad = vec2(0.0);
	v_gw_fade = 0.0;
	if (gerstner_amp > 0.001) {
		// fade out approaching shores (h < 9 m) and inside aerated surf
		v_gw_fade = smoothstep(3.0, 9.0, h);
		vec2 grad = vec2(0.0);
		float gy = gerstner_y(v_world_pos.xz, TIME, grad);
		VERTEX.y += gy * v_gw_fade;
		// gradient recomputed per-pixel in fragment(); varying kept for cheap paths
		v_gw_grad = grad * v_gw_fade;
	}
}

void fragment() {
	// World-position-based sim UV: exact, no mesh-UV interpolation error.
	vec2 suv = clamp(v_world_pos.xz / DOMAIN_SIZE, vec2(0.0), vec2(1.0));

	vec4 st = texture(tx_state, suv);
	float bed = texture(tx_bottom, suv).r;
	float h = max(st.r - bed, 0.0);
	vec4 der = texture(tx_derived, suv);
	float foam_vis = clamp(der.b, 0.0, 1.0);
	float aeration = clamp(der.a, 0.0, 1.0);

	// Shoreline convergence: as h -> 0 refraction offset, fresnel and
	// absorption all fade so the fragment equals the background pixel.
	float shore = smoothstep(0.0, 0.02, h);

	// ---- base normal from the sim (world space) ----
	vec2 nxz = clamp(der.rg, -1.0, 1.0);
	float ny = sqrt(max(1.0 - dot(nxz, nxz), 0.0));
	// Black tx_derived gives (0,0) -> straight up; max() keeps normalize() safe.
	vec3 n_world = normalize(vec3(nxz.x, max(ny, 0.05), nxz.y));
	// visual swell tilts the normal in deep water only; per-pixel gradient
	// keeps the short components crisp (per-vertex smears them)
	if (v_gw_fade > 0.001) {
		vec2 gw_grad = vec2(0.0);
		gerstner_y(v_world_pos.xz, TIME, gw_grad);
		n_world = normalize(vec3(n_world.x - gw_grad.x * v_gw_fade, n_world.y, n_world.z - gw_grad.y * v_gw_fade));
	}

	// ---- flow field for lace / detail advection ----
	vec2 vel = st.gb / max(h, 0.02); // live velocity, m/s
	vec2 disp = vel * flow_speed_scale * flow_period * 0.5;
	float dlen = length(disp);
	disp *= min(dlen, 0.35) / max(dlen, 1e-5); // cap displacement so lace never smears

	vec2 pn = texture(flow_noise_tex, v_world_pos.xz / 9.7).rg; // per-pixel phase offsets
	float t_flow = TIME / max(flow_period, 1e-3) + pn.r;

	// ---- detail ripple normal: finite-differenced lace, flow-advected ----
	float spd = length(st.gb) / max(h, 0.05);
	float det_amt = detail_normal_strength * clamp(detail_base + spd * 0.7, 0.0, 1.0);
	if (detail_use_tex > 0.5) {
		// cheap path: premade anisotropic ripple normal, flow-advected (2 taps)
		float p0 = fract(t_flow);
		float p1 = fract(t_flow + 0.5);
		float blend = abs(2.0 * p0 - 1.0);
		float dscale = max(detail_scale * 6.0, 0.5); // texture holds multi-octave ripples
		vec3 t0 = texture(detail_normal_tex, (v_world_pos.xz - disp * (p0 - 0.5)) / dscale).rgb;
		vec3 t1 = texture(detail_normal_tex, (v_world_pos.xz - disp * (p1 - 0.5)) / dscale).rgb;
		vec3 dn = mix(t0, t1, blend) * 2.0 - 1.0;
		n_world = normalize(vec3(n_world.x + dn.x * det_amt, n_world.y, n_world.z + dn.y * det_amt));
	} else {
		float eps = max(detail_scale * 0.1, 1e-4);
		float d_c = flow_lace(v_world_pos.xz, detail_scale, disp, t_flow);
		float d_x = flow_lace(v_world_pos.xz + vec2(eps, 0.0), detail_scale, disp, t_flow);
		float d_z = flow_lace(v_world_pos.xz + vec2(0.0, eps), detail_scale, disp, t_flow);
		vec2 grad = vec2(d_x - d_c, d_z - d_c) / eps; // d(lace)/d(metre)
		grad *= 0.02 * det_amt;                       // lace read as a ~2 cm bump field
		n_world = normalize(vec3(n_world.x - grad.x, n_world.y, n_world.z - grad.y));
	}

	NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);

	// ---- refraction ----
	float frag_ld = -VERTEX.z; // view-space depth of this water fragment
	float scene_ld = linear_depth(texture(depth_tex, SCREEN_UV).x, SCREEN_UV, INV_PROJECTION_MATRIX);
	float thickness = clamp(scene_ld - frag_ld, 0.0, 100.0);

	vec2 ruv = SCREEN_UV + NORMAL.xy * refraction_strength * clamp(thickness, 0.0, 1.0) * shore;
	ruv = clamp(ruv, vec2(0.001), vec2(0.999));

	// Depth-test the refracted tap: geometry in front of the water must not smear.
	float refr_ld = linear_depth(texture(depth_tex, ruv).x, ruv, INV_PROJECTION_MATRIX);
	if (refr_ld < frag_ld - 0.01) {
		ruv = SCREEN_UV;
		refr_ld = scene_ld;
	}
	thickness = clamp(refr_ld - frag_ld, 0.0, 100.0);

	// Aeration blurs the transmitted image (mip bias); scaled by shore so the
	// waterline still converges exactly to the unblurred background pixel.
	vec3 refr_rgb = textureLod(screen_tex, ruv, aeration * 4.0 * shore).rgb;

	// ---- Beer-Lambert absorption + aeration milkiness ----
	vec3 trans = exp(-absorption * thickness * 1.35);
	trans *= exp(-aeration * 8.0 * thickness); // submerged bubbles kill transmission fast
	// Aerated in-scatter shifts toward pale green-cyan, never white.
	vec3 base_scat = mix(scatter_color, deep_scatter, smoothstep(6.0, 16.0, h));
	vec3 scat = mix(base_scat, vec3(0.55, 0.80, 0.72), aeration * 0.7);
	trans = mix(vec3(1.0), trans, shore); // h -> 0: fully transparent
	vec3 water_col = refr_rgb * trans + scat * (1.0 - trans);

	// ---- foam lace ----
	// Gate by depth BEFORE erosion: the foam line hugs the swash tongue tip
	// but never renders on dry sand.
	// pow-shaping opens lace holes earlier: mid-level foam reads as lace, not sheet
	float fv = pow(foam_vis, 1.5) * smoothstep(0.0005, 0.006, h);
	// tear smooth foam fields into patches (vital when the sim grid is coarse:
	// the foam AMOUNT is low-frequency there, the pattern must not be)
	fv *= 1.0 - foam_breakup * (1.0 - flow_lace(v_world_pos.xz, breakup_scale, disp, t_flow * 0.37));
	float lace_c = flow_lace(v_world_pos.xz, lace_scale_coarse, disp, t_flow);
	float lace_f = flow_lace(v_world_pos.xz, lace_scale_fine, disp, t_flow + pn.g);
	float lace = 0.72 * lace_c + 0.28 * lace_f;
	// Black-point erosion: holes open first, filaments die last, no UV animation needed.
	float foam_mask = smoothstep(1.0 - fv, 1.0 - fv + 0.38, lace) * 0.94;

	// ---- composite ----
	float fres = 0.02 + 0.98 * pow(1.0 - clamp(dot(VIEW, NORMAL), 0.0, 1.0), 5.0);
	fres *= shore * (1.0 - foam_mask); // foam is diffuse-only; shoreline kills fresnel

	EMISSION = water_col * (1.0 - fres) * (1.0 - foam_mask);
	ALBEDO = foam_albedo * foam_mask;
	SPECULAR = 0.25;
	ROUGHNESS = mix(0.03, 0.85, max(foam_mask, aeration * 0.5));
	// ALPHA stays 1.0: shoreline blending is done by converging to the
	// background pixel, not by alpha fading (depth_draw_always).
}
