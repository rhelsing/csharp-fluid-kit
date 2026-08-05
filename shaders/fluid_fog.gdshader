// Scene 10 fog — samples the 3D fluid's density volume (a Texture3DRD) as Godot
// volumetric fog. shader_type fog runs inside the engine's froxel volumetrics — real
// volumetric smoke with no custom ray marching. UVW is the 0..1 position in the box.
shader_type fog;

uniform sampler3D density_tex : filter_linear;
uniform float gain = 8.0;
uniform vec3 smoke_albedo : source_color = vec3(0.85, 0.88, 0.95);
uniform vec3 smoke_emit : source_color = vec3(1.0, 0.55, 0.25);
uniform float emit_energy = 0.35;

void fog() {
    float d = texture(density_tex, UVW).r * gain;
    DENSITY = clamp(d, 0.0, 1.0);
    ALBEDO = smoke_albedo;
    EMISSION = smoke_emit * clamp(d, 0.0, 1.0) * emit_energy;
}
