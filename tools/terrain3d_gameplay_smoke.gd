extends SceneTree

const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const WORLD_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LEGACY_SENTINEL := "terrain3d-gameplay-smoke-legacy-sentinel\n"
const WAIT_FRAMES := 180

var _fixture := ""
var _finished := false


func _init() -> void:
	var timeout := create_timer(90.0)
	timeout.timeout.connect(func(): _fail("timed out waiting for Terrain3D gameplay smoke"))
	_fixture = "res://.godot/terrain3d_gameplay_smoke_%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "terrain3d_surface")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_fixture.path_join("regions")))
	_write_legacy_sentinels()
	var game := await _create_surface_game()
	if game == null:
		return

	var surface: Node = game.get_node_or_null("Terrain3DGameplay")
	var grid: Node = game.get_node_or_null("ChunkGridManager")
	var explorer: Node = game.get_node_or_null("Explorer")
	var editor: Node = game.get_node_or_null("GridEditController")
	var saves: Node = game.get_node_or_null("WorldSaveManager")
	if surface == null or grid == null or explorer == null or editor == null or saves == null:
		_fail("integration scene lacks Terrain3D gameplay/save dependencies")
		return
	if not bool(grid.get("UseTerrain3DSurface")) or bool(grid.get("IsSmoothTerrainReplacementActive")):
		_fail("world launcher did not select the Terrain3D surface backend")
		return
	if not bool(explorer.get("UseTerrain3DSurfacePhysics")) or not bool(editor.get("UseTerrain3DSurfaceBackend")):
		_fail("surface physics/edit controller hooks were not enabled")
		return
	if not bool(surface.call("EnsureInitialized")) or not bool(surface.get("IsReady")):
		_fail("Terrain3D gameplay failed to initialize: %s" % surface.get("LastError"))
		return
	if not _presentation_isolated(game):
		return
	if not _assert_tuning_tabs(game):
		return

	var before: Dictionary = surface.call("GetSurfaceDiagnostics")
	var around := float(before.get("frame_around", 0.0))
	var across := float(before.get("frame_across", 0.0))
	var water_around := float(before.get("player_around", around))
	var water_across := float(before.get("player_across", across))
	var water_surface := float(surface.call("SampleHeight", water_around, water_across))
	# Reset near the known water surface so this samples buoyancy from a fresh
	# seabed settle rather than accepting an already-floating equilibrium.
	explorer.call("SetCanonicalSpawnMeters", water_around, water_surface + 1.0, water_across)
	await _physics_frames(2)
	var water: Dictionary = surface.call("GetSurfaceDiagnostics")
	if bool(water.get("swimming", false)):
		var swim_height := float(water.get("player_height", NAN))
		await _physics_frames(30)
		var swim: Dictionary = surface.call("GetSurfaceDiagnostics")
		var swim_velocity: Vector3 = swim.get("velocity", Vector3.ZERO)
		if float(swim.get("player_height", NAN)) <= swim_height + 0.05 or swim_velocity.length() <= 0.01:
			_fail("surface swimming did not rise with nonzero world velocity: %s" % swim)
			return
	else:
		_fail("known underwater spawn did not enter swimming")
		return
	# The following collider check targets dry seafloor contact, not buoyancy.
	explorer.set("EnableSwimming", false)
	await _physics_frames(180)
	if not bool(surface.call("ApplySurfaceBrushAtCanonical", around, across, 2.0)):
		_fail("surface raise brush was rejected: %s" % surface.get("LastError"))
		return
	await _frames(4)
	var raised := await _ground_height(surface, explorer)
	if is_nan(raised):
		_fail("explorer could not establish Terrain3D ground contact after raise: %s" % _floor_diagnostics(surface, explorer))
		return
	if not bool(surface.call("ApplySurfaceBrushAtCanonical", around, across, -2.0)):
		_fail("surface lower brush was rejected: %s" % surface.get("LastError"))
		return
	await _frames(4)
	var lowered := await _ground_height(surface, explorer)
	if is_nan(lowered) or absf(raised - lowered) < 1.5:
		_fail("Terrain3D collider did not reflect raise/lower edits: %s -> %s" % [raised, lowered])
		return
	var persisted_before := float(surface.call("SampleHeight", around, across))
	if not bool(surface.call("ApplySurfaceBrushAtCanonical", around, across, 1.5)):
		_fail("persistent Terrain3D brush was rejected: %s" % surface.get("LastError"))
		return
	var persisted_after := float(surface.call("SampleHeight", around, across))
	if is_nan(persisted_after) or absf(persisted_after - persisted_before) < 1.25:
		_fail("persistent Terrain3D edit did not change the sampled surface height: %s -> %s" % [persisted_before, persisted_after])
		return

	# Exercise the real explorer drift path at a fractional location beyond 128 m.
	explorer.call("SetCanonicalSpawnMeters", around + 128.375, 128.25, across + 0.625)
	await _frames(8)
	var recentered: Dictionary = surface.call("GetSurfaceDiagnostics")
	if absf(float(recentered.get("player_around", 0.0)) - (around + 128.375)) > 0.01 \
		or absf(float(recentered.get("player_across", 0.0)) - (across + 0.625)) > 0.01:
		_fail("surface recenter lost the explorer's fractional canonical location: %s" % recentered)
		return

	if int(saves.call("SaveForSceneTransition")) != OK:
		_fail("WorldSaveManager rejected Terrain3D surface scene-transition save")
		return
	if not _legacy_sentinels_preserved():
		_fail("surface backend touched legacy voxel/region journal sentinels")
		return
	var saved_edits := int(surface.call("GetSurfaceDiagnostics").get("edits", 0))
	game.queue_free()
	await process_frame

	var reloaded := await _create_surface_game()
	if reloaded == null:
		return
	var reloaded_surface: Node = reloaded.get_node_or_null("Terrain3DGameplay")
	var reloaded_explorer: Node = reloaded.get_node_or_null("Explorer")
	if reloaded_surface == null or reloaded_explorer == null or not bool(reloaded_surface.call("EnsureInitialized")):
		_fail("fresh gameplay scene could not reload Terrain3D surface state")
		return
	reloaded_explorer.set("EnableSwimming", false)
	var reload: Dictionary = reloaded_surface.call("GetSurfaceDiagnostics")
	if not _assert_tuning_tabs(reloaded):
		return
	if int(reload.get("edits", 0)) < saved_edits \
		or absf(float(reload.get("player_around", 0.0)) - (around + 128.375)) > 0.01 \
		or absf(float(reload.get("player_across", 0.0)) - (across + 0.625)) > 0.01:
		_fail("fresh gameplay scene did not restore Terrain3D field/player state: %s" % reload)
		return
	if not _legacy_sentinels_preserved():
		_fail("reload touched legacy voxel/region journal sentinels")
		return
	var reloaded_height := float(reloaded_surface.call("SampleHeight", around, across))
	if is_nan(reloaded_height) or absf(reloaded_height - persisted_after) > 0.1:
		_fail("reloaded Terrain3D field lost the durable nonzero surface edit: %s -> %s" % [persisted_after, reloaded_height])
		return
	if OS.get_cmdline_user_args().has("--capture") and not OS.has_feature("headless"):
		if not await _capture_gameplay_and_drawer(reloaded):
			return
	reloaded.queue_free()
	await _frames(2)

	# A malformed Terrain3D world-state file must fail closed without replacing
	# either the bad document or the already durable surface-edit journal.
	var surface_edit_path := str(reload.get("save_path", ""))
	var surface_edit_bytes := FileAccess.get_file_as_bytes(surface_edit_path)
	var surface_state_path := _fixture.path_join("world_state_terrain3d_v1.json")
	var malformed := FileAccess.open(surface_state_path, FileAccess.WRITE)
	malformed.store_string("{ malformed terrain3d state")
	malformed.close()
	var blocked_game := await _create_surface_game()
	if blocked_game == null:
		return
	var blocked_surface: Node = blocked_game.get_node_or_null("Terrain3DGameplay")
	var blocked_saves: Node = blocked_game.get_node_or_null("WorldSaveManager")
	if blocked_surface == null or blocked_saves == null:
		_fail("blocked reload lacks Terrain3D gameplay/save nodes")
		return
	var blocked_explorer: Node = blocked_game.get_node_or_null("Explorer")
	if blocked_explorer != null: blocked_explorer.set("EnableSwimming", false)
	var blocked: Dictionary = blocked_surface.call("GetSurfaceDiagnostics")
	if not bool(blocked.get("save_blocked", false)):
		_fail("malformed Terrain3D world state did not block surface saves: %s" % blocked)
		return
	if int(blocked_saves.call("SaveForSceneTransition")) == OK:
		_fail("SaveForSceneTransition accepted malformed Terrain3D world state")
		return
	if bool(blocked_surface.call("ApplySurfaceBrushAtCanonical", around, across, 1.0)):
		_fail("surface brush was accepted after malformed Terrain3D world state")
		return
	blocked_game.queue_free()
	await _frames(2)
	if FileAccess.get_file_as_string(surface_state_path) != "{ malformed terrain3d state" \
		or FileAccess.get_file_as_bytes(surface_edit_path) != surface_edit_bytes:
		_fail("malformed Terrain3D state or saved edit bytes were overwritten during blocked teardown")
		return
	_finished = true
	print("Terrain3D gameplay smoke: PASS ", reload)
	quit(0)


