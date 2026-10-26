extends SceneTree
# Wet world acceptance capture (docs/storm-revamp-s2-wet-world-design.md section 6). Boots the certified gameplay scene, finds a natural MajorFront (same scan as the storm arrival
# captures), pins the day and freezes the cloud clock at chosen moments of its lifecycle, so the wet map (a stateless function of the rain history) can be reviewed at every stage.
#   --mode=timeline (default)   clear, approach, gust front, fat-drop spots, core (rain 0 / 30 / 90 s), trailing, clearing, then the drying sequence +30 / +120 / +300 / +600 / +1500 s after the
#                               last rain, plus a concrete overhang for the shelter line during the storm and in the aftermath
#   --mode=movie                rain on a puddle, walking out from under the overhang (lens burst), looking up in rain, looking down at the puddle: write it with --write-movie
#   --mode=night                lightning glints on wet ground at night (storm core, sun down)
#   --out=<dir> --elev=<sun elevation> --scan=<scan start> --around= --across=   --no-lens (keep the lens droplets off)
# Windowed GPU only; run it through tools/wet_world_capture.ps1. Prints WET_WORLD_CAPTURE=PASS.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const HELPER := preload("res://tools/WetWorldCaptureHelper.cs")
const SETTLE_FRAMES := 18
var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var drawer: Node
var clock: Node
var camera: Camera3D
var weather: Node
var effects: Node
var wet: Node
var lens: Node
var overlay: Node
var bridge: Node
var helper: RefCounted
var out_dir := ""
var mode := "timeline"
var around := 8150667.0
var across := 119474.0
var report := {"status": "RUNNING", "captures": []}
var storm := {}
var ground := 0.0
var elevation_target := 40.0
var day_fraction := 0.5

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func fail(message: String) -> void:
	push_error("WET_WORLD_CAPTURE=FAIL " + message)
	quit(1)

func frames(count: int) -> void:
	for ignored in count: await process_frame

func settle_seconds(s: float) -> void:
	var end := Time.get_ticks_msec() + int(s * 1000.0)
	while Time.get_ticks_msec() < end: await process_frame

func run() -> void:
	create_timer(2700.0).timeout.connect(func(): fail("watchdog"))
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	out_dir = "C:/Projects/Ringworld/.godot/captures/wet_world/run"
	var scan_start := 100000.0
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--out="): out_dir = argument.get_slice("=", 1)
		if argument.begins_with("--mode="): mode = argument.get_slice("=", 1)
		if argument.begins_with("--elev="): elevation_target = float(argument.get_slice("=", 1))
		if argument.begins_with("--scan="): scan_start = float(argument.get_slice("=", 1))
	if mode == "night" and elevation_target > 0.0: elevation_target = -25.0
	DirAccess.make_dir_recursive_absolute(out_dir)
	fixture = "res://.godot/atmosphere_visual/wet_world_%s" % Time.get_ticks_usec()
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
	weather = game.get_node_or_null("RingVolumetricClouds")
	effects = game.get_node_or_null("LocalWeatherEffects")
	wet = game.get_node_or_null("WetWorld")
	lens = game.get_node_or_null("LensDroplets")
	overlay = game.get_node_or_null("SmoothConstructionOverlay")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	helper = HELPER.new()
	bridge = game.get_node_or_null("SunshineCloudBridge")
	if explorer == null or surface == null or drawer == null or clock == null or camera == null or weather == null or wet == null or bridge == null:
		return fail("missing nodes (explorer=%s surface=%s drawer=%s clock=%s camera=%s weather=%s wet=%s bridge=%s)" % [explorer != null, surface != null, drawer != null, clock != null, camera != null, weather != null, wet != null, bridge != null])
	drawer.call("SetAtmosphereEnabled", true)
	drawer.call("SetAtmosphereHazeModel", 1)
	weather.set("CloudSimulationPaused", true)
	if mode == "scout":
		await scout_mode()
		return
	storm = helper.call("FindStorm", weather, 1800.0, scan_start) as Dictionary
	report["storm"] = storm
	if not bool(storm["ok"]): return fail("no mature MajorFront found")
	print("STORM ", JSON.stringify(storm))
	await place(float(storm["around"]), float(storm["across"]))
	var best := 0.0
	var best_err := INF
	for step in 192:
		var f := float(step) / 192.0
		clock.call("SetCycleFraction", f)
		var e := float(clock.get("SunElevationDegrees"))
		if absf(e - elevation_target) < best_err: best_err = absf(e - elevation_target); best = f
	day_fraction = best
	clock.set("Paused", true)
	clock.call("SetCycleFraction", best)
	report["sun_elevation"] = float(clock.get("SunElevationDegrees"))
	await settle_seconds(3.0)
	if mode == "timeline": await timeline_mode()
	elif mode == "movie": await movie_mode()
	elif mode == "night": await night_mode()
	elif mode == "perf": await perf_mode()
	elif mode == "scout": await scout_mode()
	report.status = "PASS"
	var f2 := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	f2.store_string(JSON.stringify(report, "\t")); f2.close()
	print("WET_WORLD_CAPTURE=PASS")
	quit(0)

