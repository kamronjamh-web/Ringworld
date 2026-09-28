extends SceneTree

## Long-path regression fixture for runtime Terrain3D generation.  It observes
## canonical movement once per physics frame so coverage stalls and recenter
## jumps cannot be hidden by render-frame timing.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const ZERO_MOTION_LIMIT := 8
var fixture := ""
var deadline := 0
var game: Node
var explorer: Node
var surface: Node
var events: Array[Dictionary] = []
var failed := false

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(720.0).timeout.connect(func(): _fail("movement fixture timed out"))

func run() -> void:
	fixture = "res://.godot/terrain-generation-tests/movement-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalMovement"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	deadline = Time.get_ticks_msec() + 720000
	game = await _create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	if explorer == null or surface == null: _fail("missing explorer or Terrain3D gameplay"); return
	explorer.set("EnableSwimming", false)
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	explorer.call("SetPresentationView", 0.0, 0.0)
	var override_speed := _arg_float("--speed=", NAN)
	var distance := _arg_float("--distance=", 1024.0)
	var speeds: Array[float] = [12.0, 60.0]
	if not is_nan(override_speed): speeds.assign([override_speed])
	for speed in speeds:
		explorer.set("MoveSpeed", speed); explorer.set("SprintSpeed", speed)
		if not await _handoff_boundary_pause(speed): return
		if not await _reset_route(): return
		if not await _travel("around_plus_%s" % speed, Vector2(1, 0), speed, distance): return
		if not await _pause("pause_%s" % speed): return
		if not await _travel("around_reverse_%s" % speed, Vector2(-1, 0), speed, distance): return
		if not await _reset_route(): return
		if not await _travel("across_plus_%s" % speed, Vector2(0, 1), speed, distance): return
		if not await _reset_route(): return
		if not await _travel("diagonal_plus_%s" % speed, Vector2(1, 1).normalized(), speed, distance): return
		if not await _travel("diagonal_reverse_%s" % speed, Vector2(-1, -1).normalized(), speed, distance): return
	_report()
	game.queue_free(); await process_frame; quit(0)

func _create_game() -> Node:
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
	for name in ["BiomeDecorations", "StandingGrass", "RingVolumetricClouds", "LocalVolumetricCloudRenderer", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "WaterFftController", "TerrainMinimap", "CertificationMonitor"]:
		var node: Node = next.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_fail("startup readiness deadline")
	return null

