extends SceneTree
# Storm anvil capture (docs/storm-revamp-s4-design.md WO-A, docs/storm-revamp-s4-sculpt-v2-design.md WO-V4). Windowed GPU run; run through
# tools/storm_anvil_capture.ps1.
# Args: --out=<dir> --shots=<name,name,...> [--settle=<frames>] [--cycle=<0..1>] [--storm-radius=<m>] [--sun=<deg>] [--rain]
#       [--hero-radius=<m>] [--life=<code>] [--mammatus=<0..1>] [--shear=<0..1>]
# The preview storm stays at one fixed place and the CAMERA moves, so the hero cell (the tower with the greatest downwind canopy reach,
# found by reading the storm profile map back) is the same in every shot. Clocks are paused, rain is off (PrecipitationStart 0.99)
# unless --rain, and --sun sets the day cycle so the sun is at that elevation.
# Shots (all relative to the hero cell, H = hero):
#   side30 / side60      camera 30 / 60 km across the shear from H, looks at H (plus _mirror from the other side)
#   side60hi             camera 9 km up, 60 km across
#   down                 camera 35 km downwind of H looking upwind (and up: upwind of H looking downwind)
#   beneath              2 km under the canopy (10.3 km up) at half its reach, pitch 60: beneath_m0 / beneath_m1 (mammatus 0 / 1, L = 1.6)
#   plan / plan_wide     camera straight above H at 25 / 45 km
#   life                 lifecycle strip (debug L override) from the side30 view
#   orbit                10 s orbit (--orbit-frames 300, --orbit-h 9000, --orbit-dist 60000) around H, one frame per saved image (flicker / mp4)
#   wall                 (S6) under the storm edge looking out at yaw 0/90/180/270 x pitch 5/15, storm then clear (--wall-d km)
#   under                looking up from under the storm, camera at 0 / 6 / 12 / 18 km downwind of H: up60, up85 and level views
#   outside              45 km outside views
#   perf                 GPU timing windows (--perf-dist, --perf-seconds, --perf-cases: new, legacy, clear (storm moved off), dfetch_off
#                        (storm-light D without its ownership/front fetch))
#   glintdbg             the ocean-glint pose (6 km downwind of H, 1.5 km up) in the normal render and the storm-level debug view
#                        (ringworld_shadow_debug_mode 6), CPU D along the view, then the same pose in clear weather (glintdbg_clear)
#   farstorm / farclear  far-viewport check: --far-dist (200 km) downwind of H, --far-h (20 km) up, looking back; normal + debug view 6, plus the
#                        CPU float mirror of the far-viewport storm-map mapping (far_map_check); farclear moves the storm off the ring
#   flat                 (reserved)

var world: Node
var helper: RefCounted
var probe: RefCounted
var out_dir := ""
var settle := 90
var cycle := 0.6
var radius := 40000.0
var finished := false
var hero := Vector2.ZERO
var hero_info := {}

func _initialize():
	create_timer(1800.0).timeout.connect(func(): _fail("1800 second watchdog expired"))
	call_deferred("_run")

func _arg(name: String, default_value: String) -> String:
	for a in OS.get_cmdline_user_args():
		if a == "--" + name:
			return "1"
		if a.begins_with("--" + name + "="):
			return a.substr(name.length() + 3)
	return default_value

