// WebGPU-Water port — SIM: feedback copy (innermost ping-pong buffer).
// A ViewportTexture can't reference its own Viewport, so the nested-SubViewport
// chain closes the loop by copying the final (normal-pass) state into the
// deepest buffer, 1 frame delayed. blend_disabled so the raw RGBA is preserved.
shader_type canvas_item;
render_mode unshaded, blend_disabled;

void fragment() {
	COLOR = texture(TEXTURE, UV);
}
