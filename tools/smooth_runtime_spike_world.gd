extends SceneTree

## Voxel/cave spike B1 world harness. Run WINDOWED for captures (a headless run has no framebuffer); quiescence timing may run either way.
##   Godot_v4.8-dev7_mono_win64_console.exe --path . --script res://tools/smooth_runtime_spike_world.gd -- --mode=terrain3d|spike|gen16 --task=capture|quiescence --out=DIR [--smooth-runtime-generation-spike]
## mode=terrain3d  existing runtime-surface world (Terrain3D near terrain).
## mode=spike      same recipe on the smooth voxel backend, caves off (needs the --smooth-runtime-generation-spike user arg, which the launcher reads).
## mode=gen16      the stock Certified smooth-voxel world (gen-16), for the quiescence reference.
## task=capture    teleports to the SmoothRuntimeSpikePoses poses and saves <mode>_<pose>.png plus <mode>_report.json.
## task=quiescence times the smooth grid from world start to quiescence, then from a teleport to the main spire pad to quiescence.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const POSES := preload("res://tools/SmoothRuntimeSpikePoses.cs")
const SETTLE_FRAMES := 90

var mode := "spike"
var task := "capture"
var out_dir := "user://smooth_runtime_spike"
var game: Node
var explorer: Node
var grid: Node
var surface: Node
var finished := false
var report := {}
var problems: Array[String] = []

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(1700.0).timeout.connect(func(): _abort("harness timed out"))

func run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--mode="): mode = argument.trim_prefix("--mode=")
		elif argument.begins_with("--task="): task = argument.trim_prefix("--task=")
		elif argument.begins_with("--out="): out_dir = argument.trim_prefix("--out=")
	DirAccess.make_dir_recursive_absolute(out_dir)
	var fixture := "res://.godot/terrain-generation-tests/spike-%s-%s" % [mode, Time.get_ticks_usec()]
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	if mode != "gen16":
		ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
		ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalSpike"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	report["mode"] = mode
	report["task"] = task
	report["display"] = DisplayServer.get_name()
	report["spike_flag"] = OS.get_cmdline_user_args().has("--smooth-runtime-generation-spike")
	var poses_runner = POSES.new()
	var poses: Array = JSON.parse_string(poses_runner.call("GetPosesJson"))
	poses_runner = null
	report["poses"] = poses
	var started := Time.get_ticks_msec()
	game = await _create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	grid = game.get_node_or_null("SmoothTerrainGrid")
	surface = game.get_node_or_null("Terrain3DGameplay")
	report["has_smooth_grid"] = grid != null
	report["has_terrain3d"] = surface != null
	if explorer == null: _abort("no explorer"); return
	if mode != "terrain3d" and grid == null: _abort("no SmoothTerrainGrid in mode " + mode); return
	if mode == "terrain3d" and surface == null: _abort("no Terrain3DGameplay in mode terrain3d"); return
	if grid != null:
		report["grid_max_workers"] = grid.get("EffectiveMaxWorkers") if grid.get("EffectiveMaxWorkers") != null else null
		report["grid_radius_around"] = grid.get("RadiusAround")
	if task == "quiescence": await _quiescence(started)
	else: await _capture(poses)
	_finish()

func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: _abort("launcher lacks terrain backend selector"); return null
	if mode == "gen16":
		selector.select(0); selector.emit_signal("item_selected", 0)
	else:
		selector.select(2); selector.emit_signal("item_selected", 2)
		if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
			_abort("runtime surface definition preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if mode == "terrain3d" and tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", ("res://.godot/terrain-generation-tests/spike-tuning-%s" % Time.get_ticks_usec()) + ".tres")
	var monitor: Node = next.get_node_or_null("CertificationMonitor")
	if monitor != null: monitor.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	var waited := 0
	while waited < 60000:
		await process_frame
		waited += 1
		if mode == "terrain3d":
			var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
			if candidate != null and bool(candidate.call("EnsureInitialized")): return next
		elif next.get_node_or_null("SmoothTerrainGrid") != null and next.get_node_or_null("Explorer") != null:
			return next
	_abort("startup readiness deadline")
	return null

func _grid_quiescent() -> bool:
	return grid != null and bool(grid.get("IsQuiescent"))

# Waits for `stable` consecutive quiescent frames (the grid can be momentarily idle between scheduling passes). Returns elapsed ms, or -1 on timeout.
func _wait_quiescent(stable: int, timeout_ms: int) -> float:
	var start := Time.get_ticks_usec()
	var run_len := 0
	while (Time.get_ticks_usec() - start) / 1000 < timeout_ms:
		await process_frame
		if _grid_quiescent(): run_len += 1
		else: run_len = 0
		if run_len >= stable: return float(Time.get_ticks_usec() - start) / 1000.0 - float(stable) * 0.0
	return -1.0

