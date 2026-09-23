extends SceneTree

# Real-input regression coverage for the grave-key tuning drawers.  This is a
# SceneTree script deliberately: the controls must receive viewport input so
# the drawer's _Input wheel guard is exercised rather than bypassed.
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MENU_DRAWER := preload("res://scripts/ui/MenuFlyoverTuningDrawer.cs")
const FIXTURE := "res://.godot/grave_menu_interaction_fixture.tres"
const WATER_FIXTURE := "res://.godot/grave_menu_interaction_water.tres"
const GRASS_FIXTURE := "res://.godot/grave_menu_interaction_grass.tres"
const AA_FIXTURE := "res://.godot/grave_menu_interaction_aa.tres"
const SHADOW_FIXTURE := "res://.godot/grave_menu_interaction_shadow.tres"

var _failed := false
var _watchdog: Timer

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_watchdog = Timer.new()
	_watchdog.one_shot = true
	_watchdog.timeout.connect(func(): _fail("watchdog expired"))
	root.add_child(_watchdog)
	_watchdog.start(90.0)
	root.get_viewport().notify_mouse_entered()
	_cleanup_fixture()
	await _test_menu_drawer()
	await _test_gameplay_drawer()
	_cleanup_fixture()
	_watchdog.stop()
	print("GRAVE_MENU_INTERACTION=%s" % ("FAIL" if _failed else "PASS"))
	quit(1 if _failed else 0)

func _test_menu_drawer() -> void:
	var host := Control.new()
	host.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.add_child(host)
	var drawer: Control = MENU_DRAWER.new()
	drawer.name = "MenuFlyoverTuningDrawerInteractionFixture"
	drawer.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	drawer.set("SavePathOverride", FIXTURE)
	host.add_child(drawer)
	await _frames(3)
	drawer.call("SetDrawerOpen", true)
	await _frames(2)
	await _exercise_drawer(drawer, "menu")
	var quality := _tab_named(drawer, "Quality")
	_require(quality != null, "menu Quality tab missing")
	if quality != null:
		_select_tab(drawer, quality)
		await _wheel_guard(drawer, quality, "menu quality")
	# Menu numeric edits intentionally target a disposable preset, never the
	# production menu defaults.  A real SpinBox arrow click verifies normal edit.
	var camera := _tab_named(drawer, "Camera")
	if camera != null:
		_select_tab(drawer, camera)
		await _frames(1)
	var spin := drawer.find_child("AltitudeKm", true, false) as SpinBox
	if spin != null:
		_ensure_visible(camera, spin)
		await _frames(2)
		var before := spin.value
		await _click(spin.get_global_rect().position + Vector2(spin.size.x - 5.0, spin.size.y * .25))
		_require(not is_equal_approx(spin.value, before), "menu SpinBox arrow click did not edit")
		spin.get_line_edit().grab_focus()
		spin.get_line_edit().select_all()
		_type_text("22")
		_send_key(KEY_ENTER)
		await _frames(2)
		_require(is_equal_approx(spin.value, 22.0), "menu SpinBox typed edit did not apply")
	else: _fail("menu has no visible SpinBox")
	_require(int((drawer.call("GetDrawerDiagnostics") as Dictionary).get("control_count", -1)) == 50, "menu control count changed")
	print("GRAVE_MENU_INTERACTION_MENU=PASS")
	drawer.queue_free()
	host.queue_free()
	await _frames(2)

