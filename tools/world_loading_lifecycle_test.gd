extends SceneTree
## Run with an isolated APPDATA directory. Exercises the real menu and launcher.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const MENU := preload("res://scenes/StartMenu.tscn")
const TIMEOUT_MS := 180000
const WORLD_PATH := "res://scenes/VoxelRingIntegrationTest.tscn"
const WARMUP_PROBES := [
	"res://materials/third_party/voxelgame/grass_albedo.png",
	"res://materials/third_party/voxelgame/dirt_albedo.png",
	"res://materials/biomes/desert_sand_albedo.png",
	"res://materials/biomes/forest_floor_albedo.png",
	"res://materials/biomes/tundra_ground_albedo.png",
	"res://materials/biomes/alpine_rock_albedo.png"
]

var _loader: Node
var _game: Node
var _ready_game: Node
var _failure := ""
var _checks := 0
var _errors := PackedStringArray()
var _done := false
var _check_arrival := false
var _grace_frames := 0
var _saved_player: Dictionary = {}
var _state_path := ""
var _edit_path := ""
var _saved_height := 0.0
var _saved_edit_count := 0

func _initialize() -> void:
	# The menu must stay out of the way of the foreground loading screen under test (same effect as --no-warm-world).
	ProjectSettings.set_setting("ringworld/runtime/no_warm_world", true)
	node_added.connect(_on_node_added)
	call_deferred("_run")

func _on_node_added(node: Node) -> void:
	# Cancellation uses change_scene_to_file; freeze its cover before _ready.
	if node.scene_file_path == "res://scenes/StartMenu.tscn":
		node.set("freeze_cover", true)

func _process(_delta: float) -> bool:
	if _done or not _check_arrival or not is_instance_valid(_loader) or not bool(_loader.get("active")):
		return false
	var screen: Node = _loader.get("_screen")
	var world: Node = _loader.get("_game")
	if is_instance_valid(screen) and bool(screen.get("_finished")) and is_instance_valid(world):
		_grace_frames += 1
		if world.get_node("Explorer").process_mode != Node.PROCESS_MODE_DISABLED:
			_errors.append("player input released during the loading fade")
		if world.get_node("LiveTuningDrawer").process_mode != Node.PROCESS_MODE_DISABLED:
			_errors.append("live tuning input released during the loading fade")
		var hologram: Node = screen.get("_hologram")
		var expected_turn := fposmod(float(_saved_player.AroundMeters) / float(hologram.get("_circumference_m")), 1.0)
		var expected_across := clampf(float(_saved_player.AcrossMeters) / float(hologram.get("_width_m")), 0.0, 1.0)
		if absf(float(hologram.get("beacon_turn")) - expected_turn) > 0.000001 or absf(float(hologram.get("beacon_across")) - expected_across) > 0.000001:
			_errors.append("arrival beacon did not mark the restored saved position")
	return false

