extends SceneTree
# Runtime-only stationary horizon attribution.  It preserves user preferences.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const SAVED_AROUND := 29183808.633417398
const SAVED_ACROSS := 101436.38341601667
const SAVED_HEIGHT := 9690.789936411195
const SAVED_YAW_RADIANS := 0.033552866
const SAVED_YAW_DEGREES := rad_to_deg(SAVED_YAW_RADIANS)
const SAVED_PITCH := 19.84004
const WARMUP_FRAMES := 24
const WARMUP_CAP_USEC := 3000000
const MEASURE_FRAMES := 30
const MEASURE_MIN_USEC := 2000000
const MEASURE_CAP_USEC := 6000000
var fixture := ""
var game: Node
var surface: Node
var explorer: Node
var camera: Camera3D
var clock: Node
var bridge: Node
var graphics: Node
var star: DirectionalLight3D
var planet: DirectionalLight3D
var grass: Node
var trees: Node
var far: Node
var drawer: Node
var monitor: Node
var helper: RefCounted
var around := SAVED_AROUND
var across := SAVED_ACROSS
var height := SAVED_HEIGHT
var yaw := SAVED_YAW_DEGREES
var pitch := SAVED_PITCH
var shadow_atlas_override := 0
var failed := false
var report := {"status":"RUNNING", "sweep":[], "cases":[], "created_utc":Time.get_datetime_string_from_system(true, true), "engine":Engine.get_version_info()}

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary:
		return settings.duplicate(true)
	func save_active_world_settings(value: Dictionary) -> void:
		settings = value.duplicate(true)
	func consume_load_selected_world_request() -> bool:
		return false

func _init() -> void:
	call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("requires Vulkan")
		return
	parse_args()
	fixture = "res://.godot/night-horizon-attribution/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var old_session := root.get_node_or_null("WorldSession")
	if old_session != null:
		old_session.name = "WorldSessionOriginalHorizon"
	var session := SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	game = await create_game()
	if game == null or failed or not resolve_nodes():
		return
	if "--ground" in OS.get_cmdline_user_args() or "--handoff-comparison" in OS.get_cmdline_user_args():
		height = float(surface.call("SampleHeight", around, across)) + 1.1
	if not place(yaw, pitch) or not await wait_ready():
		if not failed:
			fail("initial placement/readiness failed")
		return
	freeze_transients()
	clock.set("Paused", true)
	if "--handoff-comparison" in OS.get_cmdline_user_args():
		await run_handoff_comparison()
		return
	clock.call("SetCycleFraction", night_phase())
	await warmup_case()
	var focused := "--focused" in OS.get_cmdline_user_args()
	var sweep_ran := not focused and "--no-sweep" not in OS.get_cmdline_user_args()
	var slow_yaw := yaw
	if sweep_ran:
		slow_yaw = await sweep()
	if failed:
		return
	yaw = slow_yaw
	if sweep_ran:
		pitch = 0.0 # Normal cases match the horizon sweep's pitch.
	if "--sweep-only" in OS.get_cmdline_user_args():
		finish()
		return
	var original := state()
	var labels := ["baseline", "atmosphere_disabled", "sunshine_clouds_off", "directional_shadows_off", "shadow_filter_hard", "sun_angular_zero", "grass_hidden", "trees_hidden", "near_proxy_terrain_hidden", "baseline_repeat"]
	if focused:
		labels = ["baseline", "sunshine_compositor_off", "shadow_filter_medium", "shadow_atlas_8192", "shadow_balanced", "directional_shadows_off", "baseline_repeat"]
	for label in labels:
		restore_state(original)
		apply_case(label)
		await capture_case(label)
		if failed:
			return
	restore_state(original)
	finish()

func parse_args() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--around="):
			around = float(arg.trim_prefix("--around="))
		elif arg.begins_with("--across="):
			across = float(arg.trim_prefix("--across="))
		elif arg.begins_with("--height="):
			height = float(arg.trim_prefix("--height="))
		elif arg.begins_with("--yaw="):
			yaw = float(arg.trim_prefix("--yaw="))
		elif arg.begins_with("--pitch="):
			pitch = float(arg.trim_prefix("--pitch="))
		elif arg.begins_with("--shadow-atlas="):
			shadow_atlas_override = int(arg.trim_prefix("--shadow-atlas="))
	if "--handoff-comparison" in OS.get_cmdline_user_args():
		around = 16443476.0
		across = 38692.0
		yaw = 270.0
		pitch = 0.0

