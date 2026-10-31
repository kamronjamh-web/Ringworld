extends SceneTree
# Megastorm darkness captures (docs/megastorm-design.md 5.6, WO14). Same windowed setup as megastorm_capture.gd (certified world, StormAnvilCapture base
# pose, clocks paused, the megastorm moved by rewriting its anchor), plus:
#   darkness  ground luma at the clear pose (megastorm 600 km away), the ordinary-storm pose (the S4 preview storm over the camera, megastorm 600 km away)
#             and xi --dark-xi km (-40,-10,0,10,40,100,300,450). Per case: dark_<case>_ground.png (pitch --dark-pitch, -30: mostly ground) and
#             dark_<case>_view.png (pitch 8), and a DARK line with the bottom-half mean display luma, its local contrast (mean |luma - 3x3 mean| over a
#             400 x 225 downsample), the CPU levels (D', E, E', K at the camera), the eye cap / lock and the auto-exposure sensitivities.
#   darkseq   a 30 fps sequence (clock stepping 1/30 s) at xi --seq-from km (10) for --seq-frames (600), yaw --seq-yaw, pitch --seq-pitch (-20):
#             flash peaks vs the wall mean for `ffmpeg_tools.ps1 flicker` (darkseq/frame%08d.png, --seq-scale of the window).
#   darkprobe the camera at --probe-cam-xi km (0): the shader's extended level L (ring_storm_level through tools/storm_light_probe.gdshader) against the CPU twin
#             (bridge GetStormLevelAt, and the CPU E) at 16 points from xi -60 to +450 km along the camera's across (beyond the macro map the shader uses the
#             analytic E). One DARKPROBE line per point plus the max |shader - cpu|.
#   darkperf  receiver / macro-build A-B-A: darkness on and off (debug switch) x3 at the inside and ahead150 poses (MEGASTORM_DARK perf lines).
#   Godot --path . --resolution 1600x900 --script res://tools/megastorm_darkness_capture.gd -- --out=res://.godot/captures/megastorm/wo14/<label> --shots=darkness
# Shared args (megastorm_capture.gd): --cycle (0.6 noon, 0.1 night), --rain, --set=Prop=value, --settle.
# Standalone copy of the megastorm_capture.gd (HEAD 266e76d) setup and shots, so it does not depend on that file's in-progress edits; its own header follows.
# Megastorm captures (docs/megastorm-design.md WO4 near wall; WO9 extends this harness). Windowed GPU run, certified world.
#   Godot --path . --resolution 1600x900 --script res://tools/megastorm_capture.gd -- --out=res://.godot/captures/megastorm/wo4/<label> --shots=inside,leading
# Args: --out=<res dir> --shots=<list> [--far-filter=<substring of the far shot names>] [--save-far (also save the far viewport)] [--no-trigger (untriggered identity captures)]
#       [--layer-sky-shader=<res path> (debug: swap the far layer sky shader)] [--cycle=<0..1> (0.6 noon)] [--no-haze] [--matte] [--speed=<m/s> (350)] [--seed=<n> (7)] [--rain] [--deterministic]
#       [--global-handoff=<start,end> km (automatic handoff off for the run)]
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
#   far       WO5 far wedge pass: towards the wall from xi -50 km (pitch 4, 10), -200 km (3, 6), -1000 km (6.5, 9), the quarter ring
#             (xi -C/4, pitch 45) and the antipode (xi -C/2, straight up). Run once with --cycle=0.6 (camera noon) and once at night
#             (--cycle=0.1): at the antipode the wall's local time is the camera's opposite.
#   farlook   WO5b: the wall framed from 60 / 100 / 150 km ahead (look<km>_p<pitch>), --farlook-filter=<substring>
#   handoff   WO5 seam calibration: the wall at --handoff-xi km (-25) with the default handoff (the near march owns it) and with
#             --handoff=<start,end> km (5,10: the far wedge pass owns it); compare the pair (handoff_near / handoff_far)
#   flight    WO5: the camera "flies" from xi -30 to -80 km over --flight-frames frames (300) while the wall moves at --speed (the
#             weather clock steps 1/30 s per frame and the wall is re-placed each frame): flight/frame%08d.png for flicker and a sheet
#   farhold   WO5: a fixed far camera for --hold-seconds (60) at 30 fps, xi from --hold-xi km (-150), the wall moving at --speed: no 8 s
#             atlas pops (farhold/frame%08d.png). Sequences are saved at --seq-scale (0.4) of the window size.
#   farperf   WO5: far viewport GPU time with the wedge pass drawn / suppressed (A-B-A-B-A) at 200 km, 1000 km and 50 km
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
	for shot in _arg("shots", "inside").split(","):
		await _shoot(shot)
	print("MEGASTORM_CAPTURE ", mega.StripState(world))
	print("MEGASTORM_CAPTURE=PASS out=", out_dir)
	finished = true
	quit(0)