func _run() -> void:
	var session := root.get_node_or_null("WorldSession")
	if not _expect(session != null and session.select_slot(1), "isolated world slot unavailable"):
		await _finish()
		return
	var directory: String = session.get_active_world_directory()
	_state_path = directory.path_join("world_state_terrain3d_v1.json")
	_edit_path = directory.path_join("surface_terrain3d_edits_v1.json")
	if not _expect(not FileAccess.file_exists(_state_path), "test requires a fresh isolated APPDATA directory"):
		await _finish()
		return
	_start_fresh_world()
	_loader = root.get_node_or_null("RingWorldLoading")
	if not _expect(_loader != null, "launcher did not create the loading coordinator"):
		await _finish()
		return
	_loader.world_ready.connect(_on_world_ready)
	_loader.loading_failed.connect(func(message: String): _failure = message)
	var scene_request_seen := await _wait_for_early_request(false)
	_expect(scene_request_seen, "early cancel did not observe an outstanding scene request")
	_loader.call("_cancel_and_return")
	await _wait_for_menu()
	_expect(ResourceLoader.load_threaded_get_status(WORLD_PATH) == ResourceLoader.THREAD_LOAD_INVALID_RESOURCE, "cancelled scene request was not collected before returning to the menu")
	_expect(not FileAccess.file_exists(_state_path) and not FileAccess.file_exists(_edit_path), "early scene cancellation created world state or terrain edits")
	# Retry the same scene immediately. A forgotten request would reuse its token
	# and leave one reference pinned even after this retry collects its result.
	_failure = ""
	_start_fresh_world()
	var warmup_seen := await _wait_for_early_request(true)
	_expect(warmup_seen, "retry did not reach resource warmup before cancellation")
	var requested_paths := PackedStringArray()
	for path: String in WARMUP_PROBES:
		if ResourceLoader.load_threaded_get_status(path) != ResourceLoader.THREAD_LOAD_INVALID_RESOURCE:
			requested_paths.append(path)
	_expect(not requested_paths.is_empty(), "warmup cancellation did not observe any resource requests")
	_loader.call("_cancel_and_return")
	await _wait_for_menu()
	_expect(ResourceLoader.load_threaded_get_status(WORLD_PATH) == ResourceLoader.THREAD_LOAD_INVALID_RESOURCE, "scene retry left a collected scene request pinned")
	for path in requested_paths:
		_expect(ResourceLoader.load_threaded_get_status(path) == ResourceLoader.THREAD_LOAD_INVALID_RESOURCE, "cancelled warmup request was not collected: " + path)
	_expect(not FileAccess.file_exists(_state_path) and not FileAccess.file_exists(_edit_path), "warmup cancellation created world state or terrain edits")
	_failure = ""
	_start_fresh_world()
	_game = await _wait_for_ready()
	if _game == null:
		await _finish()
		return
	var explorer: Node = _game.get_node("Explorer")
	var terrain: Node = _game.get_node("Terrain3DGameplay")
	var diagnostics: Dictionary = terrain.call("GetSurfaceDiagnostics")
	var around := float(diagnostics.player_around) + 128.375
	var across := float(diagnostics.player_across) + 0.625
	var baseline := float(terrain.call("SampleHeight", around, across))
	# Choose dry terrain so the normal reloaded swimming configuration cannot
	# move the player independently of the save restoration being exercised.
	for candidate in 256:
		if is_finite(baseline) and baseline > 24.0:
			break
		around += 4096.0
		baseline = float(terrain.call("SampleHeight", around, across))
		if candidate % 16 == 15:
			await process_frame
	if not _expect(is_finite(baseline) and baseline > 24.0, "could not find dry terrain for the depression fixture"):
		await _finish()
		return
	explorer.call("SetCanonicalSpawnMeters", around, baseline + 1.05, across)
	if not await _wait_for_surface_commit(terrain, around, across):
		await _finish()
		return
	if not _expect(bool(terrain.call("ApplySurfaceBrushAtCanonical", around, across, -8.0)), "durable depression brush failed"):
		await _finish()
		return
	_saved_height = float(terrain.call("SampleHeight", around, across))
	_expect(_saved_height < baseline - 6.0, "brush did not create a meaningful saved depression")
	if not await _wait_for_surface_commit(terrain, around, across):
		await _finish()
		return
	var collision_height := float(terrain.call("SampleCollisionHeight", around, across))
	_expect(collision_height < baseline - 5.0 and collision_height > 0.0, "committed collider did not reflect a dry depression")
	explorer.call("SetCanonicalSpawnMeters", around, collision_height + 1.05, across)
	await physics_frame
	await physics_frame
	var saves: Node = _game.get_node("WorldSaveManager")
	if not _expect(int(saves.call("SaveForSceneTransition")) == OK, "fixture save did not commit"):
		await _finish()
		return
	var document: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(_state_path))
	_saved_player = document.Player
	_saved_edit_count = int(terrain.call("GetSurfaceDiagnostics").get("edits", 0))
	_expect(absf(float(_saved_player.VerticalMeters) - (collision_height + 1.05)) < 0.2, "saved player was not placed on the committed edited depression collider")
	print("WORLD_LOADING_DEPRESSION_FIXTURE=" + JSON.stringify({"around": around, "across": across, "baseline_height": baseline, "edited_height": _saved_height, "collision_height": collision_height, "saved_player": _saved_player}))
	await _dispose_world(_game)
	_game = null
	await _open_from_menu()
	_check_arrival = true
	_game = await _wait_for_ready()
	_check_arrival = false
	if _game == null:
		await _finish()
		return
	terrain = _game.get_node("Terrain3DGameplay")
	diagnostics = terrain.call("GetSurfaceDiagnostics")
	_expect(int(diagnostics.get("edits", 0)) == _saved_edit_count, "saved surface edit count changed on menu reopen")
	_expect(absf(float(terrain.call("SampleHeight", around, across)) - _saved_height) < 0.01, "saved depression was not restored")
	_expect(absf(float(diagnostics.player_around) - float(_saved_player.AroundMeters)) < 0.01 and absf(float(diagnostics.player_across) - float(_saved_player.AcrossMeters)) < 0.01, "menu reopen lost fractional saved canonical coordinates")
	_expect(absf(float(diagnostics.player_height) - float(_saved_player.VerticalMeters)) < 0.2, "menu reopen recovered saved depression height against unedited terrain")
	_expect(_grace_frames >= 2, "loading fade did not retain observable input ownership")
	_expect(_game.get_node("LiveTuningDrawer").process_mode == Node.PROCESS_MODE_ALWAYS, "live tuning's Always process mode was not restored")
	await _dispose_world(_game)
	_game = null
	var before := _saved_bytes()
	await _open_from_menu()
	var pending := await _wait_for_pending_terrain()
	_expect(pending, "cancellation test did not observe pending terrain initialization")
	_loader.call("_cancel_and_return")
	await _wait_for_menu()
	_expect(_saved_bytes() == before, "cancelling pending initialization changed authoritative save files")
	_expect(current_scene != null and current_scene.scene_file_path == "res://scenes/StartMenu.tscn", "cancel did not return to the actual start menu")
	await _open_from_menu()
	_game = await _wait_for_ready()
	_expect(_game != null, "slot could not reopen after cancellation")
	if _game == null:
		await _finish()
		return
	await _dispose_world(_game)
	_game = null
	# Malformed state must stay unchanged through the visible failure and return.
	var valid_state := FileAccess.get_file_as_bytes(_state_path)
	_write_bytes(_state_path, "{".to_utf8_buffer())
	before = _saved_bytes()
	await _open_from_menu()
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	_expect(not _failure.is_empty(), "malformed save did not show a loading failure")
	var failure_screen: Node = _loader.get("_screen")
	_expect(is_instance_valid(failure_screen) and bool(failure_screen.get("_failed")), "failure was not shown on the loading overlay")
	_loader.call("_cancel_and_return")
	await _wait_for_menu()
	_expect(_saved_bytes() == before, "failure/cancel teardown rewrote malformed state or terrain edits")
	_write_bytes(_state_path, valid_state)
	await _finish()

