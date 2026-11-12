extends SceneTree
## L0 load-time harness (docs/load-time-plan.md): one world load with per-stage timestamps, written to <out>/run.json.
## Run through tools/load_time_benchmark.ps1 (isolated APPDATA, GPU queue). The script refuses to run on the real roaming profile.
##
##   --scenario=new|new-rivers|reload   new: certified launcher world (new-rivers pins generation 1 with the water.rivers review feature); reload: saved slot --slot (default 1)
##   --out=<absolute dir>               run.json goes here
##   --vsync=on|off                     applied before the load (default off); the loader report records the state it saw
##   --post-seconds=10                  seconds sampled after entry (pipeline compiles, frame times) before shutdown

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const WORLD_LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const LOAD_TIMEOUT_MS := 300000

var _args := {}
var _loader: Node
var _game: Node
var _failure := ""
var _script_start_ms := 0

func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			if eq > 0:
				_args[text.substr(2, eq - 2)] = text.substr(eq + 1)
			else:
				_args[text.substr(2)] = "1"
	call_deferred("_run")

func _run() -> void:
	_script_start_ms = Time.get_ticks_msec()
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming):
		push_error("LOAD_BENCHMARK=FAIL user:// is the real roaming profile; use tools/load_time_benchmark.ps1")
		quit(2)
		return
	var scenario := String(_args.get("scenario", "new"))
	var out_dir := String(_args.get("out", ""))
	if out_dir.is_empty():
		push_error("LOAD_BENCHMARK=FAIL --out is required")
		quit(2)
		return
	DirAccess.make_dir_recursive_absolute(out_dir)
	var vsync_on := String(_args.get("vsync", "off")) == "on"
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED if vsync_on else DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var session := root.get_node_or_null("WorldSession")
	var slot := int(_args.get("slot", 1))
	if session == null or not session.select_slot(slot):
		push_error("LOAD_BENCHMARK=FAIL isolated world slot unavailable")
		quit(3)
		return
	var open_msec := Time.get_ticks_msec()
	if scenario == "reload":
		var metadata: Dictionary = session.get_slot_metadata(slot)
		var settings: Dictionary = metadata.get("generation_settings", {})
		session.request_load_selected_world()
		_loader = WORLD_LOADING.ensure(root)
		_connect_loader()
		_loader.open_saved_world({"world_title": str(metadata.get("name", "World")),
			"seed": int(settings.get("terrain_seed", 1337)),
			"radius_m": float(settings.get("radius_km", 5000.0)) * 1000.0,
			"circumference_m": TAU * float(settings.get("radius_km", 5000.0)) * 1000.0,
			"width_m": float(settings.get("ring_width_km", 318.0)) * 1000.0, "is_existing": true})
	else:
		# Same launch as world_loading_smoke_test; new-rivers sets generation 1 (latest frozen, the one that takes review features) and water.rivers on the launcher.
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		current_scene = launcher
		launcher.call("_on_certified_preset_pressed")
		launcher.get("terrain_backend_selector").select(1)
		launcher.call("_on_terrain_backend_selected", 1)
		if scenario == "new-rivers":
			launcher.set("_selected_world_generation", 1)
			launcher.set("_selected_review_features", PackedStringArray(["water.rivers"]))
		print("LOAD_BENCHMARK launcher generation=%s review=%s" % [launcher.get("_selected_world_generation"), launcher.get("_selected_review_features")])
		open_msec = Time.get_ticks_msec()
		launcher.call("_start_game")
		_loader = root.get_node_or_null("RingWorldLoading")
		if _loader == null:
			push_error("LOAD_BENCHMARK=FAIL loading screen did not appear with the launch request")
			quit(1)
			return
		_connect_loader()
	var deadline := Time.get_ticks_msec() + LOAD_TIMEOUT_MS
	while _game == null and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	var result := {"scenario": scenario, "vsync_requested": "on" if vsync_on else "off",
		"engine_ms_at_script_start": _script_start_ms, "engine_ms_at_open": open_msec, "engine_ms_at_ready": Time.get_ticks_msec(),
		"open_to_ready_ms": Time.get_ticks_msec() - open_msec, "report": _loader.get_diagnostics()}
	if _game == null:
		result["error"] = _failure if not _failure.is_empty() else "timed out"
		_write(out_dir, result)
		push_error("LOAD_BENCHMARK=FAIL " + str(result["error"]))
		quit(4)
		return
	result["post_entry"] = await _sample_post_entry(float(_args.get("post-seconds", 10)))
	_write(out_dir, result)
	await _shutdown()
	print("LOAD_BENCHMARK=PASS scenario=%s open_to_ready_ms=%d" % [scenario, int(result["open_to_ready_ms"])])
	quit(0)

func _connect_loader() -> void:
	_loader.world_ready.connect(func(world: Node): _game = world)
	_loader.loading_failed.connect(func(message: String): _failure = message)

## Pipeline compilations and frame times for the seconds after the reveal (hitches the loading screen no longer covers).
func _sample_post_entry(seconds: float) -> Dictionary:
	var before: Dictionary = _loader.call("_pipeline_counts")
	var frames := PackedFloat64Array()
	var last := Time.get_ticks_usec()
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame
		var now := Time.get_ticks_usec()
		frames.append(float(now - last) / 1000.0)
		last = now
	var after: Dictionary = _loader.call("_pipeline_counts")
	var compiles := {}
	for key: String in after:
		compiles[key] = int(after[key]) - int(before.get(key, 0))
	frames.sort()
	var count := frames.size()
	return {"seconds": seconds, "frames": count, "pipeline_compilations": compiles,
		"frame_p50_ms": frames[count / 2] if count > 0 else 0.0, "frame_p95_ms": frames[mini(count - 1, int(count * .95))] if count > 0 else 0.0,
		"frame_max_ms": frames[count - 1] if count > 0 else 0.0}

func _write(out_dir: String, result: Dictionary) -> void:
	var file := FileAccess.open(out_dir.path_join("run.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(result, "  "))
		file.close()
	print("LOAD_BENCHMARK_FILE=", out_dir.path_join("run.json"))

## The smoke test's fixture shutdown: no save is written and background workers drain before exit.
func _shutdown() -> void:
	var surface: Node = _game.get_node_or_null("Terrain3DGameplay")
	var saves: Node = _game.get_node("WorldSaveManager")
	saves.call("CancelWorldLoad")
	if is_instance_valid(surface):
		surface.call("CancelStartup", "Load benchmark complete.")
	var owners: Array[Node] = []
	for child: Node in _game.get_children():
		if child.has_method("BeginLoadingShutdown"):
			child.call("BeginLoadingShutdown")
			owners.append(child)
	_game.get_node("TerrainLodCoordinator").set_process(false)
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline:
		var drained := not is_instance_valid(surface) or bool(surface.get("StartupCancellationReady"))
		for owner: Node in owners:
			drained = drained and bool(owner.get("LoadingShutdownReady"))
		if drained:
			break
		await process_frame
	_game.queue_free()
	await process_frame
	await process_frame
