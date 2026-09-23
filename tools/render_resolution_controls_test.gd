extends SceneTree
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const RENDER_PREFS := "res://.godot/render_resolution_controls_fixture.tres"
const AA_PREFS := "res://.godot/render_resolution_controls_aa_fixture.tres"
var _failed := false
var _finishing := false
var _watchdog: Timer
var _game: Node

func _initialize() -> void: call_deferred("_run")
func _run() -> void:
	_cleanup()
	if RenderingServer.get_current_rendering_method() != "forward_plus" or RenderingServer.get_rendering_device() == null:
		_fail("requires a Forward+ RenderingDevice; GPU controls were not exercised"); return
	_watchdog = Timer.new(); _watchdog.one_shot = true; _watchdog.process_mode = Node.PROCESS_MODE_ALWAYS
	_watchdog.timeout.connect(func(): _fail("watchdog expired")); root.add_child(_watchdog); _watchdog.start(150.0)
	root.get_viewport().notify_mouse_entered()
	var launcher: Node = LAUNCHER_SCENE.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed"); _game = GAME_SCENE.instantiate(); launcher.call("_configure_game", _game, 5000000.0); launcher.free()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	var saves := _game.find_child("WorldSaveManager", true, false)
	if saves != null: saves.free()
	var pause := _game.get_node_or_null("LodPauseMenu"); var drawer := _game.get_node_or_null("LiveTuningDrawer")
	_require(pause != null and drawer != null, "missing pause menu or tuning drawer before tree entry")
	if pause == null or drawer == null: return
	pause.call("InstallRenderResolutionDefaults", RENDER_PREFS); drawer.call("InstallAntiAliasingDefaults", AA_PREFS); root.add_child(_game)
	await _frames(12); await _exercise(_game, pause, drawer); _finish()