func place(a: float, z: float) -> void:
	around = a; across = z
	ground = float(surface.call("SampleHeight", a, z))
	explorer.process_mode = Node.PROCESS_MODE_INHERIT
	explorer.call("SetCanonicalSpawnMeters", a, ground + 1.1, z)
	explorer.call("SetPresentationView", 0.0, 0.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED

# Sets the cloud clock. While the clock is paused and the camera still, the storm arrival observer refreshes its stale history lags only when the clock lattice moves, so the clock is nudged
# 0.12 s per frame for ten frames after the jump (1.2 s of storm time, invisible) to let the whole rain history rebuild.
func jump_time(t: float) -> void:
	weather.set("CloudSimulationTimeSeconds", t)
	for i in 10:
		await process_frame
		weather.set("CloudSimulationTimeSeconds", t + 0.12 * float(i + 1))

func wet_state() -> Dictionary:
	var arrival := weather.call("GetStormArrivalDiagnostic") as Dictionary
	var natural := weather.call("GetNaturalWeatherDiagnosticAtTime", around, across, float(weather.get("CloudSimulationTimeSeconds"))) as Dictionary
	return {"natural_precip": natural.get("precipitation"), "effects_enabled": effects.get("Enabled") if effects != null else null, "precip_phase": str(effects.get("PrecipitationPhase")) if effects != null else null,"phase": arrival.get("phase"), "fat_drop": arrival.get("fat_drop"), "recent_rain": arrival.get("recent_rain"), "time_since_rain": arrival.get("time_since_rain_s"),
		"wet_valid": wet.get("Valid"), "camera_rain": wet.get("CameraRainNow"), "camera_exposure": wet.get("CameraExposure"), "dispatches": wet.get("DispatchCount"),
		"camera_wetness": str(wet.get("CameraWetnessVector")), "lens": lens.get("Wetness") if lens != null else null, "precip": effects.get("PrecipitationIntensity") if effects != null else null}

func shot(label: String, yaw: float, pitch: float, debug_mode := 0) -> Dictionary:
	explorer.call("SetPresentationView", yaw, pitch)
	RenderingServer.global_shader_parameter_set("ring_wet_debug_mode", float(debug_mode))
	await frames(SETTLE_FRAMES)
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	image.save_png(out_dir.path_join(label + ".png"))
	RenderingServer.global_shader_parameter_set("ring_wet_debug_mode", 0.0)
	var d := {"label": label, "yaw": yaw, "pitch": pitch, "cloud_time": weather.get("CloudSimulationTimeSeconds"), "sun_elev": clock.get("SunElevationDegrees"), "state": wet_state(), "luma": image_luma(image)}
	print("SHOT ", JSON.stringify(d))
	report.captures.append(d)
	return d

func at(label: String, t: float, settle := 7.0, views := [[0.0, -22.0], [0.0, -3.0]]) -> void:
	await jump_time(t)
	await settle_seconds(settle)
	var index := 0
	for view in views:
		await shot("%s_%s" % [label, ["down", "ahead", "view3", "view4"][index]], float(view[0]), float(view[1]))
		index += 1

func timeline_mode() -> void:
	var first_gust := float(storm["first_gust"]); var first_rain := float(storm["first_rain"]); var last_rain := float(storm["last_rain"])
	lens.set("LensWetnessOverride", 0.0)
	await at("t00_clear", maxf(0.0, first_gust - 1300.0))
	await at("t01_approach", maxf(0.0, first_gust - 150.0))
	await at("t02_gust", first_gust)
	await at("t03_fatdrops", first_rain - 25.0, 7.0, [[0.0, -35.0], [0.0, -12.0]])
	await at("t04_rain0", first_rain + 2.0, 7.0, [[0.0, -35.0], [0.0, -5.0]])
	await at("t05_rain30", first_rain + 30.0, 7.0, [[0.0, -35.0], [0.0, -5.0]])
	await at("t06_rain90", first_rain + 90.0, 7.0, [[0.0, -35.0], [0.0, -5.0]])
	await at("t07_core", 0.5 * (first_rain + last_rain), 7.0, [[0.0, -35.0], [0.0, -5.0], [0.0, -70.0]])
	await shot("t07_core_dbg_film", 0.0, -30.0, 1)
	await shot("t07_core_dbg_soak", 0.0, -30.0, 2)
	await shot("t07_core_dbg_standing", 0.0, -30.0, 3)
	await shot("t07_core_dbg_rain", 0.0, -30.0, 4)
	await at("t08_trailing", last_rain - 25.0)
	# Overhang for the shelter line during the storm.
	await build_overhang()
	await jump_time(0.5 * (first_rain + last_rain))
	await settle_seconds(8.0)
	await shot("s0_core_under_looking_out", 0.0, -8.0)
	await shot("s0_core_shelter_line", 0.0, -24.0)
	await shot("s0_core_shelter_edge", 28.0, -20.0)
	await shot("s0_core_dbg_rain", 0.0, -28.0, 4)
	await at("t09_clearing", last_rain + 10.0)
	for offset in [30.0, 120.0, 300.0, 600.0, 1500.0]:
		var tag := "d%04d" % int(offset)
		await at(tag + "_after", last_rain + offset, 8.0, [[0.0, -20.0], [0.0, -4.0], [0.0, -40.0]])
		await shot(tag + "_shelter_line", 0.0, -24.0)
		await shot(tag + "_shelter_edge", 28.0, -20.0)
		await shot(tag + "_dbg_film", 0.0, -30.0, 1)
		await shot(tag + "_dbg_soak", 0.0, -30.0, 2)
		await shot(tag + "_dbg_standing", 0.0, -30.0, 3)

func build_overhang() -> void:
	# An 8 x 8 m concrete-grey slab on four pillars, its underside 4.5 m above the ground and its centre 12 m ahead (along the ring) of the camera. Plain box meshes: the precipitation
	# occlusion map renders every opaque mesh, so they shelter exactly like a building roof.
	var centre: Vector3 = bridge.call("GetStormProbeWorldPosition", around + 12.0, ground + 4.5, across)
	var p_around: Vector3 = bridge.call("GetStormProbeWorldPosition", around + 13.0, ground + 4.5, across)
	var p_up: Vector3 = bridge.call("GetStormProbeWorldPosition", around + 12.0, ground + 5.5, across)
	var p_across: Vector3 = bridge.call("GetStormProbeWorldPosition", around + 12.0, ground + 4.5, across + 1.0)
	var axis_x := (p_around - centre).normalized()
	var axis_y := (p_up - centre).normalized()
	var axis_z := axis_x.cross(axis_y).normalized()
	var basis := Basis(axis_x, axis_y, axis_z)
	var material := StandardMaterial3D.new()
	material.albedo_color = Color(0.46, 0.46, 0.48)
	material.roughness = 0.9
	var slab := MeshInstance3D.new()
	var slab_mesh := BoxMesh.new()
	slab_mesh.size = Vector3(8.0, 0.5, 8.0)
	slab.mesh = slab_mesh
	slab.material_override = material
	slab.name = "WetWorldOverhangSlab"
	game.add_child(slab)
	slab.global_transform = Transform3D(basis, centre + axis_y * 0.25)
	var index := 0
	for corner in [Vector2(-3.75, -3.75), Vector2(-3.75, 3.75), Vector2(3.75, -3.75), Vector2(3.75, 3.75)]:
		var pillar := MeshInstance3D.new()
		var pillar_mesh := BoxMesh.new()
		pillar_mesh.size = Vector3(0.5, 4.5, 0.5)
		pillar.mesh = pillar_mesh
		pillar.material_override = material
		pillar.name = "WetWorldOverhangPillar%d" % index
		game.add_child(pillar)
		pillar.global_transform = Transform3D(basis, centre + axis_x * corner.x + axis_z * corner.y - axis_y * 2.25)
		index += 1
	report["overhang"] = {"centre": str(centre), "up": str(axis_y)}
	await settle_seconds(10.0)

func movie_mode() -> void:
	clock.set("Paused", true)
	clock.call("SetCycleFraction", day_fraction)
	await settle_seconds(4.0)
	report["movie_sun_elevation"] = float(clock.get("SunElevationDegrees"))
	var first_rain := float(storm["first_rain"]); var last_rain := float(storm["last_rain"])
	await build_overhang()
	var mid := 0.5 * (first_rain + last_rain)
	await jump_time(mid)
	weather.set("CloudSimulationPaused", false)
	await settle_seconds(8.0)
	# Route: stand in the open next to the puddles looking down (ripples), step under the slab, then walk out into the rain and look up.
	var start_ground := ground + 1.1
	var under_a := around + 12.0
	var waypoints := [
		{"a": around - 2.0, "pitch": -38.0, "yaw": 0.0, "frames": 150},
		{"a": under_a, "pitch": -5.0, "yaw": 0.0, "frames": 120},
		{"a": under_a, "pitch": -2.0, "yaw": 0.0, "frames": 70},
		{"a": under_a + 11.0, "pitch": 5.0, "yaw": 0.0, "frames": 90},
		{"a": under_a + 11.0, "pitch": 60.0, "yaw": 0.0, "frames": 150},
		{"a": under_a + 11.0, "pitch": -40.0, "yaw": 0.0, "frames": 120},
	]
	report["route_first_frame"] = Engine.get_frames_drawn()
	explorer.process_mode = Node.PROCESS_MODE_INHERIT
	var current_a := around - 2.0
	var current_pitch := -38.0
	for waypoint in waypoints:
		var from_a := current_a; var from_pitch := current_pitch
		var count := int(waypoint["frames"])
		for i in count:
			var t := smoothstep(0.0, 1.0, float(i + 1) / float(count))
			current_a = lerpf(from_a, float(waypoint["a"]), t)
			current_pitch = lerpf(from_pitch, float(waypoint["pitch"]), t)
			explorer.call("SetCanonicalSpawnMeters", current_a, ground + 1.1, across)
			explorer.call("SetPresentationView", float(waypoint["yaw"]), current_pitch)
			await process_frame
	report["movie_frames"] = 150 + 120 + 70 + 90 + 150 + 120
	report["lens_final"] = lens.get("Wetness")

# A-B-C-B-A cost of the wet world at the storm core on the root viewport: A = WetWorld disabled (valid 0, no dispatch, every receiver on its dry branch), B = wet world on with the lens
# hidden (map compute + receiver fetches + puddles + ripples + engine SSR on the new smooth pixels), C = B plus the lens droplets at L = 0.8. GPU/CPU render time of the root viewport,
# median and p95 over 400 frames per round after 120 settle frames.
func perf_mode() -> void:
	var first_rain := float(storm["first_rain"]); var last_rain := float(storm["last_rain"])
	await jump_time(0.5 * (first_rain + last_rain))
	await settle_seconds(8.0)
	var viewport_rid := root.get_viewport().get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(viewport_rid, true)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	explorer.call("SetPresentationView", 0.0, -12.0)
	var perf := {"window_size": str(root.get_viewport().get_visible_rect().size), "rounds": []}
	var rounds: Array = []
	var environment: Environment = null
	var world_environment := game.find_child("WorldEnvironment", true, false) as WorldEnvironment
	if world_environment != null: environment = world_environment.environment
	var ssr_was := environment.ssr_enabled if environment != null else false
	perf["ssr_enabled"] = ssr_was
	for label in ["A", "B", "C", "D", "F", "G", "B", "A"]:
		print("PERF_ROUND ", label, " fps=", Engine.get_frames_per_second())
		# F / G: the engine SSR is off (F = wet world disabled, G = wet world on), isolating the shader cost from the extra SSR traces on glossy ground.
		if environment != null: environment.ssr_enabled = ssr_was and label != "F" and label != "G"
		wet.set("Enabled", label != "A" and label != "F")
		lens.set("LensWetnessOverride", 0.8 if label == "C" else 0.0)
		drawer.call("SetWetWorldTuningValue", "Puddles", 0.0 if label == "D" else 1.0)
		await frames(120)
		var gpu := []
		var cpu := []
		var wall := []
		var last := Time.get_ticks_usec()
		for i in 300:
			await process_frame
			var now := Time.get_ticks_usec()
			wall.append((now - last) / 1000.0)
			last = now
			gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport_rid))
			cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(viewport_rid))
		rounds.append({"arm": label, "gpu_median": median_of(gpu), "gpu_p95": percentile_of(gpu, 0.95), "cpu_median": median_of(cpu), "wall_median": median_of(wall), "valid": wet.get("Valid"), "lens_drawing": lens.get("Drawing")})
	wet.set("Enabled", true)
	lens.set("LensWetnessOverride", 0.0)
	if environment != null: environment.ssr_enabled = ssr_was
	drawer.call("SetWetWorldTuningValue", "Puddles", 1.0)
	perf["rounds"] = rounds
	for arm in ["A", "B", "C", "D", "F", "G"]:
		var medians := []
		for r in rounds:
			if r.arm == arm: medians.append(r.gpu_median)
		perf[arm + "_gpu_median_ms"] = median_of(medians)
	perf["B_minus_A_gpu_ms"] = float(perf["B_gpu_median_ms"]) - float(perf["A_gpu_median_ms"])
	perf["C_minus_B_gpu_ms"] = float(perf["C_gpu_median_ms"]) - float(perf["B_gpu_median_ms"])
	perf["G_minus_F_gpu_ms_ssr_off"] = float(perf["G_gpu_median_ms"]) - float(perf["F_gpu_median_ms"])
	perf["B_minus_D_gpu_ms"] = float(perf["B_gpu_median_ms"]) - float(perf["D_gpu_median_ms"])
	perf["D_minus_A_gpu_ms"] = float(perf["D_gpu_median_ms"]) - float(perf["A_gpu_median_ms"])
	perf["A_repeat_spread_gpu_ms"] = absf(float(rounds[0].gpu_median) - float(rounds[7].gpu_median))
	perf["B_repeat_spread_gpu_ms"] = absf(float(rounds[1].gpu_median) - float(rounds[6].gpu_median))
	report["perf"] = perf
	print("WET_WORLD_PERF ", JSON.stringify(perf))

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

