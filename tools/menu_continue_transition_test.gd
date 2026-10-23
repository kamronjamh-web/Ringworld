extends SceneTree
## Phase C (C6) test: Continue on the warm world flies in, descends and hands the world to the player with no loading screen.
##
## ALWAYS run through tools/menu_continue_transition_test.ps1, which builds an ISOLATED APPDATA holding COPIES of two saved slots. The script
## refuses to run when user:// resolves into the real roaming profile or is not a phase_c_live profile.
##
## One process per case (the .ps1 runs them in turn):
##   --case=main     warm, loiter, press Continue: rush, descend, hand-off. Final pose within 0.5 m of the saved pose; no pose jump when the
##                   scripted flight ends; the player is never below terrain from the press to the end; the world is the current scene;
##                   StartMenu is freed; the mouse is captured; the HUD is visible; the play session is active; saves are un-held and an
##                   autosave lands within 35 s; the clocks run; menu suspension is released; the grave key opens the gameplay drawer and
##                   Esc opens and closes the pause menu.
##   --case=warming  Continue pressed while the world is still loading: the button shows "Preparing NN%" and the fly-in starts by itself.
##                   Same end-state checks.
##   --case=ground   like main, but the saved player stands on the ground (the .ps1 runs --case=prep first on the same profile to rewrite it)
##   --case=underground the saved player is 40 m below the terrain surface (prep with --prep-depth=40): the fly-in stops above the surface, the camera never enters terrain, a 0.3 s fade cuts to the pose
##   --case=timeout  coverage is forced never-ready (test hook) and the timeout is shortened: the descent holds near 2 km, then the menu
##                   fades to black and the player is placed directly at the saved pose.
## Writes res://.godot/phase_c_live/c6/results_<case>.json (measured numbers, including press-to-first-person time).

const MENU := preload("res://scenes/StartMenu.tscn")
const WARM_TIMEOUT_MS := 300000
const REVEAL_TIMEOUT_MS := 180000
const TRANSITION_TIMEOUT_MS := 240000
const AUTOSAVE_WINDOW_SECONDS := 36.0
const POSE_TOLERANCE_M := 0.5
const GROUND_SETTLE_TOLERANCE_M := 2.0
## The ground case stands the saved player this far above the sampled terrain height.
const GROUND_CLEARANCE_M := 1.05
## A Continue from loiter (up to 30 km out at 1.2 km/s, then a 12 s descent that starts early) must stay near this.
const MAX_TYPICAL_PRESS_TO_PLAY_MS := 30000.0
const JUMP_TOLERANCE_M := 2.0
const JUMP_ANGLE_TOLERANCE_DEG := 3.0
const BELOW_TERRAIN_TOLERANCE_M := 0.5
const TIMEOUT_CASE_SECONDS := 25.0
const OUT_DIR := "res://.godot/phase_c_live/c6"
const WATCHDOG_USEC := 1500 * 1000000

var _errors := PackedStringArray()
var _checks := 0
var _results := {}
var _case := "main"
## --prep-depth=N (prep only): stand the saved player N metres below the terrain surface instead of on it.
var _prep_clearance := GROUND_CLEARANCE_M
var _menu: Control
var _warm: Node
var _session: Node
var _slot_dir := ""
var _live_slot := 0
var _handed_off: Node
var _terrain: Node
var _explorer: Node
var _min_margin := INF
var _below_frames := 0
var _terrain_samples := 0
var _was_scripted := false
var _last_flight_pose := {}
var _end_jump := -1.0
var _end_angle := -1.0
var _gate_seen := false
var _gate_altitude := INF
var _continue_report := {}
var _frame_count := 0

func _initialize() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--case="):
			_case = argument.get_slice("=", 1)
		if argument.begins_with("--prep-depth="):
			_prep_clearance = -float(argument.get_slice("=", 1))
	call_deferred("_run")

func _process(_delta: float) -> bool:
	if Time.get_ticks_usec() > WATCHDOG_USEC:
		push_error("watchdog: the continue transition test exceeded its time budget")
		quit(3)
		return true
	_frame_count += 1
	_sample_flight()
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

func _state_fingerprint() -> String:
	return _fingerprint(_slot_dir.path_join("world_state_surface_runtime_v1.json"))

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

func _key(code: int) -> void:
	for down in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.physical_keycode = code
		event.pressed = down
		Input.parse_input_event(event)

