// uwkit — MASKED PARTICLE draw material. Billboarded, additive, unshaded, vertex-coloured,
// with alpha multiplied by the water volume mask.
//
// Replaces a StandardMaterial3D, which cannot sample anything — which is precisely why
// particles could only ever be switched on and off as a node. Motes and bubbles now exist
// per pixel, where the water does.
// Particle draw material, masked by the water volume.
//
// Replaces a StandardMaterial3D, which cannot sample anything and so could only be switched
// on and off as a node. Same look — additive, unshaded, billboarded, vertex-coloured — with
// alpha multiplied by the mask so motes only exist where the water does.
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, shadows_disabled, depth_draw_never;

uniform sampler2D albedo_tex : source_color, filter_linear;
uniform sampler2D uw_mask_tex : filter_linear;

void vertex() {
	// Particle billboard, keeping scale — the shader equivalent of BILLBOARD_PARTICLES with
	// billboard_keep_scale.
	mat4 mv = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
	mv = mv * mat4(
		vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
		vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
		vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
		vec4(0.0, 0.0, 0.0, 1.0));
	MODELVIEW_MATRIX = mv;
}

void fragment() {
	vec4 t = texture(albedo_tex, UV);
	ALBEDO = t.rgb * COLOR.rgb;
	ALPHA = t.a * COLOR.a * texture(uw_mask_tex, SCREEN_UV).r;
}
