extends SceneTree
## Phase C (C4) test: the start menu's flight director loiters the held world's camera around the save spot.
##
## ALWAYS run through tools/menu_flight_director_test.ps1, which builds an ISOLATED APPDATA holding COPIES of two saved slots. The script
## refuses to run when user:// resolves into the real roaming profile or is not a phase_c_live profile.
##
## Checks:
##   - after the cover retires the menu owns a flight director in Loiter mode; the explorer is under scripted flight
##   - 120 s of loiter: altitude above ground stays within +/-500 m of cruise (after the first settle seconds) and strictly below 18 km,
##     the camera stays within the loiter radius of the save spot, speed stays near the loiter speed, camera position never jumps between
##     frames, the mouse stays Visible
##   - W/A/S/D (held), V, 1-5, the wheel, mouse motion and Esc are injected during the loiter and change nothing (noclip, speed tier,
##     target speed, pitch, motion)
##   - a second leg is placed just before a collision-sector boundary so the floating-origin anchor must move: at least one re-anchor, no
##     per-frame re-anchoring, and no position jump
##   - world state files are unchanged (saves held), teardown is clean
## Writes res://.godot/phase_c_live/c4/results.json (measured numbers).

const MENU := preload("res://scenes/StartMenu.tscn")
const WARM_TIMEOUT_MS := 300000
const REVEAL_TIMEOUT_MS := 180000
const LOITER_SECONDS := 120.0
const ANCHOR_LEG_SECONDS := 150.0
const SETTLE_SECONDS := 10.0
const ALTITUDE_TOLERANCE_M := 500.0
const CEILING_M := 18000.0
const SECTOR_M := 32768.0
const MAX_SPEED_FOR_JUMP := 260.0
const MAX_STEP_SECONDS := 8.0 / 60.0
const OUT_DIR := "res://.godot/phase_c_live/c4"
const WATCHDOG_USEC := 1500 * 1000000

var _errors := PackedStringArray()
var _checks := 0
var _results := {}
var _menu: Control
var _warm: Node
var _session: Node
var _slot_dir := ""

# Per-leg sampling
var _samples := 0
var _min_alt := INF
var _max_alt := -INF
var _max_home_distance := 0.0
var _max_jump := 0.0
var _max_speed := 0.0
var _speed_bad := 0
var _alt_bad := 0
var _last_pos := Vector3.ZERO
var _last_msec := 0
var _have_last := false

func _initialize() -> void:
	call_deferred("_run")

func _process(_delta: float) -> bool:
	if Time.get_ticks_usec() > WATCHDOG_USEC:
		push_error("watchdog: the flight director test exceeded its time budget")
		quit(3)
		return true
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

func _state_files() -> Dictionary:
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

func _key(code: int, down: bool) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.physical_keycode = code
	event.pressed = down
	Input.parse_input_event(event)

func _tap(code: int) -> void:
	_key(code, true)
	_key(code, false)

func _wheel(up: bool) -> void:
	var event := InputEventMouseButton.new()
	event.button_index = MOUSE_BUTTON_WHEEL_UP if up else MOUSE_BUTTON_WHEEL_DOWN
	event.pressed = true
	event.factor = 1.0
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
		print("MENU_FLIGHT_DIRECTOR=PASS checks=%d" % _checks)
		quit(0)
	else:
		for error in _errors:
			push_error(error)
		print("MENU_FLIGHT_DIRECTOR=FAIL checks=%d" % _checks)
		quit(1)

func _reset_leg() -> void:
	_samples = 0
	_min_alt = INF
	_max_alt = -INF
	_max_home_distance = 0.0
	_max_jump = 0.0
	_max_speed = 0.0
	_speed_bad = 0
	_alt_bad = 0
	_have_last = false

func _wrap_delta(delta: float, circumference: float) -> float:
	return delta - roundf(delta / circumference) * circumference

