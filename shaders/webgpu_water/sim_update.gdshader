// WebGPU-Water port — SIM: wave-propagation step. Verbatim from
// jeantimex/webgpu-water src/shaders/water/update.frag.wgsl, which ports
// Evan Wallace's WebGL Water (https://madebyevan.com/webgl-water/, MIT).
// Heightfield in one RGBA-float texture: R=height, G=velocity, B=normal.x,
// A=normal.z. Finite-difference wave equation, run 2x/frame. This is a Godot
// canvas_item pass in the nested-SubViewport ping-pong (see scene_15_webgpu_water).
// TEXTURE = previous state (the child viewport's texture).
shader_type canvas_item;
// blend_disabled: write raw RGBA (data lives in G/B/A), never alpha-blend.
render_mode unshaded, blend_disabled;

void fragment() {
	vec4 info = texture(TEXTURE, UV);

	// neighbour heights (texel step)
	vec2 dx = vec2(TEXTURE_PIXEL_SIZE.x, 0.0);
	vec2 dy = vec2(0.0, TEXTURE_PIXEL_SIZE.y);
	float average = (
		texture(TEXTURE, UV - dx).r +
		texture(TEXTURE, UV - dy).r +
		texture(TEXTURE, UV + dx).r +
		texture(TEXTURE, UV + dy).r
	) * 0.25;

	// velocity gains from the difference to the neighbour average, damped
	info.g += (average - info.r) * 2.0;
	info.g *= 0.995;
	// height integrates velocity
	info.r += info.g;

	COLOR = info;
}
