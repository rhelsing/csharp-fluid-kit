extends CompositorEffect

# uwkit — UwBoundaryEffect. PROOF 1 of the modular underwater kit.
#
# A CompositorEffect running POST_TRANSPARENT, i.e. on the fully composited frame. That
# choice is the point, not an implementation detail:
#
#   A fullscreen screen-reading QUAD cannot do this job. Godot takes ONE screen copy after
#   the opaque pass, and every screen-reading material — a refracting water surface very
#   much included — renders in the transparent pass from that same copy, invisible to the
#   others. An ALPHA=1 quad therefore erases the water. water-kit scene 01b hit this and
#   documented it; water-kit's kit/underwater/underwater_haze.gdshader IS such a quad, which
#   is the one structural flaw in an otherwise cleaner design. This kit takes 01b's
#   mechanism and the kit's abstractions.
#
# Structure ported from water-kit `scripts/lib/underwater_warp_effect.gd` (the proven
# skeleton: _init on the render thread, UniformSetCacheRD, PREDELETE cleanup). Copy-not-
# reference — this file is meant to be byte-identical in every repo that uses it, and the
# proof of that is applying it unchanged to a copy of scene 207.
#
# GDScript ON PURPOSE, in a C# project: Godot runs both, and the kit has to land in
# water-kit (GDScript) as well as here. Only the HOST scene stays C#.

const TEMPLATE := "res://uwkit/uw_boundary.glslinc"
const PROVIDER_MARKER := "//__UW_PROVIDER__"

## THE MODULARITY CONTRACT, in one line: point this at a .glslinc that defines
##     float uw_surface_height(vec2 world_xz)
## and the kit works against that water. Nothing else in the kit changes — that claim is
## exactly what proof 3 tests, and what a copy of water-kit scene 207 has to satisfy.
var provider_path := "res://uwkit/providers/uw_flat.glslinc"
const CONTEXT := &"UwBoundary"

## Sea level in world units — the datum the surface oscillates about.
var sea_level := 0.0
## PROOF-1 test ripple. 0 = flat sea (the boundary must then be a straight line).
var test_amplitude := 0.0
var test_frequency := 0.35
## Debug tint on/off without detaching the effect.
var show_boundary := true

## Texture-backed providers: the height texture and the world-space window it covers.
## Exactly the shape scene 50's MNA window already publishes (mna_tex / mna_origin /
## mna_window) and the shape water-kit's FFT cascades take. Analytic providers ignore all
## three, but the binding is always made so one template serves both.
var height_texture: RID
var window_origin := Vector2.ZERO
var window_size := 80.0

## Optional extra cascades for stacked oceans (FFT). Scale 0 = unused.
var height_texture_b: RID
var height_texture_c: RID
var window_size_b := 0.0
var window_size_c := 0.0

## Screen-space mask rasterised from the water geometry (0 above, 1 below). When set, the
## boundary is READ rather than reconstructed — see the shader header.
var mask_texture: RID
## Use the mask for the fog boundary. Off = the analytic eye-height fade, for waters that
## have no mask pass yet.
var use_mask := false

## 0 = FOG (the real renderer)  ·  1 = depth probe  ·  2 = water column, banded metres
var debug_mode := 0

## Exponential fog density per metre of water, and the distance over which the fog colour
## shifts from near to far. Defaults are water-kit's UnderwaterHaze values.
var fog_density := 0.146
var fog_transition := 148.3

## Gain on everything seen through water. 1 = untouched. Applied in proportion to the fog,
## so it cannot introduce a pop at the surface.
var underwater_brightness := 0.97

## Metres ABOVE the surface over which fog fades to nothing. Small on purpose: fog is for
## being IN the water. A hard gate on "eye is below" would pop the whole screen at the
## crossing, so this is deliberately a distance and not a bool.
var above_fade := 0.129

