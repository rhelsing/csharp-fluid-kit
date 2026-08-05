// WebGPU-Water port — SIM: sphere displaces water. Verbatim from
// jeantimex/webgpu-water src/shaders/water/sphere.frag.wgsl (Evan Wallace, MIT).
// Water rises where the sphere WAS (old_center) and falls where it IS
// (new_center), a Gaussian-ish column of displaced volume per texel.
shader_type canvas_item;
render_mode unshaded, blend_disabled;

uniform vec3 old_center = vec3(0.0);
uniform vec3 new_center = vec3(0.0);
uniform float radius = 0.25;

float volume_in_sphere(vec3 center, vec2 uv, float r) {
	vec3 p = vec3(uv.x * 2.0 - 1.0, 0.0, uv.y * 2.0 - 1.0);
	float dist = length(p - center);
	float t = dist / r;
	float dy = exp(-pow(t * 1.5, 6.0));
	float ymin = min(0.0, center.y - dy);
	float ymax = min(max(0.0, center.y + dy), ymin + 2.0 * dy);
	return (ymax - ymin) * 0.1;
}

void fragment() {
	vec4 info = texture(TEXTURE, UV);
	info.r += volume_in_sphere(old_center, UV, radius);
	info.r -= volume_in_sphere(new_center, UV, radius);
	COLOR = info;
}
