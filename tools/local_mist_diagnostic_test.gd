extends SceneTree
# Isolated evidence-gathering fixture for optional local FogVolume mist. It does
# not alter a scene, production preference, or production environment asset.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const AROUND := 16443476.0
const ACROSS := 38692.0
const SAMPLE_FRAMES := 90
var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var camera: Camera3D
var clock: Node
var grass: Node
var trees: Node
var global_terrain: Node
var weather: Node
var bridge: Node
var placement: RefCounted
var environment: Environment
var fog_volume: FogVolume
var report := {"status":"RUNNING", "cases":[], "created_utc":Time.get_datetime_string_from_system(true, true), "engine":Engine.get_version_info(), "flags":{"isolated_runtime_v2":true,"global_fog_density_zero":true,"weather_frozen":true}}
var baseline_night_draw_calls := NAN
var baseline_day_draw_calls := NAN
var failed := false

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/local-mist-diagnostics/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalLocalMist"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer"); surface = game.get_node_or_null("Terrain3DGameplay")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	clock = game.get_node_or_null("RingDayNightCycle"); grass = game.get_node_or_null("StandingGrass"); trees = game.get_node_or_null("BiomeDecorations"); global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	weather = game.get_node_or_null("LocalWeatherEffects"); bridge = game.get_node_or_null("SunshineCloudBridge")
	placement = FIXTURE_HELPER.new()
	var world_environment := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if explorer == null or surface == null or camera == null or clock == null or grass == null or trees == null or global_terrain == null or weather == null or bridge == null or world_environment == null or world_environment.environment == null: return fail("required gameplay nodes are missing")
	# This fixture owns a separate game process; keep the live Environment so
	# established day/night, sky, weather, and Sunshine references update it.
	environment = world_environment.environment
	if not bool(surface.call("GetSurfaceDiagnostics").get("runtime_surface_generation", false)): return fail("runtime Terrain3D generation is not active")
	var feet := float(surface.call("SampleHeight", AROUND, ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", AROUND, feet, ACROSS); explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	weather.process_mode = Node.PROCESS_MODE_DISABLED
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.call("ApplyCloudAppearanceTuning")
	if not bool(placement.call("Place", explorer, AROUND, feet, ACROSS)): return fail("fixture placement failed")
	clock.set("Paused", true)
	clock.call("SetCycleFraction", noon_fraction())
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	for node in all_nodes(game): if node is SubViewport: RenderingServer.viewport_set_measure_render_time((node as SubViewport).get_viewport_rid(), true)
	create_local_mist()
	await capture_case("baseline_day", false, false, 0.0)
	if failed: return
	await capture_case("froxel_enabled_empty_day", true, false, 0.0)
	if failed: return
	await capture_case("mist_low_day", true, true, 0.012)
	if failed: return
	await capture_case("baseline_day_repeat", false, false, 0.0)
	if failed: return
	clock.call("SetCycleFraction", night_fraction())
	await capture_case("baseline_night", false, false, 0.0)
	if failed: return
	await capture_case("froxel_enabled_empty_night", true, false, 0.0)
	if failed: return
	await capture_case("mist_low_night", true, true, 0.012)
	if failed: return
	await capture_case("mist_low_night_sun_fog_zero", true, true, 0.012, 512.0, 0.0)
	if failed: return
	await capture_case("mist_shorter_range", true, true, 0.020, 256.0)
	if failed: return
	await capture_case("baseline_repeat", false, false, 0.0)
	if failed: return
	report.status = "PASS"
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file == null: return fail("could not write report")
	file.store_string(JSON.stringify(report, "\t")); file.close()
	fog_volume.queue_free(); game.queue_free(); await process_frame
	print("LOCAL_MIST_DIAGNOSTIC=PASS fixture=" + fixture)
	quit(0)

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector := launcher.get("terrain_backend_selector") as OptionButton
	if selector == null: fail("missing terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)): fail("runtime preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); install_isolated_preferences(next); launcher.call("_configure_game", next, 5000000.0); root.add_child(next); current_scene = next; launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	fail("runtime Terrain3D startup timed out"); return null

func install_isolated_preferences(next: Node) -> void:
	var drawer := next.get_node_or_null("LiveTuningDrawer")
	if drawer == null: return
	for item in [["InstallGrassDefaults","grass.tres"], ["InstallVegetationDefaults","vegetation.tres"], ["InstallWaterDefaults","water.tres"], ["InstallAntiAliasingDefaults","aa.tres"], ["InstallShadowDefaults","shadow.tres"], ["InstallSurfaceTerrainDefaults","surface.tres"], ["InstallAtmosphereDefaults","atmosphere.tres"]]: drawer.call(str(item[0]), fixture.path_join(str(item[1])))

func create_local_mist() -> void:
	# Global density remains zero: only this 128x24x128 m camera-local box is tested.
	environment.volumetric_fog_enabled = false; environment.volumetric_fog_density = 0.0; environment.volumetric_fog_length = 512.0
	fog_volume = FogVolume.new(); fog_volume.name = "FixtureLocalMist"; fog_volume.shape = RenderingServer.FOG_VOLUME_SHAPE_BOX; fog_volume.size = Vector3(128, 24, 128)
	var material := FogMaterial.new(); material.density = 0.012; material.albedo = Color(0.84, 0.89, 0.94, 1.0); fog_volume.material = material; fog_volume.visible = false; game.add_child(fog_volume)

func place_mist() -> void:
	var up: Vector3 = explorer.global_transform.basis.y.normalized()
	var forward: Vector3 = -camera.global_transform.basis.z
	forward = (forward - up * forward.dot(up)).normalized()
	fog_volume.global_position = camera.global_position + forward * 40.0
	fog_volume.look_at(fog_volume.global_position + forward, up)

func capture_case(label: String, fog_enabled: bool, mist_enabled: bool, density: float, length := 512.0, volumetric_sun_energy := -1.0) -> void:
	environment.volumetric_fog_enabled = fog_enabled; fog_volume.visible = mist_enabled; (fog_volume.material as FogMaterial).density = density; environment.volumetric_fog_length = length; place_mist()
	var star := game.get_node_or_null("StarLight") as DirectionalLight3D
	var previous_sun_fog_energy := star.light_volumetric_fog_energy if star != null else 0.0
	if star != null and volumetric_sun_energy >= 0.0: star.light_volumetric_fog_energy = volumetric_sun_energy
	# Let the frozen celestial/macro-shadow state settle before any timed frames.
	await frames(120)
	if environment.volumetric_fog_enabled != fog_enabled or absf(environment.volumetric_fog_density) > 0.000001: return fail("fixture global volumetric fog was overridden")
	if volumetric_sun_energy >= 0.0 and (star == null or absf(star.light_volumetric_fog_energy - volumetric_sun_energy) > 0.000001): return fail("sun fog-energy control was overridden")
	var elevation := float(clock.get("SunElevationDegrees"))
	var daylight := float(clock.get("DaylightFactor"))
	if "night" in label and (elevation >= 0.0 or daylight > 0.05): return fail("night case is not dark")
	if "day" in label and (elevation <= 0.0 or daylight <= 0.01): return fail("day case is not daylight")
	var timing := await sample_timings()
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image(); var image_path := fixture.path_join(label + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(image_path)) != OK: return fail("capture failed " + label)
	var sun_fog_energy_during := star.light_volumetric_fog_energy if star != null else 0.0
	if star != null and volumetric_sun_energy >= 0.0: star.light_volumetric_fog_energy = previous_sun_fog_energy
	var draw_distribution: Dictionary = timing.get("draw_calls", {})
	var draw_median := float(draw_distribution.get("median", NAN))
	var surface_diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	var entry := {"label":label, "fog_enabled":fog_enabled, "mist_enabled":mist_enabled, "density":density, "requested_sun_fog_energy":volumetric_sun_energy, "actual_sun_fog_energy_during_capture":sun_fog_energy_during, "image":image_path, "clock_fraction":float(clock.call("GetCycleTimeSeconds")) / maxf(1.0, float(clock.get("FullCycleSeconds"))), "sun_elevation_degrees":elevation, "daylight_factor":daylight, "viewport_size":root.get_viewport().size, "wind":{"global_gate_set":0.0, "weather_processing":false, "cloud_wind_multiplier":bridge.get("CloudWindMultiplier")}, "geometry":{"surface_regions":surface_diagnostics.get("regions"), "global_segments":global_terrain.get("ActiveSegmentCount")}, "vegetation":{"grass_roots":grass.get("ResidentGrassInstanceCount"), "tree_roots":trees.get("GpuTreeResidentRootCount")}, "environment":environment_state(), "fog_volume":{"visible":fog_volume.visible, "size":fog_volume.size, "position":fog_volume.global_position, "material_density":(fog_volume.material as FogMaterial).density}, "clouds":cloud_state(), "lights":light_state(), "draw_calls":draw_distribution, "timing":timing}
	entry.lights.volumetric_fog_energy = sun_fog_energy_during
	report.cases.append(entry)
	if label == "baseline_day": baseline_day_draw_calls = draw_median
	if label == "baseline_night": baseline_night_draw_calls = draw_median
	if label == "baseline_day_repeat": compare_baselines("day", baseline_day_draw_calls, draw_distribution)
	if label == "baseline_repeat": compare_baselines("night", baseline_night_draw_calls, draw_distribution)

func sample_timings() -> Dictionary:
	var main: Array[float] = []; var far: Array[float] = []; var draws: Array[float] = []
	var far_viewport := find_far_viewport()
	var started := Time.get_ticks_usec()
	while main.size() < SAMPLE_FRAMES or (Time.get_ticks_usec() - started) < 3000000:
		await RenderingServer.frame_post_draw
		main.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		if far_viewport != null: far.append(RenderingServer.viewport_get_measured_render_time_gpu(far_viewport.get_viewport_rid()))
	return {"sample_seconds":(Time.get_ticks_usec()-started)*.000001, "main_gpu_ms":stats(main), "far_gpu_ms":stats(far), "draw_calls":stats(draws)}

func compare_baselines(phase: String, baseline_median: float, repeated: Dictionary) -> void:
	var repeat_median := float(repeated.get("median", NAN))
	var minimum := float(repeated.get("min", NAN))
	var maximum := float(repeated.get("max", NAN))
	var overlap := baseline_median >= minimum and baseline_median <= maximum
	var delta := absf(repeat_median - baseline_median)
	var result := {"phase":phase, "baseline_median":baseline_median, "repeat_median":repeat_median, "repeat_min":minimum, "repeat_max":maximum, "median_delta":delta, "overlap":overlap}
	if delta <= 1.0:
		result["status"] = "CONSISTENT"
	elif overlap and delta <= 4.0:
		result["status"] = "INCONCLUSIVE"
		result["reason"] = "Repeated draw distributions overlap but medians differ slightly."
	else:
		result["status"] = "CHANGED"
	report["baseline_" + phase + "_comparison"] = result

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d: Dictionary = surface.call("GetSurfaceDiagnostics"); var stream: Dictionary = d.get("streaming", {}); var native: Dictionary = d.get("native", {}); var prep: Dictionary = native.get("preparation", {})
		var placed := absf(float(d.get("player_around", NAN)) - AROUND) < 1.0 and absf(float(d.get("player_across", NAN)) - ACROSS) < 1.0
		var ready := bool(d.get("ready", false)) and bool(d.get("visual_coverage", false)) and int(stream.get("pending_jobs", 0)) == 0 and int(stream.get("active_jobs", 0)) == 0 and not bool(stream.get("packing", false)) and bool(prep.get("ready", false)) and int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 and int(grass.get("ResidentGrassInstanceCount")) > 0 and int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0 and int(trees.get("GpuTreeResidentRootCount")) > 0 and int(global_terrain.get("PendingInitialSegmentCount")) == 0 and int(global_terrain.get("PendingSegmentRebuildCount")) == 0 and not bool(global_terrain.get("GlobalSegmentSamplingWorkerPending")) and not bool(global_terrain.get("GlobalSummaryBuildPending"))
		stable = stable + 1 if placed and ready else 0
		if stable >= 12:
			if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")): fail("dryland fixture retained underwater state"); return false
			return true
	fail("workload did not settle"); return false

func noon_fraction() -> float: return phase_fraction(true)
func night_fraction() -> float: return phase_fraction(false)
func phase_fraction(maximum: bool) -> float:
	var best := -INF if maximum else INF; var result := 0.0
	for step in 48:
		var value := float(step) / 48.0; clock.call("SetCycleFraction", value); var elevation := float(clock.get("SunElevationDegrees"))
		if (maximum and elevation > best) or (not maximum and elevation < best): best = elevation; result = value
	return result

func cloud_state() -> Dictionary:
	var bridge := game.get_node_or_null("SunshineCloudsDriver")
	var resource = bridge.get("clouds_resource") if bridge != null else null
	return {"present":bridge != null, "process_mode":bridge.process_mode if bridge != null else -1, "resource_present":resource != null, "resource_enabled":bool(resource.get("enabled")) if resource != null else false, "resolution_scale":resource.get("resolution_scale") if resource != null else -1, "effect_callback_type":resource.get("effect_callback_type") if resource != null else ""}
func environment_state() -> Dictionary:
	return {"enabled":environment.volumetric_fog_enabled, "density":environment.volumetric_fog_density, "length":environment.volumetric_fog_length, "detail_spread":environment.volumetric_fog_detail_spread, "temporal_reprojection":environment.volumetric_fog_temporal_reprojection_enabled, "temporal_reprojection_amount":environment.volumetric_fog_temporal_reprojection_amount, "froxel_width":ProjectSettings.get_setting("rendering/environment/volumetric_fog/volume_size"), "froxel_depth":ProjectSettings.get_setting("rendering/environment/volumetric_fog/volume_depth")}
func light_state() -> Dictionary:
	var star := game.get_node_or_null("StarLight") as DirectionalLight3D
	return {"star_present":star != null, "energy":star.light_energy if star != null else 0.0, "volumetric_fog_energy":star.light_volumetric_fog_energy if star != null else 0.0, "ambient_light_energy":environment.ambient_light_energy, "ambient_light_color":environment.ambient_light_color}
func find_far_viewport() -> SubViewport:
	for node in all_nodes(game): if node is SubViewport and "ScaledFarViewport" in str(node.get_path()): return node
	return null
func all_nodes(node: Node) -> Array:
	var result: Array = [node]
	for child in node.get_children(): result.append_array(all_nodes(child))
	return result
func stats(values: Array[float]) -> Dictionary:
	if values.is_empty(): return {"samples":0}
	var ordered := values.duplicate(); ordered.sort(); var p95: float = ordered[mini(ordered.size()-1, int(ceil(ordered.size()*.95))-1)]
	return {"samples":ordered.size(), "median":ordered[ordered.size()/2], "p95":p95, "min":ordered[0], "max":ordered[-1]}
func frames(count: int) -> void:
	for ignored in count: await process_frame
func fail(message: String) -> void:
	if failed:
		return
	failed = true
	report.status = "FAIL"
	report.failure = message
	if not fixture.is_empty():
		var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
		if file != null:
			file.store_string(JSON.stringify(report, "\t"))
			file.close()
	push_error("LOCAL_MIST_DIAGNOSTIC=FAIL " + message)
	quit(1)