## Distance of the LINE view's classification plane, in metres. z_near (~0.05) reproduces
## proof 1 exactly and is nearly invisible in use; larger values keep the line on screen
## from further away.
var line_distance := 2.0

## Width of the smoothing band across the waterline, in METRES of surface height. This is
## what turns a stepped boolean edge into a graded mask; 0 would restore the staircase.
var line_softness := 0.03

## Slides the division up (+) or down (-) in metres. THE alignment control: probe radius
## only changes reach, so if the line sits below the visible waterline this is the knob.
var mask_bias := 0.0
## Steepens (>1) or softens (<1) how fast the mask swings through the transition, without
## moving where it sits.
var mask_gain := 1.0
## Metres along the horizontal view direction to shift WHERE the wave is read. Shifts phase
## without moving the line — the practical way to re-time a texture-baked provider.
var sample_offset := 0.0
## Scales the sampled wave height about sea level. Amplitude, independent of phase.
var sample_amplitude := 1.0

## THE WATER'S COLOUR, single source of truth. The underside chunk needs the same value —
## the host pushes it to the material — so tuning one no longer silently disagrees with the
## other. Defaults are water-kit's UnderwaterHaze pair (#1a5c62 -> #0a2830).
var fog_near_color := Color(0.102, 0.361, 0.384)
var fog_far_color := Color(0.039, 0.157, 0.188)

## Composite wave bands, 4 floats each: wavelength, steepness, speed, direction(0..1).
## Set by the host from whatever its water actually uses; the gerstner provider sums them.
var _bands := PackedFloat32Array()
var _band_count := 0
var _ubo: RID
var _ubo_dirty := true

const UBO_BYTES := 192   # 8 band vec4s + meta + 2 colour vec4s + tune


## Hand the kit the water's actual bands. Flat/ripple/texture providers ignore them.
func set_bands(bands: PackedFloat32Array, count: int) -> void:
	_bands = bands
	_band_count = count
	_ubo_dirty = true


func _ubo_bytes() -> PackedByteArray:
	var f := PackedFloat32Array()
	f.resize(48)   # 8*4 bands + 4 meta + 4 + 4 colours + 4 tune
	for i in mini(_bands.size(), 32):
		f[i] = _bands[i]
	f[32] = float(_band_count)
	f[33] = window_size_b
	f[34] = window_size_c
	f[35] = line_softness
	f[36] = fog_near_color.r; f[37] = fog_near_color.g; f[38] = fog_near_color.b; f[39] = 1.0
	f[40] = fog_far_color.r;  f[41] = fog_far_color.g;  f[42] = fog_far_color.b;  f[43] = 1.0
	f[44] = mask_bias
	f[45] = mask_gain
	f[46] = 1.0 if use_mask else 0.0
	f[47] = sample_amplitude
	return f.to_byte_array()

var _rd: RenderingDevice
var _shader: RID
var _pipeline: RID
var _sampler: RID


func _init() -> void:
	effect_callback_type = EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	_rd = RenderingServer.get_rendering_device()
	if _rd == null:
		# Headless has no rendering device. Bail rather than crash: the repo's smoke tests
		# run headless and a null RD here would take the whole scene down.
		push_warning("[uwkit] no rendering device — boundary effect disabled")
		return
	RenderingServer.call_on_render_thread(_initialize_compute)


func _notification(what: int) -> void:
	# INLINE, deliberately not a call to cleanup(). During PREDELETE the object is already
	# partly torn down and a method call on self fails with "Attempt to call function in base
	# 'null instance'" — hit for real in water-kit scene 211. water-kit's own proven
	# CompositorEffect frees inline for exactly this reason; refactoring it into a method for
	# the C# host's benefit broke the GDScript host.
	#
	# Both paths are idempotent (every free is guarded by is_valid), so a host that calls
	# cleanup() explicitly and then gets PREDELETE frees nothing twice.
	if what == NOTIFICATION_PREDELETE and _rd != null:
		if _shader.is_valid():
			_rd.free_rid(_shader)
			_shader = RID()
		if _sampler.is_valid():
			_rd.free_rid(_sampler)
			_sampler = RID()
		if _ubo.is_valid():
			_rd.free_rid(_ubo)
			_ubo = RID()
		_pipeline = RID()


