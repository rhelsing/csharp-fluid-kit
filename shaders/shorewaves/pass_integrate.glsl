#[compute]
#version 450

// Euler step + semi-implicit Manning friction + dry clamp + velocity clamp
// + foam sources. Reads state[cur] and txR, writes state[next].

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;
layout(rgba32f, set = 0, binding = 2) uniform restrict readonly image2D img_r;
layout(rgba32f, set = 0, binding = 3) uniform restrict writeonly image2D img_state_out;

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


const float H_DRY = 1e-5;
const float H_FRICTION = 3e-4;
const float V_CLAMP = 12.0;

float desing(float h, float hq, float k4) {
	float h4 = h * h * h * h;
	return 1.41421356237 * h * hq / sqrt(h4 + max(h4, k4));
}

void main() {
	int N = imageSize(img_state).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}
	vec4 q = imageLoad(img_state, id);
	if (id.x < 2 || id.x > N - 3 || id.y < 2 || id.y > N - 3) {
		// ghosts pass through; pass_boundary rewrites them afterwards
		imageStore(img_state_out, id, q);
		return;
	}
	vec4 R = imageLoad(img_r, id);
	vec4 bot = imageLoad(img_bottom, id);
	float B = bot.r;
	// steep-neighborhood desingularization (bot.a = max B over 3x3)
	float k4 = pow(max(0.01, 1.5 * (bot.a - bot.r)), 4.0);

	float h = max(q.x - B, 0.0);
	float u = desing(h, q.y, k4);
	float v = desing(h, q.z, k4);
	float speed = length(vec2(u, v));
	float fric = (h > H_FRICTION)
		? pc.g * pc.manning * pc.manning * speed / pow(max(h, H_FRICTION), 4.0 / 3.0)
		: 0.0;

	vec4 qn;
	qn.x = q.x + pc.dt * R.x;
	qn.y = (q.y + pc.dt * R.y) / (1.0 + pc.dt * fric);
	qn.z = (q.z + pc.dt * R.z) / (1.0 + pc.dt * fric);
	qn.w = q.w + pc.dt * R.w;

	float hn = qn.x - B;
	if (hn < H_DRY) {
		imageStore(img_state_out, id, vec4(B, 0.0, 0.0, 0.0));
		return;
	}

	float un = desing(hn, qn.y, k4);
	float vn = desing(hn, qn.z, k4);
	float sp = length(vec2(un, vn));
	if (sp > V_CLAMP) {
		float s = V_CLAMP / sp;
		un *= s;
		vn *= s;
		qn.y = hn * un;
		qn.z = hn * vn;
		sp = V_CLAMP;
	}

	// ---- foam sources ----
	float detadt = R.x;                      // d(eta)/dt is the mass RHS
	float cb = sqrt(pc.g * max(hn, 1e-4));
	// Kennedy et al. breaking criterion: initiation at eta_t = 0.65 sqrt(gh),
	// smooth onset from 0.35 (cessation-ish) to 0.65
	float brk = smoothstep(0.35 * cb, 0.65 * cb, detadt);
	float Fr = sp / cb;
	float bore = smoothstep(1.05, 1.35, Fr);  // supercritical white bore
	float swash = step(hn, 0.12) * smoothstep(0.5, 0.9, sp); // shoreline foam band
	// no foam from the artificial wavemaker/relaxation zone (west 100-128 cells,
	// scales with dx: ~5-6.4 m on the beach grid, ~100-128 m on the ocean grid)
	float west_mask = smoothstep(100.0 * pc.dx, 128.0 * pc.dx, (float(id.x) + 0.5) * pc.dx);
	float inject = (2.0 * brk + 0.8 * bore + 1.5 * swash) * hn * west_mask;
	qn.w = clamp(qn.w * exp(-pc.dt / 4.0) + pc.k_foam * inject * pc.dt, 0.0, hn);

	imageStore(img_state_out, id, qn);
}