func run_handoff_comparison() -> void:
	var original := state()
	if shadow_atlas_override > 0:
		if not bool(graphics.call("SetShadowTuningOverride", "AtlasSize", shadow_atlas_override)) or int(graphics.get("EffectiveDirectionalShadowAtlasSize")) != shadow_atlas_override:
			fail("requested non-persistent shadow atlas override was rejected or unresolved")
			return
		report["handoff_shadow_atlas_override"] = {"requested":shadow_atlas_override, "effective":graphics.get("EffectiveDirectionalShadowAtlasSize"), "persisted":false}
	var deep_night := night_phase()
	var daytime := day_phase()
	clock.call("SetCycleFraction", deep_night)
	await settle_handoff_phase()
	var night_baseline := await capture_handoff_case("handoff_night_legacy", false, true)
	if failed: return
	var night_automatic := await capture_handoff_case("handoff_night_automatic", true, true)
	if failed: return
	var night_disabled := await capture_handoff_case("handoff_night_requested_off", true, false)
	if failed: return
	var night_repeat := await capture_handoff_case("handoff_night_legacy_repeat", false, true)
	if failed: return
	if not verify_light_stability("night legacy/automatic", night_baseline, night_automatic) or not verify_light_stability("night legacy/repeat", night_baseline, night_repeat):
		return
	if not bool(night_baseline.handoff.get("actual_sun_shadows", false)) or not bool(night_repeat.handoff.get("actual_sun_shadows", false)):
		fail("legacy night comparison did not retain requested sun shadows")
		return
	if not bool(night_automatic.handoff.get("requested_sun_shadows", false)) or not bool(night_automatic.handoff.get("suspended", false)) or bool(night_automatic.handoff.get("actual_sun_shadows", true)):
		fail("automatic handoff did not retain requested sun shadows while suspending the effective sun shadow pass at deepest night")
		return
	if bool(night_disabled.handoff.get("requested_sun_shadows", true)) or bool(night_disabled.handoff.get("actual_sun_shadows", true)):
		fail("explicit requested sun-shadow disable was not retained")
		return
	if bool(night_baseline.planet.get("shadow_enabled", false)) and float(night_baseline.planet.get("energy", 0.0)) > 0.001:
		if not bool(night_automatic.planet.get("shadow_enabled", false)) or float(night_automatic.planet.get("energy", 0.0)) <= 0.001:
			fail("automatic handoff changed active PlanetLight shadows or illumination")
			return
	else:
		await capture_planet_lit_night_diagnostic()
		if failed:
			return
	clock.call("SetCycleFraction", daytime)
	await settle_handoff_phase()
	var day_baseline := await capture_handoff_case("handoff_day_legacy", false, true)
	if failed: return
	var day_automatic := await capture_handoff_case("handoff_day_automatic", true, true)
	if failed: return
	if not verify_light_stability("day legacy/automatic", day_baseline, day_automatic):
		return
	if not bool(day_automatic.handoff.get("actual_sun_shadows", false)):
		fail("automatic handoff did not restore requested sun shadows in daytime")
		return
	if not await verify_handoff_runtime_contract():
		return
	restore_state(original)
	finish()

func day_phase() -> float:
	var best := -INF
	var result := 0.0
	for i in 48:
		var fraction := float(i) / 48.0
		clock.call("SetCycleFraction", fraction)
		var elevation := float(clock.get("SunElevationDegrees"))
		if elevation > best:
			best = elevation
			result = fraction
	return result

func settle_handoff_phase() -> void:
	var deadline := Time.get_ticks_usec() + 3100000
	while Time.get_ticks_usec() < deadline:
		await process_frame

func capture_handoff_case(label: String, handoff_enabled: bool, requested_sun_shadows: bool) -> Dictionary:
	if not set_handoff_enabled(handoff_enabled) or not bool(graphics.call("SetShadowTuningOverride", "Enabled", requested_sun_shadows)):
		fail("local shadow handoff controller override rejected for " + label)
		return {}
	await capture_case(label)
	if failed:
		return {}
	return report.cases[-1].get("state", {}) if not report.cases.is_empty() else {}

func capture_planet_lit_night_diagnostic() -> void:
	var best_energy := -1.0
	var best_phase := -1.0
	for i in 48:
		var fraction := float(i) / 48.0
		clock.call("SetCycleFraction", fraction)
		await process_frame
		if float(clock.get("SunElevationDegrees")) < -18.0 and planet.light_energy > best_energy:
			best_energy = planet.light_energy
			best_phase = fraction
	if best_phase < 0.0:
		fail("could not find a night phase for PlanetLight diagnostic")
		return
	clock.call("SetCycleFraction", best_phase)
	await settle_handoff_phase()
	var legacy := await capture_handoff_case("handoff_planet_lit_night_legacy", false, true)
	if failed: return
	var automatic := await capture_handoff_case("handoff_planet_lit_night_automatic", true, true)
	if failed: return
	if not verify_light_stability("planet-lit night legacy/automatic", legacy, automatic):
		return
	if not bool(automatic.handoff.get("requested_sun_shadows", false)) or not bool(automatic.handoff.get("suspended", false)) or bool(automatic.handoff.get("actual_sun_shadows", true)) or float(automatic.planet.get("energy", 0.0)) <= 0.001 or not bool(automatic.planet.get("shadow_enabled", false)):
		fail("no planet-lit night phase retained positive PlanetLight energy and shadows")

