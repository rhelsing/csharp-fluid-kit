// uwkit — 2D OVERLAY: the MENISCUS, plus a debug fill, both driven by the water volume
// mask. A canvas_item shader on a full-rect ColorRect — literally 2D on the camera, no
// compositor and no compute.
//
// The meniscus is the line where the water meets the camera LENS — Crest's term. It is
// drawn, not found: a pinhole camera is either in air or in water, and the line exists
// because a real lens has finite size and straddles the surface. Crest edge-detects its
// mask at 1, 2 and 3 pixels; this takes a tap count so the band can be made fat.
//
// Only exact because the MASK is exact — edge-detecting a reconstructed boundary would
// draw a crisp line in the wrong place.
// 2D OVERLAY on the camera — debug fill and the MENISCUS, both driven by the rasterised
// water mask. A canvas_item shader on a full-rect ColorRect: no compositor, no compute.
//
// THE MENISCUS is the line where the water meets the camera lens — Crest calls it exactly
// that, "the intersection between the camera lens and the water". It is drawn, not found:
// there is no such feature in the scene, because a pinhole camera is either in air or in
// water. It exists because a real lens has finite size and straddles the surface.
//
// Crest's ComputeMeniscusWeight samples its mask at 1, 2 and 3 pixels along the horizon
// normal and draws where the samples differ. Same idea here, with a tunable tap count so the
// band can be made FAT rather than a hairline.
//
// This is only exact because the mask is exact. Edge-detecting the old reconstructed
// boundary would have drawn a crisp line in the wrong place.
shader_type canvas_item;

uniform sampler2D mask_tex : filter_linear;
uniform float threshold : hint_range(0.0, 1.0, 0.01) = 0.5;

group_uniforms debug_fill;
uniform bool fill_enabled = false;
uniform vec4 fill_color : source_color = vec4(1.0, 1.0, 1.0, 0.5);

group_uniforms meniscus;
uniform bool meniscus_enabled = true;
uniform vec4 meniscus_color : source_color = vec4(1.0, 1.0, 1.0, 0.9);
uniform float meniscus_px : hint_range(1.0, 64.0, 1.0) = 12.0;   // band thickness in pixels
uniform int meniscus_taps : hint_range(1, 16) = 8;
// Direction to search across the boundary. Vertical suits a level camera; Crest passes a
// CPU-computed horizon normal so the search stays perpendicular to the waterline when the
// camera rolls. Exposed rather than hardcoded so that stays tunable.
uniform vec2 meniscus_dir = vec2(0.0, 1.0);

void fragment() {
	vec2 texel = 1.0 / vec2(textureSize(mask_tex, 0));
	bool here = texture(mask_tex, SCREEN_UV).r > threshold;

	vec4 col = vec4(0.0);
	if (fill_enabled && here) {
		col = fill_color;
	}

	if (meniscus_enabled) {
		// Walk outward from this pixel. Every tap that lands on the OTHER side of the
		// boundary means the edge is within that distance, so accumulating them gives a band
		// that is thickest right at the edge and fades out — a fat meniscus rather than a
		// one-pixel line.
		float w = 0.0;
		vec2 step_uv = normalize(meniscus_dir) * texel * (meniscus_px / float(meniscus_taps));
		for (int i = 1; i <= meniscus_taps; i++) {
			float f = float(i);
			bool a = texture(mask_tex, SCREEN_UV + step_uv * f).r > threshold;
			bool b = texture(mask_tex, SCREEN_UV - step_uv * f).r > threshold;
			// Nearer taps count for more, so the band peaks on the boundary itself.
			float near_w = 1.0 - (f - 1.0) / float(meniscus_taps);
			if (a != here) { w = max(w, near_w); }
			if (b != here) { w = max(w, near_w); }
		}
		if (w > 0.0) {
			vec4 m = meniscus_color;
			m.a *= w;
			col = mix(col, m, m.a);
		}
	}

	COLOR = col;
}
