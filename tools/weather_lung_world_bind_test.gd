extends SceneTree

## End-to-end proof that the Weather Lung streamer binds in a REAL new v8 world (launcher defaults: hill-country landforms + Weather Lung on):
##   1. the terrain sampler carries the placement and the streamer binds, loads the POI scene and places its root at SiteTransform(...)
##      (compared with a fresh transform from the grid snapshot), and reports the world position and the distance from the spawn;
##   2. the grave-key teleport to the dam crest brings the player inside the load radius: the Detail scene attaches, the rig animates
##      (the controller's update count and the breath phase advance), and the structure collision switches on near the player;
##   3. the 40 km view keeps the station presented.
## Optional `--capture=DIR` (run windowed) saves the dam crest view.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var fixture := ""
var deadline := 0
var game: Node
var explorer: Node
var surface: Node
var drawer: Node
var streamer: Node
var failed := false
var capture_dir := ""
var poi_haze_arg := ""
var haze_model_arg := ""
var poi_cloud_shadow_arg := ""
var lock_exposure := true
var haze_master_arg := ""
var settle_frames := 60

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(1500.0).timeout.connect(func(): _fail("world bind test timed out"))

func run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--capture="): capture_dir = argument.trim_prefix("--capture=")
		elif argument.begins_with("--poi-haze="): poi_haze_arg = argument.trim_prefix("--poi-haze=")
		elif argument.begins_with("--haze-model="): haze_model_arg = argument.trim_prefix("--haze-model=")
		elif argument.begins_with("--poi-cloud-shadow="): poi_cloud_shadow_arg = argument.trim_prefix("--poi-cloud-shadow=")
		elif argument == "--auto-exposure": lock_exposure = false
		elif argument.begins_with("--haze="): haze_master_arg = argument.trim_prefix("--haze=")
		elif argument.begins_with("--settle="): settle_frames = int(argument.trim_prefix("--settle="))
	fixture = "res://.godot/terrain-generation-tests/weather-lung-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", true)
	ProjectSettings.set_setting("ringworld/runtime/surface_weather_lung_v8", true)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalWeatherLung"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	deadline = Time.get_ticks_msec() + 1450000

	if not await _launch(): return
	_apply_poi_haze_options()
	# 1. Bind and place.
	var start := Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < 240000 and not (bool(streamer.get("IsBound")) and bool(streamer.get("PoiReady"))):
		if not await _step(): return
	if bool(streamer.get("Unavailable")): _fail("streamer is Unavailable in the v8 world: " + str(streamer.call("DescribeStatus"))); return
	if not (bool(streamer.get("IsBound")) and bool(streamer.get("PoiReady"))): _fail("streamer never bound / loaded the POI: " + str(streamer.call("DescribeStatus"))); return
	await _frames(10)
	var poi: Node3D = streamer.get("PoiRoot")
	var expected: Transform3D = streamer.call("ExpectedWorldTransform")
	var error := poi.global_transform.origin.distance_to(expected.origin)
	for row in 3:
		for column in 3:
			error = maxf(error, absf(poi.global_transform.basis[row][column] - expected.basis[row][column]))
	if error > 0.001: _fail("POI root differs from SiteTransform by %.4f" % error); return
	var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
	var spawn_global: Vector3 = (explorer as Node3D).global_position
	var spawn_distance := spawn_global.distance_to(poi.global_position)
	var around_delta := fposmod(float(diag["player_around"]) - float(streamer.get("CentreAround")) + 15707968.0, 31415936.0) - 15707968.0
	var across_delta := float(diag["player_across"]) - float(streamer.get("CentreAcross"))
	print("WEATHER_LUNG_WORLD placement: ", streamer.call("DescribePlacement"))
	print("WEATHER_LUNG_WORLD poi world position=", poi.global_position, " transform_error=%.6f" % error)
	print("WEATHER_LUNG_WORLD spawn world position=", spawn_global, " distance_to_poi=%.0f m (around %.0f, across %.0f, canonical)" % [spawn_distance, around_delta, across_delta])
	if str(streamer.get("DetailState")) != "0" and int(streamer.get("DetailState")) != 0: _fail("detail loaded at the spawn (%s)" % streamer.get("DetailState")); return

	# 2. Dam crest teleport: detail, animation, collision.
	if not bool(drawer.call("RequestWeatherLungTeleport", 0)): _fail("teleport refused: " + str(drawer.get("WeatherLungTeleportMessage"))); return
	start = Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < 200000 and str(drawer.get("WeatherLungTeleportState")) == "Holding":
		if not await _step(): return
	if str(drawer.get("WeatherLungTeleportState")) != "Arrived": _fail("teleport: %s" % drawer.get("WeatherLungTeleportMessage")); return
	await _frames(30)
	var controller: Node = streamer.get("Controller")
	for control in ["WeatherLungLive", "WeatherLungEnabled", "WeatherLungCycle", "WeatherLungDetailRange", "WeatherLungLoad", "WeatherLungSunMode", "WeatherLungPhase", "WeatherLungTeleportDamcrest", "WeatherLungResult"]:
		if game.find_child(control, true, false) == null: _fail("grave-key control %s was not built" % control); return
	print("WEATHER_LUNG_WORLD grave-key section built: live status =", (game.find_child("WeatherLungLive", true, false) as Label).text.substr(0, 60))
	if int(streamer.get("DetailState")) != 2: _fail("detail not attached at the dam crest: " + str(streamer.call("DescribeStatus"))); return
	var updates0 := int(controller.get("UpdateCount"))
	var phase0 := float(controller.call("GetPhase"))
	await _frames(60)
	var updates1 := int(controller.get("UpdateCount"))
	var phase1 := float(controller.call("GetPhase"))
	if updates1 <= updates0: _fail("the rig did not update (%d -> %d), tier %s" % [updates0, updates1, controller.get("UpdateTier")]); return
	print("WEATHER_LUNG_WORLD dam crest: ", streamer.call("DescribeStatus"))
	print("WEATHER_LUNG_WORLD pieces: ", streamer.call("DescribeAttachPieces"))
	print("WEATHER_LUNG_WORLD attach: frames=%d pieces=%d worst_frame_ms=%.2f total_ms=%.2f budget_ms=%.1f" % [int(streamer.get("AttachFrames")), int(streamer.get("AttachSliceCount")), float(streamer.get("WorstAttachFrameMs")), float(streamer.get("AttachTotalMs")), float(streamer.get("AttachBudgetMs"))])
	print("WEATHER_LUNG_WORLD animation updates %d -> %d, phase %.4f -> %.4f, instantiate %.1f ms, collision bodies on %d/%d" % [
		updates0, updates1, phase0, phase1, float(streamer.get("LastDetailInstantiateMs")), int(streamer.get("CollisionBodiesOn")), int(streamer.get("CollisionBodyCount"))])
	if int(streamer.get("CollisionBodiesOn")) < 1: _fail("no structure collision body switched on at the dam crest"); return
	if capture_dir != "":
		DirAccess.make_dir_recursive_absolute(capture_dir)
		await _frames(settle_frames)
		root.get_texture().get_image().save_png(capture_dir.path_join("weather_lung_dam_crest.png"))

	# 3. 40 km view.
	if not bool(drawer.call("RequestWeatherLungTeleport", 2)): _fail("far teleport refused"); return
	start = Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < 200000 and str(drawer.get("WeatherLungTeleportState")) == "Holding":
		if not await _step(): return
	if str(drawer.get("WeatherLungTeleportState")) != "Arrived": _fail("far teleport: %s" % drawer.get("WeatherLungTeleportMessage")); return
	await _frames(30)
	print("WEATHER_LUNG_WORLD far view: ", streamer.call("DescribeStatus"))
	_print_poi_haze_state("far")
	_print_proxy_debug()
	if capture_dir != "":
		await _frames(settle_frames)
		root.get_texture().get_image().save_png(capture_dir.path_join("weather_lung_far_40km.png"))
	if not bool(streamer.get("PoiReady")): _fail("station lost at 40 km"); return
	print("WEATHER_LUNG_WORLD=PASS")
	game.queue_free(); await process_frame; quit(0)

