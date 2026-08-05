#[compute]
#version 450

// One-shot: superpose a solitary wave (sech^2 profile) onto the current state.
// eta = H * sech^2(ks * (x - x0)), hu += cs * eta. Dispatched on demand.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;

layout(push_constant) uniform Push {
	float H;    // wave height (m)
	float x0;   // crest position (m)
	float ks;   // sqrt(3H / (4 d^3))
	float cs;   // sqrt(g (H + d))
	float dx;
	float pad0;
	float pad1;
	float pad2;
} pc;



void main() {
	int N = imageSize(img_state).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}
	vec4 q = imageLoad(img_state, id);
	float B = imageLoad(img_bottom, id).r;
	float h = q.x - B;
	if (h < 0.02) {
		return;
	}
	float x = (float(id.x) + 0.5) * pc.dx;
	float s = 1.0 / cosh(pc.ks * (x - pc.x0));
	float eta = pc.H * s * s;
	q.x += eta;
	q.y += pc.cs * eta;
	imageStore(img_state, id, q);
}
