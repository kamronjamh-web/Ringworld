extends "res://tools/night_horizon_attribution_test.gd"
## Isolated A/B reproduction for a reported long-idle cloud slowdown.
## It deliberately inherits the mature fixture's isolated launch, readiness,
## timing, and screenshot helpers, but never selects its night/handoff modes.

const SOURCE_AROUND := 156205.1497623861
const SOURCE_ACROSS := 254166.4515210524
const SOURCE_HEIGHT := 5848.370188249275
const SOURCE_YAW_RADIANS := 3.656816
const SOURCE_PITCH_DEGREES := 6.5600147
const SOURCE_CLOUD_TIME := 881.3295438888823
const SOURCE_CELESTIAL_TIME := 3191.470552888884
const SOURCE_SEED := 1337
const SOURCE_RECIPE_VERSION := 3
const TOTAL_WATCHDOG_USEC := 240000000
const SKY_RETURN_WATCHDOG_USEC := 360000000
const SKY_RETURN_PITCH_DEGREES := 75.0
const DEFAULT_CASES := ["baseline", "sunshine_compositor_off", "cloud_half_resolution", "baseline_repeat"]
const PRODUCTION_COMPUTE_SOURCE := "res://addons/SunshineClouds2/SunshineCloudsCompute.glsl"
const SUPPORTED_CASES := DEFAULT_CASES + ["cloud_shadow_maps_off", "sun_shadows_off", "shadow_atlas_8192", "cloud_quarter_resolution", "cloud_lighting_2", "cloud_steps_128", "cloud_reference", "cloud_optimized", "cloud_reference_repeat", "cloud_optimized_repeat"]

var cloud_time := SOURCE_CLOUD_TIME
var celestial_time := SOURCE_CELESTIAL_TIME
var coverage_override := NAN
var soak_seconds := 0.0
var selected_cases: Array = DEFAULT_CASES.duplicate()
var started_usec := 0
var weather: Node
var driver: Node
var native_surface: Node
var saved_case_state := {}
var wind_soak_seconds := 0.0
var current_case := ""
var watchdog: Node
var run_sweep := false
var reference_shader_path := ""
var production_compute_shader: RDShaderFile
var reference_compute_shader: RDShaderFile
var optimized_compute_shader: RDShaderFile
var active_shader_label := "production_original"
var sky_return_enabled := false
var sky_return_seconds := 10.0
var watchdog_usec := TOTAL_WATCHDOG_USEC

class CloudSessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary:
		return settings.duplicate(true)
	func save_active_world_settings(value: Dictionary) -> void:
		settings = value.duplicate(true)
	func consume_load_selected_world_request() -> bool:
		return false

class FixtureWatchdog extends Node:
	var fixture_owner: SceneTree
	func _process(_delta: float) -> void:
		if fixture_owner != null:
			fixture_owner.call("watchdog_tick")

