extends SceneTree

## Walking after a fast flight (user bug 2026-11-20: after a 3-5 km/s noclip trip and a landing the body snags and only a jump frees it).
## Windowed GPU (the Terrain3D gameplay window and the vegetation colliders need the real renderer); certified runtime-surface world.
##
## Every case lands on the same patch P and walks the same legs at explicit speeds (walk 2 m/s, sprint 7 m/s; never the controller
## defaults), logging per physics frame: distance of the body from the floating origin (|global_position|, the world is anchor relative),
## floor state, slide collisions (collider name and normal), zero-motion frames with input held, and the gameplay-window streaming state.
##   control: grounded teleport to P (forced re-anchor at the body).
##   far:     flight teleport 20 km before P inside the same 32 km collision sector, scripted flight to P, descent, V (noclip off).
##   high:    flight teleport 4 km above P, scripted descent, V.
##   input:   real keys from 40 km out at 400 m: preset 4 + Shift (3.6 km/s, crosses a sector boundary), presets 3 and 1 onto P, Q down, V.
## Verdict: after each landing the body is within ReanchorLimit of the floating origin, and walking is no worse than the control
## (zero-motion frames, longest zero-motion run, floor dropouts, wall contacts on open ground).
## User args: --cases=control,far,high,input  --no-vegetation  --report-only (print numbers, never fail on them)  --keep-report (JSON in user://)
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const SECTOR := 32768.0
const WALK_SPEED := 2.0
const SPRINT_SPEED := 7.0
const REANCHOR_LIMIT := 2500.0
const ZERO_RUN_LIMIT := 8

var fixture := ""
var deadline := 0
var game: Node
var explorer: CharacterBody3D
var surface: Node
var failed := false
var report_only := false
var results: Array[Dictionary] = []
var patch_around := 0.0
var patch_across := 0.0
var reanchor_cost := {}

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(1500.0).timeout.connect(func(): _fail("flight landing walk fixture timed out"))

func run() -> void:
	fixture = "res://.godot/flight_landing_walk_%d" % OS.get_process_id()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalFlightLanding"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	report_only = "--report-only" in OS.get_cmdline_user_args()
	deadline = Time.get_ticks_msec() + 1450000
	game = await _create_game("--no-vegetation" in OS.get_cmdline_user_args())
	if game == null: return
	explorer = game.get_node_or_null("Explorer") as CharacterBody3D
	surface = game.get_node_or_null("Terrain3DGameplay")
	if explorer == null or surface == null: _fail("missing explorer or Terrain3D gameplay"); return
	explorer.set("EnableSwimming", false)
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	if not _choose_patch(): return
	var cases := _arg_string("--cases=", "control,far,high,input").split(",", false)
	for name in cases:
		var landed := false
		match name:
			"control": landed = await _land_control()
			"far": landed = await _land_scripted(patch_around - 20000.0, 300.0, "far")
			"high": landed = await _land_scripted(patch_around, 4000.0, "high")
			"input": landed = await _land_input()
			_: _fail("unknown case %s" % name); return
		if not landed: return
		if not await _walk_case(name): return
		if name == "control" and not await _measure_reanchor_cost(): return
	_verdict()
	if failed: return # _fail already finishes the run
	print("FLIGHT_LANDING_WALK=PASS")
	_finish(0)

func _create_game(no_vegetation: bool) -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: _fail("launcher lacks terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", 2)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("runtime surface definition preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", fixture.path_join("surface_tuning.tres"))
	var disabled := ["RingVolumetricClouds", "LocalVolumetricCloudRenderer", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "TerrainMinimap", "CertificationMonitor"]
	if no_vegetation: disabled.append_array(["BiomeDecorations", "StandingGrass"])
	for name in disabled:
		var node: Node = next.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_fail("startup readiness deadline")
	return null