func _finish() -> void:
	_results["checks"] = _checks
	_results["errors"] = Array(_errors)
	_results["case"] = _case
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	var file := FileAccess.open(ProjectSettings.globalize_path(OUT_DIR).path_join("results_%s.json" % _case), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(_results, "  "))
		file.close()
	if _errors.is_empty():
		print("MENU_CONTINUE_TRANSITION=PASS case=%s checks=%d" % [_case, _checks])
		quit(0)
	else:
		for error in _errors:
			push_error(error)
		print("MENU_CONTINUE_TRANSITION=FAIL case=%s checks=%d" % [_case, _checks])
		quit(1)

func _wrap_delta(delta: float, circumference: float) -> float:
	return delta - roundf(delta / circumference) * circumference

func _pose_distance(a: Dictionary, b: Dictionary, circumference: float) -> float:
	var da := _wrap_delta(float(a["around_m"]) - float(b["around_m"]), circumference)
	var dv := float(a["vertical_m"]) - float(b["vertical_m"])
	var dc := float(a["across_m"]) - float(b["across_m"])
	return sqrt(da * da + dv * dv + dc * dc)

## Each rendered frame from the press on: terrain margin (player vertical minus ground height at the player), the scripted-flight end jump.
func _sample_flight() -> void:
	if _explorer == null or not is_instance_valid(_explorer):
		return
	var pose: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	if _terrain != null and is_instance_valid(_terrain):
		var ground := float(_terrain.call("SampleHeight", float(pose["around_m"]), float(pose["across_m"])))
		if is_finite(ground):
			_terrain_samples += 1
			var margin := float(pose["vertical_m"]) - ground
			# After the underground cut the player legitimately stands below the surface.
			if _case == "underground" and not _continue_report.is_empty():
				margin = INF
			_min_margin = minf(_min_margin, margin)
			if margin < -BELOW_TERRAIN_TOLERANCE_M:
				_below_frames += 1
	var scripted := bool(_explorer.get("ScriptedFlightActive"))
	if scripted:
		_last_flight_pose = pose
		if _menu != null and is_instance_valid(_menu) and _menu.flight_director != null:
			var director: Node = _menu.flight_director
			if bool(director.get("CoverageGateHolding")):
				_gate_seen = true
				_gate_altitude = minf(_gate_altitude, float((_menu.get("_warm").bridge.call("GetExplorerTelemetry") as Dictionary).get("altitude_m", INF)))
	elif _was_scripted and _end_jump < 0.0 and not _last_flight_pose.is_empty():
		var circumference := float(_explorer.get("ScriptedFlightCircumferenceMeters"))
		_end_jump = _pose_distance(pose, _last_flight_pose, circumference)
		_end_angle = maxf(absf(angle_difference(deg_to_rad(float(pose["yaw_deg"])), deg_to_rad(float(_last_flight_pose["yaw_deg"])))), absf(float(pose["pitch_deg"]) - float(_last_flight_pose["pitch_deg"])) * PI / 180.0)
		_end_angle = rad_to_deg(_end_angle)
	_was_scripted = scripted
	if _menu != null and is_instance_valid(_menu) and not _menu.continue_report.is_empty():
		_continue_report = _menu.continue_report

## Nearest point (rings of 250 m up to 20 km) whose terrain is at least 25 m above sea level with its neighbours above 10 m.
func _find_land(terrain: Node, around: float, across: float) -> Dictionary:
	var radius := 0.0
	while radius <= 20000.0:
		var steps := 1 if radius == 0.0 else maxi(8, int(TAU * radius / 250.0))
		for i in steps:
			var angle := TAU * float(i) / float(steps)
			var a := around + cos(angle) * radius
			var c := across + sin(angle) * radius
			var ground := float(terrain.call("SampleHeight", a, c))
			if not is_finite(ground) or ground < 25.0:
				continue
			var inland := true
			for offset in [Vector2(60, 0), Vector2(-60, 0), Vector2(0, 60), Vector2(0, -60)]:
				var neighbour := float(terrain.call("SampleHeight", a + offset.x, c + offset.y))
				if not is_finite(neighbour) or neighbour < 10.0:
					inland = false
					break
			if inland:
				return {"around": a, "across": c, "ground": ground}
		radius += 250.0
	return {}

