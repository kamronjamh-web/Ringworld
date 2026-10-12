extends SceneTree

const FIXTURE := preload("res://scripts/Tools/WorldLoadingSaveContract.cs")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")

var _failures := PackedStringArray()
var _checks := 0
var _finished := false
var _game: Node
var _launcher: Node
var _slot := ""
var _original_slot: Variant
var _original_backend: Variant

func _init() -> void:
	create_timer(180.0).timeout.connect(func(): _failures.append("watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	_original_slot = ProjectSettings.get_setting("ringworld/runtime/active_world_directory", "")
	_original_backend = ProjectSettings.get_setting("ringworld/runtime/terrain_backend", "")
	var fixture := FIXTURE.new()
	root.add_child(fixture)
	while not bool(fixture.get("Complete")):
		await process_frame
	var result: Dictionary = fixture.get("Result")
	_expect(bool(result.get("pass", false)), "managed readers: %s" % result)
	_checks += int(result.get("checks", 0))
	# Actual scene failure and cancellation exercise the terrain hold before any
	# initial region can overwrite an incompatible or cancelled save slot.
	_slot = "res://.godot/world_loading_save_scene_%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_slot))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _slot)
	_launcher = LAUNCHER.instantiate()
	root.add_child(_launcher)
	await process_frame
	_launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = _launcher.get("terrain_backend_selector")
	selector.select(1)
	selector.emit_signal("item_selected", 1)
	var state_path := _slot.path_join("world_state_terrain3d_v1.json")
	_write(state_path, "{")
	_game = _create_game()
	var focus_result: Dictionary = fixture.call("VerifyLoadingFocus", _game.get_node("Explorer"))
	_expect(bool(focus_result.get("pass", false)), "saved excavation focus: %s" % focus_result)
	_checks += int(focus_result.get("checks", 0))
	fixture.queue_free()
	var saves: Node = _game.get_node("WorldSaveManager")
	var terrain: Node = _game.get_node("Terrain3DGameplay")
	_expect(not bool(terrain.call("EnsureInitialized")), "saved-state hold allowed terrain initialization")
	for frame in 600:
		if bool(saves.get("IsWorldLoadFailed")):
			break
		await process_frame
	_expect(bool(saves.get("IsWorldLoadFailed")), "malformed saved world did not reject staged startup")
	_expect(not bool(saves.get("IsWorldLoadComplete")), "failed world advertised successful restoration")
	_expect(int(saves.call("SaveWorld")) != OK, "rejected world allowed an autosave")
	_expect(_read(state_path) == "{", "rejected world changed its source bytes")
	_game.queue_free()
	await process_frame
	_expect(_read(state_path) == "{", "failed world teardown rewrote its save")
	_game = _create_game()
	saves = _game.get_node("WorldSaveManager")
	saves.call("CancelWorldLoad")
	_game.queue_free()
	await process_frame
	await process_frame
	_expect(_read(state_path) == "{", "cancelled deferred load rewrote its save")
	_expect(not FileAccess.file_exists(_slot.path_join("surface_terrain3d_edits_v1.json")), "held terrain committed edits on failure/cancellation")
	_finish()

func _create_game() -> Node:
	var game: Node = WORLD.instantiate()
	_launcher.call("_configure_game", game, 5000000.0)
	game.get_node("WorldSaveManager").set("UseStagedStartup", true)
	game.get_node("Terrain3DGameplay").set("UseStagedStartup", true)
	game.get_node("Terrain3DGameplay").set("WaitForSavedState", true)
	game.get_node("Explorer").process_mode = Node.PROCESS_MODE_DISABLED
	# The contract ends before terrain publication; remove unrelated presentation.
	for name in ["BiomeDecorations", "StandingGrass", "RingVolumetricClouds", "LocalVolumetricCloudRenderer", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "WaterFftController", "TerrainMinimap", "CertificationMonitor", "RingScaledFarViewport", "GlobalTerrainLod", "RegionalTerrainLod"]:
		var node := game.get_node_or_null(name)
		if node != null:
			node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(game)
	return game

func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()

func _read(path: String) -> String:
	return FileAccess.get_file_as_string(path)

func _expect(condition: bool, detail: String) -> void:
	_checks += 1
	if not condition:
		_failures.append(detail)

func _finish() -> void:
	if _finished:
		return
	_finished = true
	if is_instance_valid(_game):
		var saves := _game.get_node_or_null("WorldSaveManager")
		if saves != null:
			saves.call("CancelWorldLoad")
		_game.queue_free()
	if is_instance_valid(_launcher):
		_launcher.queue_free()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _original_slot)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", _original_backend)
	if _failures.is_empty():
		print("WORLD_LOADING_SAVE_CONTRACT=PASS checks=%s" % _checks)
		quit(0)
	else:
		for failure in _failures:
			push_error(failure)
		print("WORLD_LOADING_SAVE_CONTRACT=FAIL checks=%s" % _checks)
		quit(1)
