shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, depth_test_disabled, blend_mix;

// depth_test_disabled + a high render_priority on the material, and both are needed.
// The water shader samples hint_screen_texture, so the WATER IS TRANSPARENT TOO — and two
// transparent surfaces whose AABBs are both the full domain, centred identically, sort
// arbitrarily against each other. This overlay was landing behind the water and being hidden
// almost everywhere, which reads exactly like "my shader logic is wrong" and is not.

// tracker_debug — SHOW the wave-tracking signal before anything consumes it.
//
// The first barrel attempt failed because a shape was wired to a placement that was never
// built, and nothing on screen could tell us that. This renders the tracker itself, so a
// method is verified BEFORE a carve depends on it (curl-hypotheses.md §2).
//
// COST WHEN OFF IS ZERO, not small: the host hides this MeshInstance3D, and Godot skips
// hidden geometry entirely — no draw call, no fragment work, nothing.
//
// method: 0 = off, 1 = normals/steepness (field), 2 = probes (points), 3 = break curve.
// Only method 1 reads data that already exists (tx_derived is bound and written every frame);
// 2 and 3 need host-side work and currently draw their intent, not their result.

uniform sampler2D tx_state : filter_linear, repeat_disable;    // r = w
uniform sampler2D tx_bottom : filter_linear, repeat_disable;   // r = B
uniform sampler2D tx_derived : filter_linear, repeat_disable;  // r=n.x g=n.z b=foamVis a=aeration
uniform sampler2D tx_age : filter_linear, repeat_disable;      // r=age 0..1  g=birth steepness
uniform float DOMAIN_SIZE = 92.16;
uniform int method : hint_range(0, 5) = 0;

// Steepness above which a face counts as "breaking". The gradient is in surface-normal units,
// so this is dimensionless and wants tuning against the flat view.
uniform float steep_min : hint_range(0.0, 1.0) = 0.06;
uniform float steep_max : hint_range(0.0, 1.0) = 0.35;
uniform float aeration_gain : hint_range(0.0, 4.0) = 1.0;
// ---- method 5: PLACEMENT VECTORS ------------------------------------------
// Strip everything back to the one question that has to be answered before any shape is
// worth building: WHERE would a barrel go, and WHICH WAY is that piece of wave moving?
// One line per sample point, all the same length, spacing on a slider. No tube, no age
// lifecycle, no ridge — if the lines do not sit on the crest and point shoreward, nothing
// downstream can possibly be right.
// MIRRORED FROM THE BARREL. Not private copies — the host drives these from the same sliders
// that drive barrel_carve, so a line marks exactly where a tube would sit. A separate spacing
// here would debug a lattice nobody uses.
uniform float sl_spacing : hint_range(0.5, 40.0) = 8.0;    // along-crest spacing (barrel's own)
uniform float sl_age_centre : hint_range(0.0, 1.0) = 0.03; // where across-crest the tube centres
uniform float vec_len : hint_range(0.2, 12.0) = 5.0;       // line length (m) — uniform
uniform float vec_width : hint_range(0.02, 2.0) = 0.9;    // line half-width (m)
uniform float vec_gate : hint_range(0.0, 4.0) = 0.10;      // min flow speed (m/s) to draw

uniform float lift : hint_range(0.0, 3.0) = 1.2;   // hover above the surface to avoid z-fight

// Which signal to SHOW. Blending steepness, aeration and direction into one image makes each
// of them untunable — a lit pixel could be any of the three. Isolate to tune, then use All.
// 0 = all, 1 = steepness only, 2 = aeration only, 3 = gradient direction only.
uniform int channel : hint_range(0, 3) = 0;

varying vec2 v_uv;

void vertex() {
	v_uv = clamp(UV, vec2(0.0), vec2(1.0));
	float w = textureLod(tx_state, v_uv, 0.0).r;
	float bed = textureLod(tx_bottom, v_uv, 0.0).r;
	// Methods 1-4 are FIELDS — they must lie on the water to mean anything.
	// Method 5 is an ANNOTATION. Draping it over the displaced surface is what made it read as
	// a refracted stripe painted on the wave instead of a line standing in front of it; a debug
	// vector has to be straight in 3D, so it gets a flat reference plane above the sea.
	VERTEX.y = (method == 5) ? (max(bed, 0.0) + lift) : (max(w, bed) + lift);
}

