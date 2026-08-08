shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, depth_test_disabled, blend_mix;

// cull_disabled + depth_test_disabled, and both are load-bearing.
// This layer used cull_front, so only the box's BACK faces rasterised. With the camera above
// the water looking down at the surf, those back faces sit BELOW the opaque water mesh, which
// depth-rejects them — so the fragment shader never ran and the layer drew nothing regardless
// of what the carve field contained. That is why a hand-placed, known-good 210 capsule was as
// invisible as the automatic slices: the bug was never in the SDF, the placement, or the age.
// Disabling the depth test also makes the ghost a true X-ray, which is what a placement ghost
// is for.

// barrel_carve — the BARREL as an ADDITIONAL SDF LAYER over the KP07 shore (scene 28).
//
// The fixed-capsule path (BarrelCarveField, br_center/yaw/pitch/radius/half_len, br_slices)
// is DELETED. It was placed at a compile-time constant with nothing connecting it to the sim,
// so it could not track a wave and only ever rendered a slab. Slices along the crest replace
// it outright; keeping a dead second path around just made "barrel" ambiguous.
// What survives from 210 is the RIDGE and the SPIN (sl_ridge_*, sl_spin) and the smooth
// subtraction — the parts that were always doing the work.
//
// Ported from ../water-kit scene 210 (`shaders/moana_water_barrel.gdshader`, itself a fork of
// the scene-204 Wallis oracle). ONE idea is carried over: a tilted ridged capsule
// smooth-subtracted from the water so the surface wraps a tube-shaped VOID. Ridge is |fbm| in
// the capsule's angular frame, spun with TIME — the void is static, the surface pours.
//
// WHAT CHANGED IN THE PORT, and it is the whole point: 210 carves an ANALYTIC sin/cos swell.
// This carves the REAL KP07 FREE SURFACE, sampled from tx_state/tx_bottom — the same textures
// the mesh water renders from. So the barrel is cut into the wave the solver actually made,
// at the place it actually broke, instead of into a decorative swell.
//
// WHY AN SDF AND NOT MESH DISPLACEMENT: a mesh is y = h(x,z), single-valued, so an overhanging
// lip cannot be represented at all — and at 0.12 m quads a 2 m tube is ~16 quads wide. A
// raymarched implicit surface has no topology limit and no mesh resolution ceiling; cost is
// per-pixel, which is the resolution-independence that makes this worth doing.
//
// This layer renders ONLY where it hits water inside its box; everywhere else it discards, so
// the mesh water underneath shows through untouched. sl_enabled = 0 discards everything.

uniform sampler2D tx_state : filter_linear, repeat_disable;   // r = w (free surface)
uniform sampler2D tx_bottom : filter_linear, repeat_disable;  // r = B (bed)
uniform sampler2D tx_derived : filter_linear, repeat_disable; // r=n.x g=n.z (surface gradient)
uniform sampler2D tx_age : filter_linear, repeat_disable;     // r=age 0..1  g=birth steepness
// The breaking front as a CURVE, one entry per along-shore row: r = front x (m), g = surface
// height there, b = strength, a = valid. This is what makes a tube possible — age could only
// ever say "breaking here", never WHERE, so geometry derived from it collapsed to the ribbon's
// own width. See pass_curl_front.glsl.
uniform sampler2D tx_front : filter_linear, repeat_disable;
uniform float DOMAIN_SIZE = 92.16;
uniform int steps : hint_range(16, 192) = 80;

// ---- barrel carve (br_* names kept from 210 so the two are comparable) ----
uniform float sl_enabled : hint_range(0.0, 1.0) = 1.0;
// 0 = the original single fixed capsule (kept so the two are comparable); 1 = SLICES driven by
// the break-age field, which is the design that actually follows a curving crest.

