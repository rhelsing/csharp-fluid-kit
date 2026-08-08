#[compute]
#version 450

// exp_sim.glsl — the ONE kernel the whole DSP experiment series shares.
//
// Forked from shaders/shorewaves/webgpu_sim.glsl (which stays untouched — scenes 23, 24 and
// WaveTankV2 depend on it). Every experiment variable added here is a uniform whose DEFAULT IS A
// NO-OP, so 24_base is bit-identical to the stock sim and each scene turns on exactly one thing.
// That is what makes "one isolated variable per scene" true rather than aspirational.
//
// Added so far:
//   set 2 = obstacle mask (r32f, 1 = solid). `hardness` blends how a solid cell behaves:
//     1 = perfect reflector — a solid neighbour's height is replaced by the centre's, so the
//         gradient across the face is zero. That is a no-flux (Neumann) wall, which is what a
//         column physically is: water cannot pass through it.
//     0 = pure absorber — energy inside the mask is bled off instead of returned.
//   With no mask uploaded the field is all zeros and every term below vanishes.
//
//   exp1 = DIODE (24_diode). The Shockley shape from lib/mna-solver.cmajor,
//     Gd = (Is/Vt)*exp(Vd0/Vt), used as a per-cell threshold LOSS: ~zero below the knee,
//     exponential above it. NO matrix, no solve, no stamp assembly — just the exponential.
//     A threshold is the cleanest source of genuine interaction: two waves that are each
//     below the knee do nothing alone, and only bite where they cross and sum over it.
//
//   mode 5 = MODAL BASIS (24_cxm_field). The direct answer to "performant like a function".
//     A reverb tank hands you ~8 SCALARS per tick, but water needs a 2D field, and turning 8
//     numbers into 65k cells is where the real cost of the cheap methods actually lands.
//     Today the CXM scene injects taps as sim DROPS and lets the solver spread them, which
//     means the expensive thing is doing the expansion. This mode does it directly:
//
//         h(x,z) = SUM_n a_n * cos(m_n * PI * x) * cos(k_n * PI * z)
//
//     the low-order spatial basis from path-a §4. The mode shapes are evaluated ANALYTICALLY
//     from uv, so the basis costs zero memory — no precomputed textures at all. There is no
//     time stepping, no neighbour access, no CFL and no substep loop: every cell is an
//     independent weighted sum of N cosines. That is a function evaluation, not a recurrence.
//
//   exp2 = CLIP + SPEED (24_clip, 24_speed).
//     clip: MangledVerb's softclip x/(1+|x|+0.28x^2), in one of two POSITIONS. Applied after
//       the update it mangles the field but never feeds back, so it cannot make signals
//       interact — that is MangledVerb's own architecture (distortion AFTER reverb). Applied
//       inside the update it changes what the next step sees, and it can. Same curve, two
//       positions; the position is the whole variable.
//     speed: c = sqrt(g(h+eta)) — a big wave rides on deeper water and travels faster, so it
//       changes the medium the next wave passes through. This is the physically real
//       nonlinearity, and the one superposition cannot fake.
//
// Original header follows.
//
// Scene 27 — Evan Wallace's WebGL-Water heightfield sim ported to a
// RenderingDevice COMPUTE kernel (scene 15 ran it as a nested-SubViewport
// ping-pong of canvas fragment shaders). ONE kernel, selected per dispatch by a
// `mode` push-constant, reproduces the four Wallace sim passes verbatim:
//   mode 0 = DROP    (drop.frag.wgsl)   — cosine-falloff bump added to height
//   mode 1 = SPHERE  (sphere.frag.wgsl) — sphere displaces water (old rises, new falls)
//   mode 2 = UPDATE  (update.frag.wgsl) — finite-difference wave step (run 2x/tick)
//   mode 3 = NORMAL  (normal.frag.wgsl) — surface normal from height gradient
//
// State texture packs R=height, G=velocity, B=normal.x, A=normal.z (RGBA32F) —
// exactly the layout scene 15's ww_* raytracing shaders sample as `water_tex`,
// so this state texture IS water_tex (no separate pack pass needed). Passes
// ping-pong two storage images (set 0 = read, set 1 = write) with a barrier
// between dispatches. Grid 256x256, domain [-1,1] on x/z, uv = xz*0.5+0.5.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D state_in;   // previous state (read)
layout(rgba32f, set = 1, binding = 0) uniform image2D state_out;  // next state (write)
layout(r32f,    set = 2, binding = 0) uniform image2D obstacle;    // 1 = solid, 0 = open water
layout(r32f,    set = 3, binding = 0) uniform image2D reduce_out;  // 16x16 tile sums, for the SEND

