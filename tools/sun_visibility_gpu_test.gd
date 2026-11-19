extends SceneTree

## LF-1 sun glare GPU test (docs/sun-flare-shafts-near-fog-plan.md section A): the visible sun fraction F that scripts/render/ring_sun_visibility.glsl measures, checked against geometry,
## far depth, cloud, eclipse and storm cases, against an image oracle (disc-minus-annulus luminance of the resolved HDR colour before the glare is drawn, against the same sun clear),
## and the byte-identity / window / no-runaway contracts of the glare draw. Boots the pinned v8 fixture world (seed 1337) through tools/landscape_harness_common.gd.
## RUN (windowed Vulkan, not --headless; no other Godot process running): tools/run_tests.ps1 -Label lf1 -Tests sun_visibility_gpu_test
##   or Godot_console.exe --path . --resolution 1920x1080 --script res://tools/sun_visibility_gpu_test.gd -- --prefs=defaults --weather=clear [--explore=pose:time:pitch_offset,...]
## --explore prints the state buffer for the listed (pose, time, pitch offset) combinations and asserts nothing. Prints SUN_VISIBILITY_GPU=PASS.
## Test-only. The harness world directory is deleted at exit. Stills (only with --stills=<abs dir>) are written there.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const TVW := preload("res://tools/tree_visibility_world.gd")
const MARKER := "SUN_VISIBILITY_GPU"

var booted := {}
var game: Node
var explorer: Node
var atmosphere: Node
var cycle: Node
var director: Node
var glare: Node
var effect: Object
var world_environment: WorldEnvironment
var finished := false
var failures: Array = []
var report := {}
var pose_cache := {}   # pose name -> {around, vertical, across}


func _init() -> void:
	call_deferred("_run")


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	_cleanup()
	quit(1)


func _cleanup() -> void:
	Engine.time_scale = 1.0
	if booted.has("game") and is_instance_valid(booted["game"]):
		(booted["game"] as Node).queue_free()
	if booted.has("fixture_root"):
		HARNESS.remove_dir_recursive(ProjectSettings.globalize_path(str(booted["fixture_root"])))


func _frames(n: int) -> void:
	for i in n:
		await process_frame


## One readback of the state buffer (a test-only render-thread stall): {f, f_taps, clean_disc, clean_ann, hold, main_empty, far_empty, cloud, l_meas_lum, meas, meas_flag, disc_luma, ann_luma, disc_n, ann_n}.
func read_state() -> Dictionary:
	effect.call("request_state_readback")
	for i in 20:
		await process_frame
		var s: PackedFloat32Array = effect.call("get_last_state")
		if s.size() == 20:
			return {"f": s[0], "f_taps": s[1], "clean_disc": s[2], "clean_ann": s[3], "hold": Vector3(s[4], s[5], s[6]), "main_empty": s[8], "far_empty": s[9], "cloud": s[10],
				"l_meas_lum": s[11], "meas": Vector3(s[12], s[13], s[14]), "meas_flag": s[15], "disc_luma": s[16], "ann_luma": s[17], "disc_n": s[18], "ann_n": s[19]}
	return {}


func sun_dir() -> Vector3:
	return atmosphere.get("published_sun_direction")


## Teleports to pose values at yaw/pitch and lets the camera basis settle.
func teleport(p: Dictionary, yaw: float, pitch: float) -> void:
	explorer.call("TeleportToCanonicalMeters", p["around"], p["vertical"], p["across"], yaw, pitch, true)
	await _frames(10)


