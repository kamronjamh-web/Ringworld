extends SceneTree
# Real-world rain look capture (S1 follow-up). Boots the certified gameplay scene (meadow with trees at the storm_light fixture), puts a storm at the camera and
# saves rain tier x view shots, a roof (POI overhang stand-in) shot, day and dusk, plus a perf A-B-A and a movie mode. Windowed GPU only.
#   --out=<dir> --mode=sheet|perf|movie  [--around= --across=]
# Run through the scratchpad run_gpu.ps1 (GPU lock). Movie: add engine args --write-movie <dir>/frame.png --fixed-fps 30.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
var FIXTURE_AROUND := 8150667.0
var FIXTURE_ACROSS := 119474.0
const SETTLE_FRAMES := 24
var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var drawer: Node
var clock: Node
var camera: Camera3D
var grass: Node
var trees: Node
var global_terrain: Node
var bridge: Node
var local: Node
var driver: RefCounted
var out_dir := ""
var mode := "sheet"
var quick := false
var ground := 0.0
var axes: Dictionary
var report := {}
var yaw_across := 0.0
var budget_override := 0

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func fail(message: String) -> void:
	push_error("RAIN_REAL=FAIL " + message)
	quit(1)

func frames(count: int) -> void:
	for ignored in count: await process_frame

func settle_seconds(s: float) -> void:
	var end := Time.get_ticks_msec() + int(s * 1000.0)
	while Time.get_ticks_msec() < end: await process_frame

func sun_at(elevation_target: float) -> void:
	var best := 0.0
	var best_err := INF
	for step in 384:
		var f := float(step) / 384.0
		clock.call("SetCycleFraction", f)
		var e := float(clock.get("SunElevationDegrees"))
		if absf(e - elevation_target) < best_err: best_err = absf(e - elevation_target); best = f
	clock.call("SetCycleFraction", best)

func run() -> void:
	create_timer(1700.0).timeout.connect(func(): fail("watchdog"))
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	out_dir = "C:/Projects/Ringworld/.godot/captures/storm_s1/real"
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--out="): out_dir = argument.get_slice("=", 1)
		if argument.begins_with("--mode="): mode = argument.get_slice("=", 1)
		if argument == "--quick": quick = true
		if argument.begins_with("--budget="): budget_override = int(argument.get_slice("=", 1))
		if argument.begins_with("--around="): FIXTURE_AROUND = float(argument.get_slice("=", 1))
		if argument.begins_with("--across="): FIXTURE_ACROSS = float(argument.get_slice("=", 1))
	DirAccess.make_dir_recursive_absolute(out_dir)
	print("RAIN_REAL device=", RenderingServer.get_video_adapter_name())
	fixture = "res://.godot/atmosphere_visual/rain_real_%s" % Time.get_ticks_usec()
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
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	bridge = game.get_node_or_null("SunshineCloudBridge")
	local = game.get_node_or_null("LocalWeatherEffects")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	driver = DRIVER.new()
	driver.call("SetLensDropletsForFixture", 0.0)
	if explorer == null or surface == null or clock == null or camera == null or bridge == null or local == null: return fail("missing nodes")
	drawer.call("SetAtmosphereEnabled", true)
	drawer.call("SetAtmosphereHazeModel", 1)
	ground = float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	for name in ["LiveTuningDrawer", "TerrainMinimap", "PresentationHud", "LodDebugHud", "CelestialDebugHud", "LodPauseMenu"]:
		var overlay := game.get_node_or_null(name) as CanvasLayer
		if overlay != null: overlay.visible = false
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	clock.set("Paused", true)
	sun_at(40.0)
	await settle_seconds(3.0)
	axes = driver.call("FrameAxes", game)
	# Yaw that faces the across axis (roofs are placed along it).
	explorer.call("SetPresentationView", 0.0, 0.0)
	await frames(3)
	var f0: Vector3 = -camera.global_transform.basis.z
	explorer.call("SetPresentationView", 90.0, 0.0)
	await frames(3)
	var f90: Vector3 = -camera.global_transform.basis.z
	var across: Vector3 = axes.across
	yaw_across = rad_to_deg(atan2(across.dot(f90), across.dot(f0)))
	print("RAIN_REAL yaw_across=", yaw_across)
	if budget_override > 0: local.set("MaximumStreakCount", budget_override)
	local.set("PrecipitationStart", 0.18)
	bridge.set("SpawnDistanceMeters", 0.0)
	bridge.call("SpawnStormFrontAhead")
	await settle_seconds(14.0)
	print("RAIN_REAL intensity=", local.get("PrecipitationIntensity"))
	match mode:
		"sheet": await sheet()
		"perf": await perf()
		"perfarr": await perf_arrival()
		"cpu": await cpu_cost()
		"snow": await snow()
		"movie": await movie()
	report["diag"] = local.call("GetRainStreakDiagnostics")
	var file := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t")); file.close()
	print("RAIN_REAL=PASS")
	quit(0)

