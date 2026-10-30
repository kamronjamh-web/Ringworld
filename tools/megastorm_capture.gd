extends SceneTree
# Megastorm captures (docs/megastorm-design.md WO4 near wall; WO9 extends this harness). Windowed GPU run, certified world.
#   Godot --path . --resolution 1600x900 --script res://tools/megastorm_capture.gd -- --out=res://.godot/captures/megastorm/wo4/<label> --shots=inside,leading
# Args: --out=<res dir> --shots=<list> [--cycle=<0..1> (0.6 noon)] [--no-haze] [--matte] [--speed=<m/s> (350)] [--seed=<n> (7)] [--rain] [--deterministic]
#       [--settle=<frames> (120)] [--motion-frames=<n> (420)] [--motion-from=<km> (-3)] [--motion-yaw=<deg> (180)] [--motion-pitch=<deg> (20)]
#       [--orbit-frames=<n> (300)] [--perf-seconds=<s> (10)] [--trigger-xi=<m> (10000): where the wall is when it is triggered]
#       [--perf-cases=<list> (clear,natural,inside,leading x3)] [--set=Prop=value (bridge)]
# The camera stays at the StormAnvilCapture base position (420 m, terrain stays streamed); the megastorm (fully formed, +around) is moved
# relative to it by rewriting its anchor (MegastormCapture.SetXi), with clocks paused. yaw 0 = +around = the wall's travel direction, so
# the wall is behind the camera (yaw pi) when the camera is ahead of it (xi < 0).
# Shots:
#   inside    xi +10 km: up (pitch 80), forward (yaw 0 = towards the leading edge, pitch 8), back (yaw pi, pitch 8)
#   leading   xi -1.5 km, under the anvil just ahead of the shelf: towards the wall (yaw pi) pitch 15 and 40, straight up (85)
#   ahead15   xi -15 km: towards the wall, pitch 3 and 15
#   ahead40   xi -40 km: towards the wall, pitch 3 and 12
#   rim       camera 4 km from the rim wall (across), 1.5 km up: xi -20 km looking at the wall along the rim (yaw pi and pi + 0.5), and xi +8 km
#             looking at the rim wall (yaw -pi/2) and up (rim_*); the camera then returns to the base position
#   motion    fixed ground camera through the edge passage at --speed: the weather clock steps 1/30 s per saved frame (frame%08d.png in
#             <out>/motion), xi from --motion-from km, towards the wall (yaw pi, pitch 20). 60 warm-up steps fill the history first.
#   orbit     2 km up at xi -2.5 km under the shelf: a 360 degree yaw orbit over --orbit-frames frames while the wall moves (orbit/frame%08d)
#   prewarm   (with --trigger-xi=-600000) the megastorm march variants exist before the wall comes near; longest frame while they compile
#             and on the approach (WO4b)
#   perf      GPU windows: clear (megastorm 600 km away: triggered, beyond the selection radius), natural (the same plus the S4 preview storm
#             over the camera, the "natural storm at the same pose"), inside (xi +10 km, yaw 0, pitch 10), leading (xi -1.5 km, yaw pi, pitch 15)

var world: Node
var helper: RefCounted
var mega: RefCounted
var out_dir := ""
var settle := 120
var finished := false
const BASE_H := 420.0

func _initialize():
	create_timer(2400.0).timeout.connect(func(): _fail("2400 second watchdog expired"))
	call_deferred("_run")

func _arg(name: String, default_value: String) -> String:
	for a in OS.get_cmdline_user_args():
		if a == "--" + name:
			return "1"
		if a.begins_with("--" + name + "="):
			return a.substr(name.length() + 3)
	return default_value

func _frames(n: int):
	for i in n:
		await process_frame

