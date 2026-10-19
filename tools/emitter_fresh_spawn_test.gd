extends SceneTree

## Fresh-world spawn (v5 runtime surface): a world with no saved player state starts at the main spire's 40 km viewpoint, facing the
## spire, on dry land or hovering. A second launch of the same slot (state now saved) keeps the saved position instead.
## Optional `--capture=DIR` (run windowed, `--full` keeps clouds and water enabled) saves the fresh-spawn view.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const SAVED_AROUND := 1000000.0
const SAVED_VERTICAL := 6000.0
const SAVED_ACROSS := 80000.0
var fixture := ""
var deadline := 0
var game: Node
var explorer: Node
var surface: Node
var layer: Node
var drawer: Node
var failed := false
var capture_dir := ""
var full := false
var summary: Array[String] = []

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(1500.0).timeout.connect(func(): _fail("fresh spawn fixture timed out"))

func run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--capture="): capture_dir = argument.trim_prefix("--capture=")
		if argument == "--full": full = true
	fixture = "res://.godot/terrain-generation-tests/fresh-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalFresh"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	deadline = Time.get_ticks_msec() + 1450000

	# Phase 1: no saved state, so the main Far spawn applies.
	if not await _launch(): return
	var main_site := int(drawer.call("FindEmitterSite", "main"))
	var main_info: Dictionary = _find_main()
	if main_site < 0 or main_info.is_empty(): _fail("no main site"); return
	var main_around := float(main_info["around"]); var main_across := float(main_info["across"])
	var settled := 0
	var start := Time.get_ticks_msec()
	var hover := false
	while settled < 40 and Time.get_ticks_msec() - start < 180000:
		if not await _step(): return
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		hover = bool(explorer.get("NoclipEnabled"))
		var h := float(diag.get("player_height", NAN))
		var ground := float(surface.call("SampleCollisionHeight", float(diag["player_around"]), float(diag["player_across"])))
		var ok := (not bool(explorer.get("IsSpawnTerrainSettling"))) and (h >= maxf(ground, 0.0) + 195.0 if hover else (bool(explorer.call("is_on_floor")) and absf(h - ground) <= 0.25))
		settled = settled + 1 if ok else 0
	if settled < 40: _fail("fresh spawn never settled (hover=%s diag=%s)" % [hover, surface.call("GetSurfaceDiagnostics")]); return
	var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
	var d_around := absf(fposmod(float(diag["player_around"]) - main_around + 15707968.0, 31415936.0) - 15707968.0)
	var d_across := absf(float(diag["player_across"]) - main_across)
	if d_around < 37000.0 or d_around > 43000.0: _fail("fresh spawn is %.0f m from the main spire along the ring (wanted about 40 km)" % d_around); return
	if d_across > 2500.0: _fail("fresh spawn is %.0f m off the centre line" % d_across); return
	var alignment := float(drawer.call("MeasureEmitterViewAlignmentToSite", main_site, false))
	if not (alignment > 0.99): _fail("fresh spawn alignment %.4f" % alignment); return
	var ground_now := float(surface.call("SampleCollisionHeight", float(diag["player_around"]), float(diag["player_across"])))
	if not hover and ground_now < 2.0: _fail("fresh spawn grounded on %.2f m ground (not dry land)" % ground_now); return
	summary.append("fresh d_around=%.0f d_across=%.0f %s align=%.4f" % [d_around, d_across, "hover" if hover else "grounded", alignment])
	if capture_dir != "": await _capture("fresh_spawn")
	print("EMITTER_FRESH_SPAWN phase1 ", " ".join(summary))

	# Phase 2: move, free the game (the save manager writes world_state on exit), launch the same slot again.
	if not bool(explorer.call("TeleportToCanonicalMeters", SAVED_AROUND, SAVED_VERTICAL, SAVED_ACROSS, 30.0, 10.0, true)): _fail("could not move to the saved position"); return
	await _frames(5)
	var state_file := ProjectSettings.globalize_path(fixture).path_join("world_state_surface_runtime_v1.json")
	game.queue_free()
	await _frames(10)
	if not FileAccess.file_exists(state_file): _fail("no world state was saved on exit (%s)" % state_file); return
	if not await _launch(): return
	var restored := false
	start = Time.get_ticks_msec()
	var diag2: Dictionary = {}
	while Time.get_ticks_msec() - start < 120000:
		if not await _step(): return
		diag2 = surface.call("GetSurfaceDiagnostics")
		if absf(float(diag2.get("player_around", 0.0)) - SAVED_AROUND) < 50.0: restored = true; break
	if not restored: _fail("saved position was not restored: %s" % [{"around": diag2.get("player_around"), "height": diag2.get("player_height"), "across": diag2.get("player_across")}]); return
	await _frames(60)
	diag2 = surface.call("GetSurfaceDiagnostics")
	if absf(float(diag2["player_around"]) - SAVED_AROUND) > 50.0 or absf(float(diag2["player_across"]) - SAVED_ACROSS) > 50.0:
		_fail("saved position drifted to %.0f / %.0f / %.0f" % [diag2["player_around"], diag2["player_height"], diag2["player_across"]]); return
	print("EMITTER_FRESH_SPAWN=PASS %s saved_restored_around=%.1f across=%.1f height=%.1f (vertical saved as %.0f, informational only)" % [" ".join(summary), float(diag2["player_around"]), float(diag2["player_across"]), float(diag2["player_height"]), SAVED_VERTICAL])
	game.queue_free(); await process_frame; quit(0)

func _launch() -> bool:
	game = await _create_game()
	if game == null: return false
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	layer = game.get_node_or_null("EmitterBores")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	if explorer == null or surface == null or layer == null or drawer == null: _fail("missing explorer, gameplay, bore layer or drawer"); return false
	var waited := 0
	while waited < 900 and Time.get_ticks_msec() < deadline:
		if int(drawer.call("FindEmitterSite", "main")) >= 0 and not (layer.call("FindNearestBoreSite", 1000000.0, 100000.0) as Dictionary).is_empty(): return true
		waited += 1
		await physics_frame
	_fail("emitter navigation never bound")
	return false

func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: _fail("launcher lacks terrain backend selector"); return null
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
	var disabled := ["BiomeDecorations", "StandingGrass", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "TerrainMinimap", "CertificationMonitor"]
	if not full: disabled.append_array(["RingVolumetricClouds", "LocalVolumetricCloudRenderer", "WaterFftController"])
	for name in disabled:
		var node: Node = next.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_fail("startup readiness deadline")
	return null

func _find_main() -> Dictionary:
	var scan := 0.0
	while scan < 31415936.0:
		var info: Dictionary = layer.call("FindNearestBoreSite", scan, 159000.0)
		if not info.is_empty() and bool(info["is_main"]): return info
		scan += 20000.0
	return {}

func _capture(shot_name: String) -> void:
	DirAccess.make_dir_recursive_absolute(capture_dir)
	await _frames(45)
	var image := root.get_texture().get_image()
	var path := capture_dir.path_join(shot_name + ".png")
	image.save_png(path)
	print("EMITTER_FRESH_CAPTURE ", path)

func _step() -> bool:
	await physics_frame
	return not failed

func _frames(count: int) -> void:
	for ignored in count: await process_frame

func _fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("EMITTER_FRESH_SPAWN=FAIL " + message); quit(1)
