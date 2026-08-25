extends CompositorEffect

# uwkit — FULL-FRAME UNDERWATER WARP. Lifted from water-kit scene 01b
# (scripts/lib/underwater_warp_effect.gd) into the kit so every water gets it.
#
# Runs POST_TRANSPARENT, i.e. on the fully composited frame — seabed, rocks, fog, the water
# surface itself, god-rays: everything wobbles. That is the correct structure, and a
# screen-reading quad cannot do it (Godot takes ONE screen copy after the opaque pass, and
# every screen-reading material renders in the transparent pass from that same copy).
#
# MASKED, not gated: the warp is scaled per pixel by the water volume mask, so a
# half-submerged frame distorts on the submerged half only.

# Full-frame underwater warp — a REAL post-process (CompositorEffect, compute).
# Runs POST_TRANSPARENT, i.e. on the fully-composited frame: seabed, rocks, fog,
# the water surface itself, god rays — everything wobbles. This is the correct
# structure for a fullscreen underwater distortion; a screen-reading quad cannot
# do it (one SCREEN_TEXTURE copy post-opaque, invisible to other screen-readers).
# Two dispatches of the same shader (the color buffer lacks CAN_COPY_FROM, so a
# compute pass with amount=0 does the copy): color -> scratch, then scratch -> color
# warped, sampling through a linear clamp sampler.
#
# Toggle per-frame from the scene via `enabled`; tune via `amount` / `speed`.

const WARP_GLSL := "res://uwkit/uw_warp.glsl"

var amount := 0.006
## Volume mask (RD texture). Where 0, the warp is not applied — per-pixel, not on/off.
var mask_texture: RID
var speed := 0.7

var _rd: RenderingDevice
var _shader: RID
var _pipeline: RID
var _sampler: RID

const CONTEXT := &"UnderwaterWarp"
const COPY_TEX := &"color_copy"


func _init() -> void:
	effect_callback_type = EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	_rd = RenderingServer.get_rendering_device()
	RenderingServer.call_on_render_thread(_initialize_compute)


func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE and _rd != null:
		if _sampler.is_valid():
			_rd.free_rid(_sampler)
		if _shader.is_valid():
			_rd.free_rid(_shader)  # frees the pipeline with it


func _initialize_compute() -> void:
	var glsl := load(WARP_GLSL) as RDShaderFile
	_shader = _rd.shader_create_from_spirv(glsl.get_spirv())
	_pipeline = _rd.compute_pipeline_create(_shader)
	var ss := RDSamplerState.new()
	ss.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	ss.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	ss.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	ss.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	_sampler = _rd.sampler_create(ss)


func _render_callback(p_type: int, p_render_data: RenderData) -> void:
	if p_type != EFFECT_CALLBACK_TYPE_POST_TRANSPARENT or not _pipeline.is_valid():
		return
	var buffers := p_render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null:
		return
	var size := buffers.get_internal_size()
	if size.x == 0 or size.y == 0:
		return

	var t := float(Time.get_ticks_msec()) / 1000.0 * speed
	var copy_push := PackedFloat32Array([size.x, size.y, 0.0, 0.0]).to_byte_array()
	var warp_push := PackedFloat32Array([size.x, size.y, t, amount]).to_byte_array()
	var groups_x := (size.x - 1) / 8 + 1
	var groups_y := (size.y - 1) / 8 + 1

	for view in buffers.get_view_count():
		var color := buffers.get_color_layer(view)
		# cached scratch texture matching the internal size (recreated on resize)
		var scratch := buffers.create_texture(CONTEXT, COPY_TEX,
			RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT,
			RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_STORAGE_BIT,
			RenderingDevice.TEXTURE_SAMPLES_1, size, 1, 1, true, false)
		_dispatch(scratch, color, copy_push, groups_x, groups_y)  # amount 0 = copy
		_dispatch(color, scratch, warp_push, groups_x, groups_y)  # warped write-back


func _dispatch(dst_image: RID, src_tex: RID, push: PackedByteArray, gx: int, gy: int) -> void:
	var u_img := RDUniform.new()
	u_img.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	u_img.binding = 0
	u_img.add_id(dst_image)
	var u_src := RDUniform.new()
	u_src.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
	u_src.binding = 0
	u_src.add_id(_sampler)
	u_src.add_id(src_tex)
	var u_mask := RDUniform.new()
	u_mask.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
	u_mask.binding = 1
	u_mask.add_id(_sampler)
	u_mask.add_id(mask_texture if mask_texture.is_valid() else src_tex)
	var set0 := UniformSetCacheRD.get_cache(_shader, 0, [u_img])
	var set1 := UniformSetCacheRD.get_cache(_shader, 1, [u_src, u_mask])

	var cl := _rd.compute_list_begin()
	_rd.compute_list_bind_compute_pipeline(cl, _pipeline)
	_rd.compute_list_bind_uniform_set(cl, set0, 0)
	_rd.compute_list_bind_uniform_set(cl, set1, 1)
	_rd.compute_list_set_push_constant(cl, push, push.size())
	_rd.compute_list_dispatch(cl, gx, gy, 1)
	_rd.compute_list_end()