func _save_frame(dir: String, i: int):
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	var k := float(_arg("seq-scale", "0.4"))
	if k > 0.0 and k < 1.0:
		image.resize(int(image.get_width() * k), int(image.get_height() * k), Image.INTERPOLATE_BILINEAR)
	image.save_png(ProjectSettings.globalize_path(dir + "/frame%08d.png" % i))

func _save(name: String):
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	var path := out_dir + "/" + name + ".png"
	image.save_png(ProjectSettings.globalize_path(path))
	if _arg("save-far", "") != "":
		# Debug: the scaled far viewport's own colour (before the main view composites over it).
		var far_vp = world.get_node("ScaledFarViewport").get("OuterViewportForDiagnostics")
		if far_vp != null:
			far_vp.get_texture().get_image().save_png(ProjectSettings.globalize_path(out_dir + "/" + name + "_farvp.png"))
	print("MEGASTORM_CAPTURE saved ", name, " xi=%.0f" % mega.Xi(world))

func _hide_overlays():
	# HUD layers, including ones created after start-up (PDA subtitles), stay out of the captures.
	for parent in [world, root]:
		for n in parent.get_children():
			if n is CanvasLayer:
				n.visible = false

func _view(xi: float, yaw: float, pitch: float, height := BASE_H, frames := -1):
	_hide_overlays()
	mega.SetXi(world, xi)
	helper.PlaceCamera(world, height, yaw, pitch)
	await _frames(settle if frames < 0 else frames)

