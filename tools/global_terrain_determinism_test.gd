extends SceneTree
## One launch of the global-terrain determinism test: loads a world through the real loading screen, waits for the global terrain to publish its morph
## snapshot and prints GLOBAL_TERRAIN_DETERMINISM_HASHES=<total>|<per-segment list>. Run through tools/global_terrain_determinism_test.ps1
## (isolated APPDATA, GPU queue); the script refuses to run on the real roaming profile.
##
##   --mode=create   new certified runtime-generation world in slot 1, saved before exit
##   --mode=reload   reopen the saved slot 1 through the loading screen

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const WORLD_LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const LOAD_TIMEOUT_MS := 300000
const SLOT := 1

var _args := {}
var _loader: Node
var _game: Node
var _failure := ""

func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			if eq > 0:
				_args[text.substr(2, eq - 2)] = text.substr(eq + 1)
	call_deferred("_run")

func _fail(message: String, code: int = 1) -> void:
	push_error("GLOBAL_TERRAIN_DETERMINISM_LAUNCH=FAIL " + message)
	quit(code)

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming):
		_fail("user:// is the real roaming profile; use tools/global_terrain_determinism_test.ps1", 2)
		return
	var mode := String(_args.get("mode", "create"))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var session := root.get_node_or_null("WorldSession")
	if session == null or not session.select_slot(SLOT):
		_fail("isolated world slot unavailable", 3)
		return
	if mode == "reload":
		if not bool(session.slot_exists(SLOT)):
			_fail("slot %d has no saved world to reload" % SLOT, 3)
			return
		var metadata: Dictionary = session.get_slot_metadata(SLOT)
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
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		current_scene = launcher
		launcher.call("_on_certified_preset_pressed")
		# Backend 2 = runtime surface generation, what the start menu's Standard preset creates and the race needs.
		launcher.get("terrain_backend_selector").select(2)
		launcher.call("_on_terrain_backend_selected", 2)
		launcher.call("_start_game")
		_loader = root.get_node_or_null("RingWorldLoading")
		if _loader == null:
			_fail("loading screen did not appear with the launch request")
			return
		_connect_loader()
	var deadline := Time.get_ticks_msec() + LOAD_TIMEOUT_MS
	while _game == null and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	if _game == null:
		_fail(_failure if not _failure.is_empty() else "world loading timed out")
		return
	var lod: Node = _game.get_node_or_null("GlobalTerrainLod3")
	if lod == null or not lod.has_method("GetMorphDeterminismReport"):
		_fail("GlobalTerrainLod3 with GetMorphDeterminismReport not found")
		return
	var report := ""
	while Time.get_ticks_msec() < deadline:
		report = String(lod.call("GetMorphDeterminismReport"))
		if bool(lod.get("StartupComplete")) and not report.is_empty():
			break
		await process_frame
	if report.is_empty():
		_fail("global terrain never published a morph snapshot")
		return
	print("GLOBAL_TERRAIN_DETERMINISM_HASHES=" + report)
	if mode == "create":
		var saves: Node = _game.get_node("WorldSaveManager")
		var error: int = int(saves.call("SaveWorld"))
		if error != OK:
			_fail("SaveWorld failed: error %d" % error)
			return
	await _shutdown()
	print("GLOBAL_TERRAIN_DETERMINISM_LAUNCH=PASS mode=" + mode)
	quit(0)

func _connect_loader() -> void:
	_loader.world_ready.connect(func(world: Node): _game = world)
	_loader.loading_failed.connect(func(message: String): _failure = message)

## The smoke test's fixture shutdown: background workers drain before exit.
func _shutdown() -> void:
	var surface: Node = _game.get_node_or_null("Terrain3DGameplay")
	_game.get_node("WorldSaveManager").call("CancelWorldLoad")
	if is_instance_valid(surface):
		surface.call("CancelStartup", "Determinism launch complete.")
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
