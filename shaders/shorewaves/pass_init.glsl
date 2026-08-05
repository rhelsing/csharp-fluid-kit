#[compute]
#version 450

// One-shot: lake-at-rest initial state from the bottom texture into both
// ping-pong state textures. w = max(B, 0), everything else zero.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_bottom;
layout(rgba32f, set = 0, binding = 1) uniform restrict writeonly image2D img_state_a;
layout(rgba32f, set = 0, binding = 2) uniform restrict writeonly image2D img_state_b;

void main() {
	ivec2 sz = imageSize(img_bottom);
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= sz.x || id.y >= sz.y) {
		return;
	}
	float B = imageLoad(img_bottom, id).r;
	vec4 q = vec4(max(B, 0.0), 0.0, 0.0, 0.0);
	imageStore(img_state_a, id, q);
	imageStore(img_state_b, id, q);
}