func shot(label: String, yaw: float, pitch: float) -> void:
	explorer.call("SetPresentationView", yaw_across + yaw, pitch)
	await frames(SETTLE_FRAMES)
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	image.save_png(out_dir.path_join(label + ".png"))
	print("RAIN_REAL shot ", label)

func set_rain(intensity: float, wind := Vector2(9.0, 2.0)) -> void:
	driver.call("SetIntensity", local, intensity, false, wind)
	await settle_seconds(2.5)

func add_roof(half: float, around_off: float, across_off: float, height: float) -> MeshInstance3D:
	var up: Vector3 = axes.up
	var instance := MeshInstance3D.new(); var box := BoxMesh.new(); box.size = Vector3(half * 2.0, 0.5, half * 2.0); instance.mesh = box
	var material := StandardMaterial3D.new(); material.albedo_color = Color(.22, .22, .25); material.roughness = 0.9; instance.material_override = material
	root.add_child(instance)
	var centre := Vector3(axes.origin) + Vector3(axes.around) * around_off + Vector3(axes.across) * across_off + up * height
	instance.global_transform = Transform3D(Basis(Vector3(axes.around), up, Vector3(axes.across)).orthonormalized(), centre)
	return instance

func wait_map() -> void:
	var occlusion := game.get_node("PrecipitationOcclusion")
	var previous := int(occlusion.get("RenderCount"))
	occlusion.call("RequestRender")
	for i in 240:
		await process_frame
		if int(occlusion.get("RenderCount")) > previous and bool(occlusion.get("Ready")): return

func sheet() -> void:
	var tiers := {"light": 0.2, "steady": 0.5, "heavy": 0.82, "violent": 1.0}
	if quick: tiers = {"light": 0.2, "heavy": 0.82}
	for phase in ["day", "dusk"]:
		sun_at(40.0 if phase == "day" else 4.0)
		await settle_seconds(3.0)
		for tier in tiers.keys():
			if phase == "dusk" and tier == "light": continue
			await set_rain(tiers[tier])
			await shot("%s_%s_horizon" % [phase, tier], 0.0, 3.0)
			await shot("%s_%s_fwd" % [phase, tier], 90.0, -12.0)
			await shot("%s_%s_ground" % [phase, tier], 180.0, -38.0)
			if tier == "heavy" or tier == "violent": await shot("%s_%s_up" % [phase, tier], 0.0, 62.0)
		await set_rain(0.82)
		var roof := add_roof(10.0, 0.0, 16.0, 2.4)
		await wait_map()
		await shot("%s_heavy_roof" % phase, 0.0, 6.0)
		await shot("%s_heavy_roof_side" % phase, 40.0, 4.0)
		roof.queue_free()
		await wait_map()

