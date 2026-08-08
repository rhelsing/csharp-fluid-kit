shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, specular_schlick_ggx;

// liquid_splat — splats you cannot see, because the NORMAL comes from the liquid field rather
// than from the sphere it is drawn on.
//
// The splats are only there to put fragments on screen in roughly the right place. If you shade
// them with their own geometry you see balls; if you shade every fragment with the gradient of
// the liquid-fraction VOLUME at that world position, adjacent splats all read the same smooth
// field and merge into one continuous surface. Same trick that makes scene 25's water work:
// the geometry is dumb and the DETAIL LIVES IN THE NORMAL (that shader runs normal_gain 1508 on
// a flat displaced plane). A 64^3 volume looks like goo when its detail is geometry; it looks
// like water when its detail is shading.
//
// This is also the cheap half of screen-space fluid rendering. The expensive half — render
// depth, bilateral-blur it, rebuild normals from the blurred depth — buys silhouette smoothing
// too, and is the upgrade if the splat edges still read as lumpy.
//
// Trilinear filtering on the 3D texture does most of the smoothing for free: the gradient of a
// filtered field is continuous even though the field is only 64 cells across.

uniform sampler3D liquid_tex : filter_linear, repeat_disable;
uniform vec3 grid_dims = vec3(64.0);
uniform vec3 grid_origin = vec3(0.0);
uniform float grid_scale = 1.0;

uniform vec3 deep_color : source_color = vec3(0.04, 0.20, 0.30);
uniform vec3 tint : source_color = vec3(0.45, 0.78, 0.90);
uniform float absorption = 1.4;
uniform float rough = 0.14;
uniform float fresnel_power = 4.0;

// Fine ripple detail, added to the normal rather than the geometry — the ripples inside a
// barrel come from here, not from the sim, which will never have the resolution for them.
uniform float detail_gain = 0.35;
uniform float detail_freq = 9.0;

varying vec3 v_world;

void vertex() {
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float liq(vec3 g) {
    return texture(liquid_tex, clamp(g / grid_dims, vec3(0.001), vec3(0.999))).r;
}

// Cheap value-noise gradient for the detail normal.
float hashn(vec3 p) {
    return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
}

void fragment() {
    vec3 g = (v_world - grid_origin) / grid_scale;

    // Central difference on the FIELD, not the sphere. One cell either side.
    float e = 1.0;
    vec3 n_field = vec3(
        liq(g + vec3(e, 0.0, 0.0)) - liq(g - vec3(e, 0.0, 0.0)),
        liq(g + vec3(0.0, e, 0.0)) - liq(g - vec3(0.0, e, 0.0)),
        liq(g + vec3(0.0, 0.0, e)) - liq(g - vec3(0.0, 0.0, e)));

    // Liquid is high inside, low outside -> outward normal is -gradient. Fall back to the
    // sphere's own normal only where the field is flat (deep interior), which is never visible.
    vec3 nrm = length(n_field) < 1e-5 ? NORMAL : normalize(-n_field);

    // Detail: perturb along two axes so it reads as surface chop rather than a bump map.
    vec3 d = vec3(hashn(floor(g * detail_freq)),
                  hashn(floor(g * detail_freq) + 17.0),
                  hashn(floor(g * detail_freq) + 43.0)) - 0.5;
    nrm = normalize(nrm + d * detail_gain);

    vec3 v = normalize(-VERTEX);
    vec3 nv = normalize((VIEW_MATRIX * vec4(nrm, 0.0)).xyz);
    float facing = clamp(abs(dot(nv, v)), 0.0, 1.0);

    float t = 1.0 - exp(-absorption / max(0.15, facing));
    float fres = pow(1.0 - facing, fresnel_power);

    ALBEDO = mix(tint, deep_color, t) + fres * 0.35;
    ROUGHNESS = rough;
    METALLIC = 0.0;
    SPECULAR = 0.6;
    NORMAL = nv;
}