## P: dry, fairly flat ground 10-15.5 km past an around sector centre and within 14 km of an across sector centre, so starts 20-40 km
## earlier along the ring (same across) share or cross sectors as each case intends.
func _choose_patch() -> bool:
	var lowest := INF; var highest := -INF
	for sector: int in [100, 37, 211, 452, 733]:
		for across_step in 15:
			var across := 163840.0 + (across_step - 7) * 2000.0
			for step in 23:
				var around := SECTOR * sector + 10000.0 + step * 250.0
				var h := float(surface.call("SampleHeight", around, across))
				lowest = minf(lowest, h); highest = maxf(highest, h)
				if h <= 3.0: continue
				var rise := 0.0
				for d: Vector2 in [Vector2(40, 0), Vector2(-40, 0), Vector2(0, 40), Vector2(0, -40), Vector2(100, 0), Vector2(-100, 0), Vector2(0, 100), Vector2(0, -100)]:
					rise = maxf(rise, absf(float(surface.call("SampleHeight", around + d.x, across + d.y)) - h) / d.length())
				if rise < .2:
					patch_around = around; patch_across = across
					print("FLIGHT_LANDING_WALK_PATCH around=%.1f across=%.1f height=%.2f max_grade=%.3f" % [around, across, h, rise])
					return true
	_fail("no dry, gentle patch found (heights %.1f..%.1f m)" % [lowest, highest])
	return false

func _ground(around: float) -> float:
	return float(surface.call("SampleHeight", around, patch_across))

func _land_control() -> bool:
	explorer.call("TeleportToCanonicalMeters", patch_around, _ground(patch_around) + 1.1, patch_across, 0.0, 0.0, false)
	return await _settle_grounded("control")

func _land_scripted(start_around: float, altitude: float, name: String) -> bool:
	explorer.call("TeleportToCanonicalMeters", start_around, _ground(start_around) + altitude, patch_across, 0.0, 0.0, true)
	await _physics_frames(10)
	if explorer.call("BeginScriptedFlight") != true: _fail("scripted flight unavailable"); return false
	var anchors_before := int(explorer.get("ScriptedFlightAnchorCount"))
	while Time.get_ticks_msec() < deadline:
		var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
		var remaining := patch_around - float(pose["around_m"])
		var above := float(pose["vertical_m"]) - _ground(float(pose["around_m"]))
		var horizontal := clampf(remaining * 2.0, -2500.0, 2500.0) if absf(remaining) > .5 else 0.0
		var vertical := 0.0
		if absf(remaining) < 50.0:
			vertical = -clampf(above * 1.5, 0.0, 600.0) if above > 2.0 else 0.0
		explorer.call("SetScriptedFlightCommand", Vector3(horizontal, vertical, 0.0), 0.0, 0.0)
		if absf(remaining) <= .5 and above <= 2.0: break
		await physics_frame
	explorer.call("SetScriptedFlightCommand", Vector3.ZERO, 0.0, 0.0)
	var crossed := int(explorer.get("ScriptedFlightAnchorCount")) - anchors_before
	explorer.call("EndScriptedFlight")
	await _physics_frames(2)
	if crossed != 0: print("FLIGHT_LANDING_WALK_NOTE %s crossed %d sector boundaries" % [name, crossed])
	await _tap(KEY_V)
	return await _settle_grounded(name)

func _land_input() -> bool:
	# 40 km out, so the 3.6 km/s leg crosses a collision-sector boundary (a real flight re-anchors in the air) before reaching P.
	var start_around := patch_around - 40000.0
	explorer.call("TeleportToCanonicalMeters", start_around, _ground(start_around) + 400.0, patch_across, 0.0, 0.0, true)
	await _physics_frames(10)
	await _tap(KEY_4)
	_set_key(KEY_SHIFT, true)
	if not await _noclip_forward_until(1500.0): return false
	_set_key(KEY_SHIFT, false)
	await _tap(KEY_3)
	if not await _noclip_forward_until(20.0): return false
	await _tap(KEY_1)
	if not await _noclip_forward_until(.5): return false
	_set_key(KEY_Q, true)
	while Time.get_ticks_msec() < deadline:
		await physics_frame
		var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
		if float(pose["vertical_m"]) - _ground(float(pose["around_m"])) <= 2.0: break
	_set_key(KEY_Q, false)
	var pose_end: Dictionary = explorer.call("GetCanonicalPoseMeters")
	print("FLIGHT_LANDING_WALK_NOTE input landed at around=%.1f across=%.1f" % [float(pose_end["around_m"]), float(pose_end["across_m"])])
	await _tap(KEY_V)
	return await _settle_grounded("input")