func _run():
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice")
		return
	out_dir = _arg("out", "res://.godot/captures/storm_anvils/test")
	settle = int(_arg("settle", "90"))
	cycle = float(_arg("cycle", "0.6"))
	radius = float(_arg("storm-radius", "40000"))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir))
	var far = load("res://tools/FarRingGameplayCapture.cs").new()
	if not far.ConfigureArguments():
		_fail(far.ErrorText)
		return
	helper = load("res://tools/StormAnvilCapture.cs").new()
	probe = load("res://tools/StormProfileMapProbe.cs").new()
	helper.SetLegacyShape(_arg("legacy", "") != "")
	world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
	var launcher = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", world, 5000000.0)
	launcher.free()
	if not far.Prepare(world, "res://.godot/far-ring-gameplay/storm_anvils_prep"):
		_fail(far.ErrorText)
		return
	root.add_child(world)
	await process_frame
	if not helper.Setup(world, cycle, 1500.0):
		_fail(helper.ErrorText)
		return
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	await create_timer(float(_arg("warmup", "25"))).timeout
	if _arg("sun", "") != "":
		await _set_sun(float(_arg("sun", "35")))
	print("STORM_ANVIL sun_elevation_deg=", helper.SunElevationDegrees(world))
	for n in world.get_children():
		if n is CanvasLayer:
			n.visible = false
	for n in root.get_children():
		if n is CanvasLayer:
			n.visible = false
	if _arg("rain", "") == "":
		print("STORM_ANVIL rain_off nodes=", helper.RainOff(world))
	var bridge = world.get_node("SunshineCloudBridge")
	for setting in ["shear", "mammatus"]:
		if _arg(setting, "") != "":
			bridge.set("AnvilShear" if setting == "shear" else "MammatusStrength", float(_arg(setting, "0")))
	if _arg("bias", "") != "":
		bridge.set("CloudCoverageBias", float(_arg("bias", "0")))
		bridge.call("ApplyTemporalQualityTuning", true)
	if _arg("deterministic", "") != "":
		bridge.set("RayStartJitterStrength", 0.0)
		bridge.set("CloudResolutionJitter", false)
		bridge.set("DitherSpeed", 0.0)
		bridge.call("ApplyTemporalQualityTuning", true)
		bridge.call("SetDiagnosticTemporalHistoryDisabled", true)
		await _frames(30)
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--set="):
			var kv := a.substr(6).split("=")
			if kv.size() == 2:
				bridge.set(kv[0], float(kv[1]))
				print("STORM_ANVIL set ", kv[0], "=", kv[1])
	if _arg("no-haze", "") != "":
		bridge.call("DebugSetCloudAtmosphericDensity", 0.0)
	if _arg("no-ae", "") != "":
		for n in world.find_children("*", "", true, false):
			if n.get("storm_auto_exposure_enabled") != null:
				n.set("storm_auto_exposure_enabled", false)
				print("STORM_ANVIL auto exposure off on ", n.name)
	if _arg("matte", "") != "":
		bridge.call("SetDiagnosticDensityMattes", true, false)
	if _arg("life", "") != "":
		helper.SetLifecycle(float(_arg("life", "1.6")))
	var wind: Vector2 = helper.WindDirection(world)
	print("STORM_ANVIL wind=", wind, " storm_ambient=", bridge.GetStormAmbientUniform())
	if _arg("shots", "side30") != "perf":
		await _find_hero(wind)
	for shot in _arg("shots", "side30").split(","):
		await _shoot(shot, wind)
	print("STORM_ANVIL=PASS out=", out_dir)
	finished = true
	quit(0)

# Bisects the day cycle so the sun is at `target` degrees (the scan covers the whole cycle, then refines around the best fraction).
func _set_sun(target: float):
	var cyc = world.get_node("RingDayNightCycle")
	var best_f := cycle
	var best_err := 1e9
	for i in 100:
		var f := float(i) / 100.0
		cyc.call("SetCycleFraction", f)
		await process_frame
		var err := absf(float(helper.SunElevationDegrees(world)) - target)
		if err < best_err:
			best_err = err
			best_f = f
	var refine_from := best_f - 0.01
	for i in 21:
		var f := fposmod(refine_from + float(i) * 0.001, 1.0)
		cyc.call("SetCycleFraction", f)
		await process_frame
		var err := absf(float(helper.SunElevationDegrees(world)) - target)
		if err < best_err:
			best_err = err
			best_f = f
	cyc.call("SetCycleFraction", best_f)
	await _frames(10)
	print("STORM_ANVIL sun target=", target, " cycle=", best_f, " err=", best_err)

# Places the preview storm once, waits for the storm profile map, reads it back and picks the hero cell.
func _find_hero(wind: Vector2):
	_storm(Vector2.ZERO)
	helper.PlaceCamera(world, 1500.0, _yaw(wind), 0.0)
	for i in 900:
		if int(probe.State(world)["builds"]) >= 3 and bool(probe.State(world)["valid"]):
			break
		await process_frame
	await _frames(30)
	probe.RequestReadback(world)
	for i in 120:
		await process_frame
		if probe.ReadbackReady:
			break
	# 30 km: since the look pass scaled the cell lattice with the system size (sizeK, cells about 90 km wide) the nearest qualifying tower
	# of the preview storm sits about 12 km from its centre, outside the old 9 km default.
	hero_info = probe.FindHero(world, wind, 0.0, 0.0, float(_arg("hero-radius", "30000")))
	if bool(hero_info.get("found", false)):
		hero = Vector2(hero_info["around"], hero_info["across"])
	print("STORM_ANVIL hero ", hero_info)
	if _arg("preview", "") != "":
		probe.SavePreviews(ProjectSettings.globalize_path(_arg("preview", "")))

# Yaw radians for a canonical (around, across) direction.
func _yaw(d: Vector2) -> float:
	return atan2(d.y, d.x)