# Looks at the natural storms the scan finds from several start points (one clear-sky shot each) so a run can pick an open-ground site: --scan=<start> then reproduces that storm.
func scout_mode() -> void:
	weather.set("CloudSimulationPaused", true)
	for start in [100000.0, 1000000.0, 2000000.0, 3000000.0, 4000000.0, 5000000.0, 6000000.0, 7000000.0, 8000000.0, 9000000.0, 10000000.0, 12000000.0, 14000000.0, 16000000.0]:
		var found := helper.call("FindStorm", weather, 1800.0, start) as Dictionary
		if not bool(found["ok"]):
			print("SCOUT ", start, " none")
			continue
		await place(float(found["around"]), float(found["across"]))
		var noon := 0.0
		var best := INF
		for step in 96:
			clock.call("SetCycleFraction", float(step) / 96.0)
			var e := absf(float(clock.get("SunElevationDegrees")) - 45.0)
			if e < best: best = e; noon = float(step) / 96.0
		clock.set("Paused", true)
		clock.call("SetCycleFraction", noon)
		await jump_time(maxf(0.0, float(found["first_gust"]) - 1300.0))
		await settle_seconds(5.0)
		await shot("scout_%08d" % int(start), 0.0, -12.0)
		print("SCOUT ", start, " ", JSON.stringify(found))
	report.status = "PASS"
	print("WET_WORLD_CAPTURE=PASS")
	quit(0)

