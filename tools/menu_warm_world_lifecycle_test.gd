extends SceneTree
## Phase C (C1+C2) lifecycle test: the last-played Standard world warm-loads behind the start menu flyover.
##
## ALWAYS run through tools/menu_warm_world_lifecycle_test.ps1, which builds an ISOLATED APPDATA holding COPIES of two saved slots.
## The script refuses to run when user:// resolves into the real roaming profile or is not a phase_c_live profile.
##
## Checks: warm at boot (StartMenu stays current, no loading overlay), crossfade then flyover retired, root 3D enabled, world HUD hidden,
## grave opens only the menu drawer, W/A/S/D do not move the Explorer, mouse Visible, the state files are unchanged after > 35 s,
## clean teardown (world freed, active_world_directory cleared) and a different slot then loading through the loading screen.
## Writes <out>/results.json (time to warm, frame statistics while the menu was up) and, on a GPU run, live_menu.png.

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/MenuAltitudePerfProbe.cs")
const WARM_TIMEOUT_MS := 300000
const REVEAL_TIMEOUT_MS := 120000
const LOAD_TIMEOUT_MS := 300000
const AUTOSAVE_OBSERVATION_SECONDS := 36.0
const OUT_DIR := "res://.godot/phase_c_live"
const WATCHDOG_USEC := 1200 * 1000000

var _errors := PackedStringArray()
var _checks := 0
var _results := {}
var _menu: Control
var _warm: Node
var _loader: Node
var _session: Node
var _frame_max_ms := 0.0
var _last_usec := 0
var _track_frames := false
var _slot_dir := ""
var _watched := {}
var _headless := false
var _torn_world_id := 0
var _teardown_checked := false
var _teardown_frame_max_ms := 0.0
var _track_teardown := false
var _loaded_world: Node
var _live_slot := 0

func _initialize() -> void:
	# Never write the menu tuning preset or touch the real profile; the isolated APPDATA guard in _run backs this up.
	call_deferred("_run")

func _process(_delta: float) -> bool:
	var now := Time.get_ticks_usec()
	if now > WATCHDOG_USEC:
		push_error("watchdog: the warm world lifecycle test exceeded its time budget")
		quit(3)
		return true
	if _last_usec > 0:
		var milliseconds := float(now - _last_usec) / 1000.0
		if _track_frames:
			_frame_max_ms = maxf(_frame_max_ms, milliseconds)
		if _track_teardown:
			_teardown_frame_max_ms = maxf(_teardown_frame_max_ms, milliseconds)
	_last_usec = now
	return false

func _expect(condition: bool, message: String) -> bool:
	_checks += 1
	if not condition:
		_errors.append(message)
		print("FAIL: ", message)
	return condition

func _fingerprint(path: String) -> String:
	if not FileAccess.file_exists(path):
		return "missing"
	return "%s:%d" % [FileAccess.get_md5(path), FileAccess.get_modified_time(path)]

func _snapshot_state() -> Dictionary:
	var snapshot := {}
	for name: String in ["world_state_surface_runtime_v1.json", "surface_runtime_edits_v1.json"]:
		snapshot[name] = _fingerprint(_slot_dir.path_join(name))
	return snapshot

func _wait_until(condition: Callable, timeout_ms: int) -> bool:
	var deadline := Time.get_ticks_msec() + timeout_ms
	while Time.get_ticks_msec() < deadline:
		if condition.call():
			return true
		await process_frame
	return condition.call()