func _exercise(game: Node, pause: Node, drawer: Node) -> void:
	var monitor := game.get_node_or_null("CertificationMonitor"); _require(monitor != null, "CertificationMonitor missing")
	var diagnostics: Dictionary = pause.call("GetRenderResolutionDiagnostics")
	_require(bool(diagnostics.get("ready", false)) and int(diagnostics.get("preset", -1)) == 0 and is_equal_approx(float(diagnostics.get("requested_scale", 0.0)), 1.0), "default is not Native 100%")
	_require(not bool(diagnostics.get("adaptive_enabled", true)) and bool(diagnostics.get("memory_protection_enabled", false)), "default adaptive/memory protection mismatch")
	if monitor != null: _require(bool(monitor.get("EnableAdaptiveProtection")) and not bool(monitor.get("EnableAdaptiveRenderResolution")), "monitor default protection state mismatch")
	await _frames(121); diagnostics = pause.call("GetRenderResolutionDiagnostics")
	_require(is_equal_approx(float(diagnostics.get("current_scale", 0.0)), 1.0), "Native 100% drifted during renderer frames")
	pause.call("SetBenchmarkMenuOpen", true); await _frames(3); _require(paused, "pause menu did not pause")
	var option := pause.find_child("RenderResolutionPreset", true, false) as OptionButton
	_require(option != null, "pause render-resolution control missing"); if option == null: return
	_require(option.item_count == 6, "pause preset chooser does not contain six choices")
	var scroll := _nearest_scroll(option); _require(scroll != null, "pause preset is not inside a ScrollContainer")
	if scroll != null:
		scroll.scroll_vertical += roundi(option.global_position.y - scroll.global_position.y - 80.0)
	await _frames(2)
	await _capture("res://.godot/render_resolution_pause_chooser.png")
	var motion := InputEventMouseMotion.new(); motion.position = option.get_global_rect().get_center(); motion.global_position = motion.position; root.push_input(motion, true); await process_frame
	var hovered := root.get_viewport().gui_get_hovered_control(); _require(option.is_visible_in_tree() and (hovered == option or option.is_ancestor_of(hovered)), "preset did not receive valid hover before wheel")
	var before_wheel := option.selected; await _wheel(option); _require(option.selected == before_wheel, "mouse wheel changed closed preset")
	for preset in [2, 3, 4, 5, 1, 0]:
		var index := option.get_item_index(preset); _require(index >= 0, "preset id missing")
		if index < 0: continue
		option.select(index); option.item_selected.emit(index); await _frames(8)
		var expected: float = [1.0, 1.0, .85, .75, .67, .5][preset]; diagnostics = pause.call("GetRenderResolutionDiagnostics")
		_require(int(diagnostics.get("preset", -1)) == preset and is_equal_approx(root.scaling_3d_scale, expected), "preset scale mismatch: %d" % preset)
		_require(root.scaling_3d_mode == (Viewport.SCALING_3D_MODE_FSR2 if preset > 0 else Viewport.SCALING_3D_MODE_BILINEAR), "preset mode mismatch")
		if monitor != null: _require(is_equal_approx(float(monitor.get("MaximumRenderScale")), expected), "monitor ceiling mismatch")
		if preset > 0: _require(root.screen_space_aa == Viewport.SCREEN_SPACE_AA_DISABLED and root.msaa_3d == Viewport.MSAA_DISABLED and not root.use_taa, "FSR AA exclusivity failed")
		else: _require(root.screen_space_aa == Viewport.SCREEN_SPACE_AA_FXAA and root.msaa_3d == Viewport.MSAA_DISABLED and not root.use_taa, "Native did not restore FXAA")
	pause.call("SetBenchmarkMenuOpen", false); await process_frame; _require(not paused, "pause did not close before grave")
	drawer.call("SetBenchmarkDrawerOpen", true); await _frames(3)
	var tab_nodes := drawer.find_children("*", "TabContainer", true, false)
	var tabs := tab_nodes[0] as TabContainer if not tab_nodes.is_empty() else null
	_require(tabs != null, "grave tabs missing")
	if tabs != null:
		for tab_index in tabs.get_tab_count():
			if tabs.get_tab_title(tab_index) == "Anti-aliasing": tabs.current_tab = tab_index
	await _frames(2)
	var fold := drawer.find_child("SectionRenderresolution", true, false); _require(fold != null, "grave render-resolution fold missing")
	if fold != null: fold.set("folded", false)
	await _frames(2); await _capture("res://.godot/render_resolution_grave_expanded.png")
	var drawer_option := drawer.find_child("RenderResolutionPreset", true, false) as OptionButton
	var drawer_adaptive := drawer.find_child("AdaptiveRenderResolution", true, false) as CheckButton
	_require(drawer_option != null and drawer_adaptive != null, "grave controls missing")
	if drawer_option != null:
		_require(drawer_option.is_visible_in_tree() and not drawer_option.disabled, "grave preset hidden/disabled")
		var drawer_index := drawer_option.get_item_index(3); drawer_option.select(drawer_index); drawer_option.item_selected.emit(drawer_index); await _frames(8)
		_require(int((pause.call("GetRenderResolutionDiagnostics") as Dictionary).get("preset", -1)) == 3, "grave preset did not update pause")
	if drawer_adaptive != null:
		_require(drawer_adaptive.is_visible_in_tree() and not drawer_adaptive.disabled, "grave adaptive hidden/disabled")
		drawer_adaptive.button_pressed = true; await _frames(3); _require(bool((pause.call("GetRenderResolutionDiagnostics") as Dictionary).get("adaptive_enabled", false)), "grave adaptive did not update pause")
		if monitor != null: _require(bool(monitor.get("EnableAdaptiveProtection")), "automatic resolution disabled memory guard")
		drawer_adaptive.button_pressed = false; await _frames(2)
	drawer.call("SetBenchmarkDrawerOpen", false); await process_frame
	pause.call("SetBenchmarkMenuOpen", true); await _frames(2); _require(option.get_selected_id() == 3, "pause UI did not reflect grave")
	root.scaling_3d_scale = .5; pause.call("SetAdaptiveRenderResolution", false); await _frames(3); _require(is_equal_approx(root.scaling_3d_scale, .75), "adaptive disable did not restore requested scale")
	_require(bool(pause.call("FlushRenderResolutionSettings")), "preference flush failed")
	pause.call("InstallRenderResolutionDefaults", RENDER_PREFS); await _frames(3); diagnostics = pause.call("GetRenderResolutionDiagnostics")
	_require(int(diagnostics.get("preset", -1)) == 3 and not bool(diagnostics.get("adaptive_enabled", true)), "flushed preferences did not reload")
	pause.call("ResetRenderResolutionSettings"); await _frames(3); diagnostics = pause.call("GetRenderResolutionDiagnostics")
	_require(int(diagnostics.get("preset", -1)) == 0 and not bool(diagnostics.get("adaptive_enabled", true)) and bool(diagnostics.get("memory_protection_enabled", false)), "reset mismatch")

func _wheel(control: Control) -> void:
	var event := InputEventMouseButton.new(); event.button_index = MOUSE_BUTTON_WHEEL_DOWN; event.pressed = true; event.factor = 1.0; event.position = control.get_global_rect().get_center(); root.push_input(event, true); await process_frame
func _nearest_scroll(control: Control) -> ScrollContainer:
	var node: Node = control
	while node != null:
		if node is ScrollContainer: return node as ScrollContainer
		node = node.get_parent()
	return null
func _capture(path: String) -> void:
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	if image == null or image.save_png(path) != OK: _fail("failed screenshot capture: " + path)
func _frames(count: int) -> void:
	for _frame in count: await process_frame
func _require(condition: bool, message: String) -> void:
	if not condition: _fail(message)
func _fail(message: String) -> void:
	if _failed: return
	_failed = true; push_error("RENDER_RESOLUTION_CONTROLS_TEST_FAIL " + message); call_deferred("_finish")
func _finish() -> void:
	if _finishing: return
	_finishing = true
	if _game != null and is_instance_valid(_game): _game.free()
	_cleanup()
	if _watchdog != null and is_instance_valid(_watchdog): _watchdog.free()
	print("RENDER_RESOLUTION_CONTROLS_TEST=" + ("FAIL" if _failed else "PASS")); quit(1 if _failed else 0)
func _cleanup() -> void:
	for path in [RENDER_PREFS, AA_PREFS]:
		if FileAccess.file_exists(path): DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
