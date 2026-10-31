extends SceneTree
# S6 WO3b look captures (windowed GPU run; use the godot-test-run profile and the GPU queue).
#   Godot --path . --resolution 1600x900 --script res://tools/cloud_layers_capture.gd -- --out=res://.godot/captures/cloud_layers/<label> --shots=fair,overcast,asdeck,orbit
# Args: --out=<res dir> --shots=<comma list> [--settle=<frames>] [--cycle=<0..1>] [--orbit-frames=<n>] [--orbit-h=<m>] [--rain] [--set=Prop=value] [--fair=<da>,<dc>]
#       [--no-haze] [--matte] [--no-ae] [--no-storm-haze] [--deterministic] (diagnostics; deterministic = no jitter, no history)
# Shots (camera yaw 0 = along the ring, clocks paused, rain off):
#   fair      weather-free position with the most low cover: ground (420 m) and 1.5 km camera, pitch 85 / 30 / 10 / 0 (fair_<g|h>_p<pitch>)
#   overcast  a manual Nimbostratus core (500 m base) over the camera: ground and 1.5 km, pitch 85 / 30 / 0, plus plan at 12 km (overcast_*)
#   edge      the same system from 0.85 radius out, looking in (edge_*): the deck's leading edge and the lowering base
#   asdeck    a manual Altostratus bank (no base lowering) over fair cover: Cu under an As deck (asdeck_*)
#   shadow    WO4: fair and overcast looking down (-85 / -45 from 1.5 km, -20 from 420 m) and up, for shadow vs cloud pattern (shadow_<case>_<g|h>_p<pitch>)
#   plan      fair plan view at 12 km and 7.5 km looking straight down (terracing check, plan_*)
#   orbit     fair orbit (yaw sweep) at orbit-h, orbit-frames frames (frame%08d) for flicker
#   deck      S6 3.8 layer-owned decks at a LOW site (terrain 15-220 m, so the 500 m weather base sits well above the camera; --low=<da>,<dc>
#             forces it): a manual Nimbostratus core (deck_ns_*), an Altostratus bank (deck_as_*), a thin As edge (deck_asthin_*), each at
#             pitch 85/60, 30/25, 10/8, toward the sun (*_sun), looking down (*_down), plus the near/far shadow maps (shadowmap_deck_*)
#   cirrus    S6 WO5 High layer (cirrus shell) cases, --cirrus-cases=fair,cs,front,above (default all):
#               fair   no weather: ground (420 m) at fair_pos, pitch 85 / 60 / 30 / 10 / 0 (cirrus_fair_p<pitch>) and toward the sun
#                      (cirrus_fair_sun, pitch clamped 4..80); prints the map's high coverage at fair_pos
#               cs     a dry manual Cirrostratus bank (60 km, top 13 km, precipitation 0, coverage 0.3 so the macro coverage and the low
#                      layer under it stay fair) over fair_pos: the same pitches (cirrus_cs_p<pitch>, cirrus_cs_sun)
#               front  the same bank from 1.15 x its 75 km major axis out, looking in (yaw 0), pitch 30 / 10 / 3 (cirrus_front_p<pitch>):
#                      the Ci -> Cs progression into the bank
#               above  the same bank from 11 km up looking down: pitch -30 and -85 (cirrus_above_m30, cirrus_above_m85)
#             The first shot of each case settles for --settle frames, the rest 40.
#   cirrusorbit  the Cs bank, orbit like `orbit` at 420 m, pitch 35, --orbit-frames (default 120) frames (frame%08d)
#   handoff   S6 WO6 near/far seam: --handoff-cases=fair,overcast,asdeck,cs (default all), ground (420 m) and 1.5 km, yaw --handoff-yaw
#             (default 0), pitch 1 / 3 / 6 / 10 (handoff_<case>_<g|h>_p<pitch>). Run once as is and once with --handoff=<start>,<end> far out
#             (near-only reference) and `compare` the pairs.
#   dolly     S6 WO6 flicker: from the low site the camera flies along yaw 0 at --dolly-h (420) and pitch --dolly-pitch (3), --dolly-step metres per frame
#             (250) for --orbit-frames frames (frame%08d), --dolly-case=fair|asdeck|cs, so clouds cross the handoff band.
#   fineduty  fine far layer atlas duty cycle with the cloud clock running: stationary, then a flight at --duty-speed (250 m/s),
#             --duty-frames (3600) frames each; prints CLOUD_LAYERS_CAPTURE fineduty (duty, per-frame CPU median/p95). No captures.
#   s6fix     megastorm WO5b: the far layer sky before / after its ring-radius fix, in one session. Cases --s6fix-cases=fair,overcast,storm
#             (fair: no system; overcast: a manual Nimbostratus core over the camera; storm: the S4 anvil preview storm 120 km ahead), ground
#             camera (420 m), headings yaw 0 / 90 / 180 at pitch 3 and the arch (yaw 0, pitch 20): s6fix_<case>_<view>_after with the live
#             shader, then _before with --before-shader (the pre-fix copy) swapped in.
#   interp    far camera physics-interpolation check: teleports the camera and prints, for 3 frames, how far the rendered (interpolated)
#             far and main camera transforms are from their targets (CLOUD_LAYERS_CAPTURE interp ...).
# Far diagnostics: --handoff=<start>,<end> (automatic handoff off), --far-off=clipmap,shell,main,layers (zero those far cloud tiers;
#   layers = the WO6 layer sky off, i.e. the pre-WO6 far).