## Holds W or S (noclip, yaw 0 flies +around) until the body is within `tolerance` of P along the ring.
func _noclip_forward_until(tolerance: float) -> bool:
	var started := Time.get_ticks_msec()
	while Time.get_ticks_msec() < deadline and Time.get_ticks_msec() - started < 90000:
		var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
		var remaining := patch_around - float(pose["around_m"])
		if absf(remaining) <= tolerance:
			_set_key(KEY_W, false); _set_key(KEY_S, false)
			await _physics_frames(5)
			return true
		_set_key(KEY_W, remaining > 0.0); _set_key(KEY_S, remaining < 0.0)
		await physics_frame
	_set_key(KEY_W, false); _set_key(KEY_S, false)
	_fail("input flight did not reach the patch")
	return false

func _settle_grounded(name: String) -> bool:
	var stable := 0
	var started := Time.get_ticks_msec()
	while Time.get_ticks_msec() < deadline and Time.get_ticks_msec() - started < 60000:
		await physics_frame
		stable = stable + 1 if explorer.is_on_floor() and not bool(explorer.get("IsSpawnTerrainSettling")) else 0
		if stable >= 30:
			await _physics_frames(30)
			return true
	_fail("%s: body never settled on the ground" % name)
	return false

## Cost of one re-anchor while standing: hover in place (flight teleport), rise 1 m in scripted flight, then end it (a forced re-anchor
## that moves the origin by about 1 m). Synchronous cost from the controller, whole-frame wall time around it from the process frames.
func _measure_reanchor_cost() -> bool:
	var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
	explorer.call("TeleportToCanonicalMeters", float(pose["around_m"]), float(pose["vertical_m"]) + .5, float(pose["across_m"]), 0.0, 0.0, true)
	await _physics_frames(120)
	if explorer.call("BeginScriptedFlight") != true: _fail("scripted flight unavailable"); return false
	explorer.call("SetScriptedFlightCommand", Vector3(0, 2, 0), 0.0, 0.0)
	await _physics_frames(30)
	explorer.call("SetScriptedFlightCommand", Vector3.ZERO, 0.0, 0.0)
	await _frames(90)
	var before: Array[float] = []
	var last := Time.get_ticks_usec()
	for i in 90:
		await process_frame
		var now := Time.get_ticks_usec(); before.append((now - last) / 1000.0); last = now
	var count_before := int(explorer.get("FloatingOriginReanchorCount"))
	explorer.call("EndScriptedFlight")
	var sync_ms := float(explorer.get("LastFloatingOriginReanchorMilliseconds"))
	var count_after := int(explorer.get("FloatingOriginReanchorCount"))
	var after: Array[float] = []
	for i in 10:
		await process_frame
		var now := Time.get_ticks_usec(); after.append((now - last) / 1000.0); last = now
	before.sort()
	reanchor_cost = {"reanchors": count_after - count_before, "sync_ms": sync_ms, "frame_ms_median_before": before[before.size() / 2], "frame_ms_p95_before": before[int(before.size() * .95)],
		"frame_ms_max_before": before[before.size() - 1], "frame_ms_after": after}
	print("FLIGHT_LANDING_WALK_REANCHOR_COST ", JSON.stringify(reanchor_cost))
	await _tap(KEY_V)
	return await _settle_grounded("reanchor_cost")