func run() -> void:
	started_usec = Time.get_ticks_usec()
	watchdog = FixtureWatchdog.new()
	watchdog.fixture_owner = self
	root.add_child(watchdog)
	if RenderingServer.get_rendering_device() == null:
		fail("requires Vulkan")
		return
	parse_cloud_args()
	if failed:
		return
	fixture = "res://.godot/cloud-slowdown-attribution/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var old_session := root.get_node_or_null("WorldSession")
	if old_session != null:
		old_session.name = "WorldSessionOriginalCloudSlowdown"
	var session := CloudSessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	report = {"status":"RUNNING", "cases":[], "sweep":[], "created_utc":Time.get_datetime_string_from_system(true, true), "engine":Engine.get_version_info(), "fixture":fixture, "source":source_state(), "requested_cases":selected_cases, "watchdog_seconds":watchdog_usec * 0.000001, "sky_return_enabled":sky_return_enabled, "sky_return_measure_seconds":sky_return_seconds, "sky_return_pitch_degrees":SKY_RETURN_PITCH_DEGREES}
	print("CLOUD_SLOWDOWN_ATTRIBUTION=START fixture=" + fixture + " cases=" + ",".join(selected_cases))
	game = await create_game()
	if game == null or failed or not resolve_nodes() or not remaining_time("startup"):
		return
	weather = game.get_node_or_null("RingVolumetricClouds")
	driver = game.get_node_or_null("SunshineCloudsDriver")
	native_surface = surface.get_node_or_null("Terrain3DSurface")
	if weather == null or driver == null or native_surface == null:
		fail("RingVolumetricClouds, SunshineCloudsDriver, or native Terrain3DSurface missing")
		return
	if not prepare_compute_shader_variants():
		return
	if monitor != null:
		monitor.set("Enabled", true)
		monitor.set("DetailedGpuEnabled", true)
	verify_recipe()
	if failed:
		return
	if not place(yaw, pitch):
		fail("initial placement failed")
		return
	# The initial sea-level spawn may leave stale water metadata after the helper
	# moves the camera to the known above-surface mountain pose.
	explorer.set_meta("ring_camera_underwater", false)
	explorer.set_meta("ring_water_depth_meters", 0.0)
	report["water_metadata_override"] = {"ring_camera_underwater":false, "ring_water_depth_meters":0.0}
	await force_sunshine_provider()
	if failed:
		return
	# Keep the reproduced pose fixed while streaming reaches finite readiness.
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	if not await wait_ready() or not remaining_time("readiness"):
		if not failed:
			fail("initial placement/readiness failed")
		return
	await set_reported_clocks_and_freeze()
	# Let the existing atmosphere/light owners settle at the saved solar time
	# before freezing the driver's packed directional-light data.
	var light_deadline := Time.get_ticks_usec() + 3000000
	while Time.get_ticks_usec() < light_deadline:
		await process_frame
	var atmosphere := game.get_node_or_null("DaySideAtmosphere")
	if atmosphere == null or float(atmosphere.get("current_underwater_factor")) > 0.0001:
		fail("water metadata override did not clear DaySideAtmosphere underwater factor")
		return
	if wind_soak_seconds > 0.0:
		await wind_soak()
		if failed:
			return
	configure_baseline()
	# Refresh packed directional-light data at zero delta before preserving the
	# fixed cloud origins. This cannot advance driver-integrated noise.
	driver.call("_process", 0.0)
	await freeze_sunshine_driver()
	if run_sweep:
		var saved_view := {"yaw":yaw, "pitch":pitch}
		yaw = await sweep()
		pitch = 0.0
		report["sweep_departure_from_saved_view"] = {"saved":saved_view, "selected":{"yaw":yaw, "pitch":pitch}}
		write_report()
	if soak_seconds > 0.0:
		await soak()
		if failed:
			return
	saved_case_state = cloud_state()
	for label in selected_cases:
		if not remaining_time("before " + label):
			return
		restore_cloud_state(saved_case_state)
		apply_cloud_case(label)
		await capture_cloud_case(label)
		if failed:
			return
	restore_cloud_state(saved_case_state)
	bridge.call("ApplyTemporalQualityTuning", true)
	finish_cloud()

func parse_cloud_args() -> void:
	around = SOURCE_AROUND
	across = SOURCE_ACROSS
	height = SOURCE_HEIGHT
	yaw = rad_to_deg(SOURCE_YAW_RADIANS)
	pitch = SOURCE_PITCH_DEGREES
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--cases="):
			selected_cases = []
			for item in arg.trim_prefix("--cases=").split(",", false):
				var label := item.strip_edges()
				if label not in SUPPORTED_CASES:
					fail("unsupported case name: " + label)
				elif label not in selected_cases:
					selected_cases.append(label)
			if selected_cases.is_empty():
				fail("--cases did not contain a supported case")
		elif arg.begins_with("--soak-seconds="):
			soak_seconds = clampf(float(arg.trim_prefix("--soak-seconds=")), 0.0, 90.0)
		elif arg.begins_with("--cloud-time="):
			cloud_time = maxf(0.0, float(arg.trim_prefix("--cloud-time=")))
		elif arg.begins_with("--celestial-time="):
			celestial_time = maxf(0.0, float(arg.trim_prefix("--celestial-time=")))
		elif arg.begins_with("--reference-shader="):
			reference_shader_path = arg.trim_prefix("--reference-shader=")
		elif arg.begins_with("--around="):
			around = float(arg.trim_prefix("--around="))
		elif arg.begins_with("--across="):
			across = float(arg.trim_prefix("--across="))
		elif arg.begins_with("--height="):
			height = float(arg.trim_prefix("--height="))
		elif arg.begins_with("--yaw="):
			yaw = float(arg.trim_prefix("--yaw="))
		elif arg.begins_with("--pitch="):
			pitch = float(arg.trim_prefix("--pitch="))
		elif arg.begins_with("--coverage=") or arg.begins_with("--coverage-bias="):
			coverage_override = clampf(float(arg.trim_prefix("--coverage=").trim_prefix("--coverage-bias=")), -1.0, 1.0)
		elif arg.begins_with("--wind-soak-seconds="):
			wind_soak_seconds = clampf(float(arg.trim_prefix("--wind-soak-seconds=")), 0.0, 90.0)
		elif arg == "--sweep":
			run_sweep = true
		elif arg == "--sky-return":
			sky_return_enabled = true
			watchdog_usec = SKY_RETURN_WATCHDOG_USEC
		elif arg.begins_with("--sky-return-seconds="):
			sky_return_enabled = true
			watchdog_usec = SKY_RETURN_WATCHDOG_USEC
			sky_return_seconds = clampf(float(arg.trim_prefix("--sky-return-seconds=")), 10.0, 15.0)

