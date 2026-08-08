shader_type spatial;
render_mode cull_front, depth_draw_always;

// blob_raymarch — render the 3D stamp field's isosurface by RAYMARCHING it directly, with
// no mesh, no GPU->CPU readback and no per-frame polygonization.
//
// Why this exists. Scene 08 originally read the 48^3 volume back to the CPU every frame and
// marching-tets'd it into an ArrayMesh. Instrumented, that path cost:
//     readback 2.46ms + isosurface 9.93ms + mesh 2.32ms = 14.7ms   (after a 3.6x optimisation)
// against a GPU solve of 0.04ms. The field is ALREADY an image3D on the GPU; sampling it in a
// fragment shader deletes all three costs at once. A Texture3DRD wraps the RD texture live —
// the same trick StampFog3D uses for its density volume.
//
// cull_front, not cull_back: we draw the BACK faces of the bounding box so the volume still
// renders when the camera is inside it. The ray always starts at the camera and is clipped to
// the box analytically, so which face rasterizes only decides where fragments exist.
//
// DEPTH is written from the hit point, so the blob occludes and is occluded by ordinary scene
// geometry. Without it this would be a flat screen-space sprite that everything draws through.

uniform sampler3D field;
uniform float iso = 0.55;
uniform int steps : hint_range(16, 256) = 96;
uniform vec3 albedo : source_color = vec3(0.35, 0.85, 0.45);
uniform float roughness : hint_range(0.0, 1.0) = 0.22;

varying vec3 v_local;

void vertex() {
    // Godot's BoxMesh spans [-0.5, 0.5]; shift to [0,1] so local space IS the field's texture
    // coordinate space and no rescaling is needed anywhere in the march.
    v_local = VERTEX + vec3(0.5);
}

// Slab test against the unit cube. Returns false when the ray misses entirely.
bool box_hit(vec3 ro, vec3 rd, out float t0, out float t1) {
    vec3 inv = 1.0 / rd;
    vec3 a = (vec3(0.0) - ro) * inv;
    vec3 b = (vec3(1.0) - ro) * inv;
    vec3 lo = min(a, b);
    vec3 hi = max(a, b);
    t0 = max(max(lo.x, lo.y), lo.z);
    t1 = min(min(hi.x, hi.y), hi.z);
    return t1 > max(t0, 0.0);
}

// Central-difference gradient. On the GPU this is 6 hardware-filtered samples and is cheap —
// exactly the operation that was pathological on the CPU (48 scattered reads per normal).
vec3 grad(vec3 p, float h) {
    return vec3(
        texture(field, p + vec3(h, 0.0, 0.0)).r - texture(field, p - vec3(h, 0.0, 0.0)).r,
        texture(field, p + vec3(0.0, h, 0.0)).r - texture(field, p - vec3(0.0, h, 0.0)).r,
        texture(field, p + vec3(0.0, 0.0, h)).r - texture(field, p - vec3(0.0, 0.0, h)).r);
}

void fragment() {
    mat4 inv_model = inverse(MODEL_MATRIX);
    vec3 cam_local = (inv_model * INV_VIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz + vec3(0.5);
    vec3 rd = normalize(v_local - cam_local);

    float t0, t1;
    if (!box_hit(cam_local, rd, t0, t1)) {
        discard;
    }
    t0 = max(t0, 0.0);

    float dt = (t1 - t0) / float(steps);
    float t = t0;
    float prev = texture(field, cam_local + rd * t).r;
    bool hit = false;
    float t_hit = t0;

    for (int i = 1; i <= steps; i++) {
        float tn = t0 + dt * float(i);
        float cur = texture(field, cam_local + rd * tn).r;
        if (cur >= iso) {
            // Linear crossing between the last two samples — one interpolation is enough at
            // this step count and avoids a bisection loop.
            float denom = cur - prev;
            float frac = abs(denom) < 1e-8 ? 0.0 : (iso - prev) / denom;
            t_hit = tn - dt * (1.0 - clamp(frac, 0.0, 1.0));
            hit = true;
            break;
        }
        prev = cur;
        t = tn;
    }

    if (!hit) {
        discard;
    }

    vec3 p = cam_local + rd * t_hit;

    // Field is high inside, low outside -> outward normal is -gradient.
    vec3 g = grad(p, 1.5 / 48.0);
    vec3 n_local = length(g) < 1e-8 ? vec3(0.0, 1.0, 0.0) : normalize(-g);

    ALBEDO = albedo;
    ROUGHNESS = roughness;
    METALLIC = 0.0;
    NORMAL = normalize((VIEW_MATRIX * MODEL_MATRIX * vec4(n_local, 0.0)).xyz);

    // p is in [0,1] field space; MODEL_MATRIX expects the mesh's own [-0.5, 0.5].
    vec4 clip = PROJECTION_MATRIX * VIEW_MATRIX * MODEL_MATRIX * vec4(p - vec3(0.5), 1.0);
    DEPTH = clip.z / clip.w;
}
