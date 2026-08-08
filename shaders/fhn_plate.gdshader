// Scene 49 display — the FHN medium as living material. u (excitation) drives
// brightness: dark rest → bright wavefront; v (refractory) tints the tail purple.
// Spiral tips glow red (CW) / orange (CCW) from the winding-pass mask.
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D state_tex : filter_linear, repeat_disable;
uniform sampler2D tips_tex : filter_linear, repeat_disable;
uniform float tip_glow = 1.0;
uniform vec3 rest_color : source_color = vec3(0.05, 0.08, 0.16);
uniform vec3 front_color : source_color = vec3(0.55, 0.95, 1.0);
uniform vec3 tail_color : source_color = vec3(0.35, 0.16, 0.55);

varying vec2 uv;

void vertex() {
	uv = VERTEX.xz / 6.0 + 0.5;
}

void fragment() {
	vec2 s = texture(state_tex, uv).rg;
	float ex = clamp((s.r + 1.2) / 3.0, 0.0, 1.0);      // rest -> excited
	float rf = clamp((s.g + 0.62) / 1.4, 0.0, 1.0);     // refractory tail
	vec3 col = mix(rest_color, front_color, ex * ex);
	col = mix(col, tail_color, rf * (1.0 - ex) * 0.8);

	float tip = texture(tips_tex, uv).r;
	if (abs(tip) > 0.01 && tip_glow > 0.0) {
		vec3 tc = tip > 0.0 ? vec3(1.0, 0.15, 0.1) : vec3(1.0, 0.6, 0.1);
		col += tc * min(abs(tip), 1.0) * tip_glow;
	}
	ALBEDO = col;
}
