#[compute]
#version 450

// Per-frame: surface normal, visible foam concentration, aeration -> txDerived (RGBA16F).

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;
layout(rgba16f, set = 0, binding = 2) uniform restrict writeonly image2D img_derived;

layout(push_constant) uniform Push {
	float dt;
	float dx;
	float g;
	float theta;
	float kappa4;
	float manning;
	float time;
	float k_foam;
} pc;



float desing(float h, float hq) {
	float h4 = h * h * h * h;
	return 1.41421356237 * h * hq / sqrt(h4 + max(h4, pc.kappa4));
}

// free surface of a neighbor, but only if it is wet; else fall back to ours
float wetW(ivec2 id, float w_own) {
	vec4 q = imageLoad(img_state, id);
	float B = imageLoad(img_bottom, id).r;
	return (q.x - B > 1e-4) ? q.x : w_own;
}

void main() {
	int N = imageSize(img_state).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}
	ivec2 cid = clamp(id, ivec2(2), ivec2(N - 3));
	vec4 q = imageLoad(img_state, cid);
	float B = imageLoad(img_bottom, cid).r;
	float h = max(q.x - B, 0.0);
	if (h < 1e-4) {
		imageStore(img_derived, id, vec4(0.0, 0.0, 0.0, 0.0));
		return;
	}
	float wl = wetW(cid + ivec2(-1, 0), q.x);
	float wr = wetW(cid + ivec2(1, 0), q.x);
	float ws = wetW(cid + ivec2(0, -1), q.x);
	float wn = wetW(cid + ivec2(0, 1), q.x);
	vec3 n = normalize(vec3(wl - wr, 2.0 * pc.dx, ws - wn));

	float cfoam = clamp(desing(h, q.w), 0.0, 1.0);
	float u = desing(h, q.y);
	float v = desing(h, q.z);
	float Fr = length(vec2(u, v)) / sqrt(pc.g * max(h, 1e-4));
	float aeration = clamp(0.7 * smoothstep(1.0, 1.4, Fr) + 0.6 * cfoam, 0.0, 1.0);
	imageStore(img_derived, id, vec4(n.x, n.z, cfoam, aeration));
}