func _reset_route() -> bool:
	var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
	var around := 32.0
	var across := 159032.0
	var height := float(surface.call("SampleHeight", around, across)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", around, height, across)
	explorer.call("SetPresentationView", 0.0, 0.0)
	var stable_frames := 0
	while Time.get_ticks_msec() < deadline:
		await physics_frame
		var ready: Dictionary = surface.call("GetSurfaceDiagnostics")
		var player_height := float(ready.get("player_height", NAN))
		var surface_height := float(ready.get("surface_height", NAN))
		var stable := bool(explorer.call("is_on_floor")) and not is_nan(player_height) and not is_nan(surface_height) and absf(player_height - surface_height) <= .25
		stable_frames = stable_frames + 1 if stable else 0
		if stable_frames >= 20: return true
	_fail("route reset did not reach stable grounded Terrain3D coverage")
	return false

func _handoff_boundary_pause(speed: float) -> bool:
	if not await _reset_route(): return false
	var before: Dictionary = surface.call("GetSurfaceDiagnostics")
	var anchor_before := float(before.get("frame_around", NAN))
	if not await _travel("handoff_cross_%s" % speed, Vector2(1, 0), speed, 140.0): return false
	if not await _pause("handoff_pause_%s" % speed): return false
	var after: Dictionary = surface.call("GetSurfaceDiagnostics")
	var anchor_after := float(after.get("frame_around", NAN))
	if is_nan(anchor_before) or is_nan(anchor_after) or is_equal_approx(anchor_before, anchor_after):
		_fail("handoff boundary travel did not commit a new active anchor")
		return false
	events.append({"phase": "handoff_boundary_%s" % speed, "anchor_before": anchor_before, "anchor_after": anchor_after, "streaming": after.get("streaming", {})})
	return true

func _travel(name: String, direction: Vector2, speed: float, distance: float) -> bool:
	var start: Dictionary = surface.call("GetSurfaceDiagnostics")
	var previous_around := float(start.get("player_around", 0.0))
	var previous_across := float(start.get("player_across", 0.0))
	var origin_around := previous_around
	var origin_across := previous_across
	var max_jump := 0.0
	var max_velocity_delta_error := 0.0
	var zero_run := 0
	var max_zero_run := 0
	var anchor_changes := 0
	var support_samples := 0
	var support_misses := 0
	var previous_anchor := "%s/%s" % [start.get("frame_around", ""), start.get("frame_across", "")]
	_set_motion(direction)
	while Time.get_ticks_msec() < deadline:
		await physics_frame
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		var current_around := float(diag.get("player_around", 0.0))
		var current_across := float(diag.get("player_across", 0.0))
		var delta_around := current_around - previous_around
		var delta_across := current_across - previous_across
		var horizontal_jump := sqrt(delta_around * delta_around + delta_across * delta_across)
		max_jump = maxf(max_jump, horizontal_jump)
		var physics_delta := 1.0 / float(Engine.physics_ticks_per_second)
		if horizontal_jump > speed * physics_delta + .5:
			_stop_motion(); _fail("canonical horizontal jump %.3f m during %s" % [horizontal_jump, name]); return false
		var velocity: Vector3 = explorer.get("CanonicalVelocity")
		var horizontal_velocity := Vector2(velocity.x, velocity.z)
		if horizontal_velocity.length() <= .01 and horizontal_jump <= .002:
			zero_run += 1; max_zero_run = maxi(max_zero_run, zero_run)
		else:
			zero_run = 0
		if zero_run > ZERO_MOTION_LIMIT:
			_stop_motion(); _fail("unintended zero-motion episode (%s frames) during %s; diag=%s" % [zero_run, name, diag]); return false
		var dot_progress := (current_around - origin_around) * direction.x + (current_across - origin_across) * direction.y
		var expected_step := horizontal_velocity.length() * (1.0 / 60.0)
		max_velocity_delta_error = maxf(max_velocity_delta_error, absf(horizontal_jump - expected_step))
		var up: Vector3 = explorer.global_basis.y.normalized()
		var support_query := PhysicsRayQueryParameters3D.create(explorer.global_position + up * 2.0, explorer.global_position - up * 8.0, 1, [explorer.get_rid()])
		if explorer.get_world_3d().direct_space_state.intersect_ray(support_query).is_empty(): support_misses += 1
		support_samples += 1
		var anchor := "%s/%s" % [diag.get("frame_around", ""), diag.get("frame_across", "")]
		if anchor != previous_anchor: anchor_changes += 1; previous_anchor = anchor
		previous_around = current_around; previous_across = current_across
		if dot_progress >= distance: break
	_stop_motion()
	var end: Dictionary = surface.call("GetSurfaceDiagnostics")
	var final_progress := (previous_around - origin_around) * direction.x + (previous_across - origin_across) * direction.y
	events.append({"phase": name, "speed": speed, "distance": distance, "actual_progress": final_progress, "max_horizontal_jump": max_jump, "max_velocity_delta_error": max_velocity_delta_error, "max_zero_motion_frames": max_zero_run, "anchor_changes": anchor_changes, "coverage_waits": end.get("coverage_waits", -1), "request_revision": end.get("request_revision", -1), "committed_revision": end.get("committed_revision", -1), "support_samples": support_samples, "support_misses": support_misses, "streaming": end.get("streaming", {})})
	if final_progress < distance:
		_fail("route failed to complete %s" % name); return false
	return true

func _pause(name: String) -> bool:
	_stop_motion()
	var settled := 0
	while Time.get_ticks_msec() < deadline and settled < 20:
		await physics_frame
		var velocity: Vector3 = explorer.get("CanonicalVelocity")
		settled = settled + 1 if Vector2(velocity.x, velocity.z).length() <= .05 else 0
	if settled < 20: _fail("pause did not settle horizontal velocity"); return false
	var start: Dictionary = surface.call("GetSurfaceDiagnostics")
	var point_around := float(start.get("player_around", 0.0))
	var point_across := float(start.get("player_across", 0.0))
	for ignored in 30:
		await physics_frame
	var end: Dictionary = surface.call("GetSurfaceDiagnostics")
	var delta_around := float(end.get("player_around", 0.0)) - point_around
	var delta_across := float(end.get("player_across", 0.0)) - point_across
	var drift := sqrt(delta_around * delta_around + delta_across * delta_across)
	if drift > .25: _fail("pause drifted %.3f m" % drift); return false
	events.append({"phase": name, "pause_drift": drift, "streaming": end.get("streaming", {})})
	return true

func _set_motion(direction: Vector2) -> void:
	_set_key(KEY_W, direction.x > .01); _set_key(KEY_S, direction.x < -.01)
	# At yaw 0, W advances positive around. The controller's inward-up basis
	# maps D to negative across and A to positive across.
	_set_key(KEY_A, direction.y > .01); _set_key(KEY_D, direction.y < -.01)

func _stop_motion() -> void:
	for key in [KEY_W, KEY_A, KEY_S, KEY_D]: _set_key(key, false)

func _set_key(key: Key, down: bool) -> void:
	var event := InputEventKey.new(); event.keycode = key; event.pressed = down; Input.parse_input_event(event)

func _physics_frames(count: int) -> void:
	for ignored in count: await physics_frame
func _frames(count: int) -> void:
	for ignored in count: await process_frame
func _arg_float(prefix: String, fallback: float) -> float:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with(prefix): return float(argument.trim_prefix(prefix))
	return fallback

func _report() -> void:
	var path := ProjectSettings.globalize_path(fixture.path_join("movement-test.json"))
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(JSON.stringify({"events": events, "final_surface": surface.call("GetSurfaceDiagnostics")}, "\t")); file.close()
	print("TERRAIN3D_MOVEMENT_TEST=", path)

func _fail(message: String) -> void:
	if failed: return
	failed = true; _stop_motion()
	if is_instance_valid(surface): print("TERRAIN3D_MOVEMENT_FAILURE_STATE=", JSON.stringify(surface.call("GetSurfaceDiagnostics")))
	push_error("terrain3d_gameplay_movement_test: " + message); quit(1)
