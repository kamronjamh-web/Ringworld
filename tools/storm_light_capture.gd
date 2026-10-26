extends SceneTree
# Storm light acceptance capture (docs/storm-revamp-s0-storm-light-design.md). Boots the certified gameplay scene at a fixed noon pose, spawns a storm front at 0, 6, 12 and 18 km
# (45 km for the outside view) and saves down / up / debug-view shots plus report.json (storm-light state, luma, CPU vs shader D probe).
# Windowed GPU only; run it through tools/storm_light_capture.ps1.
#   --out=<dir>  --elev=<sun elevation>  --dists=0,6000,12000,18000  --no-outside  --no-ae  --no-probe  --perf  --perf-only
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
var FIXTURE_AROUND := 8150667.0
var FIXTURE_ACROSS := 119474.0
const SETTLE_FRAMES := 18
var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var drawer: Node
var clock: Node
var graphics: Node
var camera: Camera3D
var grass: Node
var trees: Node
var global_terrain: Node
var placement: RefCounted
var report := {"status":"RUNNING", "captures":[]}
var bridge: Node
var atmo: Node
var sun: DirectionalLight3D
var out_dir := ""
var ground := 0.0
var noon := 0.0
var dists: Array = [0.0, 6000.0, 12000.0, 18000.0, 24000.0, 30000.0, 36000.0]
var do_outside := true
var do_ae := true
var do_probe := true
var probe_only := false
var do_perf := false
var perf_only := false
var perf_report := {}
var probe_report := {}
var luma := {}

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func run() -> void:
	create_timer(1500.0).timeout.connect(func(): fail("watchdog"))
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	out_dir = "C:/Projects/Ringworld/.godot/captures/storm_light/run"
	var elevation_target := 40.0
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--out="): out_dir = argument.get_slice("=", 1)
		if argument.begins_with("--elev="): elevation_target = float(argument.get_slice("=", 1))
		if argument.begins_with("--dists="):
			dists = []
			for part in argument.get_slice("=", 1).split(",", false): dists.append(float(part))
		if argument == "--no-outside": do_outside = false
		if argument == "--no-ae": do_ae = false
		if argument == "--no-probe": do_probe = false
		if argument == "--probe-only":
			probe_only = true; perf_only = true; dists = [6000.0]; do_outside = false; do_ae = false; do_probe = true
		if argument == "--perf": do_perf = true
		if argument == "--perf-only":
			do_perf = true; perf_only = true; dists = [0.0]; do_outside = false; do_ae = false; do_probe = false
		if argument.begins_with("--around="): FIXTURE_AROUND = float(argument.get_slice("=", 1))
		if argument.begins_with("--across="): FIXTURE_ACROSS = float(argument.get_slice("=", 1))
	DirAccess.make_dir_recursive_absolute(out_dir)
	fixture = "res://.godot/atmosphere_visual/storm_light_%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalAtmosphere"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	clock = game.get_node_or_null("RingDayNightCycle")
	graphics = game.get_node_or_null("GraphicsQuality")
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	bridge = game.get_node_or_null("SunshineCloudBridge")
	atmo = game.get_node_or_null("DaySideAtmosphere")
	sun = game.get_node_or_null("StarLight") as DirectionalLight3D
	placement = FIXTURE_HELPER.new()
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	if explorer == null or surface == null or drawer == null or clock == null or camera == null or bridge == null: return fail("missing nodes")
	drawer.call("SetAtmosphereEnabled", true)
	drawer.call("SetAtmosphereHazeModel", 1)
	ground = float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	var best := 0.0
	var best_err := INF
	for step in 192:
		var f := float(step) / 192.0
		clock.call("SetCycleFraction", f)
		var e := float(clock.get("SunElevationDegrees"))
		if absf(e - elevation_target) < best_err: best_err = absf(e - elevation_target); best = f
	noon = best
	clock.set("Paused", true)
	clock.call("SetCycleFraction", noon)
	await settle_seconds(4.0)
	var weather := game.get_node_or_null("LocalWeatherEffects")
	await shot("clear_down", 0.0, -20.0)
	await shot("clear_up60", 0.0, 60.0)
	await shot("clear_horizon", 0.0, 3.0)
	for d in dists:
		explorer.call("SetPresentationView", 0.0, 0.0)
		await frames(2)
		if weather != null: weather.set("PrecipitationStart", 0.18 if d == 0.0 else 0.99)
		bridge.set("SpawnDistanceMeters", d)
		bridge.call("SpawnStormFrontAhead")
		await settle_seconds(14.0)
		var tag := "d%02d" % int(d / 1000.0)
		await shot(tag + "_down", 0.0, -20.0)
		await shot(tag + "_horizon", 0.0, 3.0)
		await shot(tag + "_down_dbg_sun", 0.0, -20.0, 1)
		await shot(tag + "_down_dbg_cloud", 0.0, -20.0, 5)
		await shot(tag + "_down_dbg_storm", 0.0, -20.0, 6)
		if do_probe and d == 6000.0: await probe_storm_level("d06")
		await shot(tag + "_up60", 0.0, 60.0)
		await shot(tag + "_up85", 0.0, 85.0)
		await shot(tag + "_up45_y180", 180.0, 45.0)
		await shot(tag + "_up30_y90", 90.0, 30.0)
		if d == 0.0:
			if weather != null: weather.set("PrecipitationStart", 0.99)
			await settle_seconds(4.0)
			await shot(tag + "_norain_up60", 0.0, 60.0)
			await shot(tag + "_norain_up85", 0.0, 85.0)
			await shot(tag + "_norain_down", 0.0, -20.0)
			if do_ae and atmo != null: atmo.set("storm_auto_exposure_enabled", false)
			await settle_seconds(4.0)
			if do_ae: await shot(tag + "_norain_noae_down", 0.0, -20.0)
			if do_ae: await shot(tag + "_norain_noae_up60", 0.0, 60.0)
			if atmo != null: atmo.set("storm_auto_exposure_enabled", true)
	explorer.call("SetPresentationView", 0.0, 0.0)
	await frames(2)
	if do_outside:
		bridge.set("SpawnDistanceMeters", 45000.0)
		bridge.call("SpawnStormFrontAhead")
		await settle_seconds(14.0)
		await shot("outside_horizon", 0.0, 2.0)
		await shot("outside_up12", 0.0, 12.0)
		await shot("outside_down", 0.0, -5.0)
	if do_perf: await perf_aba()
	report["perf"] = perf_report
	report["probe"] = probe_report
	report["luma"] = luma
	var summary := summarize()
	report["summary"] = summary
	print("STORM_LIGHT_CAPTURE_SUMMARY ", JSON.stringify(summary))
	report.status = "PASS"
	var f2 := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	f2.store_string(JSON.stringify(report, "\t")); f2.close()
	print("STORM_LIGHT_CAPTURE=PASS")
	quit(0)