## Aims the camera at the sun (plus pitch_offset degrees: positive puts the sun that many degrees above the screen centre... negative offset looks above it). Returns the angular error in degrees.
func aim_at_sun(p: Dictionary, start_yaw: float, start_pitch: float, pitch_offset := 0.0) -> float:
	var yaw := start_yaw
	var pitch := start_pitch
	var sign_y := -1.0
	var last_err := 1.0e9
	var err := 1.0e9
	for iter in 8:
		await teleport(p, yaw, pitch)
		var camera := root.get_camera_3d()
		var local := camera.global_transform.basis.inverse() * sun_dir()
		var e_yaw := rad_to_deg(atan2(local.x, -local.z))
		var e_pitch := rad_to_deg(atan2(local.y, sqrt(local.x * local.x + local.z * local.z)))
		err = sqrt(e_yaw * e_yaw + (e_pitch + pitch_offset) * (e_pitch + pitch_offset))
		if err < 0.05:
			break
		if iter > 0 and err > last_err * 1.05:
			sign_y = -sign_y
		last_err = err
		yaw += sign_y * e_yaw
		pitch = clampf(pitch + e_pitch + pitch_offset, -89.0, 89.0)
	return err


func go(pose_name: String, time_name: String) -> Dictionary:
	var pose := HARNESS.pose_by_name(pose_name)
	var ground_model: RefCounted = null if str(booted["world_tag"]).is_empty() else booted["world_bridge"]
	var info: Dictionary = await HARNESS.go_pose(self, game, pose, time_name, ground_model)
	if info.has("error"):
		_fail(str(info["error"]))
		return {}
	var reached: Dictionary = info["pose_reached"]["requested"]
	pose_cache[pose_name] = {"around": reached["around_m"], "vertical": reached["vertical_m"], "across": reached["across_m"], "yaw": reached["yaw_deg"], "pitch": reached["pitch_deg"]}
	return pose_cache[pose_name]


func measure(label: String, pose_name: String, time_name: String, pitch_offset := 0.0) -> Dictionary:
	var p: Dictionary = await go(pose_name, time_name)
	if p.is_empty():
		return {}
	var err := await aim_at_sun(p, p["yaw"], p["pitch"], pitch_offset)
	await _frames(6)
	var s := await read_state()
	s["aim_err_deg"] = err
	s["label"] = label
	s["sun_elevation_deg"] = float(cycle.get("SunElevationDegrees"))
	s["eclipse"] = float(cycle.get("RawEclipseCoverage"))
	s["storm_hide"] = float(glare.get("LastStormHide"))
	print("%s MEASURE %s" % [MARKER, JSON.stringify(s)])
	return s


