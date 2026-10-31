extends SceneTree
# Megastorm WO11 hazard visual captures (docs/megastorm-design.md 5.5). Same windowed setup as megastorm_capture.gd (certified world, paused clocks, the
# megastorm moved by rewriting its anchor / stepping the weather clock) with GROUND-level poses (2 m above the terrain) so the debris, trees and water show.
#   Godot --path . --resolution 1600x900 --script res://tools/megastorm_wo11_capture.gd -- --out=res://.godot/captures/megastorm/wo11/<label> --shots=boom [--rain] [--cycle=0.6]
# Shots:
#   boom    30 fps boom pass: xi from --boom-from m (-450) for --boom-frames (60) at yaw --boom-yaw (180, towards the wall), pitch --boom-pitch (8); frames in boom/,
#           one BOOM line per frame (overpressure, shake, blast front, burst, shock amplitude) for the same-frame-window check.
#   bullet  xi --bullet-xi m (6000) stills into the wind (yaw 180) and with the wind (yaw 0), plus a 30 fps run of --seq-frames (12) in bullet_seq/.
#   lee     a 10 m wall 25 m upwind of the camera: lee_wall (with the wall) and lee_open (without), both yaw 90 pitch 0 at xi --bullet-xi.
#   wake    xi --wake-xi m (29000) 30 fps run of --seq-frames (24) in wake/ with a WAKE line (wind, debris drift) per frame.
#   untrig  untriggered stills (no hazard globals live): run with --no-trigger.
var world: Node
var helper: RefCounted
var mega: RefCounted
var w11: RefCounted
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
	out_dir = _arg("out", "res://.godot/captures/megastorm/wo11/test")
	settle = int(_arg("settle", "120"))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir))
	var far = load("res://tools/FarRingGameplayCapture.cs").new()
	if not far.ConfigureArguments():
		_fail(far.ErrorText)
		return
	helper = load("res://tools/StormAnvilCapture.cs").new()
	mega = load("res://tools/MegastormCapture.cs").new()
	w11 = load("res://tools/MegastormWo11Capture.cs").new()
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
	if _arg("global-handoff", "") != "":
		# WO5b: a fixed near/far handoff (km) for the whole run, e.g. to put the seam inside the flight range.
		var gh := _arg("global-handoff", "").split(",")
		bridge.set("AutomaticCloudHandoff", false)
		bridge.set("HandoffStartMeters", float(gh[0]) * 1000.0)
		bridge.set("HandoffEndMeters", float(gh[1]) * 1000.0)
		print("MEGASTORM_CAPTURE global handoff ", gh)
	if _arg("no-haze", "") != "":
		bridge.call("DebugSetCloudAtmosphericDensity", 0.0)
	if _arg("matte", "") != "":
		bridge.call("SetDiagnosticDensityMattes", true, false)
	helper.PlaceCamera(world, BASE_H, 0.0, 8.0)
	await _frames(60)
	if _arg("layer-sky-shader", "") != "":
		print("MEGASTORM_CAPTURE layer sky shader swapped=", mega.SetLayerSkyShader(world, _arg("layer-sky-shader", "")))
	if _arg("no-trigger", "") == "" and not mega.Trigger(world, float(_arg("trigger-xi", "10000")), float(_arg("speed", "350")), int(_arg("seed", "7"))):
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
	for shot in _arg("shots", "boom").replace(" ", ",").split(","):
		await _shoot(shot)
	print("MEGASTORM_CAPTURE ", mega.StripState(world))
	print("MEGASTORM_CAPTURE=PASS out=", out_dir)
	finished = true
	quit(0)

func _view(xi: float, yaw: float, pitch: float, up := 2.0, frames := -1):
	_hide_overlays()
	mega.SetXi(world, xi)
	w11.GroundPose(world, up, yaw, pitch)
	await _frames(settle if frames < 0 else frames)

func _save_frame(dir: String, i: int):
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	var k := float(_arg("seq-scale", "0.6"))
	if k > 0.0 and k < 1.0:
		image.resize(int(image.get_width() * k), int(image.get_height() * k), Image.INTERPOLATE_BILINEAR)
	image.save_png(ProjectSettings.globalize_path(dir + "/frame%08d.png" % i))

func _save(name: String):
	await RenderingServer.frame_post_draw
	root.get_texture().get_image().save_png(ProjectSettings.globalize_path(out_dir + "/" + name + ".png"))
	print("MEGASTORM_CAPTURE saved ", name, " xi=%.0f" % mega.Xi(world))