## Samples the held world once per rendered frame: altitude above ground, distance from the save spot, speed and the position step.
func _sample(bridge: Node, home: Dictionary, circumference: float, leg_seconds: float, cruise_m: float, loiter_speed: float) -> void:
	var pose: Dictionary = bridge.call("GetCameraPose")
	var telemetry: Dictionary = bridge.call("GetExplorerTelemetry")
	if not bool(pose.get("valid", false)) or not bool(telemetry.get("valid", false)):
		return
	var around := float(pose["around_m"])
	var across := float(pose["across_m"])
	var altitude := float(telemetry["altitude_m"])
	var speed := float(telemetry["speed_mps"])
	var now := Time.get_ticks_msec()
	_samples += 1
	_min_alt = minf(_min_alt, altitude)
	_max_alt = maxf(_max_alt, altitude)
	_max_speed = maxf(_max_speed, speed)
	if altitude >= CEILING_M:
		_alt_bad += 1
	if leg_seconds >= SETTLE_SECONDS:
		if absf(altitude - cruise_m) > ALTITUDE_TOLERANCE_M:
			_alt_bad += 1
		if absf(speed - loiter_speed) > 40.0:
			_speed_bad += 1
	var da := _wrap_delta(around - float(home["around_m"]), circumference)
	var dc := across - float(home["across_m"])
	_max_home_distance = maxf(_max_home_distance, sqrt(da * da + dc * dc))
	var position := Vector3(around, float(pose["vertical_m"]), across)
	if _have_last:
		var step := position - _last_pos
		step.x = _wrap_delta(step.x, circumference)
		var allowed := MAX_SPEED_FOR_JUMP * minf(float(now - _last_msec) / 1000.0, MAX_STEP_SECONDS) + 5.0
		_max_jump = maxf(_max_jump, step.length() - allowed)
	_last_pos = position
	_last_msec = now
	_have_last = true

func _run_leg(bridge: Node, home: Dictionary, circumference: float, seconds: float, cruise_m: float, loiter_speed: float, inject_keys: bool, until_anchor := false) -> Dictionary:
	_reset_leg()
	var explorer: Node = (_warm.world as Node).get_node("Explorer")
	var anchors_base := int(explorer.get("ScriptedFlightAnchorCount"))
	var anchor_seen_at := -1.0
	var start := Time.get_ticks_msec()
	var injected := false
	var keys_held_until := 0.0
	var before := {}
	var after := {}
	while true:
		var elapsed := float(Time.get_ticks_msec() - start) / 1000.0
		if until_anchor:
			# Rendering can be slow right after a placement, so the boundary leg runs until it was crossed (and a little beyond), up to `seconds`.
			if anchor_seen_at < 0.0 and int(explorer.get("ScriptedFlightAnchorCount")) > anchors_base:
				anchor_seen_at = elapsed
			if elapsed >= seconds or (anchor_seen_at >= 0.0 and elapsed >= anchor_seen_at + 8.0):
				break
		elif elapsed >= seconds:
			break
		_sample(bridge, home, circumference, elapsed, cruise_m, loiter_speed)
		if inject_keys and not injected and elapsed >= 40.0:
			injected = true
			before = _control_state(explorer, bridge)
			for code in [KEY_W, KEY_A, KEY_S, KEY_D]:
				_key(code, true)
			_tap(KEY_V)
			for code in [KEY_1, KEY_2, KEY_3, KEY_4, KEY_5]:
				_tap(code)
			_tap(KEY_ESCAPE)
			_wheel(true)
			_wheel(false)
			_wheel(true)
			var motion := InputEventMouseMotion.new()
			motion.relative = Vector2(300.0, -200.0)
			motion.position = Vector2(500.0, 400.0)
			Input.parse_input_event(motion)
			keys_held_until = elapsed + 3.0
		if injected and keys_held_until > 0.0 and elapsed >= keys_held_until:
			keys_held_until = 0.0
			for code in [KEY_W, KEY_A, KEY_S, KEY_D]:
				_key(code, false)
			after = _control_state(explorer, bridge)
		await process_frame
	if inject_keys:
		_expect(injected, "keys were never injected")
		_results["controls_before_keys"] = before
		_results["controls_after_keys"] = after
		_expect(_controls_equal(before, after), "injected keys/wheel/mouse changed the explorer controls: %s -> %s" % [str(before), str(after)])
	return {
		"seconds": seconds, "samples": _samples, "min_alt_m": _min_alt, "max_alt_m": _max_alt, "max_home_distance_m": _max_home_distance,
		"max_jump_over_allowed_m": _max_jump, "max_speed_mps": _max_speed, "speed_out_of_band_frames": _speed_bad, "altitude_bad_frames": _alt_bad,
	}

func _control_state(explorer: Node, bridge: Node) -> Dictionary:
	var pose: Dictionary = bridge.call("GetCameraPose")
	return {
		"noclip": bool(explorer.get("NoclipEnabled")),
		"tier": int(explorer.get("NoclipSpeedTier")),
		"target_speed": float(explorer.get("NoclipTargetSpeedMetersPerSecond")),
		"scripted": bool(explorer.get("ScriptedFlightActive")),
		"pitch": float(pose.get("pitch_deg", 0.0)),
		"mouse_visible": Input.mouse_mode == Input.MOUSE_MODE_VISIBLE,
	}