## --case=prep: warms the world once, then rewrites the isolated copy's saved player to stand on the ground (noclip off, zero velocity) so the
## next process (--case=ground) exercises the real Continue landing instead of a save made in flight.
func _prepare_ground_save() -> void:
	if not _expect(await _wait_until(func() -> bool: return int(_warm.state) == 2, WARM_TIMEOUT_MS), "prep: warm world did not become ready"):
		return _finish()
	var world: Node = _warm.world
	var pose: Dictionary = _warm.bridge.call("GetHomePose")
	var terrain: Node = world.get_node("Terrain3DGameplay")
	# The saved spot is over the ocean; move the test save to the nearest inland point (so the landing is dry ground, not the seabed).
	var spot := _find_land(terrain, float(pose["around_m"]), float(pose["across_m"]))
	if not _expect(not spot.is_empty(), "prep: no inland point within 20 km of the saved spot"):
		return _finish()
	var ground := float(spot["ground"])
	await _menu._release_warm_world()
	var path := _slot_dir.path_join("world_state_surface_runtime_v1.json")
	var text := FileAccess.get_file_as_string(path)
	var head := text.find("\"Player\"")
	var tail := text.substr(head)
	var vertical := RegEx.create_from_string("\"VerticalMeters\":\\s*[-0-9.eE+]+")
	var noclip := RegEx.create_from_string("\"Noclip\":\\s*(true|false)")
	tail = vertical.sub(tail, "\"VerticalMeters\": %.4f" % (ground + _prep_clearance))
	tail = RegEx.create_from_string("\"AroundMeters\":\\s*[-0-9.eE+]+").sub(tail, "\"AroundMeters\": %.4f" % float(spot["around"]))
	tail = RegEx.create_from_string("\"AcrossMeters\":\\s*[-0-9.eE+]+").sub(tail, "\"AcrossMeters\": %.4f" % float(spot["across"]))
	tail = noclip.sub(tail, "\"Noclip\": false")
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text.substr(0, head) + tail)
	file.close()
	_results["prep_ground_m"] = ground
	_results["prep_original_vertical_m"] = float(pose["vertical_m"])
	print("PREP ground=%.2f original_vertical=%.2f" % [ground, float(pose["vertical_m"])])
	_finish()

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("phase_c_live"):
		push_error("Refusing to run: user:// (%s) is not an isolated phase_c_live APPDATA. Use menu_continue_transition_test.ps1." % user_dir)
		quit(2)
		return
	_results["adapter"] = RenderingServer.get_video_adapter_name()
	_results["headless"] = DisplayServer.get_name() == "headless"
	_session = root.get_node_or_null("WorldSession")
	if not _expect(_session != null, "WorldSession autoload missing"):
		return _finish()
	_live_slot = int(_session.get_live_menu_slot())
	if not _expect(_live_slot in [1, 2], "no eligible live menu slot (got %d)" % _live_slot):
		return _finish()
	_slot_dir = ProjectSettings.globalize_path(_session.call("_slot_directory", _live_slot))
	var state_before := _state_fingerprint()
	_expect(state_before != "missing", "isolated live slot has no saved world state")

	_menu = MENU.instantiate()
	# Never let the drawer persist test values into the project defaults.
	_menu.live_world_settings_path = "user://menu_live_world_c6.tres"
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if not _expect(_warm != null, "menu did not create RingWarmWorld"):
		return _finish()
	if _case == "prep":
		await _prepare_ground_save()
		return
	_warm.handed_off.connect(func(world: Node) -> void: _handed_off = world)

	if _case == "warming":
		# The world is still loading: Continue must wait, show the progress and fly in on its own.
		if not _expect(await _wait_until(func() -> bool: return int(_warm.state) == 1, 60000), "warm world never started loading"):
			return _finish()
		_menu.continue_button.pressed.emit()
		await process_frame
		_expect(bool(_menu.get("_continue_pending")), "Continue while warming did not become pending")
		_expect(_menu.continue_subline.text.begins_with("Preparing "), "Continue subline does not show Preparing NN%%: '%s'" % _menu.continue_subline.text)
		_expect(_menu.continue_button.disabled, "Continue stayed pressable while pending")
		_results["pending_subline"] = _menu.continue_subline.text
	if not _expect(await _wait_until(func() -> bool: return int(_warm.state) == 2 or _handed_off != null, WARM_TIMEOUT_MS), "warm world did not become ready"):
		return _finish()
	var bridge: Node = _warm.bridge
	var world: Node = _warm.world
	if _case == "warming":
		_expect(not _menu.continue_report.is_empty() or bool(_menu.get("_continue_pending")) or _menu.get("_continuing"), "pending Continue did not start after the world got ready")
	else:
		if not _expect(await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), REVEAL_TIMEOUT_MS), "the cover was never retired"):
			return _finish()
		await process_frame
	_explorer = world.get_node("Explorer")
	_terrain = world.get_node_or_null("Terrain3DGameplay")
	_expect(_terrain != null, "world has no Terrain3DGameplay")
	var saved: Dictionary = {}
	var saves: Node = world.get_node("WorldSaveManager")
	if _case != "warming":
		saved = bridge.call("GetHomePose")
		_expect(bool(saved.get("valid", false)), "no saved (home) pose")
		_expect(_menu.flight_director != null and int(_menu.flight_director.CurrentMode) == 1, "director is not loitering before Continue")
		_expect(bool(saves.get("HoldSavesUntilWorldReady")), "saves are not held before Continue")
		_expect(Input.mouse_mode == Input.MOUSE_MODE_VISIBLE, "mouse is not Visible before Continue")
	else:
		saved = bridge.call("GetHomePose") if is_instance_valid(bridge) else {}
	_results["saved_pose"] = saved
	var press_msec := Time.get_ticks_msec()
	if _case == "main" or _case == "ground" or _case == "timeout" or _case == "underground":
		if _case == "timeout":
			_menu.continue_timeout_seconds = TIMEOUT_CASE_SECONDS
			bridge.set("TestForceCoverageNotReady", true)
			_menu.live_drawer.call("SetTuningValue", "RushSpeedKmPerSecond", 3.0)
			_menu.live_drawer.call("SetTuningValue", "DescentSeconds", 4.0)
		_menu.continue_button.pressed.emit()
		await process_frame
		_expect(bool(_menu.get("_continuing")) or bool(_menu.get("_leaving")), "Continue did not start the transition")
		_expect(not is_instance_valid(root.get_node_or_null("RingWorldLoading")) or not bool(root.get_node("RingWorldLoading").active), "Continue opened the loading screen")
	var started_visible := Input.mouse_mode == Input.MOUSE_MODE_VISIBLE
	var during_flight_hud := int(bridge.call("CountVisibleHudLayers")) if is_instance_valid(bridge) else -1
	_expect(started_visible, "mouse is not Visible while the transition starts")
	_expect(during_flight_hud == 0, "world HUD is visible during the flight (%d layers)" % during_flight_hud)
	if not _expect(await _wait_until(func() -> bool: return _handed_off != null, TRANSITION_TIMEOUT_MS), "the hand-off never completed"):
		return _finish()
	var handoff_msec := Time.get_ticks_msec()
	_results["press_to_play_wall_ms"] = handoff_msec - press_msec
	var final_pose: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	await process_frame
	await process_frame

	# --- end state
	_expect(_handed_off == world and current_scene == world, "the world is not the current scene")
	_expect(not is_instance_valid(_menu), "StartMenu was not freed")
	_expect(Input.mouse_mode == Input.MOUSE_MODE_CAPTURED, "mouse is not captured after the hand-off")
	var hud: CanvasLayer = world.get_node_or_null("PresentationHud") as CanvasLayer
	_expect(hud != null and hud.visible, "PresentationHud is not visible after the hand-off")
	var diagnostics := world.get_node_or_null("LodDebugHud") as CanvasLayer
	_expect(diagnostics == null or not diagnostics.visible, "the LOD diagnostics HUD is visible after the hand-off (it is hidden by default in gameplay)")
	var played: Object = _session.get("_play_world").get_ref() if _session.get("_play_world") != null else null
	_expect(played == world, "play session is not bound to the world")
	_expect(not bool(_session.warm_world_active) and int(_session.active_slot) == _live_slot, "session still bound as warm or to the wrong slot")
	_expect(not bool(saves.get("HoldSavesUntilWorldReady")), "saves are still held after the hand-off")
	_expect(not bool(_explorer.get("ScriptedFlightActive")), "explorer is still under scripted flight")
	_expect(_explorer.process_mode != Node.PROCESS_MODE_DISABLED, "explorer is held")
	for owner_name in ["GridEditController", "LiveTuningDrawer", "LodPauseMenu"]:
		var owner_node := world.get_node_or_null(owner_name)
		_expect(owner_node != null and owner_node.process_mode != Node.PROCESS_MODE_DISABLED, "%s is still held after the hand-off" % owner_name)
	_expect(int(_warm.state) == 0, "RingWarmWorld is not idle after the hand-off")
	_expect(not is_instance_valid(root.get_node_or_null("RingWorldLoading")) or not bool(root.get_node("RingWorldLoading").active), "the loading screen is active")
	var clouds := world.get_node_or_null("RingVolumetricClouds")
	var cycle := world.get_node_or_null("RingDayNightCycle")
	_expect(clouds != null and not bool(clouds.get("CloudSimulationPaused")), "the cloud clock is still paused")
	_results["celestial_paused_after"] = bool(cycle.get("Paused")) if cycle != null else null
	var underground := world.get_node_or_null("UndergroundMode")
	if underground != null:
		_expect(int(underground.get("PresentationSuspendGroups")) == 0, "presentation suspend groups were not cleared")
	_expect(_terrain != null and not bool(_terrain.get("CoverageFocusOverrideActive")), "Terrain3D coverage focus override was not cleared")
	var circumference := float(_explorer.get("ScriptedFlightCircumferenceMeters"))
	if _case != "warming" or not saved.is_empty():
		var distance := _pose_distance(final_pose, saved, circumference)
		var horizontal := _pose_distance({"around_m": final_pose["around_m"], "vertical_m": 0.0, "across_m": final_pose["across_m"]}, {"around_m": saved["around_m"], "vertical_m": 0.0, "across_m": saved["across_m"]}, circumference)
		_results["final_pose_error_m"] = distance
		_results["final_horizontal_error_m"] = horizontal
		if _case == "underground":
			# The save is below the surface. It either stays there (a cave void) or is recovered to the surface by the normal below-terrain
			# recovery, exactly as in a loading-screen load; either way the spot is the same horizontally.
			var ground_here := float(_terrain.call("SampleHeight", float(final_pose["around_m"]), float(final_pose["across_m"])))
			var kept := absf(float(final_pose["vertical_m"]) - float(saved["vertical_m"])) <= GROUND_SETTLE_TOLERANCE_M
			var recovered := absf(float(final_pose["vertical_m"]) - ground_here) <= 6.0
			_results["underground_kept_depth"] = kept
			_results["underground_recovered_to_surface"] = recovered
			_expect(horizontal <= POSE_TOLERANCE_M and (kept or recovered), "underground final pose: %.2f m horizontally, vertical %.2f vs saved %.2f / surface %.2f" % [horizontal, float(final_pose["vertical_m"]), float(saved["vertical_m"]), ground_here])
		elif bool(_results.get("final_noclip", false)) or _case == "main" or _case == "warming":
			_expect(distance <= POSE_TOLERANCE_M, "final pose is %.2f m from the saved pose (limit %.1f)" % [distance, POSE_TOLERANCE_M])
		else:
			# A standing save goes through the loading spawn settle, which puts the capsule on real collision (up to GROUND_SETTLE_TOLERANCE_M off the saved height).
			_expect(horizontal <= POSE_TOLERANCE_M and absf(float(final_pose["vertical_m"]) - float(saved["vertical_m"])) <= GROUND_SETTLE_TOLERANCE_M, "final pose is %.2f m horizontally / %.2f m vertically from the saved pose" % [horizontal, absf(float(final_pose["vertical_m"]) - float(saved["vertical_m"]))])
	_results["final_pose"] = final_pose
	_results["post_pose"] = _explorer.call("GetCanonicalPoseMeters")
	_results["final_grounded"] = bool(_explorer.get("BodyGrounded"))
	_results["final_noclip"] = bool(_explorer.get("NoclipEnabled"))
	_results["terrain_samples"] = _terrain_samples
	_results["min_terrain_margin_m"] = _min_margin
	_results["frames_below_terrain"] = _below_frames
	_expect(_terrain_samples > 30, "too few terrain samples: %d" % _terrain_samples)
	_expect(_below_frames == 0, "the player was below terrain on %d frames (min margin %.2f m)" % [_below_frames, _min_margin])
	_results["end_jump_m"] = _end_jump
	_results["end_angle_deg"] = _end_angle
	# The timeout fallback places the player directly (behind a black fade), so only the smooth descent has a jump limit.
	if _case != "timeout" and _case != "underground":
		_expect(_end_jump >= 0.0 and _end_jump <= JUMP_TOLERANCE_M, "pose jumped %.2f m when the scripted flight ended (limit %.1f)" % [_end_jump, JUMP_TOLERANCE_M])
		_expect(_end_angle >= 0.0 and _end_angle <= JUMP_ANGLE_TOLERANCE_DEG, "view turned %.2f deg when the scripted flight ended (limit %.1f)" % [_end_angle, JUMP_ANGLE_TOLERANCE_DEG])
	var metrics: Dictionary = _warm.get_metrics()
	_results["continue_report"] = _continue_report
	_results["handoff_ms"] = metrics.get("handoff_ms", -1.0)
	_results["handoff_settle_ms"] = metrics.get("handoff_settle_ms", -1.0)
	_results["press_to_play_ms"] = metrics.get("continue_press_to_play_ms", -1.0)
	_results["outcome"] = metrics.get("continue_outcome", "")
	print("CONTINUE_RESULT outcome=%s press_to_play_ms=%s handoff_ms=%s min_margin=%.2f end_jump=%.3f" % [_results["outcome"], str(_results["press_to_play_ms"]), str(_results["handoff_ms"]), _min_margin, _end_jump])
	if _case == "timeout":
		_expect(str(_results["outcome"]) == "timeout", "outcome is '%s', not timeout" % str(_results["outcome"]))
		_expect(_gate_seen, "the descent never held for terrain coverage")
		_results["gate_hold_altitude_m"] = _gate_altitude
		_expect(_gate_altitude > 500.0 and _gate_altitude < 2600.0, "gate held at %.0f m, expected near 2000 m" % _gate_altitude)
	elif _case == "underground":
		_expect(str(_results["outcome"]) == "underground", "outcome is '%s', not underground" % str(_results["outcome"]))
		_expect(bool(_continue_report.get("use_veil", false)), "the underground cut did not use the black fade")
		var mode_node := world.get_node_or_null("UndergroundMode")
		if mode_node != null and not bool(_results.get("underground_recovered_to_surface", false)) and bool(_results.get("underground_kept_depth", false)):
			await _wait_seconds(3.0)
			_results["underground_mode"] = int(mode_node.get("Mode"))
			_expect(int(mode_node.get("Mode")) in [1, 2], "underground mode is not active after the hand-off (mode %d)" % int(mode_node.get("Mode")))
		elif mode_node != null:
			_results["underground_mode"] = int(mode_node.get("Mode"))
	else:
		_expect(str(_results["outcome"]) == "arrived", "outcome is '%s', not arrived" % str(_results["outcome"]))
		if _case != "warming":
			_expect(float(_results["press_to_play_ms"]) <= MAX_TYPICAL_PRESS_TO_PLAY_MS, "press to first person took %.1f s (limit %.0f s)" % [float(_results["press_to_play_ms"]) / 1000.0, MAX_TYPICAL_PRESS_TO_PLAY_MS / 1000.0])

	# --- gameplay controls: grave opens the gameplay drawer, Esc closes it, Esc opens and closes the pause menu
	_key(KEY_QUOTELEFT)
	await _wait_seconds(0.8)
	var drawer_panel := world.get_node("LiveTuningDrawer").find_child("LiveTuningDrawer", true, false) as Control
	_expect(drawer_panel != null and drawer_panel.visible, "grave did not open the gameplay drawer")
	_key(KEY_ESCAPE)
	await _wait_seconds(0.8)
	_expect(drawer_panel == null or not drawer_panel.visible, "Esc did not close the gameplay drawer")
	_key(KEY_ESCAPE)
	await _wait_seconds(0.4)
	_expect(paused, "Esc did not open the pause menu")
	_key(KEY_ESCAPE)
	await _wait_seconds(0.4)
	_expect(not paused and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED, "Esc did not close the pause menu")
	paused = false

	# --- an autosave lands within 35 s of the hand-off (the state file was untouched while the menu flew)
	var after_state_start := _state_fingerprint()
	var elapsed := float(Time.get_ticks_msec() - handoff_msec) / 1000.0
	var saved_at := -1.0
	if after_state_start != state_before:
		saved_at = elapsed
	else:
		var deadline := Time.get_ticks_msec() + int((AUTOSAVE_WINDOW_SECONDS - elapsed) * 1000.0)
		while Time.get_ticks_msec() < deadline:
			await process_frame
			if _state_fingerprint() != state_before:
				saved_at = float(Time.get_ticks_msec() - handoff_msec) / 1000.0
				break
	_results["autosave_after_s"] = saved_at
	_expect(saved_at >= 0.0 and saved_at <= AUTOSAVE_WINDOW_SECONDS, "no autosave within %.0f s of the hand-off" % AUTOSAVE_WINDOW_SECONDS)
	_finish()
