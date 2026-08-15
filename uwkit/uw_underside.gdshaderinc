// uwkit — THE UNDERSIDE OF THE WATER SURFACE.
//
// The kit's SECOND and last per-water touchpoint. The first is a provider snippet answering
// "how high is your surface here"; this one answers "what does your surface look like from
// below". Everything else — the boundary, the fog, the depth handling — is water-agnostic
// and lives in the compositor.
//
// WHY THIS CANNOT LIVE IN THE COMPOSITOR. The compositor runs POST_TRANSPARENT, i.e. after
// the surface has been rasterised. Godot back-face culls by default, so from underneath the
// water plane is never drawn at all and there is simply nothing in the frame to work with —
// you see raw sky where the surface should be. No post-process can recover a surface that
// was never rasterised. It has to be the water shader, and that makes it per-water.
//
// HOST SHADER MUST DO THREE THINGS (they cannot be done from an include):
//   1. `render_mode cull_disabled;`   — .gdshaderinc may not declare render_mode
//   2. `#include "res://uwkit/uw_underside.gdshaderinc"`
//   3. in fragment(), before writing ALBEDO:
//          if (!FRONT_FACING && uw_underside_on) {
//              col = uw_underside_shade(col, <surface normal>, <world view dir>,
//                                       SCREEN_UV, screen_tex);
//          }
//      (col goes in as the TOPSIDE colour, used when the camera is above — see
//       uw_camera_under.)
//
// Three lines, and `uw_underside_on` defaults FALSE so adding the include to a shader is
// inert until someone switches it on. A kit you can drop into a working water without
// changing how it looks is a kit people will actually adopt.
//
// The technique is water-kit scene 01b's: the SAME plane seen from below is the underside,
// warping SCREEN_TEXTURE (the world above the surface) by its own wave normal and tinting
// it. Generalised here from that scene's forked shader into a shared chunk, which is the
// difference between "01b has a nice underwater look" and "any water can have one".

// SEPARATION OF CONCERNS, and this file got it wrong once. The SURFACE does only what
// happens AT the interface: refraction, and Fresnel / total internal reflection. Every
// distance-based colouring belongs to the volume — the compositor's fog, which already
// computes per pixel how much water you are looking through.
//
// The first version applied a flat 0.62 tint at every angle AND at every distance, on top
// of the fog doing the same job properly. Two systems tinting by distance is a double
// count, and it is backwards where it matters most: pressed against the surface the water
// column is ~0, so the fog correctly says "clear" and the surface tinted it anyway.
//
// It also blended in the water's own TOPSIDE colour, whose depth-fade and shore-colour
// stages assume you are looking down into water. From below that value is meaningless, and
// it is what turned the out-of-cone region dark grey rather than watery.
// DEFAULTS ARE RYAN'S LIVE-TUNED VALUES (2026-08-14, scene 53, "Copy values" baked back).
// Two of them say something about the design, not just taste:
//   · tir_strength 0.85 -> 0.21. The physically-correct grazing mirror was strongly dialled
//     DOWN. Real TIR reflecting nothing reads as a lid; until there is something to reflect,
//     a faint mirror looks better than a correct one.
//   · underside_brightness landed at 3.82 against a slider that stopped at 4.0. That is the
//     tune hitting the rail, so the range is raised to 8 — a value pinned at its maximum is
//     a range that was wrong, not a preference.
group_uniforms uw_underside;
uniform bool uw_underside_on = false;
// THE VOLUME MASK decides where the underside is visible. Nothing is switched on or off.
//
// I had this wrong twice — first discarding back faces above water, then cross-fading them
// on a camera scalar. Both are booleans wearing different clothes, and both were unnecessary:
// the mask already answers this per pixel.
//
// The mask shader draws BOTH faces of the water volume — front black, back white — with
// ordinary depth testing. From OUTSIDE the volume the front face is nearer and wins, so the
// mask reads 0. From INSIDE, only back faces remain, so it reads 1. In a trough fold seen
// from above, the nearest surface is still a front face, so the mask is 0 there and the
// underside hides ITSELF. Geometry answering a question about geometry.
uniform sampler2D uw_mask_tex : filter_linear;
// The colour the surface falls back to outside Snell's window. MUST MATCH FOG_NEAR in
// uwkit/uw_boundary.glslinc — if the surface and the volume disagree, the seam shows.
// (Duplicated as a constant for now: the compositor's push constants are full at the 128
// byte guarantee, so sharing this properly needs a uniform buffer.)
uniform vec3 uw_tir_color : source_color = vec3(0.102, 0.361, 0.384);
// How completely the out-of-cone region goes to that colour. 1 = a solid mirror surrogate,
// 0 = stays refracted and the window effectively disappears.
uniform float uw_tir_strength : hint_range(0.0, 1.0, 0.01) = 0.21;
// Gain on the underside AS A SURFACE. This has to live here, not in the compositor: the
// compositor's gain is scaled by how much water a pixel looks through, and looking UP from
// just below the surface that column is ~0 — so a fog-scaled knob is inert in exactly the
// place you are looking. Surface brightness and volume brightness are different controls
// because they are different pixels.
uniform float uw_underside_brightness : hint_range(0.1, 8.0, 0.01) = 3.82;
// Lift applied to the reflected/mirror side only, so the grazing region can be brought up
// without washing out the clear view through Snell's window.
uniform float uw_tir_lift : hint_range(0.0, 4.0, 0.01) = 2.09;
// How hard the underside refracts the world above. Larger than the topside value on
// purpose: looking up through water is total-internal-reflection territory and the
// distortion genuinely is stronger from below.
uniform float uw_underside_warp : hint_range(0.0, 0.25, 0.001) = 0.0475;
// Softness of Snell's window edge. 0 = a hard rim (physically closer, reads harsh),
// larger = a gentle fade from clear sky to mirror.
uniform float uw_snell_softness : hint_range(0.0, 0.5, 0.01) = 0.1575;

