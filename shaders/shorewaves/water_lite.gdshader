shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

// Lite water surface for scene 18 — deliberately cheap so the sim can run at high FPS.
// Displaces a plane by the KP07 free surface and shades with: depth-tinted color, a bit
// of Fresnel sky-tint, and a low-roughness specular shine. Normals are computed PER PIXEL
// from the height field with a widen-able finite-difference stencil (normal_smooth), which
// smooths the coarse sim grid without needing dense geometry. No screen textures, no foam,
// no Voronoi — nothing that scales with per-pixel cost beyond a handful of taps.

uniform sampler2D tx_state : filter_linear, repeat_disable;   // r = w (free surface)
uniform sampler2D tx_bottom : filter_linear, repeat_disable;  // r = B (bed)
uniform float DOMAIN_SIZE = 30.4;
uniform float GRID_RES = 608.0;
uniform float tuck_h : hint_range(0.001, 0.2) = 0.004;        // dry-cell tuck depth

uniform float normal_strength : hint_range(0.1, 6.0) = 1.6;
uniform float normal_smooth : hint_range(0.5, 6.0) = 1.5;     // finite-diff stencil in texels (higher = smoother)
uniform bool use_bicubic = false;                            // [exp C] bicubic height reads for the normal (default off = baseline)
uniform bool use_detail = false;                             // [exp Det] procedural FBM detail normals
uniform bool use_kfield = false;                             // [exp D] aperiodic (incommensurate) drift for the detail
uniform bool use_decorrelate = false;                        // [exp F] per-region phase offset for the detail
uniform bool use_break_heuristic = false;                    // [exp B3] shader-side breaking foam
uniform float detail_strength : hint_range(0.0, 2.0) = 0.5;
uniform float detail_scale : hint_range(0.05, 2.0) = 0.35;   // metres per ripple
uniform float detail_speed : hint_range(0.0, 4.0) = 1.0;
uniform bool use_caustics = false;                           // [exp R3] caustic light in the shallows
uniform sampler2D caustics_tex : filter_linear_mipmap, repeat_enable;
uniform float caustic_scale : hint_range(0.5, 8.0) = 2.5;
uniform float caustic_strength : hint_range(0.0, 1.0) = 0.35;
uniform float caustic_speed : hint_range(0.0, 3.0) = 1.0;
uniform vec3 shallow_col : source_color = vec3(0.20, 0.55, 0.55);
uniform vec3 deep_col : source_color = vec3(0.02, 0.12, 0.22);
uniform vec3 sky_tint : source_color = vec3(0.55, 0.72, 0.92);
uniform float depth_scale : hint_range(0.05, 3.0) = 0.9;
uniform float shine : hint_range(0.0, 1.0) = 0.5;             // -> lower roughness = sharper glint
uniform float fresnel_strength : hint_range(0.0, 1.0) = 0.6;

// feature highlight: a distinct flat color where the sim's features would render
uniform sampler2D tx_derived : filter_linear, repeat_disable; // b = foam vis, a = aeration
uniform int highlight_mode : hint_range(0, 3) = 1;            // 0 none · 1 foam · 2 aeration · 3 speed
uniform float highlight_amount : hint_range(0.0, 1.0) = 0.9;
uniform vec3 highlight_foam : source_color = vec3(0.95, 0.98, 1.00);
uniform vec3 highlight_aeration : source_color = vec3(0.20, 0.95, 0.90);
uniform vec3 highlight_speed : source_color = vec3(1.00, 0.45, 0.12);

varying float v_h;

vec2 sim_uv(vec2 uv) {
	// plane vertices sit on texel centres of the sim grid
	return (uv * (GRID_RES - 1.0) + 0.5) / GRID_RES;
}

// --- [experiment C] bicubic fractional sampling of the height field ---
// The 2-D analog of the CXM reverb's Catmull-Rom fractional delay read: reconstruct the
// surface between texels with a cubic kernel instead of bilinear → smoother normals on the
// coarse sim grid. Static swizzles only (no dynamic vec indexing → safe on Metal).
float _tap(vec2 base, vec2 o, vec2 texel) {
	return textureLod(tx_state, base + o * texel, 0.0).r;
}
vec4 catmull_w(float t) {
	float t2 = t * t;
	float t3 = t2 * t;
	return vec4(-0.5 * t3 +       t2 - 0.5 * t,
	             1.5 * t3 - 2.5 * t2 + 1.0,
	            -1.5 * t3 + 2.0 * t2 + 0.5 * t,
	             0.5 * t3 - 0.5 * t2);
}
float _row(vec2 base, float j, vec4 wx, vec2 texel) {
	return wx.x * _tap(base, vec2(-1.0, j), texel)
	     + wx.y * _tap(base, vec2( 0.0, j), texel)
	     + wx.z * _tap(base, vec2( 1.0, j), texel)
	     + wx.w * _tap(base, vec2( 2.0, j), texel);
}
float tex_bicubic_r(vec2 uv, float res) {
	vec2 texel = vec2(1.0 / res);
	vec2 coord = uv * res - 0.5;
	vec2 f = fract(coord);
	vec2 base = (floor(coord) + 0.5) * texel;
	vec4 wx = catmull_w(f.x);
	vec4 wy = catmull_w(f.y);
	return wy.x * _row(base, -1.0, wx, texel) + wy.y * _row(base, 0.0, wx, texel)
	     + wy.z * _row(base,  1.0, wx, texel) + wy.w * _row(base, 2.0, wx, texel);
}
float sample_h(vec2 uv) {   // baseline bilinear vs bicubic — the experiment's one switch
	return use_bicubic ? tex_bicubic_r(uv, GRID_RES) : textureLod(tx_state, uv, 0.0).r;
}

