extends SceneTree
# Storm arrival acceptance capture (docs/storm-revamp-s3-storm-arrival-design.md section 12), modes relevant to WO-E1 and WO-F1. Boots the certified gameplay scene, finds a natural
# MajorFront with StormArrivalCaptureHelper and freezes the cloud clock (CloudSimulationPaused) at chosen moments of its lifecycle.
#   --mode=mist     post-rain mist: the rain core, then +60 / +300 / +900 s after the last rain, PostRainMist 1, and the same clearing moment with PostRainMist 0 and with the mist node off
#   --mode=rainbow  a low sun behind the observer with sunlit rain ahead: Rainbows 1, Rainbows 0, and a forced-rain-mask variant
#   --mode=mistvalley  the same clearing in a valley and from a ridge above it (relief scan within 5 km of the storm observer)
#   --mode=gust     the gust front at one fixed observer: lull, approach, the front edge, after it, fat drops; wind log; Gust front strength 0 control
#   --mode=hail     the natural hail swath at the same kind of observer: forward, looking down at the bounce, Hail 0 control
#   --mode=movie    (run with --write-movie ... --fixed-fps 30) a montage of one storm arriving at 1x: lull, gust front, fat drops, core, hail
#   --out=<dir> --elev=<sun elevation> --around=<scan start>
# Windowed GPU only; run it through tools/storm_arrival_capture.ps1 (GPU lock, fixed isolated profile). Prints STORM_ARRIVAL_CAPTURE=PASS.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const HELPER := preload("res://tools/StormArrivalCaptureHelper.cs")
const SETTLE_FRAMES := 18
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
var weather: Node
var atmo: Node
var sun: DirectionalLight3D
var mist_node: Node
var rainbow_node: Node
var effects: Node
var helper: RefCounted
var out_dir := ""
var mode := "mist"
var around := 8150667.0
var across := 119474.0
var elevation_target := 25.0
var report := {"status": "RUNNING", "captures": []}

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func fail(message: String) -> void:
	push_error("STORM_ARRIVAL_CAPTURE=FAIL " + message)
	quit(1)

func frames(count: int) -> void:
	for ignored in count: await process_frame

func settle_seconds(s: float) -> void:
	var end := Time.get_ticks_msec() + int(s * 1000.0)
	while Time.get_ticks_msec() < end: await process_frame

func run() -> void:
	create_timer(2400.0).timeout.connect(func(): fail("watchdog"))
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	out_dir = "C:/Projects/Ringworld/.godot/captures/storm_arrival/run"
	var scan_start := 100000.0
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--out="): out_dir = argument.get_slice("=", 1)
		if argument.begins_with("--mode="): mode = argument.get_slice("=", 1)
		if argument.begins_with("--elev="): elevation_target = float(argument.get_slice("=", 1))
		if argument.begins_with("--scan="): scan_start = float(argument.get_slice("=", 1))
		if argument.begins_with("--around="): around = float(argument.get_slice("=", 1))
		if argument.begins_with("--across="): across = float(argument.get_slice("=", 1))
	DirAccess.make_dir_recursive_absolute(out_dir)
	fixture = "res://.godot/atmosphere_visual/storm_arrival_%s" % Time.get_ticks_usec()
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
	weather = game.get_node_or_null("RingVolumetricClouds")
	atmo = game.get_node_or_null("DaySideAtmosphere")
	sun = game.get_node_or_null("StarLight") as DirectionalLight3D
	mist_node = game.get_node_or_null("PostRainMist")
	rainbow_node = game.get_node_or_null("Rainbow")
	effects = game.get_node_or_null("LocalWeatherEffects")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	helper = HELPER.new()
	if explorer == null or surface == null or drawer == null or clock == null or camera == null or weather == null or mist_node == null or rainbow_node == null:
		return fail("missing nodes (explorer=%s surface=%s drawer=%s clock=%s camera=%s weather=%s mist=%s rainbow=%s)" % [explorer != null, surface != null, drawer != null, clock != null, camera != null, weather != null, mist_node != null, rainbow_node != null])
	var modes := mode.split(",")
	drawer.call("SetAtmosphereEnabled", true)
	drawer.call("SetAtmosphereHazeModel", 1)
	weather.set("CloudSimulationPaused", true)
	await place(around, across)
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	await set_sun_elevation()
	report["sun_elevation"] = float(clock.get("SunElevationDegrees"))
	if "mist" in modes or mode == "both": await mist_mode(scan_start)
	if "mistvalley" in modes: await mist_valley_mode(scan_start)
	if "mistdiag" in modes: await mist_diag_mode(scan_start)
	if "gust" in modes: await gust_mode(scan_start)
	if "hail" in modes: await hail_mode(scan_start)
	if "movie" in modes: await movie_mode(scan_start)
	if "rainbow" in modes or mode == "both": await rainbow_mode(scan_start)
	report.status = "PASS"
	var f2 := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	f2.store_string(JSON.stringify(report, "\t")); f2.close()
	print("STORM_ARRIVAL_CAPTURE=PASS")
	quit(0)