func install_defaults(next: Node) -> void:
	# The inherited fixture pre-dates the gameplay preference filenames. Copy
	# only into the fixture directory before the drawer receives each override.
	var tuning := next.get_node_or_null("LiveTuningDrawer")
	if tuning == null:
		return
	for item in [["InstallGrassDefaults", "gameplay_grass_defaults.tres"], ["InstallVegetationDefaults", "gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults", "gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults", "gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults", "gameplay_shadow_defaults.tres"], ["InstallSurfaceTerrainDefaults", "gameplay_surface_terrain_defaults.tres"], ["InstallAtmosphereDefaults", "atmosphere_visual_defaults.tres"]]:
		var destination := fixture.path_join(str(item[1]))
		var source := "user://" + str(item[1])
		if FileAccess.file_exists(source):
			var copy := FileAccess.open(destination, FileAccess.WRITE)
			if copy == null:
				fail("could not isolate preference " + str(item[1]))
				return
			copy.store_string(FileAccess.get_file_as_string(source))
			copy.close()
		tuning.call(str(item[0]), destination)

func force_sunshine_provider() -> void:
	# Factory/user cloud defaults can leave Sunshine unselected. Establish the
	# normal provider through its ownership API so the legacy local-cloud
	# fallback receives the matching external-owner handoff; do not write prefs.
	bridge.set("CloudRenderingEnabled", true)
	bridge.call("SetProviderActive", true)
	bridge.call("ApplyCloudAppearanceTuning")
	await process_frame
	var clouds = cloud_resource()
	var visible := bool(bridge.get("RuntimeVisible"))
	var enabled := clouds != null and bool(clouds.get("enabled"))
	report["baseline_provider_force"] = {"CloudRenderingEnabled":bridge.get("CloudRenderingEnabled"), "RuntimeVisible":visible, "resource_enabled":enabled, "ownership":ownership()}
	write_report()
	if not bool(bridge.get("CloudRenderingEnabled")) or not visible or not enabled:
		fail("could not establish Sunshine provider ownership for baseline")

func set_reported_clocks_and_freeze() -> void:
	# These are the exact save fields used by RingWorldSaveManager on load.
	weather.set("CloudSimulationTimeSeconds", cloud_time)
	clock.call("set_simulation_seconds", celestial_time)
	clock.set("Paused", true)
	if not is_nan(coverage_override):
		bridge.set("CloudCoverageBias", coverage_override)
	# Freeze state owners after assigning their saved clocks. Do not modify the
	# bridge wind multiplier: it changes the reported cloud field.
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	weather.process_mode = Node.PROCESS_MODE_DISABLED
	var local_weather := game.get_node_or_null("LocalWeatherEffects")
	if local_weather != null:
		local_weather.process_mode = Node.PROCESS_MODE_DISABLED
	await process_frame
	report["effective_source"] = source_state()
	report["effective_source"]["actual_cloud_time"] = weather.get("CloudSimulationTimeSeconds")
	report["effective_source"]["actual_celestial_time"] = clock.call("get_simulation_seconds")
	report["effective_source"]["weather_process_mode"] = weather.process_mode
	report["effective_source"]["explorer_process_mode"] = explorer.process_mode
	report["reproduction_limitations"] = ["The saved weather clock does not encode SunshineCloudsDriver's historical delta-integrated noise origins. The fixture freezes the initialized driver state after readiness; --wind-soak-seconds can intentionally advance that state before the fixed A/B.", "Cloud defaults are factory-backed in editor runs; RingSunshineCloudBridge exposes no isolated cloud-default path override. This fixture does not save cloud defaults."]
	write_report()

func wind_soak() -> void:
	# Weather remains frozen at the saved clock; only the Sunshine driver is
	# allowed to integrate its normal seeded wind for an explicit bounded soak.
	var deadline := Time.get_ticks_usec() + int(wind_soak_seconds * 1000000.0)
	print("CLOUD_SLOWDOWN_ATTRIBUTION=WIND_SOAK seconds=" + str(wind_soak_seconds))
	while Time.get_ticks_usec() < deadline:
		await process_frame
		if not remaining_time("wind soak"):
			return

func freeze_sunshine_driver() -> void:
	driver.process_mode = Node.PROCESS_MODE_DISABLED
	await process_frame
	report["sunshine_driver_freeze"] = driver_state()
	write_report()

func configure_baseline() -> void:
	# Match the current repository's cloud overrides. The historical slowdown
	# report does not include these values, so this is not an exact replay.
	bridge.set("CloudResolutionScale", 0)
	bridge.set("RaymarchSteps", 256)
	bridge.set("LightingSteps", 8)
	bridge.call("ApplyTemporalQualityTuning", false)
	bridge.call("ApplyCloudAppearanceTuning")
	var clouds = cloud_resource()
	report["requested_baseline"] = baseline_runtime_state()
	report["requested_baseline"]["resource_max_step_count"] = clouds.get("max_step_count") if clouds != null else null
	report["requested_baseline"]["resource_max_lighting_steps"] = clouds.get("max_lighting_steps") if clouds != null else null
	write_report()