// --- [experiments Det/D/F] procedural detail noise ---
float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}
float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash21(i);
	float b = hash21(i + vec2(1.0, 0.0));
	float c = hash21(i + vec2(0.0, 1.0));
	float d = hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
float fbm2(vec2 p) {
	return vnoise(p) * 0.6 + vnoise(p * 2.03 + 7.1) * 0.3;
}
// [exp D] aperiodic time warp — incommensurate sine sum (the stateless part of the CXM
// K-field; true sample-and-hold is stateful, approximated here).
float kfield_time(float t) {
	return t + sin(t * 0.37) * 0.6 + sin(t * 0.53 + 1.3) * 0.4 + sin(t * 0.234 + 4.1) * 0.25;
}

void vertex() {
	vec2 suv = sim_uv(UV);
	float w = textureLod(tx_state, suv, 0.0).r;
	float bed = textureLod(tx_bottom, suv, 0.0).r;
	float h = max(w - bed, 0.0);
	// dry cells tuck under the sand; wet cells ride the surface
	VERTEX.y = mix(bed - 0.04, w, smoothstep(0.0, tuck_h, h));
	v_h = h;
}

void fragment() {
	vec2 suv = sim_uv(UV);
	float texel = 1.0 / GRID_RES;
	float e = normal_smooth * texel;
	float mpp = DOMAIN_SIZE / GRID_RES;          // metres per texel
	float span = 2.0 * normal_smooth * mpp;      // metres across the stencil

	// smoothed per-pixel normal from the free-surface height field
	// [exp C] sample_h() = bilinear (baseline) or bicubic (toggle) — the only variable
	float wl = sample_h(suv - vec2(e, 0.0));
	float wr = sample_h(suv + vec2(e, 0.0));
	float wd = sample_h(suv - vec2(0.0, e));
	float wu = sample_h(suv + vec2(0.0, e));
	vec3 n = normalize(vec3(-(wr - wl) / span * normal_strength, 1.0, -(wu - wd) / span * normal_strength));

	// [exp Det/D/F] procedural FBM detail normals — sub-grid ripples the sim never computes
	if (use_detail) {
		vec2 wxz = UV * DOMAIN_SIZE;
		vec2 vel = texture(tx_state, suv).gb / max(v_h, 0.05);
		float tt = TIME * detail_speed;
		if (use_kfield) { tt = kfield_time(tt); }                  // [exp D] aperiodic drift
		if (use_decorrelate) { tt += vnoise(wxz * 0.08) * 12.0; }  // [exp F] per-region phase
		vec2 sp = wxz / max(detail_scale, 1e-3) + vec2(0.0, tt * 0.3) - vel * 0.08;
		float de = 0.75;
		float nc = fbm2(sp);
		float nx = fbm2(sp + vec2(de, 0.0));
		float nz = fbm2(sp + vec2(0.0, de));
		vec2 dgrad = vec2(nx - nc, nz - nc) / de * detail_strength;
		n = normalize(vec3(n.x - dgrad.x, n.y, n.z - dgrad.y));
	}
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);

	// depth-tinted water color
	vec3 col = mix(shallow_col, deep_col, clamp(v_h * depth_scale, 0.0, 1.0));
	// a touch of Fresnel toward grazing angles, tinted to the sky
	float fres = pow(1.0 - clamp(dot(normalize(VIEW), NORMAL), 0.0, 1.0), 5.0);
	col = mix(col, sky_tint, fres * fresnel_strength);

	// [exp R3] caustic light dancing in the shallow water (double-pan + min, depth-gated)
	if (use_caustics) {
		vec2 cuv = (UV * DOMAIN_SIZE) / max(caustic_scale, 1e-3);
		float ct = TIME * caustic_speed;
		float c1 = texture(caustics_tex, cuv + vec2(ct * 0.030, ct * 0.021)).r;
		float c2 = texture(caustics_tex, cuv * 1.3 - vec2(ct * 0.025, ct * 0.017)).r;
		float caus = min(c1, c2);
		float shallow = 1.0 - smoothstep(0.05, 0.6, v_h);   // bright in shallow water, fades deep
		col += caustic_strength * caus * shallow;
	}

	// distinct-color overlay where a sim feature would render (foam / aeration / speed)
	float feat = 0.0;
	vec3 fcol = highlight_foam;
	if (highlight_mode == 1) {
		feat = clamp(texture(tx_derived, suv).b, 0.0, 1.0);        // foam
		fcol = highlight_foam;
	} else if (highlight_mode == 2) {
		feat = clamp(texture(tx_derived, suv).a, 0.0, 1.0);        // aeration
		fcol = highlight_aeration;
	} else if (highlight_mode == 3) {
		vec2 vel = texture(tx_state, suv).gb / max(v_h, 0.05);     // flow speed
		feat = clamp(length(vel) / 3.0, 0.0, 1.0);
		fcol = highlight_speed;
	}
	float hl = feat * highlight_amount;
	col = mix(col, fcol, hl);

	// [exp B3] shader-side breaking heuristic: white where the surface is steep AND fast
	// (independent of the sim's foam field — compare its zones to Highlight = Foam)
	if (use_break_heuristic) {
		float steep = length(vec2(wr - wl, wu - wd)) / max(span, 1e-4);
		float spd = length(texture(tx_state, suv).gb) / max(v_h, 0.05);
		float br = clamp(smoothstep(0.15, 0.5, steep) * smoothstep(0.4, 1.2, spd), 0.0, 1.0);
		col = mix(col, vec3(0.95), br * 0.9);
	}

	ALBEDO = col;
	METALLIC = 0.0;
	SPECULAR = 0.5;
	ROUGHNESS = mix(mix(0.35, 0.04, shine), 0.85, hl);   // shine, but highlighted foam reads matte
}