func _create_surface_game() -> Node:
	# Drive the production launcher's certified configuration before the gameplay
	# scene enters the tree, so no default backend or user preference is observed.
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	await _frames(2)
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null:
		_fail("world_size_launcher did not expose its terrain backend selector")
		return null
	launcher.call("_on_certified_preset_pressed")
	if selector.selected != 1:
		_fail("new certified preset did not default to the Terrain3D surface backend")
		return null
	selector.select(0)
	selector.emit_signal("item_selected", 0)
	launcher.call("_refresh_backend_selector", true)
	if selector.selected != 0:
		_fail("explicit smooth backend selection did not persist through launcher refresh")
		return null
	selector.select(1)
	selector.emit_signal("item_selected", 1)
	var game: Node = WORLD_SCENE.instantiate()
	launcher.call("_configure_game", game, 5000000.0)
	# Assert the launcher-selected values; no backend flags are set manually.
	var grid: Node = game.get_node_or_null("ChunkGridManager")
	var explorer: Node = game.get_node_or_null("Explorer")
	var editor: Node = game.get_node_or_null("GridEditController")
	if grid == null or explorer == null or editor == null:
		_fail("launcher-created gameplay scene lacks Terrain3D backend hooks")
		return null
	var tuning: Node = game.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", _fixture.path_join("surface_tuning.tres"))
	for ignored in ["BiomeDecorations", "StandingGrass", "RingVolumetricClouds", "LocalVolumetricCloudRenderer", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "WaterFftController", "TerrainMinimap", "CertificationMonitor"]:
		var node: Node = game.get_node_or_null(ignored)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(game)
	current_scene = game
	launcher.queue_free()
	await _frames(WAIT_FRAMES)
	return game