var world: Node
var helper: RefCounted
var probe: RefCounted
var out_dir := ""
var settle := 150
var finished := false
var fair_pos := Vector2.ZERO

func _initialize():
	create_timer(1700.0).timeout.connect(func(): _fail("1700 second watchdog expired"))
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

func _wait_builds(target: int, max_frames := 1500) -> bool:
	for i in max_frames:
		if int(probe.Builds(world)) >= target:
			return true
		await process_frame
	return false

func _save(name: String):
	await RenderingServer.frame_post_draw
	var image = root.get_texture().get_image()
	var path = ProjectSettings.globalize_path(out_dir).path_join(name + ".png")
	image.save_png(path)
	print("CLOUD_LAYERS_CAPTURE saved ", path)

func _camera(offset: Vector2, h: float, yaw_deg: float, pitch: float, frames: int):
	helper.PlaceCameraAt(world, offset.x, offset.y, h, deg_to_rad(yaw_deg), pitch)
	_apply_far_off()
	# Finish the fine layer atlas build (WO6 follow-up) so a teleport or a placed system shows in it at once.
	var far_vp := world.get_node_or_null("ScaledFarViewport")
	if far_vp != null and _arg("fine-complete", "1") == "1" and far_vp.has_method("CompleteFarFineLayerAtlas"):
		far_vp.call("CompleteFarFineLayerAtlas")
	await _frames(frames)

# --far-off=clipmap,shell,main: zero those far cloud tiers (re-applied per shot: publishes may rewrite the uniforms).
func _apply_far_off():
	var off := _arg("far-off", "")
	var far := world.get_node_or_null("ScaledFarViewport")
	if far == null:
		return
	for n in far.find_children("*", "MeshInstance3D", true, false):
		var m = n.material_override
		if m == null:
			continue
		if off.contains("clipmap") and String(n.name).begins_with("FarCloudClipmapLayer"):
			m.set_shader_parameter("opacity", 0.0)
			m.set_shader_parameter("far_density_opacity_multiplier", 0.0)
		# --far-off=layers: the pre-WO6 far (layer sky off, the clipmap draws its own cloud body again).
		if off.contains("layers") and (String(n.name) == "FarCloudLayerSky" or String(n.name).begins_with("FarCloudClipmapLayer")):
			m.set_shader_parameter("far_layers_enabled", false)
		if off.contains("shell") and String(n.name) == "ScaledCloudShell":
			m.set_shader_parameter("far_density_opacity_multiplier", 0.0)
			m.set_shader_parameter("weather_atlas_enabled", false)
	# --sky-set=name=value[;name=value]: far layer sky material uniforms (WO6 look tuning).
	var sky_set := _arg("sky-set", "")
	if sky_set != "":
		var sky := far.find_child("FarCloudLayerSky", true, false) as MeshInstance3D
		if sky != null and sky.material_override != null:
			for kv in sky_set.split(";"):
				var parts := kv.split("=")
				if parts.size() == 2:
					sky.material_override.set_shader_parameter(parts[0], float(parts[1]))
	if off.contains("main"):
		var shell = world.get_node_or_null("RingCloudLayer")
		if shell != null:
			shell.visible = false

func _readback() -> bool:
	probe.RequestReadback(world)
	for i in 120:
		await process_frame
		if probe.ReadbackReady:
			return true
	return false

# Weatherness-0 position (offset from the base) with the most low cover.
func _pick_fair() -> Vector2:
	var best := Vector2.ZERO
	var best_cover := -1.0
	for da in range(-60000, 60001, 10000):
		for dc in range(-45000, 45001, 15000):
			var layers: Vector4 = probe.LayersAt(probe.BaseAroundMeters + da, probe.BaseAcrossMeters + dc)
			if layers.w < 0.0 or layers.w > 0.0005:
				continue
			var cover: float = layers.x
			if cover > best_cover:
				best_cover = cover
				best = Vector2(da, dc)
	print("CLOUD_LAYERS_CAPTURE fair pick ", best, " low_cover=", best_cover)
	return best