# Real GPU.RainStreaks timestamps and the CPU cost of the local rain field in the production world.
func perf() -> void:
	driver.call("ConfigureRainGpuProfilerForFixture", true)
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null: monitor.set_process(false)   # it drains the GPU timestamp queue itself
	var viewport_rid := root.get_viewport().get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(viewport_rid, true)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var out := {}
	for budget: int in [6000, 12000, 20000]:
		local.set("MaximumStreakCount", budget)
		await set_rain(1.0, Vector2(14.0, 0.0))
		explorer.call("SetPresentationView", yaw_across, 6.0)
		var series: Array = []
		for phase in ["A", "B", "A2"]:
			driver.call("SetStreakSettingForFixture", "StreakDensity", 0.0 if phase == "B" else 1.0)
			await frames(30)
			driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks")
			var samples: Array = []
			var vp_gpu: Array = []
			for i in 300:
				await process_frame
				vp_gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport_rid))
				driver.call("RequestRainGpuReadbackForFixture")
				if i % 8 == 7: samples.append_array(driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks"))
			await frames(6)
			samples.append_array(driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks"))
			samples.sort()
			var median := 0.0 if samples.is_empty() else float(samples[samples.size() / 2])
			var p95 := 0.0 if samples.is_empty() else float(samples[mini(samples.size() - 1, int(samples.size() * 0.95))])
			var d: Dictionary = local.call("GetRainStreakDiagnostics")
			print("RAIN_REAL_PERF budget=%d phase=%s samples=%d median_ms=%.4f p95_ms=%.4f grid_total_ms=%.3f update_total_ms=%.3f frames=%d" % [budget, phase, samples.size(), median, p95, float(d.get("grid_total_ms", -1)), float(d.get("update_total_ms", -1)), int(d.get("grid_frames", -1))])
			vp_gpu.sort()
			print("RAIN_REAL_PERF viewport_gpu_median_ms=%.4f budget=%d phase=%s" % [float(vp_gpu[vp_gpu.size() / 2]), budget, phase])
			series.append(median)
		out[str(budget)] = series
	driver.call("SetStreakSettingForFixture", "StreakDensity", 1.0)
	report["perf"] = out

# Movie: heavy storm in the real world, 20 s. Engine args: --write-movie <png> --fixed-fps 30. The player walks toward a roof and under it, looks up, then walks out.
func movie() -> void:
	var fps := 30.0
	await set_rain(0.9, Vector2(12.0, 3.0))
	var roof := add_roof(10.0, 0.0, 20.0, 2.4)
	await wait_map()
	var frame_count := int(20.0 * fps)
	for frame in frame_count:
		var t := frame / fps
		var walk := 0.0
		if t < 2.0: walk = 0.0
		elif t < 9.0: walk = smoothstep(2.0, 9.0, t) * 20.0
		elif t < 12.0: walk = 20.0
		else: walk = 20.0 - smoothstep(12.0, 18.0, t) * 20.0
		var yaw := 0.0
		var pitch := 6.0
		if t < 2.0: yaw = lerpf(120.0, 0.0, smoothstep(0.0, 2.0, t))
		if t >= 9.0 and t < 12.0: pitch = 6.0 + 50.0 * sin((t - 9.0) / 3.0 * PI)
		if t >= 12.0: yaw = lerpf(0.0, 180.0, smoothstep(12.0, 15.0, t))
		var across_m := FIXTURE_ACROSS + walk
		var h := float(surface.call("SampleHeight", FIXTURE_AROUND, across_m)) + 1.1
		explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, h, across_m)
		explorer.call("SetPresentationView", yaw_across + yaw, pitch)
		await process_frame
	roof.queue_free()

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

# CPU cost of the local rain field per frame, with the observer walking so the lattice grid re-sweeps every 9 m (about 2 sweeps a second at a run): macro mirror vs per-cell QueryLocalWeather.
func cpu_cost() -> void:
	await set_rain(1.0, Vector2(14.0, 0.0))
	for mirror in [true, false, true, false]:
		driver.call("SetStreakGridMirrorForFixture", local, mirror)
		await frames(40)
		var before: Dictionary = local.call("GetRainStreakDiagnostics")
		var across_m := FIXTURE_ACROSS
		for i in 600:
			if i % 15 == 0:
				across_m += 9.0
				var h := float(surface.call("SampleHeight", FIXTURE_AROUND, across_m)) + 1.1
				explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, h, across_m)
			await process_frame
		var after: Dictionary = local.call("GetRainStreakDiagnostics")
		var frames_n := float(after.grid_frames) - float(before.grid_frames)
		print("RAIN_REAL_CPU mirror=%s update_ms_per_frame=%.4f grid_ms_per_frame=%.4f frames=%d" % [mirror, (float(after.update_total_ms) - float(before.update_total_ms)) / frames_n, (float(after.grid_total_ms) - float(before.grid_total_ms)) / frames_n, int(frames_n)])
	driver.call("SetStreakGridMirrorForFixture", local, true)
	mirror_parity()

