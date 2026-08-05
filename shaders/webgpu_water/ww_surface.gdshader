// WebGPU-Water port — WATER SURFACE (above-water raytracer). Verbatim from
// jeantimex/webgpu-water src/shaders/water/surface.vert.wgsl + surface-above.frag.wgsl
// (Evan Wallace, MIT). For each surface pixel it reflects & refracts the camera
// ray through the water normal (Snell), traces each ray against the pool cube /
// sphere / cubemap sky in-shader, and Fresnel-mixes reflection vs refraction.
// The whole scene (walls, floor, sphere, sky) is re-traced here — Godot lighting
// is not used. World position == source `worldPos` (plane in pool space).
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D tile_tex : filter_linear, repeat_enable;
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

const vec3 ABOVE_WATER_COLOR = vec3(0.25, 1.0, 1.25);

varying vec3 world_pos;

vec2 intersect_cube(vec3 origin, vec3 ray, vec3 cube_min, vec3 cube_max) {
	vec3 t_min = (cube_min - origin) / ray;
	vec3 t_max = (cube_max - origin) / ray;
	vec3 t1 = min(t_min, t_max);
	vec3 t2 = max(t_min, t_max);
	return vec2(max(max(t1.x, t1.y), t1.z), min(min(t2.x, t2.y), t2.z));
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
	if (point.y < info.r) {
		vec2 caustic_uv = 0.75 * (point.xz - point.y * refracted_light.xz / refracted_light.y) * 0.5 + 0.5;
		vec4 caustic = textureLod(caustic_tex, caustic_uv, 0.0);
		diffuse *= caustic.r * 4.0;
	}
	color += diffuse;
	return color;
}

vec3 get_wall_color(vec3 point, float pool_height) {
	vec3 wall_color;
	vec3 normal = vec3(0.0, 1.0, 0.0);
	if (abs(point.x) > 0.999) {
		wall_color = textureLod(tile_tex, point.yz * 0.5 + vec2(1.0, 0.5), 0.0).rgb;
		normal = vec3(-point.x, 0.0, 0.0);
	} else if (abs(point.z) > 0.999) {
		wall_color = textureLod(tile_tex, point.yx * 0.5 + vec2(1.0, 0.5), 0.0).rgb;
		normal = vec3(0.0, 0.0, -point.z);
	} else {
		wall_color = textureLod(tile_tex, point.xz * 0.5 + 0.5, 0.0).rgb;
	}

	float scale = 0.5;
	scale /= length(point);
	scale *= mix(1.0, 1.0 - 0.9 / pow(length(point - sphere_center) / sphere_radius, 4.0), sphere_shadow_on);

	vec3 refracted_light = -refract(-normalize(light_direction), vec3(0.0, 1.0, 0.0), 1.0 / ior);
	float diffuse = max(0.0, dot(refracted_light, normal));

	vec4 info = textureLod(water_tex, point.xz * 0.5 + 0.5, 0.0);
	if (point.y < info.r) {
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
		vec2 t = intersect_cube(origin, ray, vec3(-1.0, -pool_height, -1.0), vec3(1.0, 2.0, 1.0));
		color = get_wall_color(origin + ray * t.y, pool_height);
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
	VERTEX.y = info.r;
	world_pos = VERTEX;
}

void fragment() {
	vec2 uv = world_pos.xz * 0.5 + 0.5;
	vec4 info = textureLod(water_tex, uv, 0.0);
	// refine UV toward the true surface point (source loops 5x)
	for (int i = 0; i < 5; i++) {
		uv += info.ba * 0.005;
		info = textureLod(water_tex, uv, 0.0);
	}

	vec2 ba = info.ba;
	vec3 normal = vec3(info.b, sqrt(max(0.0, 1.0 - dot(ba, ba))), info.a);

	vec3 incoming_ray = normalize(world_pos - CAMERA_POSITION_WORLD);
	vec3 reflected_ray = reflect(incoming_ray, normal);
	vec3 refracted_ray = refract(incoming_ray, normal, 1.0 / ior);
	float fresnel = mix(fresnel_min, 1.0, pow(1.0 - dot(normal, -incoming_ray), 3.0));

	vec3 reflected_color = get_surface_ray_color(world_pos, reflected_ray, ABOVE_WATER_COLOR);
	vec3 refracted_color = get_surface_ray_color(world_pos, refracted_ray, ABOVE_WATER_COLOR);

	ALBEDO = mix(refracted_color, reflected_color, fresnel);
}
