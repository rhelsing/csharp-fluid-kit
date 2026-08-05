// Scene 07 display — the fluid's dye field as glowing smoke on a flat plane.
// Unshaded: the sim is the picture, not the lighting.
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D dye_tex : filter_linear, repeat_disable;
uniform float gain = 1.0;
uniform vec3 smoke_color : source_color = vec3(1.0, 0.62, 0.28);
uniform vec3 bg_color : source_color = vec3(0.02, 0.03, 0.06);

void fragment() {
    float d = clamp(texture(dye_tex, UV).r * gain, 0.0, 1.0);
    // a touch of tone shaping so wisps read against the dark background
    ALBEDO = mix(bg_color, smoke_color, d) + smoke_color * d * d * 0.4;
}
