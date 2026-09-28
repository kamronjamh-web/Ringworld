extends SceneTree

## M5 runtime-surface world contract smoke. This deliberately owns an isolated
## res:// fixture and never writes launcher preferences or an actual world slot.
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const WORLD_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const BACKEND := "surface_runtime_generation_v1"
const STATE_NAME := "world_state_surface_runtime_v1.json"
const EDIT_NAME := "surface_runtime_edits_v1.json"

var _fixture := ""
var _timer: SceneTreeTimer
var _session: WorldSessionStub

class WorldSessionStub extends Node:
	var active_settings: Dictionary = {}
	var saved_settings: Dictionary = {}
	var write_count := 0
	func get_active_world_settings() -> Dictionary: return active_settings.duplicate(true)
	func save_active_world_settings(settings: Dictionary) -> void: saved_settings = settings.duplicate(true); active_settings = saved_settings.duplicate(true); write_count += 1
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	_timer = create_timer(120.0)
	_timer.timeout.connect(func(): _fail("timeout"))
	_fixture = "res://.godot/terrain-generation-tests/m5-world-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", BACKEND)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_fixture))
	var actual_session := root.get_node_or_null("WorldSession")
	if actual_session != null: actual_session.name = "WorldSessionOriginalM5"
	_session = WorldSessionStub.new()
	_session.name = "WorldSession"
	root.add_child(_session)
	var game := await _create_runtime_game()
	if game == null: return
	var grid: Node = game.get_node_or_null("ChunkGridManager")
	var explorer: Node = game.get_node_or_null("Explorer")
	var surface: Node = game.get_node_or_null("Terrain3DGameplay")
	var saves: Node = game.get_node_or_null("WorldSaveManager")
	if grid == null or explorer == null or surface == null or saves == null:
		_fail("runtime game lacks grid/explorer/surface/save nodes")
		return
	if int(grid.get("MaxBuildGlobalY")) != 8400:
		_fail("runtime build ceiling is not +8400")
		return
	var runtime: Dictionary = surface.call("GetSurfaceDiagnostics")
	var recipe: String = str(runtime.get("surface_generation_recipe_json", ""))
	var contract: String = str(runtime.get("surface_generation_contract_id", ""))
	if recipe.is_empty() or contract.is_empty() or int(runtime.get("terrain_generator_version", 0)) != 17 or int(runtime.get("sea_level_y", -1)) != 0 or int(runtime.get("bedrock_y", 0)) != -8000:
		_fail("runtime sampler lacks complete recipe identity")
		return
	if "--capture" in OS.get_cmdline_user_args() and not OS.has_feature("headless"):
		await _capture_runtime_game(game)
	var point := _find_fractional_point(surface)
	if point.is_empty(): return
	var around := float(point["around"])
	var across := float(point["across"])
	var before := float(surface.call("SampleHeight", around, across))
	if not bool(surface.call("ApplySurfaceBrushAtCanonical", around, across, 0.25)):
		_fail("fractional runtime brush rejected: %s" % surface.get("LastError"))
		return
	await _frames(6)
	var edited := float(surface.call("SampleHeight", around, across))
	if absf(edited - before) < 0.20:
		_fail("fractional .25 brush was rounded away: %s -> %s" % [before, edited])
		return
	if int(saves.call("SaveForSceneTransition")) != OK:
		_fail("runtime surface save rejected")
		return
	var state_path := _fixture.path_join(STATE_NAME)
	var edit_path := str(surface.call("GetSurfaceDiagnostics").get("save_path", ""))
	if edit_path.get_file() != EDIT_NAME:
		_fail("runtime edit journal filename is not %s: %s" % [EDIT_NAME, edit_path])
		return
	if not FileAccess.file_exists(state_path) or not FileAccess.file_exists(edit_path):
		_fail("runtime state or edit file missing")
		return
	var state = JSON.parse_string(FileAccess.get_file_as_string(state_path))
	if not (state is Dictionary) or str(state.get("Backend", "")) != BACKEND or int(state.get("SurfaceDefinitionVersion", 0)) != 1 or str(state.get("SurfaceGenerationRecipeJson", "")) != recipe or str(state.get("SurfaceGenerationContractId", "")) != contract or int(state.get("TerrainGeneratorVersion", 0)) != 17:
		_fail("runtime state lacks complete surface identity: %s" % state)
		return
	if OS.get_cmdline_user_args().has("--capture-only"):
		game.queue_free(); await _frames(3)
		print("terrain_generation_world_capture: PASS ", _fixture)
		quit(0)
		return
	var frozen_settings := _session.active_settings.duplicate(true)
	var missing_recipe := frozen_settings.duplicate(true)
	missing_recipe.erase("surface_generation_recipe_json")
	if not await _assert_metadata_rejected(missing_recipe, "missing runtime definition"): return
	var corrupt_recipe := frozen_settings.duplicate(true)
	corrupt_recipe["surface_generation_recipe_json"] = "{ malformed"
	if not await _assert_metadata_rejected(corrupt_recipe, "corrupt runtime definition"): return
	if not await _assert_metadata_rejected(frozen_settings, "frozen backend tamper", "backend"): return
	if not await _assert_metadata_rejected(frozen_settings, "frozen generation knob tamper", "height"): return
	_session.active_settings = frozen_settings
	var edit_bytes := FileAccess.get_file_as_bytes(edit_path)
	game.queue_free(); await _frames(3)
	var reloaded := await _create_runtime_game()
	if reloaded == null: return
	var reload_surface: Node = reloaded.get_node_or_null("Terrain3DGameplay")
	if reload_surface == null:
		_fail("runtime reload lost surface gameplay")
		return
	var restored := float(reload_surface.call("SampleHeight", around, across))
	if absf(restored - edited) > 0.02:
		_fail("runtime float edit did not reload exactly: %s -> %s" % [edited, restored])
		return
	reloaded.queue_free(); await _frames(2)
	# A complete but different recipe identity is also fail-closed before state or edits mutate.
	state["SurfaceGenerationRecipeJson"] = recipe + " "
	var mismatched_text := JSON.stringify(state)
	_write(state_path, mismatched_text)
	var mismatched := await _create_runtime_game()
	if mismatched == null: return
	var mismatch_saves: Node = mismatched.get_node_or_null("WorldSaveManager")
	if mismatch_saves == null or int(mismatch_saves.call("SaveForSceneTransition")) == OK:
		_fail("mismatched runtime recipe did not block save")
		return
	mismatched.queue_free(); await _frames(2)
	if FileAccess.get_file_as_string(state_path) != mismatched_text or FileAccess.get_file_as_bytes(edit_path) != edit_bytes:
		_fail("recipe mismatch mutated persisted metadata or edits")
		return
	# A malformed state is fail-closed: no editing, transition save, or file rewrite.
	_write(state_path, "{ malformed")
	var blocked := await _create_runtime_game()
	if blocked == null: return
	var blocked_surface: Node = blocked.get_node_or_null("Terrain3DGameplay")
	var blocked_saves: Node = blocked.get_node_or_null("WorldSaveManager")
	if blocked_surface == null or blocked_saves == null or bool(blocked_surface.call("ApplySurfaceBrushAtCanonical", around, across, 1.0)) or int(blocked_saves.call("SaveForSceneTransition")) == OK:
		_fail("malformed runtime state did not block editing/save")
		return
	blocked.queue_free(); await _frames(2)
	if FileAccess.get_file_as_string(state_path) != "{ malformed" or FileAccess.get_file_as_bytes(edit_path) != edit_bytes:
		_fail("blocked malformed load mutated state or edits")
		return
	# No old slot data may be silently adopted by a new runtime world.
	DirAccess.remove_absolute(ProjectSettings.globalize_path(state_path))
	_write(_fixture.path_join("world_state.json"), "{}")
	var occupied := await _create_runtime_game()
	if occupied == null: return
	var occupied_saves: Node = occupied.get_node_or_null("WorldSaveManager")
	if occupied_saves == null or int(occupied_saves.call("SaveForSceneTransition")) == OK:
		_fail("old occupied slot was implicitly migrated into runtime world")
		return
	occupied.queue_free(); await _frames(2)
	print("terrain_generation_world_smoke: PASS")
	quit(0)