func _test_gameplay_drawer() -> void:
	# Use the certified configuration path, matching the supported large-ring
	# launch rather than a synthetic drawer host.
	var launcher: Node = LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var game: Node = GAME_SCENE.instantiate()
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	# These hooks are intentionally called before _Ready constructs the drawer.
	# They keep any accidental persistence caused by a failed wheel guard inside
	# test-only resources.
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	var saves := game.find_child("WorldSaveManager", true, false)
	if saves != null: saves.free()
	var pre_ready_drawer := game.get_node_or_null("LiveTuningDrawer")
	_require(pre_ready_drawer != null, "missing gameplay drawer before preference isolation")
	if pre_ready_drawer == null: return
	if pre_ready_drawer != null:
		pre_ready_drawer.call("InstallWaterDefaults", WATER_FIXTURE)
		pre_ready_drawer.call("InstallGrassDefaults", GRASS_FIXTURE)
		pre_ready_drawer.call("InstallAntiAliasingDefaults", AA_FIXTURE)
		pre_ready_drawer.call("InstallShadowDefaults", SHADOW_FIXTURE)
	root.add_child(game)
	await _frames(8)
	var drawer := game.get_node_or_null("LiveTuningDrawer") as CanvasLayer
	_require(drawer != null, "certified gameplay scene lacks LiveTuningDrawer")
	if drawer != null:
		drawer.call("SetBenchmarkDrawerOpen", true)
		await _frames(3)
		await _exercise_drawer(drawer, "gameplay")
		var water := _tab_named(drawer, "Water")
		_require(water != null, "gameplay Water tab missing")
		if water != null:
			_select_tab(drawer, water)
			await _wheel_guard(drawer, water, "gameplay water")
		var lighting := _tab_named(drawer, "Lighting")
		var slider: HSlider = null
		if lighting != null:
			_select_tab(drawer, lighting)
			await _frames(1)
			slider = _first_visible(lighting, "HSlider") as HSlider
		if slider != null:
			_ensure_visible(lighting, slider)
			await _frames(2)
			var before := slider.value
			var rect := slider.get_global_rect()
			await _click(rect.position + Vector2(rect.size.x * .75, rect.size.y * .5))
			_require(not is_equal_approx(slider.value, before), "gameplay slider click did not edit")
			var dragged_before := slider.value
			await _drag(slider.get_global_rect().position + Vector2(slider.size.x * .7, slider.size.y * .5), slider.get_global_rect().position + Vector2(slider.size.x * .3, slider.size.y * .5))
			_require(not is_equal_approx(slider.value, dragged_before), "gameplay slider drag did not edit")
		else: _fail("gameplay drawer has no visible HSlider")
		print("GRAVE_MENU_INTERACTION_GAMEPLAY=PASS")
	game.queue_free()
	await _frames(3)

func _exercise_drawer(drawer: Node, label: String) -> void:
	var panel := _find_panel(drawer)
	_require(panel != null and panel.visible, label + " drawer panel is not visible")
	if panel == null: return
	var tabs := _first_descendant(panel, TabContainer) as TabContainer
	_require(tabs != null, label + " drawer has no TabContainer")
	if tabs == null: return
	_require(tabs.get_tab_count() == (6 if label == "menu" else 10), label + " tab count changed")
	await _capture(label + "_collapsed")
	for index in tabs.get_tab_count():
		tabs.current_tab = index
		await _frames(2)
		var folds := _foldables(tabs.get_child(index))
		_require(folds.size() > 0, "%s tab %s lacks FoldableContainer" % [label, tabs.get_tab_title(index)])
		if folds.is_empty(): continue
		for fold in folds:
			_require(bool(fold.get("folded")), "%s section %s is not folded by default" % [label, fold.get("title")])
			_require(_has_section_controls(fold), "%s section %s has no controls" % [label, fold.get("title")])
		_require_settings_grouped(tabs.get_child(index), label)
		print("GRAVE_MENU_TAB %s/%s sections=%d" % [label, tabs.get_tab_title(index), folds.size()])
		var first: Control = folds[0]
		_ensure_visible(tabs.get_child(index), first)
		await _frames(2)
		await _click(first.get_global_rect().position + Vector2(12, minf(12, first.size.y * .5)))
		await _frames(1)
		_require(not bool(first.get("folded")), label + " section header did not expand")
		_require(_section_body_visible(first), label + " expanded section body is hidden")
		if folds.size() > 1:
			var second: Control = folds[1]
			_ensure_visible(tabs.get_child(index), second)
			await _frames(2)
			await _click(second.get_global_rect().position + Vector2(12, minf(12, second.size.y * .5)))
			await _frames(1)
			_require(not bool(second.get("folded")) and not bool(first.get("folded")), label + " sections are not independently expanded")
		_ensure_visible(tabs.get_child(index), first)
		await _frames(2)
		await _click(first.get_global_rect().position + Vector2(12, minf(12, first.size.y * .5)))
		await _frames(1)
		_require(bool(first.get("folded")) and not _section_body_visible(first), label + " section did not collapse/hide content")
		_ensure_visible(tabs.get_child(index), first)
		await _frames(2)
		await _click(first.get_global_rect().position + Vector2(12, minf(12, first.size.y * .5)))
		await _frames(1)
		_require(not bool(first.get("folded")) and _section_body_visible(first), label + " section did not restore content")
		if tabs.get_tab_title(index) == "Water" or (label == "menu" and index == 0):
			await _capture(label + "_expanded")
	# Fold state belongs to live controls and must not be reset merely by closing.
	if drawer.has_method("SetDrawerOpen"):
		drawer.call("SetDrawerOpen", false)
	else:
		drawer.call("SetBenchmarkDrawerOpen", false)
	await _frames(1)
	if drawer.has_method("SetDrawerOpen"):
		drawer.call("SetDrawerOpen", true)
	else:
		drawer.call("SetBenchmarkDrawerOpen", true)
	await _frames(2)
	var persisted := _foldables(tabs.get_child(tabs.current_tab))
	if not persisted.is_empty(): _require(not bool(persisted[0].get("folded")), label + " fold state reset after reopen")

