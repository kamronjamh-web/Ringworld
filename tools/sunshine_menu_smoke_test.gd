extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const GeneratedOutputPaths := preload("res://tools/generated_output_paths.gd")
const CAPTURE_FILE := "sunshine_live_menu.png"
const CLOUD_PROBE := preload("res://tools/GameplayCloudTuningProbe.cs")
const ISOLATED_DEFAULTS := "res://.godot/sunshine_menu_defaults_test.tres"

var _scene: Node
var _bridge: Node
var _frame := 0
var _catalog_button: Button
var _menu_scroll: ScrollContainer
var _menu: Node
var _menu_panel: Control
var _stationary_history_slider: HSlider
var _moving_history_slider: HSlider
var _history_apply_count_before := 0
var _capture_path := ""


func _initialize() -> void:
	_capture_path = GeneratedOutputPaths.smoke_capture_path(CAPTURE_FILE)
	_scene = TEST_SCENE.instantiate()
	_bridge = _scene.get_node("SunshineCloudBridge")
	# Cloud gestures now autosave; never let this UI regression author the
	# developer's actual gameplay preset.
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_DEFAULTS))
	var preset_probe: RefCounted = CLOUD_PROBE.new()
	if not preset_probe.InstallShared(_bridge, ISOLATED_DEFAULTS):
		push_error("Could not isolate live-menu cloud defaults")
		quit(1)
		return
	# Keep the integration smoke inexpensive; the menu still begins from the
	# production defaults when the normal scene is launched.
	_bridge.set("CloudResolutionScale", 0)
	_bridge.set("RaymarchSteps", 128)
	_bridge.set("CastCloudShadows", false)
	root.add_child(_scene)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 5:
		_call_bridge(
			"ApplyLiveTuning",
			"apply_live_tuning",
			[1, 0.50, 0.10, 64, 16.0, 0.18, 0.8, 4500.0, 2.8, 0.95])
		_call_bridge("SpawnStormFrontAhead", "spawn_storm_front_ahead")
	elif _frame == 10:
		_call_bridge("SpawnAnvilClusterAhead", "spawn_anvil_cluster_ahead")
		_call_bridge("SpawnCatalogCloudAhead", "spawn_catalog_cloud_ahead", [9])
		# These controls live in the non-pausing grave/live tuning drawer, not
		# the pause menu. The old smoke inspected the wrong UI and therefore never
		# exercised either temporal-history slider.
		_menu = _scene.get_node("LiveTuningDrawer")
		_menu.call("SetBenchmarkDrawerOpen", true)
		_menu_panel = _menu.get_node("LiveTuningDrawer") as Control
		var tabs := _find_tabs(_menu)
		if tabs != null:
			for index in range(tabs.get_tab_count()):
				if tabs.get_tab_title(index) == "Clouds":
					tabs.current_tab = index
					break
		_catalog_button = _find_button(_menu, "Spawn selected")
		var catalog_options := _find_catalog_options(_menu)
		_stationary_history_slider = _find_slider(_menu, "Stationary history")
		_moving_history_slider = _find_slider(_menu, "Moving history")
		_history_apply_count_before = int(_bridge.get("TemporalHistoryApplyCount"))
		if (
			_catalog_button == null
			or catalog_options == null
			or catalog_options.item_count != 10
			or _stationary_history_slider == null
			or _moving_history_slider == null
		):
			push_error("Sunshine live-menu cloud or history controls are missing or incomplete")
			quit(1)
			return false
		_stationary_history_slider.value = 0.42
		_moving_history_slider.value = 0.11
		var parent := _catalog_button.get_parent()
		while parent != null and not parent is ScrollContainer:
			parent = parent.get_parent()
		if parent is ScrollContainer:
			_menu_scroll = parent as ScrollContainer
	elif _frame == 12:
		var requested_stationary := float(_bridge.get("StationaryHistoryWeight"))
		var requested_moving := float(_bridge.get("MovingHistoryWeight"))
		var effective := float(_bridge.get("EffectiveHistoryWeight"))
		var applied := float(_bridge.get("AppliedHistoryWeight"))
		var apply_count := int(_bridge.get("TemporalHistoryApplyCount"))
		var driver := _scene.get_node("SunshineCloudsDriver")
		var clouds := driver.get("clouds_resource") as Resource
		var compositor_decay := float(clouds.get("accumulation_decay"))
		if (
			not is_equal_approx(requested_stationary, 0.42)
			or not is_equal_approx(requested_moving, 0.11)
			or apply_count <= _history_apply_count_before
			or not is_equal_approx(applied, effective)
			or not is_equal_approx(compositor_decay, applied)
		):
			push_error(
				"History sliders did not reach Sunshine immediately: requested=%s/%s effective=%s applied=%s compositor=%s count=%s->%s"
				% [requested_stationary, requested_moving, effective, applied, compositor_decay, _history_apply_count_before, apply_count])
			quit(1)
			return false
		var viewport_rect := root.get_visible_rect()
		var panel_rect := _menu_panel.get_global_rect() if _menu_panel != null else Rect2()
		if (
			_menu_panel == null
			or panel_rect.position.x < viewport_rect.position.x
			or panel_rect.position.x + panel_rect.size.x > viewport_rect.position.x + viewport_rect.size.x
			or (_menu_scroll != null and _menu_scroll.get_h_scroll_bar().visible)
		):
			push_error(
				"Live tuning drawer is horizontally clipped: viewport=%s panel=%s horizontalScroll=%s"
				% [viewport_rect, panel_rect, _menu_scroll.get_h_scroll_bar().visible if _menu_scroll != null else true])
			quit(1)
			return false
		if _menu_scroll != null and _catalog_button != null:
			_menu_scroll.ensure_control_visible(_catalog_button)
	elif _frame == 16:
		if _menu_scroll != null and _catalog_button != null:
			_menu_scroll.ensure_control_visible(_catalog_button)
	elif _frame == 18:
		if _catalog_button != null:
			var viewport_center := _catalog_button.get_global_rect().get_center()
			var physical_center := root.get_stretch_transform() * viewport_center
			Input.warp_mouse(physical_center)
			# `warp_mouse()` moves the Windows cursor, but some drivers do not emit a
			# corresponding GUI motion event. Inject the matching viewport-space event
			# so this regression also verifies Godot's Control hit routing.
			var motion := InputEventMouseMotion.new()
			motion.position = viewport_center
			motion.global_position = viewport_center
			Input.parse_input_event(motion)
	elif _frame == 22:
		if _catalog_button == null or not _catalog_button.is_hovered():
			push_error(
				"Sunshine live-menu pointer hit-test missed the visual button center: mouse=%s rect=%s"
				% [root.get_mouse_position(), _catalog_button.get_global_rect() if _catalog_button != null else Rect2()])
			quit(1)
			return false
	elif _frame == 26:
		var image := root.get_texture().get_image()
		var error := image.save_png(_capture_path)
		if error != OK:
			push_error("Sunshine live-menu capture failed: %s (%s)" % [_capture_path, error])
			quit(1)
			return false
		_call_bridge("ClearSpawnedWeather", "clear_spawned_weather")
		_menu.call("SetBenchmarkDrawerOpen", false)
		DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_DEFAULTS))
		print("Sunshine live-menu smoke test complete: %s" % _capture_path)
		quit(0)
	return false