## A/B switches for the POI architecture haze captures: --poi-haze=on|off, --poi-cloud-shadow=on|off, --haze-model=legacy|physical
## (all through the grave-key drawer, before the route starts) and exposure locking (default on; --auto-exposure keeps it).
func _apply_poi_haze_options() -> void:
	if drawer == null: return
	drawer.call("SetAtmosphereEnabled", haze_master_arg != "off")  # the certified launcher preset can leave the master haze off
	if haze_model_arg != "": drawer.call("SetAtmosphereHazeModel", 1 if haze_model_arg == "physical" else 0)
	if poi_haze_arg != "": drawer.call("SetAtmospherePoiHaze", poi_haze_arg != "off")
	if poi_cloud_shadow_arg != "": drawer.call("SetAtmospherePoiCloudShadow", poi_cloud_shadow_arg == "on")
	if lock_exposure:
		for node in root.find_children("*", "WorldEnvironment", true, false):
			var attributes = (node as WorldEnvironment).camera_attributes as CameraAttributesPractical
			if attributes != null: attributes.auto_exposure_enabled = false
		var day_side = game.find_child("DaySideAtmosphere", true, false) if game != null else null
		if day_side != null: day_side.set("storm_auto_exposure_enabled", false)
	print("POI_HAZE_OPTIONS poi_haze=%s cloud_shadow=%s model=%s lock_exposure=%s" % [poi_haze_arg, poi_cloud_shadow_arg, haze_model_arg, lock_exposure])

