extends SceneTree
## Phase C (C1 + D1) lifecycle test: the last-played Standard world warm-loads behind the start menu's boot cover (snapshot or starfield).
##
## ALWAYS run through tools/menu_warm_world_lifecycle_test.ps1, which builds an ISOLATED APPDATA holding COPIES of two saved slots.
## The script refuses to run when user:// resolves into the real roaming profile or is not a phase_c_live profile.
##
## One process per phase, all phases sharing one isolated profile (the .ps1 -Sequence switch runs 1, 2, 3):
##   --phase=1  fresh profile: starfield cover, warm, capture at reveal (sidecar pose matches the bridge pose within 1 m / 0.1 deg, no UI
##              in the PNG), plus the full C1/C2 checks (HUD hidden, mouse Visible, WASD ignored, grave does nothing, state files
##              unchanged > 35 s, clean teardown, another slot through the loading screen)
##   --phase=2  second boot: snapshot cover, PlacementUsedSnapshot, settled cover vs first live frame, no recapture
##   --phase=4  (empty profile, the .ps1 builds it) no-slot boot shows the bundled showcase picture and loads nothing; a slot that fails to warm-load
##              dissolves to the picture; choosing the showcase creates a normal seed-1337 slot loaded through the loading screen
##   --phase=3  stale snapshot (SavedUnix edited in the isolated copy): snapshot still shown, placement at the actual save spot,
##              dissolve to live, recapture with the new state stamp
## Windowed GPU runs check the snapshot (headless has no pixels and runs phase 1 without the snapshot checks).
## Writes <out>/results_phase<N>.json (time to warm, frame statistics, luma diffs) and screenshots to res://.godot/phase_c_live/d1/.

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/MenuAltitudePerfProbe.cs")
const SNAPSHOT := preload("res://scripts/ui/menu_snapshot.gd")
const WARM_TIMEOUT_MS := 300000
const REVEAL_TIMEOUT_MS := 120000
const LOAD_TIMEOUT_MS := 300000
const AUTOSAVE_OBSERVATION_SECONDS := 36.0
const OUT_DIR := "res://.godot/phase_c_live/d1"
const WATCHDOG_USEC := 1500 * 1000000
const LUMA_DIFF_LIMIT := 0.06
const TOP_BAND_LIMIT := 0.02

var _errors := PackedStringArray()
var _checks := 0
var _results := {}
var _menu: Control
var _cover: Control
var _warm: Node
var _loader: Node
var _session: Node
var _frame_max_ms := 0.0
var _last_usec := 0
var _track_frames := false
var _slot_dir := ""
var _headless := false
var _torn_world_id := 0
var _teardown_checked := false
var _teardown_frame_max_ms := 0.0
var _track_teardown := false
var _loaded_world: Node
var _live_slot := 0
var _phase := 1
var _magenta_probe: ColorRect
var _slow_after_unlock: Array = []

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
	if _last_usec > 0 and _menu != null and is_instance_valid(_menu) and not _menu.get("menu_locked") and not (_menu.get("unlock_info") as Dictionary).is_empty():
		var gap := float(now - _last_usec) / 1000.0
		if gap > 100.0:
			_slow_after_unlock.append(snappedf(gap, 0.1))
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
	_results["phase"] = _phase
	_results["slow_frames_over_100ms_after_unlock"] = _slow_after_unlock
	print("MENU_SLOW_AFTER_UNLOCK=", _slow_after_unlock)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	var file := FileAccess.open(ProjectSettings.globalize_path(OUT_DIR).path_join("results_phase%d.json" % _phase), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(_results, "  "))
		file.close()
	SNAPSHOT.wait_pending()
	if _errors.is_empty():
		print("MENU_WARM_WORLD_LIFECYCLE=PASS phase=%d checks=%d" % [_phase, _checks])
		quit(0)
	else:
		for error in _errors:
			push_error(error)
		print("MENU_WARM_WORLD_LIFECYCLE=FAIL phase=%d checks=%d" % [_phase, _checks])
		quit(1)