// ---- slice mode (curl-hypotheses.md §3b) ----
// The cross-section plane is (age, depth-below-surface). Age varies ACROSS the front — oldest
// where breaking began, zero at the leading edge — so it is a real across-crest coordinate, and
// a circle in that plane is a tube that follows the crest wherever it curves. That is the whole
// trick: no crest curve is ever extracted.
uniform float sl_age_metres : hint_range(0.2, 20.0) = 1.2;   // world metres one unit of age spans
uniform float sl_age_centre : hint_range(0.0, 1.0) = 0.10;   // where in its life the bore is widest
uniform float sl_depth : hint_range(0.0, 3.0) = 0.45;        // bore centre below the surface (m)
uniform float sl_radius : hint_range(0.05, 3.0) = 0.55;      // bore radius at full size (m)
uniform float sl_radius_by_steep : hint_range(0.0, 3.0) = 1.0; // bigger waves -> bigger tubes
uniform float sl_crash : hint_range(0.0, 1.0) = 0.85;        // how far age closes the bore
uniform float sl_len : hint_range(0.3, 20.0) = 3.0;          // slice length along the crest (m)
// ---- explicit slice controls: spacing, size, rotation, offset -------------
// The slice used to derive all of these from the field. It could not be told "six big ones,
// pointing that way", which is what tuning coverage actually requires.
uniform float sl_spacing : hint_range(0.5, 40.0) = 8.0;      // metres between slice centres
uniform float sl_circle_r : hint_range(0.1, 8.0) = 1.5;      // TRUE radius in metres — uniform
uniform float sl_half_len : hint_range(0.5, 30.0) = 6.0;     // half-length ALONG the axis (m)
uniform float sl_yaw_off : hint_range(-180.0, 180.0) = 0.0;  // deg, RELATIVE to wave forward
uniform float sl_pitch_off : hint_range(-90.0, 90.0) = 0.0;  // deg, relative to horizontal
uniform float sl_off_across : hint_range(-20.0, 20.0) = 0.0; // m, along wave forward
uniform float sl_off_up : hint_range(-8.0, 8.0) = 0.0;       // m, vertical
uniform float sl_off_along : hint_range(-20.0, 20.0) = 0.0;  // m, along the crest
uniform float sl_seed_var : hint_range(0.0, 1.0) = 0.7;      // per-slice ridge/phase decorrelation
uniform float sl_spin_var : hint_range(0.0, 1.0) = 0.35;     // per-slice spin-rate spread
uniform float sl_debug : hint_range(0.0, 1.0) = 1.0;   // ghost the carve solid for placement

// ---- 210's FIXED CAPSULE, restored ----------------------------------------
// Placement by hand, no age, no field, no lifecycle. This is the known-good shape from
// water-kit 210: a capsule with |fbm| ridges in its spun angular frame, smooth-subtracted
// from the water. Slices were an attempt to place it automatically; that is a separate
// problem, and it cannot be debugged until the SHAPE is proven to render.
uniform float br_fixed : hint_range(0.0, 1.0) = 1.0;   // 1 = fixed capsule, 0 = age slices
uniform vec3 br_center = vec3(56.0, 0.4, 42.0);
uniform float br_yaw_deg : hint_range(-180.0, 180.0) = 0.0;
uniform float br_pitch_deg : hint_range(-45.0, 45.0) = 0.0;
uniform float br_radius : hint_range(0.1, 8.0) = 1.6;
uniform float br_half_len : hint_range(1.0, 40.0) = 14.0;
uniform float sl_smooth : hint_range(0.05, 4.0) = 0.7;
uniform float sl_ridge_amp : hint_range(0.0, 1.5) = 0.18;
uniform float sl_ridge_scale : hint_range(0.05, 3.0) = 0.7;
uniform float sl_ridge_stretch : hint_range(0.5, 8.0) = 3.0;
uniform float sl_spin : hint_range(-4.0, 4.0) = 1.88;