func set_handoff_enabled(enabled: bool) -> bool:
	return bool(graphics.call("SetShadowTuningOverride", "LocalShadowHandoffEnabled", enabled))

func handoff_diagnostics() -> Dictionary:
	return graphics.call("GetLocalShadowHandoffDiagnostics") if graphics.has_method("GetLocalShadowHandoffDiagnostics") else {}

func verify_light_stability(label: String, first: Dictionary, second: Dictionary) -> bool:
	for key in ["star", "planet"]:
		var a: Dictionary = first.get(key, {})
		var b: Dictionary = second.get(key, {})
		var a_direction: Vector3 = a.get("direction", Vector3.ZERO)
		var b_direction: Vector3 = b.get("direction", Vector3.ZERO)
		if absf(float(a.get("energy", 0.0)) - float(b.get("energy", 0.0))) > 0.001 or absf(float(a.get("angular_distance", 0.0)) - float(b.get("angular_distance", 0.0))) > 0.001 or a_direction.distance_to(b_direction) > 0.001:
			fail(label + " changed " + key + " energy or direction")
			return false
	if int(first.get("shared_shadow_atlas", 0)) != int(second.get("shared_shadow_atlas", 0)) or int(first.get("shared_shadow_filter", 0)) != int(second.get("shared_shadow_filter", 0)):
		fail(label + " changed shared shadow atlas or filter")
		return false
	var far_a: Dictionary = first.get("far_sun", {})
	var far_b: Dictionary = second.get("far_sun", {})
	if far_a.is_empty() or far_b.is_empty():
		fail(label + " did not capture the far sun")
		return false
	var far_a_direction: Vector3 = far_a.get("direction", Vector3.ZERO)
	var far_b_direction: Vector3 = far_b.get("direction", Vector3.ZERO)
	if absf(float(far_a.get("energy", 0.0)) - float(far_b.get("energy", 0.0))) > 0.001 or far_a_direction.distance_to(far_b_direction) > 0.001:
		fail(label + " changed far sun energy or direction")
		return false
	return true

func verify_handoff_runtime_contract() -> bool:
	var grid := game.get_node_or_null("ChunkGridManager")
	if grid == null:
		fail("handoff hysteresis check missing ChunkGridManager")
		return false
	var original_night_direct: float = float(grid.get("RingNightDirectLightFactor"))
	var original_cycle_seconds: float = float(clock.call("GetCycleTimeSeconds"))
	var full_cycle_seconds: float = maxf(1.0, float(clock.get("FullCycleSeconds")))
	var steps: Array = []
	if not set_handoff_enabled(true) or not bool(graphics.call("SetShadowTuningOverride", "Enabled", true)) or not bool(graphics.call("SetShadowTuningOverride", "SunShadowOffElevationDegrees", -18.0)) or not bool(graphics.call("SetShadowTuningOverride", "SunShadowOnElevationDegrees", -16.0)):
		fail("handoff hysteresis override rejected")
		return false
	for expectation in [{"elevation":-25.0, "suspended":true}, {"elevation":-17.0, "suspended":true}, {"elevation":-14.0, "suspended":false}, {"elevation":-17.0, "suspended":false}, {"elevation":-25.0, "suspended":true}]:
		var phase := phase_nearest_sun_elevation(float(expectation.elevation))
		clock.call("SetCycleFraction", phase)
		for ignored in 4:
			await process_frame
		var diagnostic := handoff_diagnostics()
		steps.append({"target_sun_elevation":expectation.elevation, "phase":phase, "diagnostic":diagnostic})
		if not bool(diagnostic.get("requested_sun_shadows", false)) or bool(diagnostic.get("suspended", not bool(expectation.suspended))) != bool(expectation.suspended) or bool(diagnostic.get("actual_sun_shadows", bool(expectation.suspended))) == bool(expectation.suspended):
			fail("handoff hysteresis state mismatch near sun elevation " + str(expectation.elevation))
			return false
	clock.call("SetCycleFraction", phase_nearest_sun_elevation(-25.0))
	for ignored in 4:
		await process_frame
	graphics.call("ApplyPreset", graphics.get("CurrentPreset"), false, false)
	for ignored in 4:
		await process_frame
	var after_preset := handoff_diagnostics()
	if not bool(after_preset.get("requested_sun_shadows", false)) or not bool(after_preset.get("suspended", false)):
		fail("handoff did not remain suspended after reapplying the current preset")
		return false
	grid.set("RingNightDirectLightFactor", 0.1)
	for ignored in 4:
		await process_frame
	var direct_lighting := handoff_diagnostics()
	if bool(direct_lighting.get("suspended", true)) or not bool(direct_lighting.get("actual_sun_shadows", false)):
		fail("night direct lighting did not resume requested sun shadows")
		return false
	grid.set("RingNightDirectLightFactor", 0.0)
	for ignored in 4:
		await process_frame
	var restored_direct := handoff_diagnostics()
	if not bool(restored_direct.get("suspended", false)):
		fail("removing night direct lighting did not restore automatic suspension")
		return false
	set_handoff_enabled(false)
	for ignored in 4:
		await process_frame
	var auto_disabled := handoff_diagnostics()
	if bool(auto_disabled.get("suspended", true)) or not bool(auto_disabled.get("actual_sun_shadows", false)):
		fail("disabling automatic handoff did not resume requested sun shadows")
		return false
	clock.call("SetCycleFraction", day_phase())
	for ignored in 4:
		await process_frame
	graphics.call("SetShadowTuningOverride", "Enabled", false)
	for ignored in 4:
		await process_frame
	var manual_disabled := handoff_diagnostics()
	if bool(manual_disabled.get("requested_sun_shadows", true)) or bool(manual_disabled.get("actual_sun_shadows", true)):
		fail("manual requested sun-shadow disable did not remain off in daytime")
		return false
	grid.set("RingNightDirectLightFactor", original_night_direct)
	clock.call("SetCycleFraction", original_cycle_seconds / full_cycle_seconds)
	report["handoff_runtime_contract"] = {"success":true, "steps":steps, "after_preset":after_preset, "night_direct":direct_lighting, "night_direct_restored":restored_direct, "automatic_disabled":auto_disabled, "manual_disabled":manual_disabled}
	write_report()
	return true