func _create_runtime_game() -> Node:
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	await _frames(2)
	if _session.active_settings.is_empty(): launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null or selector.item_count < 3:
		_fail("launcher does not expose runtime surface backend index 2")
		return null
	selector.select(2)
	selector.emit_signal("item_selected", 2)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("valid runtime definition was rejected")
		return null
	if _session.active_settings.is_empty(): launcher.call("_save_selected_slot_settings")
	var game: Node = WORLD_SCENE.instantiate()
	launcher.call("_configure_game", game, 5000000.0)
	var tuning: Node = game.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", _fixture.path_join("surface_tuning.tres"))
	for name in ["BiomeDecorations", "StandingGrass", "RingVolumetricClouds", "LocalVolumetricCloudRenderer", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "WaterFftController", "TerrainMinimap", "CertificationMonitor"]:
		var node: Node = game.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(game)
	current_scene = game
	launcher.queue_free()
	await _frames(180)
	return game

func _assert_metadata_rejected(settings: Dictionary, label: String, tamper: String = "") -> bool:
	_session.active_settings = settings.duplicate(true)
	var writes_before := _session.write_count
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null:
		_fail("metadata fixture lacks backend selector")
		return false
	selector.select(2)
	selector.emit_signal("item_selected", 2)
	if tamper == "backend":
		selector.select(1)
		selector.emit_signal("item_selected", 1)
	elif tamper == "height":
		var height: SpinBox = launcher.get("base_surface_spin")
		height.set_value_no_signal(height.value + 1)
	if bool(launcher.call("_prepare_runtime_surface_generation", true)) or _session.write_count != writes_before:
		_fail(label + " was accepted or persisted before launch")
		return false
	launcher.queue_free()
	await _frames(1)
	return true

