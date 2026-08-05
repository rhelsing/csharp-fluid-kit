#[compute]
#version 450

// Scene 25 — the WAVE TANK as one stamped MNA system, solved matrix-free.
//
// Fork of mna_wave.glsl. Where that kernel had a single global beta, every term the explicit tank
// grew over scene 23 is now a STAMP into the same linear system A·h = b, and one Jacobi sweep
// relaxes all of them together:
//
//   pipe conductance   beta_face = dt^2 * g * h_face     <- BATHYMETRY. Depth IS the conductance,
//                                                           so shoaling/refraction are intrinsic,
//                                                           not a coefficient hack, and there is
//                                                           no CFL ceiling to normalise against.
//   cell storage       1                                 <- the capacitor
//   damping            a                                 <- resistor to ground, flat loss
//   chop damping       chop * Lap(h_curr - h_prev)       <- dashpot across each pipe: loss ~ k^2
//   absorbing edge     absorb * sqrt(beta)               <- MATCHED RESISTOR to ground on boundary
//                                                           cells. This is the free win: a few
//                                                           cells replace a wide sponge.
//   paddle / drop      current sources into b
//
// Backward-Euler on the wave equation gives, per cell:
//   (1 + a + SUM beta_face) * h_next  -  SUM beta_face * h_neighbour  =  rhs
// Unconditionally stable, so dt is bound by what you want to resolve, not by sqrt(g*h).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(r32f, set = 0, binding = 0) uniform image2D h_curr;   // h at t          (read)
layout(r32f, set = 1, binding = 0) uniform image2D h_prev;   // h at t-dt       (read)
layout(r32f, set = 2, binding = 0) uniform image2D iter_in;  // current iterate (read)
layout(r32f, set = 3, binding = 0) uniform image2D iter_out; // next iterate    (write)

layout(push_constant, std430) uniform Params {
	vec2 size;
	float beta_scale;    // dt^2 * g  — multiplied by depth to get each pipe's conductance
	float a;             // flat damping (resistor to ground)

	vec4 drop;           // xy = centre px, z = radius px, w = strength (0 = none)

	float floor_base;    // bed ramp: y = floor_base + slope*(x+1)
	float slope;
	float water_level;
	float chop;          // k^2 loss

	float absorb;        // matched-impedance boundary conductance (0 = reflective walls)
	float segs;          // paddle micro-layer segment count (0 = continuous)
	float pad0;
	float pad1;

	vec4 paddle;         // x = piston x_old, y = x_new, z = reach, w = gain (0 = paddle off)
	vec4 macro;          // x = amp, y = lobes, z = phase_old, w = phase_new
	vec4 micro;          // x = amp, y = lobes, z = phase_old, w = phase_new
} p;

const float PI = 3.141592653589793;

// still-water depth at a normalized x — the SAME ramp the renderer draws
float depth_at(float x) {
	return max(0.02, p.water_level - (p.floor_base + p.slope * (x + 1.0)));
}