// SNELL'S WINDOW. Looking up from under water you do not see a uniform ceiling — you see
// the entire sky squeezed into a cone about 97 degrees wide straight overhead, and OUTSIDE
// that cone the surface is a mirror (total internal reflection). A flat tint at every angle
// is why the first version looked like a painted lid: correct thing, missing its angle
// dependence.
//
// The critical angle for water -> air is asin(1/1.333) = 48.6 degrees from vertical, so the
// test is cos(angle between the view ray and the surface normal) against cos(48.6) = 0.661.
// Inside the cone we look out at the world; outside it we fall back to the water's own
// colour, which stands in for the reflected underwater scene until there is a probe to
// reflect properly.
const float UW_COS_CRITICAL = 0.6614;   // cos(asin(1.0 / 1.333))

vec3 uw_underside_shade(vec3 base_topside, vec3 surf_normal, vec3 view_dir, vec2 screen_uv,
		sampler2D screen) {
	// Warp by the surface's OWN normal, so the ripples you see from below are the ripples
	// that are actually there — not a decorative noise that disagrees with the topside.
	vec2 off = surf_normal.xz * uw_underside_warp;
	vec3 above = texture(screen, clamp(screen_uv + off, vec2(0.001), vec2(0.999))).rgb;

	float ct = clamp(abs(dot(normalize(view_dir), normalize(surf_normal))), 0.0, 1.0);
	float window = smoothstep(UW_COS_CRITICAL - uw_snell_softness,
		UW_COS_CRITICAL + uw_snell_softness, ct);

	// Outside the cone it is a mirror, and there is nothing here to reflect yet — no probe,
	// no screen-space reflection of the underwater scene. So it falls back to the FOG's
	// colour, never to the water's topside colour. Fog-coloured is guaranteed to agree with
	// the volume, which is what stops a seam appearing at the edge of the cone.
	vec3 tir = mix(above, uw_tir_color * uw_tir_lift, uw_tir_strength);

	// No distance term anywhere in here. That is the fog's job, and it does it per pixel.
	vec3 under = mix(tir, above, window) * uw_underside_brightness;

	// Always shaded, never gated: the mask decides how much of the underside shows at this
	// pixel. 0 outside the volume, 1 inside, and the transition is the waterline itself.
	float m = texture(uw_mask_tex, screen_uv).r;
	return mix(base_topside, under, clamp(m, 0.0, 1.0));
}