## Legs: +around, +across, -around, -across at each speed, from wherever the body stands.
func _walk_case(name: String) -> bool:
	var row := {"case": name, "anchor_distance_m": explorer.global_position.length(), "legs": []}
	var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
	row["start_around"] = float(pose["around_m"]); row["start_vertical_above_ground"] = float(pose["vertical_m"]) - _ground(float(pose["around_m"]))
	var totals := {"frames": 0, "zero_frames": 0, "longest_zero_run": 0, "floor_dropouts": 0, "wall_contacts": 0, "coverage_wait_frames": 0, "progress_ratio_min": 1.0}
	var blockers := {}
	for speed: float in [WALK_SPEED, SPRINT_SPEED]:
		explorer.set("MoveSpeed", speed); explorer.set("SprintSpeed", speed)
		var distance := 24.0 if speed == WALK_SPEED else 70.0
		for direction: Vector2 in [Vector2(1, 0), Vector2(0, 1), Vector2(-1, 0), Vector2(0, -1)]:
			var leg: Dictionary = await _leg(direction, speed, distance, blockers)
			if leg.is_empty(): return false
			row["legs"].append(leg)
			for key in ["frames", "zero_frames", "floor_dropouts", "wall_contacts", "coverage_wait_frames"]: totals[key] += int(leg[key])
			totals["longest_zero_run"] = maxi(int(totals["longest_zero_run"]), int(leg["longest_zero_run"]))
			totals["progress_ratio_min"] = minf(float(totals["progress_ratio_min"]), float(leg["progress_ratio"]))
	row.merge(totals)
	row["blockers"] = blockers
	row["anchor_distance_end_m"] = explorer.global_position.length()
	results.append(row)
	print("FLIGHT_LANDING_WALK_CASE ", JSON.stringify(row))
	return true

func _leg(direction: Vector2, speed: float, distance: float, blockers: Dictionary) -> Dictionary:
	var start: Dictionary = explorer.call("GetCanonicalPoseMeters")
	# GDScript floats are doubles, Vector2 is single precision: positions are kept relative to the leg start (around is ~1e7 m).
	var origin_around := float(start["around_m"]); var origin_across := float(start["across_m"])
	var origin := Vector2.ZERO
	var previous := origin
	var frames := 0; var zero_frames := 0; var zero_run := 0; var longest := 0; var dropouts := 0; var walls := 0; var waits := 0
	var max_wall := cos(deg_to_rad(float(explorer.get("MaxWalkableSlopeDegrees"))))
	var started := Time.get_ticks_msec()
	var limit_ms := int(distance / speed * 1000.0 * 3.0) + 4000
	_set_motion(direction)
	await _physics_frames(int(ceil(speed / 45.0 * 60.0)) + 2) # past the ground acceleration ramp
	while Time.get_ticks_msec() < deadline and Time.get_ticks_msec() - started < limit_ms:
		await physics_frame
		var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
		var here := Vector2(float(pose["around_m"]) - origin_around, float(pose["across_m"]) - origin_across)
		var step := here.distance_to(previous)
		previous = here
		frames += 1
		var velocity: Vector3 = explorer.get("CanonicalVelocity")
		var state := ""
		if frames % 6 == 0 or step < .002:
			state = str(((surface.call("GetSurfaceDiagnostics") as Dictionary).get("streaming", {}) as Dictionary).get("state", ""))
		if state == "waiting_for_coverage": waits += 1
		if step < .002:
			zero_frames += 1; zero_run += 1; longest = maxi(longest, zero_run)
		else:
			zero_run = 0
		if not explorer.is_on_floor(): dropouts += 1
		var up: Vector3 = explorer.up_direction
		for i in explorer.get_slide_collision_count():
			var hit := explorer.get_slide_collision(i)
			if hit.get_normal().dot(up) < max_wall:
				walls += 1
				var collider: Object = hit.get_collider()
				var key: String = "?"
				if collider is Node: key = String((collider as Node).name).get_slice("_", 0)
				blockers[key] = int(blockers.get(key, 0)) + 1
		if (here - origin).dot(direction) >= distance: break
	_stop_motion()
	await _physics_frames(20)
	var progress := (previous - origin).dot(direction)
	var leg := {"direction": "%d,%d" % [direction.x, direction.y], "speed": speed, "frames": frames, "zero_frames": zero_frames, "longest_zero_run": longest,
		"floor_dropouts": dropouts, "wall_contacts": walls, "coverage_wait_frames": waits, "progress_m": progress, "progress_ratio": progress / maxf(1.0, distance)}
	return leg

