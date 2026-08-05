#[compute]
#version 450

// Per-frame ground memory: wetness (drying tau 45 s), stranded foam lace
// (deposited when a cell dries, tau 10 s), wall/plunge impact intensity
// (tau 0.3 s). Ping-pong RGBA16F. A channel = was-wet flag.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;
layout(rgba16f, set = 0, binding = 2) uniform restrict readonly image2D img_ground_in;
layout(rgba16f, set = 0, binding = 3) uniform restrict writeonly image2D img_ground_out;
layout(rgba32f, set = 0, binding = 4) uniform restrict readonly image2D img_r;

layout(push_constant) uniform Push {
	float dt_frame;
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

void main() {
	int N = imageSize(img_state).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}
	vec4 q = imageLoad(img_state, id);
	vec4 bot = imageLoad(img_bottom, id);
	vec4 prev = imageLoad(img_ground_in, id);
	float h = max(q.x - bot.r, 0.0);
	bool wet_now = h > 3e-4;
	float dtf = pc.dt_frame;

	float wetness = wet_now ? 1.0 : prev.r * exp(-dtf / 45.0);
	float stranded = prev.g * exp(-dtf / 10.0);
	float cfoam = clamp(desing(h, q.w), 0.0, 1.0);
	// A channel remembers the foam concentration while the cell was last wet;
	// on the drying transition that memory becomes the stranded lace arc.
	if (!wet_now && prev.a > 0.02) {
		stranded = max(stranded, clamp(prev.a * 1.2, 0.0, 1.0));
	}
	if (wet_now) {
		stranded *= exp(-dtf / 0.12); // re-wetting sweeps arcs away fast
	}

	// --- impact sources ---
	float impact = prev.b * exp(-dtf / 0.3);
	if (wet_now) {
		float u = desing(h, q.y);
		float v = desing(h, q.z);
		// wall impact: momentum toward a neighbor whose bed towers > 1 m above us
		float dBe = imageLoad(img_bottom, ivec2(min(id.x + 1, N - 1), id.y)).r - bot.r;
		float dBw = imageLoad(img_bottom, ivec2(max(id.x - 1, 0), id.y)).r - bot.r;
		float dBn = imageLoad(img_bottom, ivec2(id.x, min(id.y + 1, N - 1))).r - bot.r;
		float dBs = imageLoad(img_bottom, ivec2(id.x, max(id.y - 1, 0))).r - bot.r;
		float toward = 0.0;
		if (dBe > 1.0) { toward = max(toward, u * h); }
		if (dBw > 1.0) { toward = max(toward, -u * h); }
		if (dBn > 1.0) { toward = max(toward, v * h); }
		if (dBs > 1.0) { toward = max(toward, -v * h); }
		if (toward > 0.15) {
			impact = max(impact, clamp(toward * 2.0, 0.0, 1.0));
		}
		// plunging-breaker impact on the open beach: rising fast + steep + supercritical
		float detadt = imageLoad(img_r, id).x;
		float Fr = length(vec2(u, v)) / sqrt(pc.g * max(h, 1e-4));
		if (detadt > 0.35 && Fr > 1.1 && h > 0.05) {
			impact = max(impact, clamp(0.5 * detadt, 0.0, 0.6));
		}
	}

	imageStore(img_ground_out, id, vec4(wetness, stranded, impact, wet_now ? cfoam : 0.0));
}
