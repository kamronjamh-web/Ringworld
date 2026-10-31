extends SceneTree
## Megastorm real player path check (debug tool, windowed, GPU). Runs the path a player takes, not the capture harness:
## start menu -> warm world -> Continue (saved world restore) -> hand-off -> the RingMegastorm node as the game runs it.
## Logs MEGASTORM_REAL t=... lines (tools/MegastormRealPathProbe.cs) every --log-every seconds and saves the ACTUAL game camera every
## --shot-every seconds to --out.
##
## Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (the script refuses otherwise; it saves the world on exit):
##   $env:APPDATA = "...\.godot\test_runs\appdata_spawncheck"
##   tools/use_godot_48.ps1 --script res://tools/megastorm_real_path_check.gd ++ --seconds=240 [--spawn-km=60] [--speed=350] [--look-up-xi-km=10]
## Godot's "++" separator is used because PowerShell drops a bare "--" when calling use_godot_48.ps1.
##
## --spawn-km=N   after the hand-off, open the grave-key drawer, set "Spawn distance ahead (km)" to N and press "Spawn megastorm".
## --speed=V      set the menu megastorm speed (m/s) through the drawer before spawning (shortens the approach).
## --look-up-xi-km=K  once the observer is K km behind the edge (inside), save one shot pitched up 80 degrees.
## --hide-hud     hide the HUD canvas layers in the saved frames.
## --noon        set the celestial clock to the observer's local noon (paused) before spawning (WO14 daytime sheets).
## --night       same, at local midnight (the noon fraction + 0.5).
## --sun-elev=D  with --noon: walk from noon toward sunrise (--sun-side=set: sunset) to the first time the sun is at or below D degrees.
## --yaw=D / --pitch=D  after the spawn, hold the camera at this yaw / pitch (degrees; re-applied before every shot). --yaw-rel=D adds D to the
##               saved yaw instead (facing stays on the spawn side).
## --debug-mask=N  RingSunshineCloudBridge.CameraIndependentDebugMask for A/B shots (debug only).
## --history-fade-off / --history-floor=F  megastorm cloud-history fade A/B (debug properties on the bridge).
## --xi-names    name shots tNNN_xiK (K = observer xi in km, rounded).
## --probe       after the spawn and --scan-settle seconds, one shot per layer toggle (base, Sunshine off, rain veil off, curtains off,
##               far + companion off, base again) to attribute pixels; spawn with a low --speed so the storm barely moves.
## Prints MEGASTORM_REAL=DONE at the end (this is a capture tool, not a pass/fail gate).

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/MegastormRealPathProbe.cs")

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _probe: RefCounted
var _out := "res://.godot/captures/megastorm/spawn/raw"
var _lifted := false

func _view(explorer: Node, yaw: float, pitch: float) -> void:
	if _lifted:
		explorer.call("SetScriptedFlightCommand", Vector3.ZERO, yaw, pitch)
	else:
		explorer.call("SetPresentationView", yaw, pitch)

func _initialize() -> void:
	create_timer(1800.0).timeout.connect(func(): print("MEGASTORM_REAL watchdog"); quit(3))
	call_deferred("_run")

func _arg(name: String, default_value: String) -> String:
	for a in OS.get_cmdline_user_args():
		if a == "--" + name:
			return "1"
		if a.begins_with("--" + name + "="):
			return a.substr(name.length() + 3)
	return default_value

func _wait_until(condition: Callable, timeout_ms: int) -> bool:
	var deadline := Time.get_ticks_msec() + timeout_ms
	while Time.get_ticks_msec() < deadline:
		if condition.call():
			return true
		await process_frame
	return condition.call()