func phase_nearest_sun_elevation(target: float) -> float:
	var best_delta := INF
	var result := 0.0
	for i in 720:
		var fraction := float(i) / 720.0
		clock.call("SetCycleFraction", fraction)
		var delta := absf(float(clock.get("SunElevationDegrees")) - target)
		if delta < best_delta:
			best_delta = delta
			result = fraction
	return result

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector := launcher.get("terrain_backend_selector") as OptionButton
	if selector == null:
		fail("backend selector missing")
		return null
	selector.select(2)
	selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		fail("runtime Terrain3D preparation failed")
		return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate()
	install_defaults(next)
	if failed:
		next.queue_free()
		launcher.queue_free()
		return null
	launcher.call("_configure_game", next, 5000000.0)
	var pm := next.get_node_or_null("RingPerformanceMonitor")
	if pm != null:
		pm.call("InstallSettingsPathForTests", fixture.path_join("performance-settings.tres"))
		pm.call("InstallReportDirectoryForTests", fixture.path_join("performance"))
	root.add_child(next)
	current_scene = next
	launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			return next
	fail("runtime Terrain3D startup timeout")
	return null

func install_defaults(next: Node) -> void:
	var tuning := next.get_node_or_null("LiveTuningDrawer")
	if tuning == null:
		return
	for item in [["InstallGrassDefaults", "gameplay_grass_defaults.tres"], ["InstallVegetationDefaults", "gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults", "gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults", "gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults", "gameplay_shadow_defaults.tres"], ["InstallSurfaceTerrainDefaults", "surface_tuning.tres"], ["InstallAtmosphereDefaults", "atmosphere_defaults.tres"]]:
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