func baseline_runtime_state() -> Dictionary:
	var viewport := root.get_viewport()
	return {"CloudResolutionScale":bridge.get("CloudResolutionScale"), "RaymarchSteps":bridge.get("RaymarchSteps"), "LightingSteps":bridge.get("LightingSteps"), "CloudCoverageBias":bridge.get("CloudCoverageBias"), "CastCloudShadows":bridge.get("CastCloudShadows"), "GpuShadowGenerationEnabled":bridge.get("GpuShadowGenerationEnabled"), "PreferGpuCloudShadows":bridge.get("PreferGpuCloudShadows"), "GpuNearShadowMapResolution":bridge.get("GpuNearShadowMapResolution"), "GpuFarShadowMapResolution":bridge.get("GpuFarShadowMapResolution"), "GpuNearShadowRadiusMeters":bridge.get("GpuNearShadowRadiusMeters"), "GpuFarShadowRadiusMeters":bridge.get("GpuFarShadowRadiusMeters"), "camera_fov":camera.fov, "camera_near":camera.near, "camera_far":camera.far, "viewport_size":viewport.get_visible_rect().size, "render_size":viewport.size, "cloud_bands":driver_state(), "sun_shadows":star.shadow_enabled}

func driver_state() -> Dictionary:
	var clouds = cloud_resource()
	return {"process_mode":driver.process_mode, "update_continuously":driver.get("update_continuously"), "driver_origin_offset":driver.get("origin_offset"), "resource_origin_offset":clouds.get("origin_offset") if clouds != null else null, "extra_large_position":clouds.get("extra_large_scale_clouds_position") if clouds != null else null, "large_position":clouds.get("large_scale_clouds_position") if clouds != null else null, "medium_position":clouds.get("medium_scale_clouds_position") if clouds != null else null, "detail_position":clouds.get("detail_clouds_position") if clouds != null else null, "resource_current_time":clouds.get("current_time") if clouds != null else null}

func source_state() -> Dictionary:
	return {"saved_pose":{"around":around, "across":across, "feet":height, "yawRadians":deg_to_rad(yaw), "pitchDegrees":pitch}, "saved_clock":{"cloudtime":cloud_time, "celestialtime":celestial_time}, "expected_recipe":{"seed":SOURCE_SEED, "version":SOURCE_RECIPE_VERSION}, "coverage_override":null if is_nan(coverage_override) else coverage_override}

func verify_recipe() -> void:
	var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	var recipe_json := str(diagnostics.get("surface_generation_recipe_json", ""))
	var parsed = JSON.parse_string(recipe_json)
	var actual: Dictionary = parsed if parsed is Dictionary else {}
	var deviations: Array = []
	if int(actual.get("seed", -1)) != SOURCE_SEED:
		deviations.append("seed expected %s got %s" % [SOURCE_SEED, actual.get("seed", "missing")])
	var generator_id := str(actual.get("generator_id", ""))
	if not generator_id.ends_with("_v3"):
		deviations.append("recipe version expected v3 got " + generator_id)
	report["recipe_verification"] = {"actual":actual, "deviations":deviations, "matches_source":deviations.is_empty()}
	write_report()
	if not deviations.is_empty():
		fail("isolated runtime recipe differs from saved report: " + "; ".join(deviations))

func prepare_compute_shader_variants() -> bool:
	var clouds = cloud_resource()
	if clouds == null:
		fail("Sunshine cloud resource missing for shader A/B")
		return false
	production_compute_shader = clouds.get("compute_shader") as RDShaderFile
	if production_compute_shader == null:
		fail("production Sunshine compute shader missing")
		return false
	var wants_reference := "cloud_reference" in selected_cases or "cloud_reference_repeat" in selected_cases
	var wants_optimized := "cloud_optimized" in selected_cases or "cloud_optimized_repeat" in selected_cases
	if wants_reference:
		if reference_shader_path.is_empty():
			fail("cloud_reference requires --reference-shader=res://...glsl")
			return false
		reference_compute_shader = compile_fresh_compute_shader(reference_shader_path, PRODUCTION_COMPUTE_SOURCE.get_base_dir())
		if reference_compute_shader == null:
			return false
	if wants_optimized:
		optimized_compute_shader = compile_fresh_compute_shader(PRODUCTION_COMPUTE_SOURCE, PRODUCTION_COMPUTE_SOURCE.get_base_dir())
		if optimized_compute_shader == null:
			return false
	report["shader_variants"] = {"original_resource_path":production_compute_shader.resource_path, "reference_source":reference_shader_path, "optimized_source":PRODUCTION_COMPUTE_SOURCE, "reference_include_root":PRODUCTION_COMPUTE_SOURCE.get_base_dir(), "include_resolution":"reference includes are expanded in memory against the production Sunshine include root; production include files remain unchanged"}
	write_report()
	return true