func _presentation_isolated(game: Node) -> bool:
	var smooth: Node = game.get_node_or_null("SmoothTerrainGrid")
	if smooth != null and smooth.process_mode != Node.PROCESS_MODE_DISABLED:
		_fail("smooth terrain presentation remained enabled")
		return false
	return true


func _ground_height(surface: Node, explorer: Node) -> float:
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline:
		await physics_frame
		if explorer.has_method("is_on_floor") and bool(explorer.call("is_on_floor")):
			var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
			var streaming: Dictionary = diagnostics.get("streaming", {})
			if int(streaming.get("committed_edit_revision", -1)) != int(diagnostics.get("revision", 0)):
				continue
			# IsOnFloor still describes the previous MoveAndSlide immediately
			# after an atomic collider swap. Wait for contact with the new floor.
			if absf(float(diagnostics.get("player_height", NAN)) - float(diagnostics.get("surface_height", NAN))) > .2:
				continue
			return float(diagnostics.get("player_height", NAN))
	return NAN


func _floor_diagnostics(surface: Node, explorer: Node) -> Dictionary:
	return {"surface": surface.call("GetSurfaceDiagnostics"), "velocity": explorer.get("velocity")}


func _capture_gameplay_and_drawer(game: Node) -> bool:
	var surface: Node = game.get_node_or_null("Terrain3DGameplay")
	var explorer: Node = game.get_node_or_null("Explorer")
	if surface == null or explorer == null:
		_fail("Terrain3D gameplay capture nodes are unavailable")
		return false
	var source: Dictionary = surface.call("GetSurfaceDiagnostics")
	var dry_around := float(source.get("player_around", 0.0))
	var dry_across := float(source.get("player_across", 0.0))
	var found_dry := float(surface.call("SampleHeight", dry_around, dry_across)) > 10.0
	for offset in [512.0, -512.0, 2048.0, -2048.0, 8192.0]:
		if float(surface.call("SampleHeight", dry_around + offset, dry_across)) > 10.0:
			dry_around += offset
			found_dry = true
			break
	if not found_dry:
		_fail("could not find a dry Terrain3D capture location")
		return false
	explorer.call("SetCanonicalSpawnMeters", dry_around, 64.0, dry_across)
	await _physics_frames(90)
	var camera: Camera3D = game.get_node_or_null("Explorer/Head/Camera3D")
	if camera != null: camera.rotation_degrees = Vector3(-48.0, 0.0, 0.0)
	await RenderingServer.frame_post_draw
	var gameplay_path := ProjectSettings.globalize_path(_fixture.path_join("terrain3d_gameplay.png"))
	if root.get_viewport().get_texture().get_image().save_png(gameplay_path) != OK:
		_fail("Terrain3D gameplay GPU capture failed")
		return false
	var drawer: Node = game.get_node_or_null("LiveTuningDrawer")
	if drawer == null or not drawer.has_method("SetBenchmarkDrawerOpen"):
		_fail("full tuning drawer is unavailable for Terrain3D capture")
		return false
	drawer.call("SetBenchmarkDrawerOpen", true)
	await process_frame
	await RenderingServer.frame_post_draw
	var drawer_path := ProjectSettings.globalize_path(_fixture.path_join("terrain3d_gameplay_tuning.png"))
	if root.get_viewport().get_texture().get_image().save_png(drawer_path) != OK:
		_fail("Terrain3D tuning drawer GPU capture failed")
		return false
	drawer.call("SetBenchmarkDrawerOpen", false)
	print("Terrain3D gameplay captures: ", gameplay_path, " | ", drawer_path)
	return true