func resolve_nodes() -> bool:
	surface = game.get_node_or_null("Terrain3DGameplay")
	explorer = game.get_node_or_null("Explorer")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	clock = game.get_node_or_null("RingDayNightCycle")
	bridge = game.get_node_or_null("SunshineCloudBridge")
	graphics = game.get_node_or_null("GraphicsQuality")
	star = game.get_node_or_null("StarLight") as DirectionalLight3D
	planet = game.get_node_or_null("PlanetLight") as DirectionalLight3D
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	far = game.get_node_or_null("ScaledFarViewport")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	monitor = game.get_node_or_null("RingPerformanceMonitor")
	helper = HELPER.new()
	if surface == null or explorer == null or camera == null or clock == null or bridge == null or graphics == null or star == null or planet == null or grass == null or trees == null or drawer == null:
		fail("required scene node missing")
		return false
	if not bool(surface.call("GetSurfaceDiagnostics").get("runtime_surface_generation", false)):
		fail("runtime Terrain3D backend inactive")
		return false
	return true

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	var global_lod := game.get_node_or_null("GlobalTerrainLod3")
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d: Dictionary = surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = d.get("streaming", {})
		var native: Dictionary = d.get("native", {})
		var prep: Dictionary = native.get("preparation", {})
		var placed := absf(float(d.get("player_around", NAN)) - around) < 1.0 and absf(float(d.get("player_across", NAN)) - across) < 1.0
		# A high flight can legitimately have no near vegetation residents.
		var grass_ready := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0
		var trees_ready := int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0
		var global_ready := global_lod != null and not bool(global_lod.get("GlobalSummaryBuildPending")) and int(global_lod.get("PendingInitialSegmentCount")) == 0 and int(global_lod.get("PendingSegmentRebuildCount")) == 0 and not bool(global_lod.get("GlobalSegmentSamplingWorkerPending"))
		var eye_height := float(helper.call("ResolveCameraCanonicalHeight", game.get_node_or_null("ChunkGridManager"), camera))
		var terrain_height := float(surface.call("SampleHeight", around, across))
		var above_surface := eye_height > terrain_height + 0.5
		var ready := placed and above_surface and bool(d.get("ready", false)) and bool(d.get("visual_coverage", false)) and int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false)) and bool(prep.get("ready", false)) and grass_ready and trees_ready and global_ready
		stable = stable + 1 if ready else 0
		if stable >= 12:
			return true
	fail("terrain/vegetation/global LOD did not settle")
	return false

func freeze_transients() -> void:
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	var weather := game.get_node_or_null("LocalWeatherEffects")
	if weather != null:
		weather.process_mode = Node.PROCESS_MODE_DISABLED
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.call("ApplyCloudAppearanceTuning")

func night_phase() -> float:
	var best := INF
	var result := 0.0
	for i in 48:
		var fraction := float(i) / 48.0
		clock.call("SetCycleFraction", fraction)
		var elevation := float(clock.get("SunElevationDegrees"))
		if elevation < best:
			best = elevation
			result = fraction
	return result

func place(view_yaw: float, view_pitch: float) -> bool:
	if not bool(helper.call("Place", explorer, around, height, across)):
		return false
	explorer.call("SetPresentationView", view_yaw, view_pitch)
	return true

func sweep() -> float:
	var slow_yaw := 0.0
	var worst := -INF
	for heading in [0.0, 90.0, 180.0, 270.0]:
		if not place(heading, 0.0):
			fail("sweep placement failed")
			return slow_yaw
		var warmup := await warmup_case()
		var timing := await measure(12, 1000000)
		report.sweep.append({"yaw":heading, "pitch":0.0, "warmup":warmup, "timing":timing, "pose":pose()})
		write_report()
		var median := float(timing.main_gpu_ms.get("median", 0.0))
		if median > worst:
			worst = median
			slow_yaw = heading
	return slow_yaw

func state() -> Dictionary:
	var clouds = cloud_resource()
	var handoff := handoff_diagnostics()
	return {"clouds":bridge.get("CloudRenderingEnabled"), "bridge_process_mode":bridge.process_mode, "cloud_resource_enabled":clouds.get("enabled") if clouds != null else null, "shadow":star.shadow_enabled, "angular":star.light_angular_distance, "atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "filter":graphics.get("EffectiveDirectionalShadowFilter"), "atmosphere":graphics.call("GetAtmosphereTuningDiagnostics").get("enabled", true), "grass_layers":geometry_layers(grass), "trees_layers":geometry_layers(trees), "near_visibility":near_terrain_visibility(), "handoff":handoff, "star":light_state(star), "planet":light_state(planet), "shared_shadow_atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "shared_shadow_filter":graphics.get("EffectiveDirectionalShadowFilter"), "far_sun":far_sun_state()}

func restore_state(saved: Dictionary) -> void:
	graphics.call("SetShadowTuningOverride", "AtlasSize", saved.atlas)
	graphics.call("SetShadowTuningOverride", "Filter", saved.filter)
	var handoff: Dictionary = saved.get("handoff", {})
	graphics.call("SetShadowTuningOverride", "Enabled", handoff.get("requested_sun_shadows", saved.shadow))
	graphics.call("SetShadowTuningOverride", "LocalShadowHandoffEnabled", handoff.get("handoff_enabled", false))
	star.light_angular_distance = saved.angular
	bridge.set("CloudRenderingEnabled", saved.clouds)
	bridge.process_mode = saved.bridge_process_mode
	var clouds = cloud_resource()
	if clouds != null:
		clouds.set("enabled", saved.cloud_resource_enabled)
	bridge.call("ApplyCloudAppearanceTuning")
	drawer.call("SetAtmosphereEnabled", saved.atmosphere)
	restore_layers(saved.grass_layers)
	restore_layers(saved.trees_layers)
	restore_visibility(saved.near_visibility)