func _controls_equal(a: Dictionary, b: Dictionary) -> bool:
	if a.is_empty() or b.is_empty():
		return false
	for key: String in ["noclip", "tier", "scripted", "mouse_visible"]:
		if a[key] != b[key]:
			return false
	# Pitch eases toward the cruise pitch by itself; the injected mouse motion (-200 px) would have moved it by far more than a degree or two.
	return is_equal_approx(float(a["target_speed"]), float(b["target_speed"])) and absf(float(a["pitch"]) - float(b["pitch"])) < 3.0

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("phase_c_live"):
		push_error("Refusing to run: user:// (%s) is not an isolated phase_c_live APPDATA. Use menu_flight_director_test.ps1." % user_dir)
		quit(2)
		return
	_results["adapter"] = RenderingServer.get_video_adapter_name()
	_results["headless"] = DisplayServer.get_name() == "headless"
	_session = root.get_node_or_null("WorldSession")
	if not _expect(_session != null, "WorldSession autoload missing"):
		return _finish()
	var live_slot := int(_session.get_live_menu_slot())
	if not _expect(live_slot in [1, 2], "no eligible live menu slot (got %d)" % live_slot):
		return _finish()
	_slot_dir = ProjectSettings.globalize_path(_session.call("_slot_directory", live_slot))
	var before := _state_files()
	_expect(before["world_state_surface_runtime_v1.json"] != "missing", "isolated live slot has no saved world state")
	_results["state_before"] = before

	_menu = MENU.instantiate()
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if not _expect(_warm != null, "menu did not create RingWarmWorld"):
		return _finish()
	_expect(_menu.flight_director == null, "a flight director exists before the world is live")
	if not _expect(await _wait_until(func() -> bool: return int(_warm.state) == 2, WARM_TIMEOUT_MS), "warm world did not become ready"):
		return _finish()
	if not _expect(await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), REVEAL_TIMEOUT_MS), "the cover was never retired"):
		return _finish()
	await process_frame
	var director: Node = _menu.flight_director
	if not _expect(director != null, "the menu started no flight director after the reveal"):
		return _finish()
	var bridge: Node = _warm.bridge
	var world: Node = _warm.world
	var explorer: Node = world.get_node("Explorer")
	_expect(int(director.CurrentMode) == 1, "director is not loitering (mode %d)" % int(director.CurrentMode))
	_expect(bool(explorer.get("ScriptedFlightActive")), "explorer is not under scripted flight")
	_expect(explorer.process_mode != Node.PROCESS_MODE_DISABLED, "explorer is still held (disabled) while the director flies it")
	_expect(bool(world.get_node("WorldSaveManager").get("HoldSavesUntilWorldReady")), "saves are not held while the menu flies")
	_expect(Input.mouse_mode == Input.MOUSE_MODE_VISIBLE, "mouse is not Visible while loitering")
	_expect(_menu.get("_survey_label").text.begins_with("ALT "), "survey readout does not show explorer telemetry")
	var circumference := float(explorer.get("ScriptedFlightCircumferenceMeters"))
	var home: Dictionary = bridge.call("GetHomePose")
	_expect(bool(home.get("valid", false)), "no home pose")
	var cruise_m := float(director.CruiseAltitudeMeters)
	var loiter_speed := float(director.LoiterSpeedMetersPerSecond)
	var radius_m := float(director.LoiterRadiusMeters)
	_expect(is_equal_approx(radius_m, _menu.live_drawer.call("GetValue", "LoiterRadiusKm") * 1000.0), "director loiter radius does not follow the drawer")
	_results["home"] = home
	_results["cruise_m"] = cruise_m
	_results["loiter_radius_m"] = radius_m
	_results["loiter_speed_mps"] = loiter_speed
	var anchors_start := int(explorer.get("ScriptedFlightAnchorCount"))

	# --- leg 1: the natural loiter
	var leg1 := await _run_leg(bridge, home, circumference, LOITER_SECONDS, cruise_m, loiter_speed, true)
	var anchors_leg1 := int(explorer.get("ScriptedFlightAnchorCount")) - anchors_start
	leg1["anchors"] = anchors_leg1
	_results["loiter_leg"] = leg1
	print("MENU_LOITER ", JSON.stringify(leg1))
	_expect(float(leg1["max_alt_m"]) < CEILING_M, "altitude reached the 18 km ceiling: %.0f m" % float(leg1["max_alt_m"]))
	_expect(int(leg1["altitude_bad_frames"]) == 0, "altitude left cruise +/-%.0f m (or the ceiling) on %d frames (min %.0f, max %.0f)" % [ALTITUDE_TOLERANCE_M, int(leg1["altitude_bad_frames"]), float(leg1["min_alt_m"]), float(leg1["max_alt_m"])])
	_expect(float(leg1["max_home_distance_m"]) <= radius_m + 500.0, "camera left the loiter radius: %.0f m" % float(leg1["max_home_distance_m"]))
	_expect(int(leg1["speed_out_of_band_frames"]) == 0, "speed left the loiter band on %d frames (max %.1f)" % [int(leg1["speed_out_of_band_frames"]), float(leg1["max_speed_mps"])])
	_expect(float(leg1["max_jump_over_allowed_m"]) <= 0.0, "position jumped between frames by %.1f m more than the motion allows" % float(leg1["max_jump_over_allowed_m"]))
	_expect(int(leg1["samples"]) > 600, "too few samples: %d" % int(leg1["samples"]))
	_expect(Input.mouse_mode == Input.MOUSE_MODE_VISIBLE, "mouse is not Visible after the loiter")
	_expect(_menu.flight_director == director and int(director.CurrentMode) == 1, "director stopped loitering")
	var after_state := _state_files()
	_results["state_after_loiter"] = after_state
	_expect(after_state == before, "world state files changed while the menu flew: %s vs %s" % [str(before), str(after_state)])

	# --- leg 2: force the floating-origin anchor to move. Place just short of a collision-sector boundary (an anchor sits at the nearest
	# multiple of SECTOR_M, so the next boundary is 16384 m from it) and loiter across it; the orbit passes through the start, heading +around.
	var pose: Dictionary = bridge.call("GetCameraPose")
	var width := float(explorer.get("ScriptedFlightAcrossWidthMeters"))
	var x := floorf(float(pose["around_m"]) / SECTOR_M) * SECTOR_M + SECTOR_M * 0.5 - 300.0
	var across := clampf(float(pose["across_m"]), 20000.0, maxf(20000.0, width - 45000.0))
	var vertical := float(pose["vertical_m"])
	_expect(director.Place(x, vertical, across, 0.0, -25.0), "placement failed")
	await _wait_seconds(0.5)
	# Ground differs at the new spot: bring the camera to cruise altitude with one corrected placement before loitering.
	var altitude := float((bridge.call("GetExplorerTelemetry") as Dictionary)["altitude_m"])
	vertical += cruise_m - altitude
	_expect(director.Place(x, vertical, across, 0.0, -25.0), "corrected placement failed")
	await _wait_seconds(0.5)
	var place_altitude := float((bridge.call("GetExplorerTelemetry") as Dictionary)["altitude_m"])
	_expect(place_altitude < CEILING_M - 500.0 and absf(place_altitude - cruise_m) < 1500.0, "placement altitude is off: %.0f m" % place_altitude)
	
	# Home 8 km across from the placement: the phase points the orbit at +around, straight at the boundary.
	director.SetHome(x, vertical, across + 8000.0)
	director.StartLoiter()
	var anchors_before := int(explorer.get("ScriptedFlightAnchorCount"))
	var leg2 := await _run_leg(bridge, home, circumference, ANCHOR_LEG_SECONDS, cruise_m, loiter_speed, false, true)
	var anchors_leg2 := int(explorer.get("ScriptedFlightAnchorCount")) - anchors_before
	leg2["anchors"] = anchors_leg2
	leg2["placed_at_around_m"] = x
	_results["anchor_leg"] = leg2
	print("MENU_ANCHOR_LEG ", JSON.stringify(leg2))
	_expect(anchors_leg2 >= 1, "no re-anchor happened on the boundary leg")
	_expect(anchors_leg2 <= 3, "the anchor moved %d times in %.0f s (per-frame re-anchoring?)" % [anchors_leg2, ANCHOR_LEG_SECONDS])
	_expect(float(leg2["max_jump_over_allowed_m"]) <= 0.0, "position jumped across the re-anchor by %.1f m more than the motion allows" % float(leg2["max_jump_over_allowed_m"]))
	_expect(float(leg2["max_alt_m"]) < CEILING_M, "altitude reached the 18 km ceiling on the boundary leg: %.0f m" % float(leg2["max_alt_m"]))

	# --- teardown: still nothing written, and the explorer is handed back
	var final_state := _state_files()
	_expect(final_state == before, "world state files changed by the end of the test")
	await _menu._release_warm_world()
	_expect(int(_warm.state) == 0 and not bool(_session.warm_world_active), "teardown left the warm owner or session bound")
	_expect(_menu.flight_director == null, "the flight director outlived the world")
	_expect(_state_files() == before, "teardown wrote world state")
	_finish()

func _wait_seconds(seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame
