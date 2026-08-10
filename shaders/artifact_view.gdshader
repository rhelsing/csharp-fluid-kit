shader_type spatial;
render_mode unshaded, cull_disabled;

// artifact_view.gdshader — THE shared display family for the artifact series
// (docs/artifacts-250.md "Palette" + "Standard panel"; docs/artifacts-270.md
// "Palette extension"). One shader, every 2D scene from 250 up: a scene picks a
// field mode, and the residual always arrives on top in the same signature colour.
//
// The contract this exists to enforce:
//   · the artifact is ALWAYS #E23D6D — never a per-scene colour choice
//   · every scene can show its residual field, not just its pretty field
//   · signed data reads plus/minus, so chirality (270) is legible without
//     stealing the artifact colour from the residual
//
// Modes (the base doc's four):
//   0 ink scalar        — paper → ink, gamma'd. Dye, density, concentration.
//   1 signed two-tone   — minus ← paper → plus. ALSO the 270 chirality mode
//                         (winding sign, defect charge, vorticity sign).
//   2 lit height plane  — mode 0's albedo, lit by a normal built from the field
//                         gradient. One light, matte, no specular. Height fields.
//   3 artifact solo     — the residual alone on room, for reading it undistracted.
//
// On top of modes 0–2, the artifact OVERLAY adds residual × #E23D6D additively
// (artifact_gain = 0 turns it off), and the defect-marker channel adds sparse
// points — artifact-coloured, hue-shifted toward plus/minus by their charge.
//
// Colour management: the palette below is written in the doc's sRGB hex, all
// mixing happens in that space (so the mixes look like the palette table says),
// and ONE conversion to linear happens at the end. Pair it with a LINEAR tonemap
// — a filmic curve (AgX/Filmic) will shift these on-screen and the palette stops
// being the palette. Scene250Base sets this up.

// ── the palette (docs/artifacts-250.md), sRGB ────────────────────────────────
const vec3 PAPER    = vec3(0.9098, 0.8863, 0.8353);   // #E8E2D5  background / zero level
const vec3 INK      = vec3(0.0863, 0.1294, 0.2431);   // #16213E  scalar field body
const vec3 PLUS     = vec3(0.2078, 0.7686, 0.7098);   // #35C4B5  signed +
const vec3 MINUS    = vec3(0.8784, 0.6431, 0.3451);   // #E0A458  signed −
const vec3 ARTIFACT = vec3(0.8863, 0.2392, 0.4275);   // #E23D6D  THE residual, always
const vec3 ROOM     = vec3(0.0902, 0.0745, 0.0627);   // #171310  scene background

uniform int mode : hint_range(0, 3) = 0;

// The field. r = scalar (mode 0/2) or signed value (mode 1).
uniform sampler2D field_tex : filter_linear, repeat_disable, hint_default_black;
uniform float field_gain = 1.0;     // value → [0,1] (or [-1,1] signed) before shaping
uniform float field_gamma : hint_range(0.15, 4.0) = 1.0;

// The residual. Magnitude is what reads, so it is |r| by default — a signed
// residual (a pressure error, a winding sum) can keep its sign with artifact_signed.
uniform sampler2D artifact_tex : filter_linear, repeat_disable, hint_default_black;
uniform float artifact_gain : hint_range(0.0, 40.0) = 0.0;   // 0 = overlay off
uniform bool artifact_signed = false;   // true → +/− split instead of magnitude

// Sparse defect markers (270): r = marker strength, g = charge sign.
uniform sampler2D defect_tex : filter_linear, repeat_disable, hint_default_black;
uniform float defect_gain : hint_range(0.0, 8.0) = 0.0;
uniform float defect_chirality : hint_range(0.0, 1.0) = 0.6;   // 0 = pure artifact colour

// Mode 2 lighting. Matte on purpose: relief should read as shape, not as a material.
uniform float height_scale : hint_range(0.0, 200.0) = 40.0;
uniform vec3 light_dir = vec3(-0.45, 0.80, -0.40);
uniform float ambient : hint_range(0.0, 1.0) = 0.32;

uniform float texel = 0.001953125;   // 1 / grid — Scene250Base keeps this in sync

// Sample cell-exactly (texelFetch) instead of through the linear filter. Mandatory for any
// artifact whose structure is CELL-SCALED: scene 256 plaid's checkerboard is two cells wide,
// and bilinear interpolation of a two-cell checkerboard is a flat grey — the artifact would
// be invisible not because it isn't there, but because the sampler averaged it away.
uniform bool pixel_exact = false;

// Analytic overlay (scene 259 smear): a circle of this radius in UV, drawn in the artifact
// colour. r = c·t is the EXACT wavefront position for an impulse on a uniform medium, so
// this is not a guide line — it is the right answer, drawn on top of the computed one. The
// gap between the ring and the simulated front IS the dispersion error, read directly.
// 0 disables it.
uniform float ring_radius = 0.0;
uniform float ring_width = 0.0035;