func _shot(name: String) -> void:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	var path := ProjectSettings.globalize_path(_out + "/" + name + ".png")
	image.save_png(path)
	print("MEGASTORM_REAL shot ", name, " ", _probe.State(_world))

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("appdata_spawncheck"):
		push_error("Refusing to run: user:// (%s) is not an appdata_spawncheck copy." % user_dir)
		quit(2)
		return
	_out = _arg("out", _out)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out))
	_probe = PROBE.new()
	print("MEGASTORM_REAL user_args=", OS.get_cmdline_user_args(), " args=", OS.get_cmdline_args())
	_menu = MENU.instantiate()
	_menu.live_world_settings_path = "user://menu_live_world_spawncheck.tres"
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if _warm == null:
		print("MEGASTORM_REAL no RingWarmWorld")
		quit(1)
		return
	_warm.handed_off.connect(func(w: Node) -> void: _handed_off = w)
	if not await _wait_until(func() -> bool: return int(_warm.state) == 2, 300000):
		print("MEGASTORM_REAL warm world never ready")
		quit(1)
		return
	_world = _warm.world
	print("MEGASTORM_REAL warm ready ", _probe.State(_world))
	if _arg("prep-ground", "") != "":
		await _prep_ground()
		return
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
	print("MEGASTORM_REAL menu ready ", _probe.State(_world))
	_menu.continue_button.pressed.emit()
	if not await _wait_until(func() -> bool: return _handed_off != null, 240000):
		print("MEGASTORM_REAL hand-off never completed")
		quit(1)
		return
	print("MEGASTORM_REAL handed off ", _probe.State(_world))
	# --terrain-scan: max terrain height per azimuth (degrees from the -around axis, + = +across) out to 200 km, to find relief near the wall's path.
	if _arg("terrain-scan", "") != "":
		var pose_t: Dictionary = _world.get_node("Explorer").call("GetCanonicalPoseMeters")
		var terrain_t: Node = _world.get_node("Terrain3DGameplay")
		for az in range(-60, 61, 10):
			var best_h := -1.0e9
			var best_d := 0.0
			var d := 2000.0
			while d <= 200000.0:
				var a: float = float(pose_t["around_m"]) - cos(deg_to_rad(float(az))) * d
				var c: float = float(pose_t["across_m"]) + sin(deg_to_rad(float(az))) * d
				var g := float(terrain_t.call("SampleHeight", a, c))
				if is_finite(g) and g > best_h:
					best_h = g
					best_d = d
				d += 1000.0
			print("MEGASTORM_REAL terrain az=%+d max_h=%.0f at %.0f km" % [az, best_h, best_d / 1000.0])
		quit(0)
		return
	if _arg("hide-hud", "") != "":
		for n in _world.get_children():
			if n is CanvasLayer and n.name != "LiveTuningDrawer":
				n.visible = false
	if _arg("noon", "") != "" or _arg("night", "") != "":
		await _set_local_noon()
	if _arg("history-fade-off", "") != "":
		_world.get_node("SunshineCloudBridge").set("DebugMegastormHistoryFadeOff", true)
	if _arg("history-floor", "") != "":
		_world.get_node("SunshineCloudBridge").set("DebugMegastormHistoryFloorOverride", float(_arg("history-floor", "0.15")))
	if _arg("debug-mask", "") != "":
		_world.get_node("SunshineCloudBridge").set("CameraIndependentDebugMask", int(_arg("debug-mask", "0")))
	# --lift=M: hand the body to scripted flight and climb M metres (holds there; views then go through the scripted command).
	_lifted = _arg("lift", "") != ""
	if _lifted:
		var ex: Node = _world.get_node("Explorer")
		ex.call("BeginScriptedFlight")
		var pose_l: Dictionary = ex.call("GetCanonicalPoseMeters")
		var climb := float(_arg("lift", "300"))
		var rate := 100.0
		ex.call("SetScriptedFlightCommand", Vector3(0.0, rate, 0.0), float(pose_l["yaw_deg"]), float(pose_l["pitch_deg"]))
		await create_timer(climb / rate).timeout
		ex.call("SetScriptedFlightCommand", Vector3.ZERO, float(pose_l["yaw_deg"]), float(pose_l["pitch_deg"]))
		await create_timer(2.0).timeout
		print("MEGASTORM_REAL lifted ", _probe.State(_world))
	var spawn_km := float(_arg("spawn-km", "-1"))
	if spawn_km > 0.0:
		await create_timer(float(_arg("settle", "10"))).timeout
		var drawer: Node = _world.get_node("LiveTuningDrawer")
		drawer.call("SetBenchmarkDrawerOpen", true)
		await process_frame
		if _arg("speed", "") != "":
			drawer.call("SetMegastormTuningValue", "SpeedMetersPerSecond", float(_arg("speed", "350")))
		var distance := drawer.find_child("MegastormSpawnDistanceKm", true, false) as HSlider
		var button := drawer.find_child("MegastormSpawn", true, false) as Button
		print("MEGASTORM_REAL drawer distance_slider=", distance != null, " spawn_button=", button != null)
		if distance != null:
			distance.value = spawn_km
		if button != null:
			button.pressed.emit()
		await process_frame
		drawer.call("SetBenchmarkDrawerOpen", false)
		await process_frame
		print("MEGASTORM_REAL spawned ", _probe.State(_world))
	var seconds := float(_arg("seconds", "240"))
	var log_every := float(_arg("log-every", "5"))
	var shot_every := float(_arg("shot-every", "20"))
	var look_up_xi := float(_arg("look-up-xi-km", "nan")) if _arg("look-up-xi-km", "") != "" else NAN
	var start := Time.get_ticks_msec()
	var next_log := 0.0
	var next_shot := 0.0
	var looked_up := false
	# --seq-from-xi=K --seq-frames=N: once the observer's xi reaches K km, save N consecutive frames as <out>/seq/frameNNNNNNNN.png
	# (run with the engine's --fixed-fps 30 for a 30 fps sequence; the ffmpeg_tools.ps1 frame.png convention).
	var seq_from := float(_arg("seq-from-xi", "nan")) if _arg("seq-from-xi", "") != "" else NAN
	var seq_done := false
	var explorer: Node = _world.get_node("Explorer")
	var hold_view := _arg("yaw", "") != "" or _arg("yaw-rel", "") != "" or _arg("pitch", "") != ""
	var hold_yaw := 0.0
	var hold_pitch := 0.0
	if hold_view:
		var pose0: Dictionary = explorer.call("GetCanonicalPoseMeters")
		hold_yaw = float(_arg("yaw", str(float(pose0["yaw_deg"]) + float(_arg("yaw-rel", "0")))))
		hold_pitch = float(_arg("pitch", str(float(pose0["pitch_deg"]))))
		_view(explorer, hold_yaw, hold_pitch)
		print("MEGASTORM_REAL hold view yaw=%.2f pitch=%.2f" % [hold_yaw, hold_pitch])
	if _arg("probe", "") != "":
		await create_timer(float(_arg("scan-settle", "8"))).timeout
		await _probe_toggles()
	# --yaw-scan=a:b:... (or a,b,... outside PowerShell): one shot per yaw offset (degrees, relative to the held / saved yaw) right after the spawn, to pick a heading.
	if _arg("yaw-scan", "") != "":
		var pose_s: Dictionary = explorer.call("GetCanonicalPoseMeters")
		var base_yaw := hold_yaw if hold_view else float(pose_s["yaw_deg"])
		var base_pitch := hold_pitch if hold_view else float(pose_s["pitch_deg"])
		await create_timer(float(_arg("scan-settle", "8"))).timeout
		for part in _arg("yaw-scan", "").replace(":", ",").split(","):
			_view(explorer, base_yaw + float(part), base_pitch)
			await create_timer(1.5).timeout
			await _shot("scan_yaw%+d" % int(float(part)))
		_view(explorer, base_yaw, base_pitch)
	while true:
		var elapsed := float(Time.get_ticks_msec() - start) / 1000.0
		if elapsed > seconds:
			break
		if elapsed >= next_log:
			next_log += log_every
			print("MEGASTORM_REAL e=%.0f " % elapsed, _probe.State(_world))
		if elapsed >= next_shot:
			next_shot += shot_every
			if hold_view:
				_view(explorer, hold_yaw, hold_pitch)
			if _arg("hide-hud", "") != "":
				_world.get_node("LiveTuningDrawer").call("SetBenchmarkDrawerOpen", false)
			var shot_name := "t%03d" % int(elapsed)
			if _arg("xi-names", "") != "":
				var mega_n: Node = _world.get_node("RingMegastorm")
				var pose_n: Dictionary = explorer.call("GetCanonicalPoseMeters")
				var q_n: Dictionary = mega_n.call("query_megastorm", float(pose_n["around_m"]), float(pose_n["across_m"]), -1.0)
				shot_name += "_xi%d" % int(round(float(q_n["xi_meters"]) / 1000.0))
			await _shot(shot_name)
		if not seq_done and not is_nan(seq_from):
			var mega_q: Node = _world.get_node("RingMegastorm")
			var pose_q: Dictionary = explorer.call("GetCanonicalPoseMeters")
			var qq: Dictionary = mega_q.call("query_megastorm", float(pose_q["around_m"]), float(pose_q["across_m"]), -1.0)
			if bool(qq["active"]) and float(qq["xi_meters"]) >= seq_from * 1000.0:
				seq_done = true
				var seq_dir := ProjectSettings.globalize_path(_out + "/seq")
				DirAccess.make_dir_recursive_absolute(seq_dir)
				if hold_view:
					_view(explorer, hold_yaw, hold_pitch)
				print("MEGASTORM_REAL seq start ", _probe.State(_world))
				for fi in int(_arg("seq-frames", "300")):
					await RenderingServer.frame_post_draw
					root.get_texture().get_image().save_png(seq_dir + "/frame%08d.png" % fi)
				print("MEGASTORM_REAL seq end ", _probe.State(_world))
		if not looked_up and not is_nan(look_up_xi):
			var mega: Node = _world.get_node("RingMegastorm")
			var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
			var q: Dictionary = mega.call("query_megastorm", float(pose["around_m"]), float(pose["across_m"]), -1.0)
			if bool(q["active"]) and float(q["xi_meters"]) >= look_up_xi * 1000.0:
				looked_up = true
				var yaw := float(pose["yaw_deg"])
				var pitch := float(pose["pitch_deg"])
				explorer.call("SetPresentationView", yaw, 80.0)
				for i in 8:
					await process_frame
				await _shot("inside_up_t%03d" % int(elapsed))
				explorer.call("SetPresentationView", yaw, pitch)
		await process_frame
	print("MEGASTORM_REAL=DONE out=", _out)
	quit(0)