func compile_fresh_compute_shader(source_path: String, include_root: String) -> RDShaderFile:
	var expanded := expand_compute_includes(source_path, include_root, [])
	if expanded.is_empty():
		return null
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		fail("local RenderingDevice unavailable for fresh shader compilation")
		return null
	var source := RDShaderSource.new()
	source.source_compute = expanded.replace("#[compute]", "")
	var spirv := rd.shader_compile_spirv_from_source(source)
	if not spirv.compile_error_compute.is_empty():
		rd.free()
		fail("compute shader compile failed for " + source_path + ": " + spirv.compile_error_compute)
		return null
	var shader := rd.shader_create_from_spirv(spirv)
	if not shader.is_valid():
		rd.free()
		fail("fresh compute shader produced invalid RID: " + source_path)
		return null
	var pipeline := rd.compute_pipeline_create(shader)
	if not pipeline.is_valid():
		rd.free_rid(shader)
		rd.free()
		fail("fresh compute shader produced invalid pipeline: " + source_path)
		return null
	rd.free_rid(pipeline)
	rd.free_rid(shader)
	var result := RDShaderFile.new()
	result.set_bytecode(spirv)
	rd.free()
	return result

func expand_compute_includes(path: String, include_root: String, stack: Array[String]) -> String:
	if path in stack:
		fail("recursive shader include: " + path)
		return ""
	var source := FileAccess.get_file_as_string(path)
	if source.is_empty():
		fail("could not read shader source: " + path)
		return ""
	var next_stack: Array[String] = stack.duplicate()
	next_stack.append(path)
	var result := ""
	for line in source.split("\n"):
		var stripped := line.strip_edges()
		if stripped.begins_with("#include"):
			var first_quote := stripped.find("\"")
			var last_quote := stripped.rfind("\"")
			if first_quote < 0 or last_quote <= first_quote:
				fail("malformed shader include in " + path)
				return ""
			var relative := stripped.substr(first_quote + 1, last_quote - first_quote - 1)
			var child := include_root.path_join(relative).simplify_path()
			var included := expand_compute_includes(child, include_root, next_stack)
			if included.is_empty():
				return ""
			result += included + "\n"
		else:
			result += line + "\n"
	return result

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	var next_progress := Time.get_ticks_msec()
	var global_lod := game.get_node_or_null("GlobalTerrainLod3")
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = diagnostics.get("streaming", {})
		var native: Dictionary = diagnostics.get("native", {})
		var preparation: Dictionary = native.get("preparation", {})
		var placed := absf(float(diagnostics.get("player_around", NAN)) - around) < 1.0 and absf(float(diagnostics.get("player_across", NAN)) - across) < 1.0
		var grass_ready := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0
		var trees_ready := int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0
		var global_ready := global_lod != null and not bool(global_lod.get("GlobalSummaryBuildPending")) and int(global_lod.get("PendingInitialSegmentCount")) == 0 and int(global_lod.get("PendingSegmentRebuildCount")) == 0 and not bool(global_lod.get("GlobalSegmentSamplingWorkerPending"))
		var eye_height := float(helper.call("ResolveCameraCanonicalHeight", game.get_node_or_null("ChunkGridManager"), camera))
		var terrain_height := float(surface.call("SampleHeight", around, across))
		var above_surface := eye_height > terrain_height + 0.5
		var ready := placed and above_surface and bool(diagnostics.get("ready", false)) and bool(diagnostics.get("visual_coverage", false)) and int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false)) and bool(preparation.get("ready", false)) and grass_ready and trees_ready and global_ready
		stable = stable + 1 if ready else 0
		if Time.get_ticks_msec() >= next_progress:
			var pending := []
			for pair in [["placed", placed], ["above_surface", above_surface], ["surface_ready", bool(diagnostics.get("ready", false))], ["visual_coverage", bool(diagnostics.get("visual_coverage", false))], ["streaming_idle", int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false))], ["native_prepared", bool(preparation.get("ready", false))], ["grass_ready", grass_ready], ["trees_ready", trees_ready], ["global_lod_ready", global_ready]]:
				if not bool(pair[1]): pending.append(pair[0])
			var readiness := {"pending":pending, "stable_frames":stable, "eye_height":eye_height, "terrain_height":terrain_height, "requested_height":height, "player_around":diagnostics.get("player_around"), "player_across":diagnostics.get("player_across"), "streaming":streaming, "preparation":preparation}
			report["readiness_progress"] = readiness
			write_report()
			print("CLOUD_SLOWDOWN_ATTRIBUTION=READINESS pending=" + ",".join(pending) + " eye=" + str(snappedf(eye_height, 0.1)) + " terrain=" + str(snappedf(terrain_height, 0.1)))
			next_progress += 10000
		if stable >= 12:
			return true
	fail("terrain/vegetation/global LOD did not settle; last=" + JSON.stringify(report.get("readiness_progress", {})))
	return false