func _wait_seconds(seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame

func _key(code: int, down: bool) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.physical_keycode = code
	event.pressed = down
	Input.parse_input_event(event)

func _finish() -> void:
	_results["checks"] = _checks
	_results["errors"] = Array(_errors)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	var file := FileAccess.open(ProjectSettings.globalize_path(OUT_DIR).path_join("results.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(_results, "  "))
		file.close()
	if _errors.is_empty():
		print("MENU_WARM_WORLD_LIFECYCLE=PASS checks=%d" % _checks)
		quit(0)
	else:
		for error in _errors:
			push_error(error)
		print("MENU_WARM_WORLD_LIFECYCLE=FAIL checks=%d" % _checks)
		quit(1)

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("phase_c_live"):
		push_error("Refusing to run: user:// (%s) is not an isolated phase_c_live APPDATA. Use menu_warm_world_lifecycle_test.ps1." % user_dir)
		quit(2)
		return
	_headless = DisplayServer.get_name() == "headless"
	_results["adapter"] = RenderingServer.get_video_adapter_name()
	_results["headless"] = _headless
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	_session = root.get_node_or_null("WorldSession")
	if not _expect(_session != null, "WorldSession autoload missing"):
		return _finish()
	# A fresh profile has slot 1 as the most recently played world; a reused one may have slot 2 after an earlier run.
	var live_slot := int(_session.get_live_menu_slot())
	_expect(live_slot in [1, 2], "no eligible live menu slot (got %d)" % live_slot)
	var other_slot := 2 if live_slot == 1 else 1
	_live_slot = live_slot
	_slot_dir = ProjectSettings.globalize_path(_session.call("_slot_directory", live_slot))
	var before := _snapshot_state()
	_expect(before["world_state_surface_runtime_v1.json"] != "missing", "isolated slot 1 has no saved world state")
	_results["state_before"] = before

	# --- cancel mid-load: tearing down while the background load is still in flight leaves nothing behind
	var early: Node = load("res://scripts/Loading/ring_warm_world.gd").ensure(root)
	_expect(early.start(live_slot), "warm owner refused to start")
	_expect(int(early.state) == 1 and bool(_session.warm_world_active), "owner is not LOADING after start")
	await _wait_seconds(7.0)
	var cancel_started := Time.get_ticks_msec()
	await early.teardown()
	_results["cancel_mid_load_ms"] = Time.get_ticks_msec() - cancel_started
	_expect(int(early.state) == 0 and not bool(_session.warm_world_active) and _session.active_slot == 0, "mid-load teardown left the owner or session bound")
	_expect(str(ProjectSettings.get_setting("ringworld/runtime/active_world_directory", "")) == "", "mid-load teardown left active_world_directory set")
	_expect(root.get_node_or_null("RingWorldLoading") == null or not bool(root.get_node("RingWorldLoading").get("active")), "loader still active after the mid-load teardown")
	for child in root.get_children():
		var early_name := str(child.name)
		_expect(not (early_name.contains("VoxelRing") or early_name.contains("WorldSizeLauncher")), "world runtime node remains after the mid-load teardown: " + early_name)
	_expect(_snapshot_state() == before, "mid-load teardown wrote world state")

	# --- boot: the menu warms the world itself
	var boot_msec := Time.get_ticks_msec()
	_menu = MENU.instantiate()
	_menu.set("use_saved_menu_defaults", false)
	_menu.set("menu_defaults_path", "res://.godot/menu_warm_world_tuning_fixture.tres")
	root.add_child(_menu)
	current_scene = _menu
	_warm = root.get_node_or_null("RingWarmWorld")
	if not _expect(_warm != null, "menu did not create RingWarmWorld"):
		return _finish()
	_loader = root.get_node_or_null("RingWorldLoading")
	_track_frames = true
	var overlay_seen := false
	var load_shot_taken := false
	var mouse_captured_frames := 0
	var mouse_captured_worst := 0
	var deadline := Time.get_ticks_msec() + WARM_TIMEOUT_MS
	while int(_warm.state) != 2 and Time.get_ticks_msec() < deadline and int(_warm.state) != 0:
		if _loader == null:
			_loader = root.get_node_or_null("RingWorldLoading")
		if _loader != null and _loader.get_node_or_null("WorldLoadingOverlay") != null:
			overlay_seen = true
		# The world's _Ready may capture the pointer for the remainder of one frame; the owner restores it on its next _process.
		mouse_captured_frames = mouse_captured_frames + 1 if Input.mouse_mode != Input.MOUSE_MODE_VISIBLE else 0
		mouse_captured_worst = maxi(mouse_captured_worst, mouse_captured_frames)
		if current_scene != _menu:
			_errors.append("current scene changed while warm-loading")
			break
		if not load_shot_taken and Time.get_ticks_msec() - boot_msec > 14000:
			load_shot_taken = true
			_results["luma_during_load"] = await _grab("0_flyover_during_load")
		await process_frame
	_expect(int(_warm.state) == 2, "warm world did not become ready (state %d)" % int(_warm.state))
	if int(_warm.state) != 2:
		return _finish()
	var time_to_warm_ms := Time.get_ticks_msec() - boot_msec
	_expect(not overlay_seen, "a loading overlay was created for the background load")
	_expect(mouse_captured_worst <= 2, "mouse stayed captured for %d consecutive frames during the warm load" % mouse_captured_worst)
	_expect(current_scene == _menu, "StartMenu is not the current scene")
	_expect(_session.active_slot == _live_slot and bool(_session.warm_world_active), "warm world did not bind the live slot")
	_expect(str(ProjectSettings.get_setting("ringworld/runtime/active_world_directory", "")).ends_with("slot_%d" % _live_slot), "active_world_directory is not the live slot while warm")
	var world: Node = _warm.world
	var bridge: Node = _warm.bridge
	_expect(is_instance_valid(world) and is_instance_valid(bridge), "warm world or bridge missing")
	_expect(bool(bridge.get("CruisePlaced")), "explorer was not placed at the cruise viewpoint")
	print("MENU_WARM time_to_warm_ms=", time_to_warm_ms)
	_results["time_to_warm_ms"] = time_to_warm_ms
	var warm_msec := Time.get_ticks_msec()

	# --- C2: reveal behind the opaque flyover, crossfade, retire
	var luma_flyover := await _grab("1_flyover_while_world_reveals")
	_expect(await _wait_until(func() -> bool: return bool(_warm.reveal_complete), REVEAL_TIMEOUT_MS), "world did not become quiet behind the flyover")
	await _wait_seconds(1.1)
	var luma_mid := await _grab("2_mid_crossfade")
	_expect(not root.disable_3d, "root disable_3d still true after the reveal")
	var retired := await _wait_until(func() -> bool: return bool(_menu.get("_flyover_retired")), 30000)
	_expect(retired, "flyover backdrop was not retired after the crossfade")
	_expect(bool(_menu.space_backdrop.get("IsRetired")), "backdrop reports it is not retired")
	_expect(not root.disable_3d, "root disable_3d true after retire")
	await _wait_seconds(0.5)
	var luma_live := await _grab("3_live_world")
	_results["luma"] = {"flyover": luma_flyover, "mid_crossfade": luma_mid, "live": luma_live}
	if not _headless:
		_expect(luma_flyover > 0.08 and luma_mid > 0.08 and luma_live > 0.08, "a presentation frame is black or empty: %s" % str(_results["luma"]))
	_expect(int(_warm.get_visible_hud_layers()) == 0, "world HUD layers are still visible")
	var visible_layers: Array = []
	for layer in world.find_children("*", "CanvasLayer", false, false):
		if not (layer as CanvasLayer).visible:
			continue
		# A visible layer only matters when it actually has something drawn (the pause menu layer keeps its root hidden).
		for control in layer.find_children("*", "Control", true, false):
			if (control as Control).is_visible_in_tree():
				visible_layers.append(str(layer.name))
				break
	_results["visible_world_canvas_layers"] = visible_layers
	_expect(visible_layers.is_empty(), "world CanvasLayers visible over the menu: " + ", ".join(PackedStringArray(visible_layers)))
	_expect(Input.mouse_mode == Input.MOUSE_MODE_VISIBLE, "mouse is not Visible after the crossfade")
	var survey: String = _menu.get("_survey_label").text
	_expect(survey.begins_with("ALT "), "survey readout did not switch to Explorer telemetry: " + survey)
	_results["survey_after_retire"] = survey
	var telemetry: Dictionary = _warm.get_telemetry()
	_results["telemetry"] = telemetry
	_expect(bool(telemetry.get("valid", false)) and bool(telemetry.get("clocks_paused", false)), "clocks are not paused on the menu")
	_expect(absf(float(telemetry.get("altitude_m", 0.0)) - 16000.0) < 600.0, "explorer altitude is not about 16 km: %s" % str(telemetry.get("altitude_m")))
	_expect(_menu.get("_continue_slot") == _live_slot, "Continue is not bound to the warm slot")

	# --- grave opens only the menu drawer; W/A/S/D do not move the Explorer
	var explorer: Node = world.get_node("Explorer")
	_expect(explorer.process_mode == Node.PROCESS_MODE_DISABLED, "Explorer is not held")
	_expect(world.get_node("LiveTuningDrawer").process_mode == Node.PROCESS_MODE_DISABLED, "world LiveTuningDrawer is not held")
	var drawer: Control = _menu.get("_tuning_drawer")
	_key(KEY_QUOTELEFT, true)
	_key(KEY_QUOTELEFT, false)
	await _wait_seconds(0.5)
	_expect(bool(drawer.get("IsDrawerOpen")), "grave did not open the menu drawer")
	var world_drawer_visible := false
	for control in world.get_node("LiveTuningDrawer").find_children("*", "Control", true, false):
		if (control as Control).is_visible_in_tree():
			world_drawer_visible = true
	_expect(not world_drawer_visible, "grave opened the world's live tuning drawer")
	_key(KEY_QUOTELEFT, true)
	_key(KEY_QUOTELEFT, false)
	await _wait_seconds(0.5)
	_expect(not bool(drawer.get("IsDrawerOpen")), "grave did not close the menu drawer")
	var probe = PROBE.new()
	_expect(probe.Bind(world), "pose probe could not bind")
	var pose_before: Dictionary = probe.GetPose()
	# Space and Enter would activate the focused menu button (Continue), so they are not part of the movement keys.
	for code in [KEY_W, KEY_A, KEY_S, KEY_D, KEY_E, KEY_Q, KEY_SHIFT]:
		_key(code, true)
	await _wait_seconds(1.5)
	for code in [KEY_W, KEY_A, KEY_S, KEY_D, KEY_E, KEY_Q, KEY_SHIFT]:
		_key(code, false)
	await _wait_seconds(0.3)
	var pose_after: Dictionary = probe.GetPose()
	var moved := absf(float(pose_after.around) - float(pose_before.around)) + absf(float(pose_after.across) - float(pose_before.across)) + absf(float(pose_after.vertical) - float(pose_before.vertical))
	_results["pose_before"] = pose_before
	_results["pose_after_keys"] = pose_after
	_expect(moved < 0.5, "W/A/S/D/E/Q/Shift moved the Explorer by %.2f m" % moved)
	_expect(Input.mouse_mode == Input.MOUSE_MODE_VISIBLE, "mouse is not Visible after key input")

	# --- GPU screenshot of the live menu
	if not _headless:
		await _wait_seconds(1.0)
		await RenderingServer.frame_post_draw
		var image := root.get_texture().get_image()
		if image != null and not image.is_empty():
			var path := ProjectSettings.globalize_path(OUT_DIR).path_join("live_menu.png")
			_expect(image.save_png(path) == OK, "could not save the live menu screenshot")
			_results["screenshot"] = path
			print("MENU_WARM_SCREENSHOT=", path)

	# --- saves: nothing written after more than one autosave interval
	var elapsed := float(Time.get_ticks_msec() - warm_msec) / 1000.0
	if elapsed < AUTOSAVE_OBSERVATION_SECONDS:
		await _wait_seconds(AUTOSAVE_OBSERVATION_SECONDS - elapsed)
	var after := _snapshot_state()
	_results["state_after_observation"] = after
	_expect(after == before, "world state files changed while the world was held: %s vs %s" % [str(before), str(after)])
	_track_frames = false
	_results["warm_metrics"] = _warm.get_metrics()
	_results["loader_report"] = _loader.get_diagnostics() if _loader != null else {}
	_results["frame_max_ms_menu_up"] = _frame_max_ms
	print("MENU_WARM_METRICS=", JSON.stringify(_results["warm_metrics"]))

	# --- menu hook: loading another slot tears the warm world down first, then uses the loading screen
	_torn_world_id = world.get_instance_id()
	_warm.torn_down.connect(_check_teardown, CONNECT_ONE_SHOT)
	_track_teardown = true
	var started := Time.get_ticks_msec()
	var accepted: bool = await _menu._load_slot(other_slot)
	_track_teardown = false
	_results["teardown_and_load_start_ms"] = Time.get_ticks_msec() - started
	_results["teardown_frame_max_ms"] = _teardown_frame_max_ms
	_expect(accepted, "menu refused to load the other slot")
	_expect(_teardown_checked, "teardown notification was never observed")
	_expect(_teardown_frame_max_ms < 4000.0, "a frame during teardown took %.0f ms (proxy _ExitTree waits?)" % _teardown_frame_max_ms)
	_loader = root.get_node_or_null("RingWorldLoading")
	_expect(_loader != null and bool(_loader.get("active")) and not bool(_loader.get("is_background")), "the other slot did not start the foreground loading screen")
	var screen_seen: bool = _loader != null and _loader.get_node_or_null("WorldLoadingOverlay") != null
	_expect(screen_seen, "no loading overlay for the other slot load")
	if _loader != null:
		_loader.world_ready.connect(func(ready_world: Node) -> void: _loaded_world = ready_world)
		await _wait_until(func() -> bool: return _loaded_world != null, LOAD_TIMEOUT_MS)
	var loaded_world: Node = _loaded_world
	_expect(loaded_world != null, "the other slot did not finish loading through the loading screen")
	_expect(_session.active_slot == other_slot, "active slot is not the other slot after the load")
	_expect(current_scene == loaded_world, "the loaded world is not the current scene")
	_expect(not bool(_session.warm_world_active), "warm flag still set after the foreground load")
	_finish()

## Saves a root-viewport screenshot and returns its mean luminance (0 when headless, where nothing is drawn).
func _grab(label: String) -> float:
	if _headless:
		return 0.0
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image == null or image.is_empty():
		return 0.0
	image.save_png(ProjectSettings.globalize_path(OUT_DIR).path_join("shot_%s.png" % label))
	image.resize(64, 36)
	var total := 0.0
	for y in image.get_height():
		for x in image.get_width():
			var color := image.get_pixel(x, y)
			total += color.get_luminance()
	return total / float(image.get_width() * image.get_height())

func _check_teardown() -> void:
	_teardown_checked = true
	_expect(not is_instance_valid(instance_from_id(_torn_world_id)), "warm world node is still alive after teardown")
	_expect(str(ProjectSettings.get_setting("ringworld/runtime/active_world_directory", "")) == "", "active_world_directory not cleared by teardown")
	_expect(_session.active_slot == 0 and not bool(_session.warm_world_active), "session slot/flag not cleared by teardown")
	_expect(int(_warm.state) == 0, "warm owner is not idle after teardown")
	_expect(not root.disable_3d, "root disable_3d not restored by teardown")
	for child in root.get_children():
		var child_name := str(child.name)
		_expect(not (child_name.contains("VoxelRing") or child_name.contains("WorldSizeLauncher") or child_name == "RingWorldLoadingBridge"),
			"world runtime node remains after teardown: " + child_name)
	var owner_children := PackedStringArray()
	for child in _warm.get_children():
		owner_children.append("%s(%s queued=%s tree=%s script=%s)" % [child.name, child.get_class(), child.is_queued_for_deletion(), child.is_inside_tree(), child.get_script().resource_path if child.get_script() != null else "-"])
	_expect(owner_children.is_empty(), "warm owner still parents nodes after teardown: " + ", ".join(owner_children))
	var after := _snapshot_state()
	_expect(after == _results.get("state_before", {}), "teardown wrote world state: %s" % str(after))
