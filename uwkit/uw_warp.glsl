#[compute]
#version 450

// 01b underwater FULL-FRAME warp — CompositorEffect compute pass (POST_TRANSPARENT).
// Runs on the FINISHED frame (seabed, rocks, fog, the water surface itself, god rays),
// so everything seen underwater wobbles — the thing the old fullscreen quad could
// never do (it only ever saw the post-opaque screen copy and erased the water planes).
// The warp is the old quad's animated 2-channel value noise, unchanged.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict writeonly image2D color_image;
layout(set = 1, binding = 0) uniform sampler2D color_copy;
// Volume mask: 1 where this pixel is under the water. The warp is SCALED by it, so a
// half-submerged frame is distorted on the submerged half only — per pixel, instead of the
// whole effect being switched on when a camera boolean flips.
layout(set = 1, binding = 1) uniform sampler2D mask_tex;

layout(push_constant, std430) uniform Params {
	vec2 raster_size;
	float time;      // pre-multiplied by speed on the CPU
	float amount;
} params;

float hash12(vec2 p) {
	uvec2 q = uvec2(ivec2(p)) * uvec2(1597334677u, 3812015801u);
	uint n = (q.x ^ q.y) * 1597334677u;
	return float(n) * (1.0 / 4294967295.0);
}
float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return -1.0 + 2.0 * mix(
		mix(hash12(i + vec2(0.0, 0.0)), hash12(i + vec2(1.0, 0.0)), u.x),
		mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}

void main() {
	ivec2 pix = ivec2(gl_GlobalInvocationID.xy);
	if (pix.x >= int(params.raster_size.x) || pix.y >= int(params.raster_size.y)) {
		return;
	}
	vec2 uv = (vec2(pix) + 0.5) / params.raster_size;
	vec2 noise_dir = vec2(
		vnoise(uv * 12.0 + params.time * vec2(1.0, 0.4)),
		vnoise(uv * 12.0 + params.time * vec2(0.6, 1.0)));
	vec2 warped = uv + noise_dir * params.amount * texture(mask_tex, uv).r;
	imageStore(color_image, pix, texture(color_copy, warped));
}