func _call_bridge(pascal_name: StringName, snake_name: StringName, arguments: Array = []) -> void:
	var method_name := pascal_name if _bridge.has_method(pascal_name) else snake_name
	if not _bridge.has_method(method_name):
		push_error("Sunshine bridge smoke method is unavailable: %s / %s" % [pascal_name, snake_name])
		quit(1)
		return
	_bridge.callv(method_name, arguments)


func _find_button(node: Node, text: String) -> Button:
	if node is Button and (node as Button).text == text:
		return node as Button
	for child in node.get_children():
		var found := _find_button(child, text)
		if found != null:
			return found
	return null


func _find_catalog_options(node: Node) -> OptionButton:
	if node is OptionButton:
		var options := node as OptionButton
		if options.item_count == 10 and options.get_item_text(0).begins_with("Ci"):
			return options
	for child in node.get_children():
		var found := _find_catalog_options(child)
		if found != null:
			return found
	return null


func _find_tabs(node: Node) -> TabContainer:
	if node is TabContainer:
		return node as TabContainer
	for child in node.get_children():
		var found := _find_tabs(child)
		if found != null:
			return found
	return null


func _find_slider(node: Node, label_text: String) -> HSlider:
	for child_index in range(node.get_child_count()):
		var child := node.get_child(child_index)
		if child is Label and (child as Label).text == label_text and child_index + 1 < node.get_child_count():
			var row := node.get_child(child_index + 1)
			for row_child in row.get_children():
				if row_child is HSlider:
					return row_child as HSlider
	for child in node.get_children():
		var found := _find_slider(child, label_text)
		if found != null:
			return found
	return null