func _base_shoot(shot: String):
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
			await _view(float(_arg("probe-xi", "-15")) * 1000.0, PI, 3.0, BASE_H, 60)
			var clouds_res = world.get_node("SunshineCloudBridge").GetSunshineCloudsForTests()
			var ds := [0.0, 5000.0, 10000.0, 14000.0, 16000.0, 20000.0, 30000.0, 45000.0]
			if _arg("probe-d", "") != "":
				ds = []
				for k in _arg("probe-d", "").split(","):
					ds.append(float(k) * 1000.0)
			print("MEGASTORM_CAPTURE probeline handoff ", world.get_node("SunshineCloudBridge").call("GetEffectiveCloudHandoff"), " ", mega.StripState(world))
			for d in ds:
				for h in [2000.0, 6000.0, 12000.0]:
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
		"far":
			var circumference := 31415936.0
			for v in [[-50000.0, 4.0, "far_ahead50_p04"], [-50000.0, 10.0, "far_ahead50_p10"], [-200000.0, 3.0, "far_ahead200_p03"],
					[-200000.0, 6.0, "far_ahead200_p06"], [-1000000.0, 6.5, "far_ahead1000_p065"], [-1000000.0, 9.0, "far_ahead1000_p09"],
					[-circumference * 0.25, 45.0, "far_quarter_p45"], [-circumference * 0.5 + 1000.0, 89.0, "far_antipode_up"]]:
				if _arg("far-filter", "") != "" and not str(v[2]).contains(_arg("far-filter", "")):
					continue
				await _view(v[0], PI, v[1])
				var far_node = world.get_node_or_null("ScaledFarViewport")
				print("MEGASTORM_CAPTURE far ", v[2], " ", far_node.get("FarMegastormStatus") if far_node != null else "")
				if far_node != null and _arg("ray-probe", "") != "":
					print("MEGASTORM_CAPTURE probe ", v[2], " ", far_node.call("FarMegastormRayProbe"))
				await _save(v[2])
		"farlook":
			# WO5b look: the wall from 60, 100 and 150 km ahead, framed (--farlook-filter=<substring>). Run with RING_MEGASTORM_FAR_DEBUG for
			# the debug views (9 unhazed, 12 haze T, 13 in-scatter).
			var looks := [[-60000.0, 5.0, "look060_p05"], [-60000.0, 12.0, "look060_p12"], [-100000.0, 4.0, "look100_p04"], [-100000.0, 8.0, "look100_p08"],
					[-150000.0, 3.0, "look150_p03"], [-150000.0, 6.0, "look150_p06"]]
			if _arg("farlook-xis", "") != "":
				looks = []
				for k in _arg("farlook-xis", "").split(","):
					looks.append([-float(k) * 1000.0, float(_arg("farlook-pitch", "6")), "look%03d_p%02d" % [int(k), int(_arg("farlook-pitch", "6"))]])
			for v in looks:
				if _arg("farlook-filter", "") != "" and not str(v[2]).contains(_arg("farlook-filter", "")):
					continue
				await _view(v[0], PI, v[1])
				await _save(v[2])
		"identity":
			# WO5 untriggered identity (run with --no-trigger): each far pose with the live far layer sky, then with --identity-shader
			# (the HEAD copy) swapped in, in one session; compare the pairs.
			var poses := [[-200000.0, 3.0, "id_ahead200"], [-1000000.0, 6.5, "id_ahead1000"], [-50000.0, 10.0, "id_ahead50"], [0.0, 45.0, "id_up45"]]
			for v in poses:
				mega.SetLayerSkyShader(world, "res://materials/ring_far_cloud_layer_sky.gdshader")
				await _view(v[0], PI, v[1])
				await _save(v[2] + "_live")
				mega.SetLayerSkyShader(world, _arg("identity-shader", ""))
				await _frames(settle)
				await _save(v[2] + "_head")
				if _arg("rfix-shader", "") != "":
					# Experiment: the far layer sky with its ring radius in metres (it reads ring_radius_meters in scaled units).
					mega.SetLayerSkyShader(world, _arg("rfix-shader", ""))
					await _frames(settle)
					await _save(v[2] + "_rfix")
			mega.SetLayerSkyShader(world, "res://materials/ring_far_cloud_layer_sky.gdshader")
		"handoff":
			var xi := float(_arg("handoff-xi", "-25")) * 1000.0
			var bridge_h = world.get_node("SunshineCloudBridge")
			await _view(xi, PI, float(_arg("handoff-pitch", "6")))
			await _save("handoff_near")
			var hv := _arg("handoff", "5,10").split(",")
			var auto_before = bridge_h.get("AutomaticCloudHandoff")
			var start_before = bridge_h.get("HandoffStartMeters")
			var end_before = bridge_h.get("HandoffEndMeters")
			bridge_h.set("AutomaticCloudHandoff", false)
			bridge_h.set("HandoffStartMeters", float(hv[0]) * 1000.0)
			bridge_h.set("HandoffEndMeters", float(hv[1]) * 1000.0)
			await _frames(settle)
			await _save("handoff_far")
			bridge_h.set("AutomaticCloudHandoff", auto_before)
			bridge_h.set("HandoffStartMeters", start_before)
			bridge_h.set("HandoffEndMeters", end_before)
			await _frames(60)
		"flight":
			var dir := out_dir + "/flight"
			DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
			var frames := int(_arg("flight-frames", "300"))
			var pitch := float(_arg("flight-pitch", "5"))
			await _view(-30000.0, PI, pitch)
			for i in frames:
				mega.StepClock(world, 1.0 / 30.0)
				mega.SetXi(world, -30000.0 - 50000.0 * float(i) / float(max(frames - 1, 1)))
				await _save_frame(dir, i)
			print("MEGASTORM_CAPTURE flight frames=", frames, " end xi=%.0f" % mega.Xi(world))
		"farhold":
			var dir := out_dir + "/farhold"
			DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
			var frames := int(float(_arg("hold-seconds", "60")) * 30.0)
			await _view(float(_arg("hold-xi", "-150")) * 1000.0, PI, float(_arg("hold-pitch", "4")))
			for i in frames:
				mega.StepClock(world, 1.0 / 30.0)
				await _save_frame(dir, i)
			print("MEGASTORM_CAPTURE farhold frames=", frames, " end xi=%.0f" % mega.Xi(world))
		"farperf":
			# WO5: far wedge pass cost, A-B-A within one run (the pass suppressed vs drawn) at the 200 km, 1000 km and 50 km views.
			for v in [[-200000.0, 3.0, "far200"], [-1000000.0, 6.5, "far1000"], [-50000.0, 10.0, "far50"]]:
				await _view(v[0], PI, v[1], BASE_H, 150)
				for case in ["on", "off", "on", "off", "on"]:
					mega.SetFarPassSuppressed(world, case == "off")
					await _frames(30)
					helper.PerfBegin(world)
					await create_timer(float(_arg("perf-seconds", "8"))).timeout
					print("MEGASTORM_CAPTURE farperf ", v[2], " ", case, " ", mega.FarPerfEnd(world))
				mega.SetFarPassSuppressed(world, false)
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