func _arg(prefix: String, fallback: String) -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(prefix):
			return arg.substr(prefix.length())
	return fallback

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("phase_c_live"):
		push_error("Refusing to run: user:// (%s) is not an isolated phase_c_live APPDATA. Use menu_warm_world_lifecycle_test.ps1." % user_dir)
		quit(2)
		return
	_phase = int(_arg("--phase=", "1"))
	_headless = DisplayServer.get_name() == "headless"
	_results["adapter"] = RenderingServer.get_video_adapter_name()
	_results["headless"] = _headless
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	if _headless and not _phase in [1, 4]:
		print("MENU_WARM_WORLD_LIFECYCLE=SKIP phase=%d (headless has no pixels, so no snapshot is captured)" % _phase)
		quit(0)
		return
	_session = root.get_node_or_null("WorldSession")
	if not _expect(_session != null, "WorldSession autoload missing"):
		return _finish()
	if _phase == 4:
		await _run_showcase_phase()
		return
	if _phase != 1:
		# Phase 1 ends by loading the other slot, which stamps it as most recent; make the slot that holds the snapshot the live one again.
		for slot in range(1, 4):
			if FileAccess.file_exists(ProjectSettings.globalize_path(str(_session.get_menu_snapshot_paths(slot)["json"]))):
				_session.call("_touch_slot", slot)
	# A fresh profile has slot 1 as the most recently played world; a reused one may have slot 2 after an earlier run.
	var live_slot := int(_session.get_live_menu_slot())
	_expect(live_slot in [1, 2], "no eligible live menu slot (got %d)" % live_slot)
	var other_slot := 2 if live_slot == 1 else 1
	_live_slot = live_slot
	_slot_dir = ProjectSettings.globalize_path(_session.call("_slot_directory", live_slot))
	var paths: Dictionary = _session.get_menu_snapshot_paths(live_slot)
	var png_path := ProjectSettings.globalize_path(str(paths["png"]))
	var json_path := ProjectSettings.globalize_path(str(paths["json"]))
	if _phase == 1 and not _headless:
		# Phase 1 is the fresh boot: no snapshot may exist yet (the .ps1 builds a fresh profile for it).
		_expect(not FileAccess.file_exists(json_path), "phase 1 profile already has a menu snapshot")
	if _phase == 3:
		_edit_saved_unix(5)
	var before := _snapshot_state()
	_expect(before["world_state_surface_runtime_v1.json"] != "missing", "isolated live slot has no saved world state")
	_results["state_before"] = before
	var state_stamp_before: Dictionary = _session.read_world_state_stamp(_slot_dir)
	var started_unix := int(Time.get_unix_time_from_system())

	if _phase == 1:
		await _cancel_mid_load(live_slot, before)
	if _phase == 2:
		await _quit_while_locked(before)

	# --- boot: the menu warms the world itself
	var boot_msec := Time.get_ticks_msec()
	_menu = MENU.instantiate()
	root.add_child(_menu)
	current_scene = _menu
	_cover = _menu.boot_cover
	_check_locked_at_boot(_menu)
	# A magenta panel that exists only in the menu: it proves the menu draws, and must never appear in the captured snapshot.
	_magenta_probe = ColorRect.new()
	_magenta_probe.color = Color(1.0, 0.0, 1.0)
	_magenta_probe.position = Vector2(700, 300)
	_magenta_probe.size = Vector2(320, 220)
	_magenta_probe.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_menu.add_child(_magenta_probe)
	# The menu starts the live world deferred (it cannot add root nodes while root is still setting up the main scene).
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if not _expect(_warm != null, "menu did not create RingWarmWorld"):
		return _finish()
	_loader = root.get_node_or_null("RingWorldLoading")
	_check_boot_cover()
	_track_frames = true
	var overlay_seen := false
	var load_shot_taken := false
	var survey_during_load := ""
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
		if not load_shot_taken and Time.get_ticks_msec() - boot_msec > 4000:
			load_shot_taken = true
			survey_during_load = _menu.get("_survey_label").text
			_results["luma_boot_cover"] = await _grab("p%d_boot_cover" % _phase)
		await process_frame
	_expect(int(_warm.state) == 2, "warm world did not become ready (state %d)" % int(_warm.state))
	if int(_warm.state) != 2:
		return _finish()
	_expect(survey_during_load.begins_with("PREPARING"), "survey readout did not show PREPARING during the load: '%s'" % survey_during_load)
	var time_to_warm_ms := Time.get_ticks_msec() - boot_msec
	_expect(not overlay_seen, "a loading overlay was created for the background load")
	_expect(mouse_captured_worst <= 2, "mouse stayed captured for %d consecutive frames during the warm load" % mouse_captured_worst)
	_expect(current_scene == _menu, "StartMenu is not the current scene")
	_expect(_session.active_slot == _live_slot and bool(_session.warm_world_active), "warm world did not bind the live slot")
	_expect(str(ProjectSettings.get_setting("ringworld/runtime/active_world_directory", "")).ends_with("slot_%d" % _live_slot), "active_world_directory is not the live slot while warm")
	_expect(await _wait_until(func() -> bool: return not bool(_menu.get("menu_locked")), 40000), "menu stayed locked after the warm-load")
	var unlock_info: Dictionary = _menu.get("unlock_info")
	_results["unlock_info"] = unlock_info
	_expect(not _menu.continue_button.disabled and not _menu.worlds_button.disabled, "Continue / Enter Ringworld are still disabled after the unlock")
	for button in _menu.slot_buttons:
		_expect(not button.disabled, "a world card stayed disabled after the unlock")
	_expect(_menu.continue_button.has_focus(), "focus did not move to Continue on unlock")
	_expect(float(unlock_info.get("ms", 99999.0)) <= 16500.0, "menu unlocked after the safety cap: %s" % str(unlock_info))
	_expect(str(unlock_info.get("reason", "")) in ["quiet", "cap"], "unexpected unlock reason: %s" % str(unlock_info))
	var world: Node = _warm.world
	var bridge: Node = _warm.bridge
	_expect(is_instance_valid(world) and is_instance_valid(bridge), "warm world or bridge missing")
	_expect(bool(bridge.get("CruisePlaced")), "explorer was not placed at the cruise viewpoint")
	print("MENU_WARM time_to_warm_ms=", time_to_warm_ms)
	_results["time_to_warm_ms"] = time_to_warm_ms
	var warm_msec := Time.get_ticks_msec()
	var placed_pose: Dictionary = bridge.call("GetCameraPose")
	_results["placed_pose"] = placed_pose
	var used_snapshot := bool(bridge.get("PlacementUsedSnapshot"))
	_results["placement_used_snapshot"] = used_snapshot
	if _phase == 2:
		_expect(used_snapshot, "phase 2: placement did not use the snapshot pose")
		var sidecar_before: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(json_path))
		_expect(_pose_matches(placed_pose, sidecar_before.get("pose", {})), "phase 2: bridge pose differs from the sidecar pose")
	else:
		_expect(not used_snapshot, "phase %d: placement used a snapshot pose (none was valid and current)" % _phase)
	if _phase == 3:
		# The stale snapshot is placed at the actual save spot: cruise above it.
		var saved_around := float(state_stamp_before.get("around_m", 0.0))
		var saved_across := float(state_stamp_before.get("across_m", 0.0))
		_expect(absf(float(placed_pose.get("around_m", 0.0)) - saved_around) < 50.0 and absf(float(placed_pose.get("across_m", 0.0)) - saved_across) < 50.0, "phase 3: placement is not above the actual save spot")

	# --- reveal behind the opaque cover (capture when needed), settle, crossfade, retire
	_expect(await _wait_until(func() -> bool: return bool(_warm.reveal_complete), REVEAL_TIMEOUT_MS), "world did not become quiet behind the cover")
	if _phase == 2 and not _headless:
		await _check_settled_against_live(world)
	else:
		await _wait_seconds(0.3)
		_results["luma_cover_after_reveal"] = await _grab("p%d_cover_after_reveal" % _phase)
		await _wait_seconds(1.1)
		_results["luma_mid_crossfade"] = await _grab("p%d_mid_crossfade" % _phase)
		_expect(not root.disable_3d, "root disable_3d still true after the reveal")
		_expect(await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 30000), "boot cover was not retired after the crossfade")
	_expect(_cover.is_retired, "cover reports it is not retired")
	_expect(not root.disable_3d, "root disable_3d true after retire")
	_expect(_menu.get("_footer_label").text.begins_with("LIVE WORLD"), "footer is not LIVE WORLD after the reveal: " + _menu.get("_footer_label").text)
	await _wait_seconds(0.5)
	var luma_live := await _grab("p%d_live_world" % _phase)
	_results["luma_live"] = luma_live
	if not _headless:
		_expect(luma_live > 0.05, "the live frame is black or empty: %f" % luma_live)
		_expect(_count_magenta(await _live_image()) > 100, "the menu's magenta probe panel is not visible in the live screenshot")
	if not _headless:
		await _check_top_matches(png_path)
	if _phase != 2:
		await _check_snapshot_capture(json_path, png_path, bridge, started_unix, state_stamp_before)

	# --- HUD, mouse, survey, clocks
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

	# --- grave does nothing on the menu (no drawer until D4); W/A/S/D do not move the Explorer
	var explorer: Node = world.get_node("Explorer")
	_expect(explorer.process_mode == Node.PROCESS_MODE_DISABLED, "Explorer is not held")
	_expect(world.get_node("LiveTuningDrawer").process_mode == Node.PROCESS_MODE_DISABLED, "world LiveTuningDrawer is not held")
	_key(KEY_QUOTELEFT, true)
	_key(KEY_QUOTELEFT, false)
	await _wait_seconds(0.5)
	_expect(_menu.get_node_or_null("MenuTuningDrawer") == null, "grave created a menu drawer")
	var world_drawer_visible := false
	for control in world.get_node("LiveTuningDrawer").find_children("*", "Control", true, false):
		if (control as Control).is_visible_in_tree():
			world_drawer_visible = true
	_expect(not world_drawer_visible, "grave opened the world's live tuning drawer")
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

	if _phase != 1:
		# Phases 2 and 3: a clean teardown, then done (the long observation and the foreign-slot load run in phase 1).
		_track_frames = false
		_results["warm_metrics"] = _warm.get_metrics()
		_results["frame_max_ms_menu_up"] = _frame_max_ms
		_torn_world_id = world.get_instance_id()
		_warm.torn_down.connect(_check_teardown, CONNECT_ONE_SHOT)
		await _warm.teardown()
		_expect(_teardown_checked, "teardown notification was never observed")
		return _finish()

	# --- GPU screenshot of the live menu
	if not _headless:
		var image := await _live_image()
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

