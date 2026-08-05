// Caustic-lit sandy seabed — a normal LIT surface for the ocean floor.
// The procedural caustics are the SAME GPU OpenSimplex2 noise + caustic_sample
// used by shaders/malidos_ocean.gdshader (verbatim, CC0 1.0 / public domain,
// https://github.com/Malidos/Stylized-Water-Shader). Here they're projected onto
// the real lit bed via triplanar from the fragment's WORLD position + surface
// NORMAL (no depth reconstruction needed — this IS a solid surface), and added as
// EMISSION so they read as dancing light on the sand regardless of shadow.
shader_type spatial;

uniform vec3 albedo : source_color = vec3(0.72, 0.64, 0.46); // sandy
uniform float roughness : hint_range(0.0, 1.0, 0.01) = 0.9;
uniform float metallic : hint_range(0.0, 1.0, 0.01) = 0.0;

group_uniforms caustics;
uniform float caustic_scale : hint_range(0.1, 4.0, 0.01) = 0.9;   // world units -> noise
uniform float caustic_strength : hint_range(0.0, 2.0, 0.01) = 0.6;
uniform float caustic_speed : hint_range(0.0, 2.0, 0.01) = 0.6;
uniform vec3 caustic_tint : source_color = vec3(1.0, 0.98, 0.9);

// ---- Boat shadow projected onto the seabed ----
// The boat's hull footprint, projected DOWN THE SUN onto this floor plane and
// drawn as an oriented ellipse. The scene feeds boat_pos / boat_yaw / sun_dir each
// frame. shadow_refracted wobbles the footprint edge by the water-surface tilt
// (a compact 4-wave Gerstner normal matching the scene's water) so it reads like a
// shadow cast through moving water; OFF gives a clean crisp cast.
group_uniforms boat_shadow;
uniform vec3 boat_pos = vec3(0.0, 0.0, 0.0);          // world position of the hull
uniform float boat_yaw = 0.0;                          // heading (radians)
uniform vec2 hull_half = vec2(1.0, 2.6);               // hull half-extents (beam, length)
uniform vec3 sun_dir = vec3(0.3, -0.9, 0.2);           // light TRAVEL dir (points down, y<0)
uniform float shadow_darkness : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float shadow_softness : hint_range(0.0, 1.0, 0.01) = 0.4;
uniform float shadow_size : hint_range(0.5, 4.0, 0.05) = 1.4;
uniform bool shadow_refracted = true;
uniform float shadow_refract_amount : hint_range(0.0, 2.0, 0.01) = 0.6;

// Wave field for the refracted ripple — MATCHES the water shader / GerstnerField.
// wave_time already folds in wave_speed (scene passes field.time * field.speed) so
// the phase equals the water shader's f = k*(dot(d,xz) - wave_speed*wave_time).
group_uniforms shadow_waves;
uniform float wave_time = 0.0;
uniform float wl = 17.09;                               // wavelength
uniform float steepness = 0.03;
uniform vec4 wave_dirs = vec4(0.175, 0.220, 0.470, 0.800);

varying vec3 world_pos;

// ---- OpenSimplex2 caustics (verbatim from malidos_ocean.gdshader) ----
// Permutation polynomial hash credit Stefan Gustavson
vec4 permute(vec4 t) {
	return t * (t * 34.0 + 133.0);
}

// Gradient set is a normalized expanded rhombic dodecahedron
vec3 grad(float hash) {
	// Random vertex of a cube, +/- 1 each
	vec3 cube = mod(floor(hash / vec3(1.0, 2.0, 4.0)), 2.0) * 2.0 - 1.0;

	// Random edge of the three edges connected to that vertex
	// Also a cuboctahedral vertex
	// And corresponds to the face of its dual, the rhombic dodecahedron
	vec3 cuboct = cube;
	cuboct[int(hash / 16.0)] = 0.0;

	// In a funky way, pick one of the four points on the rhombic face
	float type = mod(floor(hash / 8.0), 2.0);
	vec3 rhomb = (1.0 - type) * cube + type * (cuboct + cross(cube, cuboct));

	// Expand it so that the new edges are the same length
	// as the existing ones
	vec3 grad = fma(cuboct, vec3(1.22474487139), rhomb);

	// To make all gradients the same length, we only need to shorten the
	// second type of vector. We also put in the whole noise scale constant.
	// The compiler should reduce it into the existing floats. I think.
	grad *= fma(-0.042942436724648037, type, 1.0) * 3.5946317686139184;

	return grad;
}

