// TANK fork of ww_surface — scene 23 raytraced pool with three RENDER knobs:
//   pool_half   : scale. The trace runs in normalized pool space (world / pool_half), so the
//                 geometry can be pool_half x bigger in world units and the math is unchanged.
//                 tile_scale (= pool_half) keeps the tiles physically tile-sized as it grows.
//   water_level : lower the surface (negative sinks the water below the rim)
//   tilt        : slant the floor.  y = -pool_height + tilt*(x+1)/2  — the DEEP end (x=-1) stays
//                 pinned on the original floor and the SHALLOW end (x=+1) lifts by `tilt`, so the
//                 floor never punches through the pool box. Where the floor rises above the
//                 waterline the surface fragment is discarded => dry beach.
// Option A: RENDER-only. The sim is still flat-depth, so waves don't shoal — the floor just
// looks slanted. Verbatim Wallace math otherwise (jeantimex/webgpu-water, MIT).
shader_type spatial;
render_mode unshaded, cull_back;

// [scale] tiles get ~pool_half x denser, so they MUST minify through mipmaps — textureLod(...,0)
// would pin mip 0 and alias into noise. One texture() call after the branch keeps the sample in
// uniform control flow so the screen-space derivatives are usable.
uniform sampler2D tile_tex : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D water_tex : filter_linear, repeat_disable;
uniform sampler2D caustic_tex : filter_linear, repeat_disable;
uniform samplerCube sky_tex : filter_linear;
uniform vec3 light_direction = vec3(0.816, 0.816, -0.408);
uniform vec3 sphere_center = vec3(-0.4, -0.75, 0.2);
uniform float sphere_radius = 0.25;
uniform float rim_shadow = 1.0;
uniform float sphere_shadow_on = 1.0;
uniform float ior = 1.333;
uniform float fresnel_min = 0.25;
uniform float pool_half = 1.0;      // scale: world = normalized * pool_half
uniform float tile_scale = 1.0;     // tile repeats across the pool (keeps tiles physical at scale)
uniform float water_level = 0.0;    // lower the surface (negative = below the rim)
uniform float tilt = 0.0;           // shallow-end lift, in pool units

const vec3 ABOVE_WATER_COLOR = vec3(0.25, 1.0, 1.25);

varying vec3 world_pos;

// [tilt] the slanted floor, anchored at the deep end: y(-1) = -1, y(+1) = -1 + tilt
float floor_y(float x) { return -1.0 + tilt * (x + 1.0) * 0.5; }

vec2 intersect_cube(vec3 origin, vec3 ray, vec3 cube_min, vec3 cube_max) {
	vec3 t_min = (cube_min - origin) / ray;
	vec3 t_max = (cube_max - origin) / ray;
	vec3 t1 = min(t_min, t_max);
	vec3 t2 = max(t_min, t_max);
	return vec2(max(max(t1.x, t1.y), t1.z), min(min(t2.x, t2.y), t2.z));
}

// [tilt] ray vs the floor plane  y = floor_y(x)  ==  (-1 + tilt/2) + (tilt/2)*x
float intersect_floor(vec3 o, vec3 r) {
	float a = tilt * 0.5;
	float fy0 = -1.0 + a;
	float denom = r.y - a * r.x;
	if (abs(denom) < 1.0e-6) { return 1.0e6; }
	float t = (fy0 + a * o.x - o.y) / denom;
	return t > 0.0 ? t : 1.0e6;
}

float intersect_sphere(vec3 origin, vec3 ray, vec3 center, float radius) {
	vec3 to_sphere = origin - center;
	float a = dot(ray, ray);
	float b = 2.0 * dot(to_sphere, ray);
	float c = dot(to_sphere, to_sphere) - radius * radius;
	float d = b * b - 4.0 * a * c;
	if (d > 0.0) {
		float t = (-b - sqrt(d)) / (2.0 * a);
		if (t > 0.0) { return t; }
	}
	return 1.0e6;
}

vec3 get_sphere_color(vec3 point) {
	vec3 color = vec3(0.5);
	color *= 1.0 - 0.9 / pow((1.0 + sphere_radius - abs(point.x)) / sphere_radius, 3.0);
	color *= 1.0 - 0.9 / pow((1.0 + sphere_radius - abs(point.z)) / sphere_radius, 3.0);
	color *= 1.0 - 0.9 / pow((point.y + 1.0 + sphere_radius) / sphere_radius, 3.0);

	vec3 sphere_normal = (point - sphere_center) / sphere_radius;
	vec3 refracted_light = refract(-normalize(light_direction), vec3(0.0, 1.0, 0.0), 1.0 / ior);
	float diffuse = max(0.0, dot(-refracted_light, sphere_normal)) * 0.5;

	vec4 info = textureLod(water_tex, point.xz * 0.5 + 0.5, 0.0);
	if (point.y < water_level + info.r) {
		vec2 caustic_uv = 0.75 * (point.xz - point.y * refracted_light.xz / refracted_light.y) * 0.5 + 0.5;
		vec4 caustic = textureLod(caustic_tex, caustic_uv, 0.0);
		diffuse *= caustic.r * 4.0;
	}
	color += diffuse;
	return color;
}