## At boot with a warm world the main buttons are disabled (Quit stays enabled).
func _check_locked_at_boot(menu: Control) -> void:
	_expect(bool(menu.get("menu_locked")), "menu is not locked at boot while a world warms")
	_expect(menu.continue_button.disabled and menu.worlds_button.disabled, "Continue / Enter Ringworld are enabled at boot")
	for button in menu.slot_buttons:
		_expect(button.disabled, "a world card is enabled at boot")
	_expect(not menu.get("_quit_button").disabled, "Quit is disabled while the menu is locked")

## Quit while the menu is still locked: the same teardown the Quit button runs must leave nothing behind.
func _quit_while_locked(before: Dictionary) -> void:
	var menu: Control = MENU.instantiate()
	root.add_child(menu)
	current_scene = menu
	await _wait_seconds(5.0)
	_expect(bool(menu.get("menu_locked")), "menu unlocked within 5 s of boot")
	await menu._release_warm_world()
	_expect(int(root.get_node("RingWarmWorld").state) == 0 and not bool(_session.warm_world_active), "Quit while locked did not tear the warm world down")
	_expect(_snapshot_state() == before, "Quit while locked wrote world state")
	menu.queue_free()
	await process_frame
	await process_frame

## Tearing down while the background load is still in flight leaves nothing behind.
func _cancel_mid_load(live_slot: int, before: Dictionary) -> void:
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

