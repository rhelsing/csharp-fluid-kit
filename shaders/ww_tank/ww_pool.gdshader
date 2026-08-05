// TANK fork of ww_pool — the pool box (walls + floor) that shows THROUGH and BESIDE the water.
// Verbatim Wallace (jeantimex/webgpu-water pool.{vert,frag}, MIT) plus the tank knobs:
//   tilt        : the floor VERTICES lift with the same ramp the surface raytracer uses,
//                 y = -1 + tilt*(x+1)/2, so the emerged shallow end is real geometry — this is
//                 what you actually see as dry beach where the surface shader discards.
//   water_level : the waterline used for the underwater/above-water split (caustics vs rim shadow).
//   tile_scale  : tile repeats across the pool, so tiles stay physically tile-sized as it scales.
// Geometry stays in normalized pool space [-1,1]; world scale comes from the node transform.
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D tile_tex : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D water_tex : filter_linear, repeat_disable;
uniform sampler2D caustic_tex : filter_linear, repeat_disable;
uniform vec3 light_direction = vec3(0.816, 0.816, -0.408);
uniform vec3 sphere_center = vec3(-0.4, -0.75, 0.2);
uniform float sphere_radius = 0.25;
uniform float rim_shadow = 1.0;
uniform float sphere_shadow_on = 1.0;
uniform float tile_scale = 1.0;
uniform float water_level = 0.0;
uniform float tilt = 0.0;

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
	// [tilt] lift the floor ring (and the wall bottoms that share it) onto the ramp
	if (p.y < -0.999) { p.y += tilt * (p.x + 1.0) * 0.5; }
	local_pos = p;      // model transform is a pure scale, so this is normalized pool space
	VERTEX = p;
}

void fragment() {
	vec3 point = local_pos;

	// [scale] mipmapped texture() — at tile_scale 10 a pinned mip 0 aliases into noise
	vec2 tuv;
	if (abs(point.x) > 0.999) {
		tuv = (point.yz * 0.5 + vec2(1.0, 0.5)) * tile_scale;
	} else if (abs(point.z) > 0.999) {
		tuv = (point.yx * 0.5 + vec2(1.0, 0.5)) * tile_scale;
	} else {
		tuv = (point.xz * 0.5 + 0.5) * tile_scale;
	}
	vec3 wall_color = texture(tile_tex, tuv).rgb;

	float IOR_AIR = 1.0;
	float IOR_WATER = 1.333;
	float pool_height = 1.0;

	vec3 normal = normalize(vec3(-tilt * 0.5, 1.0, 0.0));   // [tilt] slanted floor normal
	if (abs(point.x) > 0.999) { normal = vec3(-point.x, 0.0, 0.0); }
	else if (abs(point.z) > 0.999) { normal = vec3(0.0, 0.0, -point.z); }

	// Ambient occlusion (corner + sphere proximity)
	float scale = 0.5;
	scale /= length(point);
	scale *= mix(1.0, 1.0 - 0.9 / pow(length(point - sphere_center) / sphere_radius, 4.0), sphere_shadow_on);

	vec3 refracted_light = -refract(-normalize(light_direction), vec3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
	float diffuse = max(0.0, dot(refracted_light, normal));

	vec4 water_info = textureLod(water_tex, point.xz * 0.5 + 0.5, 0.0);
	float surface_y = water_level + water_info.r;
	if (point.y < surface_y) {
		// Underwater: caustic lighting
		vec2 caustic_uv = 0.75 * (point.xz - point.y * refracted_light.xz / refracted_light.y) * 0.5 + 0.5;
		vec4 caustic = textureLod(caustic_tex, caustic_uv, 0.0);
		float intensity = caustic.r;
		float sphere_shadow = caustic.g;
		if (rim_shadow < 0.5 && intensity < 0.001) { intensity = 0.2; sphere_shadow = 1.0; }
		scale += diffuse * intensity * 2.0 * sphere_shadow;
	} else {
		// Above water (incl. the dry beach): rim shadow at the water edge
		vec2 t = intersect_cube(point, refracted_light, vec3(-1.0, -pool_height, -1.0), vec3(1.0, 2.0, 1.0));
		float shadow_factor = 1.0 / (1.0 + exp(-200.0 / (1.0 + 10.0 * (t.y - t.x)) * (point.y + refracted_light.y * t.y - 2.0 / 12.0)));
		scale += diffuse * mix(1.0, shadow_factor, rim_shadow) * 0.5;
	}

	vec3 final_color = wall_color * scale;
	if (point.y < surface_y) {
		final_color *= vec3(0.4, 0.9, 1.0) * 1.2;  // underwater tint
	}
	ALBEDO = final_color;
}