uniform vec3 water_shallow : source_color = vec3(0.32, 0.66, 0.62);
uniform vec3 water_deep : source_color = vec3(0.02, 0.16, 0.24);
uniform vec3 foam_col : source_color = vec3(0.92, 0.95, 0.95);
uniform float foam_gain : hint_range(0.0, 4.0) = 1.4;
uniform vec3 sun_dir = vec3(-0.45, 0.78, 0.42);

varying vec3 v_cam_ws;
varying vec3 v_pos_ws;

void vertex() {
	v_pos_ws = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_cam_ws = CAMERA_POSITION_WORLD;
}

// 1D hash: per-slice decorrelation seed. Lives here (not with the deleted legacy path) —
// it is what gives each slice its own ridge phase and spin rate, so a long tube does not read
// as one extruded shape repeated down the crest.
float hash11(float p) {
	p = fract(p * 0.1031);
	p *= p + 33.33;
	p *= p + p;
	return fract(p);
}

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i), n100 = hash13(i + vec3(1, 0, 0));
	float n010 = hash13(i + vec3(0, 1, 0)), n110 = hash13(i + vec3(1, 1, 0));
	float n001 = hash13(i + vec3(0, 0, 1)), n101 = hash13(i + vec3(1, 0, 1));
	float n011 = hash13(i + vec3(0, 1, 1)), n111 = hash13(i + vec3(1, 1, 1));
	return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
	           mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z) * 2.0 - 1.0;
}

float fbm_4(vec3 p) {
	float a = 0.5, s = 0.0;
	for (int i = 0; i < 4; i++) { s += a * vnoise(p); p *= 2.02; a *= 0.5; }
	return s;
}

// The KP07 free surface as a signed distance: positive above the water, negative below.
// A heightfield distance is only exact for a flat surface, so the march is under-relaxed
// below to stay conservative on steep faces.
// IQ smooth subtraction. k is the lip feather — the single value that decides whether the
// carve reads as a hole punched in the water or as a lip curling over it. Deleted with the
// legacy block by accident; without it nothing compiles and the layer's box renders opaque.
float sdSmoothSubtraction(float d1, float d2, float k) {
	float h = clamp(0.5 - 0.5 * (d2 + d1) / k, 0.0, 1.0);
	return mix(d2, -d1, h) + k * h * (1.0 - h);
}

float WaterField(vec3 p) {
	vec2 uv = clamp(p.xz / DOMAIN_SIZE, vec2(0.0), vec2(1.0));
	float w = texture(tx_state, uv).r;
	float bed = texture(tx_bottom, uv).r;
	float surf = max(w, bed);
	return p.y - surf;
}

// 210's carve, verbatim in structure: capsule + |fbm| ridge in the spun angular frame.
// 210's carve: capsule + |fbm| ridge in the spun angular frame. Rotationally static; br_spin
// only turns the noise, so the void holds still while the surface pours through it.
float BarrelCarveField(vec3 pos, float t) {
	float yaw = radians(br_yaw_deg);
	float pitch = radians(br_pitch_deg);
	vec3 q = pos - br_center;
	float cy = cos(-yaw), sy = sin(-yaw);
	q = vec3(cy * q.x - sy * q.z, q.y, sy * q.x + cy * q.z);
	float cp = cos(-pitch), sp = sin(-pitch);
	q = vec3(cp * q.x - sp * q.y, sp * q.x + cp * q.y, q.z);

	float capsule = length(vec3(max(abs(q.x) - br_half_len, 0.0), q.y, q.z)) - br_radius;
	float ang = atan(q.z, q.y) - t * sl_spin;
	vec3 nd = vec3(q.x * sl_ridge_scale,
		cos(ang) * br_radius * sl_ridge_scale / sl_ridge_stretch,
		sin(ang) * br_radius * sl_ridge_scale / sl_ridge_stretch);
	return capsule + sl_ridge_amp * abs(fbm_4(nd)) * br_radius;
}