## The cover mode at boot: starfield for phase 1, the snapshot (stale in phase 3) afterwards.
func _check_boot_cover() -> void:
	if _phase == 1:
		_expect(_cover.mode == "starfield", "phase 1 boot cover is not the starfield: " + str(_cover.mode))
		_expect(_menu.get("_footer_label").text == "", "starfield footer is not empty")
		return
	_expect(_cover.mode == "snapshot", "phase %d boot cover is not the snapshot: %s" % [_phase, str(_cover.mode)])
	_expect(bool(_menu.get("_snapshot_stale")) == (_phase == 3), "phase %d: snapshot stale flag is %s" % [_phase, str(_menu.get("_snapshot_stale"))])
	_expect(_menu.get("_footer_label").text.begins_with("LAST SESSION"), "snapshot footer is not LAST SESSION: " + _menu.get("_footer_label").text)

## Phase 2: wait for the cover to finish settling (opaque, identity), compare it with the first frame after the cover is gone.
func _check_settled_against_live(world: Node) -> void:
	var settled := await _wait_until(func() -> bool: return bool(_cover.get("_settling")) and float(_cover.get("_settle_progress")) >= 0.999, 30000)
	_expect(settled, "the cover never finished settling")
	var picture: TextureRect = _cover.get_node_or_null("SnapshotPicture") as TextureRect
	if picture != null:
		_results["settled_scale"] = picture.scale.x
		_expect(absf(picture.scale.x - 1.0) < 0.01 and picture.position.length() < 2.0, "settled cover is not at identity: scale %s offset %s" % [str(picture.scale), str(picture.position)])
	var cover_image := await _live_image()
	if cover_image != null:
		cover_image.save_png(ProjectSettings.globalize_path(OUT_DIR).path_join("shot_p2_settled_cover.png"))
	_results["luma_cover_after_reveal"] = await _grab("p2_cover_settled")
	await _wait_seconds(1.2)
	_results["luma_mid_crossfade"] = await _grab("p2_mid_crossfade")
	_expect(await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 30000), "boot cover was not retired after the crossfade")
	await _wait_seconds(0.5)
	var live_image := await _live_image()
	_expect(world != null and cover_image != null and live_image != null, "could not grab the comparison frames")
	if cover_image != null and live_image != null:
		var diff := _mean_abs_luma_diff(cover_image, live_image)
		_results["settled_cover_vs_live_luma_diff"] = diff
		print("MENU_COVER_LUMA_DIFF=", diff)
		_expect(diff < LUMA_DIFF_LIMIT, "settled cover differs from the live frame: mean abs luma diff %.4f >= %.2f" % [diff, LUMA_DIFF_LIMIT])
	# The current snapshot was used: it must not have been rewritten.
	var sidecar_after: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(ProjectSettings.globalize_path(str(_session.get_menu_snapshot_paths(_live_slot)["json"]))))
	_expect(not SNAPSHOT.has_pending(), "a snapshot write is pending although the current snapshot was used")
	_results["sidecar_captured_unix_after"] = sidecar_after.get("captured_unix", 0)