vec3 get_wall_color(vec3 point, float pool_height) {
	vec2 tuv;
	vec3 normal = vec3(0.0, 1.0, 0.0);
	if (abs(point.x) > 0.999) {
		tuv = (point.yz * 0.5 + vec2(1.0, 0.5)) * tile_scale;
		normal = vec3(-point.x, 0.0, 0.0);
	} else if (abs(point.z) > 0.999) {
		tuv = (point.yx * 0.5 + vec2(1.0, 0.5)) * tile_scale;
		normal = vec3(0.0, 0.0, -point.z);
	} else {
		tuv = (point.xz * 0.5 + 0.5) * tile_scale;
		normal = normalize(vec3(-tilt * 0.5, 1.0, 0.0));   // [tilt] slanted floor normal
	}
	vec3 wall_color = texture(tile_tex, tuv).rgb;

	float scale = 0.5;
	scale /= length(point);
	scale *= mix(1.0, 1.0 - 0.9 / pow(length(point - sphere_center) / sphere_radius, 4.0), sphere_shadow_on);

	vec3 refracted_light = -refract(-normalize(light_direction), vec3(0.0, 1.0, 0.0), 1.0 / ior);
	float diffuse = max(0.0, dot(refracted_light, normal));

	vec4 info = textureLod(water_tex, point.xz * 0.5 + 0.5, 0.0);
	if (point.y < water_level + info.r) {
		vec2 caustic_uv = 0.75 * (point.xz - point.y * refracted_light.xz / refracted_light.y) * 0.5 + 0.5;
		vec4 caustic = textureLod(caustic_tex, caustic_uv, 0.0);
		scale += diffuse * caustic.r * 2.0 * caustic.g;
	} else {
		vec2 t = intersect_cube(point, refracted_light, vec3(-1.0, -pool_height, -1.0), vec3(1.0, 2.0, 1.0));
		diffuse *= 1.0 / (1.0 + exp(-200.0 / (1.0 + 10.0 * (t.y - t.x)) * (point.y + refracted_light.y * t.y - 2.0 / 12.0)));
		scale += diffuse * 0.5;
	}
	return wall_color * scale;
}

vec3 get_surface_ray_color(vec3 origin, vec3 ray, vec3 water_color) {
	vec3 color;
	float pool_height = 1.0;

	float q = 1.0e6;
	if (sphere_shadow_on > 0.5) {
		q = intersect_sphere(origin, ray, sphere_center, sphere_radius);
	}

	if (q < 1.0e6) {
		color = get_sphere_color(origin + ray * q);
	} else if (ray.y < 0.0) {
		// [tilt] walls only (cube floor pushed far down) vs the slanted floor plane — nearer wins
		vec2 tw = intersect_cube(origin, ray, vec3(-1.0, -100.0, -1.0), vec3(1.0, 2.0, 1.0));
		float tf = intersect_floor(origin, ray);
		color = get_wall_color(origin + ray * min(tw.y, tf), pool_height);
	} else {
		vec2 t = intersect_cube(origin, ray, vec3(-1.0, -pool_height, -1.0), vec3(1.0, 2.0, 1.0));
		vec3 hit = origin + ray * t.y;
		if (hit.y < 2.0 / 12.0) {
			color = get_wall_color(hit, pool_height);
		} else {
			color = textureLod(sky_tex, ray, 0.0).rgb;
			vec3 sun_dir = normalize(light_direction);
			float spec = pow(max(0.0, dot(sun_dir, ray)), 5000.0);
			color += vec3(spec) * vec3(10.0, 8.0, 6.0);
		}
	}

	if (ray.y < 0.0) { color *= water_color; }
	return color;
}

void vertex() {
	vec2 uv = VERTEX.xz * 0.5 + 0.5;
	vec4 info = textureLod(water_tex, uv, 0.0);
	VERTEX.y = water_level + info.r;                       // [water_level] lowered surface
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// work in normalized pool space (world / pool_half); rays are scale-invariant
	vec3 npos = world_pos / pool_half;
	vec3 ncam = CAMERA_POSITION_WORLD / pool_half;

	vec2 uv = npos.xz * 0.5 + 0.5;
	vec4 info = textureLod(water_tex, uv, 0.0);

	// [tilt] dry beach: no water where the floor has risen above the waterline
	if (floor_y(npos.x) >= water_level + info.r) { discard; }

	for (int i = 0; i < 5; i++) {
		uv += info.ba * 0.005;
		info = textureLod(water_tex, uv, 0.0);
	}

	vec2 ba = info.ba;
	vec3 normal = vec3(info.b, sqrt(max(0.0, 1.0 - dot(ba, ba))), info.a);

	vec3 incoming_ray = normalize(npos - ncam);
	vec3 reflected_ray = reflect(incoming_ray, normal);
	vec3 refracted_ray = refract(incoming_ray, normal, 1.0 / ior);
	float fresnel = mix(fresnel_min, 1.0, pow(1.0 - dot(normal, -incoming_ray), 3.0));

	vec3 reflected_color = get_surface_ray_color(npos, reflected_ray, ABOVE_WATER_COLOR);
	vec3 refracted_color = get_surface_ray_color(npos, refracted_ray, ABOVE_WATER_COLOR);

	ALBEDO = mix(refracted_color, reflected_color, fresnel);
}