// BCC lattice split up into 2 cube lattices
vec4 os2NoiseWithDerivativesPart(vec3 X) {
	vec3 b = floor(X);
	vec4 i4 = vec4(X - b, 2.5);

	// Pick between each pair of oppposite corners in the cube.
	vec3 v1 = b + floor(dot(i4, vec4(.25)));
	vec3 v2 = b + vec3(1, 0, 0) + vec3(-1, 1, 1) * floor(dot(i4, vec4(-.25, .25, .25, .35)));
	vec3 v3 = b + vec3(0, 1, 0) + vec3(1, -1, 1) * floor(dot(i4, vec4(.25, -.25, .25, .35)));
	vec3 v4 = b + vec3(0, 0, 1) + vec3(1, 1, -1) * floor(dot(i4, vec4(.25, .25, -.25, .35)));

	// Gradient hashes for the four vertices in this half-lattice.
	vec4 hashes = permute(mod(vec4(v1.x, v2.x, v3.x, v4.x), 289.0));
	hashes = permute(mod(hashes + vec4(v1.y, v2.y, v3.y, v4.y), 289.0));
	hashes = mod(permute(mod(hashes + vec4(v1.z, v2.z, v3.z, v4.z), 289.0)), 48.0);

	// Gradient extrapolations & kernel function
	vec3 d1 = X - v1; vec3 d2 = X - v2; vec3 d3 = X - v3; vec3 d4 = X - v4;
	vec4 a = max(0.75 - vec4(dot(d1, d1), dot(d2, d2), dot(d3, d3), dot(d4, d4)), 0.0);
	vec4 aa = a * a; vec4 aaaa = aa * aa;
	vec3 g1 = grad(hashes.x); vec3 g2 = grad(hashes.y);
	vec3 g3 = grad(hashes.z); vec3 g4 = grad(hashes.w);
	vec4 extrapolations = vec4(dot(d1, g1), dot(d2, g2), dot(d3, g3), dot(d4, g4));

	// Derivatives of the noise
	vec4 derivative = -8.0 * mat4(vec4(d1,0.), vec4(d2,0.), vec4(d3,0.), vec4(d4,0.)) * (aa * a * extrapolations)
		+ mat4(vec4(g1, 0.), vec4(g2, 0.), vec4(g3, 0.), vec4(g4, 0.)) * aaaa;

	// Return it all as a vec4
	return vec4(derivative.xyz, dot(aaaa, extrapolations));
}

// Rotates domain, but preserve shape. Hides grid better in cardinal slices.
// Good for texturing 3D objects with lots of flat parts along cardinal planes.
vec4 os2NoiseWithDerivatives_Fallback(vec3 X) {
	X = dot(X, vec3(2.0/3.0)) - X;

	vec4 result = os2NoiseWithDerivativesPart(X) + os2NoiseWithDerivativesPart(X + 144.5);

	return vec4(dot(result.xyz, vec3(2.0/3.0)) - result.xyz, result.w);
}

// Two-tap caustic noise sample for a single planar 2D coord + time slice.
// Wraps the original XZ sampling so each triplanar axis reuses identical math.
float caustic_sample(vec2 uv, float t) {
	vec3 X = vec3(uv, t);
	vec4 n = os2NoiseWithDerivatives_Fallback(X);
	n = os2NoiseWithDerivatives_Fallback(X - n.xyz / 16.0);
	return fma(n.w, 0.5, 0.5);
}