func _storm(offset: Vector2) -> bool:
	return helper.PlaceStorm(world, offset.x, offset.y, _yaw(Vector2(1, 0)), radius)

func _frames(n: int):
	for i in n:
		await process_frame

func _save(name: String):
	await RenderingServer.frame_post_draw
	var image = root.get_texture().get_image()
	var path = ProjectSettings.globalize_path(out_dir).path_join(name + ".png")
	image.save_png(path)
	print("STORM_ANVIL saved ", path)

# Camera at hero - view_dir * dist (offsets from the base position), height h, looking along view_dir at aim_h above the hero.
func _look_at_hero(h: float, view_dir: Vector2, dist: float, aim_h: float, extra_frames := 0):
	var cam := hero - view_dir * dist
	var pitch = rad_to_deg(atan2(aim_h - h, dist))
	helper.PlaceCameraAt(world, cam.x, cam.y, h, _yaw(view_dir), pitch)
	await _frames(settle + extra_frames)

func _camera_at(offset: Vector2, h: float, yaw: float, pitch: float, extra_frames := 0):
	helper.PlaceCameraAt(world, offset.x, offset.y, h, yaw, pitch)
	await _frames(settle + extra_frames)

func _shoot(shot: String, wind: Vector2):
	var cross := Vector2(-wind.y, wind.x)
	var far_extra := 90
	match shot:
		"side30", "side60":
			var d := 30000.0 if shot == "side30" else 60000.0
			var aim := 9000.0 if shot == "side30" else 8000.0
			await _look_at_hero(1500.0, cross, d, aim, far_extra)
			await _save(shot)
			# Mirror: view from the other side of the shear (wind direction reversed in the image).
			await _look_at_hero(1500.0, -cross, d, aim, far_extra)
			await _save(shot + "_mirror")
		"side60hi":
			await _look_at_hero(9000.0, cross, 60000.0, 8000.0, far_extra)
			await _save("side60hi")
		"mamside":
			await _look_at_hero(1500.0, cross, 16000.0, 11000.0, far_extra)
			await _save("mamside")
		"down":
			# Camera downwind of the hero, looking upwind at it; and the reverse.
			await _look_at_hero(1500.0, -wind, 35000.0, 9000.0, far_extra)
			await _save("down")
			await _look_at_hero(1500.0, wind, 35000.0, 9000.0, far_extra)
			await _save("up")
		"beneath":
			# Under the downwind canopy at half its reach, pitched up 60 degrees, mammatus off then on, mature (L = 1.6).
			helper.SetLifecycle(1.6)
			var reach: float = float(hero_info.get("reach", 12000.0))
			var spot := hero + wind * (0.5 * reach)
			var bridge = world.get_node("SunshineCloudBridge")
			for m in [0.0, 1.0]:
				bridge.set("MammatusStrength", m)
				await _camera_at(spot, 10300.0, _yaw(-wind), 60.0, far_extra)
				await _save("beneath_m%d" % int(m))
				await _camera_at(spot, 10300.0, _yaw(cross), 60.0, 0)
				await _save("beneath_cross_m%d" % int(m))
			bridge.set("MammatusStrength", 0.6)
		"plan", "plan_wide":
			var h := 25000.0 if shot == "plan" else 45000.0
			await _camera_at(hero, h, _yaw(wind), -85.0, 60 + far_extra)
			await _save(shot)
		"orbit":
			var n := int(_arg("orbit-frames", "300"))
			for i in n:
				var a := TAU * float(i) / float(n)
				var v := Vector2(cos(a), sin(a))
				var orbit_h := float(_arg("orbit-h", "9000"))
				var orbit_d := float(_arg("orbit-dist", "60000"))
				var cam := hero - v * orbit_d
				helper.PlaceCameraAt(world, cam.x, cam.y, orbit_h, _yaw(v), rad_to_deg(atan2(8000.0 - orbit_h, orbit_d)))
				await _frames(settle + far_extra if i == 0 else 2)
				await _save("frame%08d" % i)
		"life":
			# Lifecycle strip through the debug L override.
			for code in [0.5, 1.0, 1.5, 2.0, 2.5]:
				helper.SetLifecycle(code)
				await _look_at_hero(1500.0, cross, 30000.0, 9000.0, far_extra)
				await _save("life_%.1f" % code)
			helper.SetLifecycle(float(_arg("life", "NAN")) if _arg("life", "") != "" else NAN)
		"under":
			# Looking up from under the storm: camera 0 / 6 / 12 / 18 km downwind of the hero core.
			helper.SetLifecycle(float(_arg("life", "1.6")))
			for d in [0, 6, 12, 18]:
				var spot2 := hero + wind * (float(d) * 1000.0)
				await _camera_at(spot2, 1500.0, _yaw(wind), 60.0, far_extra if d == 0 else 30)
				print("STORM_ANVIL levels under ", d, " ", helper.StormLevels(world, spot2.x, spot2.y))
				await _save("under_%02d_up60" % d)
				await _camera_at(spot2, 1500.0, _yaw(wind), 85.0, 0)
				await _save("under_%02d_up85" % d)
				await _camera_at(spot2, 1500.0, _yaw(-wind), 8.0, 0)
				await _save("under_%02d_level" % d)
		"edge":
			# Invisible-wall check: camera inside the storm footprint looking OUTWARD across the edge at low pitch, then the same poses with the storm moved away.
			helper.SetLifecycle(float(_arg("life", "1.6")))
			for d in [20, 30, 38]:
				var spot4 := hero + cross * (float(d) * 1000.0)
				await _camera_at(spot4, 1500.0, _yaw(cross), 6.0, far_extra if d == 20 else 30)
				await _save("edge_%02d" % d)
			_storm(Vector2(0.0, 400000.0))
			for d in [20, 30, 38]:
				await _camera_at(hero + cross * (float(d) * 1000.0), 1500.0, _yaw(cross), 6.0, far_extra + 60 if d == 20 else 30)
				await _save("edge_%02d_clear" % d)
			_storm(Vector2.ZERO)
			await _frames(60)
		"wall":
			# S6 WO3a wall check: camera under the storm near its edge (--wall-d km from H across the wind, 1.5 km up) looking out at yaw 0 / 90 /
			# 180 / 270 relative to the wind and pitch 5 / 15; clouds beyond the edge must be present with the storm and without the wall.
			helper.SetLifecycle(float(_arg("life", "1.6")))
			var wall_spot := hero + cross * (float(_arg("wall-d", "30")) * 1000.0)
			var wall_dirs := [wind, cross, -wind, -cross]
			for pass_index in 2:
				if pass_index == 1:
					_storm(Vector2(0.0, 400000.0))
				for yi in 4:
					for pitch in [5.0, 15.0]:
						await _camera_at(wall_spot, 1500.0, _yaw(wall_dirs[yi]), pitch, far_extra if (yi == 0 and pitch == 5.0) else 20)
						await _save("wall_%s_y%03d_p%02d" % ["clear" if pass_index == 1 else "storm", yi * 90, int(pitch)])
			_storm(Vector2.ZERO)
			await _frames(60)
		"glint":
			# Look pass diagnostic: the ocean sun glint under a closed storm, with the sky sun disc and the StarLight toggled.
			helper.SetLifecycle(float(_arg("life", "1.6")))
			var env = world.get_node("WorldEnvironment").environment
			var sky_mat = env.sky.sky_material
			var star = world.get_node("StarLight")
			var spot3 := hero + wind * 6000.0
			await _camera_at(spot3, 1500.0, _yaw(-wind), 8.0, far_extra + 30)
			print("STORM_ANVIL levels glint ", helper.StormLevels(world, spot3.x, spot3.y))
			await _save("glint_base")
			var disk = sky_mat.get_shader_parameter("sun_disk_intensity")
			if disk == null: disk = 2.55
			var halo = sky_mat.get_shader_parameter("sun_halo_intensity")
			if halo == null: halo = 0.20
			sky_mat.set_shader_parameter("sun_disk_intensity", 0.0)
			sky_mat.set_shader_parameter("sun_halo_intensity", 0.0)
			await _frames(20)
			await _save("glint_nodisc")
			var energy: float = star.light_energy
			star.light_energy = 0.0
			await _frames(20)
			await _save("glint_nodisc_nostar")
			sky_mat.set_shader_parameter("sun_disk_intensity", disk)
			sky_mat.set_shader_parameter("sun_halo_intensity", halo)
			await _frames(20)
			await _save("glint_nostar")
			star.light_energy = energy
		"glintdbg":
			# Look pass diagnostic: the glint pose in the normal render and in the storm-level debug view (ringworld_shadow_debug_mode 6), with the
			# CPU storm level D along the view for comparison.
			helper.SetLifecycle(float(_arg("life", "1.6")))
			var spot5 := hero + wind * 6000.0
			await _camera_at(spot5, 1500.0, _yaw(-wind), 8.0, far_extra + 30)
			for k in [0.0, 2000.0, 5000.0, 10000.0, 20000.0, 40000.0]:
				print("STORM_ANVIL levels glintdbg ahead=", k, " ", helper.StormLevels(world, spot5.x - wind.x * k, spot5.y - wind.y * k))
			await _save("glintdbg_base")
			RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 6)
			await _frames(20)
			await _save("glintdbg_d6")
			RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
			await _frames(20)
			# The same pose in clear weather (preview storm moved off the ring): the sun glint must still be there.
			_storm(Vector2(0.0, 400000.0))
			await _frames(far_extra + 60)
			await _save("glintdbg_clear")
			_storm(Vector2.ZERO)
			await _frames(60)
		"farstorm", "farclear":
			# Far-viewport check: camera 200 km downwind of the storm, 20 km up, looking back at it; normal render and the storm-level debug view.
			# farclear: the same poses with the preview storm moved off the ring.
			helper.SetLifecycle(float(_arg("life", "1.6")))
			if shot == "farclear":
				_storm(Vector2(0.0, 400000.0))
			var far_dist := float(_arg("far-dist", "200000"))
			var far_h := float(_arg("far-h", "20000"))
			var spot6 := hero + wind * far_dist
			var far_pitch := -rad_to_deg(atan2(far_h, far_dist)) + rad_to_deg(far_dist / 2.0 / 5000000.0)
			await _camera_at(spot6, far_h, _yaw(-wind), far_pitch, far_extra + 60)
			print("STORM_ANVIL ", helper.FarStormMappingCheck(world))
			for k in [0.0, 50000.0, 100000.0, 150000.0, 200000.0, 250000.0]:
				print("STORM_ANVIL levels ", shot, " ahead=", k, " ", helper.StormLevels(world, spot6.x - wind.x * k, spot6.y - wind.y * k))
			await _save(shot + "_base")
			RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 6)
			await _frames(20)
			await _save(shot + "_d6")
			RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
			if shot == "farclear":
				_storm(Vector2.ZERO)
			await _frames(30)
		"clear":
			# Reference for the under-storm balance: the same level / up views with the preview storm moved 400 km away.
			_storm(Vector2(0.0, 400000.0))
			await _camera_at(hero, 1500.0, _yaw(-wind), 8.0, far_extra + 120)
			await _save("clear_level")
			await _camera_at(hero, 1500.0, _yaw(wind), 60.0, 0)
			await _save("clear_up60")
			_storm(Vector2.ZERO)
			await _frames(60)
		"u6":
			helper.SetLifecycle(float(_arg("life", "1.6")))
			await _camera_at(hero + wind * 6000.0, 1500.0, _yaw(wind), 60.0, far_extra + 30)
			await _save("u6_up60")
		"outside":
			for d in [30000.0, 45000.0]:
				await _look_at_hero(1500.0, cross, d, 9000.0, far_extra)
				await _save("outside_%d" % int(d / 1000.0))
		"perf":
			# A-B-A(-B-A): storm in view, High, clocks paused, day. The storm stays where PlaceStorm puts it relative to a fixed camera
			# (like the WO-A runs), so the numbers compare with the 2026-10-25 HEAD tables.
			var dist := float(_arg("perf-dist", "15000"))
			var center: Vector2 = cross * dist
			radius = float(_arg("storm-radius", "24000"))
			_storm(center)
			helper.PlaceCamera(world, 1500.0, _yaw(center.normalized()), rad_to_deg(atan2(9000.0 - 1500.0, center.length())))
			for case in _arg("perf-cases", "new,legacy,new,legacy,new,legacy").split(","):
				helper.SetLegacyShape(case == "legacy")
				# "clear": the same pose with the storm moved off (400 km across), so a storm-path delta can be separated from the scene's
				# natural weather.
				_storm(Vector2(0.0, 400000.0) if case == "clear" else center)
				await _frames(150)
				# "dfetch_off": the storm-light D's second map fetch (ownership / front rain layer) skipped by publishing the map frame with no
				# storm-light layer (w = 0; receivers then read the pre-2026-10-29 D). Cost A/B only; restored after the window.
				var fetch_w: float = helper.SetStormLightFetch(world, case != "dfetch_off")
				helper.PerfBegin(world)
				await create_timer(float(_arg("perf-seconds", "10"))).timeout
				print("STORM_ANVIL perf ", case, " ", helper.PerfEnd(world), " | storm_map_frame.w=%.0f" % fetch_w)
				if case == "dfetch_off":
					helper.SetStormLightFetch(world, true)
			await _save("perf_view")
		_:
			_fail("unknown shot " + shot)

func _fail(reason):
	if finished:
		return
	finished = true
	printerr("STORM_ANVIL=FAIL ", reason)
	quit(1)