layout(push_constant, std430) uniform Params {
	vec2 size;            // grid width, height (px)
	float mode;           // 0 drop, 1 sphere, 2 update, 3 normal
	float drop_radius;    // drop falloff radius (water space, ~0.03)
	vec2 drop_center;     // drop centre in [-1,1]
	float drop_strength;  // + up / - down, 0 = no drop this tick
	float sphere_radius;  // sphere radius (0.25)
	vec4 old_center;      // sphere centre last frame (xyz); .w = velocity damping per step
	vec4 new_center;      // sphere centre this frame (xyz used)
	vec4 extra;           // mode 2: .x = chop damping (k^2); mode 4: micro layer (amp, lobes, ph, ph)
	vec4 exp0;            // .x = obstacle hardness (1 reflect .. 0 absorb), .y = obstacle enable
	vec4 exp1;            // .x = diode enable, .y = knee, .z = Is, .w = Vt
	vec4 exp2;            // .x = clip mode (0 off/1 after/2 in-loop), .y = clip drive, .z = speed coupling
} p;

const float PI = 3.141592653589793;

// The spatial basis, shared by the MODAL BASIS synth (mode 5) and the PROJECTION analysis
// (mode 6) so the two can never disagree about what mode n means. Indices are deliberately
// non-harmonic so the summed shapes have no common period — the same reason the tank's delay
// lengths are mutually incommensurate.
vec2 modeIndex(int i) {
	const vec2 tbl[8] = vec2[8](vec2(1.0, 2.0), vec2(2.0, 1.0), vec2(2.0, 3.0), vec2(3.0, 2.0),
	                            vec2(1.0, 4.0), vec2(4.0, 1.0), vec2(3.0, 5.0), vec2(5.0, 3.0));
	return tbl[clamp(i, 0, 7)];
}

// sphere.frag.wgsl volume_in_sphere — column of displaced water volume per texel.
float volume_in_sphere(vec3 center, vec2 uv, float r) {
	vec3 pos = vec3(uv.x * 2.0 - 1.0, 0.0, uv.y * 2.0 - 1.0);
	float dist = length(pos - center);
	float t = dist / r;
	float dy = exp(-pow(t * 1.5, 6.0));
	float ymin = min(0.0, center.y - dy);
	float ymax = min(max(0.0, center.y + dy), ymin + 2.0 * dy);
	return (ymax - ymin) * 0.1;
}

