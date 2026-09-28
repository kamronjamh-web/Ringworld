extends SceneTree

# Diagnostic-only fixed-forest comparison. It never saves cloud or wind values.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const AROUND := 16443476.0
const ACROSS := 38692.0
const SETTLE_FRAMES := 48

var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var camera: Camera3D
var clock: Node
var bridge: Node
var driver: Node
var weather: Node
var grass: Node
var trees: Node
var global_terrain: Node
var helper: RefCounted
var feet := 0.0
var normal_blur := 0.0
var failed := false
var report := {
	"status": "RUNNING",
	"captures": [],
	"reference_note": "res://.godot/atmosphere_visual/4115113 predates the grass fix and used different wind; it is visual context only, not a pixel comparator."
}

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary:
		return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void:
		settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool:
		return false

func _init() -> void:
	call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null:
		return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/night-rendering-comparison/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var old_session := root.get_node_or_null("WorldSession")
	if old_session != null:
		old_session.name = "WorldSessionOriginalNightComparison"
	var session := SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	game = await create_game()
	if game == null:
		return
	if not resolve_nodes():
		return
	feet = float(surface.call("SampleHeight", AROUND, ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", AROUND, feet, ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready():
		return
	if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")):
		return fail("forest fixture is underwater")
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	weather.process_mode = Node.PROCESS_MODE_DISABLED
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.call("ApplyCloudAppearanceTuning")
	normal_blur = float(bridge.get("BlurPower"))
	var phase := select_phases()
	if "--night-only" not in OS.get_cmdline_user_args():
		await capture("noon_baseline", phase["noon"], true, normal_blur, false, true)
		if failed:
			return
	await capture("night_baseline", phase["night"], true, normal_blur, false, false)
	if failed: return
	await capture("night_clouds_disabled", phase["night"], false, normal_blur, false, false)
	if failed: return
	await capture("night_clouds_normal_blur", phase["night"], true, normal_blur, false, false)
	if failed: return
	await capture("night_clouds_blur_zero", phase["night"], true, 0.0, false, false)
	if failed: return
	await capture("night_clouds_blur_zero_taa", phase["night"], true, 0.0, true, false)
	if failed: return
	report.status = "PASS"
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file == null:
		return fail("report write failed")
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	game.queue_free()
	await process_frame
	print("NIGHT_RENDERING_COMPARISON=PASS fixture=" + fixture)
	quit(0)

func resolve_nodes() -> bool:
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	clock = game.get_node_or_null("RingDayNightCycle")
	bridge = game.get_node_or_null("SunshineCloudBridge")
	driver = game.get_node_or_null("SunshineCloudsDriver")
	weather = game.get_node_or_null("LocalWeatherEffects")
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	helper = HELPER.new()
	if explorer == null or surface == null or camera == null or clock == null or bridge == null or driver == null or weather == null or grass == null or trees == null or global_terrain == null:
		fail("required scene nodes missing")
		return false
	var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	if not bool(diagnostics.get("runtime_surface_generation", false)):
		fail("runtime Terrain3D generation inactive")
		return false
	return true

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
		fail("runtime preparation failed")
		return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate()
	install_isolated(next)
	launcher.call("_configure_game", next, 5000000.0)
	root.add_child(next)
	current_scene = next
	launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			return next
	fail("Terrain3D startup timeout")
	return null

func install_isolated(next: Node) -> void:
	var drawer := next.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		return
	for item in [["InstallGrassDefaults", "grass.tres"], ["InstallVegetationDefaults", "vegetation.tres"], ["InstallWaterDefaults", "water.tres"], ["InstallAntiAliasingDefaults", "aa.tres"], ["InstallShadowDefaults", "shadow.tres"], ["InstallSurfaceTerrainDefaults", "surface.tres"], ["InstallAtmosphereDefaults", "atmosphere.tres"]]:
		drawer.call(str(item[0]), fixture.path_join(str(item[1])))

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d: Dictionary = surface.call("GetSurfaceDiagnostics")
		var stream: Dictionary = d.get("streaming", {})
		var native: Dictionary = d.get("native", {})
		var prep: Dictionary = native.get("preparation", {})
		var placed := absf(float(d.get("player_around", NAN)) - AROUND) < 1.0 and absf(float(d.get("player_across", NAN)) - ACROSS) < 1.0
		var ready := placed and bool(d.get("ready", false)) and bool(d.get("visual_coverage", false)) and int(stream.get("pending_jobs", 0)) == 0 and int(stream.get("active_jobs", 0)) == 0 and not bool(stream.get("packing", false)) and bool(prep.get("ready", false)) and int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 and int(grass.get("ResidentGrassInstanceCount")) > 0 and int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0 and int(trees.get("GpuTreeResidentRootCount")) > 0 and int(global_terrain.get("PendingInitialSegmentCount")) == 0 and int(global_terrain.get("PendingSegmentRebuildCount")) == 0 and not bool(global_terrain.get("GlobalSummaryBuildPending")) and not bool(global_terrain.get("GlobalSegmentSamplingWorkerPending"))
		stable = stable + 1 if ready else 0
		if stable >= 12:
			return true
	fail("forest workload did not settle")
	return false

func select_phases() -> Dictionary:
	var noon := 0.0
	var night := 0.0
	var highest: float = -INF
	var lowest: float = INF
	for step in 48:
		var fraction: float = float(step) / 48.0
		clock.call("SetCycleFraction", fraction)
		var elevation: float = float(clock.get("SunElevationDegrees"))
		if elevation > highest:
			highest = elevation
			noon = fraction
		if elevation < lowest:
			lowest = elevation
			night = fraction
	return {"noon": noon, "night": night}

func capture(label: String, fraction: float, clouds_enabled: bool, blur: float, taa: bool, require_day: bool) -> void:
	if not bool(helper.call("Place", explorer, AROUND, feet, ACROSS)):
		return fail("fixture placement failed")
	clock.set("Paused", true)
	clock.call("SetCycleFraction", fraction)
	bridge.set("CloudRenderingEnabled", clouds_enabled)
	bridge.set("BlurPower", blur)
	bridge.call("ApplyCloudAppearanceTuning")
	bridge.call("ApplyTemporalQualityTuning", true)
	root.set("use_taa", taa)
	await frames(SETTLE_FRAMES)
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	for node in all_nodes(game):
		if node is SubViewport:
			RenderingServer.viewport_set_measure_render_time((node as SubViewport).get_viewport_rid(), true)
	var elevation := float(clock.get("SunElevationDegrees"))
	var daylight := float(clock.get("DaylightFactor"))
	if require_day and (elevation <= 0.0 or daylight <= 0.01):
		return fail("noon phase is not daylight")
	if not require_day and (elevation >= 0.0 or daylight > 0.05):
		return fail("night phase is not dark")
	var resource = driver.get("clouds_resource")
	if resource == null or bool(resource.get("enabled")) != clouds_enabled:
		return fail("cloud resource did not apply requested enabled state")
	if clouds_enabled and absf(float(resource.get("blur_power")) - blur) > 0.001:
		return fail("cloud resource did not apply requested blur")
	var grid := game.get_node_or_null("ChunkGridManager")
	var eye: float = float(helper.call("ResolveCameraCanonicalHeight", grid, camera))
	if eye <= feet + 0.5:
		return fail("camera eye invalid")
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	var path := fixture.path_join(label + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(path)) != OK:
		return fail("capture failed " + label)
	var cycle_fraction := float(clock.call("GetCycleTimeSeconds")) / maxf(1.0, float(clock.get("FullCycleSeconds")))
	var timing := await sample_render_timings()
	var grass_bindings := grass_shader_bindings()
	if not validate_grass_bindings(grass_bindings, require_day):
		return
	report.captures.append({"label": label, "image": path, "clock_fraction": cycle_fraction, "sun_elevation": elevation, "daylight": daylight, "eye_height": eye, "feet_height": feet, "vegetation": {"grass_roots": grass.get("ResidentGrassInstanceCount"), "tree_roots": trees.get("GpuTreeResidentRootCount"), "coverage": surface.call("GetSurfaceDiagnostics").get("visual_coverage")}, "clouds": {"bridge_enabled": bridge.get("CloudRenderingEnabled"), "bridge_blur": bridge.get("BlurPower"), "wind_multiplier": bridge.get("CloudWindMultiplier"), "resource_enabled": resource.get("enabled"), "resource_blur": resource.get("blur_power"), "resolution": resource.get("resolution_scale"), "callback_type": resource.get("effect_callback_type")}, "wind": {"global_gate_set": 0.0, "local_weather_processing": false}, "taa": root.get("use_taa"), "grass_shader_bindings": grass_bindings, "timing": timing})

func sample_render_timings() -> Dictionary:
	var main: Array[float] = []
	var far: Array[float] = []
	var draws: Array[float] = []
	var far_viewport := find_far_viewport()
	var started := Time.get_ticks_usec()
	while main.size() < 90 or Time.get_ticks_usec() - started < 3000000:
		await RenderingServer.frame_post_draw
		main.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		if far_viewport != null:
			far.append(RenderingServer.viewport_get_measured_render_time_gpu(far_viewport.get_viewport_rid()))
	return {"sample_seconds": (Time.get_ticks_usec() - started) * .000001, "main_gpu_ms": stats(main), "far_gpu_ms": stats(far), "draw_calls": stats(draws)}

func find_far_viewport() -> SubViewport:
	for node in all_nodes(game):
		if node is SubViewport and "ScaledFarViewport" in str(node.get_path()):
			return node
	return null

func stats(values: Array[float]) -> Dictionary:
	if values.is_empty():
		return {"samples": 0}
	var ordered := values.duplicate()
	ordered.sort()
	var p95: float = ordered[mini(ordered.size() - 1, int(ceil(ordered.size() * .95)) - 1)]
	return {"samples": ordered.size(), "median": ordered[ordered.size() / 2], "p95": p95, "min": ordered[0], "max": ordered[-1]}

func grass_shader_bindings() -> Array:
	var result: Array = []
	for node in all_nodes(game):
		if node is MultiMeshInstance3D and node.is_visible_in_tree():
			var material = (node as MultiMeshInstance3D).material_override
			if material is ShaderMaterial:
				var shader := (material as ShaderMaterial).shader
				var path := shader.resource_path if shader != null else ""
				if "grass" in path:
					result.append({"path": str(node.get_path()), "shader": path, "gate_enabled": (material as ShaderMaterial).get_shader_parameter("grass_primary_sun_gate_enabled"), "sun_direction": (material as ShaderMaterial).get_shader_parameter("grass_primary_sun_direction"), "day_factor": (material as ShaderMaterial).get_shader_parameter("grass_primary_sun_day_factor"), "night_direct": (material as ShaderMaterial).get_shader_parameter("grass_primary_sun_night_direct"), "day_direct": (material as ShaderMaterial).get_shader_parameter("grass_primary_sun_day_direct")})
	return result

func validate_grass_bindings(bindings: Array, require_day: bool) -> bool:
	if bindings.is_empty():
		fail("no visible grass ShaderMaterials were found")
		return false
	for binding in bindings:
		if not bool(binding.get("gate_enabled", false)):
			fail("grass primary sun gate is disabled: " + str(binding.get("path", "")))
			return false
		var day_factor := float(binding.get("day_factor", NAN))
		if require_day and day_factor < 0.99:
			fail("grass primary sun day factor is not daytime")
			return false
		if not require_day and day_factor > 0.01:
			fail("grass primary sun day factor is not nighttime")
			return false
	return true

func all_nodes(node: Node) -> Array:
	var result: Array = [node]
	for child in node.get_children():
		result.append_array(all_nodes(child))
	return result

func frames(count: int) -> void:
	for ignored in count:
		await process_frame

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
	push_error("NIGHT_RENDERING_COMPARISON=FAIL " + message)
	quit(1)