func cloud_state() -> Dictionary:
	var clouds = cloud_resource()
	return {"shader_variant":active_shader_label, "compute_shader_path":str(clouds.get("compute_shader").resource_path) if clouds != null else "", "bridge_process_mode":bridge.process_mode, "driver_process_mode":driver.process_mode, "cloud_resource_enabled":clouds.get("enabled") if clouds != null else null, "cloud_resolution":bridge.get("CloudResolutionScale"), "raymarch_steps":bridge.get("RaymarchSteps"), "lighting_steps":bridge.get("LightingSteps"), "min_step_distance_m":bridge.get("MinStepDistanceMeters"), "max_step_distance_m":bridge.get("MaxStepDistanceMeters"), "cloud_base_m":bridge.get("NaturalCloudBaseAltitudeMeters"), "cloud_top_m":bridge.get("NaturalCloudTopAltitudeMeters"), "cloud_density_multiplier":bridge.get("CloudDensityMultiplier"), "cloud_sharpness":bridge.get("CloudSharpness"), "resource_density":clouds.get("clouds_density") if clouds != null else null, "resource_coverage":clouds.get("clouds_coverage") if clouds != null else null, "cloud_coverage_bias":bridge.get("CloudCoverageBias"), "cloud_rendering_requested":bridge.get("CloudRenderingEnabled"), "cast_cloud_shadows":bridge.get("CastCloudShadows"), "gpu_shadow_generation":bridge.get("GpuShadowGenerationEnabled"), "prefer_gpu_cloud_shadows":bridge.get("PreferGpuCloudShadows"), "sun_shadows":star.shadow_enabled, "sun":light_state(star), "planet":light_state(planet), "handoff":handoff_diagnostics(), "effective_shadow_atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "effective_shadow_filter":graphics.get("EffectiveDirectionalShadowFilter"), "requested_sun_shadows":handoff_diagnostics().get("requested_sun_shadows", star.shadow_enabled), "clock_paused":clock.get("Paused"), "cloud_clock":weather.get("CloudSimulationTimeSeconds"), "celestial_clock":clock.call("get_simulation_seconds"), "weather_process_mode":weather.process_mode, "explorer_process_mode":explorer.process_mode, "driver":driver_state(), "ownership":ownership()}

func restore_cloud_state(saved: Dictionary) -> void:
	var clouds = cloud_resource()
	bridge.process_mode = saved.bridge_process_mode
	driver.process_mode = saved.driver_process_mode
	if clouds != null:
		clouds.set("enabled", saved.cloud_resource_enabled)
		clouds.set("compute_shader", production_compute_shader)
	active_shader_label = "production_original"
	bridge.set("CloudResolutionScale", saved.cloud_resolution)
	bridge.set("RaymarchSteps", saved.raymarch_steps)
	bridge.set("LightingSteps", saved.lighting_steps)
	bridge.set("CloudCoverageBias", saved.cloud_coverage_bias)
	bridge.call("ApplyTemporalQualityTuning", false)
	bridge.set("CastCloudShadows", saved.cast_cloud_shadows)
	bridge.set("GpuShadowGenerationEnabled", saved.gpu_shadow_generation)
	bridge.set("PreferGpuCloudShadows", saved.prefer_gpu_cloud_shadows)
	graphics.call("SetShadowTuningOverride", "AtlasSize", saved.effective_shadow_atlas)
	graphics.call("SetShadowTuningOverride", "Enabled", saved.requested_sun_shadows)
	clock.set("Paused", saved.clock_paused)
	weather.set("CloudSimulationTimeSeconds", saved.cloud_clock)
	clock.call("set_simulation_seconds", saved.celestial_clock)
	weather.process_mode = saved.weather_process_mode
	explorer.process_mode = saved.explorer_process_mode
	bridge.call("ApplyCloudAppearanceTuning")

func apply_cloud_case(label: String) -> void:
	if label == "sunshine_compositor_off":
		# Keep requested ownership true; only suppress the Sunshine resource and
		# freeze the bridge so it cannot re-enable the compositor during the case.
		bridge.process_mode = Node.PROCESS_MODE_DISABLED
		var clouds = cloud_resource()
		if clouds != null:
			clouds.set("enabled", false)
	elif label == "cloud_shadow_maps_off":
		bridge.set("CastCloudShadows", false)
		bridge.set("GpuShadowGenerationEnabled", false)
		bridge.set("PreferGpuCloudShadows", false)
	elif label == "sun_shadows_off":
		graphics.call("SetShadowTuningOverride", "Enabled", false)
	elif label == "shadow_atlas_8192":
		graphics.call("SetShadowTuningOverride", "AtlasSize", 8192)
	elif label == "cloud_half_resolution":
		bridge.set("CloudResolutionScale", 1) # enum: Native=0, Half=1, Quarter=2.
		bridge.call("ApplyTemporalQualityTuning", false)
	elif label == "cloud_quarter_resolution":
		bridge.set("CloudResolutionScale", 2)
		bridge.call("ApplyTemporalQualityTuning", false)
	elif label == "cloud_lighting_2":
		bridge.set("LightingSteps", 2)
		bridge.call("ApplyTemporalQualityTuning", false)
		# Refresh packed light steps without running the driver's delta integration.
		driver.call("retrieve_texture_data")
	elif label == "cloud_steps_128":
		bridge.set("RaymarchSteps", 128)
		bridge.call("ApplyTemporalQualityTuning", false)
	elif label == "cloud_reference" or label == "cloud_reference_repeat":
		set_compute_shader_variant(reference_compute_shader, "reference")
	elif label == "cloud_optimized" or label == "cloud_optimized_repeat":
		set_compute_shader_variant(optimized_compute_shader, "optimized")
	# Every case obtains exactly one whole-compositor refresh after its settings
	# and selected compute shader are in place. The bridge releases external
	# shadow receivers before refresh_compute retires the previous generation.
	bridge.call("ApplyTemporalQualityTuning", true)

func set_compute_shader_variant(shader_file: RDShaderFile, label: String) -> void:
	var clouds = cloud_resource()
	if clouds == null or shader_file == null:
		fail("requested compute shader variant unavailable: " + label)
		return
	clouds.set("compute_shader", shader_file)
	active_shader_label = label

func verify_cloud_case(label: String) -> bool:
	var clouds = cloud_resource()
	if label != "sunshine_compositor_off" and (clouds == null or not bool(bridge.get("CloudRenderingEnabled")) or not bool(bridge.get("RuntimeVisible")) or not bool(clouds.get("enabled"))):
		return false
	if label == "baseline" or label == "baseline_repeat":
		return clouds != null and int(bridge.get("CloudResolutionScale")) == 0 and int(clouds.get("resolution_scale")) == 0 and int(clouds.get("max_step_count")) == 256 and int(clouds.get("max_lighting_steps")) == 8
	if label == "sunshine_compositor_off":
		return clouds != null and not bool(clouds.get("enabled")) and bool(bridge.get("CloudRenderingEnabled")) and bridge.process_mode == Node.PROCESS_MODE_DISABLED
	if label == "cloud_shadow_maps_off":
		return not bool(bridge.get("CastCloudShadows")) and not bool(bridge.get("GpuShadowGenerationEnabled")) and not bool(bridge.get("PreferGpuCloudShadows"))
	if label == "sun_shadows_off":
		return not star.shadow_enabled
	if label == "shadow_atlas_8192":
		return int(graphics.get("EffectiveDirectionalShadowAtlasSize")) == 8192
	if label == "cloud_half_resolution":
		return clouds != null and int(bridge.get("CloudResolutionScale")) == 1 and int(clouds.get("resolution_scale")) == 1
	if label == "cloud_quarter_resolution":
		return clouds != null and int(bridge.get("CloudResolutionScale")) == 2 and int(clouds.get("resolution_scale")) == 2
	if label == "cloud_lighting_2":
		return int(bridge.get("LightingSteps")) == 2 and driver_state().get("process_mode") == Node.PROCESS_MODE_DISABLED
	if label == "cloud_steps_128":
		return int(bridge.get("RaymarchSteps")) == 128
	if label == "cloud_reference" or label == "cloud_reference_repeat":
		return clouds != null and clouds.get("compute_shader") == reference_compute_shader and active_shader_label == "reference"
	if label == "cloud_optimized" or label == "cloud_optimized_repeat":
		return clouds != null and clouds.get("compute_shader") == optimized_compute_shader and active_shader_label == "optimized"
	return true

func capture_cloud_case(label: String) -> void:
	print("CLOUD_SLOWDOWN_ATTRIBUTION=CASE " + label)
	current_case = label
	var sky_return_timing := {}
	if sky_return_enabled:
		sky_return_timing = await capture_sky_return_transient(label)
		if failed:
			return
	if not place(yaw, pitch):
		fail("placement failed for " + label)
		return
	if monitor != null:
		monitor.set("Enabled", true)
		monitor.set("DetailedGpuEnabled", true)
	var warmup := await warmup_case()
	if not remaining_time(label + " warmup"):
		return
	if not verify_cloud_case(label):
		fail("case control ineffective: " + label)
		return
	await RenderingServer.frame_post_draw
	var image_path := fixture.path_join(label + ".png")
	var image := root.get_viewport().get_texture().get_image()
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(image_path)) != OK:
		fail("screenshot failed for " + label)
		return
	# Drain the previous case and screenshot readback before starting this
	# case's detailed-GPU history. Keeping detailed GPU enabled avoids a stale
	# disabled-compositor state from suppressing later samples.
	for ignored in 8:
		await RenderingServer.frame_post_draw
	if monitor != null:
		monitor.call("ClearHistory")
	var timing := await measure(MEASURE_FRAMES, MEASURE_MIN_USEC)
	var gpu_report = monitor.call("CaptureSlowdown") if monitor != null else "unavailable"
	report.cases.append({"label":label, "image":image_path, "warmup":warmup, "timing":timing, "gpu_report":gpu_report, "sky_return_timing":sky_return_timing, "pose":pose(), "state":cloud_state(), "ownership":ownership(), "source_capture_paths":{"fixture":fixture, "screenshot":image_path, "performance":fixture.path_join("performance")}})
	write_report()

