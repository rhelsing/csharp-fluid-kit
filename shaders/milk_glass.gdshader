shader_type spatial;
render_mode unshaded, cull_back;

// Scene 32 — milk poured into a glass of clear coffee, raymarched. The dye volume is
// sampled front-to-back inside a cylinder mask (the glass); milk accumulates as white
// scatter with a cheap top-light occlusion term, and the clear liquid behind it tints
// the backdrop by Beer–Lambert over the ray's in-glass path.

uniform sampler3D dye_tex : filter_linear, repeat_disable;
uniform vec3 box_extents = vec3(1.4, 2.1, 1.4);   // volume half-extents (world; node at origin)
uniform vec3 liquid_tint : source_color = vec3(0.52, 0.30, 0.14);  // weak clear coffee
uniform vec3 milk_color : source_color = vec3(0.97, 0.95, 0.91);
uniform vec3 backdrop : source_color = vec3(0.62, 0.56, 0.50);
uniform float density : hint_range(1.0, 40.0) = 8.0;
uniform float knee : hint_range(0.0, 0.15) = 0.03;  // dilute milk below this stays translucent haze
uniform float absorb : hint_range(0.0, 2.0) = 0.45;
uniform float shade : hint_range(0.0, 1.0) = 0.4;
uniform float steps : hint_range(16.0, 96.0) = 56.0;
uniform float cyl_radius : hint_range(0.5, 1.0) = 0.94;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 ro = CAMERA_POSITION_WORLD;
	vec3 rd = normalize(world_pos - ro);

	// slab test against the axis-aligned volume box at the origin
	vec3 inv = 1.0 / rd;
	vec3 tA = (-box_extents - ro) * inv;
	vec3 tB = (box_extents - ro) * inv;
	vec3 tMin = min(tA, tB);
	vec3 tMax = max(tA, tB);
	float t0 = max(max(tMin.x, tMin.y), max(tMin.z, 0.0));
	float t1 = min(tMax.x, min(tMax.y, tMax.z));

	vec3 acc = vec3(0.0);
	float alpha = 0.0;
	if (t1 > t0) {
		float dt = (t1 - t0) / steps;
		float t = t0 + dt * 0.5;
		for (int i = 0; i < 96; i++) {
			if (float(i) >= steps || alpha > 0.985) { break; }
			vec3 p = ro + rd * t;
			vec3 uvw = p / (2.0 * box_extents) + 0.5;
			float rad = length((uvw.xz - 0.5) * 2.0);
			if (rad < cyl_radius) {
				float d = max(texture(dye_tex, uvw).r - knee, 0.0);
				if (d > 0.002) {
					float a = 1.0 - exp(-d * density * dt);
					float occ = texture(dye_tex, uvw + vec3(0.0, 0.05, 0.0)).r; // milk above darkens
					vec3 col = milk_color * (1.0 - shade * clamp(occ * 1.6, 0.0, 1.0));
					acc += (1.0 - alpha) * a * col;
					alpha += (1.0 - alpha) * a;
				}
			}
			t += dt;
		}
	}

	// the clear liquid tints whatever shows through (Beer–Lambert over the in-glass path)
	vec3 through = exp(-(vec3(1.0) - liquid_tint) * absorb * max(t1 - t0, 0.0));
	vec3 col = acc + (1.0 - alpha) * backdrop * through;
	ALBEDO = col;
}