func settle_seconds(s: float) -> void:
	var end := Time.get_ticks_msec() + int(s * 1000.0)
	while Time.get_ticks_msec() < end: await process_frame

func shot(label: String, yaw: float, pitch: float, debug_mode := 0) -> void:
	if perf_only: return
	explorer.call("SetPresentationView", yaw, pitch)
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", debug_mode)
	await frames(SETTLE_FRAMES)
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	image.save_png(out_dir.path_join(label + ".png"))
	if debug_mode == 0: luma[label] = image_luma(image)
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
	var env: Environment = null
	var we := game.find_child("WorldEnvironment", true, false) as WorldEnvironment
	if we != null: env = we.environment
	var d := {"label":label, "yaw":yaw, "pitch":pitch, "debug":debug_mode,
		"sun_elev":clock.get("SunElevationDegrees"),
		"cam_sun_T":bridge.get("CameraDirectSunTransmittance"), "cam_amb_T":bridge.get("CameraAmbientTransmittance"),
		"haze_storm_factor":bridge.call("GetHazeStormFactor"), "diag":bridge.call("GetCloudLightingDiagnostics"),
		"atmo_cloud_amb_T":atmo.get("current_cloud_ambient_transmittance") if atmo else null,
		"atmo_air_sun_vis":atmo.get("current_air_sun_visibility") if atmo else null,
		"atmo_haze_storm":atmo.get("current_haze_storm") if atmo else null,
		"sun_energy":sun.light_energy if sun else null, "sun_shadow":sun.shadow_enabled if sun else null,
		"sun_shadow_opacity":sun.shadow_opacity if sun else null,
		"env_amb_energy":env.ambient_light_energy if env else null, "env_amb_color":str(env.ambient_light_color) if env else null,
		"env_sky_contrib":env.ambient_light_sky_contribution if env else null,
		"storm_valid":bridge.call("IsStormLightValid"), "storm_D_camera":bridge.call("GetCameraStormLevel"),
		"storm_ambient_camera":bridge.call("GetStormAmbientAtCamera"), "storm_eye_cap":bridge.call("GetStormEyeCap"),
		"atmo_storm_ambient":atmo.get("current_storm_ambient") if atmo else null, "atmo_storm_eye_cap":atmo.get("current_storm_eye_cap") if atmo else null,
		"exposure_max_sensitivity":attributes_max_sensitivity(we), "luma":luma.get(label, null)}
	print("SHOT ", JSON.stringify(d))
	report.captures.append(d)

