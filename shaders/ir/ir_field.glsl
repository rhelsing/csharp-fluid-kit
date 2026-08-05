#[compute]
#version 450

// ir_field.glsl — impulse-response CAPTURE and CONVOLUTION for the 24_ir fork.
//
// The literal audio move, done on a 2D field. Fire one impulse into the full sim, record
// the height field every tick into a 3D texture (x, y, k) — that stack IS the basin's
// impulse response h(x, y, k). Afterwards the solver is switched OFF entirely and the
// surface is reconstructed by convolution:
//
//     field(x, y, t) = SUM_k  s[t-k] * h(x, y, k)
//
// No neighbours, no CFL, no substeps — every cell is independent, so this is a pure
// gather. That is the whole point: the sim is a recurrence, the IR is a function. Drive
// any signal s through it and you get that signal's sea back.
//
// EXACT for the linear regime, which is what an LTI system means — the operator does not
// depend on the amplitude, so one measurement serves infinitely many inputs.
//
// The tap list is COMPACTED host-side to the non-zero entries of s. Mathematically
// identical (zero terms contribute nothing), but it makes the cost proportional to how
// dense the drive signal is rather than to the IR length — a sparse impulse train is
// nearly free, a noise drive pays full price. That difference is itself a result worth
// seeing on the readout.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D state_in;    // sim state (R = height)
layout(r32f,    set = 1, binding = 0) uniform image3D ir_tex;      // h(x, y, k)
layout(rgba32f, set = 2, binding = 0) uniform image2D field_out;   // reconstructed (R = height)

layout(set = 3, binding = 0, std430) restrict readonly buffer Taps {
	// .x = IR slice index k (as float), .y = signal weight s[t-k]
	vec2 taps[];
} tp;

layout(push_constant, std430) uniform Params {
	vec2 size;        // grid px
	float mode;       // 0 = capture slice, 1 = convolve
	float slice;      // mode 0: which IR slice to write
	float tap_count;  // mode 1: number of active taps
	float gain;       // mode 1: output scale
	float pad0;
	float pad1;
} p;

void main() {
	ivec2 c = ivec2(gl_GlobalInvocationID.xy);
	ivec2 mx = ivec2(p.size) - ivec2(1);
	if (c.x > mx.x || c.y > mx.y) {
		return;
	}

	int mode = int(p.mode + 0.5);

	if (mode == 0) {
		// --- CAPTURE: record this tick's height into IR slice k ---
		float h = imageLoad(state_in, c).r;
		imageStore(ir_tex, ivec3(c, int(p.slice + 0.5)), vec4(h, 0.0, 0.0, 0.0));

	} else if (mode == 3) {
		// --- NORMAL: B=normal.x, A=normal.z from the height gradient. Verbatim from
		// webgpu_sim.glsl mode 3, because the ww_* shaders sample exactly that layout and a
		// field with zero normals renders as a featureless blob no matter how good the heights
		// are. Safe in-place: this pass reads only .r and writes only .b/.a, so no thread
		// overwrites what another is reading.
		vec4 info = imageLoad(field_out, c);
		vec2 dpx = 1.0 / p.size;
		float val_dx = imageLoad(field_out, clamp(c + ivec2(1, 0), ivec2(0), mx)).r;
		float val_dy = imageLoad(field_out, clamp(c + ivec2(0, 1), ivec2(0), mx)).r;
		vec3 dx = vec3(dpx.x, val_dx - info.r, 0.0);
		vec3 dy = vec3(0.0, val_dy - info.r, dpx.y);
		vec3 normal = normalize(cross(dy, dx));
		info.b = normal.x;
		info.a = normal.z;
		imageStore(field_out, c, info);

	} else if (mode == 2) {
		// --- INSPECT: show one raw IR slice, so the recorded response can be LOOKED at
		// instead of inferred from the convolution. If this is flat, capture is broken; if it
		// rings, the bug is downstream.
		vec4 info = imageLoad(field_out, c);
		info.r = imageLoad(ir_tex, ivec3(c, int(p.slice + 0.5))).r * p.gain;
		imageStore(field_out, c, info);

	} else {
		// --- CONVOLVE: the weighted sum over the recorded response ---
		int n = int(p.tap_count + 0.5);
		float acc = 0.0;
		for (int i = 0; i < n; ++i) {
			vec2 t = tp.taps[i];
			acc += t.y * imageLoad(ir_tex, ivec3(c, int(t.x + 0.5))).r;
		}
		vec4 info = imageLoad(field_out, c);
		info.r = acc * p.gain;
		imageStore(field_out, c, info);
	}
}
