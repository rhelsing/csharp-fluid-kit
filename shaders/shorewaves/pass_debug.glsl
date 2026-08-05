#[compute]
#version 450

// Single-workgroup reduction: total volume, max depth, max speed, wet cells, NaN count.
// Dispatched (1,1,1) once per rendered frame.

layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;
layout(std430, set = 0, binding = 2) restrict buffer DbgBuf {
	float vals[16];
} dbg;

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



shared float s_vol[256];
shared float s_maxh[256];
shared float s_maxs[256];
shared float s_wet[256];
shared float s_nan[256];

float desing(float h, float hq, float k4) {
	float h4 = h * h * h * h;
	return 1.41421356237 * h * hq / sqrt(h4 + max(h4, k4));
}

void main() {
	int N = imageSize(img_state).x;
	uint tid = gl_LocalInvocationID.x;
	float vol = 0.0;
	float mh = 0.0;
	float ms = 0.0;
	float wet = 0.0;
	float nan_c = 0.0;
	for (uint idx = tid; idx < uint(N * N); idx += 256u) {
		ivec2 id = ivec2(int(idx % uint(N)), int(idx / uint(N)));
		if (id.x < 2 || id.x > N - 3 || id.y < 2 || id.y > N - 3) {
			continue;
		}
		vec4 q = imageLoad(img_state, id);
		if (isnan(q.x) || isinf(q.x) || isnan(q.y) || isnan(q.z)) {
			nan_c += 1.0;
			continue;
		}
		vec4 bt = imageLoad(img_bottom, id);
		float B = bt.r;
		float k4 = pow(max(0.01, 1.5 * (bt.a - bt.r)), 4.0);
		float h = max(q.x - B, 0.0);
		vol += h;
		mh = max(mh, h);
		float u = desing(h, q.y, k4);
		float v = desing(h, q.z, k4);
		ms = max(ms, length(vec2(u, v)));
		wet += (h > 1e-4) ? 1.0 : 0.0;
	}
	s_vol[tid] = vol;
	s_maxh[tid] = mh;
	s_maxs[tid] = ms;
	s_wet[tid] = wet;
	s_nan[tid] = nan_c;
	barrier();
	for (uint stride = 128u; stride > 0u; stride >>= 1u) {
		if (tid < stride) {
			s_vol[tid] += s_vol[tid + stride];
			s_maxh[tid] = max(s_maxh[tid], s_maxh[tid + stride]);
			s_maxs[tid] = max(s_maxs[tid], s_maxs[tid + stride]);
			s_wet[tid] += s_wet[tid + stride];
			s_nan[tid] += s_nan[tid + stride];
		}
		barrier();
	}
	if (tid == 0u) {
		dbg.vals[0] = s_vol[0] * pc.dx * pc.dx;
		dbg.vals[1] = s_maxh[0];
		dbg.vals[2] = s_maxs[0];
		dbg.vals[3] = s_wet[0];
		dbg.vals[4] = s_nan[0];
		dbg.vals[5] = pc.time;
	}
}