# A-B-A-B-A receiver cost: A = the storm-light globals report valid = 0 (every receiver returns D = 0 before any fetch), B = valid (the storm map fetched per receiver
# fragment). The bridge only rewrites ringworld_storm_light when its own value changes, so this override stays in place. Pose: standing in a heavy storm at noon, so
# the fetch hits a non-trivial map. GPU/CPU render time of the root viewport, median and p95 over WINDOW frames per round after SETTLE frames.
func perf_aba() -> void:
	var viewport_rid := root.get_viewport().get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(viewport_rid, true)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	explorer.call("SetPresentationView", 0.0, -5.0)
	var fade_start := float(bridge.get("StormShadowFade"))
	var live := Vector4(float(bridge.get("StormDarkness")), fade_start, fade_start + 0.35, 1.0)
	perf_report["storm_light_live"] = str(live)
	perf_report["window_size"] = str(root.get_viewport().get_visible_rect().size)
	var rounds := []
	for label in ["A", "B", "A", "B", "A"]:
		var value := Vector4(live.x, live.y, live.z, 0.0) if label == "A" else live
		RenderingServer.global_shader_parameter_set("ringworld_storm_light", value)
		await frames(120)
		var gpu := []
		var cpu := []
		var wall := []
		var last := Time.get_ticks_usec()
		for i in 400:
			await process_frame
			var now := Time.get_ticks_usec()
			wall.append((now - last) / 1000.0)
			last = now
			gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport_rid))
			cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(viewport_rid))
		rounds.append({"arm": label, "gpu_median": median_of(gpu), "gpu_p95": percentile_of(gpu, 0.95), "cpu_median": median_of(cpu), "wall_median": median_of(wall), "wall_p95": percentile_of(wall, 0.95)})
	RenderingServer.global_shader_parameter_set("ringworld_storm_light", live)
	perf_report["rounds"] = rounds
	for arm in ["A", "B"]:
		var gpu_medians := []
		var wall_medians := []
		for r in rounds:
			if r.arm == arm:
				gpu_medians.append(r.gpu_median)
				wall_medians.append(r.wall_median)
		perf_report[arm + "_gpu_median_ms"] = median_of(gpu_medians)
		perf_report[arm + "_wall_median_ms"] = median_of(wall_medians)
	perf_report["delta_B_minus_A_gpu_ms"] = float(perf_report["B_gpu_median_ms"]) - float(perf_report["A_gpu_median_ms"])
	perf_report["A_repeat_spread_gpu_ms"] = absf(float(rounds[0].gpu_median) - float(rounds[2].gpu_median))
	print("STORM_LIGHT_PERF ", JSON.stringify(perf_report))

func median_of(values: Array) -> float:
	if values.is_empty(): return 0.0
	var sorted := values.duplicate()
	sorted.sort()
	return float(sorted[sorted.size() / 2])

