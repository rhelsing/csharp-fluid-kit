#[compute]
#version 450

// Scene 21 — the MNA "stamp solver" on the GPU, matrix-free.
//
// One Jacobi relaxation sweep of the IMPLICIT damped-wave step. Backward-Euler on
// the wave equation gives, per cell:
//     (1 + a + 4*beta) * h_next  -  beta * sum(neighbour h_next)  =  rhs
// which is exactly a stamped linear system A h = b:
//     beta            = the pipe conductance stamp to each of the 4 neighbours
//                       (beta = dt^2 * c^2),
//     (1 + a + 4*beta)= the diagonal (cell storage + its 4 pipes + damping),
//     rhs = 2*h_curr - h_prev + a*h_prev + force = the companion source.
// We never build A: the stamp defines the 5-point stencil and we RELAX it (Jacobi)
// K sweeps per tick, ping-ponging the iterate on the GPU. Unconditionally stable.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(r32f, set = 0, binding = 0) uniform image2D h_curr;   // h at t         (read)
layout(r32f, set = 1, binding = 0) uniform image2D h_prev;   // h at t-dt      (read)
layout(r32f, set = 2, binding = 0) uniform image2D iter_in;  // current iterate (read)
layout(r32f, set = 3, binding = 0) uniform image2D iter_out; // next iterate    (write)

layout(push_constant, std430) uniform Params {
	vec2 size;    // grid width, height (px)
	float beta;   // dt^2 * c^2   -> neighbour conductance stamp
	float a;      // gamma*dt/2   -> damping
	vec4 drop;    // xy = centre px, z = radius px, w = strength (0 = no poke)
} p;

void main() {
	ivec2 c = ivec2(gl_GlobalInvocationID.xy);
	ivec2 mx = ivec2(p.size) - ivec2(1);
	if (c.x > mx.x || c.y > mx.y) {
		return;
	}

	float hc = imageLoad(h_curr, c).r;
	float hp = imageLoad(h_prev, c).r;

	// one-timestep localised forcing = an interactive poke
	float force = 0.0;
	if (p.drop.w != 0.0) {
		vec2 d = vec2(c) - p.drop.xy;
		force = p.drop.w * exp(-dot(d, d) / (p.drop.z * p.drop.z));
	}

	// companion source (constant across the K Jacobi sweeps of this tick)
	float rhs = 2.0 * hc - hp + p.a * hp + force;

	// 4-neighbour Laplacian of the iterate; clamp = Neumann (reflective) walls
	float nE = imageLoad(iter_in, clamp(c + ivec2(1, 0), ivec2(0), mx)).r;
	float nW = imageLoad(iter_in, clamp(c + ivec2(-1, 0), ivec2(0), mx)).r;
	float nN = imageLoad(iter_in, clamp(c + ivec2(0, 1), ivec2(0), mx)).r;
	float nS = imageLoad(iter_in, clamp(c + ivec2(0, -1), ivec2(0), mx)).r;

	float h = (rhs + p.beta * (nE + nW + nN + nS)) / (1.0 + p.a + 4.0 * p.beta);
	imageStore(iter_out, c, vec4(h, 0.0, 0.0, 0.0));
}