func apply_case(label: String) -> void:
	# Historical attribution isolates sun shadows only.  The runtime handoff is
	# disabled so the controller cannot reinterpret a deliberately fixed case.
	set_handoff_enabled(false)
	if label == "atmosphere_disabled":
		drawer.call("SetAtmosphereEnabled", false)
	elif label == "sunshine_clouds_off":
		bridge.set("CloudRenderingEnabled", false)
		bridge.call("ApplyCloudAppearanceTuning")
	elif label == "sunshine_compositor_off":
		bridge.process_mode = Node.PROCESS_MODE_DISABLED
		var clouds = cloud_resource()
		if clouds != null:
			clouds.set("enabled", false)
	elif label == "directional_shadows_off":
		graphics.call("SetShadowTuningOverride", "Enabled", false)
	elif label == "shadow_filter_hard":
		graphics.call("SetShadowTuningOverride", "Filter", 0)
	elif label == "shadow_filter_medium":
		graphics.call("SetShadowTuningOverride", "Filter", 3)
	elif label == "shadow_atlas_8192":
		graphics.call("SetShadowTuningOverride", "AtlasSize", 8192)
	elif label == "shadow_balanced":
		graphics.call("SetShadowTuningOverride", "AtlasSize", 8192)
		graphics.call("SetShadowTuningOverride", "Filter", 3)
	elif label == "sun_angular_zero":
		star.light_angular_distance = 0.0
	elif label == "grass_hidden":
		set_layers(geometry_layers(grass), 0)
	elif label == "trees_hidden":
		set_layers(geometry_layers(trees), 0)
	elif label == "near_proxy_terrain_hidden":
		set_visibility(near_terrain_visibility(), false)

func capture_case(label: String) -> void:
	if not place(yaw, pitch):
		fail("placement failed for " + label)
		return
	if monitor != null:
		monitor.set("Enabled", true)
		monitor.set("DetailedGpuEnabled", true)
		monitor.call("ClearHistory")
	var warmup := await warmup_case()
	if failed or not verify_case(label):
		return
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	var image_path := fixture.path_join(label + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(image_path)) != OK:
		fail("screenshot failed for " + label)
		return
	var timing := await measure(MEASURE_FRAMES, MEASURE_MIN_USEC)
	var gpu_report = monitor.call("CaptureSlowdown") if monitor != null else "unavailable"
	report.cases.append({"label":label, "shadow_scope":"sun_only" if label == "directional_shadows_off" else "all_directional", "image":image_path, "warmup":warmup, "timing":timing, "gpu_report":gpu_report, "pose":pose(), "ownership":ownership(), "state":state()})
	write_report()

func warmup_case() -> Dictionary:
	var started := Time.get_ticks_usec()
	var frames := 0
	while frames < WARMUP_FRAMES and Time.get_ticks_usec() - started < WARMUP_CAP_USEC:
		await process_frame
		frames += 1
	return {"frames":frames, "seconds":(Time.get_ticks_usec() - started) * 0.000001, "capped":frames < WARMUP_FRAMES}

func measure(minimum_frames: int, minimum_usec: int) -> Dictionary:
	var main_viewport := root.get_viewport()
	RenderingServer.viewport_set_measure_render_time(main_viewport.get_viewport_rid(), true)
	var far_views := find_far_viewports()
	var far_samples := {}
	for view in far_views:
		RenderingServer.viewport_set_measure_render_time(view.get_viewport_rid(), true)
		far_samples[str(view.get_path())] = {"gpu":[], "cpu":[]}
	var main_gpu: Array = []
	var main_cpu: Array = []
	var draws: Array = []
	var primitives: Array = []
	var started := Time.get_ticks_usec()
	while (main_gpu.size() < minimum_frames or Time.get_ticks_usec() - started < minimum_usec) and Time.get_ticks_usec() - started < MEASURE_CAP_USEC:
		await RenderingServer.frame_post_draw
		main_gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(main_viewport.get_viewport_rid()))
		main_cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(main_viewport.get_viewport_rid()))
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		for view in far_views:
			var values: Dictionary = far_samples[str(view.get_path())]
			values.gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(view.get_viewport_rid()))
			values.cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(view.get_viewport_rid()))
	var far_result := {}
	for path in far_samples:
		var values: Dictionary = far_samples[path]
		far_result[path] = {"gpu_ms":stats(values.gpu), "cpu_ms":stats(values.cpu)}
	return {"seconds":(Time.get_ticks_usec() - started) * 0.000001, "samples":main_gpu.size(), "capped":Time.get_ticks_usec() - started >= MEASURE_CAP_USEC, "main_gpu_ms":stats(main_gpu), "main_cpu_ms":stats(main_cpu), "far_viewports":far_result, "draw_calls":stats(draws), "primitives":stats(primitives)}

