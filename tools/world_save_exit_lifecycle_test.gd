extends SceneTree
## Run with an isolated APPDATA directory. Loads a world, presses the pause menu's "Save & Exit to Menu" and checks that the whole
## world runtime is freed, the StartMenu is current, and save state/metadata were written.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const TIMEOUT_MS := 180000

var _loader: Node
var _ready_game: Node
var _failure := ""
var _errors := PackedStringArray()
var _checks := 0

func _initialize() -> void:
	# The menu shown after Save & Exit must not warm-load the world again (same effect as --no-warm-world).
	ProjectSettings.set_setting("ringworld/runtime/no_warm_world", true)
	node_added.connect(func(node: Node):
		if node.scene_file_path == "res://scenes/StartMenu.tscn":
			node.set("freeze_cover", true))
	call_deferred("_run")

func _run() -> void:
	var session := root.get_node_or_null("WorldSession")
	if not _expect(session != null and session.select_slot(1), "isolated world slot unavailable"):
		return _finish()
	var directory: String = session.get_active_world_directory()
	var state_path: String = directory.path_join("world_state_terrain3d_v1.json")
	_expect(not FileAccess.file_exists(state_path), "test requires a fresh isolated APPDATA directory")
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	current_scene = launcher
	launcher.call("_on_certified_preset_pressed")
	launcher.get("terrain_backend_selector").select(1)
	launcher.call("_on_terrain_backend_selected", 1)
	launcher.call("_start_game")
	_loader = root.get_node_or_null("RingWorldLoading")
	if not _expect(_loader != null, "no loading coordinator"):
		return _finish()
	_loader.world_ready.connect(func(world: Node):
		_ready_game = world)
	_loader.loading_failed.connect(func(message: String): _failure = message)
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while _ready_game == null and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	if not _expect(_ready_game != null, "world did not become ready: " + _failure):
		return _finish()
	var game := _ready_game
	var game_id := game.get_instance_id()
	# Let the loading fade finish so the world is in its normal running state.
	for i in 90:
		await process_frame
	await create_timer(1.5).timeout
	var menu: Node = game.get_node("LodPauseMenu")
	var button: Button = null
	for candidate in menu.find_children("*", "Button", true, false):
		if candidate.text == "Save & Exit to Menu":
			button = candidate
	if not _expect(button != null, "Save & Exit to Menu button missing"):
		return _finish()
	_expect(root.get_node_or_null("WorldSession").active_slot == 1, "active slot lost")
	menu.call("SetBenchmarkMenuOpen", true)
	await process_frame
	_expect(paused, "menu did not pause the tree")
	button.emit_signal("pressed")
	_expect(button.disabled, "button not disabled after press")
	deadline = Time.get_ticks_msec() + TIMEOUT_MS
	while (current_scene == null or current_scene.scene_file_path != "res://scenes/StartMenu.tscn") and Time.get_ticks_msec() < deadline:
		await process_frame
	_expect(current_scene != null and current_scene.scene_file_path == "res://scenes/StartMenu.tscn", "StartMenu is not the current scene")
	for i in 20:
		await process_frame
	_expect(not paused, "tree left paused")
	_expect(not is_instance_valid(instance_from_id(game_id)) or instance_from_id(game_id) == null, "world node still alive")
	for child in root.get_children():
		var child_name := str(child.name)
		print("ROOT_CHILD ", child_name, " ", child.get_class())
		_expect(not (child_name.contains("VoxelRing") or child_name.contains("WorldSizeLauncher") or child_name == "RingWorldLoadingBridge"), "world runtime node remains: " + child_name)
	_expect(FileAccess.file_exists(state_path), "world state not written")
	var meta: Dictionary = session.get_slot_metadata(1)
	_expect(not (meta.get("generation_settings", {}) as Dictionary).is_empty(), "generation settings missing from metadata")
	_expect(int(meta.get("last_played_unix", 0)) > 0, "last_played missing")
	if _loader != null and is_instance_valid(_loader):
		_expect(not bool(_loader.get("active")), "loader still active")
	print("SAVE_EXIT_META playtime=", meta.get("playtime_seconds", 0))
	_finish()

func _expect(condition: bool, message: String) -> bool:
	_checks += 1
	if not condition:
		_errors.append(message)
	return condition

func _finish() -> void:
	if _errors.is_empty():
		print("WORLD_SAVE_EXIT_LIFECYCLE=PASS checks=%d" % _checks)
		quit(0)
	else:
		for error in _errors:
			push_error(error)
		print("WORLD_SAVE_EXIT_LIFECYCLE=FAIL checks=%d" % _checks)
		quit(1)