func _run():
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice")
		return
	out_dir = _arg("out", "res://.godot/captures/megastorm/wo4/test")
	settle = int(_arg("settle", "120"))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir))
	var far = load("res://tools/FarRingGameplayCapture.cs").new()
	if not far.ConfigureArguments():
		_fail(far.ErrorText)
		return
	helper = load("res://tools/StormAnvilCapture.cs").new()
	mega = load("res://tools/MegastormCapture.cs").new()
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
	if not helper.Setup(world, float(_arg("cycle", "0.6")), BASE_H):
		_fail(helper.ErrorText)
		return
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	await create_timer(float(_arg("warmup", "25"))).timeout
	for n in world.get_children():
		if n is CanvasLayer:
			n.visible = false
	for n in root.get_children():
		if n is CanvasLayer:
			n.visible = false
	if _arg("rain", "") == "":
		print("MEGASTORM_CAPTURE rain_off nodes=", helper.RainOff(world))
	var bridge = world.get_node("SunshineCloudBridge")
	if _arg("deterministic", "") != "":
		bridge.set("RayStartJitterStrength", 0.0)
		bridge.set("CloudResolutionJitter", false)
		bridge.set("DitherSpeed", 0.0)
		bridge.call("ApplyTemporalQualityTuning", true)
		bridge.call("SetDiagnosticTemporalHistoryDisabled", true)
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--set="):
			var kv := a.substr(6).split("=")
			if kv.size() == 2:
				bridge.set(kv[0], float(kv[1]))
				print("MEGASTORM_CAPTURE set ", kv[0], "=", kv[1])
	if _arg("no-haze", "") != "":
		bridge.call("DebugSetCloudAtmosphericDensity", 0.0)
	if _arg("matte", "") != "":
		bridge.call("SetDiagnosticDensityMattes", true, false)
	helper.PlaceCamera(world, BASE_H, 0.0, 8.0)
	await _frames(60)
	if not mega.Trigger(world, float(_arg("trigger-xi", "10000")), float(_arg("speed", "350")), int(_arg("seed", "7"))):
		_fail(mega.ErrorText)
		return
	# The 90 frames after the trigger are timed: the megastorm prewarm (WO4b) runs in them (prewarm shot).
	var trigger_longest := 0.0
	var trigger_last := Time.get_ticks_usec()
	for i in 90:
		await process_frame
		var trigger_now := Time.get_ticks_usec()
		trigger_longest = maxf(trigger_longest, float(trigger_now - trigger_last) / 1000.0)
		trigger_last = trigger_now
	print("MEGASTORM_CAPTURE triggered xi=", mega.Xi(world), " ", mega.StripState(world), " longest_frame_ms=%.1f" % trigger_longest)
	for shot in _arg("shots", "inside").split(","):
		await _shoot(shot)
	print("MEGASTORM_CAPTURE ", mega.StripState(world))
	print("MEGASTORM_CAPTURE=PASS out=", out_dir)
	finished = true
	quit(0)

func _save(name: String):
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	var path := out_dir + "/" + name + ".png"
	image.save_png(ProjectSettings.globalize_path(path))
	print("MEGASTORM_CAPTURE saved ", name, " xi=%.0f" % mega.Xi(world))

func _view(xi: float, yaw: float, pitch: float, height := BASE_H, frames := -1):
	mega.SetXi(world, xi)
	helper.PlaceCamera(world, height, yaw, pitch)
	await _frames(settle if frames < 0 else frames)