func _compositor_effects(script_part: String) -> Array:
	var found := []
	for n in root.find_children("*", "WorldEnvironment", true, false):
		var c: Compositor = (n as WorldEnvironment).compositor
		if c == null:
			continue
		for e in c.compositor_effects:
			if e != null and e.get_script() != null and String(e.get_script().resource_path).contains(script_part):
				found.append(e)
	for n in root.find_children("*", "Camera3D", true, false):
		var c2: Compositor = (n as Camera3D).compositor
		if c2 == null:
			continue
		for e2 in c2.compositor_effects:
			if e2 != null and e2.get_script() != null and String(e2.get_script().resource_path).contains(script_part):
				found.append(e2)
	return found

func _probe_set(kind: String, off: bool) -> void:
	match kind:
		"sunshine":
			_world.get_node("SunshineCloudBridge").call("SetRenderSuspended", off)
		"farbackdrop":
			for e in _compositor_effects("RingFarBackdropComposite.gd"):
				e.enabled = not off
		"kzero":
			var mega_now = RenderingServer.global_shader_parameter_get("ringworld_storm_light_mega")
			RenderingServer.global_shader_parameter_set("ringworld_storm_light_mega", Vector4(0.0 if off else 0.06, mega_now.y, mega_now.z, mega_now.w))
		"veil":
			for e in _compositor_effects("RingRainHazeComposite.gd"):
				e.enabled = not off
		"curtains":
			var c := _world.find_child("DistantRainCurtains", true, false)
			if c != null:
				c.set("Enabled", not off)
				c.visible = not off
		"darkness0":
			_world.get_node("SunshineCloudBridge").set("StormDarkness", 0.0 if off else 0.9)
		"far":
			var f := _world.get_node_or_null("ScaledFarViewport")
			if f != null:
				f.set("FarMegastormSuppressed", off)
				f.set("FarMegastormMainSuppressed", off)