// A slice is 210's CAPSULE, placed on the front curve instead of by hand.
//
// THE AXIS RUNS ALONG THE CREST, i.e. along the curve's TANGENT — perpendicular to the
// shoreward normal that the debug lines draw. The lines mark where a tube goes and which way
// the wave is coming; the tube lies across them. Getting that 90 degrees wrong is what made
// earlier attempts look like they were standing on end.
//
// Every coordinate below is METRES, so R is a true radius and the cross-section is a circle.
// No age-derived axis, no mismatched units, no warping.
float BarrelSliceFieldA(vec3 pos, float t, out float ang_out, out float age_out, out float slice_out) {
	ang_out = 0.0; age_out = 0.0; slice_out = 0.0;

	float spacing = max(sl_spacing, 0.1);
	float slice = floor(pos.z / spacing);           // stable world axis -> stable identity
	slice_out = slice;
	float zc = (slice + 0.5) * spacing;             // this slice's centre, along-shore

	// --- the curve, sampled at the slice centre and either side for the tangent ---
	float du = spacing / DOMAIN_SIZE;
	vec4 f0 = texture(tx_front, vec2(clamp(zc / DOMAIN_SIZE, 0.0, 1.0), 0.5));
	// CONTINUOUS lookup at THIS point's own z, not the slice centre. Sampling only at the
	// centre made each tube a straight chord across a curving front, so it drifted off the wave
	// by up to half a slice at the ends — the tubes looked out of sync with the crest even
	// though placement was correct. The front texture is filter_linear, so this interpolates
	// between rows for free and the tube sweeps along the curve.
	vec4 fp = texture(tx_front, vec2(clamp(pos.z / DOMAIN_SIZE, 0.0, 1.0), 0.5));
	if (fp.a < 0.5) { return 1e5; }                 // no front here: no tube
	vec4 fa = texture(tx_front, vec2(clamp((zc - spacing * 0.5) / DOMAIN_SIZE, 0.0, 1.0), 0.5));
	vec4 fb = texture(tx_front, vec2(clamp((zc + spacing * 0.5) / DOMAIN_SIZE, 0.0, 1.0), 0.5));

	// tangent = along-crest. dz is the along-shore step, dx the front's drift across it.
	vec2 tang = vec2(fb.r - fa.r, spacing);
	tang = length(tang) > 1e-5 ? normalize(tang) : vec2(0.0, 1.0);
	vec2 norm = vec2(tang.y, -tang.x);              // shoreward normal (the debug line)
	if (norm.x < 0.0) { norm = -norm; }

	float age = texture(tx_age, clamp(pos.xz / DOMAIN_SIZE, vec2(0.0), vec2(1.0))).r;
	age_out = age;
	float seed = hash11(slice) * sl_seed_var;

	// --- slice centre, then project the sample point into the tube's own frame ---
	vec2 C = vec2(fp.r, zc) + norm * sl_off_across + tang * sl_off_along;
	float Cy = fp.g - sl_depth - sl_off_up;
	vec2 dxz = pos.xz - C;
	float a = dot(dxz, tang);                       // along the axis
	float b = dot(dxz, norm);                       // across the wave
	float c = pos.y - Cy;                           // vertical

	// yaw turns the axis within the horizontal plane, pitch tilts it — both RELATIVE to the
	// curve, so an angle dialled once keeps meaning the same thing as the front moves.
	float cy2 = cos(radians(sl_yaw_off)), sy2 = sin(radians(sl_yaw_off));
	vec2 ab = vec2(cy2 * a - sy2 * b, sy2 * a + cy2 * b);
	float cp2 = cos(radians(sl_pitch_off)), sp2 = sin(radians(sl_pitch_off));
	vec2 ac = vec2(cp2 * ab.x - sp2 * c, sp2 * ab.x + cp2 * c);

	float axial = ac.x;
	float across = ab.y;
	float vert = ac.y;

	float R = max((sl_circle_r + fp.b * sl_radius_by_steep * 0.5) * (1.0 - sl_crash * age), 0.02);
	float L = max(sl_half_len, 0.05);

	// 210's capsule, unchanged: a segment of length 2L swept by a sphere of radius R.
	float capsule = length(vec3(max(abs(axial) - L, 0.0), across, vert)) - R;

	// 210's ridge: |fbm| in the spun angular frame about the axis
	float ang = atan(vert, across) - t * sl_spin * (1.0 + (seed - 0.5) * sl_spin_var) + seed * 6.283;
	ang_out = ang;
	vec3 nd = vec3(axial * sl_ridge_scale + seed * 13.0,
		cos(ang) * R * sl_ridge_scale / sl_ridge_stretch,
		sin(ang) * R * sl_ridge_scale / sl_ridge_stretch);
	return capsule + sl_ridge_amp * abs(fbm_4(nd)) * R;
}