func _wheel_guard(drawer: Node, tab: Node, label: String) -> void:
	for fold in _foldables(tab): fold.set("folded", false)
	await _frames(2)
	var slider := _first_visible(tab, "HSlider") as HSlider
	var spin := _first_visible(tab, "SpinBox") as SpinBox
	_require(slider != null or spin != null, label + " lacks wheel-test numeric controls")
	if slider != null: await _wheel_numeric(tab, slider, label + " HSlider", false)
	if spin != null: await _wheel_numeric(tab, spin, label + " SpinBox", true)

func _wheel_numeric(tab: Node, numeric: Control, label: String, focus_line_edit: bool) -> void:
	var scroll := _closest_scroll(numeric)
	_require(scroll != null, label + " has no owning ScrollContainer")
	if scroll == null: return
	_ensure_visible(tab, numeric)
	await _frames(2)
	var max_scroll := maxf(0.0, scroll.get_v_scroll_bar().max_value - scroll.get_v_scroll_bar().page)
	_require(max_scroll > 0.0, label + " does not overflow for wheel test")
	if max_scroll <= 0.0: return
	var before := (numeric as Range).value
	var signals := [0]
	(numeric as Range).value_changed.connect(func(_value): signals[0] += 1)
	var start_scroll := scroll.scroll_vertical
	var target := (numeric as SpinBox).get_line_edit() as Control if focus_line_edit else numeric
	if focus_line_edit: (numeric as SpinBox).get_line_edit().grab_focus()
	await _wheel_over(target.get_global_rect().get_center(), MOUSE_BUTTON_WHEEL_DOWN, 1.0, target)
	_require(is_equal_approx((numeric as Range).value, before) and signals[0] == 0 and scroll.scroll_vertical > start_scroll, label + " normal wheel was not guarded")
	var numerics := _visible_numeric_controls(tab)
	scroll.scroll_vertical = 0
	_ensure_visible(tab, numerics[0])
	await _frames(2)
	var first: Control = numerics[0]
	var first_target := (first as SpinBox).get_line_edit() as Control if first is SpinBox else first
	var first_value := (first as Range).value
	var first_signals := [0]
	(first as Range).value_changed.connect(func(_value): first_signals[0] += 1)
	await _wheel_over(first_target.get_global_rect().get_center(), MOUSE_BUTTON_WHEEL_UP, 0.5, first_target)
	_require(scroll.scroll_vertical == 0 and is_equal_approx((first as Range).value, first_value) and first_signals[0] == 0, label + " minimum wheel boundary changed state")
	scroll.scroll_vertical = int(max_scroll)
	var last: Control = numerics[numerics.size() - 1]
	await _frames(2)
	var boundary_target := (last as SpinBox).get_line_edit() as Control if last is SpinBox else last
	var boundary_value := (last as Range).value
	var boundary_signals := [0]
	(last as Range).value_changed.connect(func(_value): boundary_signals[0] += 1)
	await _wheel_over(boundary_target.get_global_rect().get_center(), MOUSE_BUTTON_WHEEL_DOWN, 0.5, boundary_target)
	_require(scroll.scroll_vertical == int(max_scroll) and is_equal_approx((last as Range).value, boundary_value) and boundary_signals[0] == 0, label + " maximum wheel boundary changed state")

func _wheel_over(position: Vector2, button: MouseButton, factor: float, expected: Control) -> void:
	var move := InputEventMouseMotion.new(); move.position = position; root.get_viewport().push_input(move, true)
	await _frames(1)
	var hovered := root.get_viewport().gui_get_hovered_control()
	_require(hovered == expected or expected.is_ancestor_of(hovered), "wheel target did not become hovered")
	var wheel := InputEventMouseButton.new(); wheel.position = position; wheel.button_index = button; wheel.pressed = true; wheel.factor = factor
	root.get_viewport().push_input(wheel, true)
	await _frames(1)

func _click(position: Vector2) -> void:
	var move := InputEventMouseMotion.new(); move.position = position; root.get_viewport().push_input(move, true)
	var press := InputEventMouseButton.new(); press.position = position; press.button_index = MOUSE_BUTTON_LEFT; press.pressed = true; root.get_viewport().push_input(press, true)
	var release := InputEventMouseButton.new(); release.position = position; release.button_index = MOUSE_BUTTON_LEFT; release.pressed = false; root.get_viewport().push_input(release, true)
	await _frames(1)

func _send_key(key: Key) -> void:
	var event := InputEventKey.new(); event.keycode = key; event.pressed = true; root.get_viewport().push_input(event, true)

func _type_text(value: String) -> void:
	for character in value:
		var event := InputEventKey.new()
		event.unicode = character.unicode_at(0)
		event.pressed = true
		root.get_viewport().push_input(event, true)

