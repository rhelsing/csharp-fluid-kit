// uwkit — a ready-made material for a submerged object: flat colour plus mask-gated
// caustics. Drop it on rocks, a seabed, a hull. Objects with their own shader should
// #include uw_caustics.gdshaderinc instead and add uw_caustics(...) to their EMISSION.
shader_type spatial;

#include "res://uwkit/uw_caustics.gdshaderinc"

uniform vec3 base_color : source_color = vec3(0.80, 0.72, 0.52);
uniform float base_roughness : hint_range(0.0, 1.0, 0.01) = 0.9;
uniform float base_metallic : hint_range(0.0, 1.0, 0.01) = 0.0;

varying vec3 v_world_pos;
varying vec3 v_world_normal;

void vertex() {
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	ALBEDO = base_color;
	ROUGHNESS = base_roughness;
	METALLIC = base_metallic;
	EMISSION = uw_caustics(v_world_pos, v_world_normal, SCREEN_UV);
}
