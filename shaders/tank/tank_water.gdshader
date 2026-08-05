// TANK water surface — NO RAYTRACING. Where ww_surface re-traced the whole scene in-shader
// (walls, floor, sphere, sky) this just renders a normal lit surface and lets Godot do the rest:
//   * refraction  = screen texture, offset by the surface normal (what's already drawn behind it)
//   * depth tint  = analytic water column, (surface y - floor y), so the deep end goes blue and
//                   the shallow end stays clear. No depth buffer read, no trace.
//   * reflection  = ALBEDO 0 + low roughness, so the environment/sun light it like real water.
// The walls, floor and ball are ordinary meshes now — this shader knows nothing about them.
// Geometry is the normalized pool plane [-1,1]; the tank node scales it into world units.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, specular_schlick_ggx;

uniform sampler2D water_tex : filter_linear, repeat_disable;   // R=height, B/A=normal.x/.z
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform float water_level = 0.0;
// Floor is a ramp: y = floor_base + slope*(x+1), x in [-1,1]. floor_base is the DEEP end height
// (raise it to shallow the whole tank) and slope is tan(angle), so the knob is a real angle and
// stays a real angle at any tank scale — the tank node scales x and y together.
uniform float floor_base = -1.0;
uniform float slope = 0.0;
// [shoaling] Green's law, applied WKB-style: the sim evolves the reduced wave and the SURFACE is
// read as G(x)*psi, G = (h_ref/h)^(1/4). Energy flux E*cg is conserved, so as the wave slows into
// the shallows the same energy packs into a shorter wave and the crest must rise. Nothing feeds
// back into the solver, so it cannot destabilise. 0 = off. Deep water (h > h_ref) clamps to 1.
uniform float shoal_ref = 0.0;
uniform float shoal_gain_max = 3.0;
uniform float normal_strength = 1.0;
uniform float refraction = 0.03;
uniform float absorption = 1.6;
uniform vec3 deep_color : source_color = vec3(0.02, 0.22, 0.32);
uniform vec3 tint : source_color = vec3(0.55, 0.88, 0.95);

// [candidate points] Pure VISUALISATION — read-only, nothing here touches the sim. Marks the two
// places worth agitating later:
//   CREST  — high elevation on the wave's FRONT face (the shoreward-facing slope)
//   FRICTION — shallow water that is still moving (thin fast sheet over the bed)
// One jittered lattice cell = one candidate point, so they read as discrete dots rather than bands.
uniform float viz_points = 0.0;      // 0 = off
uniform float viz_density = 60.0;    // cells across the tank
uniform float viz_size = 0.30;       // dot radius as a fraction of a cell
uniform float crest_level = 0.02;    // elevation above still water to count as a crest
uniform float crest_slope = 0.10;    // how steep the front face must be
uniform float friction_depth = 0.08; // water this thin counts as "near shore"
uniform float friction_slope = 0.04; // and it must still be moving
uniform vec3 crest_color : source_color = vec3(1.0, 0.93, 0.30);
uniform vec3 friction_color : source_color = vec3(1.0, 0.30, 0.08);
// LEAD points: the crest test sampled `lead_offset` BEHIND this dot, so the dot lights up that far
// AHEAD of the crest it belongs to — the water the wave is about to arrive at.
uniform float lead_offset = 0.06;
uniform vec3 lead_color : source_color = vec3(1.0, 0.35, 0.75);

varying vec3 pool_pos;   // normalized pool space

float floor_y(float x) { return floor_base + slope * (x + 1.0); }

vec2 hash22(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xx + p3.yz) * p3.zy);
}

float shoal_gain(float x) {
	if (shoal_ref <= 0.0) { return 1.0; }
	float h = max(0.02, water_level - floor_y(x));
	return clamp(pow(shoal_ref / h, 0.25), 1.0, shoal_gain_max);
}

void vertex() {
	vec2 uv = VERTEX.xz * 0.5 + 0.5;
	vec4 info = textureLod(water_tex, uv, 0.0);
	VERTEX.y = water_level + info.r * shoal_gain(VERTEX.x);
	pool_pos = VERTEX;
}

void fragment() {
	vec4 info = texture(water_tex, pool_pos.xz * 0.5 + 0.5);
	float gain = shoal_gain(pool_pos.x);
	float surface_y = water_level + info.r * gain;

	// dry beach: the ramp has come up through the waterline, so there is no water here
	if (floor_y(pool_pos.x) >= surface_y) { discard; }

	// sim normal (B/A hold normal.x/.z), amplified, into view space for lighting
	vec2 ba = info.ba * normal_strength * gain;
	vec3 n = normalize(vec3(ba.x, 1.0, ba.y));
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);

	// refraction: nudge the screen lookup by the surface slope
	vec2 ruv = clamp(SCREEN_UV + NORMAL.xy * refraction, vec2(0.001), vec2(0.999));
	vec3 behind = texture(screen_tex, ruv).rgb;

	// how much water the view is looking through, straight from the ramp
	float column = max(0.0, surface_y - floor_y(pool_pos.x));
	float absorb = 1.0 - exp(-column * absorption);

	EMISSION = mix(behind * tint, deep_color, absorb);

	// candidate points: evaluate the condition AT the dot centre so a dot is all-or-nothing
	if (viz_points > 0.5) {
		vec2 cell = floor((pool_pos.xz * 0.5 + 0.5) * viz_density);
		vec2 j = hash22(cell) - 0.5;
		vec2 cuv = (cell + 0.5 + j * 0.7) / viz_density;
		vec2 cpos = cuv * 2.0 - 1.0;
		float r = length(pool_pos.xz - cpos) * viz_density * 0.5;
		if (r < viz_size) {
			vec4 ci = texture(water_tex, cuv);
			float cg = shoal_gain(cpos.x);
			float ce = ci.r * cg;                                   // displayed elevation
			float cs = length(ci.ba * cg);                          // local steepness
			float cd = max(0.0, (water_level + ce) - floor_y(cpos.x));
			// front face = shoreward-facing slope (waves run toward +x)
			bool is_crest = ce > crest_level && (ci.b * cg) < -crest_slope;
			bool is_fric  = cd < friction_depth && cs > friction_slope;

			// same crest test, sampled behind by lead_offset -> this dot sits ahead of that crest
			vec2 luv = cuv - vec2(lead_offset * 0.5, 0.0);
			vec4 li = texture(water_tex, luv);
			float lg = shoal_gain(cpos.x - lead_offset);
			bool is_lead = (li.r * lg) > crest_level && (li.b * lg) < -crest_slope;

			if (is_crest || is_lead || is_fric) {
				float edge = smoothstep(viz_size, viz_size * 0.55, r);
				vec3 col = is_crest ? crest_color : (is_lead ? lead_color : friction_color);
				EMISSION += col * edge * 4.0;
			}
		}
	}
	ALBEDO = vec3(0.0);          // dielectric: no diffuse, all specular + environment
	ROUGHNESS = 0.03;
	METALLIC = 0.0;
	SPECULAR = 0.6;
	ALPHA = 1.0;
}