func _find_fractional_point(surface: Node) -> Dictionary:
	var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	var around := float(diagnostics.get("frame_around", 0.0)) + 0.25
	var across := float(diagnostics.get("frame_across", 0.0)) + 0.25
	if is_nan(float(surface.call("SampleHeight", around, across))):
		_fail("runtime surface does not sample fractional point")
		return {}
	return {"around": around, "across": across}

func _capture_runtime_game(game: Node) -> void:
	var drawer: Node = game.get_node_or_null("LiveTuningDrawer")
	var explorer: Node3D = game.get_node("Explorer")
	explorer.set_physics_process(false)
	# Inspect the real surface in neutral fixture lighting, independent of the
	# world's clock/weather. This capture is not full-environment acceptance.
	var camera := Camera3D.new()
	game.add_child(camera)
	var up := explorer.global_transform.basis.y.normalized()
	var surface: Node = game.get_node("Terrain3DGameplay")
	var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	var ground := explorer.global_position + up * (float(diagnostics.surface_height) - float(diagnostics.player_height))
	camera.global_position = ground + up * 30 + explorer.global_transform.basis.z.normalized() * 20
	camera.look_at(ground, up)
	camera.environment = Environment.new()
	camera.environment.background_mode = Environment.BG_COLOR
	camera.environment.background_color = Color(.04, .08, .12)
	camera.environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	camera.environment.ambient_light_color = Color.WHITE
	camera.environment.ambient_light_energy = .7
	camera.current = true
	var light := DirectionalLight3D.new()
	game.add_child(light)
	light.global_transform = camera.global_transform
	light.light_energy = 1.5
	await _frames(5)
	await RenderingServer.frame_post_draw
	root.get_texture().get_image().save_png(_fixture.path_join("runtime-terrain.png"))
	drawer.call("SetBenchmarkDrawerOpen", true)
	var definition := drawer.find_child("RuntimeSurfaceDefinition", true, false) as Label
	if definition == null or not definition.text.contains("generator v17") or not definition.text.contains(str(diagnostics.terrain_definition_hash)):
		_fail("grave drawer lacks initialized runtime definition/hash")
		return
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	for index in tabs.get_tab_count():
		if tabs.get_tab_title(index) == "Terrain3D": tabs.current_tab = index
	var section := drawer.find_child("SectionSurfaceeditingandcamera", true, false)
	if section != null: section.set("folded", false)
	await _frames(4)
	await RenderingServer.frame_post_draw
	root.get_texture().get_image().save_png(_fixture.path_join("runtime-terrain-drawer.png"))
	drawer.call("SetBenchmarkDrawerOpen", false)
	camera.free()
	light.free()
	game.get_node("Explorer/Head/Camera3D").current = true
	explorer.set_physics_process(true)

func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()

func _frames(count: int) -> void:
	for ignored in count: await process_frame

func _fail(message: String) -> void:
	push_error("terrain_generation_world_smoke: " + message)
	quit(1)