func percentile_of(values: Array, q: float) -> float:
	if values.is_empty(): return 0.0
	var sorted := values.duplicate()
	sorted.sort()
	return float(sorted[mini(sorted.size() - 1, int(float(sorted.size()) * q))])

# Mean linear luma of the central crop (x 31-74 %, y 14-82 %: clear of the HUD panels and the minimap), sampled every 3rd pixel; sRGB bytes -> linear with a 2.2 power.
func image_luma(image: Image) -> float:
	image.convert(Image.FORMAT_RGBA8)
	var data := image.get_data()
	var w := image.get_width()
	var h := image.get_height()
	var sum := 0.0
	var count := 0
	for y in range(int(h * 0.14), int(h * 0.82), 3):
		for x in range(int(w * 0.31), int(w * 0.74), 3):
			var i := (y * w + x) * 4
			sum += 0.2126 * pow(data[i] / 255.0, 2.2) + 0.7152 * pow(data[i + 1] / 255.0, 2.2) + 0.0722 * pow(data[i + 2] / 255.0, 2.2)
			count += 1
	return sum / maxf(1.0, float(count))

func attributes_max_sensitivity(we: WorldEnvironment) -> Variant:
	if we == null or not (we.camera_attributes is CameraAttributesPractical): return null
	return (we.camera_attributes as CameraAttributesPractical).auto_exposure_max_sensitivity

# CPU storm level (bridge.GetStormLevelAt) against the shader's (ring_storm_level, read through the macro map) at 16 world positions around the camera,
# then again with the macro map off, where the globals must report valid = 0 and the shader must return D = 0 everywhere.
func probe_storm_level(tag: String) -> void:
	# The first render of a fresh SubViewport compiles the probe shader, so the first reading is a warm-up; "on" is the second.
	await run_probe()
	var on := await run_probe()
	on["valid_flag"] = bool(bridge.call("IsStormLightValid"))
	
	probe_report[tag] = {"on": on}
	bridge.set("MacroWeatherMap", false)
	await settle_seconds(1.5)
	var off := await run_probe()
	off["valid_flag"] = bool(bridge.call("IsStormLightValid"))
	
	probe_report[tag]["map_off"] = off
	bridge.set("MacroWeatherMap", true)
	await settle_seconds(3.0)
	var back := await run_probe()
	back["valid_flag"] = bool(bridge.call("IsStormLightValid"))
	probe_report[tag]["map_on_again"] = back
	print("STORM_LIGHT_PROBE ", JSON.stringify(probe_report[tag]))

func run_probe() -> Dictionary:
	var cam: Vector3 = bridge.call("GetExplorerCanonicalPosition")
	var worlds := PackedVector3Array()
	var cpu := PackedFloat32Array()
	for da in [-45000.0, -20000.0, 20000.0, 45000.0]:
		for dc in [-60000.0, -25000.0, 25000.0, 60000.0]:
			worlds.append(bridge.call("GetStormProbeWorldPosition", cam.x + da, 0.0, cam.z + dc))
			cpu.append(float(bridge.call("GetStormLevelAt", cam.x + da, cam.z + dc)))
	var viewport := SubViewport.new()
	viewport.size = Vector2i(16, 1)
	viewport.disable_3d = true
	viewport.transparent_bg = false
	viewport.use_hdr_2d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var rect := ColorRect.new()
	rect.size = Vector2(16, 1)
	var material := ShaderMaterial.new()
	material.shader = load("res://tools/storm_light_probe.gdshader")
	material.set_shader_parameter("probe_world", worlds)
	rect.material = material
	viewport.add_child(rect)
	root.add_child(viewport)
	await frames(4)
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var shader_d := PackedFloat32Array()
	var max_error := 0.0
	var max_cpu := 0.0
	var max_shader := 0.0
	for i in 16:
		var value := image.get_pixel(i, 0).r
		shader_d.append(value)
		max_error = maxf(max_error, absf(value - cpu[i]))
		max_cpu = maxf(max_cpu, cpu[i])
		max_shader = maxf(max_shader, value)
	viewport.queue_free()
	var partial := 0
	for value in cpu:
		if value > 0.05 and value < 0.95: partial += 1
	return {"cpu": Array(cpu), "shader": Array(shader_d), "max_abs_error": max_error, "max_cpu": max_cpu, "max_shader": max_shader, "partial_points": partial}