func _run() -> void:
	var watchdog := create_timer(1500.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (1500 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var res := HARNESS.COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		root.size = Vector2i(int(res[0]), int(res[1]))
	print("%s %s" % [MARKER, HARNESS.COMMON.machine_info()["label"]])
	booted = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		quit(1)
		return
	game = booted["game"]
	explorer = game.get_node("Explorer")
	atmosphere = game.get_node("DaySideAtmosphere")
	cycle = game.get_node("RingDayNightCycle")
	director = game.get_node_or_null("EclipseEvent")
	glare = game.get_node_or_null("SunGlare")
	world_environment = game.get_node("WorldEnvironment")
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	HARNESS.COMMON.hold_world_clocks(game)
	var weather_pin := HARNESS.pin_weather(game, HARNESS.report_poses("t0"))
	if weather_pin.has("error"):
		_fail(str(weather_pin["error"]))
		return
	var preset: Dictionary = await HARNESS.apply_preset(self, game)
	if preset.has("error"):
		_fail(str(preset["error"]))
		return
	if glare == null or glare.get("Effect") == null:
		await _frames(30)
	effect = glare.get("Effect") if glare != null else null
	if effect == null:
		_fail("SunGlare node or its compositor effect is missing")
		return
	# Native resolution and no temporal upscaler or TAA: the stills must be byte-comparable and the window maths is in output pixels (FSR2 / TAA jitter makes every frame differ).
	root.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
	root.scaling_3d_scale = 1.0
	root.use_taa = false
	root.msaa_3d = Viewport.MSAA_DISABLED
	await _frames(20)
	var attributes := world_environment.camera_attributes as CameraAttributesPractical
	if attributes != null and attributes.auto_exposure_enabled:
		attributes.auto_exposure_enabled = false
	var explore := HARNESS.COMMON.arg_value("--explore=", "")
	if explore != "":
		for item in explore.split(","):
			var parts := item.split(":")
			await measure(item, parts[0], parts[1], float(parts[2]) if parts.size() > 2 else 0.0)
		_cleanup()
		finished = true
		print("%s=PASS explore" % MARKER)
		quit(0)
		return
	await _cases()


func check(ok: bool, message: String) -> void:
	if not ok:
		failures.append(message)
		print("%s CHECK FAILED %s" % [MARKER, message])


## (disc - annulus) luminance of the resolved HDR colour before the glare draw, the image oracle.
func excess(s: Dictionary) -> float:
	return float(s["disc_luma"]) - float(s["ann_luma"])


## Reference excess of a clear sun: a measurement taken where the sun is on screen and clear, divided by its F_taps (clouds in front of the reference are corrected).
func clear_excess(ref: Dictionary) -> float:
	return excess(ref) / maxf(float(ref["f_taps"]), 0.05)


func oracle(label: String, s: Dictionary, ref: Dictionary) -> void:
	if ref.is_empty() or float(ref["f_taps"]) < 0.3 or excess(ref) <= 0.05:
		print("%s ORACLE %s n/a (reference F_taps %.2f)" % [MARKER, label, float(ref.get("f_taps", -1.0))])
		report["oracle_" + label] = "n/a"
		return
	var value := excess(s) / clear_excess(ref)
	report["oracle_" + label] = {"oracle": value, "f": s["f"]}
	print("%s ORACLE %s image=%.3f F=%.3f diff=%.3f %s" % [MARKER, label, value, float(s["f"]), absf(value - float(s["f"])), "ok" if absf(value - float(s["f"])) <= 0.1 else "REPORT: beyond 0.1"])


func rows_equal(a: Image, b: Image, window: Rect2i) -> Dictionary:
	var w := a.get_width()
	var h := a.get_height()
	var da := a.get_data()
	var db := b.get_data()
	var stride := w * 4
	var outside_diff := 0
	var inside_diff_rows := 0
	for y in h:
		var row_a := da.slice(y * stride, (y + 1) * stride)
		var row_b := db.slice(y * stride, (y + 1) * stride)
		if row_a == row_b:
			continue
		if y < window.position.y or y >= window.position.y + window.size.y:
			outside_diff += 1
			continue
		inside_diff_rows += 1
		var x0 := window.position.x * 4
		var x1 := (window.position.x + window.size.x) * 4
		if row_a.slice(0, x0) != row_b.slice(0, x0) or row_a.slice(x1) != row_b.slice(x1):
			outside_diff += 1
	return {"outside_diff_rows": outside_diff, "inside_diff_rows": inside_diff_rows}


func stills_dir() -> String:
	return HARNESS.COMMON.arg_value("--stills=", "")


func still(img: Image, name: String) -> void:
	var dir := stills_dir()
	if dir == "":
		return
	DirAccess.make_dir_recursive_absolute(dir)
	img.save_png(dir.path_join(name + ".png"))


func shot() -> Image:
	await RenderingServer.frame_post_draw
	return TVW.grab(self)


func _cases() -> void:
	var t0 := Time.get_ticks_msec()
	if HARNESS.COMMON.has_flag("--only-eclipse"):
		await _eclipse_case(await measure("noon_centred", "plains_eye", "noon"))
		_cleanup()
		finished = true
		print("%s %s" % [MARKER, "ONLY-ECLIPSE " + ("FAIL " + " | ".join(failures) if not failures.is_empty() else "PASS")])
		quit(0)
		return
	# ---- 1. clear sky, sun on screen: centred and 14 deg off centre
	var centred := await measure("noon_centred", "plains_eye", "noon")
	var off14 := await measure("noon_off14", "plains_eye", "noon", -14.0)
	check(float(centred["f"]) >= 0.97, "case 1 noon centred F %.3f < 0.97" % float(centred["f"]))
	check(float(off14["f"]) >= 0.97, "case 1 noon 14 deg off centre F %.3f < 0.97" % float(off14["f"]))
	check(float(centred["clean_disc"]) >= 8 and float(centred["clean_ann"]) >= 4, "noon measurement not clean")
	var noon_l := float(centred["l_meas_lum"])
	check(noon_l > 1.9 and noon_l < 3.2, "noon displayed disc radiance %.2f is not near sun_color x 2.55" % noon_l)
	report["noon_disc_luminance"] = noon_l
	var ref_noon := centred
	oracle("noon_centred", centred, ref_noon)
	oracle("noon_off14", off14, ref_noon)

	# ---- 2. behind a ridge (hills at sun 2 deg: main depth occludes the disc)
	var ridge := await measure("ridge", "hills_eye", "sun_2_set")
	check(float(ridge["f"]) <= 0.03, "case 2 ridge F %.3f > 0.03" % float(ridge["f"]))
	check(float(ridge["main_empty"]) <= 0.1, "case 2 ridge: main depth is empty at the sun (%.2f), the ridge did not do the work" % float(ridge["main_empty"]))
	var ref8 := await measure("ref_plains_8", "plains_eye", "sun_8_set")
	oracle("ridge", ridge, ref8 if float(ref8["f_taps"]) > 0.5 else ref_noon)

	# ---- 3. behind the far arch / far terrain: the camera looks along the ring; the sun direction is forced to the first pitch (10 to 25 deg) where main depth is empty and the
	# scaled far viewport has geometry (the sun itself is never behind the arch on a real day; the visibility pass is a function of the direction only).
	var p: Dictionary = pose_cache["plains_eye"]
	var far_case := {}
	var far_pitch := 0.0
	for pitch in [10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 22.0, 25.0]:
		await teleport(p, 0.0, pitch)
		var cam := root.get_camera_3d()
		glare.set("ForcedSunDirection", -cam.global_transform.basis.z)
		await _frames(6)
		var f := await read_state()
		print("%s FAR pitch %.0f F=%.3f main_empty=%.2f far_empty=%.2f" % [MARKER, pitch, float(f["f"]), float(f["main_empty"]), float(f["far_empty"])])
		if float(f["main_empty"]) >= 0.95 and float(f["far_empty"]) <= 0.05:
			far_case = f
			far_pitch = pitch
			break
	glare.set("ForcedSunDirection", Vector3.ZERO)
	check(not far_case.is_empty(), "case 3: no pitch in 10-25 deg along the ring has main depth empty and far depth occupied")
	if not far_case.is_empty():
		report["far_pitch"] = far_pitch
		check(float(far_case["f"]) <= 0.03, "case 3 far arch F %.3f > 0.03" % float(far_case["f"]))

	# ---- 4. inside a cloud (coast at sun 8 deg: a cloud covers the sun; main and far depth are empty there)
	var cloud := await measure("cloud", "coast_eye", "sun_8_set")
	check(float(cloud["f"]) <= 0.05, "case 4 cloud F %.3f > 0.05" % float(cloud["f"]))
	check(float(cloud["main_empty"]) >= 0.95 and float(cloud["far_empty"]) >= 0.95, "case 4: geometry occludes the sun (main %.2f far %.2f), the cloud did not do the work" % [float(cloud["main_empty"]), float(cloud["far_empty"])])
	check(float(cloud["cloud"]) <= 0.05, "case 4 cloud term %.3f > 0.05" % float(cloud["cloud"]))
	oracle("cloud", cloud, ref8 if float(ref8["f_taps"]) > 0.5 else ref_noon)

	# ---- 8 and 9, perf, stills (clear noon centred)
	await _contracts()
	# ---- 6. under the pinned closed storm (it replaces the weather pin), then clear weather again for the eclipse
	await _storm_case(ref_noon)
	var repin: Dictionary = HARNESS.pin_weather(game, HARNESS.report_poses("t0"))
	if repin.has("error"):
		check(false, "clear weather re-pin: " + str(repin["error"]))
	await HARNESS.COMMON.settle(self, 6.0)
	await HARNESS.COMMON.wait_builds_idle(self, game, 120.0)
	# ---- 5. eclipse crescent in a cloud-free window (last: it leaves the clock inside the event)
	await _eclipse_case(ref_noon)

	report["seconds"] = (Time.get_ticks_msec() - t0) / 1000.0
	print("%s REPORT %s" % [MARKER, JSON.stringify(report)])
	_cleanup()
	finished = true
	if failures.is_empty():
		print("%s=PASS" % MARKER)
		quit(0)
	else:
		push_error("%s=FAIL %s" % [MARKER, " | ".join(failures)])
		quit(1)


func _eclipse_case(ref: Dictionary) -> void:
	if director == null:
		check(false, "case 5: no EclipseEvent director")
		return
	await go("plains_eye", "noon")
	director.call("JumpToPhase", 2)
	await _frames(6)
	var t_total := float(cycle.call("GetSimulationSeconds"))
	var tried := 0
	var best := {}
	var seen_min := 2.0
	var seen_max := -1.0
	var candidates: Array = []
	for k in range(100, 500, 2):
		for sgn in [-1.0, 1.0]:
			var t: float = t_total + sgn * float(k)
			cycle.call("SetSimulationSeconds", t)
			await process_frame
			var coverage := float(cycle.get("RawEclipseCoverage"))
			seen_min = minf(seen_min, coverage)
			seen_max = maxf(seen_max, coverage)
			if coverage >= 0.3 and coverage <= 0.7 and float(cycle.get("DaylightFactor")) >= 0.3:
				candidates.append(t)
	print("%s ECLIPSE scan t_total=%.0f coverage seen %.3f..%.3f candidates=%d" % [MARKER, t_total, seen_min, seen_max, candidates.size()])
	check(not candidates.is_empty(), "case 5: no crescent time with coverage 0.3-0.7 at plains_eye")
	if candidates.is_empty():
		return
	candidates.sort()
	var t_mid := float(candidates[candidates.size() / 2])
	cycle.call("SetSimulationSeconds", t_mid)
	var p: Dictionary = pose_cache["plains_eye"]
	var clouds := game.get_node_or_null("RingVolumetricClouds")
	# Clouds are weather time: walk the clear-weather candidate times until the crescent is in a cloud-free window.
	for k in range(36, 140):
		var cloud_time: float = HARNESS.COMMON.FIXED_CLOUD_TIME + 600.0 * k
		clouds.set("CloudSimulationTimeSeconds", cloud_time)
		await _frames(4)
		await aim_at_sun(p, p["yaw"], p["pitch"])
		await HARNESS.COMMON.settle(self, 1.0)
		await aim_at_sun(p, p["yaw"], p["pitch"])
		await _frames(6)
		var s := await read_state()
		tried += 1
		var c := float(cycle.get("RawEclipseCoverage"))
		print("%s ECLIPSE cloud_time=%.0f t=%.0f coverage=%.3f F=%.3f taps=%.3f main=%.2f far=%.2f cloud=%.2f" % [MARKER, cloud_time, t_mid, c, float(s["f"]), float(s["f_taps"]), float(s["main_empty"]), float(s["far_empty"]), float(s["cloud"])])
		if float(s["f_taps"]) >= 0.95 and c >= 0.3 and c <= 0.7:
			s["coverage"] = c
			best = s
			break
	check(not best.is_empty(), "case 5: no cloud-free crescent window found in %d tries" % tried)
	if best.is_empty():
		return
	var expected := float(best["f_taps"]) * (1.0 - float(best["coverage"]))
	report["eclipse"] = {"coverage": best["coverage"], "f": best["f"], "f_taps": best["f_taps"], "expected": expected}
	check(absf(float(best["f"]) - expected) <= 0.03, "case 5 eclipse F %.3f vs F_taps x (1 - coverage) %.3f differs by more than 0.03" % [float(best["f"]), expected])
	oracle("eclipse", best, ref)


func _storm_case(ref: Dictionary) -> void:
	var pin: Dictionary = HARNESS._pin_storm(game, HARNESS.report_poses("t0"))
	if pin.has("error"):
		check(false, "case 6 storm pin: " + str(pin["error"]))
		return
	print("%s STORM pinned cloud_time=%.1f" % [MARKER, float(pin.get("cloud_time", -1.0))])
	await HARNESS.COMMON.settle(self, 6.0)
	await HARNESS.COMMON.wait_builds_idle(self, game, 120.0)
	var s := await measure("storm", "plains_eye", "noon")
	if s.is_empty():
		return
	check(float(s["storm_hide"]) <= 0.1, "case 6: the CPU storm hide %.3f is not closed (the pin did not produce a closed storm here)" % float(s["storm_hide"]))
	check(float(s["f"]) <= 0.02, "case 6 storm F %.3f > 0.02" % float(s["f"]))
	oracle("storm", s, ref)


func _contracts() -> void:
	var p: Dictionary = pose_cache["plains_eye"]
	await go("plains_eye", "noon")
	await aim_at_sun(p, p["yaw"], p["pitch"])
	TVW.freeze_world(game)
	var cap: Dictionary = await TVW.capture_begin(self, game)
	print("%s CAPTURE %s" % [MARKER, str(cap)])
	if not bool(cap.get("deterministic", false)):
		var fa := await shot()
		var fb := await shot()
		var rows := []
		var cols_min := 99999
		var cols_max := -1
		var ba := fa.get_data()
		var bb := fb.get_data()
		var stride := fa.get_width() * 4
		for y in fa.get_height():
			if ba.slice(y * stride, (y + 1) * stride) != bb.slice(y * stride, (y + 1) * stride):
				rows.append(y)
				if rows.size() % 40 == 1:
					for x in fa.get_width():
						if fa.get_pixel(x, y) != fb.get_pixel(x, y):
							cols_min = mini(cols_min, x)
							cols_max = maxi(cols_max, x)
		print("%s CAPTURE_DIFF rows=%d first=%s last=%s cols=%d..%d taa=%s fsr=%s scale=%.2f msaa=%d" % [MARKER, rows.size(), str(rows.front() if rows.size() > 0 else -1), str(rows.back() if rows.size() > 0 else -1), cols_min, cols_max, str(root.use_taa), str(root.scaling_3d_mode), root.scaling_3d_scale, root.msaa_3d])
		var dir := stills_dir()
		if dir != "":
			DirAccess.make_dir_recursive_absolute(dir)
			fa.save_png(dir.path_join("capture_diff_a.png"))
			fb.save_png(dir.path_join("capture_diff_b.png"))
	check(bool(cap.get("deterministic", false)), "capture mode did not reach byte-identical frames")
	var scale := root.scaling_3d_scale
	# glare ON
	await _frames(6)
	var on := await shot()
	var window: Rect2i = effect.call("get_last_window")
	var window_px := Rect2i(Vector2i(Vector2(window.position) / scale), Vector2i(Vector2(window.size) / scale))
	# strength 0: effect detached
	_set_strength(0.0)
	await _frames(8)
	var zero := await shot()
	var zero2 := await shot()
	# effect absent
	_set_strength(1.0)
	glare.set("AllowAttach", false)
	await _frames(8)
	var absent := await shot()
	glare.set("AllowAttach", true)
	await _frames(8)
	var on2 := await shot()
	check(zero.get_data() == absent.get_data(), "case 8: strength 0 is not byte-identical to the effect absent")
	check(zero.get_data() == zero2.get_data(), "case 8: two strength 0 frames differ (capture not deterministic)")
	check(on.get_data() == on2.get_data(), "case 8: two glare-on frames differ")
	check(on.get_data() != zero.get_data(), "case 8: glare ON equals OFF (nothing drawn at the clear noon sun)")
	var diff := rows_equal(on, zero, window_px)
	report["window"] = {"rect": window_px, "outside_diff_rows": diff["outside_diff_rows"], "inside_diff_rows": diff["inside_diff_rows"]}
	check(int(diff["outside_diff_rows"]) == 0, "case 8: glare ON differs from OFF outside the computed window (%d rows)" % int(diff["outside_diff_rows"]))
	print("%s WINDOW %s %s" % [MARKER, str(window_px), str(diff)])
	# effect attached but inactive (sun off screen): byte identical to the effect absent
	var q := {"around": p["around"], "vertical": p["vertical"], "across": p["across"]}
	await teleport(q, 0.0, -60.0)
	await _frames(8)
	var inactive := await shot()
	glare.set("AllowAttach", false)
	await _frames(8)
	var inactive_absent := await shot()
	glare.set("AllowAttach", true)
	check(inactive.get_data() == inactive_absent.get_data(), "case 8: effect attached with the sun off screen is not byte-identical to the effect absent")
	check(not bool(glare.get("Active")), "case 8: the sun is off screen but the effect is still active")
	# stills + the L_hold run (case 9)
	await aim_at_sun(p, p["yaw"], p["pitch"])
	await _frames(8)
	still(on, "clear_noon_centre_glare_on")
	still(zero, "clear_noon_centre_glare_off")
	var holds_on: Array = []
	var holds_off: Array = []
	glare.set("AllowAttach", true)
	for i in 30:
		var s := await read_state()
		holds_on.append(s["hold"])
	_set_gain(0.0)
	for i in 30:
		var s2 := await read_state()
		holds_off.append(s2["hold"])
	_set_gain(1.0)
	var worst := 0.0
	var drift := 0.0
	for i in 30:
		var a: Vector3 = holds_on[i]
		var b: Vector3 = holds_off[i]
		worst = maxf(worst, (a - b).length() / maxf(b.length(), 1e-6))
		drift = maxf(drift, ((holds_on[i] as Vector3) - (holds_on[0] as Vector3)).length() / maxf((holds_on[0] as Vector3).length(), 1e-6))
	report["l_hold"] = {"on_vs_off_worst": worst, "drift_over_30_frames": drift, "first": holds_on[0]}
	print("%s LHOLD sequence on %s off %s" % [MARKER, str(holds_on.slice(0, 6)), str(holds_off.slice(0, 6))])
	print("%s LHOLD on-vs-off worst %.5f drift %.5f" % [MARKER, worst, drift])
	check(worst <= 0.01, "case 9: L_hold with glare on differs from glare off by %.4f (> 1 %%)" % worst)
	check(drift <= 0.01, "case 9: L_hold drifted by %.4f over 30 frames" % drift)
	TVW.capture_end(game)
	Engine.time_scale = 1.0
	await _perf()
	await _gain_stills()


func _set_strength(value: float) -> void:
	glare.call("DebugSetStrength", value)


func _set_gain(value: float) -> void:
	glare.call("DebugSetGain", value)


func _perf() -> void:
	var rid := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(rid, true)
	var windows := [1.0, 0.0, 1.0]
	var means := []
	for w in windows:
		_set_strength(w)
		await _frames(30)
		var sum := 0.0
		var n := 0
		var until := Time.get_ticks_msec() + 3000
		while Time.get_ticks_msec() < until:
			await process_frame
			sum += RenderingServer.viewport_get_measured_render_time_gpu(rid)
			n += 1
		means.append(sum / maxf(n, 1))
	_set_strength(1.0)
	RenderingServer.viewport_set_measure_render_time(rid, false)
	report["gpu_ms_aba_on_off_on"] = means
	print("%s PERF root viewport GPU ms (strength 1, 0, 1): %s (informational; 4070 indicative)" % [MARKER, str(means)])


func _gain_stills() -> void:
	if stills_dir() == "":
		return
	var p: Dictionary = pose_cache["plains_eye"]
	await go("plains_eye", "sun_8_set")
	await aim_at_sun(p, p["yaw"], p["pitch"])
	TVW.hide_overlays(game)
	TVW.freeze_world(game)
	await TVW.capture_begin(self, game)
	for item in [["low_sun_8_glare_on", 1.0], ["low_sun_8_gain30_DEBUG_ONLY", 30.0], ["low_sun_8_gain1000_DEBUG_ONLY", 1000.0]]:
		_set_gain(float(item[1]))
		await _frames(8)
		still(await shot(), str(item[0]))
	_set_gain(1.0)
	_set_strength(0.0)
	await _frames(8)
	still(await shot(), "low_sun_8_glare_off")
	_set_strength(1.0)
	TVW.capture_end(game)