func _drag(from: Vector2, to: Vector2) -> void:
	var press := InputEventMouseButton.new(); press.position = from; press.button_index = MOUSE_BUTTON_LEFT; press.pressed = true; root.get_viewport().push_input(press, true)
	var move := InputEventMouseMotion.new(); move.position = to; move.button_mask = MOUSE_BUTTON_MASK_LEFT; root.get_viewport().push_input(move, true)
	var release := InputEventMouseButton.new(); release.position = to; release.button_index = MOUSE_BUTTON_LEFT; release.pressed = false; root.get_viewport().push_input(release, true)
	await _frames(2)

func _find_panel(node: Node) -> Control:
	var panel := node.get_node_or_null("LiveTuningDrawer") as Control
	if panel != null: return panel
	return _first_descendant(node, PanelContainer) as Control

func _section_body_visible(section: Control) -> bool:
	for child in section.get_children():
		if child is Control: return (child as Control).visible
	return false

func _foldables(node: Node) -> Array[Control]:
	var result: Array[Control] = []
	for found in node.find_children("*", "FoldableContainer", true, false): result.append(found as Control)
	return result

func _first_visible(node: Node, type_name: String) -> Control:
	for found in node.find_children("*", type_name, true, false):
		if (found as Control).is_visible_in_tree(): return found as Control
	return null

func _visible_numeric_controls(node: Node) -> Array[Control]:
	var result: Array[Control] = []
	for name in ["HSlider", "SpinBox"]:
		for control in node.find_children("*", name, true, false):
			if (control as Control).is_visible_in_tree(): result.append(control as Control)
	result.sort_custom(func(a: Control, b: Control): return a.get_global_rect().position.y < b.get_global_rect().position.y)
	return result

func _closest_scroll(node: Node) -> ScrollContainer:
	var current: Node = node
	while current != null:
		if current is ScrollContainer: return current as ScrollContainer
		current = current.get_parent()
	return null

func _first_descendant(node: Node, type) -> Node:
	if is_instance_of(node, type): return node
	for child in node.get_children():
		var found := _first_descendant(child, type)
		if found != null: return found
	return null

func _tab_named(drawer: Node, title: String) -> Node:
	var tabs := _first_descendant(drawer, TabContainer) as TabContainer
	if tabs == null: return null
	for index in tabs.get_tab_count():
		if tabs.get_tab_title(index) == title: return tabs.get_child(index)
	return null

func _select_tab(drawer: Node, tab: Node) -> void:
	var tabs := _first_descendant(drawer, TabContainer) as TabContainer
	if tabs != null: tabs.current_tab = tab.get_index()

func _frames(count: int) -> void:
	for _frame in count: await process_frame

func _capture(label: String) -> void:
	if RenderingServer.get_rendering_device() == null: return
	await RenderingServer.frame_post_draw
	var path := "res://.godot/grave_menu_%s.png" % label
	var result := root.get_texture().get_image().save_png(ProjectSettings.globalize_path(path))
	_require(result == OK, "could not capture " + label)

func _require(condition: bool, reason: String) -> void:
	if not condition: _fail(reason)

func _fail(reason: String) -> void:
	if _failed: return
	_failed = true
	push_error("GRAVE_MENU_INTERACTION=FAIL " + reason)
	call_deferred("quit", 1)

func _ensure_visible(tab: Node, control: Control) -> void:
	var scroll := _first_descendant(tab, ScrollContainer) as ScrollContainer
	if scroll != null:
		if control is FoldableContainer:
			# An expanded body may be taller than the viewport. Aim at its header,
			# not its bottom edge, when testing expand/collapse clicks.
			scroll.scroll_vertical += roundi(control.global_position.y - scroll.global_position.y - 4.0)
		else:
			scroll.ensure_control_visible(control)

func _has_section_controls(section: Node) -> bool:
	for child in section.find_children("*", "Control", true, false):
		if child is BaseButton or child is Slider or child is SpinBox: return true
	return false

func _require_settings_grouped(tab: Node, label: String) -> void:
	for child in tab.find_children("*", "Control", true, false):
		if not (child is Slider or child is SpinBox or child is OptionButton or child is CheckButton or child is CheckBox or child is ColorPickerButton): continue
		var ancestor: Node = child.get_parent()
		while ancestor != null and ancestor != tab and not ancestor is FoldableContainer:
			ancestor = ancestor.get_parent()
		_require(ancestor is FoldableContainer, "%s setting %s is outside a collapsible section" % [label, child.name])

func _cleanup_fixture() -> void:
	for path in [FIXTURE, WATER_FIXTURE, GRASS_FIXTURE, AA_FIXTURE, SHADOW_FIXTURE]:
		if FileAccess.file_exists(path): DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