float BarrelSliceField(vec3 pos, float t) {
	float a, g, sl;
	return BarrelSliceFieldA(pos, t, a, g, sl);
}


float Scene(vec3 p, float t) {
	float d = WaterField(p);
	if (sl_enabled > 0.5) {
		float carve = br_fixed > 0.5 ? BarrelCarveField(p, t) : BarrelSliceField(p, t);
		d = sdSmoothSubtraction(carve, d, sl_smooth);
	}
	return d;
}
// What the raymarch actually walks. In DEBUG the carve solid is marched ON ITS OWN, so the
// tubes are visible as objects — which is the whole point of a placement ghost and precisely
// what the previous version failed to do: it marched the carved WATER and merely recoloured
// the hit, so it painted the box's screen footprint flat and revealed nothing.
float MarchField(vec3 p, float t) {
	if (sl_debug > 0.5) {
		return br_fixed > 0.5 ? BarrelCarveField(p, t) : BarrelSliceField(p, t);
	}
	return Scene(p, t);
}


vec3 SceneNormal(vec3 p, float t, float e) {
	vec2 k = vec2(1.0, -1.0) * e;
	return normalize(k.xyy * MarchField(p + k.xyy, t) + k.yyx * MarchField(p + k.yyx, t)
	               + k.yxy * MarchField(p + k.yxy, t) + k.xxx * MarchField(p + k.xxx, t));
}

