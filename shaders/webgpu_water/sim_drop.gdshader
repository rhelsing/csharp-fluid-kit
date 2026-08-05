// WebGPU-Water port — SIM: add a circular drop (ripple). Verbatim from
// jeantimex/webgpu-water src/shaders/water/drop.frag.wgsl (Evan Wallace, MIT).
// Cosine falloff bump added to height at `center` (water space [-1,1]).
// Identity pass when strength == 0 (no click this frame).
shader_type canvas_item;
render_mode unshaded, blend_disabled;

uniform vec2 center = vec2(0.0);   // drop position in [-1,1]
uniform float radius = 0.03;
uniform float strength = 0.0;      // + up / - down, 0 = no drop

void fragment() {
	vec4 info = texture(TEXTURE, UV);
	float drop = max(0.0, 1.0 - length(center * 0.5 + 0.5 - UV) / radius);
	drop = 0.5 - cos(drop * PI) * 0.5;
	info.r += drop * strength;
	COLOR = info;
}