func _on_world_ready(world: Node) -> void:
	# Hold before the next physics frame so comparisons measure restoration.
	world.get_node("Explorer").process_mode = Node.PROCESS_MODE_DISABLED
	_ready_game = world
	_game = world

func _start_fresh_world() -> void:
	var old_scene := current_scene
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	current_scene = launcher
	if is_instance_valid(old_scene):
		old_scene.queue_free()
	launcher.call("_on_certified_preset_pressed")
	launcher.get("terrain_backend_selector").select(1)
	launcher.call("_on_terrain_backend_selected", 1)
	launcher.call("_start_game")

func _wait_for_early_request(warmup: bool) -> bool:
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while bool(_loader.get("active")) and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		if warmup:
			var resources: Node = _loader.get("_resources")
			if is_instance_valid(resources) and not bool(resources.call("IsComplete")) and ResourceLoader.load_threaded_get_status(WARMUP_PROBES[0]) != ResourceLoader.THREAD_LOAD_INVALID_RESOURCE:
				return true
		else:
			var status := ResourceLoader.load_threaded_get_status(WORLD_PATH)
			if status == ResourceLoader.THREAD_LOAD_IN_PROGRESS or status == ResourceLoader.THREAD_LOAD_LOADED:
				return true
		await process_frame
	return false

func _wait_for_ready() -> Node:
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while _ready_game == null and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	var result := _ready_game
	_ready_game = null
	_expect(result != null, "world entry failed or timed out: " + _failure)
	return result

