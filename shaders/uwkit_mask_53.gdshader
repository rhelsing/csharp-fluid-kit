// SCENE 53 MASK VOLUME — the water volume's back faces.
//
// A box whose TOP FACE is displaced by uw53_displace(), the SAME function the water shader
// calls. Not a copy of it: the same include, so composite Gerstner, the MNA window and the
// foam puff all apply identically and cannot drift.
//
// Back-facing means the camera is inside the volume along that ray — under the water — so
// the mask covers the surface and everything below it, without hiding the sea from above.
//
// The mesh must match the water plane's footprint AND subdivisions. Two piecewise-linear
// surfaces agree only where their vertices coincide; a coarser mask straight-lines across
// short waves and reads as "a line instead of a curve", worsening with every band.
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://uwkit/uw_displace_53.gdshaderinc"

uniform float box_top = 0.0;   // local y of the box's top face

void vertex() {
	if (VERTEX.y > box_top - 0.001) {
		vec3 pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 tangent;
		vec3 binormal;
		vec2 uv_l;
		float fade_l;
		VERTEX += uw53_displace(pos, tangent, binormal, uv_l, fade_l);
	}
}

void fragment() {
	// ALBEDO, not EMISSION — under `unshaded` emission does not land in the buffer.
	ALBEDO = FRONT_FACING ? vec3(0.0) : vec3(1.0);
}
