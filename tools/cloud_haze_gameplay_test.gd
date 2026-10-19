extends SceneTree
# Matched full-game capture for the Sunshine/ring-atmosphere handoff.  This
# owns an isolated settings directory and never writes player preferences.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const FIXTURE_AROUND := 16443476.0
const FIXTURE_ACROSS := 38692.0
const SETTLE_FRAMES := 24

var fixture := ""
var game: Node
var surface: Node
var explorer: Node
var camera: Camera3D
var clock: Node
var drawer: Node
var graphics: Node
var bridge: Node
var placement: RefCounted
var report := {"status":"RUNNING", "captures":[]}
var backdrop_images := {}
var frozen_driver: Node
var frozen_driver_mode := Node.PROCESS_MODE_INHERIT
var frozen_weather_refresh := 0.25

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null:
		return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/cloud_haze_gameplay/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var old_session := root.get_node_or_null("WorldSession")
	if old_session != null: old_session.name = "WorldSessionOriginalCloudHaze"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	surface = game.get_node_or_null("Terrain3DGameplay")
	explorer = game.get_node_or_null("Explorer")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	clock = game.get_node_or_null("RingDayNightCycle")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	graphics = game.get_node_or_null("GraphicsQuality")
	bridge = game.get_node_or_null("SunshineCloudBridge")
	placement = FIXTURE_HELPER.new()
	if surface == null or explorer == null or camera == null or clock == null or drawer == null or graphics == null or bridge == null:
		return fail("missing gameplay cloud-haze nodes")
	var ground := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	if not is_finite(ground): return fail("fixture terrain height is non-finite")
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground, FIXTURE_ACROSS)
	# A high pitch places the nearby 2--6 km cloud band in the centre of the shot.
	explorer.call("SetPresentationView", 0.0, 65.0)
	if not await wait_ready(): return
	if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")):
		return fail("fixture remained underwater")
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	if not bool(placement.call("Place", explorer, FIXTURE_AROUND, ground, FIXTURE_ACROSS)):
		return fail("could not set canonical fixture pose")
	# Place primes the camera with its terrain-capture pitch; restore overhead.
	explorer.call("SetPresentationView", 0.0, 65.0)
	clock.set("Paused", true)
	clock.call("SetCycleFraction", select_noon_phase())
	await frames(SETTLE_FRAMES)
	# Disable stochastic inputs and history only for the capture.  The cloud
	# resource remains rendered by its normal compositor, so alpha comparisons
	# expose whether the haze branch changes opacity rather than colour alone.
	freeze_cloud_sampling(true)
	await frames(SETTLE_FRAMES)
	await capture("haze_off", false, 30.0, 1.5)
	await capture("cinematic_30km", true, 30.0, 1.5)
	await capture("cinematic_10km", true, 10.0, 1.5)
	var off: Dictionary = report.captures[0]
	var cinematic: Dictionary = report.captures[1]
	var strong: Dictionary = report.captures[2]
	var normal_delta: Dictionary = compare_backdrops(backdrop_images.get("haze_off"), backdrop_images.get("cinematic_30km"))
	var strong_delta: Dictionary = compare_backdrops(backdrop_images.get("haze_off"), backdrop_images.get("cinematic_10km"))
	report["cinematic_30km_delta"] = normal_delta
	report["cinematic_10km_delta"] = strong_delta
	if float(normal_delta.get("cloud_pixels", 0)) <= 0.0: return fail("nearby cloud backdrop has no nonzero alpha")
	if float(normal_delta.get("mean_rgb_delta", 0.0)) <= 0.0005: return fail("30 km cinematic haze did not alter cloud RGB")
	if float(strong_delta.get("mean_rgb_delta", 0.0)) <= float(normal_delta.get("mean_rgb_delta", 0.0)):
		return fail("10 km haze control was not stronger than 30 km haze")
	if float(normal_delta.get("mean_alpha_delta", 1.0)) > 0.012:
		return fail("deterministic haze changed cloud alpha unexpectedly")
	report.status = "PASS"
	if not write_report(): return fail("could not write report")
	freeze_cloud_sampling(false)
	game.queue_free(); await process_frame
	print("CLOUD_HAZE_GAMEPLAY_TEST=PASS fixture=" + fixture)
	quit(0)

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: fail("missing terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		fail("runtime generation preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); install_isolated_preferences(next)
	# Allocate the diagnostics-capable accumulation textures before their first
	# compositor callback.  This fixture currently reports the public backdrop;
	# the private output_data texture has no addon readback API.
	var driver := next.get_node_or_null("SunshineCloudsDriver")
	var clouds = driver.get("clouds_resource") if driver != null else null
	if clouds != null: clouds.set("diagnostic_temporal_readback", true)
	launcher.call("_configure_game", next, 5000000.0)
	root.add_child(next); current_scene = next; launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	fail("runtime Terrain3D startup timed out")
	return null