func mirror_parity() -> void:
	for spacing in [60.0, 1000.0, 5000.0, 20000.0]:
		print("RAIN_REAL_MIRROR spacing=%d " % int(spacing), local.call("CompareStreakPotentialMirror", spacing))

# Snow shelter (S1 WO7): flakes of the camera-local snow pool are hidden under a roof. Pixels that differ between snow and dry frames, open sky vs under a big roof.
func snow() -> void:
	explorer.call("SetPresentationView", yaw_across, 4.0)
	var open_count := await snow_pixels("open")
	var roof := add_roof(60.0, 0.0, 0.0, 3.0)
	await wait_map()
	await wait_map()
	var roof_count := await snow_pixels("roof")
	roof.queue_free()
	print("RAIN_REAL_SNOW open=%d roof=%d" % [open_count, roof_count])
	report["snow"] = {"open": open_count, "roof": roof_count}
	driver.call("SetIntensity", local, 0.0, false, Vector2.ZERO)

func snow_pixels(tag: String) -> int:
	driver.call("SetIntensity", local, 0.0, false, Vector2.ZERO)
	await settle_seconds(3.0)
	await frames(10)
	await RenderingServer.frame_post_draw
	var dry := root.get_viewport().get_texture().get_image()
	driver.call("SetIntensity", local, 1.0, true, Vector2(3.0, 1.0))
	await settle_seconds(5.0)
	await frames(10)
	await RenderingServer.frame_post_draw
	var wet := root.get_viewport().get_texture().get_image()
	dry.save_png(out_dir.path_join("snow_%s_dry.png" % tag)); wet.save_png(out_dir.path_join("snow_%s_snow.png" % tag))
	var count := 0
	for y in range(0, dry.get_height(), 2):
		for x in range(0, dry.get_width(), 2):
			var a := dry.get_pixel(x, y); var b := wet.get_pixel(x, y)
			if absf((a.r + a.g + a.b) - (b.r + b.g + b.b)) > 0.12: count += 1
	return count

# S3 arrival cost: the same GPU.RainStreaks measurement with the arrival hooks forced (none, fat drops, heavy core, core with hail), heavy rain, A-B-A with the streak density at 0 as B.
func perf_arrival() -> void:
	driver.call("ConfigureRainGpuProfilerForFixture", true)
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null: monitor.set_process(false)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var cases := {"none": null, "core": [0.0, 1.0, 0.0, 1.0, 0.0, 0.0], "fat_core": [1.0, 1.0, 0.0, 1.0, 0.0, 0.0], "hail": [1.0, 1.0, 0.0, 1.0, 0.8, 0.6], "none2": null}
	var out := {}
	for budget: int in [6000, 12000]:
		local.set("MaximumStreakCount", budget)
		await set_rain(1.0, Vector2(14.0, 0.0))
		explorer.call("SetPresentationView", yaw_across, 6.0)
		for label in cases.keys():
			var c = cases[label]
			if c == null: driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, true)
			else: driver.call("SetStreakArrivalForFixture", local, c[0], c[1], c[2], c[3], c[4], c[5], false)
			await frames(30)
			driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks")
			var samples: Array = []
			for i in 300:
				await process_frame
				driver.call("RequestRainGpuReadbackForFixture")
				if i % 8 == 7: samples.append_array(driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks"))
			await frames(6)
			samples.append_array(driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks"))
			samples.sort()
			var median := 0.0 if samples.is_empty() else float(samples[samples.size() / 2])
			var p95 := 0.0 if samples.is_empty() else float(samples[mini(samples.size() - 1, int(samples.size() * 0.95))])
			print("RAIN_REAL_PERFARR budget=%d case=%s samples=%d median_ms=%.4f p95_ms=%.4f" % [budget, label, samples.size(), median, p95])
			out["%d_%s" % [budget, label]] = median
	driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, true)
	report["perfarr"] = out