# Lowest land (terrain 15-220 m, all four 2 km neighbours above the sea) on a coarse grid around the base: the deck shots' site.
func _pick_low() -> Vector2:
	if _arg("low", "") != "":
		var xy := _arg("low", "").split(",")
		return Vector2(float(xy[0]), float(xy[1]))
	var best := Vector2.ZERO
	var best_score := 1e30
	for da in range(-160000, 160001, 8000):
		for dc in range(-60000, 60001, 6000):
			var h: float = probe.TerrainHeight(world, da, dc)
			if is_nan(h) or h < 15.0 or h > 220.0:
				continue
			var land := true
			for o in [Vector2(2000, 0), Vector2(-2000, 0), Vector2(0, 2000), Vector2(0, -2000)]:
				if probe.TerrainHeight(world, da + o.x, dc + o.y) < 10.0:
					land = false
			if not land:
				continue
			var score := absf(h - 100.0) + absf(float(da)) * 0.0002
			if score < best_score:
				best_score = score
				best = Vector2(da, dc)
	print("CLOUD_LAYERS_CAPTURE low site ", best, " terrain=", probe.TerrainHeight(world, best.x, best.y))
	return best

# Deck shots for one placed system: three pitches, toward the sun, looking down; then the shadow maps.
func _deck_shots(label: String, site: Vector2, pitches: Array, first_settle: int):
	var h: float = probe.TerrainHeight(world, site.x, site.y) + 25.0
	for i in pitches.size():
		await _camera(site, h, 0.0, pitches[i], first_settle if i == 0 else 40)
		await _save("deck_%s_p%02d" % [label, int(pitches[i])])
	await _camera(site, h, 0.0, 0.0, 2)
	var sun: Vector2 = probe.SunYawPitch(world)
	print("CLOUD_LAYERS_CAPTURE sun yaw/pitch ", sun)
	await _camera(site, h, sun.x, clampf(sun.y, 4.0, 80.0), 40)
	await _save("deck_%s_sun" % label)
	await _camera(site, h + 150.0, 0.0, -25.0, 40)
	await _save("deck_%s_down" % label)
	_dump_shadow_map("shadowmap_deck_" + label)

# Cirrus shell pitch sweep at one position (cirrus_<label>_p<pitch>), optionally followed by the shot toward the sun.
func _cirrus_shots(label: String, pos: Vector2, h: float, pitches: Array, with_sun: bool):
	for i in pitches.size():
		await _camera(pos, h, 0.0, pitches[i], settle if i == 0 else 40)
		await _save("cirrus_%s_p%02d" % [label, int(pitches[i])])
	if with_sun:
		await _camera(pos, h, 0.0, 0.0, 2)
		var sun: Vector2 = probe.SunYawPitch(world)
		print("CLOUD_LAYERS_CAPTURE sun yaw/pitch ", sun)
		await _camera(pos, h, sun.x, clampf(sun.y, 4.0, 80.0), 40)
		await _save("cirrus_%s_sun" % label)

func _clear():
	probe.ClearWeather(world)

# The S6 WO6 seam cases: fair (no system), overcast (Ns core), asdeck (As bank), cs (dry Cirrostratus bank), all over fair_pos.
func _place_case(hcase: String):
	match hcase:
		"overcast":
			await _place(10, 4, 60000.0, 7000.0, fair_pos)
		"asdeck":
			await _place(7, 2, 60000.0, 7000.0, fair_pos)
		"cs":
			await _place(5, 2, 60000.0, 13000.0, fair_pos, 0.0, 0.3)
		_:
			_clear()
			await _frames(60)

func _place(profile: int, kind: int, radius: float, top: float, at: Vector2, precipitation := 0.94, coverage := 0.97):
	_clear()
	probe.PlaceWeather(world, profile, at.x, at.y, radius, top, kind, precipitation, coverage)
	var builds := int(probe.Builds(world))
	await _wait_builds(builds + 2)
	await _frames(60)