## The top 35% of the frame (horizon, rim wall, atmosphere) must match between the snapshot and the live world-only view after the
## crossfade, or the dissolve pops there. The menu is hidden for three presented frames so the live image carries no UI.
func _check_top_matches(png_path: String) -> void:
	_expect(await _wait_until(func() -> bool: return not SNAPSHOT.has_pending(), 30000), "snapshot write did not finish before the top-band check")
	var snapshot := Image.load_from_file(png_path)
	if snapshot == null or snapshot.is_empty():
		_expect(false, "no snapshot PNG for the top-band check")
		return
	_menu.visible = false
	await process_frame
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var live := root.get_texture().get_image()
	_menu.visible = true
	live.save_png(ProjectSettings.globalize_path(OUT_DIR).path_join("shot_p%d_live_world_only.png" % _phase))
	var diff := _top_band_diff(snapshot, live)
	_results["top35_snapshot_vs_live_diff"] = diff
	print("MENU_TOP35_DIFF=", diff)
	_expect(diff < TOP_BAND_LIMIT, "top 35%% of the snapshot differs from the live view: mean abs luma diff %.4f >= %.3f" % [diff, TOP_BAND_LIMIT])
	var dark := _near_black_fraction(snapshot, 0.5)
	var dark_live := _near_black_fraction(live, 0.5)
	_results["near_black_top50_snapshot_live"] = [dark, dark_live]
	_expect(absf(dark - dark_live) < 0.01, "near-black horizon band differs: snapshot %.4f vs live %.4f" % [dark, dark_live])

func _top_band_diff(a: Image, b: Image) -> float:
	var x := a.duplicate() as Image
	var y := b.duplicate() as Image
	x.convert(Image.FORMAT_RGB8)
	y.convert(Image.FORMAT_RGB8)
	x.resize(128, 72, Image.INTERPOLATE_BILINEAR)
	y.resize(128, 72, Image.INTERPOLATE_BILINEAR)
	var rows := int(72 * 0.35)
	var total := 0.0
	for j in rows:
		for i in 128:
			total += absf(x.get_pixel(i, j).get_luminance() - y.get_pixel(i, j).get_luminance())
	return total / float(rows * 128)

## Fraction of pixels in the top `fraction` of the frame that are near black (a missing far layer or wall shows as a black band).
func _near_black_fraction(image: Image, fraction: float) -> float:
	var small := image.duplicate() as Image
	small.convert(Image.FORMAT_RGB8)
	small.resize(128, 72, Image.INTERPOLATE_BILINEAR)
	var rows := int(72 * fraction)
	var count := 0
	for j in rows:
		for i in 128:
			if small.get_pixel(i, j).get_luminance() < 0.02:
				count += 1
	return float(count) / float(rows * 128)

