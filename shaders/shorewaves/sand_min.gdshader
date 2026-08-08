shader_type spatial;

// Minimal asset-free wet-sand terrain for the C# Shorewaves port. No PBR texture sets —
// procedural sand tint darkened + glossed by the solver's ground-memory pass, with
// stranded-foam streaks. Samples tx_ground (r = wetness, g = stranded foam) at UV = the
// terrain's world_xz / domain (which matches the sim grid).
//
// Every knob below defaults to the ORIGINAL behaviour, so scene 17 — which sets none of
// them — is byte-for-byte the shader it was. Scene 26 drives them from sliders.
uniform sampler2D tx_ground : filter_linear, repeat_disable;
uniform vec3 sand_col : source_color = vec3(0.78, 0.70, 0.53);
uniform vec3 wet_col : source_color = vec3(0.30, 0.24, 0.18);
uniform vec3 foam_col : source_color = vec3(0.90, 0.92, 0.92);

// How far the wet band is allowed to travel toward wet_col. 1.0 = the original, which
// lands on a near-black (0.30,0.24,0.18) wherever wetness saturates — the swash band reads
// as a burnt stripe rather than damp sand. Lower it to keep the sand sandy when wet.
uniform float wet_darkness : hint_range(0.0, 1.0) = 1.0;
// Gain on the wetness signal BEFORE clamping — controls how much AREA reads wet, separately
// from how dark that area gets. Below 1 pulls the waterline in; above 1 spreads it.
uniform float wet_gain : hint_range(0.0, 3.0) = 1.0;
uniform float sand_brightness : hint_range(0.2, 2.0) = 1.0;
uniform float foam_strength : hint_range(0.0, 1.0) = 0.7;
uniform float wet_gloss : hint_range(0.0, 1.0) = 1.0;   // scales the roughness/specular shift

void fragment() {
	vec2 g = texture(tx_ground, UV).rg;
	float wet = clamp(g.r * wet_gain, 0.0, 1.0);
	float foam = clamp(g.g, 0.0, 1.0);
	// faint procedural grain so the flat sand isn't dead-flat under raking light
	float grain = fract(sin(dot(floor(UV * 900.0), vec2(12.99, 78.23))) * 43758.55) * 0.05;
	vec3 col = mix(sand_col * sand_brightness, wet_col, wet * wet_darkness) * (0.97 + grain);
	col = mix(col, foam_col, foam * foam_strength);
	ALBEDO = col;
	ROUGHNESS = mix(0.95, mix(0.95, 0.35, wet_gloss), wet);   // wet sand reads glossier
	SPECULAR = mix(0.2, mix(0.2, 0.5, wet_gloss), wet);
}
