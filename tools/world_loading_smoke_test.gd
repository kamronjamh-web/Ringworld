extends SceneTree
## Exercise the real launcher and readiness gates with an isolated WorldSession.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
var _done := false
var _loader: Node
var _game: Node
var _held_frames := 0
var _capture_pending := false
var _captured := false
var _saw_pending_terrain := false
var _start_usec := 0
var _next_report := 0
var _smooth := false

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_start_usec = Time.get_ticks_usec()
	_smooth = "--smooth" in OS.get_cmdline_user_args()
	var session := root.get_node_or_null("WorldSession")
	if session == null or not session.select_slot(1):
		_fail("isolated world slot unavailable")
		return
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	current_scene = launcher
	launcher.call("_on_certified_preset_pressed")
	launcher.get("terrain_backend_selector").select(0 if _smooth else 1)
	launcher.call("_on_terrain_backend_selected", 0 if _smooth else 1)
	launcher.call("_start_game")
	_loader = root.get_node_or_null("RingWorldLoading")
	if _loader == null:
		_fail("loading screen did not appear synchronously with the launch request")
		return
	_loader.world_ready.connect(_ready_world)
	_loader.loading_failed.connect(func(message: String): _fail(message))

func _process(_delta: float) -> bool:
	if _done or _loader == null:
		return false
	if Time.get_ticks_usec() - _start_usec > 180000000:
		_fail("world loading timed out: " + str(_loader.get("_stage")))
		return false
	if Time.get_ticks_msec() > _next_report:
		_next_report = Time.get_ticks_msec() + 10000
		var bridge: Node = _loader.get("_bridge")
		if is_instance_valid(bridge):
			print("WORLD_LOADING_PENDING=" + JSON.stringify(bridge.call("GetStartupDiagnostics")))
	var game: Node = _loader.get("_game")
	if game != null and game.is_inside_tree():
		var explorer: Node = game.get_node("Explorer")
		if explorer.process_mode == Node.PROCESS_MODE_DISABLED:
			_held_frames += 1
		var surface: Node = game.get_node_or_null("Terrain3DGameplay")
		if surface != null and bool(surface.get("InitializationPending")):
			_saw_pending_terrain = true
		if "--capture" in OS.get_cmdline_user_args() and DisplayServer.get_name() != "headless" and not _capture_pending and not _captured:
			_capture_pending = true
			_capture.call_deferred()
	return false

func _capture() -> void:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image != null and not image.is_empty():
		image.save_png("res://.godot/world_loading_hologram.png")
		_captured = true

func _ready_world(game: Node) -> void:
	if _done:
		return
	_game = game
	var report: Dictionary = _loader.get_diagnostics()
	var surface: Node = game.get_node_or_null("Terrain3DGameplay")
	var saves: Node = game.get_node("WorldSaveManager")
	if _held_frames < 4 or (not _smooth and not _saw_pending_terrain):
		_fail("startup did not yield while player input was held")
		return
	if (not _smooth and not bool(surface.get("IsReady"))) or not bool(saves.get("IsWorldLoadComplete")):
		_fail("world was revealed before terrain and save restoration completed")
		return
	if game.get_node("Explorer").process_mode == Node.PROCESS_MODE_DISABLED:
		_fail("player input was not restored")
		return
	if str(report.get("outcome")) != "ready" or int(report.get("frames", 0)) < 5:
		_fail("loading diagnostics were not published")
		return
	var systems: Dictionary = report.get("systems", {})
	if systems.is_empty():
		_fail("structural readiness owners were not registered")
		return
	for system: Dictionary in systems.values():
		if not bool(system.get("ready", false)):
			_fail("world entered before a structural owner was ready")
			return
	_done = true
	# No gameplay modifications were made; avoid writing fixture data during exit.
	saves.call("CancelWorldLoad")
	if is_instance_valid(surface):
		surface.call("CancelStartup", "Loading smoke fixture complete.")
	var shutdown_owners: Array[Node] = []
	for child: Node in _game.get_children():
		if child.has_method("BeginLoadingShutdown"):
			child.call("BeginLoadingShutdown")
			shutdown_owners.append(child)
	_game.get_node("TerrainLodCoordinator").set_process(false)
	var deadline := Time.get_ticks_msec() + 10000
	var drained := false
	while Time.get_ticks_msec() < deadline:
		drained = not is_instance_valid(surface) or bool(surface.get("StartupCancellationReady"))
		for owner: Node in shutdown_owners:
			drained = drained and bool(owner.get("LoadingShutdownReady"))
		if drained:
			break
		await process_frame
	if not drained:
		push_error("WORLD_LOADING_SMOKE=FAIL background workers did not drain before fixture shutdown")
		quit(1)
		return
	_game.queue_free()
	await process_frame
	await process_frame
	print("WORLD_LOADING_SMOKE=PASS held_frames=%d pending_terrain=%s report=%s" % [_held_frames, _saw_pending_terrain, JSON.stringify(report)])
	quit(0)

func _fail(message: String) -> void:
	if _done:
		return
	_done = true
	push_error("WORLD_LOADING_SMOKE=FAIL " + message)
	if is_instance_valid(_game):
		_game.get_node("WorldSaveManager").call("CancelWorldLoad")
		_game.queue_free()
	quit(1)
