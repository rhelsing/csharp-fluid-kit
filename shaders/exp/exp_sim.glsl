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
} p;

const float PI = 3.141592653589793;

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

		// [obstacles] A solid neighbour reflects: substitute the CENTRE height so the gradient
		// across that face is zero (no-flux / Neumann). Scaled by hardness, so turning hardness
		// down lets the wave leak into the obstacle instead of bouncing off it.
		float hl = nl.r, hr = nr.r, hd = nd.r, hu = nu.r;
		float oc = 0.0;
		if (p.exp0.y > 0.5) {
			float k = clamp(p.exp0.x, 0.0, 1.0);
			oc = imageLoad(obstacle, c).r;
			hl = mix(hl, info.r, imageLoad(obstacle, clamp(c + ivec2(-1, 0), ivec2(0), mx)).r * k);
			hr = mix(hr, info.r, imageLoad(obstacle, clamp(c + ivec2(1, 0), ivec2(0), mx)).r * k);
			hd = mix(hd, info.r, imageLoad(obstacle, clamp(c + ivec2(0, -1), ivec2(0), mx)).r * k);
			hu = mix(hu, info.r, imageLoad(obstacle, clamp(c + ivec2(0, 1), ivec2(0), mx)).r * k);
		}
		float average = (hl + hr + hd + hu) * 0.25;

		// [shoaling] The 2.0 is c^2 in grid units, and the explicit scheme sits exactly at the 2D
		// CFL limit there — so depth may only SLOW waves, never speed them past it. drop_radius
		// carries the reference (deepest) depth, 0 = feature off; new_center = (floor_base, slope,
		// water_level), the same ramp the renderer draws. c^2 ~ g*h, so the coefficient scales
		// linearly with the local water column: waves slow, shorten and pile up as the bed rises.
		float coeff = 2.0;
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

		// [obstacles] the soft end of the slider: bleed energy off inside the mask rather than
		// returning it. hardness 1 leaves this untouched, so a hard column is purely reflective.
		if (p.exp0.y > 0.5 && oc > 0.0) {
			float absorb = oc * (1.0 - clamp(p.exp0.x, 0.0, 1.0));
			info.g *= (1.0 - absorb * 0.60);
			info.r *= (1.0 - absorb * 0.35);
		}
		info.r += info.g;

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