func _quiescence(started_ms: int) -> void:
	var q := {}
	q["to_first_node_ready_ms"] = Time.get_ticks_msec() - started_ms
	var t_init := await _wait_quiescent(20, 900000)
	q["spawn_default_quiescent_ms"] = snappedf(t_init, 0.1)
	q["spawn_ready_render_chunks"] = grid.get("ReadyRenderCount")
	q["spawn_ready_collision_chunks"] = grid.get("ReadyCollisionCount")
	var pose: Dictionary = report["poses"][0]
	var ground: float = float(pose["ground"])
	if mode == "gen16":
		var helper = POSES.new()
		ground = float(helper.call("Gen16Height", int(pose["around"]), int(pose["across"])))
		helper = null
	q["main_pad_ground_m"] = ground
	explorer.call("TeleportToCanonicalMeters", float(pose["around"]), ground + 2.0, float(pose["across"]), float(pose["yaw"]), float(pose["pitch"]), true)
	await process_frame
	await process_frame
	var t_main := await _wait_quiescent(20, 900000)
	q["main_pad_teleport_quiescent_ms"] = snappedf(t_main, 0.1)
	q["main_ready_render_chunks"] = grid.get("ReadyRenderCount")
	q["main_ready_collision_chunks"] = grid.get("ReadyCollisionCount")
	# Second, short hop (~300 m) to show incremental streaming cost.
	explorer.call("TeleportToCanonicalMeters", float(pose["around"]) + 300.0, ground + 40.0, float(pose["across"]), float(pose["yaw"]), float(pose["pitch"]), true)
	await process_frame
	await process_frame
	var t_hop := await _wait_quiescent(20, 900000)
	q["hop_300m_quiescent_ms"] = snappedf(t_hop, 0.1)
	report["quiescence"] = q
	print("SPIKE_QUIESCENCE ", JSON.stringify(q))

func _capture(poses: Array) -> void:
	explorer.set("EnableSwimming", true)
	_freeze_time()
	if mode == "terrain3d":
		await _wait_global()
	else:
		var t0 := await _wait_quiescent(20, 900000)
		report["initial_quiescent_ms"] = snappedf(t0, 0.1)
	var shots := []
	for pose in poses:
		var label: String = pose["name"]
		_freeze_time()
		var fl: bool = bool(pose["flight"])
		explorer.call("TeleportToCanonicalMeters", float(pose["around"]), float(pose["vertical"]), float(pose["across"]), float(pose["yaw"]), float(pose["pitch"]), fl)
		await _frames(5)
		if mode != "terrain3d": await _wait_quiescent(20, 600000)
		else: await _frames(240)
		if not fl:
			var landing := 0
			while landing < 600 and not bool(explorer.call("is_on_floor")):
				landing += 1
				await physics_frame
		await _wait_global()
		await _frames(SETTLE_FRAMES)
		if mode != "terrain3d": await _wait_quiescent(10, 120000)
		await _wait_global()
		var frame_ms := await _frame_ms(60)
		var image := root.get_texture().get_image()
		var path := out_dir.path_join("%s_%s.png" % [mode, label])
		var err := image.save_png(path)
		if err != OK: problems.append("could not write " + path)
		var info := {"name": label, "file": path, "median_frame_ms": snappedf(frame_ms, 0.01), "on_floor": bool(explorer.call("is_on_floor")), "flight": fl}
		if mode == "terrain3d" and surface != null:
			var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
			info["player_height"] = diag.get("player_height", null)
		shots.append(info)
		print("SPIKE_CAPTURE ", JSON.stringify(info))
	report["shots"] = shots

func _freeze_time() -> void:
	# Same sun for every shot: the clock would otherwise drift into night during a long capture run.
	var cycle: Node = game.get_node_or_null("RingDayNightCycle")
	if cycle == null: return
	cycle.set("Paused", true)
	cycle.call("SetCycleFraction", 0.02)

func _frame_ms(count: int) -> float:
	var times := []
	var prev := Time.get_ticks_usec()
	for i in count:
		await process_frame
		var now := Time.get_ticks_usec()
		times.append(float(now - prev) / 1000.0)
		prev = now
	times.sort()
	return float(times[times.size() / 2])

func _wait_global() -> void:
	var global_lod: Node = game.get_node_or_null("GlobalTerrainLod3") if is_instance_valid(game) else null
	if global_lod == null: return
	var start := Time.get_ticks_msec()
	while int(global_lod.get("PendingInitialSegmentCount")) > 0 and Time.get_ticks_msec() - start < 180000:
		await process_frame

func _frames(count: int) -> void:
	for ignored in count: await process_frame

func _finish() -> void:
	if finished: return
	finished = true
	report["problems"] = problems
	var path := out_dir.path_join("%s_%s_report.json" % [mode, task])
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(report, "\t")); file.close()
	print("SPIKE_WORLD=", "PASS" if problems.is_empty() else "FAIL", " mode=", mode, " task=", task)
	quit(0 if problems.is_empty() else 1)

func _abort(message: String) -> void:
	if finished: return
	finished = true
	problems.append(message)
	push_error("SPIKE_WORLD abort: " + message)
	print("SPIKE_WORLD=FAIL")
	quit(1)
