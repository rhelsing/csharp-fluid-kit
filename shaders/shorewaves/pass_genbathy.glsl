#[compute]
#version 450

// One-shot: generate the ocean-archipelago bathymetry analytically and pack
// it in the solver's corner-consistent format (R = cell-center B, G = east
// face, B = north face, A = 3x3 max of centers). All values derive from one
// corner function, which the well-balancing requires.
// Island layout is mirrored in scripts/main.gd (camera placement); keep both
// in sync when editing.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict writeonly image2D img_bottom;

layout(push_constant) uniform Push {
	float dx;
	float pad0;
	float pad1;
	float pad2;
} pc;

const float FLOOR_DEPTH = -25.0;

// smooth min/max, polynomial
float smin(float a, float b, float k) {
	float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
	return mix(b, a, h) - k * h * (1.0 - h);
}

float smax(float a, float b, float k) {
	return -smin(-a, -b, k);
}

// cheap smooth 2D noise for coves and floor undulation (deterministic)
float n2(vec2 p) {
	return 0.5
		+ 0.35 * sin(p.x * 0.011 + 1.7) * cos(p.y * 0.013 + 0.5)
		+ 0.15 * sin(p.x * 0.031 + 4.2) * sin(p.y * 0.027 + 2.1);
}

// island: shore radius rs (B = 0 there), plateau height peak; coves from
// noise-modulated radius; underwater skirt steepens from beach 1:18 to 1:10
float island(vec2 p, vec2 c, float rs, float peak, float phase) {
	float d = length(p - c);
	float rs_eff = rs + 55.0 * (n2(p * 0.9 + vec2(phase * 37.0, phase * 61.0)) - 0.5) * 2.0 * 0.5;
	float t = rs_eff - d; // metres inland of the shoreline
	float slope = mix(0.10, 0.055, smoothstep(-30.0, 10.0, t));
	return smin(slope * t, peak, 4.0);
}

float segDist(vec2 p, vec2 a, vec2 b) {
	vec2 ab = b - a;
	float u = clamp(dot(p - a, ab) / dot(ab, ab), 0.0, 1.0);
	return length(p - a - ab * u);
}

// rubble-mound breakwater: flat crest, 1:3 sides; water overtops the crest
float breakwater(vec2 p, vec2 a, vec2 b, float crest) {
	float d = segDist(p, a, b);
	return crest - 0.35 * max(d - 5.0, 0.0);
}

// scattered round boulders on a ~7 m jittered grid (deterministic hash)
float boulders(vec2 p) {
	vec2 cell = floor(p / 7.0);
	float best = -100.0;
	for (int ox = -1; ox <= 1; ox++) {
		for (int oy = -1; oy <= 1; oy++) {
			vec2 cl = cell + vec2(float(ox), float(oy));
			vec2 hs = fract(sin(vec2(dot(cl, vec2(127.1, 311.7)), dot(cl, vec2(269.5, 183.3)))) * 43758.5453);
			float keep = fract(sin(dot(cl, vec2(419.2, 371.9))) * 12543.21);
			if (keep < 0.45) { continue; } // ~55% of cells hold a boulder
			vec2 ctr = (cl + 0.15 + 0.7 * hs) * 7.0;
			float r = 2.2 + 2.6 * hs.x;
			float hgt = 1.0 + 1.6 * hs.y;
			float dd = length(p - ctr) / r;
			float bump = hgt * pow(max(1.0 - dd * dd, 0.0), 1.5);
			best = max(best, bump);
		}
	}
	return best;
}

// bed elevation at a corner position (metres)
float bcorner(vec2 p) {
	float b = FLOOR_DEPTH
		+ 1.8 * sin(p.x * 0.0035 + 0.7) * cos(p.y * 0.0041 + 1.9)
		+ 1.1 * sin(p.x * 0.0102 + 3.1) * sin(p.y * 0.0093 + 0.8);
	float i1 = island(p, vec2(700.0, 700.0), 260.0, 12.0, 1.0);
	b = smax(b, i1, 2.5);
	b = smax(b, island(p, vec2(1400.0, 1250.0), 330.0, 15.0, 2.0), 2.5);
	float i3 = island(p, vec2(1100.0, 520.0), 170.0, 8.0, 3.0);
	// island 3, wave-facing western sector: terraced rock shelf. Quantizing
	// the profile into 0.35 m steps turns the beach into stone tiers the
	// swash cascades over.
	{
		vec2 rel = p - vec2(1100.0, 520.0);
		float ang = atan(rel.y, rel.x); // west sector: |ang| > 2.2 rad
		float sector = smoothstep(2.0, 2.5, abs(ang));
		float band = (1.0 - smoothstep(-1.2, 1.6, i3)) * smoothstep(-4.5, -2.2, i3);
		float terraced = floor(i3 / 0.35 + 0.5) * 0.35;
		i3 = mix(i3, terraced, sector * band);
	}
	b = smax(b, i3, 2.5);
	b = smax(b, island(p, vec2(600.0, 1500.0), 210.0, 10.0, 4.0), 2.5);
	// breaker shoal: crest 1.6 m below still water, waves trip over it
	float ds = length(p - vec2(1610.0, 640.0));
	b = smax(b, FLOOR_DEPTH + 23.4 * exp(-(ds * ds) / (140.0 * 140.0)), 2.5);

	// boulder field on island 2's wave-facing west shore: stones the surf
	// wraps around and washes over, beach-scene style at map scale
	{
		vec2 c2 = vec2(1400.0, 1250.0);
		vec2 rel = p - c2;
		float ang = atan(rel.y, rel.x);
		float sector = smoothstep(1.9, 2.4, abs(ang));
		// band around the waterline: bed between -2.5 and +1 m
		float band = smoothstep(-2.5, -1.2, b) * (1.0 - smoothstep(0.6, 1.4, b));
		float bf = boulders(p);
		if (bf > 0.0 && sector * band > 0.01) {
			b += bf * sector * band;
		}
	}

	// rubble breakwater guarding island 1's lagoon mouth: crest +0.9 m, the
	// bigger sets overtop it and sheet down the back side
	b = max(b, breakwater(p, vec2(470.0, 880.0), vec2(350.0, 985.0), 0.9));
	// submerged groyne off island 2's south-west skirt: crest -0.3 m
	b = max(b, breakwater(p, vec2(1195.0, 1400.0), vec2(1090.0, 1505.0), -0.3));
	return b;
}

void main() {
	ivec2 sz = imageSize(img_bottom);
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= sz.x || id.y >= sz.y) {
		return;
	}
	// 4x4 corner block around this cell: c[a][b] = corner (id.x + a - 1, id.y + b - 1)
	float c[4][4];
	for (int a = 0; a < 4; a++) {
		for (int b = 0; b < 4; b++) {
			vec2 p = vec2(float(id.x + a - 1), float(id.y + b - 1)) * pc.dx;
			c[a][b] = bcorner(p);
		}
	}
	float Bc = 0.25 * (c[1][1] + c[2][1] + c[1][2] + c[2][2]);
	float Be = 0.5 * (c[2][1] + c[2][2]);
	float Bn = 0.5 * (c[1][2] + c[2][2]);
	float Bmax = -1e9;
	for (int di = 0; di < 3; di++) {
		for (int dj = 0; dj < 3; dj++) {
			float ctr = 0.25 * (c[di][dj] + c[di + 1][dj] + c[di][dj + 1] + c[di + 1][dj + 1]);
			Bmax = max(Bmax, ctr);
		}
	}
	imageStore(img_bottom, id, vec4(Bc, Be, Bn, Bmax));
}