// ---- Compact Gerstner surface normal (matches ameye_water_boat.gdshader) ----
// Accumulates tangent/binormal from the same 4-wave Gerstner the water surface uses,
// then n = normalize(cross(binormal, tangent)). Only the tilt (n.xz) is needed here.
void gerstner_tb(vec2 pxz, float steep, float wavelen, float dir, inout vec3 tangent, inout vec3 binormal) {
	dir = dir * 2.0 - 1.0;
	vec2 d = normalize(vec2(cos(3.14159265 * dir), sin(3.14159265 * dir)));
	float k = 2.0 * 3.14159265 / wavelen;
	float f = k * (dot(d, pxz) - wave_time);  // wave_time already includes wave_speed
	tangent += vec3(-d.x * d.x * (steep * sin(f)), d.x * (steep * cos(f)), -d.x * d.y * (steep * sin(f)));
	binormal += vec3(-d.x * d.y * (steep * sin(f)), d.y * (steep * cos(f)), -d.y * d.y * (steep * sin(f)));
}

vec3 water_surface_normal(vec2 pxz) {
	vec3 tangent = vec3(1.0, 0.0, 0.0);
	vec3 binormal = vec3(0.0, 0.0, 1.0);
	gerstner_tb(pxz, steepness, wl, wave_dirs.x, tangent, binormal);
	gerstner_tb(pxz, steepness, wl, wave_dirs.y, tangent, binormal);
	gerstner_tb(pxz, steepness, wl, wave_dirs.z, tangent, binormal);
	gerstner_tb(pxz, steepness, wl, wave_dirs.w, tangent, binormal);
	return normalize(cross(binormal, tangent));
}

// Soft oriented shadow footprint at this fragment: 1 under the hull, feathered out.
float boat_shadow_mask(vec3 world) {
	// Project the hull down the sun onto this floor plane (sun_dir points down, y<0).
	float tproj = (boat_pos.y - world.y) / max(-sun_dir.y, 1e-3);
	vec2 center = boat_pos.xz + sun_dir.xz * tproj;

	// Sample point — optionally displaced by the water-surface tilt so the shadow
	// edge ripples as if the light bent through the moving surface (refraction).
	vec2 sample_xz = world.xz;
	if (shadow_refracted) {
		vec3 sn = water_surface_normal(world.xz);
		float depth = max(boat_pos.y - world.y, 0.0);   // water column above the bed
		sample_xz += sn.xz * shadow_refract_amount * depth;
	}

	// Into the hull's local frame: length axis = boat forward, beam axis = its perp.
	vec2 rel = sample_xz - center;
	vec2 fdir = vec2(-sin(boat_yaw), -cos(boat_yaw));   // along hull length
	vec2 rdir = vec2(-cos(boat_yaw),  sin(boat_yaw));   // along hull beam
	vec2 q = vec2(dot(rel, rdir) / (hull_half.x * shadow_size),
	              dot(rel, fdir) / (hull_half.y * shadow_size));

	float d = length(q);
	return 1.0 - smoothstep(1.0 - shadow_softness, 1.0, d);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Real lit surface: use the shading NORMAL directly (view space) and rotate
	// it back to world space — no depth reconstruction like the water needed.
	vec3 wn = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 bw = abs(wn);
	bw /= (bw.x + bw.y + bw.z + 1e-5);

	float t = TIME * caustic_speed;
	// Triplanar: blend three planar caustic projections by the world normal.
	float cy = caustic_sample(world_pos.xz * caustic_scale, t); // floor-facing (n.y)
	float cx = caustic_sample(world_pos.zy * caustic_scale, t); // x-facing wall
	float cz = caustic_sample(world_pos.xy * caustic_scale, t); // z-facing wall
	float caustics = cx * bw.x + cy * bw.y + cz * bw.z;

	// Sharpen the bright ridges a little so they read as light lines, not haze.
	caustics = pow(clamp(caustics, 0.0, 1.0), 2.0);

	// Boat shadow: darken the sand under the hull AND cut the caustic light web there,
	// so the shadow reads on both the seabed albedo and the dancing caustics.
	float s = boat_shadow_mask(world_pos);
	caustics *= (1.0 - s * shadow_darkness);

	ALBEDO = albedo * mix(1.0, 1.0 - shadow_darkness, s);
	ROUGHNESS = roughness;
	METALLIC = metallic;
	// Caustics as glowing light on the bed (survives shadow/ambient).
	EMISSION = caustic_tint * caustics * caustic_strength;
}