func _run():
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice")
		return
	out_dir = _arg("out", "res://.godot/captures/cloud_layers/test")
	settle = int(_arg("settle", "150"))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir))
	var far = load("res://tools/FarRingGameplayCapture.cs").new()
	if not far.ConfigureArguments():
		_fail(far.ErrorText)
		return
	helper = load("res://tools/StormAnvilCapture.cs").new()
	probe = load("res://tools/CloudLayersProbe.cs").new()
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
	if not helper.Setup(world, float(_arg("cycle", "0.6")), 1500.0):
		_fail(helper.ErrorText)
		return
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	await create_timer(25.0).timeout
	for n in world.get_children():
		if n is CanvasLayer:
			n.visible = false
	for n in root.get_children():
		if n is CanvasLayer:
			n.visible = false
	if _arg("rain", "") == "":
		helper.RainOff(world)
	var bridge = world.get_node("SunshineCloudBridge")
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--set="):
			var kv := a.substr(6).split("=")
			if kv.size() == 2:
				bridge.set(kv[0], float(kv[1]))
	# Diagnostics: --no-haze (cloud aerial perspective off), --matte (raw density matte), --no-ae (storm auto exposure off).
	if _arg("no-haze", "") != "":
		bridge.call("DebugSetCloudAtmosphericDensity", 0.0)
	if _arg("matte", "") != "":
		bridge.call("SetDiagnosticDensityMattes", true, false)
	if _arg("deterministic", "") != "":
		bridge.set("RayStartJitterStrength", 0.0)
		bridge.set("CloudResolutionJitter", false)
		bridge.set("DitherSpeed", 0.0)
		bridge.call("ApplyTemporalQualityTuning", true)
		bridge.call("SetDiagnosticTemporalHistoryDisabled", true)
	if _arg("no-storm-haze", "") != "":
		for n in world.find_children("*", "", true, false):
			if n.get("haze_storm_strength") != null:
				n.set("haze_storm_strength", 0.0)
	if _arg("no-ae", "") != "":
		for n in world.find_children("*", "", true, false):
			if n.get("storm_auto_exposure_enabled") != null:
				n.set("storm_auto_exposure_enabled", false)
	if _arg("handoff", "") != "":
		var hv := _arg("handoff", "").split(",")
		bridge.set("AutomaticCloudHandoff", false)
		bridge.set("HandoffStartMeters", float(hv[0]))
		bridge.set("HandoffEndMeters", float(hv[1]))
	helper.PlaceCamera(world, 1500.0, 0.0, 0.0)
	if not await _wait_builds(2):
		_fail("macro map never became valid")
		return
	await _frames(60)
	if not await _readback():
		_fail("readback timed out")
		return
	fair_pos = _pick_fair()
	if _arg("fair", "") != "":
		var xy := _arg("fair", "").split(",")
		fair_pos = Vector2(float(xy[0]), float(xy[1]))
		print("CLOUD_LAYERS_CAPTURE fair pos forced ", fair_pos)
	print("CLOUD_LAYERS_CAPTURE effective handoff (start, end, reach) ", bridge.call("GetEffectiveCloudHandoff"))
	var far_vp := world.get_node_or_null("ScaledFarViewport")
	if far_vp != null:
		print("CLOUD_LAYERS_CAPTURE ", far_vp.get("FarLayerSkyStatus"))
	for shot in _arg("shots", "fair").split(","):
		await _shoot(shot)
	if far_vp != null:
		print("CLOUD_LAYERS_CAPTURE end ", far_vp.get("FarLayerSkyStatus"), " fine_worst_ms=", far_vp.get("FarFineAtlasWorstFrameMilliseconds"))
	print("CLOUD_LAYERS_CAPTURE=PASS out=", out_dir)
	finished = true
	quit(0)