void main() {
	ivec2 c = ivec2(gl_GlobalInvocationID.xy);
	ivec2 mx = ivec2(p.size) - ivec2(1);
	if (c.x > mx.x || c.y > mx.y) {
		return;
	}

	vec2 uv = (vec2(c) + 0.5) / p.size;
	float x = uv.x * 2.0 - 1.0;
	float z = uv.y * 2.0 - 1.0;
	float dx = 2.0 / p.size.x;   // cell width in pool units

	float hc = imageLoad(h_curr, c).r;
	float hp = imageLoad(h_prev, c).r;

	// ---- pipe conductances from the bed (arithmetic mean of the two cell depths) ----
	float dC = depth_at(x);
	float bE = p.beta_scale * 0.5 * (dC + depth_at(x + dx));
	float bW = p.beta_scale * 0.5 * (dC + depth_at(x - dx));
	float bN = p.beta_scale * dC;   // ramp varies in x only
	float bS = bN;

	// ---- sources ----
	float force = 0.0;

	if (p.drop.w != 0.0) {
		vec2 d = vec2(c) - p.drop.xy;
		force += p.drop.w * exp(-dot(d, d) / (p.drop.z * p.drop.z));
	}

	if (p.paddle.w != 0.0) {
		float zq = z;
		if (p.segs >= 1.0) {
			zq = (floor((z * 0.5 + 0.5) * p.segs) + 0.5) / p.segs * 2.0 - 1.0;
		}
		float face_old = p.paddle.x
			+ p.macro.x * sin(p.macro.y * PI * z  + p.macro.z)
			+ p.micro.x * sin(p.micro.y * PI * zq + p.micro.z);
		float face_new = p.paddle.y
			+ p.macro.x * sin(p.macro.y * PI * z  + p.macro.w)
			+ p.micro.x * sin(p.micro.y * PI * zq + p.micro.w);
		float reach = max(1.0e-4, p.paddle.z);
		float dist = abs(x - face_new);
		if (dist < reach) {
			float w = 0.5 + 0.5 * cos(PI * dist / reach);
			force += (face_new - face_old) * p.paddle.w * w;
		}
	}

	// ---- k^2 loss: a dashpot across each pipe, damping the RATE not the state ----
	if (p.chop > 0.0) {
		float rC = hc - hp;
		float rE = imageLoad(h_curr, clamp(c + ivec2(1, 0), ivec2(0), mx)).r
			- imageLoad(h_prev, clamp(c + ivec2(1, 0), ivec2(0), mx)).r;
		float rW = imageLoad(h_curr, clamp(c + ivec2(-1, 0), ivec2(0), mx)).r
			- imageLoad(h_prev, clamp(c + ivec2(-1, 0), ivec2(0), mx)).r;
		float rN = imageLoad(h_curr, clamp(c + ivec2(0, 1), ivec2(0), mx)).r
			- imageLoad(h_prev, clamp(c + ivec2(0, 1), ivec2(0), mx)).r;
		float rS = imageLoad(h_curr, clamp(c + ivec2(0, -1), ivec2(0), mx)).r
			- imageLoad(h_prev, clamp(c + ivec2(0, -1), ivec2(0), mx)).r;
		force += p.chop * ((rE + rW + rN + rS) * 0.25 - rC);
	}

	// ---- neighbour iterate; clamp = Neumann (reflective) ----
	float nE = imageLoad(iter_in, clamp(c + ivec2(1, 0), ivec2(0), mx)).r;
	float nW = imageLoad(iter_in, clamp(c + ivec2(-1, 0), ivec2(0), mx)).r;
	float nN = imageLoad(iter_in, clamp(c + ivec2(0, 1), ivec2(0), mx)).r;
	float nS = imageLoad(iter_in, clamp(c + ivec2(0, -1), ivec2(0), mx)).r;

	// ---- matched-impedance termination on the boundary cells ----
	// Z = 1/c, and dt*c = sqrt(beta), so a conductance of sqrt(beta) on the diagonal drains
	// outgoing energy instead of reflecting it. A handful of cells replaces a wide sponge.
	float gAbs = 0.0;
	if (p.absorb > 0.0 && (c.x == 0 || c.y == 0 || c.x == mx.x || c.y == mx.y)) {
		gAbs = p.absorb * sqrt(max(bE, 1.0e-8));
	}

	float diag = 1.0 + p.a + gAbs + bE + bW + bN + bS;

	// The paddle and the drop are DISPLACEMENTS, not accelerations — a piston moves water by a
	// distance. The implicit solve divides rhs by the diagonal, so a raw source would be damped
	// by ~(1+4*beta). Scaling it by the diagonal makes `gain` mean the same thing it does in the
	// explicit fork, which is what lets the two scenes be compared knob-for-knob.
	float rhs = 2.0 * hc - hp + p.a * hp + force * diag;

	float h = (rhs + bE * nE + bW * nW + bN * nN + bS * nS) / diag;
	imageStore(iter_out, c, vec4(h, 0.0, 0.0, 0.0));
}
