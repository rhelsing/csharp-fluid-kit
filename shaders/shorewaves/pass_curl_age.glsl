#[compute]
#version 450

// pass_curl_age — per-cell BREAK AGE that TRAVELS WITH THE WAVE.
//
// v1 of this pass aged each column in place, and it did not work: a fixed column is steep only
// while the front passes over it, so age ticked up a few hundredths and then retired. The
// field never left the blue end of its range, and the barrel's cross-section — which lives in
// (age, depth) space — was placed at age 0.42 in a field that never exceeded ~0.05. Nothing
// was ever inside the tube.
//
// The fix is that age must be a property of the WAVE, not of the seabed under it. Each frame
// the field is advected semi-Lagrangian along the local propagation velocity, so a breaking
// front carries its own age shoreward and ACCUMULATES it. "Proportional into the face over
// time" then means the barrel's life, which is what was asked for, instead of one column's
// brief moment of steepness.
//
// Advection reads a BACKTRACED neighbour, so unlike v1 this cannot run in place — it
// ping-pongs (age_in -> age_out).
//
// rgba16f: r = age (0..1), g = steepness at birth, BA = propagation direction (xz, unit).
// BA is not new physics — `dir` is already computed here for the advection and was being
// discarded. Storing it means one readback yields position, age AND heading, which is what
// a line renderer needs and what a screen-space distance test could never give cleanly.
// Age is ALSO the across-crest coordinate: oldest where breaking began, ~0 at the leading
// edge, which is what lets a circle in (age, depth) be a tube that follows a curving crest
// without anyone extracting the crest curve.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;
layout(rgba16f, set = 0, binding = 2) uniform restrict readonly image2D img_derived;
layout(rgba16f, set = 0, binding = 3) uniform restrict readonly image2D img_age_in;
layout(rgba16f, set = 0, binding = 4) uniform restrict writeonly image2D img_age_out;

layout(push_constant) uniform Push {
	float dt;            // frame delta (s)
	float birth_steep;   // gradient magnitude that starts a barrel — 0.515 measured
	float min_depth;     // ignore dry sand / paper-thin swash
	float age_rate;      // 1/s toward crashed. HOST picks this: wall-clock vs phase-driven
	float decay_rate;    // 1/s back to nothing once it stops breaking
	float dx;            // cell size (m) — converts a velocity into a cell offset
	float g;
	float celerity;      // scales sqrt(g h); 0 = advect on flow alone, 1 = full wave speed
} pc;

// bilinear fetch from an integer-addressed storage image (no sampler on image2D)
vec4 sample_age(vec2 p, int N) {
	p = clamp(p, vec2(0.0), vec2(float(N - 1)));
	ivec2 i = ivec2(floor(p));
	ivec2 j = min(i + ivec2(1), ivec2(N - 1));
	vec2 f = p - vec2(i);
	vec4 a = mix(imageLoad(img_age_in, ivec2(i.x, i.y)), imageLoad(img_age_in, ivec2(j.x, i.y)), f.x);
	vec4 b = mix(imageLoad(img_age_in, ivec2(i.x, j.y)), imageLoad(img_age_in, ivec2(j.x, j.y)), f.x);
	return mix(a, b, f.y);
}

void main() {
	int N = imageSize(img_state).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}

	vec4 q = imageLoad(img_state, id);
	float B = imageLoad(img_bottom, id).r;
	float h = max(q.x - B, 0.0);
	vec2 grad = imageLoad(img_derived, id).rg;
	float steep = length(grad);

	// --- where did the water now at this cell come from? ---
	// Direction from the depth-averaged momentum: for a breaking front the flow is shoreward
	// and unambiguous, which the surface gradient's sign is not. Speed is the shallow-water
	// wave celerity plus the flow itself, since the crest outruns the particles.
	vec2 u = vec2(q.y, q.z) / max(h, 0.02);
	vec2 dir = length(u) > 1e-4 ? normalize(u) : (steep > 1e-5 ? normalize(grad) : vec2(0.0));
	float speed = pc.celerity * sqrt(pc.g * max(h, 0.0)) + length(u);
	vec2 back = vec2(id) - dir * (speed * pc.dt / max(pc.dx, 1e-4));

	vec4 prev = sample_age(back, N);
	float age = prev.r;
	float born_steep = prev.g;

	bool breaking = steep > pc.birth_steep && h > pc.min_depth;
	if (breaking) {
		// Birth only where nothing arrived: otherwise this front already has an age and is
		// simply getting older, which is the entire point of advecting it.
		if (age <= 0.001) {
			born_steep = steep;
		} else {
			born_steep = max(born_steep, steep);
		}
		age = min(age + pc.dt * pc.age_rate, 1.0);
	} else {
		age = max(age - pc.dt * pc.decay_rate, 0.0);
		if (age <= 0.0) {
			born_steep = 0.0;
		}
	}

	imageStore(img_age_out, id, vec4(age, born_steep, dir.x, dir.y));
}
