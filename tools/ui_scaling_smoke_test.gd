extends SceneTree

const START_MENU := preload("res://scenes/StartMenu.tscn")
const TEST_SIZES := [Vector2i(1280, 720), Vector2i(1600, 900), Vector2i(1920, 1080)]

var _scene: Node
var _frame := 0
var _size_index := 0
var _target_button: Button
var _failed := false


func _initialize() -> void:
	for screen in range(DisplayServer.get_screen_count()):
		print(
			"UI screen %d rect=%s usable=%s scale=%.3f dpi=%d"
			% [screen, DisplayServer.screen_get_position(screen), DisplayServer.screen_get_usable_rect(screen), DisplayServer.screen_get_scale(screen), DisplayServer.screen_get_dpi(screen)]
		)
	_scene = START_MENU.instantiate()
	root.add_child(_scene)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 4:
		_target_button = _find_button(_scene)
		if _target_button == null:
			return _fail("Could not find a start-menu button.")
		_apply_test_size()
	elif _frame == 8:
		_run_coordinate_probe()
	elif _frame == 12:
		print(
			"UI hover result requested=%s actual=%s windowPos=%s usable=%s osMouse=%s viewportMouse=%s hovered=%s"
			% [
				TEST_SIZES[_size_index],
				DisplayServer.window_get_size(),
				DisplayServer.window_get_position(),
				DisplayServer.screen_get_usable_rect(DisplayServer.SCREEN_OF_MAIN_WINDOW),
				DisplayServer.mouse_get_position(),
				root.get_mouse_position(),
				_target_button.is_hovered()
			]
		)
		if not _target_button.is_hovered():
			return _fail("Button visual-center hover failed at %s." % TEST_SIZES[_size_index])
		_size_index += 1
		if _size_index >= TEST_SIZES.size():
			print("UI scaling smoke test passed at all window sizes.")
			quit(0)
			return true
		_frame = 3
		_apply_test_size()
	return false


func _apply_test_size() -> void:
	DisplayServer.window_set_size(TEST_SIZES[_size_index])


func _run_coordinate_probe() -> void:
	var viewport_center := _target_button.get_global_rect().get_center()
	var stretch := root.get_stretch_transform()
	var unscaled_screen_center := _target_button.get_screen_transform() * (_target_button.size * 0.5)
	var physical_visual_center := stretch * unscaled_screen_center
	print(
		"UI probe size=%s root=%s visible=%s screenScale=%.3f dpi=%d contentSize=%s mode=%d aspect=%d factor=%.3f stretch=%s viewportCenter=%s physicalVisualCenter=%s"
		% [
			DisplayServer.window_get_size(),
			root.size,
			root.get_visible_rect(),
			DisplayServer.screen_get_scale(DisplayServer.SCREEN_OF_MAIN_WINDOW),
			DisplayServer.screen_get_dpi(DisplayServer.SCREEN_OF_MAIN_WINDOW),
			root.content_scale_size,
			root.content_scale_mode,
			root.content_scale_aspect,
			root.content_scale_factor,
			stretch,
			viewport_center,
			physical_visual_center
		]
	)
	Input.warp_mouse(physical_visual_center)


func _find_button(node: Node) -> Button:
	if node is Button and (node as Button).is_visible_in_tree():
		return node as Button
	for child in node.get_children():
		var found := _find_button(child)
		if found != null:
			return found
	return null


func _fail(message: String) -> bool:
	push_error(message)
	_failed = true
	quit(1)
	return true