func _luma_stats(image: Image) -> Array:
	var small := image.duplicate() as Image
	small.convert(Image.FORMAT_RGB8)
	small.resize(400, 225, Image.INTERPOLATE_BILINEAR)
	var w := small.get_width()
	var h := small.get_height()
	var luma := PackedFloat32Array()
	luma.resize(w * h)
	for y in h:
		for x in w:
			var c := small.get_pixel(x, y)
			luma[y * w + x] = (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) * 255.0
	var total := 0.0
	var contrast := 0.0
	var n := 0
	for y in range(h / 2, h - 1):
		for x in range(1, w - 1):
			var v := luma[y * w + x]
			var local := 0.0
			for dy in range(-1, 2):
				for dx in range(-1, 2):
					local += luma[(y + dy) * w + x + dx]
			contrast += absf(v - local / 9.0)
			total += v
			n += 1
	return [total / maxf(n, 1.0), contrast / maxf(n, 1.0)]

func _dark_state() -> String:
	var bridge = world.get_node("SunshineCloudBridge")
	var day = world.get_node("DaySideAtmosphere")
	var attributes = null
	var env_node = day.get("world_environment")
	if env_node != null:
		attributes = env_node.camera_attributes
	var ae := ""
	if attributes != null:
		ae = " ae=%s min=%.1f max=%.1f" % [str(attributes.auto_exposure_enabled), attributes.auto_exposure_min_sensitivity, attributes.auto_exposure_max_sensitivity]
	return "L=%.3f Dp=%.3f E=%.3f Ep=%.3f K=%.3f floor=%.3f eye_cap=%.3f eye_lock=%.3f%s" % [
		float(bridge.call("GetCameraStormLevelExtended")), float(bridge.call("GetCameraStormLevel")), float(bridge.call("GetCameraMegastormDarkness")),
		float(bridge.call("GetCameraMegastormExtra")), float(bridge.call("GetCameraDarknessMultiplier")), float(bridge.get("MegastormDarknessFloor")),
		float(day.get("current_storm_eye_cap")), float(day.get("current_storm_eye_lock")), ae]

func _dark_case(tag: String, xi: float, natural: bool):
	var clouds = world.get_node("RingVolumetricClouds")
	if natural:
		helper.PlaceStorm(world, 0.0, 0.0, 0.0, 24000.0)
	else:
		clouds.UnregisterManualWeather("sunshine_preview")
	var pitch := float(_arg("dark-pitch", "-30"))
	await _view(xi, 0.0, pitch, BASE_H, int(_arg("dark-settle", "240")))
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	image.save_png(ProjectSettings.globalize_path(out_dir + "/dark_%s_ground.png" % tag))
	var stats := _luma_stats(image)
	print("MEGASTORM_DARK case=%s xi=%.0f ground_luma=%.2f local_contrast=%.3f %s" % [tag, mega.Xi(world), stats[0], stats[1], _dark_state()])
	await _view(xi, 0.0, 8.0, BASE_H, 60)
	await _save("dark_%s_view" % tag)