void fragment() {
	if (sl_enabled < 0.5) { discard; }

	vec3 ro = v_cam_ws;
	vec3 rd = normalize(v_pos_ws - v_cam_ws);
	// March from the CAMERA. The previous `length(back_face - cam) - 40.0` was tuned when this
	// layer's box was 34 units wide; the box now spans the 92 m domain, so that expression
	// started the ray up to ~50 units downrange and stepped straight over any barrel near the
	// viewer. Back faces are what we rasterise, so the box only ever bounded which PIXELS run —
	// the ray must still start at the eye.
	float t = 0.0;

	float hit = -1.0;
	float dist = 0.0;
	for (int i = 0; i < steps; i++) {
		vec3 p = ro + rd * (t + dist);
		float d = MarchField(p, TIME);
		if (d < 0.012) { hit = t + dist; break; }
		// CLAMPED, and the upper bound matters as much as the lower: BarrelSliceField returns
		// 1e5 to mean "no barrel here", so an unclamped step teleports past the whole domain on
		// the first sample over open water and the march bails instantly. Invisible in normal
		// mode (sdSmoothSubtraction blends the sentinel away against the water), fatal in the
		// ghost, which marches the raw carve.
		// STEP SIZE IS PROXIMITY-GATED, and it has to be. Over open water BarrelSliceField
		// returns its 1e5 "no barrel here" sentinel, which clamps the step to the coarse bound —
		// but the carve slab is only ~1.5 m tall and sits in a thin strip on the break line, so
		// marching at 1.5 m samples AT the feature scale and steps clean over every tube. The
		// water surface is the cheap proximity oracle: far from it, stride; near it, creep.
		// The capsule is a genuine SDF, so its own distance is a safe stride. The slice path
		// returns a 1e5 sentinel over open water, which needs the surface as a proximity oracle
		// or the march samples at the feature scale and steps over every tube.
		// One stride for both paths. The proximity gate that used to creep at 0.18 m near the
		// surface existed for the OLD slice field, which was a degenerate slab thin enough to
		// alias through. It is actively harmful now: the camera looks ALONG the water, so a ray
		// is within a few metres of the surface for most of its length, and 80 steps x 0.18 m
		// reaches only ~14 m — the march died long before the surf 40 m away. Both fields are
		// true SDFs now, so sphere tracing converges on its own.
		float step_max = 2.0;
		dist += clamp(d * 0.7, 0.02, step_max);   // under-relaxed: a heightfield SDF overestimates
		// 92 m domain -> ~130 m diagonal. 90 cut off the far half of the shore.
		if (dist > 160.0) { break; }
	}
	if (hit < 0.0) { discard; }

	vec3 p = ro + rd * hit;

	// DISCARD WHERE THE CARVE ISN'T. Scene() is sdSmoothSubtraction(carve, WaterField), and
	// WaterField is defined across the ENTIRE domain because its UVs are clamped — so without
	// this the march hits water everywhere and the layer repaints the whole sea in its own
	// colours, laying a second water material over the real one. That is not a shading bug to
	// tune, it is this pass running where it has no business running. Only pixels the carve
	// actually reaches belong to this layer; everywhere else the water mesh owns the frame.
	float infl = br_fixed > 0.5 ? BarrelCarveField(p, TIME) : BarrelSliceField(p, TIME);
	if (infl > sl_smooth * 2.0) { discard; }

	vec3 n = SceneNormal(p, TIME, 0.03);

	float lam = clamp(dot(n, normalize(sun_dir)), 0.0, 1.0);
	float fres = pow(1.0 - clamp(dot(n, -rd), 0.0, 1.0), 3.0);
	// NO BESPOKE FOAM. This layer used to paint froth wherever a sample sat near the carve
	// surface — a decorative term keyed to distance-from-carve, with no physics behind it, and
	// it did not match the water shader. The water's own foam (tx_derived, foam_breakup) is the
	// only foam now, so the barrel reads as the same water rather than a second material.
	float froth = 0.0;

	vec2 uv = clamp(p.xz / DOMAIN_SIZE, vec2(0.0), vec2(1.0));
	float depth = clamp((texture(tx_state, uv).r - texture(tx_bottom, uv).r) * 0.8, 0.0, 1.0);
	vec3 body = mix(water_shallow, water_deep, depth) * (0.45 + 0.8 * lam) + vec3(fres * 0.35);

	// --- placement ghost: the carve SOLID, banded so the spin reads ---
	float g_ang, g_age, g_slice;
	BarrelSliceFieldA(p, TIME, g_ang, g_age, g_slice);
	// stripes around the bore: they rotate with sl_spin, so a still frame shows the tube and a
	// moving one shows which way (and how fast) it is turning.
	float band_s = smoothstep(0.35, 0.65, fract(g_ang / 6.28318 * 6.0));
	// MAGENTA, as in 210 — a colour nothing else in the scene uses, so the carve solid reads
	// unambiguously as a debug object rather than as water. Age is kept, but as brightness
	// rather than hue: dim = just born, hot = crashed. Hue would have made the ghost look like
	// shading and lost the "this is not real geometry" signal that makes a ghost useful.
	float g_lit = br_fixed > 0.5 ? 1.0 : clamp(g_age, 0.0, 1.0);
	vec3 ghost = vec3(0.95, 0.15, 0.85) * (0.45 + 0.55 * g_lit);
	ghost = mix(ghost * 0.45, ghost, band_s);
	ghost *= 0.35 + 0.75 * clamp(dot(n, normalize(sun_dir)), 0.0, 1.0);

	ALBEDO = sl_debug > 0.5 ? ghost : mix(body, foam_col, froth);
	ALPHA = sl_debug > 0.5 ? 0.75 : 1.0;
}