func _verdict() -> void:
	var control: Dictionary = {}
	for row in results:
		if row["case"] == "control": control = row
	var problems: Array[String] = []
	for row in results:
		var name: String = row["case"]
		if name != "control" and float(row["anchor_distance_m"]) > REANCHOR_LIMIT:
			problems.append("%s: body %.0f m from the floating origin after landing (limit %.0f)" % [name, float(row["anchor_distance_m"]), REANCHOR_LIMIT])
		if int(row["longest_zero_run"]) > ZERO_RUN_LIMIT:
			problems.append("%s: %d-frame zero-motion run with input held" % [name, int(row["longest_zero_run"])])
		if float(row["progress_ratio_min"]) < .98:
			problems.append("%s: a leg reached only %.0f%% of its distance" % [name, 100.0 * float(row["progress_ratio_min"])])
		if not control.is_empty() and name != "control":
			for key in ["zero_frames", "floor_dropouts", "wall_contacts"]:
				var allowed := int(control[key]) * 2 + 6
				if int(row[key]) > allowed: problems.append("%s: %s %d vs control %d" % [name, key, int(row[key]), int(control[key])])
	# The report is scratch (the CASE lines in the log carry the numbers); --keep-report writes it next to the profile for a later look.
	var path := ProjectSettings.globalize_path("user://flight_landing_walk_report_%d.json" % OS.get_process_id())
	var file := FileAccess.open(path, FileAccess.WRITE) if "--keep-report" in OS.get_cmdline_user_args() else null
	if file != null:
		file.store_string(JSON.stringify({"patch_around": patch_around, "patch_across": patch_across, "reanchor_cost": reanchor_cost, "cases": results, "problems": problems}, "\t")); file.close()
		print("FLIGHT_LANDING_WALK_REPORT=", path)
	for problem in problems: print("FLIGHT_LANDING_WALK_PROBLEM ", problem)
	if not problems.is_empty() and not report_only: _fail("; ".join(problems))

## Frees the game first (it autosaves into the fixture directory on exit), then deletes the fixture directory and quits.
func _finish(code: int) -> void:
	_stop_motion()
	if is_instance_valid(game): game.queue_free()
	await _frames(3)
	_remove_tree(ProjectSettings.globalize_path(fixture))
	quit(code)

func _remove_tree(path: String) -> void:
	if fixture.is_empty() or not DirAccess.dir_exists_absolute(path): return
	var dir := DirAccess.open(path)
	if dir == null: return
	for child in dir.get_directories(): _remove_tree(path.path_join(child))
	for child in dir.get_files(): DirAccess.remove_absolute(path.path_join(child))
	DirAccess.remove_absolute(path)

func _set_motion(direction: Vector2) -> void:
	_set_key(KEY_W, direction.x > .01); _set_key(KEY_S, direction.x < -.01)
	# At yaw 0, W advances positive around; A is positive across (inward-up basis).
	_set_key(KEY_A, direction.y > .01); _set_key(KEY_D, direction.y < -.01)

func _stop_motion() -> void:
	for key in [KEY_W, KEY_A, KEY_S, KEY_D, KEY_SHIFT, KEY_Q]: _set_key(key, false)

func _tap(key: Key) -> void:
	_set_key(key, true); await process_frame; _set_key(key, false)

func _set_key(key: Key, down: bool) -> void:
	var event := InputEventKey.new(); event.keycode = key; event.physical_keycode = key; event.pressed = down; Input.parse_input_event(event)

func _physics_frames(count: int) -> void:
	for ignored in count: await physics_frame
func _frames(count: int) -> void:
	for ignored in count: await process_frame
func _arg_string(prefix: String, fallback: String) -> String:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with(prefix): return argument.trim_prefix(prefix)
	return fallback

func _fail(message: String) -> void:
	if failed: return
	failed = true; _stop_motion()
	push_error("FLIGHT_LANDING_WALK=FAIL " + message)
	_finish(1)