func night_mode() -> void:
	var first_rain := float(storm["first_rain"]); var last_rain := float(storm["last_rain"])
	var mid := 0.5 * (first_rain + last_rain)
	lens.set("LensWetnessOverride", 0.0)
	await jump_time(mid)
	await settle_seconds(8.0)
	await shot("n00_core_down", 0.0, -22.0)
	# Lightning glints: the local storm flash light lights the wet ground; capture a few strikes.
	for i in 6:
		effects.call("TriggerLightningPreview")
		await frames(4 + i)
		await shot("n01_strike_%d" % i, 0.0, -22.0)
		await settle_seconds(1.5)

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

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: fail("missing terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)): fail("runtime generation preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate()
	var target := next.get_node_or_null("LiveTuningDrawer")
	if target != null:
		for item in [["InstallGrassDefaults","grass.tres"], ["InstallVegetationDefaults","vegetation.tres"], ["InstallWaterDefaults","water.tres"], ["InstallAntiAliasingDefaults","anti_aliasing.tres"], ["InstallShadowDefaults","shadows.tres"], ["InstallSurfaceTerrainDefaults","surface.tres"], ["InstallAtmosphereDefaults","atmosphere.tres"]]:
			target.call(str(item[0]), fixture.path_join(str(item[1])))
		target.call("InstallWetWorldDefaults", fixture.path_join("wet_world.tres"), fixture.path_join("surface.tres"))
	var wet_node := next.get_node_or_null("WetWorld")
	if wet_node != null: wet_node.set("PreferencesPath", fixture.path_join("wet_world.tres"))
	launcher.call("_configure_game", next, 5000000.0)
	root.add_child(next); current_scene = next; launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	fail("runtime Terrain3D startup timed out")
	return null

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 360000
	var grass := game.get_node_or_null("StandingGrass")
	var trees := game.get_node_or_null("BiomeDecorations")
	var global_terrain := game.get_node_or_null("GlobalTerrainLod3")
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = diagnostics.get("streaming", {})
		var native: Dictionary = diagnostics.get("native", {})
		var preparation: Dictionary = native.get("preparation", {})
		var placed: bool = absf(float(diagnostics.get("player_around", NAN)) - around) < 1.0 and absf(float(diagnostics.get("player_across", NAN)) - across) < 1.0
		var grass_ready: bool = grass == null or (int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0)
		var trees_ready: bool = trees == null or (int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0)
		var global_ready: bool = global_terrain == null or (not bool(global_terrain.get("GlobalSummaryBuildPending")) and int(global_terrain.get("PendingInitialSegmentCount")) == 0 and int(global_terrain.get("PendingSegmentRebuildCount")) == 0 and not bool(global_terrain.get("GlobalSegmentSamplingWorkerPending")))
		var ready := bool(diagnostics.get("ready", false)) and bool(diagnostics.get("visual_coverage", false)) and int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false)) and bool(preparation.get("ready", false)) and grass_ready and trees_ready and global_ready
		stable = stable + 1 if placed and ready else 0
		if stable >= 12: return true
	fail("Terrain3D workload did not settle at fixed pose")
	return false