func _probe_levels() -> void:
	var bridge: Node = _world.get_node("SunshineCloudBridge")
	var mega: Node = _world.get_node("RingMegastorm")
	var pose: Dictionary = _world.get_node("Explorer").call("GetCanonicalPoseMeters")
	for sgn in [-1.0, 1.0]:
		for d in [0.0, 4000.0, 8000.0, 18000.0, 28000.0, 38000.0, 60000.0]:
			var a: float = float(pose["around_m"]) + float(sgn) * float(d)
			var c: float = float(pose["across_m"])
			var q: Dictionary = mega.call("query_megastorm", a, c, -1.0)
			var terms: Dictionary = bridge.call("GetStormLightTermsAt", a, c)
			print("MEGASTORM_REAL level dir=%+d d=%.0fkm xi=%.1fkm D=%.3f E=%.3f" % [int(sgn), d / 1000.0, float(q["xi_meters"]) / 1000.0, float(terms.get("D", -1.0)) - float(terms.get("E", 0.0)), float(terms.get("E", -1.0))])

func _probe_toggles() -> void:
	await _probe_levels()
	await _shot("probe_base")
	for kind in _arg("probe-kinds", "sunshine:veil:curtains:far:farbackdrop:kzero:darkness0").split(":"):
		_probe_set(kind, true)
		await create_timer(1.0).timeout
		await _shot("probe_off_" + kind)
		_probe_set(kind, false)
		await create_timer(1.0).timeout
	print("MEGASTORM_REAL probe effects sunshine=%d veil=%d" % [_compositor_effects("SunshineClouds.gd").size(), _compositor_effects("RingRainHazeComposite.gd").size()])
	await _shot("probe_base2")