## Phase 1 and 3: the capture was written; its sidecar and PNG are consistent with the bridge and contain no menu UI.
func _check_snapshot_capture(json_path: String, png_path: String, bridge: Node, started_unix: int, stamp_before: Dictionary) -> void:
	if _headless:
		_expect(not FileAccess.file_exists(json_path), "headless run wrote a snapshot")
		return
	_expect(await _wait_until(func() -> bool: return not SNAPSHOT.has_pending(), 30000), "snapshot write did not finish")
	_expect(FileAccess.file_exists(png_path) and FileAccess.file_exists(json_path), "no snapshot PNG and sidecar after the reveal")
	if not FileAccess.file_exists(json_path):
		return
	var sidecar: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(json_path))
	_results["sidecar"] = sidecar
	_expect(int(sidecar.get("format_version", 0)) == SNAPSHOT.FORMAT_VERSION and int(sidecar.get("render_epoch", 0)) == SNAPSHOT.RENDER_EPOCH, "sidecar format or epoch mismatch")
	_expect(str(sidecar.get("kind", "")) == "last_session", "sidecar kind is not last_session")
	_expect(int(sidecar.get("captured_unix", 0)) >= started_unix, "sidecar captured_unix predates this run")
	var bridge_pose: Dictionary = bridge.call("GetCameraPose")
	_expect(_pose_matches(bridge_pose, sidecar.get("pose", {})), "sidecar pose differs from the bridge pose (>1 m or >0.1 deg): %s vs %s" % [str(sidecar.get("pose", {})), str(bridge_pose)])
	var stamp: Dictionary = sidecar.get("state_stamp", {})
	_expect(int(stamp.get("saved_unix", -1)) == int(stamp_before.get("saved_unix", -2)), "sidecar state stamp does not match the save at capture time")
	_expect(float(sidecar.get("last_warm_ms", 0.0)) > 1000.0, "sidecar last_warm_ms is missing")
	_expect(absf(float(sidecar.get("sun_elevation_deg", 999.0))) <= 90.0, "sidecar sun elevation is invalid")
	var image := Image.load_from_file(png_path)
	_expect(image != null and not image.is_empty() and image.get_height() <= SNAPSHOT.MAX_HEIGHT, "snapshot PNG missing or taller than 1080 px")
	if image != null and not image.is_empty():
		var size: Array = sidecar.get("image_size", [0, 0])
		_expect(int(size[0]) == image.get_width() and int(size[1]) == image.get_height(), "sidecar image_size does not match the PNG")
		var magenta := _count_magenta(image)
		_results["snapshot_magenta_pixels"] = magenta
		_expect(magenta == 0, "the snapshot contains menu UI (%d magenta probe pixels)" % magenta)
		_expect(_mean_luma(image) > 0.05, "the snapshot is black or empty")
		_expect(not FileAccess.file_exists(png_path + ".tmp") and not FileAccess.file_exists(json_path + ".tmp"), "temporary snapshot files left behind")
	if _phase == 3:
		_expect(int(stamp.get("saved_unix", -1)) == int(_session.read_world_state_stamp(_slot_dir).get("saved_unix", -2)), "phase 3: recapture does not carry the new state stamp")

## Phase 3: shifts SavedUnix in the isolated copy of the live slot's state file, as a window close after the capture would.
func _edit_saved_unix(delta: int) -> void:
	var path := _slot_dir.path_join("world_state_surface_runtime_v1.json")
	var text := FileAccess.get_file_as_string(path)
	var regex := RegEx.create_from_string("\"SavedUnix\"\\s*:\\s*(\\d+)")
	var found := regex.search(text)
	if not _expect(found != null, "state file has no SavedUnix"):
		return
	var edited := text.substr(0, found.get_start(1)) + str(int(found.get_string(1)) + delta) + text.substr(found.get_end(1))
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(edited)
	file.close()

func _pose_matches(a: Dictionary, b: Dictionary) -> bool:
	for key in ["around_m", "vertical_m", "across_m"]:
		if not a.has(key) or not b.has(key) or absf(float(a[key]) - float(b[key])) > 1.0:
			return false
	for key in ["yaw_deg", "pitch_deg"]:
		if not a.has(key) or not b.has(key) or absf(wrapf(float(a[key]) - float(b[key]), -180.0, 180.0)) > 0.1:
			return false
	return true

func _live_image() -> Image:
	if _headless:
		return null
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	return image if image != null and not image.is_empty() else null

func _count_magenta(image: Image) -> int:
	if image == null:
		return 0
	var count := 0
	for y in range(0, image.get_height(), 2):
		for x in range(0, image.get_width(), 2):
			var color := image.get_pixel(x, y)
			if color.r > 0.8 and color.b > 0.8 and color.g < 0.3:
				count += 1
	return count