void main() {
	ivec2 c = ivec2(gl_GlobalInvocationID.xy);
	ivec2 mx = ivec2(p.size) - ivec2(1);
	if (c.x > mx.x || c.y > mx.y) {
		return;
	}

	vec4 info = imageLoad(state_in, c);
	vec2 uv = (vec2(c) + 0.5) / p.size;   // texel-centre UV in [0,1]
	int mode = int(p.mode + 0.5);

	if (mode == 0) {
		// --- DROP: cosine-falloff bump added to height ---
		float drop = max(0.0, 1.0 - length(p.drop_center * 0.5 + 0.5 - uv) / p.drop_radius);
		drop = 0.5 - cos(drop * PI) * 0.5;
		info.r += drop * p.drop_strength;

	} else if (mode == 1) {
		// --- SPHERE: water rises where the sphere WAS, falls where it IS ---
		info.r += volume_in_sphere(p.old_center.xyz, uv, p.sphere_radius);
		info.r -= volume_in_sphere(p.new_center.xyz, uv, p.sphere_radius);

	} else if (mode == 2) {
		// --- UPDATE: finite-difference wave propagation ---
		// one fetch per neighbour: .r feeds the height Laplacian, .g the velocity one below
		vec4 nl = imageLoad(state_in, clamp(c + ivec2(-1, 0), ivec2(0), mx));
		vec4 nr = imageLoad(state_in, clamp(c + ivec2(1, 0), ivec2(0), mx));
		vec4 nd = imageLoad(state_in, clamp(c + ivec2(0, -1), ivec2(0), mx));
		vec4 nu = imageLoad(state_in, clamp(c + ivec2(0, 1), ivec2(0), mx));

		// [obstacles] The mask is BINARY for physics — a cell is solid or it is not. The mask
		// texture is anti-aliased for the visual, but a fractional value here is incoherent:
		// it partially decouples a cell from its neighbours without pinning it, so the cell
		// loses its restoring force while keeping its velocity, and that velocity integrates
		// into height unopposed. That is what blew this up twice.
		//
		// One mechanism covers hard AND soft, and solid cells are ALWAYS pinned to rest:
		//   hardness 1 -> substitution ON: water cells replace a solid neighbour's height with
		//     their own, so there is no gradient across the face. A no-flux wall. Nothing ever
		//     enters, so the pinned interior is invisible — the obstacle reflects.
		//   hardness 0 -> substitution OFF: waves propagate straight into the region and are
		//     killed there by the same pinning. A perfect absorber — it reads as a hole.
		float hl = nl.r, hr = nr.r, hd = nd.r, hu = nu.r;
		float solid = 0.0;
		if (p.exp0.y > 0.5) {
			float k = clamp(p.exp0.x, 0.0, 1.0);
			solid = step(0.5, imageLoad(obstacle, c).r);
			hl = mix(hl, info.r, step(0.5, imageLoad(obstacle, clamp(c + ivec2(-1, 0), ivec2(0), mx)).r) * k);
			hr = mix(hr, info.r, step(0.5, imageLoad(obstacle, clamp(c + ivec2(1, 0), ivec2(0), mx)).r) * k);
			hd = mix(hd, info.r, step(0.5, imageLoad(obstacle, clamp(c + ivec2(0, -1), ivec2(0), mx)).r) * k);
			hu = mix(hu, info.r, step(0.5, imageLoad(obstacle, clamp(c + ivec2(0, 1), ivec2(0), mx)).r) * k);
		}
		float average = (hl + hr + hd + hu) * 0.25;

		// [shoaling] The 2.0 is c^2 in grid units, and the explicit scheme sits exactly at the 2D
		// CFL limit there — so depth may only SLOW waves, never speed them past it. drop_radius
		// carries the reference (deepest) depth, 0 = feature off; new_center = (floor_base, slope,
		// water_level), the same ramp the renderer draws. c^2 ~ g*h, so the coefficient scales
		// linearly with the local water column: waves slow, shorten and pile up as the bed rises.
		float coeff = 2.0;
		// [speed] amplitude-dependent wave speed. The explicit scheme already sits AT the 2D
		// CFL limit at coeff = 2.0, so the coupling may only ever pull the coefficient DOWN --
		// letting it exceed 2.0 would be unconditionally unstable rather than merely stiff.
		// Crests therefore travel at full speed and troughs are slowed, which is the same
		// relative ordering c = sqrt(g(h+eta)) produces, expressed within the stable range.
		if (p.exp2.z > 0.0) {
			float k = clamp(p.exp2.z, 0.0, 1.0);
			coeff *= clamp(1.0 + k * clamp(info.r * 8.0, -1.0, 0.0), 0.05, 1.0);
		}
		if (p.drop_radius > 0.0) {
			float xs = uv.x * 2.0 - 1.0;
			float bed = p.new_center.x + p.new_center.y * (xs + 1.0);
			float depth = max(0.0, p.new_center.z - bed);
			coeff = 2.0 * clamp(depth / p.drop_radius, 0.02, 1.0);
		}
		info.g += (average - info.r) * coeff;

		// [chop damping] Flat `v *= damp` loses every wavelength equally — that is what a viscous
		// fluid does. Real water dissipates as k^2: chop dies in seconds, swell runs for minutes.
		// Diffusing the VELOCITY field does exactly that: for a mode of wavenumber k the Laplacian
		// is -k^2 v, so this term removes energy in proportion to k^2 and leaves long waves alone.
		if (p.extra.x > 0.0) {
			info.g += ((nl.g + nr.g + nd.g + nu.g) * 0.25 - info.g) * clamp(p.extra.x, 0.0, 1.0);
		}
		// Damping is per STEP, not per second — so it scales with both frame rate and grid size
		// (a wave crossing a 1024 grid takes 4x the steps a 256 grid does, hence 4x the decay).
		// Wallace's constant was 0.995 at 256; the host compensates.
		info.g *= p.old_center.w;

		// [diode] Shockley threshold loss, from stampDiode() in lib/mna-solver.cmajor:
		//     Id = Is * (exp(Vd/Vt) - 1)
		//
		// TWO things here are load-bearing, both learned by blowing this up:
		//
		// 1. It damps the VELOCITY, never the height. Scaling info.r directly is not dissipation
		//    -- it is an instantaneous, spatially-varying rescale of the surface. Because the loss
		//    rises exponentially, adjacent cells can differ enormously, so it carves a cliff along
		//    the knee contour. The Laplacian sees that cliff next step and the explicit scheme
		//    goes unstable. Energy loss is a force on velocity; elevation is a state, not a knob.
		//
		// 2. It is applied SEMI-IMPLICITLY as g / (1 + Gd), not g * (1 - Gd). The explicit form
		//    goes negative -- and then oscillates -- as soon as Gd > 1, which an exponential
		//    reaches immediately. The semi-implicit form is unconditionally stable for ANY Gd >= 0
		//    and needs no cap or clamp to stay sane. This is the same companion-stamp trick
		//    shorewaves/sim/pass_step.glsl already uses for Manning friction:
		//        qn.y = (q_0.y + dt*R.y) / (1.0 + dt*fric)
		//    i.e. an implicit conductance for the stiffest term -- exactly what the diode is.
		if (p.exp1.x > 0.5) {
			float Vt = max(p.exp1.w, 1.0e-3);
			float over = clamp((abs(info.r) - p.exp1.y) / Vt, 0.0, 12.0);
			if (over > 0.0) {
				float Gd = max(p.exp1.z, 0.0) * (exp(over) - 1.0);   // 0 exactly AT the knee
				info.g /= (1.0 + Gd);
			}
		}

		// [clip] IN-LOOP position: the curve sits inside the update, so what it does this step
		// changes what the next step sees. This is the position that can make signals interact.
		if (p.exp2.x > 1.5) {
			float d = max(p.exp2.y, 0.01);
			float x = info.r * d;
			float ax = abs(x);
			info.r = (x / (1.0 + ax + 0.28 * x * x)) * 1.2 / d;
		}

		info.r += info.g;

		// [obstacles] A solid interior must be DEAD, not merely decoupled. The neighbour
		// substitution above stops water cells seeing INTO the obstacle, but it also leaves the
		// interior with no Laplacian coupling at all — so whatever velocity is in there has no
		// restoring force and just keeps integrating into height. A STATIC obstacle never shows
		// this because nothing ever enters it. A MOVING one sweeps over water that already has
		// velocity, traps it, and the trapped velocity accumulates as a geometric series
		// (~100x at 0.99 per-step damping) until the obstacle moves on and releases it as a
		// spike -> instability -> NaN -> the water mesh vanishes.
		// Pinning the interior to rest is also just what "solid" means. Outside cells never read
		// these values (they substitute the centre instead), so this cannot affect scattering.
		// [obstacles] Solid cells are dead — always, regardless of hardness. Nothing can
		// accumulate inside a region that is reset every step, which is what makes a MOVING
		// obstacle safe: it sweeps over water that has velocity, and that energy is removed
		// rather than trapped and released as a spike when the obstacle passes on.
		if (solid > 0.5) {
			info.r = 0.0;
			info.g = 0.0;
		}

	} else if (mode == 7) {
		// --- REDUCE: tile sums of |h| in the send region, for Path A's SEND ---
		// path-a §4 makes the tank a FEEDBACK loop: energy the domain would otherwise throw
		// away becomes the tank's input. That needs water state back on the CPU every frame,
		// and a full 1 MB readback per frame is a sync stall that would dominate the budget.
		// So the GPU reduces first: one invocation per 16x16 tile, and the host reads back 1 KB.
		//
		// drop_radius carries the send-region width as a fraction of the half-domain: only the
		// outer annulus contributes, which is the "sponge zone" the doc points at.
		ivec2 tiles = ivec2(16);
		if (c.x < tiles.x && c.y < tiles.y) {
			ivec2 lo = ivec2(vec2(c) / vec2(tiles) * p.size);
			ivec2 hi = ivec2(vec2(c + ivec2(1)) / vec2(tiles) * p.size);
			float band = clamp(p.drop_radius, 0.01, 1.0);
			float acc = 0.0;
			for (int y = lo.y; y < hi.y; ++y) {
				for (int x = lo.x; x < hi.x; ++x) {
					vec2 u = (vec2(x, y) + 0.5) / p.size * 2.0 - 1.0;
					// outer annulus only — how far from centre, in Chebyshev distance
					if (max(abs(u.x), abs(u.y)) < 1.0 - band) { continue; }
					acc += abs(imageLoad(state_in, ivec2(x, y)).r);
				}
			}
			imageStore(reduce_out, c, vec4(acc, 0.0, 0.0, 0.0));
		}

	} else if (mode == 5) {
		// --- MODAL BASIS: N tap amplitudes -> a height field, no solver involved ---
		// Mode indices are deliberately incommensurate-ish (1,2 / 2,1 / 2,3 / 3,2 ...) so the
		// summed shapes do not share a common period and the field does not read as one tidy
		// standing wave. Same reason the tank's delay lengths are mutually incommensurate.
		vec2 q = uv * PI;
		// Amplitudes ride old_center/new_center, which mode 5 does not otherwise use. Vulkan
		// only GUARANTEES 128 bytes of push constant, so adding fields past that would work on
		// this Metal box and fail elsewhere; reusing dead slots keeps the block at exactly 128.
		float amps[8] = float[8](p.old_center.x, p.old_center.y, p.old_center.z, p.old_center.w,
		                        p.new_center.x, p.new_center.y, p.new_center.z, p.new_center.w);
		int n = int(p.drop_strength + 0.5);   // tap count rides the spare strength slot
		float h = 0.0;
		for (int i = 0; i < n && i < 8; ++i) {
			vec2 md = modeIndex(i);
			h += amps[i] * cos(md.x * q.x) * cos(md.y * q.y);
		}
		// drop_radius carries the output gain; additive so this can sit on top of a live sim.
		info.r += h * p.drop_radius;

	} else if (mode == 4) {
		// --- PADDLE (wavemaker): a piston spanning the whole z edge, face at drop_center.y.
		// It moved drop_center.x -> .y this tick; the water in front of the face is pushed by
		// that stroke, falling off over drop_radius. Behind the face is inside the paddle, so
		// it is gated out. This is what makes waves now — no ball, no drops needed.
		float px_old = p.drop_center.x;
		float px_new = p.drop_center.y;
		float x = uv.x * 2.0 - 1.0;
		float z = uv.y * 2.0 - 1.0;

		// [curve] The face need not be straight. Bending it by amp*sin(lobes*PI*z + phase)
		// makes each z-slice stroke by a different amount, so the wavefront leaves the paddle
		// curved instead of as one parallel front: convex lobes diverge, concave ones focus.
		// Advance the phase over time and this is a snake wavemaker — the segmented paddle real
		// directional basins use to make oblique and short-crested seas.
		float c_amp   = p.old_center.z;
		float c_lobes = p.old_center.y;

		// TWO INDEPENDENT LAYERS on the same face, summed:
		//   MACRO — continuous, evaluated on raw z: one big paddle that undulates.
		//   MICRO — evaluated on z quantised into `segs` steps: a bank of tiny snake segments.
		// Either can be zeroed by its own amplitude, so they run alone or together.
		float segs = p.new_center.y;
		float zq = z;
		if (segs >= 1.0) {
			zq = (floor((z * 0.5 + 0.5) * segs) + 0.5) / segs * 2.0 - 1.0;
		}
		float m_amp   = p.extra.x;
		float m_lobes = p.extra.y;

		float face_old = px_old
			+ c_amp * sin(c_lobes * PI * z  + p.old_center.x)
			+ m_amp * sin(m_lobes * PI * zq + p.extra.z);
		float face_new = px_new
			+ c_amp * sin(c_lobes * PI * z  + p.new_center.x)
			+ m_amp * sin(m_lobes * PI * zq + p.extra.w);
		// The window must be SMOOTH and two-sided. A one-sided exp() cut at the paddle face is a
		// step from 1 to 0 across a single texel, and stamping that discontinuity every tick feeds
		// grid-scale wavelengths — which is what showed up as striations. A raised cosine reaches
		// zero with zero slope at both edges, so only the intended long waves get injected.
		float reach = max(1.0e-4, p.drop_radius);
		float d = abs(x - face_new);
		if (d < reach) {
			float w = 0.5 + 0.5 * cos(PI * d / reach);
			info.r += (face_new - face_old) * p.drop_strength * w;
		}

	} else {
		// --- NORMAL: surface normal from height gradient -> B=normal.x, A=normal.z ---
		// [clip] AFTER-LOOP position: applied here, once per tick, downstream of the update.
		// It reshapes what is displayed and carried forward, but it is NOT inside the
		// recurrence the same way -- this is the control that should NOT produce interaction.
		if (p.exp2.x > 0.5 && p.exp2.x < 1.5) {
			float d = max(p.exp2.y, 0.01);
			float x = info.r * d;
			float ax = abs(x);
			info.r = (x / (1.0 + ax + 0.28 * x * x)) * 1.2 / d;
		}
		vec2 dpx = 1.0 / p.size;
		float val_dx = imageLoad(state_in, clamp(c + ivec2(1, 0), ivec2(0), mx)).r;
		float val_dy = imageLoad(state_in, clamp(c + ivec2(0, 1), ivec2(0), mx)).r;
		vec3 dx = vec3(dpx.x, val_dx - info.r, 0.0);
		vec3 dy = vec3(0.0, val_dy - info.r, dpx.y);
		vec3 normal = normalize(cross(dy, dx));
		info.b = normal.x;
		info.a = normal.z;
	}

	imageStore(state_out, c, info);
}