## --prep-ground[=yaw_deg]: rewrites the copy's saved player to stand on dry ground near the saved spot (noclip off, pitch 4, optional yaw), then
## quits, so the next run's Continue lands a walking player (the capture harness leaves arbitrary flight poses in shared profiles).
func _prep_ground() -> void:
	var pose: Dictionary = _warm.bridge.call("GetHomePose")
	var terrain: Node = _world.get_node("Terrain3DGameplay")
	var spot := {}
	var radius := 0.0
	while radius <= 30000.0 and spot.is_empty():
		var steps := 1 if radius == 0.0 else maxi(8, int(TAU * radius / 250.0))
		for i in steps:
			var a := float(pose["around_m"]) + cos(TAU * i / steps) * radius
			var c := float(pose["across_m"]) + sin(TAU * i / steps) * radius
			var g := float(terrain.call("SampleHeight", a, c))
			if is_finite(g) and g >= 25.0:
				spot = {"around": a, "across": c, "ground": g}
				break
		radius += 250.0
	if spot.is_empty():
		print("MEGASTORM_REAL prep: no land within 30 km")
		quit(1)
		return
	await _menu._release_warm_world()
	var path := ProjectSettings.globalize_path(get_root().get_node("WorldSession").call("_slot_directory", int(get_root().get_node("WorldSession").get_live_menu_slot()))).path_join("world_state_surface_runtime_v1.json")
	var text := FileAccess.get_file_as_string(path)
	var head := text.find("\"Player\"")
	var tail := text.substr(head)
	tail = RegEx.create_from_string("\"VerticalMeters\":\\s*[-0-9.eE+]+").sub(tail, "\"VerticalMeters\": %.4f" % (float(spot["ground"]) + 1.05))
	tail = RegEx.create_from_string("\"AroundMeters\":\\s*[-0-9.eE+]+").sub(tail, "\"AroundMeters\": %.4f" % float(spot["around"]))
	tail = RegEx.create_from_string("\"AcrossMeters\":\\s*[-0-9.eE+]+").sub(tail, "\"AcrossMeters\": %.4f" % float(spot["across"]))
	tail = RegEx.create_from_string("\"Pitch\":\\s*[-0-9.eE+]+").sub(tail, "\"Pitch\": 4")
	if _arg("prep-ground", "") != "1":
		tail = RegEx.create_from_string("\"Yaw\":\\s*[-0-9.eE+]+").sub(tail, "\"Yaw\": %.5f" % deg_to_rad(float(_arg("prep-ground", "0"))))
	tail = RegEx.create_from_string("\"Noclip\":\\s*(true|false)").sub(tail, "\"Noclip\": false")
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text.substr(0, head) + tail)
	file.close()
	print("MEGASTORM_REAL prep ground=%.1f around=%.1f across=%.1f path=%s" % [float(spot["ground"]), float(spot["around"]), float(spot["across"]), path])
	quit(0)