func capture_sky_return_transient(label: String) -> Dictionary:
	# This follows the reported interaction while the driver/weather clocks stay
	# frozen: look skyward, hold, then measure the first horizon-return frames.
	# Rotate the existing camera without teleporting/reinitializing the player.
	explorer.call("SetPresentationView", yaw, SKY_RETURN_PITCH_DEGREES)
	var cloud_up: Vector4 = cloud_resource().get("cloud_frame_up")
	var local_up := Vector3(cloud_up.x, cloud_up.y, cloud_up.z).normalized()
	var sky_up_dot := (-camera.global_basis.z).dot(local_up)
	if sky_up_dot < 0.9:
		fail("sky-return camera is not looking upward: " + str(sky_up_dot))
		return {}
	var sky_pose := pose()
	var started := Time.get_ticks_usec()
	var frames := 0
	while frames < 60 or Time.get_ticks_usec() - started < 3000000:
		await process_frame
		frames += 1
		if not remaining_time(label + " sky hold"):
			return {}
	var held_seconds := (Time.get_ticks_usec() - started) * 0.000001
	if monitor != null:
		monitor.call("ClearHistory")
	explorer.call("SetPresentationView", yaw, 0.0)
	var horizon_pose := pose()
	var timing := await measure_sky_return(sky_return_seconds)
	var gpu_report: String = str(monitor.call("CaptureSlowdown")) if monitor != null else "unavailable"
	return {"label":label + "_sky_return", "sky_pose":sky_pose, "sky_up_dot":sky_up_dot, "horizon_pose":horizon_pose, "sky_hold_frames":frames, "sky_hold_seconds":held_seconds, "timing":timing, "gpu_report":gpu_report, "source_capture_path":fixture.path_join("performance"), "screenshot":"not captured during transient interval"}

