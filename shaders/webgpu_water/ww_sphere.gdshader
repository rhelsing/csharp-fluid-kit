// WebGPU-Water port — the interactive SPHERE. Verbatim from jeantimex/webgpu-water
// src/shaders/sphere/sphere.{vert,frag}.wgsl (Evan Wallace, MIT). A grey ball lit
// by refracted sun, darkened near pool walls/floor (AO), caustic-lit and tinted
// when underwater. Mesh is a unit sphere placed at sphere_center scaled by radius,
// so world position == source `point`, local position == source unit-sphere normal.
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D water_tex : filter_linear, repeat_disable;
uniform sampler2D caustic_tex : filter_linear, repeat_disable;
uniform vec3 light_direction = vec3(0.816, 0.816, -0.408);
uniform vec3 sphere_center = vec3(-0.4, -0.75, 0.2);
uniform float sphere_radius = 0.25;

varying vec3 local_pos;
varying vec3 world_pos;

void vertex() {
	local_pos = normalize(VERTEX);
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float IOR_AIR = 1.0;
	float IOR_WATER = 1.333;
	vec3 color = vec3(0.5);
	vec3 point = world_pos;

	// Distance-based darkening near pool boundaries (soft AO)
	float dist_x = (1.0 + sphere_radius - abs(point.x)) / sphere_radius;
	float dist_z = (1.0 + sphere_radius - abs(point.z)) / sphere_radius;
	float dist_y = (point.y + 1.0 + sphere_radius) / sphere_radius;
	color *= 1.0 - 0.9 / pow(max(0.1, dist_x), 3.0);
	color *= 1.0 - 0.9 / pow(max(0.1, dist_z), 3.0);
	color *= 1.0 - 0.9 / pow(max(0.1, dist_y), 3.0);

	vec3 refracted_light = refract(-normalize(light_direction), vec3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
	vec3 sphere_normal = local_pos;
	float diffuse = max(0.0, dot(-refracted_light, sphere_normal)) * 0.5;

	vec4 water_info = textureLod(water_tex, point.xz * 0.5 + 0.5, 0.0);
	if (point.y < water_info.r) {
		vec2 caustic_uv = 0.75 * (point.xz - point.y * refracted_light.xz / refracted_light.y) * 0.5 + 0.5;
		vec4 caustic = textureLod(caustic_tex, caustic_uv, 0.0);
		diffuse *= caustic.r * 4.0;
	}
	color += diffuse;

	if (point.y < water_info.r) {
		color *= vec3(0.4, 0.9, 1.0) * 1.2;
	}
	ALBEDO = color;
}