# --noon (WO14 daytime sheets): scans the day-night cycle fraction, keeps the middle of the range where the observer's daylight factor is full
# (local noon) and pauses the celestial clock there. The weather clock keeps running, so the megastorm still moves.
func _set_local_noon() -> void:
	var cycle: Node = _world.get_node_or_null("RingDayNightCycle")
	if cycle == null:
		print("MEGASTORM_REAL noon: no RingDayNightCycle")
		return
	var full: Array[float] = []
	for i in 100:
		var f := float(i) / 100.0
		cycle.call("set_cycle_fraction", f)
		await process_frame
		await process_frame
		if float(cycle.call("get_daylight_factor")) >= 0.999:
			full.append(f)
	if full.is_empty():
		print("MEGASTORM_REAL noon: no full-daylight fraction found")
		return
	# The full-daylight fractions form one arc (possibly wrapping past 1): take its circular middle.
	var start := full[0]
	for i in range(1, full.size()):
		if full[i] - full[i - 1] > 0.015:
			start = full[i]
			break
	var length := full.size() / 100.0
	var noon := fposmod(start + 0.5 * (length - 0.01), 1.0)
	if _arg("night", "") != "":
		noon = fposmod(noon + 0.5, 1.0)
	# --sun-elev=D: walk back from noon (the rising side) to the first fraction whose sun elevation is at or below D degrees (morning, low sun).
	if _arg("sun-elev", "") != "":
		var want := float(_arg("sun-elev", "10"))
		var sign := -1.0 if _arg("sun-side", "rise") == "rise" else 1.0
		var f := noon
		for i in 200:
			f = fposmod(noon + sign * 0.0025 * float(i), 1.0)
			cycle.call("set_cycle_fraction", f)
			await process_frame
			await process_frame
			if float(cycle.get("SunElevationDegrees")) <= want:
				break
		noon = f
	cycle.call("set_cycle_fraction", noon)
	cycle.set("Paused", true)
	await create_timer(3.0).timeout
	print("MEGASTORM_REAL noon fraction=%.3f daylight=%.3f sun_elev=%.1f full_range=%d%%" % [noon, float(cycle.call("get_daylight_factor")), float(cycle.get("SunElevationDegrees")), full.size()])
