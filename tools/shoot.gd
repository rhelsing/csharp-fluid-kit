extends Node

# Non-invasive screenshot harness (ported verbatim from water-kit). Loads a real
# scene by path (first user arg), lets it run `secs` seconds (second arg, default
# 4), captures the viewport to tools/shot_<scene>.png, and quits. Uses the
# scene's OWN camera/lights so the PNG reflects exactly what it looks like.
#
# This is GDScript on purpose: it's tooling, not an experiment, and GDScript's
# no-build-step is ideal for a screenshot tool. The .NET build runs it fine and
# it renders C# scenes exactly the same as GDScript ones.
#
# A visual system reads "fine" in numbers and wrong on screen — so this is THE
# way to look at a scene. Bump the 3rd arg "WxH" when a UI panel covers content.
#
# Run (must render — NO --headless; must be arm64 on this machine — see CLAUDE.md):
#   arch -arm64 <godot-mono> --path . res://tools/shoot.tscn -- res://scenes/01_hello_csharp.tscn 2
#   arch -arm64 <godot-mono> --path . res://tools/shoot.tscn -- res://scenes/01_hello_csharp.tscn 2 1600x1200

const DEFAULT_SIZE := Vector2i(1024, 768)

var _t := 0.0
var _secs := 4.0
var _shot_name := "scene"
var _done := false


func _ready() -> void:
	var ua := OS.get_cmdline_user_args()
	if ua.is_empty():
		printerr("SHOOT: pass a scene path as user arg")
		get_tree().quit(1)
		return
	var target: String = ua[0]
	if ua.size() > 1:
		_secs = float(ua[1])
	DisplayServer.window_set_size(_parse_size(ua[2] if ua.size() > 2 else ""))
	_shot_name = target.get_file().get_basename()
	var ps := load(target) as PackedScene
	if ps == null:
		printerr("SHOOT: could not load %s" % target)
		get_tree().quit(1)
		return
	add_child(ps.instantiate())
	print("SHOOT: running %s for %.1fs" % [target, _secs])


func _parse_size(s: String) -> Vector2i:
	var parts := s.to_lower().split("x")
	if parts.size() == 2 and parts[0].is_valid_int() and parts[1].is_valid_int():
		return Vector2i(int(parts[0]), int(parts[1]))
	return DEFAULT_SIZE


func _process(delta: float) -> void:
	if _done:
		return
	_t += delta
	if _t >= _secs:
		_done = true
		await RenderingServer.frame_post_draw
		await RenderingServer.frame_post_draw  # let the drawn frame land
		var img := get_viewport().get_texture().get_image()
		var path := "res://tools/shot_%s.png" % _shot_name
		img.save_png(path)
		print("SHOOT saved %s" % path)
		get_tree().quit()
