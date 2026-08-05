#[compute]
#version 450

// Bridges the MNA GPU solver (scene 25) into scene 15's RAYTRACED renderer.
// The ww_* shaders sample a water texture packed as R=height, B=normal.x,
// A=normal.z (they reconstruct normal.y = sqrt(1-b*b-a*a)). The stamp solver only
// produces height (R32F), so this kernel reads it and writes that packed RGBA
// layout: height scaled to fit the pool, plus a gradient normal. Same idea as
// scene 15's sim_normal pass, done as a compute dispatch.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(r32f, set = 0, binding = 0) uniform image2D h_in;
layout(rgba32f, set = 1, binding = 0) uniform image2D packed_out;

layout(push_constant, std430) uniform Params {
	vec2 size;
	float height_scale;   // world height per solver unit (fit into the pool)
	float normal_scale;   // ripple steepness for refraction/caustics
} p;

void main() {
	ivec2 c = ivec2(gl_GlobalInvocationID.xy);
	ivec2 mx = ivec2(p.size) - ivec2(1);
	if (c.x > mx.x || c.y > mx.y) {
		return;
	}
	float h = imageLoad(h_in, c).r;
	float hR = imageLoad(h_in, clamp(c + ivec2(1, 0), ivec2(0), mx)).r;
	float hU = imageLoad(h_in, clamp(c + ivec2(0, 1), ivec2(0), mx)).r;

	float gx = (hR - h) * p.normal_scale;
	float gz = (hU - h) * p.normal_scale;
	vec3 n = normalize(vec3(-gx, 1.0, -gz));

	imageStore(packed_out, c, vec4(h * p.height_scale, 0.0, n.x, n.z));
}