// SURFACE mode (scene 290/291): render the field as a PHASE with a boundary, not as a
// concentration. Mapping ρ continuously to paper→ink is right for dye and wrong for water:
// any smearing at all reads as fog, and a two-phase field that is 90% water still shows as
// a mid grey. Thresholding at `surface_level` with a narrow band shows where the interface
// IS. It does not fix smearing — 291's sharpening does that — but it stops the display from
// exaggerating it, and it makes the remaining smear legible as a band of a measurable width
// rather than an ambiguous haze.
uniform float surface_level = 0.0;    // 0 disables; typical 0.5
uniform float surface_soft : hint_range(0.001, 0.5) = 0.06;

// The seabed, in UV. Drawn rather than merely simulated: the solid region is invisible in ρ
// (it is filled with water that never moves), and without it on screen a shoaling wave has
// nothing to visibly shoal against.
uniform float bed_y0 = 0.0;
uniform float bed_slope = 0.0;

vec3 srgb_to_linear(vec3 c) {
	// the exact piecewise sRGB transfer, not a pow(2.2) approximation: these are
	// hand-picked palette colours and the toe matters on the dark ones (#171310).
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void fragment() {
	vec2 uv = UV;

	// Cell index for pixel-exact mode, clamped — texelFetch out of range is undefined.
	int gs = int(1.0 / texel);
	ivec2 cell = clamp(ivec2(uv / texel), ivec2(0), ivec2(gs - 1));

	float fRaw = pixel_exact ? texelFetch(field_tex, cell, 0).r : texture(field_tex, uv).r;
	float aRaw = pixel_exact ? texelFetch(artifact_tex, cell, 0).r : texture(artifact_tex, uv).r;

	float f = fRaw * field_gain;

	// central differences, shared by mode 2's normal (cheap; only used there)
	float gx = texture(field_tex, uv + vec2(texel, 0.0)).r - texture(field_tex, uv - vec2(texel, 0.0)).r;
	float gy = texture(field_tex, uv + vec2(0.0, texel)).r - texture(field_tex, uv - vec2(0.0, texel)).r;

	vec3 col;

	if (mode == 1) {
		// signed two-tone / chirality: paper is genuinely zero, so the sign of a
		// field is readable at a glance and small values stay near the background.
		float s = clamp(f, -1.0, 1.0);
		vec3 tint = s >= 0.0 ? PLUS : MINUS;
		col = mix(PAPER, tint, pow(abs(s), field_gamma));
	} else if (mode == 3) {
		// artifact solo — deliberately NOT on paper: the residual owns the frame.
		col = mix(ROOM, ARTIFACT, clamp(abs(aRaw) * max(artifact_gain, 1.0), 0.0, 1.0));
	} else {
		float v = pow(clamp(f, 0.0, 1.0), field_gamma);
		if (surface_level > 0.0) {
			v = smoothstep(surface_level - surface_soft, surface_level + surface_soft, fRaw);
		}
		col = mix(PAPER, INK, v);
		if (mode == 2) {
			// height plane: UV → XZ, so the gradient tilts the up-normal directly.
			vec3 n = normalize(vec3(-gx * height_scale, 1.0, -gy * height_scale));
			float lam = max(dot(n, normalize(light_dir)), 0.0);
			col *= ambient + (1.0 - ambient) * lam;
		}
	}

	// ── the overlay: residual × #E23D6D, additive (base doc). Modes 0–2 only —
	// mode 3 already IS the residual and would double-count it.
	if (mode != 3 && artifact_gain > 0.0) {
		float a = aRaw;
		if (artifact_signed) {
			// keep the artifact colour for magnitude, lean the hue by sign so a
			// signed residual is still unmistakably "the residual".
			vec3 tint = mix(ARTIFACT, a >= 0.0 ? PLUS : MINUS, 0.35);
			col += tint * abs(a) * artifact_gain;
		} else {
			col += ARTIFACT * abs(a) * artifact_gain;
		}
	}

	// ── defect markers (270): artifact-coloured points, charge shown as hue.
	// The FIELD stays #E23D6D; only these sparse marks carry chirality, which is
	// what keeps "the residual is always the artifact colour" true.
	if (defect_gain > 0.0) {
		vec2 d = texture(defect_tex, uv).rg;
		if (d.r > 0.0) {
			vec3 mc = mix(ARTIFACT, d.g >= 0.0 ? PLUS : MINUS, defect_chirality);
			col += mc * d.r * defect_gain;
		}
	}

	// The seabed in MINUS — sand. It was ROOM, which is within a few percent of INK, so
	// sea and seabed were both "nearly black" and the waterline could not be read at all.
	// A beach needs the bed to be a different MATERIAL on screen, not a darker dark.
	if (bed_y0 > 0.0 || bed_slope != 0.0) {
		float bed = bed_y0 + bed_slope * uv.x;
		col = mix(col, MINUS * 0.55, smoothstep(bed + 0.004, bed - 0.004, uv.y));
	}

	if (ring_radius > 0.0) {
		float rr = distance(uv, vec2(0.5));
		col += ARTIFACT * (1.0 - smoothstep(0.0, ring_width, abs(rr - ring_radius)));
	}

	ALBEDO = srgb_to_linear(col);
}
