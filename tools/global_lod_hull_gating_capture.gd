extends SceneTree

## Windowed repro for the global-LOD hull roof showing as a pale panel while terrain segments stream in.
## Usage: godot --path . --script res://tools/global_lod_hull_gating_capture.gd -- --capture=DIR
## Spawns grounded near the ring edge, flight-teleports to 150 km altitude before the staged global build finishes,
## captures at frame 400, again once the build completes, and once at ground level near the edge walls.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var fixture := ""
var capture_dir := ""
var game: Node
var failed := false

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(900.0).timeout.connect(func(): _fail("capture timed out"))

func run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--capture="): capture_dir = argument.trim_prefix("--capture=")
	if capture_dir == "": _fail("pass --capture=DIR"); return
	DirAccess.make_dir_recursive_absolute(capture_dir)
	fixture = "res://.godot/terrain-generation-tests/hullgate-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalHullGate"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await _create_game()
	if game == null: return
	var explorer: Node = game.get_node("Explorer")
	var surface: Node = game.get_node("Terrain3DGameplay")
	var lod: Node = game.get_node("GlobalTerrainLod3")
	print("HULLGATE pending_at_start=", lod.get("PendingInitialSegmentCount"))
	explorer.call("SetCanonicalSpawnMeters", 30590000.0, 600.0, 79500.0)
	for i in 120: await process_frame
	explorer.call("TeleportToCanonicalMeters", 30650000.0, 150000.0, 79500.0, 180.0, -68.0, true)
	print("HULLGATE teleported pending=", lod.get("PendingInitialSegmentCount"))
	for i in 400: await process_frame
	print("HULLGATE pending_at_capture=", lod.get("PendingInitialSegmentCount"), " refreshes=", lod.get("RegionalVisibilityRefreshCount"))
	_save("1_high_during_build")
	var start := Time.get_ticks_msec()
	while int(lod.get("PendingInitialSegmentCount")) > 0 and Time.get_ticks_msec() - start < 400000: await process_frame
	for i in 60: await process_frame
	print("HULLGATE build_done pending=", lod.get("PendingInitialSegmentCount"))
	_save("2_high_after_build")
	var info: Dictionary = surface.call("GetSurfaceDiagnostics")
	explorer.call("TeleportToCanonicalMeters", 30650000.0, 400.0, 79000.0, 0.0, -5.0, true)
	for i in 240: await process_frame
	_save("3_ground_edge_yaw0")
	explorer.call("TeleportToCanonicalMeters", 30650000.0, 400.0, 79000.0, 90.0, -5.0, true)
	for i in 120: await process_frame
	_save("4_ground_edge_yaw90")
	explorer.call("TeleportToCanonicalMeters", 30650000.0, 400.0, 79000.0, -90.0, -5.0, true)
	for i in 120: await process_frame
	_save("5_ground_edge_yawm90")
	print("HULLGATE=DONE")
	quit(0)

func _save(shot: String) -> void:
	var path := capture_dir.path_join(shot + ".png")
	root.get_texture().get_image().save_png(path)
	print("HULLGATE_CAPTURE ", path)

func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher)
	for i in 2: await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	selector.select(2); selector.emit_signal("item_selected", 2)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("runtime surface definition preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", fixture.path_join("surface_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEmitterSiteDefaults"):
		tuning.call("InstallEmitterSiteDefaults", fixture.path_join("emitter_tuning.tres"))
	for name in ["BiomeDecorations", "StandingGrass", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "TerrainMinimap", "CertificationMonitor", "RingVolumetricClouds", "LocalVolumetricCloudRenderer", "WaterFftController"]:
		var node: Node = next.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	var t := Time.get_ticks_msec()
	while Time.get_ticks_msec() - t < 300000:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_fail("startup readiness deadline")
	return null

func _fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("HULLGATE=FAIL " + message); quit(1)