func _shoot(shot: String):
	match shot:
		"fair":
			_clear()
			for hi in 2:
				var h := 420.0 if hi == 0 else 1500.0
				for pitch in [85.0, 30.0, 10.0, 0.0]:
					await _camera(fair_pos, h, 0.0, pitch, settle if (hi == 0 and pitch == 85.0) else 30)
					await _save("fair_%s_p%02d" % ["g" if hi == 0 else "h", int(pitch)])
		"overcast":
			await _place(10, 4, 60000.0, 7000.0, fair_pos)
			for hi in 2:
				var h := 420.0 if hi == 0 else 1500.0
				for pitch in [85.0, 30.0, 0.0]:
					await _camera(fair_pos, h, 0.0, pitch, settle if (hi == 0 and pitch == 85.0) else 40)
					await _save("overcast_%s_p%02d" % ["g" if hi == 0 else "h", int(pitch)])
			await _camera(fair_pos, 12000.0, 0.0, -85.0, 60)
			await _save("overcast_plan")
			_clear()
		"edge":
			await _place(10, 4, 60000.0, 7000.0, fair_pos)
			var from := fair_pos + Vector2(-0.85 * 60000.0, 0.0)
			for pitch in [15.0, 4.0]:
				await _camera(from, 420.0, 0.0, pitch, settle)
				await _save("edge_g_p%02d" % int(pitch))
			await _camera(from, 1500.0, 0.0, 8.0, 40)
			await _save("edge_h_p08")
			_clear()
		"asdeck":
			await _place(7, 2, 60000.0, 7000.0, fair_pos)
			for pitch in [60.0, 25.0, 8.0]:
				await _camera(fair_pos, 420.0, 0.0, pitch, settle if pitch == 60.0 else 40)
				await _save("asdeck_g_p%02d" % int(pitch))
			_clear()
		"deck":
			var site := _pick_low()
			var cases := _arg("deck-cases", "clear,ns,as,asthin").split(",")
			var first := 400
			if cases.has("clear"):
				_clear()
				await _frames(30)
				await _deck_shots("clear", site, [85.0, 30.0], first)
				first = settle
			if cases.has("ns"):
				await _place(10, 4, 60000.0, 7000.0, site)
				await _deck_shots("ns", site, [85.0, 30.0, 10.0], first)
				first = settle
				await _camera(site, 12000.0, 0.0, -85.0, 60)
				await _save("deck_ns_plan")
			if cases.has("as"):
				await _place(7, 2, 60000.0, 7000.0, site)
				await _deck_shots("as", site, [60.0, 25.0, 8.0], first)
				first = settle
			if cases.has("asthin"):
				# Thin As: the camera under the bank's outer falloff (normalised 0.95 of the 75 km major axis, influence about 0.45).
				await _place(7, 2, 60000.0, 7000.0, site - Vector2(0.95 * 75000.0, 0.0))
				await _deck_shots("asthin", site, [60.0, 25.0], first)
			_clear()
		"cirrus":
			var cirrus_cases := _arg("cirrus-cases", "fair,cs,front,above").split(",")
			if cirrus_cases.has("fair"):
				_clear()
				await _frames(60)
				if await _readback():
					print("CLOUD_LAYERS_CAPTURE cirrus fair map high coverage at fair_pos ", probe.LayersAt(probe.BaseAroundMeters + fair_pos.x, probe.BaseAcrossMeters + fair_pos.y).z)
				await _cirrus_shots("fair", fair_pos, 420.0, [85.0, 60.0, 30.0, 10.0, 0.0], true)
			if cirrus_cases.has("cs"):
				await _place(5, 2, 60000.0, 13000.0, fair_pos, 0.0, 0.3)
				await _cirrus_shots("cs", fair_pos, 420.0, [85.0, 60.0, 30.0, 10.0, 0.0], true)
			if cirrus_cases.has("front"):
				await _place(5, 2, 60000.0, 13000.0, fair_pos, 0.0, 0.3)
				var cirrus_from := fair_pos + Vector2(-1.15 * 75000.0, 0.0)
				await _cirrus_shots("front", cirrus_from, 420.0, [30.0, 10.0, 3.0], false)
			if cirrus_cases.has("above"):
				await _place(5, 2, 60000.0, 13000.0, fair_pos, 0.0, 0.3)
				for pitch in [-30.0, -85.0]:
					await _camera(fair_pos, 11000.0, 0.0, pitch, settle if pitch == -30.0 else 40)
					await _save("cirrus_above_m%02d" % int(-pitch))
			_clear()
		"cirrusorbit":
			await _place(5, 2, 60000.0, 13000.0, fair_pos, 0.0, 0.3)
			var cn := int(_arg("orbit-frames", "120"))
			for i in cn:
				var cyaw := 360.0 * float(i) / float(cn)
				await _camera(fair_pos, 420.0, cyaw, 35.0, settle if i == 0 else 2)
				await _save("frame%08d" % i)
			_clear()
		"plan":
			_clear()
			await _camera(fair_pos, 12000.0, 0.0, -85.0, settle)
			await _save("plan_fair_12k")
			await _camera(fair_pos, 7500.0, 0.0, -85.0, 40)
			await _save("plan_fair_7k")
		"shadow":
			# WO4 shadow consistency: fair then overcast, looking down at the ground from 1.5 km and 420 m (cloud shadows on the terrain),
			# plus the overhead views of the same pose for comparing the cloud pattern with the shadow pattern.
			for case in ["fair", "overcast"]:
				if case == "overcast":
					await _place(10, 4, 60000.0, 7000.0, fair_pos)
				else:
					_clear()
				for pose in [[1500.0, -85.0], [1500.0, -45.0], [420.0, -20.0], [420.0, 85.0], [1500.0, 85.0]]:
					await _camera(fair_pos, pose[0], 0.0, pose[1], settle if pose == [1500.0, -85.0] else 40)
					await _save("shadow_%s_%s_p%d" % [case, "g" if pose[0] < 1000.0 else "h", int(pose[1])])
				_dump_shadow_map("shadowmap_" + case)
			_clear()
		"perf":
			# GPU windows at fixed poses (day, clocks paused): fair (Cu + Ac) and overcast (Nimbostratus core), ground camera pitch 20.
			# --perf-cases=fair,overcast,fair,overcast --perf-seconds=10; one line per window with the cloud GPU scopes.
			for case in _arg("perf-cases", "fair,overcast,fair,overcast,fair,overcast").split(","):
				if case == "overcast":
					await _place(10, 4, 60000.0, 7000.0, fair_pos)
				else:
					_clear()
					await _frames(60)
				await _camera(fair_pos, 420.0, 0.0, float(_arg("perf-pitch", "20")), 150)
				helper.PerfBegin(world)
				# The scaled far viewport's own GPU time (the far cloud layer sky renders there; GPU.Clouds does not include it).
				var far_rid := RID()
				var far_vp_node := world.get_node_or_null("ScaledFarViewport/FarRingViewport") as SubViewport
				if far_vp_node != null:
					far_rid = far_vp_node.get_viewport_rid()
					RenderingServer.viewport_set_measure_render_time(far_rid, true)
				var far_ms: Array = []
				var perf_end := Time.get_ticks_msec() + int(float(_arg("perf-seconds", "10")) * 1000.0)
				while Time.get_ticks_msec() < perf_end:
					await process_frame
					if far_rid.is_valid():
						far_ms.append(RenderingServer.viewport_get_measured_render_time_gpu(far_rid))
				far_ms.sort()
				var far_median: float = far_ms[far_ms.size() / 2] if far_ms.size() > 0 else -1.0
				print("CLOUD_LAYERS_CAPTURE perf ", case, " ", helper.PerfEnd(world), " | far_viewport_gpu=median %.3f n %d" % [far_median, far_ms.size()])
			_clear()
		"handoff":
			var hyaw := float(_arg("handoff-yaw", "0"))
			var hfirst := true
			# An open horizon: the low site (or --low), with the cases centred on it.
			var hsite := _pick_low()
			var hground: float = probe.TerrainHeight(world, hsite.x, hsite.y) + 25.0
			var saved_fair := fair_pos
			fair_pos = hsite
			for hcase in _arg("handoff-cases", "fair,overcast,asdeck,cs").split(","):
				await _place_case(hcase)
				for hi in 2:
					if not _arg("handoff-heights", "g,h").split(",").has("g" if hi == 0 else "h"):
						continue
					var hh := hground if hi == 0 else 1500.0
					for pitch_s in _arg("handoff-pitches", "1,3,6,10").split(","):
						var pitch := float(pitch_s)
						await _camera(fair_pos, hh, hyaw, pitch, settle if hfirst else (90 if pitch == 1.0 else 40))
						hfirst = false
						await _save("handoff_%s_%s_p%02d" % [hcase, "g" if hi == 0 else "h", int(pitch)])
			fair_pos = saved_fair
			_clear()
		"far150":
			# WO6 follow-up: a manual As bank placed --far150-km (default 150) down the ring from the low site, seen from the site (yaw 0).
			var fsite := _pick_low()
			var fground: float = probe.TerrainHeight(world, fsite.x, fsite.y) + 25.0
			var fsaved := fair_pos
			await _place(7, 2, 60000.0, 7000.0, fsite + Vector2(float(_arg("far150-km", "150")) * 1000.0, 0.0))
			for km in [0, 60, 100, 130, 150, 165, 200]:
				print("CLOUD_LAYERS_CAPTURE far150 layers at +%d km (low, mid, high, weatherness) " % km, probe.LayersAt(probe.BaseAroundMeters + fsite.x + km * 1000.0, probe.BaseAcrossMeters + fsite.y))
			var ffirst := true
			for fh in [fground, 1500.0]:
				for fp in [1.0, 3.0]:
					await _camera(fsite, fh, 0.0, fp, settle if ffirst else 60)
					ffirst = false
					await _save("far150_%s_p%02d" % ["g" if fh == fground else "h", int(fp)])
			fair_pos = fsaved
			_clear()
		"dolly":
			var dsite := _pick_low()
			var dsaved := fair_pos
			fair_pos = dsite
			await _place_case(_arg("dolly-case", "fair"))
			var dn := int(_arg("orbit-frames", "180"))
			var dstep := float(_arg("dolly-step", "250"))
			var dh := float(_arg("dolly-h", "420"))
			var dpitch := float(_arg("dolly-pitch", "3"))
			var dfar := world.get_node_or_null("ScaledFarViewport")
			var dcpu: Array = []
			for i in dn:
				await _camera(fair_pos + Vector2(dstep * i, 0.0), dh, 0.0, dpitch, settle if i == 0 else 2)
				if dfar != null and i > 5:
					dcpu.append(float(dfar.get("FarFineAtlasLastFrameMilliseconds")))
				await _save("frame%08d" % i)
			if dcpu.size() > 0:
				dcpu.sort()
				print("CLOUD_LAYERS_CAPTURE fine_cpu_ms median %.3f p95 %.3f max %.3f n %d publications %d" % [dcpu[dcpu.size() / 2], dcpu[int(dcpu.size() * 0.95)], dcpu[dcpu.size() - 1], dcpu.size(), int(dfar.get("FarFineAtlasPublicationCount"))])
			fair_pos = dsaved
			_clear()
		"fineduty":
			# Fine far layer atlas duty cycle with the cloud clock running (the other shots pause it): stationary at the fair site,
			# then flying along yaw 0 at --duty-speed m/s, --duty-frames frames each. Prints the fraction of frames spent building and
			# the per-frame CPU (all frames, and building frames only). No captures.
			var clouds := world.get_node("RingVolumetricClouds")
			var duty_far := world.get_node_or_null("ScaledFarViewport")
			if duty_far == null:
				return
			clouds.set("CloudSimulationPaused", false)
			var duty_n := int(_arg("duty-frames", "3600"))
			var duty_speed := float(_arg("duty-speed", "250"))
			for leg in ["stationary", "flight"]:
				await _camera(fair_pos, 420.0, 0.0, 3.0, 30)
				var all_ms: Array = []
				var build_ms: Array = []
				var pubs0 := int(duty_far.get("FarFineAtlasPublicationCount"))
				var sim0 := float(clouds.get("CloudSimulationTimeSeconds"))
				var t0 := Time.get_ticks_msec()
				var pos := fair_pos
				for i in duty_n:
					if leg == "flight":
						pos.x += duty_speed * root.get_process_delta_time()
						helper.PlaceCameraAt(world, pos.x, pos.y, 420.0, 0.0, 3.0)
					await process_frame
					var ms := float(duty_far.get("FarFineAtlasLastFrameMilliseconds"))
					all_ms.append(ms)
					if bool(duty_far.get("FarFineAtlasBuildInProgress")) or ms > 0.1:
						build_ms.append(ms)
				all_ms.sort()
				build_ms.sort()
				var wall_s := (Time.get_ticks_msec() - t0) * 0.001
				print("CLOUD_LAYERS_CAPTURE fineduty %s frames %d wall_s %.1f sim_s %.1f publications %d duty %.3f all_ms median %.3f p95 %.3f mean %.4f build_ms median %.3f p95 %.3f last_build_frames %d" % [
					leg, duty_n, wall_s, float(clouds.get("CloudSimulationTimeSeconds")) - sim0,
					int(duty_far.get("FarFineAtlasPublicationCount")) - pubs0, float(build_ms.size()) / duty_n,
					all_ms[all_ms.size() / 2], all_ms[int(all_ms.size() * 0.95)], all_ms.reduce(func(a, b): return a + b, 0.0) / duty_n,
					build_ms[build_ms.size() / 2] if build_ms.size() > 0 else 0.0, build_ms[int(build_ms.size() * 0.95)] if build_ms.size() > 0 else 0.0,
					int(duty_far.get("FarFineAtlasLastBuildFrames"))])
			clouds.set("CloudSimulationPaused", true)
		"interp":
			_clear()
			# Gameplay runs with physics interpolation on (GameplayMotionSettings); this harness leaves it off unless asked.
			var interp_was: bool = physics_interpolation
			physics_interpolation = true
			await _camera(fair_pos, 420.0, 0.0, 3.0, 60)
			var far_cam := world.get_node_or_null("ScaledFarViewport/FarRingViewport/FarRingCamera") as Camera3D
			var main_cam := root.get_viewport().get_camera_3d()
			print("CLOUD_LAYERS_CAPTURE interp tree physics_interpolation=", physics_interpolation,
				" far_mode=", far_cam.physics_interpolation_mode if far_cam != null else -1)
			for jump in [20000.0, 0.0]:
				helper.PlaceCameraAt(world, fair_pos.x + jump, fair_pos.y, 420.0, 0.0, 3.0)
				for f in 4:
					await RenderingServer.frame_post_draw
					if far_cam != null and main_cam != null:
						var fd: float = far_cam.get_global_transform_interpolated().origin.distance_to(far_cam.global_transform.origin) * 1000.0
						var md: float = main_cam.get_global_transform_interpolated().origin.distance_to(main_cam.global_transform.origin)
						print("CLOUD_LAYERS_CAPTURE interp jump=%d frame=%d far_render_vs_target_m=%.1f main_render_vs_target_m=%.1f" % [int(jump), f, fd, md])
			physics_interpolation = interp_was
		"s6perf":
			# WO5b: far viewport GPU of the fixed layer sky vs the pre-fix copy (--before-shader), A-B-A-B-A per case, pitch --perf-pitch (3).
			var megap = load("res://tools/MegastormCapture.cs").new()
			var far_vp_node := world.get_node_or_null("ScaledFarViewport/FarRingViewport") as SubViewport
			var far_rid := far_vp_node.get_viewport_rid() if far_vp_node != null else RID()
			if far_rid.is_valid():
				RenderingServer.viewport_set_measure_render_time(far_rid, true)
			for c in _arg("s6fix-cases", "fair,overcast,storm").split(","):
				world.get_node("RingVolumetricClouds").UnregisterManualWeather("sunshine_preview")
				match c:
					"overcast":
						await _place(10, 4, 60000.0, 7000.0, fair_pos)
					"storm":
						_clear()
						helper.PlaceStorm(world, fair_pos.x + 120000.0, fair_pos.y, 0.0, 30000.0)
					_:
						_clear()
				await _camera(fair_pos, 420.0, float(_arg("perf-yaw", "0")), float(_arg("perf-pitch", "3")), 150)
				for variant in ["after", "before", "after", "before", "after"]:
					megap.SetLayerSkyShader(world, "res://materials/ring_far_cloud_layer_sky.gdshader" if variant == "after" else _arg("before-shader", ""))
					await _frames(45)
					var far_ms: Array = []
					var perf_end := Time.get_ticks_msec() + int(float(_arg("perf-seconds", "8")) * 1000.0)
					while Time.get_ticks_msec() < perf_end:
						await process_frame
						if far_rid.is_valid():
							far_ms.append(RenderingServer.viewport_get_measured_render_time_gpu(far_rid))
					far_ms.sort()
					var med: float = far_ms[far_ms.size() / 2] if far_ms.size() > 0 else -1.0
					var p95: float = far_ms[int(far_ms.size() * 0.95)] if far_ms.size() > 0 else -1.0
					print("CLOUD_LAYERS_CAPTURE s6perf %s %s far_viewport_gpu median %.3f p95 %.3f n %d" % [c, variant, med, p95, far_ms.size()])
				megap.SetLayerSkyShader(world, "res://materials/ring_far_cloud_layer_sky.gdshader")
			world.get_node("RingVolumetricClouds").UnregisterManualWeather("sunshine_preview")
			_clear()
		"s6fix":
			var mega = load("res://tools/MegastormCapture.cs").new()
			var live := "res://materials/ring_far_cloud_layer_sky.gdshader"
			var before := _arg("before-shader", "")
			var views := [[0.0, 3.0, "y000"], [90.0, 3.0, "y090"], [180.0, 3.0, "y180"], [0.0, 20.0, "arch"]]
			await _camera(fair_pos, 420.0, 0.0, 0.0, 2)
			print("CLOUD_LAYERS_CAPTURE s6fix sun yaw/pitch ", probe.SunYawPitch(world))
			for c in _arg("s6fix-cases", "fair,overcast,storm").split(","):
				world.get_node("RingVolumetricClouds").UnregisterManualWeather("sunshine_preview")
				match c:
					"overcast":
						await _place(10, 4, 60000.0, 7000.0, fair_pos)
					"storm":
						_clear()
						helper.PlaceStorm(world, fair_pos.x + 120000.0, fair_pos.y, 0.0, 30000.0)
						await _frames(120)
					_:
						_clear()
						await _frames(60)
				for i in views.size():
					var v = views[i]
					mega.SetLayerSkyShader(world, live)
					await _camera(fair_pos, 420.0, v[0], v[1], settle if i == 0 else 60)
					await _save("s6fix_%s_%s_after" % [c, v[2]])
					if before != "":
						mega.SetLayerSkyShader(world, before)
						await _frames(40)
						await _save("s6fix_%s_%s_before" % [c, v[2]])
				mega.SetLayerSkyShader(world, live)
			world.get_node("RingVolumetricClouds").UnregisterManualWeather("sunshine_preview")
			_clear()
		"orbit":
			_clear()
			var n := int(_arg("orbit-frames", "300"))
			var h := float(_arg("orbit-h", "1500"))
			for i in n:
				var yaw := 360.0 * float(i) / float(n)
				await _camera(fair_pos, h, yaw, 12.0, settle if i == 0 else 2)
				await _save("frame%08d" % i)

