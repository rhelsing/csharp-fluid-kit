shader_type spatial;

// Minimal asset-free wet-sand terrain for the C# Shorewaves port. No PBR texture sets —
// procedural sand tint darkened + glossed by the solver's ground-memory pass, with
// stranded-foam streaks. Samples tx_ground (r = wetness, g = stranded foam) at UV = the
// terrain's world_xz / domain (which matches the sim grid).
uniform sampler2D tx_ground : filter_linear, repeat_disable;
uniform vec3 sand_col : source_color = vec3(0.78, 0.70, 0.53);
uniform vec3 wet_col : source_color = vec3(0.30, 0.24, 0.18);
uniform vec3 foam_col : source_color = vec3(0.90, 0.92, 0.92);

void fragment() {
	vec2 g = texture(tx_ground, UV).rg;
	float wet = clamp(g.r, 0.0, 1.0);
	float foam = clamp(g.g, 0.0, 1.0);
	// faint procedural grain so the flat sand isn't dead-flat under raking light
	float grain = fract(sin(dot(floor(UV * 900.0), vec2(12.99, 78.23))) * 43758.55) * 0.05;
	vec3 col = mix(sand_col, wet_col, wet) * (0.97 + grain);
	col = mix(col, foam_col, foam * 0.7);
	ALBEDO = col;
	ROUGHNESS = mix(0.95, 0.35, wet);   // wet sand reads glossier
	SPECULAR = mix(0.2, 0.5, wet);
}
