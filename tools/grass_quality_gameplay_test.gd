extends SceneTree

# Real integration-scene capture: whole-frame values include terrain/weather and are not GPU timings.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/GrassQualityValidation.cs")
var game: Node
var driver: Node
var grass: Node
var capture_camera: Camera3D
var samples: Array[float] = []
var previous_usec := 0
var frame := 0
var settled := false
var captures_complete := false
var settled_frames := 0
var focus_diagnostics: Dictionary
var lighting_captures: Array[Dictionary] = []
var failed := false

func _initialize() -> void:
	call_deferred("boot")

func boot() -> void:
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		fail("LiveTuningDrawer missing before scene entry"); return
	drawer.call("InstallWaterDefaults", "res://.godot/grass_lod/isolated_live_tuning.tres")
	drawer.call("InstallGrassDefaults", "res://.godot/grass_lod/isolated_grass_tuning.tres")
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	root.add_child(game)
	for i in range(12): await process_frame
	driver = DRIVER.new()
	game.add_child(driver)
	if not bool(driver.call("VerifyProfileContract")):
		fail("pure profile contract"); return
	if not bool(driver.call("FindAndPlaceGrassland", game)):
		fail("no dry grassland focus"); return
	grass = game.get_node_or_null("StandingGrass")
	if grass == null:
		fail("StandingGrass missing"); return
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE

func _process(_delta: float) -> bool:
	if grass == null: return false
	frame += 1
	if frame > 1200:
		fail("grass did not settle roots=%s pending=%s/%s" % [grass.get("ResidentGrassInstanceCount"), grass.get("PendingGrassTileBuildCount"), grass.get("PendingGrassUploadCount")]); return true
	if not settled:
		if frame < 60: return false
		if int(grass.get("ResidentGrassInstanceCount")) <= 0: return false
		if int(grass.get("PendingGrassTileBuildCount")) != 0 or int(grass.get("PendingGrassUploadCount")) != 0:
			settled_frames = 0; return false
		if not bool(grass.get("EnableDistanceLod")) or float(grass.get("BladesPerSquareMeter")) <= 0:
			fail("dense distance LOD profile was not active"); return true
		settled_frames += 1
		if settled_frames < 3: return false
		focus_diagnostics = driver.call("GetFocusDiagnostics", game)
		if int(focus_diagnostics.get("focus_roots", 0)) <= 0:
			fail("settled roots do not cover teleported grassland focus"); return true
		settled = true
		call_deferred("capture_route")
		return false
	if not captures_complete: return false
	var now := Time.get_ticks_usec()
	if previous_usec != 0: samples.append((now - previous_usec) / 1000.0)
	previous_usec = now
	if samples.size() < 120: return false
	var ordered := samples.duplicate(); ordered.sort()
	var total := 0.0
	for value in samples: total += value
	var report := {
		"whole_frame_not_gpu": true, "samples": samples.size(),
		"frame_mean_ms": total / samples.size(), "frame_p99_ms": ordered[int((ordered.size() - 1) * .99)],
		"resident_roots": grass.get("ResidentGrassInstanceCount"), "resident_tiles": grass.get("ResidentGrassTileCount"),
		"visible_prefix_draw_roots": grass.get("InstalledGrassClumpCount"), "rendered_tiles": grass.get("RenderedGrassTileCount"),
		"focus": focus_diagnostics, "lighting_captures": lighting_captures, "eclipse": {"skipped": true, "reason": "no deterministic local eclipse event selected"} }
	var output := FileAccess.open("res://artifacts/grass_lod/gameplay_stats.json", FileAccess.WRITE)
	if output == null: fail("report write failed"); return true
	output.store_string(JSON.stringify(report, "\t")); output.close()
	print("GRASS_QUALITY_GAMEPLAY=PASS ", report)
	game.queue_free(); quit(0); return true

func capture(name: String) -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/grass_lod"))
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	if image.save_png("res://artifacts/grass_lod/%s.png" % name) != OK:
		fail("capture failed " + name)

func capture_route() -> void:
	capture_camera = driver.call("CreateDedicatedCaptureCamera", game) as Camera3D
	if capture_camera == null:
		fail("dedicated grass capture camera unavailable"); return
	print("GRASS_QUALITY_CAPTURE near ", focus_diagnostics, " roots=", grass.get("ResidentGrassInstanceCount"), " settings=", settings_snapshot())
	await capture("gameplay_near")
	if failed: return
	var cycle_state: Dictionary = driver.call("SetCaptureCycle", game, 0.0)
	await process_frame
	await process_frame
	await capture_lighting("gameplay_day", "day")
	if failed: return
	if float(lighting_captures.back().get("daylight", 0.0)) <= float(lighting_captures.back().get("night", 1.0)): fail("day capture did not resolve daylight"); return
	driver.call("SetCaptureCycle", game, 0.5)
	await process_frame
	await process_frame
	await capture_lighting("gameplay_night", "night")
	if failed: return
	if float(lighting_captures.back().get("night", 0.0)) <= float(lighting_captures.back().get("daylight", 1.0)): fail("night capture did not resolve night"); return
	driver.call("SetCaptureCycle", game, 0.0)
	driver.call("BeginStormWindCapture", game)
	for i in range(12): await process_frame
	await capture_lighting("gameplay_storm_wind", "storm_wind")
	if failed: return
	driver.call("EndStormWindCapture", game)
	driver.call("RestoreCaptureCycle", game, cycle_state)
	driver.call("SetDedicatedCaptureView", capture_camera, true)
	await process_frame
	await process_frame
	print("GRASS_QUALITY_CAPTURE transition ", driver.call("GetFocusDiagnostics", game), " roots=", grass.get("ResidentGrassInstanceCount"), " settings=", settings_snapshot())
	await capture("gameplay_higher_look_down")
	if failed: return
	captures_complete = true

func capture_lighting(file_name: String, label: String) -> void:
	var state: Dictionary = driver.call("GetLightingDiagnostics", game)
	state["label"] = label
	state["file"] = file_name + ".png"
	state["roots"] = grass.get("ResidentGrassInstanceCount")
	state["visible_prefix"] = grass.get("InstalledGrassClumpCount")
	lighting_captures.append(state)
	print("GRASS_QUALITY_LIGHTING ", state)
	await capture(file_name)

func settings_snapshot() -> Dictionary:
	var quality := game.get_node_or_null("GraphicsQuality")
	return {"density": grass.get("BladesPerSquareMeter"), "near": grass.get("LodNearDistanceMeters"),
		"far": grass.get("LodFarDistanceMeters"), "near_ratio": grass.get("LodNearDensity"),
		"far_ratio": grass.get("LodFarDensity"), "preset": quality.get("CurrentPresetName") if quality != null else "unavailable"}

func fail(reason: String) -> void:
	if failed: return
	failed = true
	push_error("GRASS_QUALITY_GAMEPLAY=FAIL " + reason)
	if game != null: game.queue_free()
	quit(1)
