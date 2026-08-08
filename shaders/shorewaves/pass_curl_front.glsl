#[compute]
#version 450

// pass_curl_front — reduce the breaking front to a CURVE: one entry per along-shore row.
//
// WHY THIS EXISTS. Every previous attempt to place a barrel derived its geometry from the age
// field, and that cannot work: age is not a distance field. Inside the breaking ribbon it ramps
// 0..1; OUTSIDE it is flat zero, so a point 5 m in front of the wave and a point 50 m in front
// read identically. Any across-crest distance computed from it saturates at the ribbon's own
// width (~0.6 m), which is why the tube kept collapsing into a horizontal slab — a ribbon with
// a top and a bottom instead of a cylinder.
//
// A curve carries what a field cannot: the front's WORLD POSITION. With front_x known, the
// across-crest coordinate is just `pos.x - front_x` — real metres, so a tube can be any radius
// you like regardless of how thin the breaking band is.
//
// One thread per along-shore row (z), scanning across-shore (x) for the steepest wet cell.
// Output is N x 1:  r = front x (m) · g = surface height there (m) · b = strength · a = valid.

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;
layout(rgba16f, set = 0, binding = 2) uniform restrict readonly image2D img_derived;
layout(rgba16f, set = 0, binding = 3) uniform restrict writeonly image2D img_front;

layout(push_constant) uniform Push {
	float birth_steep;   // gate — 0.515 measured on screen
	float min_depth;     // ignore dry sand / paper-thin swash
	float dx;            // cell size (m)
	float _pad0;
} pc;

void main() {
	int N = imageSize(img_state).x;
	int z = int(gl_GlobalInvocationID.x);
	if (z >= N) {
		return;
	}

	// Scan shoreward. The FIRST cell past the gate is taken, not the steepest: a breaking wave
	// has a steep face AND a steep back, and the seaward edge is the one the lip throws from.
	// Taking the max would sit the tube on whichever side happened to be sharper this frame,
	// which reads as the barrel flickering between the front and back of the same wave.
	// First cell past the gate, then stop. Restored: the sub-cell + no-hard-miss version made
	// the flicker WORSE, and the reason is the fallback, not the interpolation — when no cell
	// crossed the gate it reported the row's strongest wet cell instead, which can be anywhere
	// (far offshore, or at the shoreline). So a marginal row stopped losing its tube and
	// started TELEPORTING it, which reads far worse than a clean disappearance.
	float best_x = -1.0;
	float best_s = 0.0;
	float best_w = 0.0;
	for (int x = 2; x < N - 2; x++) {
		ivec2 id = ivec2(x, z);
		vec4 q = imageLoad(img_state, id);
		float B = imageLoad(img_bottom, id).r;
		float h = max(q.x - B, 0.0);
		if (h <= pc.min_depth) {
			continue;
		}
		float s = length(imageLoad(img_derived, id).rg);
		if (s > pc.birth_steep) {
			best_x = float(x) * pc.dx;
			best_s = s;
			best_w = q.x;          // free-surface height at the front
			break;
		}
	}

	imageStore(img_front, ivec2(z, 0),
		vec4(best_x, best_w, best_s, best_x >= 0.0 ? 1.0 : 0.0));
}