func measure_sky_return(minimum_seconds: float) -> Dictionary:
	var viewport := root.get_viewport()
	RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), true)
	var gpu: Array = []
	var cpu: Array = []
	var started := Time.get_ticks_usec()
	var minimum_usec := int(minimum_seconds * 1000000.0)
	const CAP_USEC := 15000000
	while (gpu.size() < 30 or Time.get_ticks_usec() - started < minimum_usec) and Time.get_ticks_usec() - started < CAP_USEC:
		await RenderingServer.frame_post_draw
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport.get_viewport_rid()))
		cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(viewport.get_viewport_rid()))
		if not remaining_time("sky-return transient measure"):
			return {}
	return {"seconds":(Time.get_ticks_usec() - started) * 0.000001, "samples":gpu.size(), "capped":Time.get_ticks_usec() - started >= CAP_USEC, "main_gpu_ms":stats(gpu), "main_cpu_ms":stats(cpu), "transient_window":"horizon return; no screenshot or warmup included"}

func soak() -> void:
	var deadline := Time.get_ticks_usec() + int(soak_seconds * 1000000.0)
	print("CLOUD_SLOWDOWN_ATTRIBUTION=SOAK seconds=" + str(soak_seconds))
	while Time.get_ticks_usec() < deadline:
		await process_frame
		if not remaining_time("soak"):
			return

func remaining_time(stage: String) -> bool:
	if Time.get_ticks_usec() - started_usec < watchdog_usec:
		return true
	fail(str(watchdog_usec * 0.000001) + "-second watchdog expired during " + stage)
	return false

func watchdog_tick() -> void:
	if not failed and started_usec > 0 and Time.get_ticks_usec() - started_usec >= watchdog_usec:
		fail(str(watchdog_usec * 0.000001) + "-second watchdog expired during asynchronous startup or readiness")

func finish_cloud() -> void:
	if failed:
		return
	report.status = "PASS"
	report.elapsed_seconds = (Time.get_ticks_usec() - started_usec) * 0.000001
	if not write_report():
		return
	if game != null:
		game.queue_free()
	for ignored in 2:
		await process_frame
	print("CLOUD_SLOWDOWN_ATTRIBUTION=PASS fixture=" + fixture)
	quit(0)