func _shoot(shot: String):
	match shot:
		"inside":
			await _view(10000.0, 0.0, 80.0)
			await _save("inside_up")
			await _view(10000.0, 0.0, 8.0, BASE_H, 60)
			await _save("inside_forward")
			await _view(10000.0, PI, 8.0, BASE_H, 60)
			await _save("inside_back")
		"leading":
			await _view(-1500.0, PI, 15.0)
			await _save("leading_p15")
			await _view(-1500.0, PI, 40.0, BASE_H, 60)
			await _save("leading_p40")
			await _view(-1500.0, PI, 85.0, BASE_H, 60)
			await _save("leading_up")
		"ahead15":
			await _view(-15000.0, PI, 3.0)
			await _save("ahead15_p03")
			await _view(-15000.0, PI, 15.0, BASE_H, 60)
			await _save("ahead15_p15")
		"ahead40":
			await _view(-40000.0, PI, 3.0)
			await _save("ahead40_p03")
			await _view(-40000.0, PI, 12.0, BASE_H, 60)
			await _save("ahead40_p12")
		"rim":
			# 4 km from the rim wall at across 0 (the base position is mid-ring, so this streams new terrain: long settle).
			var across_offset := 4000.0 - 119474.33742621914
			helper.PlaceCameraAt(world, 0.0, across_offset, 1500.0, PI, 10.0)
			mega.SetXi(world, -20000.0)
			await create_timer(20.0).timeout
			await _frames(settle)
			await _save("rim_ahead20_y180")
			helper.PlaceCameraAt(world, 0.0, across_offset, 1500.0, PI + 0.5, 10.0)
			await _frames(60)
			await _save("rim_ahead20_y209")
			mega.SetXi(world, 8000.0)
			helper.PlaceCameraAt(world, 0.0, across_offset, 1500.0, -PI * 0.5, 10.0)
			await _frames(settle)
			await _save("rim_inside_wall")
			helper.PlaceCameraAt(world, 0.0, across_offset, 1500.0, -PI * 0.5, 70.0)
			await _frames(60)
			await _save("rim_inside_up")
			helper.PlaceCamera(world, BASE_H, 0.0, 8.0)
			await create_timer(10.0).timeout
		"probe":
			# Density probes (Sunshine point samples) over the camera column at xi +10 km and -30 km (canopy), plus the record / frame state.
			for xi in [10000.0, -30000.0]:
				await _view(xi, 0.0, 8.0, BASE_H, 60)
				print("MEGASTORM_CAPTURE records ", mega.Records(world))
				var clouds_res = world.get_node("SunshineCloudBridge").GetSunshineCloudsForTests()
				for h in [800.0, 2000.0, 4000.0, 8000.0, 10500.0, 13000.0, 16000.0]:
					var got := [false, 0.0]
					clouds_res.add_sample(func(_p, d): got[0] = true; got[1] = d, mega.WorldAtCamera(world, h))
					for i in 30:
						await process_frame
						if got[0]:
							break
					print("MEGASTORM_CAPTURE probe xi=%.0f h=%.0f density=%s" % [xi, h, str(got[1]) if got[0] else "none"])
		"dbg":
			var br = world.get_node("SunshineCloudBridge")
			for mask_text in _arg("dbg-masks", "0,1024,2048,4096").split(","):
				var m := int(mask_text)
				br.set("CameraIndependentDebugMask", m)
				await _view(-15000.0, 0.0, 3.0, BASE_H, 90)
				await _save("dbg_ahead15y0_%d" % m)
				await _view(-15000.0, PI, 3.0, BASE_H, 90)
				await _save("dbg_ahead15_%d" % m)
			br.set("CameraIndependentDebugMask", 0)
			for yaw in [0.0, PI]:
				await _view(-15000.0, yaw, 3.0, BASE_H, 90)
				print("MEGASTORM_CAPTURE axes yaw=", yaw, " ", mega.CameraAxes(world))
				await _save("dbg_ahead15_yaw%d" % int(round(rad_to_deg(yaw))))
		"probeline":
			await _view(-15000.0, PI, 3.0, BASE_H, 60)
			var clouds_res = world.get_node("SunshineCloudBridge").GetSunshineCloudsForTests()
			for d in [0.0, 5000.0, 10000.0, 14000.0, 16000.0, 20000.0, 30000.0, 45000.0]:
				for h in [2000.0, 6000.0]:
					var got := [false, 0.0]
					clouds_res.add_sample(func(_p, dd): got[0] = true; got[1] = dd, mega.WorldAtOffset(world, -d, h))
					for i in 30:
						await process_frame
						if got[0]:
							break
					print("MEGASTORM_CAPTURE probeline d=%.0f h=%.0f density=%s" % [d, h, str(got[1]) if got[0] else "none"])
		"motion":
			var dir := out_dir + "/motion"
			DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
			var frames := int(_arg("motion-frames", "420"))
			await _view(float(_arg("motion-from", "-3")) * 1000.0 - 60.0 / 30.0 * float(_arg("speed", "350")),
				deg_to_rad(float(_arg("motion-yaw", "180"))), float(_arg("motion-pitch", "20")))
			for i in 60:
				mega.StepClock(world, 1.0 / 30.0)
				await process_frame
			for i in frames:
				mega.StepClock(world, 1.0 / 30.0)
				await RenderingServer.frame_post_draw
				root.get_texture().get_image().save_png(ProjectSettings.globalize_path(dir + "/frame%08d.png" % i))
			print("MEGASTORM_CAPTURE motion frames=", frames, " end xi=%.0f" % mega.Xi(world))
		"orbit":
			var dir := out_dir + "/orbit"
			DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
			var frames := int(_arg("orbit-frames", "300"))
			await _view(-2500.0, PI, 25.0, 2000.0)
			for i in frames:
				helper.PlaceCamera(world, 2000.0, PI + TAU * float(i) / float(frames), 25.0)
				mega.StepClock(world, 1.0 / 30.0)
				await RenderingServer.frame_post_draw
				root.get_texture().get_image().save_png(ProjectSettings.globalize_path(dir + "/frame%08d.png" % i))
			print("MEGASTORM_CAPTURE orbit frames=", frames)
		"prewarm":
			# First-approach hitch (WO4b): the megastorm march variants must exist before the wall is near. Moves the (already triggered)
			# megastorm 600 km away, then lists the megastorm variants (keys >= 1000) and the prewarm compile time, then brings the wall to
			# 50 km and reports the longest frame of the approach.
			var sun_res = world.get_node("SunshineCloudBridge").GetSunshineCloudsForTests()
			await _view(-600000.0, PI, 10.0, BASE_H, 90)
			var wait_longest := 0.0
			var wait_last := Time.get_ticks_usec()
			for i in 1200:
				var have := 0
				for k in sun_res.get("interleave_main_pipelines").keys():
					if int(k) >= 1000:
						have += 1
				if have >= 2:
					break
				await process_frame
				var wait_now := Time.get_ticks_usec()
				wait_longest = maxf(wait_longest, float(wait_now - wait_last) / 1000.0)
				wait_last = wait_now
			print("MEGASTORM_CAPTURE prewarm wait longest_frame_ms=%.1f" % wait_longest)
			var keys := []
			for k in sun_res.get("interleave_main_pipelines").keys():
				if int(k) >= 1000:
					keys.append(int(k))
			print("MEGASTORM_CAPTURE prewarm far variants=", keys, " compile_ms=%.1f" % (float(sun_res.get("megastorm_prewarm_usec")) / 1000.0), " ", mega.StripState(world))
			mega.SetXi(world, -50000.0)
			var longest := 0.0
			var last := Time.get_ticks_usec()
			for i in 60:
				await process_frame
				var now := Time.get_ticks_usec()
				longest = maxf(longest, float(now - last) / 1000.0)
				last = now
			print("MEGASTORM_CAPTURE prewarm approach longest_frame_ms=%.1f" % longest, " compile_ms=%.1f" % (float(sun_res.get("megastorm_prewarm_usec")) / 1000.0), " ", mega.StripState(world))
		"perf":
			var cases := _arg("perf-cases", "clear,natural,inside,leading,clear,natural,inside,leading,clear,natural,inside,leading").split(",")
			for case in cases:
				var natural: bool = case == "natural"
				if natural:
					helper.PlaceStorm(world, 0.0, 0.0, 0.0, 24000.0)
				else:
					world.get_node("RingVolumetricClouds").UnregisterManualWeather("sunshine_preview")
				match case:
					"clear", "natural":
						await _view(-600000.0, 0.0, 10.0, BASE_H, 150)
					"inside":
						await _view(10000.0, 0.0, 10.0, BASE_H, 150)
					"leading":
						await _view(-1500.0, PI, 15.0, BASE_H, 150)
				helper.PerfBegin(world)
				await create_timer(float(_arg("perf-seconds", "10"))).timeout
				print("MEGASTORM_CAPTURE perf ", case, " ", helper.PerfEnd(world))
			world.get_node("RingVolumetricClouds").UnregisterManualWeather("sunshine_preview")
		_:
			_fail("unknown shot " + shot)


func _fail(reason):
	if finished:
		return
	finished = true
	printerr("MEGASTORM_CAPTURE=FAIL ", reason)
	quit(1)
