extends SceneTree

## Standalone presentation capture. No gameplay scene or terrain is instantiated.
const VIEW := preload("res://scripts/ui/ring_loading_screen.gd")
const FULL_OUTPUT := "res://.godot/world_loading_preview.png"
const SMALL_OUTPUT := "res://.godot/world_loading_preview_small.png"
var _view

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("LOADING_HOLOGRAM_PREVIEW_FAIL Forward+ RenderingDevice required")
		quit(1)
		return
	call_deferred("_capture")

func _capture() -> void:
	root.content_scale_mode = Window.CONTENT_SCALE_MODE_DISABLED
	root.content_scale_size = Vector2i.ZERO
	var layer := CanvasLayer.new()
	layer.layer = 100
	root.add_child(layer)
	_view = VIEW.new()
	_view.configure({
		"world_title": "The Meridian Ring",
		"seed": 1337,
		"radius_m": 5000000.0,
		"circumference_m": TAU * 5000000.0,
		"width_m": 318000.0,
		"spawn_around_m": TAU * 5000000.0 * 0.16,
		"spawn_across_m": 159000.0,
		"is_existing": true
	})
	layer.add_child(_view)
	_view.set_progress("surroundings", "Adding trees and grass around your arrival point.", 0.62)
	# Use a stable pose so full/small captures can be compared independently of timing.
	_view.get("_hologram").set_presentation(0.18, false, 1.0)
	if not await _capture_size(Vector2i(1280, 720), FULL_OUTPUT):
		quit(1)
		return
	if not await _capture_size(Vector2i(960, 540), SMALL_OUTPUT):
		quit(1)
		return
	print("LOADING_HOLOGRAM_PREVIEW_PASS full=%s small=%s" % [FULL_OUTPUT, SMALL_OUTPUT])
	layer.queue_free()
	await process_frame
	quit(0)

func _capture_size(dimensions: Vector2i, output: String) -> bool:
	DisplayServer.window_set_size(dimensions)
	root.size = dimensions
	for frame in range(4):
		await process_frame
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	var saved := image != null and image.save_png(ProjectSettings.globalize_path(output)) == OK
	if not saved:
		push_error("LOADING_HOLOGRAM_PREVIEW_FAIL could not save " + output)
		return false
	var cancel: Button = _view.get("_cancel")
	var rect := cancel.get_global_rect()
	var visible := rect.position.y >= 0 and rect.end.y <= dimensions.y and rect.end.x <= dimensions.x
	print("Loading preview %dx%d: image=%dx%d cancelVisible=%s" % [dimensions.x, dimensions.y, image.get_width(), image.get_height(), visible])
	if not visible:
		push_error("LOADING_HOLOGRAM_PREVIEW_FAIL return control outside viewport")
	return visible