# Writes the published near and far GPU cloud shadow maps (combined R/G occlusion, white = full shadow) as greyscale PNGs.
func _dump_shadow_map(label: String):
	var clouds = world.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	if clouds == null:
		return
	var rd := RenderingServer.get_rendering_device()
	for cascade in 2:
		var texture := clouds.call("get_gpu_cloud_shadow_texture", cascade) as Texture2DRD
		if texture == null:
			continue
		var bytes := rd.texture_get_data(texture.texture_rd_rid, 0)
		var size := int(sqrt(bytes.size() / 4.0))
		var image := Image.create(size, size, false, Image.FORMAT_L8)
		var total := 0.0
		for y in size:
			for x in size:
				var offset := (y * size + x) * 4
				var occlusion := 1.0 - (1.0 - bytes.decode_half(offset)) * (1.0 - bytes.decode_half(offset + 2))
				total += occlusion
				image.set_pixel(x, y, Color(occlusion, occlusion, occlusion))
		image.save_png(ProjectSettings.globalize_path(out_dir) + "/%s_%s.png" % [label, "near" if cascade == 0 else "far"])
		print("CLOUD_LAYERS_CAPTURE ", label, " ", "near" if cascade == 0 else "far", " mean_occlusion=", total / float(size * size))

func _fail(reason):
	if finished:
		return
	finished = true
	printerr("CLOUD_LAYERS_CAPTURE=FAIL ", reason)
	quit(1)