## Free GPU resources. A C# host MUST call this explicitly before dropping the effect:
## Dispose() from C# does not run GDScript's PREDELETE, and the shader then outlives the
## RenderingDevice ("1 RID of type Shader was leaked"). A GDScript host needs nothing — its
## PREDELETE fires normally and frees inline above. This asymmetry is the only thing the two
## hosts do differently, and it is a property of the C#/GDScript boundary, not of the kit.
func cleanup() -> void:
	if _rd == null:
		return
	if _shader.is_valid():
		_rd.free_rid(_shader)   # frees the pipeline with it
		_shader = RID()
	if _sampler.is_valid():
		_rd.free_rid(_sampler)
		_sampler = RID()
	if _ubo.is_valid():
		_rd.free_rid(_ubo)
		_ubo = RID()
	_pipeline = RID()


func _initialize_compute() -> void:
	var tmpl := FileAccess.get_file_as_string(TEMPLATE)
	var prov := FileAccess.get_file_as_string(provider_path)
	if tmpl == "" or prov == "":
		push_error("[uwkit] missing template or provider (%s)" % provider_path)
		return
	if not tmpl.contains(PROVIDER_MARKER):
		push_error("[uwkit] template has no %s marker" % PROVIDER_MARKER)
		return

	var src := RDShaderSource.new()
	src.language = RenderingDevice.SHADER_LANGUAGE_GLSL
	src.source_compute = tmpl.replace(PROVIDER_MARKER, prov)
	var spirv := _rd.shader_compile_spirv_from_source(src)
	var err := spirv.get_stage_compile_error(RenderingDevice.SHADER_STAGE_COMPUTE)
	if err != "":
		push_error("[uwkit] compile error with provider %s:\n%s" % [provider_path, err])
		return
	_shader = _rd.shader_create_from_spirv(spirv)
	_pipeline = _rd.compute_pipeline_create(_shader)
	# LINEAR + REPEAT, explicitly. RDSamplerState defaults to NEAREST, which point-samples the
	# height texture and hands back a value quantised to texels — a staircase waterline that
	# looks exactly like threshold aliasing and is not. REPEAT because cascade textures tile,
	# so a bilinear tap straddling the edge must wrap rather than clamp.
	var ss := RDSamplerState.new()
	ss.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	ss.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	ss.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	ss.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	_sampler = _rd.sampler_create(ss)
	if not _ubo.is_valid():
		_ubo = _rd.uniform_buffer_create(UBO_BYTES, _ubo_bytes())
	print("[uwkit] boundary ready · provider=%s" % provider_path.get_file())


## Recompile against a different provider at runtime. This is the swap proof 3 measures:
## one call, no other edit anywhere in the kit.
func set_provider(path: String) -> void:
	provider_path = path
	RenderingServer.call_on_render_thread(func() -> void:
		cleanup()
		_initialize_compute())