void fragment() {
	if (method == 0) { discard; }

	vec4 d = texture(tx_derived, v_uv);
	float h = max(texture(tx_state, v_uv).r - texture(tx_bottom, v_uv).r, 0.0);
	if (h < 0.02) { discard; }                       // dry sand: nothing to track

	// n.x / n.z ARE the surface gradient: magnitude = steepness, direction = which way the
	// face points, i.e. the peel direction a carve would be oriented by.
	vec2 grad = d.rg;
	float steep = length(grad);
	float band = smoothstep(steep_min, steep_max, steep);
	float aer = clamp(d.a * aeration_gain, 0.0, 1.0);

	vec3 col;
	float a;
	if (method == 1) {
		// FIELD view. r = steepness (how close to breaking), g = aeration (KP07's own breaking
		// intensity), hue = gradient DIRECTION so the peel angle is readable, not just where.
		float ang = atan(grad.y, grad.x);
		vec3 dir = 0.5 + 0.5 * vec3(cos(ang), cos(ang - 2.094), cos(ang + 2.094));
		if (channel == 1) {
			col = vec3(1.0, 0.25, 0.1);      // steepness ALONE — alpha IS the value
			a = band * 0.95;
		} else if (channel == 2) {
			col = vec3(0.2, 1.0, 0.4);       // aeration ALONE
			a = aer * 0.95;
		} else if (channel == 3) {
			col = dir;                        // direction ALONE, at full strength everywhere wet
			a = 0.8;
		} else {
			col = mix(dir * 0.5, vec3(1.0, 0.25, 0.1), band);
			col = mix(col, vec3(0.2, 1.0, 0.4), aer * 0.6);
			a = clamp(band * 0.85 + aer * 0.5, 0.0, 0.95);
		}
	} else if (method == 2) {
		// PROBE view: a sparse lattice, lit where its cell is breaking. Shows the sampling
		// pattern a moving-box tracker would actually see — sparse is the point.
		vec2 g = fract(v_uv * 48.0) - 0.5;
		float dot_m = 1.0 - smoothstep(0.10, 0.22, length(g));
		col = mix(vec3(0.25, 0.45, 0.9), vec3(1.0, 0.9, 0.2), band);
		a = dot_m * clamp(0.25 + band, 0.0, 1.0);
	} else if (method == 5) {
		// PLACEMENT VECTORS — a line standing in FRONT of the wave face, pointing the way that
		// slice is moving. Placement is barrel_carve's own maths; only the primitive differs.
		//
		// THE BUG THIS REPLACES: the previous version gated the whole branch on `age > 0.001`.
		// But a line drawn ahead of the wave lives exactly where age IS zero, so every pixel it
		// should occupy was discarded and only the sliver overlapping the aged region survived —
		// a stripe painted on the displaced wave face, which is what "refracted stripes" were.
		// Placement needs age; DRAWING must not be gated by it. One test was doing both jobs.
		vec2 world = v_uv * DOMAIN_SIZE;
		float sp = max(sl_spacing, 0.1);

		// direction of travel, sampled where WE are (it varies slowly, so it is a good guess
		// for the march even out on the flat water ahead of the crest)
		vec4 q5 = texture(tx_state, v_uv);
		float h5 = max(q5.r - texture(tx_bottom, v_uv).r, 0.0);
		if (h5 < 0.02) { discard; }
		vec2 flow0 = vec2(q5.g, q5.b) / max(h5, 0.02);
		if (length(flow0) < 1e-4) { discard; }
		vec2 dir0 = normalize(flow0);

		// March BACKWARD along travel until we meet the crest. The first hit is the leading
		// edge (age -> 0 there), which is exactly the wave FACE the line should start from.
		const int BACK_STEPS = 24;
		float stepm = vec_len / float(BACK_STEPS);
		vec2 face = world;
		float found = 0.0;
		for (int k = 0; k <= BACK_STEPS; k++) {
			vec2 pw = world - dir0 * (float(k) * stepm);
			vec2 puv = clamp(pw / DOMAIN_SIZE, vec2(0.0), vec2(1.0));
			if (texture(tx_age, puv).r > 0.001) { face = pw; found = 1.0; break; }
		}
		if (found < 0.5) { discard; }               // no crest behind us within reach

		// Quantise AT THE FACE, so one line per slice — same expression as BarrelSliceFieldA.
		vec2 fuv = clamp(face / DOMAIN_SIZE, vec2(0.0), vec2(1.0));
		vec2 gf = texture(tx_derived, fuv).rg;
		vec2 gdir = length(gf) > 1e-5 ? normalize(gf) : vec2(1.0, 0.0);
		vec2 tdir = vec2(-gdir.y, gdir.x);
		float slice = floor(dot(face, tdir) / sp);

		// snap the origin to the slice centre ALONG the crest: one line per slice, not a smear
		float along = dot(face, tdir) - (slice + 0.5) * sp;
		vec2 origin = face - tdir * along;

		// the slice's own travel direction, read at its own origin
		vec2 ouv = clamp(origin / DOMAIN_SIZE, vec2(0.0), vec2(1.0));
		vec4 oq = texture(tx_state, ouv);
		float oh = max(oq.r - texture(tx_bottom, ouv).r, 0.0);
		vec2 oflow = vec2(oq.g, oq.b) / max(oh, 0.02);
		if (length(oflow) < 1e-4) { discard; }
		vec2 dir = normalize(oflow);

		// segment: origin (the face) -> forward, uniform length
		vec2 rel = world - origin;
		float tproj = clamp(dot(rel, dir), 0.0, vec_len);
		if (length(rel - dir * tproj) > vec_width) { discard; }

		float head = smoothstep(vec_len * 0.55, vec_len, tproj);
		col = mix(vec3(0.15, 0.9, 1.0), vec3(1.0, 0.95, 0.3), head);
		a = 1.0;
	} else if (method == 3) {
		// CURVE view: isolate the breaking FRONT rather than the whole steep region — the
		// ridge of the steepness field, which is what a per-row reduction would return.
		float ridge = smoothstep(0.55, 0.9, band) * (1.0 - smoothstep(0.9, 1.0, band));
		col = vec3(1.0, 0.35, 0.85);
		a = ridge;
	} else {
		// AGE view — the ONLY signal that cannot be judged from a still frame, so it gets its
		// own ramp: blue = just born (bore open) -> cyan -> yellow -> red = crashed (bore shut).
		// Watch for a gradient ACROSS the front: that gradient is what makes age usable as the
		// across-crest coordinate, and if age reads as a flat blob instead the tube will smear
		// into a slab exactly like the first carve attempt did.
		vec2 ag = texture(tx_age, v_uv).rg;
		float t = clamp(ag.r, 0.0, 1.0);
		col = t < 0.5
			? mix(vec3(0.15, 0.35, 1.0), vec3(0.1, 0.95, 0.9), t * 2.0)
			: mix(vec3(0.1, 0.95, 0.9), vec3(1.0, 0.2, 0.1), (t - 0.5) * 2.0);
		a = step(0.001, ag.r) * 0.9;
	}
	ALBEDO = col;
	ALPHA = a;
}
