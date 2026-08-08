shader_type spatial;
render_mode unshaded;

// Scene 18 — milk in coffee, viewed from above. The dye field maps coffee → milk
// through a gamma'd blend; a cheap gradient-relief term lights the cream folds so
// filaments read as sitting on the surface; a ceramic cup rim masks the square sim.

uniform sampler2D dye_tex : filter_linear, repeat_disable;
uniform vec3 coffee_color : source_color = vec3(0.180, 0.098, 0.048);
uniform vec3 milk_color : source_color = vec3(0.964, 0.937, 0.890);
uniform vec3 cup_color : source_color = vec3(0.92, 0.90, 0.87);
uniform vec3 table_color : source_color = vec3(0.35, 0.26, 0.20);
uniform float milk_gamma : hint_range(0.2, 3.0) = 0.85;
uniform float relief : hint_range(0.0, 3.0) = 1.0;
uniform float cup_radius : hint_range(0.2, 0.5) = 0.46;
uniform float rim_width : hint_range(0.005, 0.1) = 0.035;
uniform float texel = 0.00390625;   // 1 / grid

void fragment() {
	vec2 uv = UV;
	float d = texture(dye_tex, uv).r;
	float m = pow(clamp(d, 0.0, 1.0), milk_gamma);
	vec3 col = mix(coffee_color, milk_color, m);

	// gradient relief: a fixed light from upper-left rakes the cream folds
	float gx = texture(dye_tex, uv + vec2(texel, 0.0)).r - texture(dye_tex, uv - vec2(texel, 0.0)).r;
	float gy = texture(dye_tex, uv + vec2(0.0, texel)).r - texture(dye_tex, uv - vec2(0.0, texel)).r;
	col *= 1.0 + relief * clamp(-gx - gy, -0.35, 0.35);

	// specular sheen on the liquid surface — brightest on the dark coffee
	col += vec3(0.03, 0.03, 0.035) * (1.0 - m);

	// cup mask: liquid → rim ring → table
	float r = distance(uv, vec2(0.5));
	float rimIn = smoothstep(cup_radius - 0.004, cup_radius, r);
	float rimOut = smoothstep(cup_radius + rim_width, cup_radius + rim_width + 0.004, r);
	col = mix(col, cup_color * (1.0 - 0.35 * smoothstep(cup_radius, cup_radius + rim_width, r)), rimIn);
	col = mix(col, table_color, rimOut);

	ALBEDO = col;
}