func stats(values: Array) -> Dictionary:
	if values.is_empty():
		return {"samples":0}
	var ordered := values.duplicate()
	ordered.sort()
	return {"samples":ordered.size(), "min":ordered[0], "median":ordered[ordered.size() / 2], "p95":ordered[mini(ordered.size() - 1, int(ceil(ordered.size() * 0.95)) - 1)], "max":ordered[-1]}

func pose() -> Dictionary:
	var grid := game.get_node_or_null("ChunkGridManager")
	var eye := NAN
	if grid != null:
		eye = float(helper.call("ResolveCameraCanonicalHeight", grid, camera))
	return {"requested":{"around":around, "across":across, "height":height, "yaw":yaw, "pitch":pitch}, "explorer_saved_yaw_radians":explorer.get("SavedYaw"), "explorer_saved_pitch_degrees":explorer.get("SavedPitch"), "camera_global_position":camera.global_position, "camera_basis":camera.global_transform.basis, "canonical_eye_height":eye, "sun_elevation":clock.get("SunElevationDegrees"), "daylight":clock.get("DaylightFactor"), "cycle_time_seconds":clock.call("GetCycleTimeSeconds"), "full_cycle_seconds":clock.get("FullCycleSeconds"), "simulation_seconds":clock.call("GetSimulationSeconds")}

func find_far_viewports() -> Array:
	var result: Array = []
	for node in all_nodes(game):
		if node is SubViewport and ("FarRingViewport" in str(node.get_path()) or "ScaledFarViewport" in str(node.get_path())):
			result.append(node)
	return result