func _frames(count: int) -> void:
	for _i in count:
		await process_frame


func _physics_frames(count: int) -> void:
	for _i in count:
		await physics_frame


func _assert_tuning_tabs(game: Node) -> bool:
	var drawer: Node = game.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		_fail("live tuning drawer is unavailable")
		return false
	var tabs: TabContainer = null
	for candidate in drawer.find_children("*", "TabContainer", true, false):
		tabs = candidate as TabContainer
		if tabs != null: break
	if tabs == null:
		_fail("live tuning drawer has no tab container")
		return false
	var terrain_index := -1
	var terrain_count := 0
	for index in tabs.get_tab_count():
		if tabs.get_tab_title(index) == "Terrain3D":
			terrain_count += 1
			terrain_index = index
	if terrain_count != 1 or tabs.get_tab_count() < 2:
		_fail("Terrain3D live tuning tab was not uniquely added beside normal tabs")
		return false
	tabs.current_tab = terrain_index
	return true


func _write_legacy_sentinels() -> void:
	for relative in ["world_state.json", "voxel_delta_journal.ndjson", "regions/a_0_x_0.ndjson", "smooth_terrain_edits_v1.json", "smooth_construction_v1.json"]:
		var file := FileAccess.open(_fixture.path_join(relative), FileAccess.WRITE)
		file.store_string(LEGACY_SENTINEL)


func _legacy_sentinels_preserved() -> bool:
	for relative in ["world_state.json", "voxel_delta_journal.ndjson", "regions/a_0_x_0.ndjson", "smooth_terrain_edits_v1.json", "smooth_construction_v1.json"]:
		if FileAccess.get_file_as_string(_fixture.path_join(relative)) != LEGACY_SENTINEL:
			return false
	return true


func _fail(message: String) -> void:
	if _finished: return
	_finished = true
	push_error(message)
	quit(1)