# The sun is fixed in the world, so its elevation depends on where on the ring the player stands: set the cycle after every long teleport.
func set_sun_elevation() -> void:
	var best := 0.0
	var best_err := INF
	for step in 192:
		var f := float(step) / 192.0
		clock.call("SetCycleFraction", f)
		var e := float(clock.get("SunElevationDegrees"))
		if absf(e - elevation_target) < best_err: best_err = absf(e - elevation_target); best = f
	clock.set("Paused", true)
	clock.call("SetCycleFraction", best)
	await settle_seconds(3.0)

func place(a: float, z: float) -> void:
	around = a; across = z
	var ground := float(surface.call("SampleHeight", a, z)) + 1.1
	explorer.process_mode = Node.PROCESS_MODE_INHERIT
	explorer.call("SetCanonicalSpawnMeters", a, ground, z)
	explorer.call("SetPresentationView", 0.0, 0.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED

func set_cloud_time(t: float) -> void:
	weather.set("CloudSimulationTimeSeconds", t)

func shot(label: String, yaw: float, pitch: float) -> Dictionary:
	explorer.call("SetPresentationView", yaw, pitch)
	await frames(SETTLE_FRAMES)
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	image.save_png(out_dir.path_join(label + ".png"))
	var arrival := weather.call("GetStormArrivalDiagnostic") as Dictionary
	var d := {"label": label, "ground_m": surface.call("SampleHeight", around, across), "yaw": yaw, "pitch": pitch, "cloud_time": weather.get("CloudSimulationTimeSeconds"),
		"phase": arrival.get("phase"), "clearing": arrival.get("clearing"), "recent_rain": arrival.get("recent_rain"), "time_since_rain": arrival.get("time_since_rain_s"),
		"cooling": arrival.get("cooling_offset"), "mist": helper.call("MistState"), "mist_target": mist_node.get("TargetMist"),
		"rainbow_active": rainbow_node.get("Active"), "rainbow_strengths": rainbow_node.get("LastStrengths"), "rainbow_global": rainbow_node.get("LastGlobalStrength"),
		"luma": image_luma(image), "streaks": streak_state(), "fat_drop": arrival.get("fat_drop"), "gust_front": arrival.get("gust_front"), "hail_intensity": arrival.get("hail_intensity"),
		"core": arrival.get("core"), "approach": arrival.get("approach")}
	print("SHOT ", JSON.stringify(d))
	report.captures.append(d)
	return d

func streak_state() -> Dictionary:
	if effects == null: return {}
	var d := effects.call("GetRainStreakDiagnostics") as Dictionary
	return {"active": d.get("active"), "fat_drop": d.get("fat_drop"), "hail": d.get("hail"), "near_instances": d.get("near_instances"), "draws": d.get("draws")}

func wait_mist(target_quantised_tolerance := 0.04, timeout_s := 110.0) -> void:
	var end := Time.get_ticks_msec() + int(timeout_s * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame
		var state := helper.call("MistState") as Dictionary
		if absf(float(state["mist"]) - float(mist_node.get("TargetMist"))) <= target_quantised_tolerance: return

func mist_mode(scan_start: float) -> void:
	var storm := helper.call("FindStorm", weather, 1800.0, scan_start, 80.0) as Dictionary
	report["storm"] = storm
	if not bool(storm["ok"]): return fail("no mature MajorFront found")
	print("STORM ", JSON.stringify(storm))
	await place(float(storm["around"]), float(storm["across"]))
	await set_sun_elevation()
	report["sun_elevation_at_storm"] = float(clock.get("SunElevationDegrees"))
	var last_rain := float(storm["last_rain"]); var first_rain := float(storm["first_rain"]); var first_gust := float(storm["first_gust"])
	# Rain core, for contrast.
	set_cloud_time(0.5 * (first_rain + last_rain))
	await settle_seconds(3.0)
	await shot("mist_00_rain_core", 0.0, 3.0)
	# Mist off for the first look (node disabled): the post-rain clearing without the mist, the identity baseline.
	set_cloud_time(last_rain + 60.0)
	mist_node.process_mode = Node.PROCESS_MODE_DISABLED
	drawer.call("SetStormArrivalTuningValue", "PostRainMist", 0.0)
	await settle_seconds(6.0)
	await shot("mist_10_clear60_node_off", 0.0, 3.0)
	await shot("mist_10_clear60_node_off_b", 0.0, 3.0)
	# Setting 0 with the node running: the identity (must equal node off).
	mist_node.process_mode = Node.PROCESS_MODE_INHERIT
	await settle_seconds(5.0)
	await shot("mist_11_clear60_setting0", 0.0, 3.0)
	# Setting 1: the time-lapse through the clearing.
	drawer.call("SetStormArrivalTuningValue", "PostRainMist", 1.0)
	for offset in [60.0, 300.0, 900.0, 1500.0]:
		set_cloud_time(last_rain + offset)
		await wait_mist()
		await settle_seconds(3.0)
		await shot("mist_2%d_clear%d" % [int(offset) / 300, int(offset)], 0.0, 3.0)
		await shot("mist_2%d_clear%d_up" % [int(offset) / 300, int(offset)], 90.0, 3.0)
	# A/B at +300 s through four headings: mist off, then on.
	set_cloud_time(last_rain + 300.0)
	for setting in [0.0, 1.0]:
		drawer.call("SetStormArrivalTuningValue", "PostRainMist", setting)
		await wait_mist(0.04, 110.0)
		await settle_seconds(3.0)
		for yaw in [0.0, 90.0, 180.0, 270.0]:
			await shot("mist_4%d_ab_y%d" % [int(setting), int(yaw)], yaw, 2.0)
	# Pre-storm for the baseline of the same place (no mist, nothing arrived).
	set_cloud_time(maxf(0.0, first_gust - 300.0))
	await wait_mist()
	await settle_seconds(3.0)
	await shot("mist_30_prestorm", 0.0, 3.0)
	report["mist_publishes"] = (helper.call("MistState") as Dictionary)["publishes"]


# ---------------------------------------------------------------- mist diagnostics: what the materials really carry at M
func collect_haze(node: Node, into: Dictionary) -> void:
	var mats: Array = []
	if node is GeometryInstance3D:
		var g := node as GeometryInstance3D
		if g.material_override != null: mats.append(g.material_override)
		if g is MeshInstance3D and (g as MeshInstance3D).mesh != null:
			for i in (g as MeshInstance3D).mesh.get_surface_count():
				var m := (g as MeshInstance3D).get_active_material(i)
				if m != null: mats.append(m)
	for m in mats:
		if m is ShaderMaterial:
			var v = (m as ShaderMaterial).get_shader_parameter("ring_haze_sigma_M0")
			if v != null:
				var key := "%s|%s" % [str(snappedf(float(v), 1e-7)), str((m as ShaderMaterial).get_shader_parameter("ring_haze_H_mist"))]
				into[key] = int(into.get(key, 0)) + 1
	for c in node.get_children(): collect_haze(c, into)

func mist_diag_mode(scan_start: float) -> void:
	var storm := helper.call("FindStorm", weather, 1800.0, scan_start, 80.0) as Dictionary
	if not bool(storm["ok"]): return fail("no storm")
	await place(float(storm["around"]), float(storm["across"]))
	await set_sun_elevation()
	var last_rain := float(storm["last_rain"])
	for setting in [0.0, 1.0]:
		drawer.call("SetStormArrivalTuningValue", "PostRainMist", setting)
		set_cloud_time(last_rain + 300.0)
		await wait_mist(0.04, 110.0)
		await settle_seconds(4.0)
		var found := {}
		collect_haze(game, found)
		print("MISTDIAG setting=", setting, " state=", JSON.stringify(helper.call("MistState")), " materials=", JSON.stringify(found))
		await shot("mistdiag_%d" % int(setting), 0.0, 2.0)

# ---------------------------------------------------------------- mist valley
func mist_valley_mode(scan_start: float) -> void:
	var storm := helper.call("FindStorm", weather, 1800.0, scan_start, 80.0) as Dictionary
	report["storm"] = storm
	if not bool(storm["ok"]): return fail("no mature MajorFront found")
	var relief := helper.call("ScanRelief", weather, float(storm["around"]), float(storm["across"]), 5000.0, 100.0) as Dictionary
	report["relief"] = relief
	print("RELIEF ", JSON.stringify(relief))
	var last_rain := float(storm["last_rain"])
	var spots := {"valley": [float(relief["valley_around"]), float(relief["valley_across"])], "ridge": [float(relief["ridge_around"]), float(relief["ridge_across"])]}
	drawer.call("SetStormArrivalTuningValue", "PostRainMist", 1.0)
	for spot in ["valley", "ridge"]:
		await place(float(spots[spot][0]), float(spots[spot][1]))
		await set_sun_elevation()
		for offset in [300.0, 900.0]:
			set_cloud_time(last_rain + offset)
			await wait_mist()
			await settle_seconds(3.0)
			for yaw in [0.0, 120.0, 240.0]:
				await shot("mistvalley_%s_%d_y%d" % [spot, int(offset), int(yaw)], yaw, (-9.0 if spot == "ridge" else 2.0))
		# Off control at the same place: identical framing without the mist.
		drawer.call("SetStormArrivalTuningValue", "PostRainMist", 0.0)
		set_cloud_time(last_rain + 300.0)
		await wait_mist(0.04, 110.0)
		await settle_seconds(3.0)
		await shot("mistvalley_%s_off_y0" % spot, 0.0, (-9.0 if spot == "ridge" else 2.0))
		drawer.call("SetStormArrivalTuningValue", "PostRainMist", 1.0)

# ---------------------------------------------------------------- arrival sequence (gust, hail, movie)
func find_sequence(scan_start: float) -> Dictionary:
	var seq := helper.call("FindArrivalSequence", weather, 1800.0, scan_start, 120.0) as Dictionary
	report["sequence"] = seq
	print("SEQUENCE ", JSON.stringify(seq))
	if not bool(seq["ok"]): fail("no arrival sequence with hail found: " + JSON.stringify(seq))
	return seq

func face_storm(bearing: Vector2) -> float:
	var grid_node := game.get_node("ChunkGridManager")
	var world_dir: Vector3 = helper.call("RingDirectionToWorld", grid_node, around, across, bearing)
	return await align_yaw(world_dir)

func gust_mode(scan_start: float) -> void:
	var seq := find_sequence(scan_start)
	if seq.is_empty() or not bool(seq["ok"]): return
	await place(float(seq["around"]), float(seq["across"]))
	await set_sun_elevation()
	var first_gust := float(seq["first_gust"])
	# Wind log through the front (strength 1 and 0): the speed jump and direction change at the edge.
	var wind_log := []
	for setting in [1.0, 0.0]:
		drawer.call("SetStormArrivalTuningValue", "GustFrontStrength", setting)
		for step in range(-40, 41, 4):
			var w := helper.call("WindAt", weather, around, across, first_gust + float(step)) as Dictionary
			wind_log.append({"strength": setting, "t": step, "mean_speed": w["mean_speed"], "instant_speed": w["instant_speed"], "mean": str(w["mean"])})
	report["wind_log"] = wind_log
	drawer.call("SetStormArrivalTuningValue", "GustFrontStrength", 1.0)
	set_cloud_time(first_gust)
	await settle_seconds(3.0)
	var arrival := weather.call("GetStormArrivalDiagnostic") as Dictionary
	var yaw: float = await face_storm(arrival.get("bearing", Vector2(1, 0)))
	report["storm_yaw"] = yaw
	for item in [["a_lull", -120.0], ["b_approach", -40.0], ["c_edge", 0.0], ["d_after", 25.0], ["e_fat", float(seq["first_fat"]) - first_gust], ["f_core", float(seq["first_core"]) - first_gust + 20.0]]:
		set_cloud_time(first_gust + float(item[1]))
		await settle_seconds(4.0)
		await shot("gust_%s" % item[0], yaw, 4.0)
		await shot("gust_%s_down" % item[0], yaw, -25.0)
	# Control: Gust front strength 0 at the edge (wind and gusts identical to the pre-S3 field).
	drawer.call("SetStormArrivalTuningValue", "GustFrontStrength", 0.0)
	set_cloud_time(first_gust + 5.0)
	await settle_seconds(4.0)
	await shot("gust_g_edge_strength0", yaw, 4.0)
	drawer.call("SetStormArrivalTuningValue", "GustFrontStrength", 1.0)

func hail_mode(scan_start: float) -> void:
	var seq := find_sequence(scan_start)
	if seq.is_empty() or not bool(seq["ok"]): return
	await place(float(seq["around"]), float(seq["across"]))
	await set_sun_elevation()
	var peak := float(seq["hail_peak"])
	set_cloud_time(peak)
	await settle_seconds(4.0)
	var yaw := 0.0
	for item in [["forward", 3.0], ["level", -8.0], ["down", -30.0], ["up", 25.0]]:
		await shot("hail_on_%s" % item[0], yaw, float(item[1]))
	await shot("hail_on_forward_b", yaw, 3.0)
	drawer.call("SetStormArrivalTuningValue", "Hail", 0.0)
	await settle_seconds(3.0)
	for item in [["forward", 3.0], ["down", -30.0]]:
		await shot("hail_off_%s" % item[0], yaw, float(item[1]))
	drawer.call("SetStormArrivalTuningValue", "Hail", 1.0)
	# The swath edge (weaker hail).
	set_cloud_time(float(seq["hail_start"]) + 20.0)
	await settle_seconds(4.0)
	await shot("hail_edge_forward", yaw, 3.0)
	await shot("hail_edge_down", yaw, -30.0)

func movie_frames(count: int) -> void:
	for i in count: await process_frame

# Montage at 1x on the cloud clock (the fixed-fps timestep makes frames exact): lull, gust front, fat drops, core, hail. Cuts between segments jump the cloud clock.
func movie_mode(scan_start: float) -> void:
	var seq := find_sequence(scan_start)
	if seq.is_empty() or not bool(seq["ok"]): return
	await place(float(seq["around"]), float(seq["across"]))
	await set_sun_elevation()
	var first_gust := float(seq["first_gust"])
	set_cloud_time(first_gust)
	await settle_seconds(1.0)
	var arrival := weather.call("GetStormArrivalDiagnostic") as Dictionary
	var yaw: float = await face_storm(arrival.get("bearing", Vector2(1, 0)))
	var fps := 30
	var segments := [["lull", first_gust - 100.0, 5.0], ["gust_front", first_gust - 6.0, 12.0], ["fat_drops", float(seq["first_fat"]) - 4.0, 12.0], ["core", float(seq["first_core"]) + 10.0, 6.0], ["hail", float(seq["hail_peak"]) - 4.0, 10.0]]
	weather.set("CloudSimulationPaused", true)
	var movie_log := []
	var frame_index := 0
	DirAccess.make_dir_recursive_absolute(out_dir.path_join("frames"))
	for segment in segments:
		set_cloud_time(float(segment[1]))
		await movie_frames(24)   # let the new moment settle (not recorded)
		var total := int(float(segment[2]) * fps)
		for f in total:
			# One recorded frame per 1/30 s of cloud time: the clock is set exactly, so the montage is real time at 1x inside each segment.
			set_cloud_time(float(segment[1]) + float(f) / float(fps))
			await process_frame
			await RenderingServer.frame_post_draw
			root.get_viewport().get_texture().get_image().save_png(out_dir.path_join("frames/f%05d.png" % frame_index))
			frame_index += 1
			if f % (fps * 2) == 0:
				var a := weather.call("GetStormArrivalDiagnostic") as Dictionary
				movie_log.append({"segment": segment[0], "frame": frame_index, "t": weather.get("CloudSimulationTimeSeconds"), "phase": a.get("phase"), "gust": a.get("gust_front"), "fat": a.get("fat_drop"), "core": a.get("core"), "hail": a.get("hail_intensity")})
	report["movie_log"] = movie_log

func align_yaw(anti_world: Vector3) -> float:
	explorer.call("SetPresentationView", 0.0, 0.0)
	await frames(4)
	var up: Vector3 = (explorer as Node3D).global_transform.basis.y.normalized()
	var forward: Vector3 = -camera.global_transform.basis.z
	forward = (forward - up * forward.dot(up)).normalized()
	var target: Vector3 = (anti_world - up * anti_world.dot(up)).normalized()
	var delta := rad_to_deg(forward.signed_angle_to(target, up))
	explorer.call("SetPresentationView", delta, 0.0)
	await frames(4)
	var after: Vector3 = -camera.global_transform.basis.z
	after = (after - up * after.dot(up)).normalized()
	if after.dot(target) < 0.98:
		delta = -delta
		explorer.call("SetPresentationView", delta, 0.0)
		await frames(4)
	return delta

func rainbow_mode(scan_start: float) -> void:
	# Go to the storm area first and set the sun there (its elevation changes with the position on the ring), then place the observer within 30 km.
	var storm := helper.call("FindStorm", weather, 1800.0, scan_start, 1.0e9) as Dictionary
	if not bool(storm["ok"]): return fail("no mature MajorFront found")
	await place(float(storm["around"]), float(storm["across"]))
	await set_sun_elevation()
	report["sun_elevation_at_storm"] = float(clock.get("SunElevationDegrees"))
	var to_sun: Vector3 = sun.global_transform.basis.z.normalized()
	var grid_node := game.get_node("ChunkGridManager")
	var sun_ring: Vector3 = helper.call("SunInRingFrame", grid_node, around, across, to_sun)
	var placement := helper.call("PlaceRainbowObserver", weather, sun_ring, 1800.0, float(storm["around"]) - 200000.0) as Dictionary
	placement["sun_ring_at_fixture"] = str(sun_ring)
	report["rainbow_placement"] = placement
	print("RAINBOW_PLACEMENT ", JSON.stringify(placement))
	if not bool(placement["ok"]): return fail("no rainbow observer found")
	await place(float(placement["around"]), float(placement["across"]))
	set_cloud_time(1800.0)
	drawer.call("SetStormArrivalTuningValue", "PostRainMist", 0.0)
	drawer.call("SetStormArrivalTuningValue", "Rainbows", 1.0)
	await settle_seconds(5.0)
	to_sun = sun.global_transform.basis.z.normalized()
	var sun_ring_now: Vector3 = helper.call("SunInRingFrame", grid_node, around, across, to_sun)
	report["sun_ring_after_teleport"] = str(sun_ring_now)
	print("SUN_RING before=", sun_ring, " after=", sun_ring_now)
	var yaw := await align_yaw(-to_sun)
	report["rainbow_yaw"] = yaw
	for pitch in [8.0, 18.0, 28.0]:
		await shot("rainbow_real_on_p%d" % int(pitch), yaw, pitch)
	await settle_seconds(1.0)
	drawer.call("SetStormArrivalTuningValue", "Rainbows", 0.0)
	await settle_seconds(2.0)
	await shot("rainbow_real_off_p18", yaw, 18.0)
	await shot("rainbow_real_off_p18_b", yaw, 18.0)
	# Forced rain mask: wetness 1 at every distance on every azimuth, so the bow is fully there whatever the storm geometry.
	drawer.call("SetStormArrivalTuningValue", "Rainbows", 1.0)
	rainbow_node.set("ForcedWetness", 1.0)
	await settle_seconds(4.0)
	for pitch in [8.0, 18.0, 28.0]:
		await shot("rainbow_forced_on_p%d" % int(pitch), yaw, pitch)
	rainbow_node.set("ForcedWetness", -1.0)

func image_luma(image: Image) -> float:
	image.convert(Image.FORMAT_RGBA8)
	var data := image.get_data()
	var w := image.get_width(); var h := image.get_height()
	var sum := 0.0; var count := 0
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
	for item in [["InstallGrassDefaults","grass.tres"], ["InstallVegetationDefaults","vegetation.tres"], ["InstallWaterDefaults","water.tres"], ["InstallAntiAliasingDefaults","anti_aliasing.tres"], ["InstallShadowDefaults","shadows.tres"], ["InstallSurfaceTerrainDefaults","surface.tres"], ["InstallAtmosphereDefaults","atmosphere.tres"], ["InstallStormArrivalDefaults","storm_arrival.tres"], ["InstallWindDefaults","wind.tres"], ["InstallRainHazeDefaults","rain_haze.tres"]]:
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
		var placed: bool = absf(float(diagnostics.get("player_around", NAN)) - around) < 1.0 and absf(float(diagnostics.get("player_across", NAN)) - across) < 1.0
		var grass_ready := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0
		var trees_ready := int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0
		var global_ready: bool = global_terrain != null and not bool(global_terrain.get("GlobalSummaryBuildPending")) and int(global_terrain.get("PendingInitialSegmentCount")) == 0 and int(global_terrain.get("PendingSegmentRebuildCount")) == 0 and not bool(global_terrain.get("GlobalSegmentSamplingWorkerPending"))
		var ready := bool(diagnostics.get("ready", false)) and bool(diagnostics.get("visual_coverage", false)) and int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false)) and bool(preparation.get("ready", false)) and grass_ready and trees_ready and global_ready
		stable = stable + 1 if placed and ready else 0
		if stable >= 12: return true
	fail("Terrain3D workload did not settle at the pose")
	return false
