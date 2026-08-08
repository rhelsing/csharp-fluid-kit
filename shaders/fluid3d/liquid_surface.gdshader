shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, specular_schlick_ggx;

// liquid_surface — scene 25's water LOOK, on a mesh instead of a heightfield.
//
// tank_water_mna.gdshader is heightfield-only: it takes `sampler2D water_tex` (r32f height)
// and derives both displacement and normals from it, which cannot express a multivalued
// surface — no curl, no bubble. These scenes get their surface from marching tets, so the
// normal arrives on the vertex and everything height-derived is dropped.
//
// KEPT from the tank shader (same model, same names, so the knobs transfer):
//   deep_color / tint / absorption   — analytic depth tint
//   refraction + screen_tex          — screen-space refraction
//   specular_schlick_ggx             — same specular
// DROPPED: caustics, shoaling gain, the height/normal sampling, the debug field viz.
//
// Depth tint is driven by view-space thickness rather than the tank's floor-distance, because
// there is no floor plane here — a bubble has water in front of AND behind it.

uniform vec3 deep_color : source_color = vec3(0.02, 0.22, 0.32);
uniform vec3 tint : source_color = vec3(0.55, 0.88, 0.95);
uniform float absorption = 1.6;
uniform float refraction = 0.03;
uniform float roughness_v = 0.06;
uniform float fresnel_power = 5.0;
uniform float thickness_scale = 0.35;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear, repeat_disable;

void fragment() {
    vec3 n = normalize(NORMAL);
    vec3 v = normalize(-VERTEX);

    // How much water the eye is looking through. Grazing angles see more, which is what makes
    // a thin sheet read as thin and a body of water read as deep.
    float facing = clamp(dot(n, v), 0.0, 1.0);
    float thickness = thickness_scale / max(0.08, facing);
    float t = 1.0 - exp(-absorption * thickness);
    vec3 body = mix(tint, deep_color, t);

    // Screen-space refraction, same idea as the tank: offset the background read by the
    // surface normal. Small offsets only — this is a look, not a physical trace.
    vec2 uv = SCREEN_UV + n.xy * refraction;
    vec3 behind = texture(screen_tex, clamp(uv, vec2(0.001), vec2(0.999))).rgb;

    float fres = pow(1.0 - facing, fresnel_power);

    ALBEDO = mix(behind * body, body, clamp(t + fres, 0.0, 1.0));
    ROUGHNESS = roughness_v;
    METALLIC = 0.0;
    SPECULAR = 0.5;
    NORMAL = n;
}
