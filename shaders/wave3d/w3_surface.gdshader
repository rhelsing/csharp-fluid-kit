shader_type spatial;
render_mode unshaded, cull_front, depth_draw_never;

// w3_surface — raymarch the water level set inside a unit box. No readback and no
// polygonizer: the sim texture feeds the renderer directly (the scene-10 lesson), which
// matters here because a curling surface has no heightfield to hand a mesher anyway.
// Front faces are culled so the camera can be inside the box.

uniform sampler3D phi_tex : filter_linear, repeat_disable;
uniform sampler3D mask_tex : filter_nearest, repeat_disable;
uniform int steps : hint_range(16, 256) = 96;
uniform vec3 water_shallow : source_color = vec3(0.30, 0.62, 0.60);
uniform vec3 water_deep : source_color = vec3(0.02, 0.13, 0.22);
uniform vec3 bed_col : source_color = vec3(0.76, 0.68, 0.52);
uniform vec3 sun_dir = vec3(-0.45, 0.80, 0.35);

varying vec3 v_local_cam;
varying vec3 v_local_pos;

void vertex() {
	v_local_pos = VERTEX;
	v_local_cam = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

// central-difference normal of the level set
vec3 phi_normal(vec3 p, float h) {
	vec2 e = vec2(h, 0.0);
	return normalize(vec3(
		texture(phi_tex, p + e.xyy).r - texture(phi_tex, p - e.xyy).r,
		texture(phi_tex, p + e.yxy).r - texture(phi_tex, p - e.yxy).r,
		texture(phi_tex, p + e.yyx).r - texture(phi_tex, p - e.yyx).r));
}

void fragment() {
	vec3 ro = v_local_cam + vec3(0.5);      // box local space -> [0,1]^3
	vec3 rd = normalize(v_local_pos - v_local_cam);

	// slab intersection with the unit cube
	vec3 inv = 1.0 / max(abs(rd), vec3(1e-6)) * sign(rd + vec3(1e-9));
	vec3 t0 = (vec3(0.0) - ro) * inv;
	vec3 t1 = (vec3(1.0) - ro) * inv;
	vec3 tn = min(t0, t1);
	vec3 tf = max(t0, t1);
	float tmin = max(max(tn.x, tn.y), tn.z);
	float tmax = min(min(tf.x, tf.y), tf.z);
	tmin = max(tmin, 0.0);
	if (tmax <= tmin) { discard; }

	float dt = (tmax - tmin) / float(steps);
	vec3 col = vec3(0.0);
	float alpha = 0.0;

	for (int i = 0; i < steps; i++) {
		vec3 p = ro + rd * (tmin + dt * float(i));
		float m = texture(mask_tex, p).r;
		if (m < -0.5) {                                   // bed: opaque, stop
			col = bed_col * (0.55 + 0.45 * clamp(p.y * 2.0, 0.0, 1.0));
			alpha = 1.0;
			break;
		}
		if (texture(phi_tex, p).r > 0.5) {                 // first water hit = the surface
			vec3 n = phi_normal(p, 1.0 / 96.0);
			float lam = clamp(dot(n, normalize(sun_dir)), 0.0, 1.0);
			float fres = pow(1.0 - clamp(dot(n, -rd), 0.0, 1.0), 3.0);
			// depth below the surface along the ray, as a cheap thickness tint
			float depth = clamp((tmax - (tmin + dt * float(i))) * 1.6, 0.0, 1.0);
			col = mix(water_shallow, water_deep, depth) * (0.45 + 0.75 * lam) + vec3(fres * 0.5);
			alpha = 1.0;
			break;
		}
	}
	if (alpha < 0.5) { discard; }
	ALBEDO = col;
}
