// WebGPU-Water port — POOL walls + floor. Verbatim from jeantimex/webgpu-water
// src/shaders/pool/pool.{vert,frag}.wgsl (Evan Wallace, MIT). Tiled walls of the
// unit pool cube, lit by sun refracted through the water: projected caustics +
// sphere shadow underwater, rim shadow above water, corner/sphere AO, underwater
// tint. Geometry is the source's octant cube in [-1,1]; the vertex remaps y to
// pool space (floor y=-1, rim y~2/12) exactly as pool.vert does, so cull_back
// culls the near walls identically to the source and the fragment's position ==
// the source's `point`.
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D tile_tex : filter_linear, repeat_enable;
uniform sampler2D water_tex : filter_linear, repeat_disable;
uniform sampler2D caustic_tex : filter_linear, repeat_disable;
uniform vec3 light_direction = vec3(0.816, 0.816, -0.408);
uniform vec3 sphere_center = vec3(-0.4, -0.75, 0.2);
uniform float sphere_radius = 0.25;
uniform float rim_shadow = 1.0;
uniform float sphere_shadow_on = 1.0;

varying vec3 local_pos;

vec2 intersect_cube(vec3 origin, vec3 ray, vec3 cube_min, vec3 cube_max) {
	vec3 t_min = (cube_min - origin) / ray;
	vec3 t_max = (cube_max - origin) / ray;
	vec3 t1 = min(t_min, t_max);
	vec3 t2 = max(t_min, t_max);
	return vec2(max(max(t1.x, t1.y), t1.z), min(min(t2.x, t2.y), t2.z));
}

void vertex() {
	// pool.vert: remap cube-space y [-1,1] into pool space (floor y=-1, rim y=2/12)
	vec3 p = VERTEX;
	p.y = (1.0 - VERTEX.y) * (7.0 / 12.0) - 1.0;
	local_pos = p;      // model transform = identity, so this is world position
	VERTEX = p;
}

void fragment() {
	vec3 point = local_pos;

	vec3 wall_color;
	if (abs(point.x) > 0.999) {
		wall_color = textureLod(tile_tex, point.yz * 0.5 + vec2(1.0, 0.5), 0.0).rgb;
	} else if (abs(point.z) > 0.999) {
		wall_color = textureLod(tile_tex, point.yx * 0.5 + vec2(1.0, 0.5), 0.0).rgb;
	} else {
		wall_color = textureLod(tile_tex, point.xz * 0.5 + 0.5, 0.0).rgb;
	}

	float IOR_AIR = 1.0;
	float IOR_WATER = 1.333;
	float pool_height = 1.0;

	vec3 normal = vec3(0.0, 1.0, 0.0);
	if (abs(point.x) > 0.999) { normal = vec3(-point.x, 0.0, 0.0); }
	else if (abs(point.z) > 0.999) { normal = vec3(0.0, 0.0, -point.z); }

	// Ambient occlusion (corner + sphere proximity)
	float scale = 0.5;
	scale /= length(point);
	scale *= mix(1.0, 1.0 - 0.9 / pow(length(point - sphere_center) / sphere_radius, 4.0), sphere_shadow_on);

	vec3 refracted_light = -refract(-normalize(light_direction), vec3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
	float diffuse = max(0.0, dot(refracted_light, normal));

	vec4 water_info = textureLod(water_tex, point.xz * 0.5 + 0.5, 0.0);
	if (point.y < water_info.r) {
		// Underwater: caustic lighting
		vec2 caustic_uv = 0.75 * (point.xz - point.y * refracted_light.xz / refracted_light.y) * 0.5 + 0.5;
		vec4 caustic = textureLod(caustic_tex, caustic_uv, 0.0);
		float intensity = caustic.r;
		float sphere_shadow = caustic.g;
		if (rim_shadow < 0.5 && intensity < 0.001) { intensity = 0.2; sphere_shadow = 1.0; }
		scale += diffuse * intensity * 2.0 * sphere_shadow;
	} else {
		// Above water: rim shadow at the water edge
		vec2 t = intersect_cube(point, refracted_light, vec3(-1.0, -pool_height, -1.0), vec3(1.0, 2.0, 1.0));
		float shadow_factor = 1.0 / (1.0 + exp(-200.0 / (1.0 + 10.0 * (t.y - t.x)) * (point.y + refracted_light.y * t.y - 2.0 / 12.0)));
		scale += diffuse * mix(1.0, shadow_factor, rim_shadow) * 0.5;
	}

	vec3 final_color = wall_color * scale;
	if (point.y < water_info.r) {
		final_color *= vec3(0.4, 0.9, 1.0) * 1.2;  // underwater tint
	}
	ALBEDO = final_color;
}