func install_isolated_preferences(next: Node) -> void:
	var target := next.get_node_or_null("LiveTuningDrawer")
	if target == null: return
	for item in [["InstallGrassDefaults", "grass.tres"], ["InstallVegetationDefaults", "vegetation.tres"], ["InstallWaterDefaults", "water.tres"], ["InstallAntiAliasingDefaults", "anti_aliasing.tres"], ["InstallShadowDefaults", "shadows.tres"], ["InstallSurfaceTerrainDefaults", "surface.tres"], ["InstallAtmosphereDefaults", "atmosphere.tres"]]:
		target.call(str(item[0]), fixture.path_join(str(item[1])))

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 60000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d: Dictionary = surface.call("GetSurfaceDiagnostics")
		var stream: Dictionary = d.get("streaming", {})
		var native: Dictionary = d.get("native", {})
		var prep: Dictionary = native.get("preparation", {})
		var placed := absf(float(d.get("player_around", NAN)) - FIXTURE_AROUND) < 1.0 and absf(float(d.get("player_across", NAN)) - FIXTURE_ACROSS) < 1.0
		var ready := bool(d.get("ready", false)) and bool(d.get("visual_coverage", false)) and bool(prep.get("ready", false)) and int(stream.get("pending_jobs", 1)) == 0 and int(stream.get("active_jobs", 1)) == 0 and not bool(stream.get("packing", true))
		stable = stable + 1 if placed and ready else 0
		if stable >= 8: return true
	fail("Terrain3D workload did not settle")
	return false

func select_noon_phase() -> float:
	var phase := 0.0
	var highest := -INF
	for step in 48:
		var candidate := float(step) / 48.0
		clock.call("SetCycleFraction", candidate)
		var elevation := float(clock.get("SunElevationDegrees"))
		if elevation > highest: highest = elevation; phase = candidate
	return phase

func freeze_cloud_sampling(enabled: bool) -> void:
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	var clouds = driver.get("clouds_resource") if driver != null else null
	if clouds == null: return
	clouds.set("dither_speed", 0.0)
	clouds.set("ray_start_jitter_strength", 0.0)
	clouds.set("diagnostic_disable_temporal_history", enabled)
	if enabled:
		# Keep the bridge running so it still uploads the live atmosphere frame,
		# while freezing its weather writes and the driver's wind/light updates.
		frozen_driver = driver
		frozen_driver_mode = driver.process_mode
		frozen_weather_refresh = float(bridge.get("WeatherRefreshSeconds"))
		bridge.set("WeatherRefreshSeconds", 1000000000.0)
		driver.process_mode = Node.PROCESS_MODE_DISABLED
	elif frozen_driver != null:
		bridge.set("WeatherRefreshSeconds", frozen_weather_refresh)
		frozen_driver.process_mode = frozen_driver_mode
		frozen_driver = null

func capture(label: String, haze_enabled: bool, visibility_km: float, density: float) -> void:
	drawer.call("SetAtmospherePreset", 1) # Cinematic profile is the player-facing target.
	drawer.call("SetAtmosphereTuningValue", "Visibility", visibility_km)
	drawer.call("SetAtmosphereTuningValue", "Density", density)
	# Match the current saved cinematic profile used for the reported gameplay
	# symptom: color/haze strength without a sun-forward-scattering contribution.
	drawer.call("SetAtmosphereTuningValue", "Forward", 0.0)
	drawer.call("SetAtmosphereTuningValue", "Warmth", 0.0)
	drawer.call("SetAtmosphereEnabled", haze_enabled)
	await frames(SETTLE_FRAMES)
	await RenderingServer.frame_post_draw
	var final_image := root.get_viewport().get_texture().get_image()
	var final_path := fixture.path_join(label + "_final.png")
	if final_image.is_empty() or final_image.save_png(ProjectSettings.globalize_path(final_path)) != OK:
		return fail("final viewport capture failed " + label)
	var backdrop = bridge.call("GetCloudBackdropTexture")
	if backdrop == null: return fail("cloud backdrop unavailable " + label)
	var backdrop_image: Image = backdrop.get_image()
	var backdrop_path := fixture.path_join(label + "_backdrop.png")
	if backdrop_image.is_empty() or backdrop_image.save_png(ProjectSettings.globalize_path(backdrop_path)) != OK:
		return fail("cloud backdrop capture failed " + label)
	backdrop_images[label] = backdrop_image.duplicate()
	var profile = backdrop_profile()
	if haze_enabled and (not bool(profile.get("enabled", false)) or float(profile.get("clear_distance", 0.0)) <= 0.0):
		return fail("ring atmosphere profile was not uploaded " + label + ": " + JSON.stringify(profile))
	if not haze_enabled and bool(profile.get("enabled", true)):
		return fail("disabled haze left an enabled profile")
	report.captures.append({"label":label, "final_path":final_path, "backdrop_path":backdrop_path, "backdrop_size":[backdrop_image.get_width(), backdrop_image.get_height()], "backdrop_stats":image_stats(backdrop_image), "profile":profile, "cloud_geometry":cloud_geometry(), "atmosphere":graphics.call("GetAtmosphereTuningDiagnostics"), "camera_position":camera.global_position, "camera_basis":camera.global_transform.basis, "sun_elevation_degrees":clock.get("SunElevationDegrees")})

