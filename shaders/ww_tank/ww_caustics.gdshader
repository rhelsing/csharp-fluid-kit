// TANK fork of ww_caustics — the caustics pass, projected onto the SLANTED floor.
// Verbatim Wallace (jeantimex/webgpu-water caustics.{vert,frag}, MIT) except that project()
// lands rays on  y = -1 + tilt*(x+1)/2  instead of the flat y = -1, and the surface starts at
// `water_level` instead of 0. The caustic-lookup mapping the consumers use
// (0.75 * (p.xz - p.y * rl.xz/rl.y)) already unprojects along the light ray, so it reads a
// point at ANY floor height correctly — only the producer needed the slope.
//   R = caustic intensity, G = sphere-shadow factor.
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_test_disabled, depth_draw_never, shadows_disabled;

uniform sampler2D water_tex : filter_linear, repeat_disable;
uniform vec3 light_direction = vec3(0.816, 0.816, -0.408);
uniform vec3 sphere_center = vec3(-0.4, -0.75, 0.2);
uniform float sphere_radius = 0.25;
uniform float rim_shadow = 1.0;
uniform float sphere_shadow_on = 1.0;
uniform float caustic_intensity = 0.2;
uniform float water_level = 0.0;
uniform float tilt = 0.0;

varying vec3 v_old_pos;
varying vec3 v_new_pos;

vec2 intersect_cube(vec3 origin, vec3 ray, vec3 cube_min, vec3 cube_max) {
	vec3 t_min = (cube_min - origin) / ray;
	vec3 t_max = (cube_max - origin) / ray;
	vec3 t1 = min(t_min, t_max);
	vec3 t2 = max(t_min, t_max);
	float t_near = max(max(t1.x, t1.y), t1.z);
	float t_far = min(min(t2.x, t2.y), t2.z);
	return vec2(t_near, t_far);
}

vec3 project(vec3 origin, vec3 ray, vec3 refracted_light) {
	float pool_height = 1.0;
	vec3 point = origin;
	vec2 tcube = intersect_cube(origin, ray, vec3(-1.0, -pool_height, -1.0), vec3(1.0, 2.0, 1.0));
	point += ray * tcube.y;
	// [tilt] land on the slanted floor plane instead of y = -1
	float a = tilt * 0.5;
	float fy0 = -1.0 + a;
	float denom = refracted_light.y - a * refracted_light.x;
	float tplane = (fy0 + a * point.x - point.y) / denom;
	return point + refracted_light * tplane;
}

void vertex() {
	// Godot plane is XZ; source plane is XY->(x,z). uv from plane position.
	vec2 pxy = VERTEX.xz;
	vec2 uv = pxy * 0.5 + 0.5;
	vec4 info = textureLod(water_tex, uv, 0.0);

	vec2 ba = info.ba * 0.5;
	vec3 normal = vec3(ba.x, sqrt(max(0.0, 1.0 - dot(ba, ba))), ba.y);

	float IOR_AIR = 1.0;
	float IOR_WATER = 1.333;
	vec3 light_dir = normalize(light_direction);
	vec3 refracted_light = refract(-light_dir, vec3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
	vec3 ray = refract(-light_dir, normal, IOR_AIR / IOR_WATER);

	vec3 pos = vec3(pxy.x, water_level, pxy.y);          // [water_level] lowered surface
	v_old_pos = project(pos, refracted_light, refracted_light);
	v_new_pos = project(pos + vec3(0.0, info.r, 0.0), ray, refracted_light);

	vec2 projected = 0.75 * (v_new_pos.xz - v_new_pos.y * refracted_light.xz / refracted_light.y);
	// Direct clip-space output (bypasses the camera). Y sign flipped vs source to
	// match Godot's render-target orientation (verified on screen).
	POSITION = vec4(projected.x, projected.y, 0.0, 1.0);
}

void fragment() {
	// Intensity from area compression: converging rays => shrinking triangle => bright.
	float old_area = length(dFdx(v_old_pos)) * length(dFdy(v_old_pos));
	float new_area = length(dFdx(v_new_pos)) * length(dFdy(v_new_pos));
	float intensity = old_area / new_area * caustic_intensity;

	float IOR_AIR = 1.0;
	float IOR_WATER = 1.333;
	vec3 light_dir = normalize(light_direction);
	vec3 refracted_light = refract(-light_dir, vec3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);

	// Sphere shadow from distance to the refracted-light ray.
	vec3 dir = (sphere_center - v_new_pos) / sphere_radius;
	vec3 area = cross(dir, refracted_light);
	float shadow = dot(area, area);
	float dist = dot(dir, -refracted_light);
	shadow = 1.0 + (shadow - 1.0) / (0.05 + dist * 0.025);
	shadow = clamp(1.0 / (1.0 + exp(-shadow)), 0.0, 1.0);
	shadow = mix(1.0, shadow, clamp(dist * 2.0, 0.0, 1.0));
	shadow = mix(1.0, shadow, sphere_shadow_on);

	// Rim shadow at pool edges.
	float pool_height = 1.0;
	vec2 t = intersect_cube(v_new_pos, -refracted_light, vec3(-1.0, -pool_height, -1.0), vec3(1.0, 2.0, 1.0));
	float rim = 1.0 / (1.0 + exp(-200.0 / (1.0 + 10.0 * (t.y - t.x)) * (v_new_pos.y - refracted_light.y * t.y - 2.0 / 12.0)));
	intensity *= mix(1.0, rim, rim_shadow);

	ALBEDO = vec3(intensity, shadow, 0.0);
	ALPHA = 1.0;
}