func _launch() -> bool:
	game = await _create_game()
	if game == null: return false
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	streamer = game.get_node_or_null("WeatherLung")
	if explorer == null or surface == null or drawer == null or streamer == null: _fail("missing explorer, gameplay, drawer or WeatherLung node"); return false
	return true

func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: _fail("launcher lacks terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", 2)
	var lung: CheckBox = launcher.get("weather_lung_toggle")
	print("WEATHER_LUNG_WORLD launcher toggle pressed=", lung.button_pressed, " disabled=", lung.disabled)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("runtime surface definition preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", fixture.path_join("surface_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEmitterSiteDefaults"):
		tuning.call("InstallEmitterSiteDefaults", fixture.path_join("emitter_tuning.tres"))
	if tuning != null and tuning.has_method("InstallWeatherLungDefaults"):
		tuning.call("InstallWeatherLungDefaults", fixture.path_join("weather_lung_tuning.tres"))
	var disabled := ["BiomeDecorations", "StandingGrass", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "TerrainMinimap", "CertificationMonitor",
		"LocalVolumetricCloudRenderer", "WaterFftController"]  # RingVolumetricClouds stays on: it owns the saved weather clock the breath phase follows
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

func _step() -> bool:
	await physics_frame
	return not failed

func _frames(count: int) -> void:
	for ignored in count: await process_frame

func _fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("WEATHER_LUNG_WORLD=FAIL " + message); quit(1)

## Prints the haze uniforms of the first POI-shader material under the station (A/B diagnostics).
func _print_poi_haze_state(label: String) -> void:
	var poi: Node = streamer.get("PoiRoot")
	if poi == null: return
	for node in poi.find_children("*", "MeshInstance3D", true, false):
		var mesh := (node as MeshInstance3D).mesh
		if mesh == null or mesh.get_surface_count() == 0: continue
		var material := mesh.surface_get_material(0) as ShaderMaterial
		if material == null: continue
		print("POI_HAZE_STATE %s %s: valid=%s enabled=%s model=%s camera=%s height=%s radius=%s" % [label, node.name, material.get_shader_parameter("ring_atmosphere_ray_frame_valid"), material.get_shader_parameter("poi_haze_enabled"), material.get_shader_parameter("atmosphere_haze_model"), material.get_shader_parameter("ring_atmosphere_camera_world_position"), material.get_shader_parameter("ring_atmosphere_camera_height_meters"), material.get_shader_parameter("ring_radius_meters")])
		return

func _print_proxy_debug() -> void:
	var poi: Node = streamer.get("PoiRoot")
	var proxy := poi.get_node_or_null("FarProxy") as MeshInstance3D
	if proxy == null: print("POI_HAZE_STATE no FarProxy"); return
	var cam := get_root().get_viewport().get_camera_3d()
	print("POI_HAZE_STATE proxy visible=%s override=%s surfaces=%d dist=%.0f camfar=%.0f layers=%d" % [proxy.is_visible_in_tree(), proxy.material_override, proxy.mesh.get_surface_count(), proxy.global_position.distance_to(cam.global_position), cam.far, proxy.layers])
	for s in proxy.mesh.get_surface_count():
		var m := proxy.mesh.surface_get_material(s) as ShaderMaterial
		print("  surf %d %s enabled=%s atm=%s mult=%s" % [s, m.resource_name if m else "null", m.get_shader_parameter("poi_haze_enabled") if m else "-", m.get_shader_parameter("ring_atmosphere_enabled") if m else "-", m.get_shader_parameter("atmosphere_opacity_multiplier") if m else "-"])
