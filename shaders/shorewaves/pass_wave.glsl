#[compute]
#version 450

// Explicit leapfrog LINEAR wave equation  d2h/dt2 = c^2 * laplacian(h)  on a heightfield.
// 3-level leapfrog via 2 ping-pong buffers: binding 1 holds the PREVIOUS level and is
// overwritten in place with the NEXT level (each thread touches only its own cell there,
// so read+write is race-free); binding 0 is the CURRENT level (neighbours read-only).
// Optional varying wave speed c^2 = g * depth (depth = -bed) gives shoaling. Poke + a
// west-edge sinusoidal wavemaker provide forcing.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(r32f, set = 0, binding = 0) uniform restrict readonly image2D img_curr;
layout(r32f, set = 0, binding = 1) uniform restrict image2D img_prevout;   // read prev, write next
layout(r32f, set = 0, binding = 2) uniform restrict readonly image2D img_bathy;

layout(push_constant) uniform Push {
	float dt;
	float dx;
	float g;
	float damp;
	float const_c2;       // used when use_bathy == 0
	float use_bathy;      // 0 / 1
	float poke_px;        // world m
	float poke_pz;
	float poke_r;
	float poke_str;
	float wave_amp;
	float wave_period;
	float time;
	float use_wavemaker;  // 0 / 1
	float _p0;
	float _p1;
} pc;

void main() {
	int N = imageSize(img_curr).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}

	float hc = imageLoad(img_curr, id).r;
	float hp = imageLoad(img_prevout, id).r;
	float hl = imageLoad(img_curr, ivec2(max(id.x - 1, 0), id.y)).r;
	float hr = imageLoad(img_curr, ivec2(min(id.x + 1, N - 1), id.y)).r;
	float hd = imageLoad(img_curr, ivec2(id.x, max(id.y - 1, 0))).r;
	float hu = imageLoad(img_curr, ivec2(id.x, min(id.y + 1, N - 1))).r;
	float lap = hl + hr + hd + hu - 4.0 * hc;

	float c2 = pc.const_c2;
	if (pc.use_bathy > 0.5) {
		float bed = imageLoad(img_bathy, id).r;
		c2 = pc.g * max(-bed, 0.02);   // still-water depth = -bed; ~0 at the shoreline
	}
	float cfl = clamp(c2 * pc.dt * pc.dt / (pc.dx * pc.dx), 0.0, 0.45);   // stability clamp

	float hnew = 2.0 * hc - hp + cfl * lap;
	hnew = hc + (hnew - hc) * (1.0 - pc.damp);

	if (pc.poke_str > 0.0) {
		float x = (float(id.x) + 0.5) * pc.dx;
		float z = (float(id.y) + 0.5) * pc.dx;
		float d2 = (x - pc.poke_px) * (x - pc.poke_px) + (z - pc.poke_pz) * (z - pc.poke_pz);
		hnew += pc.poke_str * exp(-d2 / max(pc.poke_r * pc.poke_r, 1e-4));
	}
	if (pc.use_wavemaker > 0.5 && id.x < 3) {
		hnew = pc.wave_amp * sin(6.28318530718 * pc.time / max(pc.wave_period, 0.1));   // Dirichlet drive
	}

	imageStore(img_prevout, id, vec4(hnew, 0.0, 0.0, 0.0));
}