func _dark_probe():
	var bridge = world.get_node("SunshineCloudBridge")
	var cam_xi := float(_arg("probe-cam-xi", "0"))
	await _view(cam_xi * 1000.0, 0.0, 8.0, BASE_H, 90)
	var cam: Vector3 = bridge.call("GetExplorerCanonicalPosition")
	var xis := [-60.0, -45.0, -25.0, -5.0, -1.5, 2.0, 10.0, 27.5, 45.0, 70.0, 100.0, 160.0, 235.0, 300.0, 390.0, 450.0]
	var worlds := PackedVector3Array()
	var cpu := PackedFloat32Array()
	var cpu_e := PackedFloat32Array()
	for km in xis:
		var around: float = cam.x - (km - cam_xi) * 1000.0
		worlds.append(bridge.call("GetStormProbeWorldPosition", around, 0.0, cam.z))
		cpu.append(float(bridge.call("GetStormLevelAt", around, cam.z)))
		var terms: Dictionary = bridge.call("GetStormLightTermsAt", around, cam.z)
		cpu_e.append(float(terms.get("E", -1.0)))
	var viewport := SubViewport.new()
	viewport.size = Vector2i(16, 1)
	viewport.disable_3d = true
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
	for i in 8:
		await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var max_error := 0.0
	for i in 16:
		var shader_l := image.get_pixel(i, 0).r
		max_error = maxf(max_error, absf(shader_l - cpu[i]))
		print("MEGASTORM_DARKPROBE xi_km=%.1f cpu_E=%.4f cpu_L=%.4f shader_L=%.4f" % [xis[i], cpu_e[i], cpu[i], shader_l])
	print("MEGASTORM_DARKPROBE cam_xi_km=%.0f max_abs_error=%.4f map_valid=%s" % [cam_xi, max_error, str(bridge.call("IsStormLightValid"))])
	viewport.queue_free()

func _shoot(shot: String):
	match shot:
		"darkperf":
			# A-B-A-B-A-B: megastorm darkness on / off (bridge SetDebugMegastormDarknessOff: E = 0 in the macro map and the analytic receivers) at the
			# --perf-poses (inside: xi 10 km yaw 0 pitch 10; ahead150: xi -150 km towards the wall, pitch 3), --perf-seconds (8) each.
			var bridge = world.get_node("SunshineCloudBridge")
			for pose in _arg("perf-poses", "inside,ahead150").split(","):
				for case in ["on", "off", "on", "off", "on", "off"]:
					bridge.call("SetDebugMegastormDarknessOff", case == "off")
					match pose:
						"inside":
							await _view(10000.0, 0.0, 10.0, BASE_H, 150)
						"ahead150":
							await _view(-150000.0, PI, 3.0, BASE_H, 150)
					helper.PerfBegin(world)
					await create_timer(float(_arg("perf-seconds", "8"))).timeout
					print("MEGASTORM_DARK perf ", pose, " ", case, " ", helper.PerfEnd(world))
			bridge.call("SetDebugMegastormDarknessOff", false)
		"darkprobe":
			await _dark_probe()
		"darkness":
			await _dark_case("clear", -600000.0, false)
			await _dark_case("natural", -600000.0, true)
			for km in _arg("dark-xi", "-40,-10,0,10,40,100,300,450").split(","):
				await _dark_case("xi%s" % km, float(km) * 1000.0, false)
			world.get_node("RingVolumetricClouds").UnregisterManualWeather("sunshine_preview")
		"darkseq":
			var dir := out_dir + "/darkseq"
			DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
			var frames := int(_arg("seq-frames", "600"))
			await _view(float(_arg("seq-from", "10")) * 1000.0, deg_to_rad(float(_arg("seq-yaw", "0"))), float(_arg("seq-pitch", "-20")), BASE_H, 150)
			for i in frames:
				mega.StepClock(world, 1.0 / 30.0)
				await _save_frame(dir, i)
				if i % 150 == 0:
					print("MEGASTORM_DARK seq frame=%d xi=%.0f %s" % [i, mega.Xi(world), _dark_state()])
			print("MEGASTORM_DARK seq frames=", frames, " end xi=%.0f" % mega.Xi(world))
		_:
			await _base_shoot(shot)