func _open_from_menu() -> void:
	_failure = ""
	_ready_game = null
	var menu: Node = current_scene
	if menu == null or menu.scene_file_path != "res://scenes/StartMenu.tscn":
		menu = MENU.instantiate()
		menu.set("freeze_cover", true)
		root.add_child(menu)
		current_scene = menu
	else:
		menu.set("freeze_cover", true)
	# Let the real menu's deferred backdrop initialization finish before a click.
	await _frames(2)
	menu.set("selected_slot", 1)
	menu.call("_load_selected_world")
	await process_frame

func _wait_for_surface_commit(terrain: Node, around: float, across: float) -> bool:
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while Time.get_ticks_msec() < deadline:
		var status: Dictionary = terrain.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = status.get("streaming", {})
		var current_focus := absf(float(status.get("frame_around", 0.0)) - around) <= 160.0 and absf(float(status.get("frame_across", 0.0)) - across) <= 160.0
		var committed := int(streaming.get("committed_edit_revision", -1)) == int(status.get("revision", -2)) and int(status.get("request_revision", -1)) == int(status.get("committed_revision", -2))
		if current_focus and committed and str(streaming.get("state", "")) == "active":
			await physics_frame
			await physics_frame
			return _expect(true, "edited collider committed")
		await process_frame
	return _expect(false, "surface collider did not commit at the fixture's current focus before the deadline")

func _wait_for_pending_terrain() -> bool:
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while bool(_loader.get("active")) and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		var world: Node = _loader.get("_game")
		if is_instance_valid(world) and world.is_inside_tree():
			var terrain: Node = world.get_node("Terrain3DGameplay")
			if bool(terrain.get("InitializationPending")) and not bool(terrain.get("WaitForSavedState")):
				return true
		await process_frame
	return false

func _wait_for_menu() -> void:
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while (bool(_loader.get("active")) or current_scene == null or current_scene.scene_file_path != "res://scenes/StartMenu.tscn") and Time.get_ticks_msec() < deadline:
		await process_frame
	_expect(not bool(_loader.get("active")), "safe cancellation did not finish")

func _dispose_world(world: Node) -> void:
	if not is_instance_valid(world):
		return
	world.get_node("WorldSaveManager").call("CancelWorldLoad")
	var terrain: Node = world.get_node("Terrain3DGameplay")
	terrain.call("CancelStartup", "Lifecycle fixture teardown")
	var proxies: Array[Node] = []
	for child: Node in world.get_children():
		if child.has_method("BeginLoadingShutdown"):
			child.call("BeginLoadingShutdown")
			proxies.append(child)
	world.get_node("TerrainLodCoordinator").set_process(false)
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	var drained := false
	while Time.get_ticks_msec() < deadline:
		var ready := bool(terrain.get("StartupCancellationReady"))
		for proxy in proxies:
			ready = ready and bool(proxy.get("LoadingShutdownReady"))
		if ready:
			drained = true
			break
		await process_frame
	if not _expect(drained, "world teardown workers did not drain before the deadline"):
		return
	world.queue_free()
	await _frames(3)

func _saved_bytes() -> Dictionary:
	# Slot metadata intentionally updates last-played time on menu selection.
	return {"state": FileAccess.get_file_as_bytes(_state_path), "edits": FileAccess.get_file_as_bytes(_edit_path)}

func _write_bytes(path: String, bytes: PackedByteArray) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_buffer(bytes)
	file.close()

func _frames(count: int) -> void:
	for frame in count:
		await process_frame

func _expect(condition: bool, message: String) -> bool:
	_checks += 1
	if not condition:
		_errors.append(message)
	return condition

func _finish() -> void:
	if _done:
		return
	_done = true
	if is_instance_valid(_loader) and bool(_loader.get("active")):
		_loader.call("_cancel_and_return")
		await _wait_for_menu()
	elif is_instance_valid(_game):
		await _dispose_world(_game)
	if _errors.is_empty():
		print("WORLD_LOADING_LIFECYCLE=PASS checks=%d grace_frames=%d" % [_checks, _grace_frames])
		quit(0)
	else:
		for error in _errors:
			push_error(error)
		print("WORLD_LOADING_LIFECYCLE=FAIL checks=%d" % _checks)
		quit(1)