func _render_callback(p_type: int, p_render_data: RenderData) -> void:
	if p_type != EFFECT_CALLBACK_TYPE_POST_TRANSPARENT or not _pipeline.is_valid():
		return
	if not show_boundary:
		return
	var buffers := p_render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := p_render_data.get_render_scene_data() as RenderSceneDataRD
	if buffers == null or scene == null:
		return
	var size := buffers.get_internal_size()
	if size.x == 0 or size.y == 0:
		return

	# Camera basis + tangent half-angles instead of an inverse view-projection: no NDC
	# convention to get silently backwards. See the shader header.
	var cam := scene.get_cam_transform()
	var proj := scene.get_cam_projection()
	# MAGNITUDES, and this is the bug that proof 1 actually caught. Godot's Projection here
	# reports z_near as NEGATIVE (-0.05) and m11 negative — a Vulkan-style depth/Y-flip
	# convention. Used raw, `origin + forward * z_near` puts the near point BEHIND the eye,
	# which inverts the whole test: pitch up and you get classified as underwater.
	#
	# The lesson, since the shader header claims to dodge NDC conventions by using the
	# camera basis rather than an inverse view-projection: it dodged them in the matrix and
	# then let them back in through two scalars pulled off that same matrix. The direction
	# is carried entirely by the basis; the projection supplies only distances, so take the
	# projection's numbers as sizes and never as directions.
	var z_near: float = absf(proj.get_z_near())
	var tan_y: float = absf(1.0 / proj[1][1])
	var tan_x: float = absf(1.0 / proj[0][0])
	var t := float(Time.get_ticks_msec()) / 1000.0

	var push := PackedFloat32Array([
		cam.basis.x.x, cam.basis.x.y, cam.basis.x.z, underwater_brightness,
		cam.basis.y.x, cam.basis.y.y, cam.basis.y.z, above_fade,
		-cam.basis.z.x, -cam.basis.z.y, -cam.basis.z.z, line_distance,   # basis.z points BACK
		cam.origin.x, cam.origin.y, cam.origin.z, z_near,
		tan_x, tan_y, float(size.x), float(size.y),
		sea_level, test_amplitude, test_frequency, t,
		window_origin.x, window_origin.y, window_size, float(debug_mode),
		proj[2][2], proj[3][2], fog_density, fog_transition,
	]).to_byte_array()

	if _ubo.is_valid():
		_ubo_dirty = false
		_rd.buffer_update(_ubo, 0, UBO_BYTES, _ubo_bytes())

	var gx := (size.x - 1) / 8 + 1
	var gy := (size.y - 1) / 8 + 1

	for view in buffers.get_view_count():
		var color := buffers.get_color_layer(view)
		var u_img := RDUniform.new()
		u_img.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
		u_img.binding = 0
		u_img.add_id(color)
		# A texture must ALWAYS be bound or the set is incomplete, even when the provider is
		# analytic and never samples it. Falling back to the colour buffer costs nothing and
		# avoids maintaining a dummy 1x1 texture just to satisfy the layout.
		var u_h := RDUniform.new()
		u_h.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		u_h.binding = 1
		u_h.add_id(_sampler)
		u_h.add_id(height_texture if height_texture.is_valid() else color)
		var u_d := RDUniform.new()
		u_d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		u_d.binding = 2
		u_d.add_id(_sampler)
		u_d.add_id(buffers.get_depth_layer(view))
		var u_hb := RDUniform.new()
		u_hb.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		u_hb.binding = 4
		u_hb.add_id(_sampler)
		u_hb.add_id(height_texture_b if height_texture_b.is_valid() else color)
		var u_hc := RDUniform.new()
		u_hc.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		u_hc.binding = 5
		u_hc.add_id(_sampler)
		u_hc.add_id(height_texture_c if height_texture_c.is_valid() else color)
		var u_m := RDUniform.new()
		u_m.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		u_m.binding = 6
		u_m.add_id(_sampler)
		u_m.add_id(mask_texture if mask_texture.is_valid() else color)
		var u_k := RDUniform.new()
		u_k.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
		u_k.binding = 3
		u_k.add_id(_ubo)
		var set0 := UniformSetCacheRD.get_cache(_shader, 0, [u_img, u_h, u_d, u_k, u_hb, u_hc, u_m])

		var cl := _rd.compute_list_begin()
		_rd.compute_list_bind_compute_pipeline(cl, _pipeline)
		_rd.compute_list_bind_uniform_set(cl, set0, 0)
		_rd.compute_list_set_push_constant(cl, push, push.size())
		_rd.compute_list_dispatch(cl, gx, gy, 1)
		_rd.compute_list_end()