func _small_luma(image: Image) -> PackedFloat32Array:
	var small := image.duplicate() as Image
	small.resize(64, 36, Image.INTERPOLATE_BILINEAR)
	var values := PackedFloat32Array()
	for y in small.get_height():
		for x in small.get_width():
			values.append(small.get_pixel(x, y).get_luminance())
	return values

func _mean_luma(image: Image) -> float:
	var values := _small_luma(image)
	var total := 0.0
	for value in values:
		total += value
	return total / maxf(1.0, float(values.size()))

func _mean_abs_luma_diff(a: Image, b: Image) -> float:
	var va := _small_luma(a)
	var vb := _small_luma(b)
	var total := 0.0
	for index in mini(va.size(), vb.size()):
		total += absf(va[index] - vb[index])
	return total / maxf(1.0, float(mini(va.size(), vb.size())))

## Saves a root-viewport screenshot and returns its mean luminance (0 when headless, where nothing is drawn).
func _grab(label: String) -> float:
	var image := await _live_image()
	if image == null:
		return 0.0
	image.save_png(ProjectSettings.globalize_path(OUT_DIR).path_join("shot_%s.png" % label))
	return _mean_luma(image)

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

## Phase 4 (D2, empty profile): no saved world means no warm-load; the menu shows the bundled showcase picture. A slot that fails to
## warm-load dissolves to the same picture. Choosing the showcase creates a normal slot (seed 1337) and loads it through the loading screen.
func _run_showcase_phase() -> void:
	var definition: Dictionary = _session.get_showcase_definition()
	var settings: Dictionary = definition.get("generation_settings", {})
	if not _expect(not definition.is_empty(), "WorldSession has no valid showcase definition"):
		return _finish()
	_expect(int(_session.get_most_recent_slot()) == 0 and int(_session.get_live_menu_slot()) == 0, "phase 4 profile is not empty")

	# A) no-slot boot: the picture, no world load
	var menu: Control = MENU.instantiate()
	root.add_child(menu)
	current_scene = menu
	_menu = menu
	await _wait_seconds(5.0)
	_check_showcase_cover(menu)
	_expect(not bool(menu.get("menu_locked")), "menu is locked although no world is loading")
	_expect(menu.showcase_button.visible and not menu.continue_button.visible, "no-slot main menu does not offer the showcase instead of Continue")
	_expect(menu.worlds_showcase_button.visible, "worlds screen has no showcase entry")
	var warm: Node = root.get_node_or_null("RingWarmWorld")
	_expect(warm == null or int(warm.state) == 0, "a warm world started for the no-slot boot")
	var loader: Node = root.get_node_or_null("RingWorldLoading")
	_expect(loader == null or not bool(loader.get("active")), "a world load started for the no-slot boot")
	_expect(not bool(_session.warm_world_active) and _session.active_slot == 0, "session is bound although nothing loads")
	for slot in range(1, 4):
		_expect(not _session.slot_exists(slot), "no-slot boot created slot %d" % slot)
	for child in root.get_children():
		var child_name := str(child.name)
		_expect(not (child_name.contains("VoxelRing") or child_name.contains("WorldSizeLauncher")), "world runtime node exists at the no-slot boot: " + child_name)
	menu.queue_free()
	await _wait_seconds(0.3)

	# B) a slot that cannot warm-load falls back to the picture, never to a live showcase
	_expect(_session.select_slot(1), "could not create the broken slot")
	var broken := settings.duplicate(true)
	broken["surface_generation_recipe_json"] = "{not a recipe"
	_expect(_session.save_active_world_settings(broken), "could not store the broken settings")
	_session.clear_preview_slot()
	_expect(int(_session.get_live_menu_slot()) == 1, "the broken Standard slot is not eligible for a warm-load")
	menu = MENU.instantiate()
	root.add_child(menu)
	current_scene = menu
	_menu = menu
	_expect(menu.boot_cover.mode == "starfield", "slot without a snapshot did not start on the starfield: " + str(menu.boot_cover.mode))
	_expect(await _wait_until(func() -> bool: return str((menu.get("unlock_info") as Dictionary).get("reason", "")) == "failed", 180000), "the broken slot did not report a failed warm-load")
	await _wait_seconds(3.5)
	_check_showcase_cover(menu)
	warm = root.get_node_or_null("RingWarmWorld")
	_expect(warm != null and int(warm.state) == 0 and not bool(_session.warm_world_active) and _session.active_slot == 0, "failed warm-load left the owner or session bound")
	_expect(str(ProjectSettings.get_setting("ringworld/runtime/active_world_directory", "")) == "", "failed warm-load left active_world_directory set")
	for child in root.get_children():
		var child_name := str(child.name)
		_expect(not (child_name.contains("VoxelRing") or child_name.contains("WorldSizeLauncher")), "world runtime node remains after the failed warm-load: " + child_name)
	menu.queue_free()
	await _wait_seconds(0.3)
	_expect(_session.delete_slot(1), "could not delete the broken slot")

	# C) session level: every pick makes a fresh slot and never touches an earlier one
	_expect(int(_session.get_first_empty_slot()) == 1, "first empty slot is not 1 after the cleanup")
	_expect(_session.create_showcase_world(1), "first showcase slot was not created")
	var first_meta: Dictionary = _session.get_slot_metadata(1)
	_expect(_session.get_first_empty_slot() == 2 and _session.create_showcase_world(2), "second pick did not create another slot")
	_expect(_session.get_slot_metadata(1) == first_meta, "second pick changed the first showcase slot")
	_expect(not _session.create_showcase_world(2), "create_showcase_world overwrote an existing slot")
	_expect(JSON.stringify(_session.get_slot_metadata(2).get("generation_settings", {}), "", true) == JSON.stringify(settings, "", true), "second slot's settings differ from the showcase definition")
	_session.clear_preview_slot()
	_expect(_session.delete_slot(2) and _session.delete_slot(1), "could not delete the session-level showcase slots")

	# D) the menu entry: a normal slot with seed 1337 loads through the loading screen
	menu = MENU.instantiate()
	root.add_child(menu)
	current_scene = menu
	_menu = menu
	await _wait_seconds(1.0)
	_loader = load("res://scripts/Loading/ring_world_loading.gd").ensure(root)
	_loader.world_ready.connect(func(ready_world: Node) -> void: _loaded_world = ready_world)
	var failure := ""
	_loader.loading_failed.connect(func(message: String) -> void: failure = message)
	await menu._explore_showcase()
	_expect(_session.slot_exists(1), "choosing the showcase did not create slot 1")
	var created: Dictionary = _session.get_slot_metadata(1)
	var created_settings: Dictionary = created.get("generation_settings", {})
	_expect(int(created_settings.get("terrain_seed", -1)) == 1337, "showcase slot seed is not 1337")
	_expect(JSON.stringify(created_settings, "", true) == JSON.stringify(settings, "", true), "showcase slot settings differ from the definition")
	_expect(str(created.get("name", "")) == str(definition.get("name", "?")), "showcase slot name differs from the definition")
	_expect(_session.active_slot == 1 and not bool(_session.warm_world_active), "showcase slot is not the bound active slot")
	_expect(bool(_loader.get("active")) and not bool(_loader.get("is_background")), "the showcase did not start the foreground loading screen")
	_expect(await _wait_until(func() -> bool: return _loader.get_node_or_null("WorldLoadingOverlay") != null, 20000), "no loading overlay for the showcase load")
	await _wait_until(func() -> bool: return _loaded_world != null or not failure.is_empty(), LOAD_TIMEOUT_MS)
	_expect(_loaded_world != null, "the showcase did not finish loading: " + failure)
	_expect(current_scene == _loaded_world, "the loaded showcase is not the current scene")
	_expect(_session.active_slot == 1, "active slot is not the showcase slot after the load")
	_finish()

## The menu shows the bundled showcase picture: cover in snapshot mode, hi-res texture, showcase footer, no live world behind it.
func _check_showcase_cover(menu: Control) -> void:
	var cover: Control = menu.boot_cover
	_expect(cover.mode == "snapshot" and bool(menu.get("_showcase_picture")), "cover is not the showcase picture: mode %s" % str(cover.mode))
	_expect(menu.get("_footer_label").text == "RINGWORLD SHOWCASE", "footer is not RINGWORLD SHOWCASE: " + menu.get("_footer_label").text)
	var picture := cover.get_node_or_null("SnapshotPicture") as TextureRect
	if _expect(picture != null and picture.texture != null, "showcase picture node is missing"):
		_expect(picture.texture.get_width() >= 2000, "showcase picture is not hi-res (%d px wide)" % picture.texture.get_width())
		_results["showcase_picture_size"] = [picture.texture.get_width(), picture.texture.get_height()]
	_expect(str(cover.snapshot_sidecar.get("showcase_id", "")) == str(_session.get_showcase_definition().get("showcase_id", "?")), "cover picture is for another showcase_id")