func _seq_dir(name: String) -> String:
	var dir := out_dir + "/" + name
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	return dir

func _shoot(shot: String):
	var speed := float(_arg("speed", "350"))
	match shot:
		"boom":
			var dir := _seq_dir("boom")
			var frames := int(_arg("boom-frames", "60"))
			var from := float(_arg("boom-from", "-450"))
			await _view(from - 60.0 / 30.0 * speed, deg_to_rad(float(_arg("boom-yaw", "180"))), float(_arg("boom-pitch", "8")), 2.0, 90)
			for i in 60:
				mega.StepClock(world, 1.0 / 30.0)
				await process_frame
			for i in frames:
				mega.StepClock(world, 1.0 / 30.0)
				await process_frame
				print("BOOM f=%d %s" % [i, w11.Fx(world)])
				await _save_frame(dir, i)
		"boomab":
			# A-B-C of the boom: everything on, the hazard node off (no blast, debris hook, distortion), only the debris hook off. 24 frames each from --boom-from m.
			var fx = world.get_node("MegastormHazardFx")
			var debris = world.get_node("GustFrontDebris")
			for variant in ["all", "fxoff", "nodebris"]:
				fx.set("Enabled", variant != "fxoff")
				if variant == "nodebris":
					debris.set("MegastormLevel", 0.0)
					debris.set("MegastormDust", 0.0)
					fx.set("DebrisPath", NodePath("../Nowhere"))
					fx.call("_Ready")
				var dir := _seq_dir("boom_" + variant)
				var from := float(_arg("boom-from", "-250"))
				await _view(from - 60.0 / 30.0 * speed, deg_to_rad(float(_arg("boom-yaw", "180"))), float(_arg("boom-pitch", "8")), 2.0, 60)
				for i in 60:
					mega.StepClock(world, 1.0 / 30.0)
					await process_frame
				for i in 36:
					mega.StepClock(world, 1.0 / 30.0)
					await process_frame
					await _save_frame(dir, i)
				print("BOOMAB ", variant, " ", w11.Fx(world))
		"bullet":
			var xi := float(_arg("bullet-xi", "6000"))
			await _view(xi, PI, 3.0, 2.0, 90)
			await _save("bullet_into_wind")
			print("BULLET into ", w11.Streaks(world), " ", w11.Fx(world))
			await _view(xi, 0.0, 3.0, 2.0, 60)
			await _save("bullet_with_wind")
			await _view(xi, PI * 0.5, 3.0, 2.0, 60)
			await _save("bullet_side")
			var dir := _seq_dir("bullet_seq")
			await _view(xi, PI * 0.5, 3.0, 2.0, 60)
			for i in int(_arg("seq-frames", "12")):
				mega.StepClock(world, 1.0 / 30.0)
				await _save_frame(dir, i)
		"lee":
			var xi := float(_arg("bullet-xi", "6000"))
			await _view(xi, PI * 0.5, 0.0, 2.0, 90)
			await _save("lee_open")
			print("LEE open ", w11.Streaks(world))
			if not w11.AddWall(world, -25.0, 10.0, 60.0, 2.0):
				_fail(w11.ErrorText)
				return
			await _frames(90)
			await _save("lee_wall")
			print("LEE wall ", w11.Streaks(world))
			w11.RemoveWall(world)
		"wake":
			var dir := _seq_dir("wake")
			var xi := float(_arg("wake-xi", "29000"))
			await _view(xi, 0.0, 6.0, 2.0, 120)
			for i in int(_arg("seq-frames", "24")):
				mega.StepClock(world, 1.0 / 30.0)
				await process_frame
				print("WAKE f=%d %s %s" % [i, w11.Fx(world), w11.DebrisState(world)])
				await _save_frame(dir, i)
		"untrig":
			await _view(0.0, PI, 8.0, 2.0, 90)
			await _save("untrig_view")
			print("UNTRIG ", w11.Fx(world))
		_:
			_fail("unknown shot " + shot)

func _hide_overlays():
	for parent in [world, root]:
		for n in parent.get_children():
			if n is CanvasLayer:
				n.visible = false

func _fail(reason):
	if finished:
		return
	finished = true
	printerr("MEGASTORM_CAPTURE=FAIL ", reason)
	quit(1)
