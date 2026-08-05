#[compute]
#version 450

// [exp B4] Localized poke: raise the free surface by a Gaussian bump at a world position,
// in place on the current state. The bump then radiates as a ripple through the normal
// KP07 step (like dropping a stone). Skips dry cells.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;

layout(push_constant) uniform Push {
	float px;        // poke centre x (m)
	float pz;        // poke centre z (m)
	float radius;    // m
	float strength;  // m added at centre
	float dx;
	float _p0;
	float _p1;
	float _p2;
} pc;

void main() {
	int N = imageSize(img_state).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}
	float x = (float(id.x) + 0.5) * pc.dx;
	float z = (float(id.y) + 0.5) * pc.dx;
	float dx = x - pc.px;
	float dz = z - pc.pz;
	float r2 = max(pc.radius * pc.radius, 1e-4);
	float bump = pc.strength * exp(-(dx * dx + dz * dz) / r2);
	if (bump < 1e-5) {
		return;
	}
	vec4 q = imageLoad(img_state, id);
	float B = imageLoad(img_bottom, id).r;
	if (q.x - B < 0.02) {   // skip dry cells
		return;
	}
	q.x += bump;
	imageStore(img_state, id, q);
}