func backdrop_profile() -> Dictionary:
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	var clouds = driver.get("clouds_resource") if driver != null else null
	var values: Array = clouds.get("ring_atmosphere_profile") if clouds != null else []
	if values.size() != 10: return {"enabled":false, "count":values.size()}
	var packed: Array = []
	for value in values:
		packed.append([value.x, value.y, value.z, value.w])
	return {"enabled":float(values[0].x) > 0.5, "radius":values[0].y, "clear_distance":values[3].x, "full_distance":values[3].y, "distance_haze_opacity":values[2].z, "scattering_energy":values[2].w, "camera_height":values[7].w, "outward":packed[7], "axis":packed[8], "to_sun":packed[9], "packed":packed}

func cloud_geometry() -> Dictionary:
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	var clouds = driver.get("clouds_resource") if driver != null else null
	var surface_height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS))
	var camera_height := float(placement.call("ResolveCameraCanonicalHeight", game.get_node_or_null("ChunkGridManager"), camera))
	var cloud_floor := float(clouds.get("cloud_floor")) if clouds != null else NAN
	var cloud_ceiling := float(clouds.get("cloud_ceiling")) if clouds != null else NAN
	# The compositor currently does not publish output_data.b for CPU readback.
	# This reports the closest possible vertical path to the cloud band instead,
	# making a naturally weak near-haze result distinguishable from no upload.
	return {"surface_height_m":surface_height, "camera_height_m":camera_height, "cloud_floor_m":cloud_floor, "cloud_ceiling_m":cloud_ceiling, "minimum_vertical_cloud_distance_m":maxf(0.0, cloud_floor - camera_height), "depth_readback":"unpublished; output_data.b is GPU-private"}

func image_stats(image: Image) -> Dictionary:
	var pixels := 0
	var cloud_pixels := 0
	var alpha_total := 0.0
	for y in range(0, image.get_height(), 2):
		for x in range(0, image.get_width(), 2):
			var color := image.get_pixel(x, y)
			pixels += 1; alpha_total += color.a
			if color.a > 0.02: cloud_pixels += 1
	return {"sampled_pixels":pixels, "cloud_pixels":cloud_pixels, "mean_alpha":alpha_total / max(1, pixels)}

func compare_backdrops(a: Image, b: Image) -> Dictionary:
	# Compare GPU readback images directly. PNGs remain useful visual artifacts,
	# but their 8-bit conversion could hide a deliberately subtle haze change.
	if a.is_empty() or b.is_empty() or a.get_size() != b.get_size(): return {"error":"incompatible backdrop images"}
	var cloud_pixels := 0
	var rgb_total := 0.0
	var alpha_total := 0.0
	for y in range(0, a.get_height(), 2):
		for x in range(0, a.get_width(), 2):
			var ca := a.get_pixel(x, y); var cb := b.get_pixel(x, y)
			if maxf(ca.a, cb.a) <= 0.02: continue
			cloud_pixels += 1
			rgb_total += Vector3(ca.r, ca.g, ca.b).distance_to(Vector3(cb.r, cb.g, cb.b)) / sqrt(3.0)
			alpha_total += absf(ca.a - cb.a)
	return {"cloud_pixels":cloud_pixels, "mean_rgb_delta":rgb_total / max(1, cloud_pixels), "mean_alpha_delta":alpha_total / max(1, cloud_pixels)}

func frames(count: int) -> void:
	for ignored in count: await process_frame

func write_report() -> bool:
	if fixture.is_empty(): return false
	var file := FileAccess.open(ProjectSettings.globalize_path(fixture.path_join("report.json")), FileAccess.WRITE)
	if file == null: return false
	file.store_string(JSON.stringify(report, "\t")); file.close()
	return true

func fail(message: String) -> void:
	report.status = "FAIL"
	report["error"] = message
	write_report()
	push_error("CLOUD_HAZE_GAMEPLAY_TEST=FAIL " + message)
	quit(1)
