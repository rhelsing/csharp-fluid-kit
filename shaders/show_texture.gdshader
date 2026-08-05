// Minimal unshaded display of a texture (the compute output / stamp-solver field).
// Representative of how the solver surfaces a Texture2Drd via a sampler2D uniform.
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D tex : filter_linear;

void fragment() {
	ALBEDO = texture(tex, UV).rgb;
}