# Ratios of the storm shots to the clear shots (mean linear luma) for the acceptance report.
func summarize() -> Dictionary:
	var out := {}
	var clear_down: float = luma.get("clear_down", 0.0)
	var clear_up: float = luma.get("clear_up60", 0.0)
	for key in luma.keys():
		var name := str(key)
		if name.begins_with("d") or name.begins_with("outside"):
			var reference := clear_up if "_up" in name else clear_down
			if "_horizon" in name: reference = luma.get("clear_horizon", 0.0)
			if name.begins_with("outside"): reference = luma.get("clear_horizon", 0.0)
			out[name + "_ratio"] = float(luma[key]) / maxf(reference, 0.000001)
	for key in luma.keys():
		var name := str(key)
		if name.begins_with("d") and name.ends_with("_down") and not "norain" in name:
			var tag := name.substr(0, 3)
			var ground_luma: float = luma[key]
			if luma.has(tag + "_horizon"): out[tag + "_horizon_over_ground"] = float(luma[tag + "_horizon"]) / maxf(ground_luma, 0.000001)
			if luma.has(tag + "_up60"): out[tag + "_up60_over_ground"] = float(luma[tag + "_up60"]) / maxf(ground_luma, 0.000001)
	return out

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: fail("missing terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)): fail("runtime generation preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate()
	install_isolated_preferences(next)
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
	for item in [["InstallGrassDefaults","grass.tres"], ["InstallVegetationDefaults","vegetation.tres"], ["InstallWaterDefaults","water.tres"], ["InstallAntiAliasingDefaults","anti_aliasing.tres"], ["InstallShadowDefaults","shadows.tres"], ["InstallSurfaceTerrainDefaults","surface.tres"], ["InstallAtmosphereDefaults","atmosphere.tres"]]:
		target.call(str(item[0]), fixture.path_join(str(item[1])))

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = diagnostics.get("streaming", {})
		var native: Dictionary = diagnostics.get("native", {})
		var preparation: Dictionary = native.get("preparation", {})
		var placed: bool = absf(float(diagnostics.get("player_around", NAN)) - FIXTURE_AROUND) < 1.0 and absf(float(diagnostics.get("player_across", NAN)) - FIXTURE_ACROSS) < 1.0
		var grass_ready := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0
		var trees_ready := int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0
		var global_ready: bool = global_terrain != null and not bool(global_terrain.get("GlobalSummaryBuildPending")) and int(global_terrain.get("PendingInitialSegmentCount")) == 0 and int(global_terrain.get("PendingSegmentRebuildCount")) == 0 and not bool(global_terrain.get("GlobalSegmentSamplingWorkerPending"))
		var ready := bool(diagnostics.get("ready", false)) and bool(diagnostics.get("visual_coverage", false)) and int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false)) and bool(preparation.get("ready", false)) and grass_ready and trees_ready and global_ready
		stable = stable + 1 if placed and ready else 0
		if stable >= 12: return true
	fail("Terrain3D workload did not settle at fixed pose")
	return false

func select_visual_phases() -> Dictionary:
	var noon := 0.0
	var dusk := 0.0
	var night := 0.0
	var highest := -INF
	var lowest := INF
	var horizon_distance := INF
	for step in 48:
		var fraction := float(step) / 48.0
		clock.call("SetCycleFraction", fraction)
		var elevation := float(clock.get("SunElevationDegrees"))
		if elevation > highest: highest = elevation; noon = fraction
		if elevation < lowest: lowest = elevation; night = fraction
		if abs(elevation) < horizon_distance: horizon_distance = abs(elevation); dusk = fraction
	return {"noon":noon, "dusk":dusk, "night":night}

func frames(count: int) -> void:
	for ignored in count: await process_frame

func fail(message: String) -> void:
	push_error("STORM_LIGHT_CAPTURE=FAIL " + message)
	quit(1)

