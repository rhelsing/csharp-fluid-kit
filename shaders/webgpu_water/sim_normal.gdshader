// WebGPU-Water port — SIM: compute surface normal from the heightfield.
// Verbatim from jeantimex/webgpu-water src/shaders/water/normal.frag.wgsl
// (Evan Wallace, MIT). Two tangent vectors from height differences, normal =
// cross(dy, dx); store normal.x in B and normal.z in A (normal.y is
// reconstructed as sqrt(1 - b*b - a*a) by consumers).
shader_type canvas_item;
render_mode unshaded, blend_disabled;

void fragment() {
	vec4 info = texture(TEXTURE, UV);
	vec2 d = TEXTURE_PIXEL_SIZE;

	float val_dx = texture(TEXTURE, vec2(UV.x + d.x, UV.y)).r;
	float val_dy = texture(TEXTURE, vec2(UV.x, UV.y + d.y)).r;

	vec3 dx = vec3(d.x, val_dx - info.r, 0.0);
	vec3 dy = vec3(0.0, val_dy - info.r, d.y);

	vec3 normal = normalize(cross(dy, dx));
	info.b = normal.x;
	info.a = normal.z;

	COLOR = info;
}