func ownership() -> Dictionary:
	var world := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	var effects: Array = []
	if world != null and world.compositor != null:
		for effect in world.compositor.compositor_effects:
			effects.append(str(effect.resource_path))
	var fogs: Array = []
	for node in all_nodes(game):
		if node is FogVolume:
			var fog := node as FogVolume
			fogs.append({"path":str(fog.get_path()), "visible":fog.visible, "density":fog.material.density if fog.material is FogMaterial else NAN})
	var clouds = cloud_resource()
	var local_renderer := game.get_node_or_null("LocalVolumetricCloudRenderer")
	var volumetric_clouds := game.get_node_or_null("RingVolumetricClouds")
	return {"sunshine_bridge_enabled":bridge.get("CloudRenderingEnabled"), "bridge_process_mode":bridge.process_mode, "cloud_resource_enabled":clouds.get("enabled") if clouds != null else null, "cloud_callback_type":clouds.get("effect_callback_type") if clouds != null else null, "cloud_resolution":clouds.get("resolution_scale") if clouds != null else null, "world_compositor_effects":effects, "local_fog_volumes":fogs, "local_renderer_present":local_renderer != null, "local_renderer_enabled":local_renderer.get("Enabled") if local_renderer != null else null, "conventional_mesh_cloud_geometry":mesh_cloud_geometry(), "fallback_cloud_shell_visible":far.get("FallbackCloudShellVisible") if far != null else null, "root_scaling_3d_scale":root.get("scaling_3d_scale"), "root_msaa_3d":root.get("msaa_3d"), "root_use_taa":root.get("use_taa"), "effective_shadow_filter":graphics.get("EffectiveDirectionalShadowFilter"), "effective_shadow_atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "far_clipmap":far.get("FarCloudClipmapEnabled") if far != null else null, "cloud_mask_active":far.get("CloudMaskActive") if far != null else null, "exact_sunshine_backdrop_active":far.get("ExactSunshineBackdropActive") if far != null else null, "cloud_simulation_time_seconds":volumetric_clouds.get("CloudSimulationTimeSeconds") if volumetric_clouds != null else null}

func cloud_resource():
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	return driver.get("clouds_resource") if driver != null else null

func mesh_cloud_geometry() -> Array:
	var result: Array = []
	for node in all_nodes(game):
		if node is GeometryInstance3D and ("Cloud" in node.name or "cloud" in str(node.get_path())):
			result.append({"path":str(node.get_path()), "visible":node.visible, "layers":node.layers})
	return result

func verify_case(label: String) -> bool:
	var clouds = cloud_resource()
	if label == "sunshine_clouds_off":
		if bool(bridge.get("CloudRenderingEnabled")) or clouds == null or bool(clouds.get("enabled")):
			fail("cloud toggle did not disable active cloud resource")
			return false
	elif label == "sunshine_compositor_off":
		if not bool(bridge.get("CloudRenderingEnabled")) or bridge.process_mode != Node.PROCESS_MODE_DISABLED or clouds == null or bool(clouds.get("enabled")):
			fail("Sunshine compositor isolation changed requested ownership or left resource enabled")
			return false
	elif label == "directional_shadows_off":
		if star.shadow_enabled:
			fail("sun-only directional shadow toggle ineffective")
			return false
	elif label == "shadow_filter_hard":
		if int(graphics.get("EffectiveDirectionalShadowFilter")) != 0:
			fail("hard shadow filter override ineffective")
			return false
	elif label == "shadow_filter_medium":
		if int(graphics.get("EffectiveDirectionalShadowFilter")) != 3:
			fail("medium shadow filter override ineffective")
			return false
	elif label == "shadow_atlas_8192":
		if int(graphics.get("EffectiveDirectionalShadowAtlasSize")) != 8192:
			fail("8192 shadow atlas override ineffective")
			return false
	elif label == "shadow_balanced":
		if int(graphics.get("EffectiveDirectionalShadowAtlasSize")) != 8192 or int(graphics.get("EffectiveDirectionalShadowFilter")) != 3:
			fail("balanced shadow overrides ineffective")
			return false
	elif label == "sun_angular_zero":
		if star.light_angular_distance > 0.0001:
			fail("sun angular-distance override ineffective")
			return false
	elif label == "atmosphere_disabled":
		if bool(graphics.call("GetAtmosphereTuningDiagnostics").get("enabled", true)):
			fail("atmosphere toggle ineffective")
			return false
	elif label == "grass_hidden":
		if not all_layers_equal(geometry_layers(grass), 0):
			fail("grass geometry layers remained visible")
			return false
	elif label == "trees_hidden":
		if not all_layers_equal(geometry_layers(trees), 0):
			fail("tree geometry layers remained visible")
			return false
	elif label == "near_proxy_terrain_hidden":
		if not all_visibility_equal(near_terrain_visibility(), false):
			fail("near Terrain3D or proxy visibility remained enabled")
			return false
	return true

func light_state(light: DirectionalLight3D) -> Dictionary:
	return {"shadow_enabled":light.shadow_enabled, "energy":light.light_energy, "direction":light.global_basis.z, "angular_distance":light.light_angular_distance, "shadow_distance":light.directional_shadow_max_distance}

func far_sun_state() -> Dictionary:
	if far == null:
		return {}
	var candidate = far.find_child("FarRingSun", true, false) as DirectionalLight3D
	return light_state(candidate) if candidate != null else {}

func all_nodes(node: Node) -> Array:
	var result: Array = [node]
	for child in node.get_children():
		result.append_array(all_nodes(child))
	return result

func geometry_layers(root_node: Node) -> Array:
	var result: Array = []
	for node in all_nodes(root_node):
		if node is GeometryInstance3D:
			result.append({"node":node, "layers":(node as GeometryInstance3D).layers})
	return result

func near_terrain_visibility() -> Array:
	var result: Array = [{"node":surface, "visible":surface.visible}]
	for node in all_nodes(game):
		if node.get_script() != null and "RingTerrainProxySector" in node.get_script().resource_path:
			result.append({"node":node, "visible":node.visible})
	return result

func set_layers(values: Array, layers: int) -> void:
	for value in values:
		if is_instance_valid(value.node):
			value.node.layers = layers

func restore_layers(values: Array) -> void:
	for value in values:
		if is_instance_valid(value.node):
			value.node.layers = value.layers

func all_layers_equal(values: Array, expected: int) -> bool:
	if values.is_empty():
		return false
	for value in values:
		if not is_instance_valid(value.node) or int(value.node.layers) != expected:
			return false
	return true

func set_visibility(values: Array, visible: bool) -> void:
	for value in values:
		if is_instance_valid(value.node):
			value.node.visible = visible

func restore_visibility(values: Array) -> void:
	for value in values:
		if is_instance_valid(value.node):
			value.node.visible = value.visible

func all_visibility_equal(values: Array, expected: bool) -> bool:
	if values.is_empty():
		return false
	for value in values:
		if not is_instance_valid(value.node) or bool(value.node.visible) != expected:
			return false
	return true

func write_report() -> bool:
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file == null:
		failed = true
		report.status = "FAIL"
		report.failure = "could not write diagnostic report"
		push_error("NIGHT_HORIZON_ATTRIBUTION=FAIL could not write diagnostic report")
		quit(1)
		return false
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	return true

func finish() -> void:
	if failed:
		return
	if "--handoff-comparison" in OS.get_cmdline_user_args() and not bool(report.get("handoff_runtime_contract", {}).get("success", false)):
		fail("handoff comparison cannot pass without a successful runtime contract")
		return
	report.status = "PASS"
	if not write_report():
		return
	if game != null:
		game.queue_free()
	await process_frame
	print("NIGHT_HORIZON_ATTRIBUTION=PASS fixture=" + fixture)
	quit(0)

func fail(message: String) -> void:
	if failed:
		return
	failed = true
	report.status = "FAIL"
	report.failure = message
	write_report()
	push_error("NIGHT_HORIZON_ATTRIBUTION=FAIL " + message)
	quit(1)
