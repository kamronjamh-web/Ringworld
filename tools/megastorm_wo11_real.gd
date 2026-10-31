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
## --yaw=D / --pitch=D  after the spawn, hold the camera at this yaw / pitch (degrees; re-applied before every shot). --yaw-rel=D adds D to the
##               saved yaw instead (facing stays on the spawn side).
## --debug-mask=N  RingSunshineCloudBridge.CameraIndependentDebugMask for A/B shots (debug only).
## --xi-names    name shots tNNN_xiK (K = observer xi in km, rounded).
## --burst=LO:HI  (WO11 copy) once the observer's xi (km) is in [LO, HI] every frame is saved (burst_NNNN.png) with a BOOMREAL line (hazard node diagnostics, camera shake).
## Prints MEGASTORM_REAL=DONE at the end (this is a capture tool, not a pass/fail gate).

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/MegastormRealPathProbe.cs")

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _probe: RefCounted
var _out := "res://.godot/captures/megastorm/spawn/raw"

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
	if _arg("hide-hud", "") != "":
		for n in _world.get_children():
			if n is CanvasLayer and n.name != "LiveTuningDrawer":
				n.visible = false
	if _arg("noon", "") != "" or _arg("night", "") != "":
		await _set_local_noon()
	if _arg("debug-mask", "") != "":
		_world.get_node("SunshineCloudBridge").set("CameraIndependentDebugMask", int(_arg("debug-mask", "0")))
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
	var burst_n := 0
	var explorer: Node = _world.get_node("Explorer")
	var hold_view := _arg("yaw", "") != "" or _arg("yaw-rel", "") != "" or _arg("pitch", "") != ""
	var hold_yaw := 0.0
	var hold_pitch := 0.0
	if hold_view:
		var pose0: Dictionary = explorer.call("GetCanonicalPoseMeters")
		hold_yaw = float(_arg("yaw", str(float(pose0["yaw_deg"]) + float(_arg("yaw-rel", "0")))))
		hold_pitch = float(_arg("pitch", str(float(pose0["pitch_deg"]))))
		explorer.call("SetPresentationView", hold_yaw, hold_pitch)
		print("MEGASTORM_REAL hold view yaw=%.2f pitch=%.2f" % [hold_yaw, hold_pitch])
	# --yaw-scan=a:b:... (or a,b,... outside PowerShell): one shot per yaw offset (degrees, relative to the held / saved yaw) right after the spawn, to pick a heading.
	if _arg("yaw-scan", "") != "":
		var pose_s: Dictionary = explorer.call("GetCanonicalPoseMeters")
		var base_yaw := hold_yaw if hold_view else float(pose_s["yaw_deg"])
		var base_pitch := hold_pitch if hold_view else float(pose_s["pitch_deg"])
		await create_timer(float(_arg("scan-settle", "8"))).timeout
		for part in _arg("yaw-scan", "").replace(":", ",").split(","):
			explorer.call("SetPresentationView", base_yaw + float(part), base_pitch)
			await create_timer(1.5).timeout
			await _shot("scan_yaw%+d" % int(float(part)))
		explorer.call("SetPresentationView", base_yaw, base_pitch)
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
				explorer.call("SetPresentationView", hold_yaw, hold_pitch)
			if _arg("hide-hud", "") != "":
				_world.get_node("LiveTuningDrawer").call("SetBenchmarkDrawerOpen", false)
			var shot_name := "t%03d" % int(elapsed)
			if _arg("xi-names", "") != "":
				var mega_n: Node = _world.get_node("RingMegastorm")
				var pose_n: Dictionary = explorer.call("GetCanonicalPoseMeters")
				var q_n: Dictionary = mega_n.call("query_megastorm", float(pose_n["around_m"]), float(pose_n["across_m"]), -1.0)
				shot_name += "_xi%d" % int(round(float(q_n["xi_meters"]) / 1000.0))
			await _shot(shot_name)
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
		if _arg("burst", "") != "":
			var span := _arg("burst", "").split(":")
			var mb: Node = _world.get_node("RingMegastorm")
			var pb: Dictionary = explorer.call("GetCanonicalPoseMeters")
			var qb: Dictionary = mb.call("query_megastorm", float(pb["around_m"]), float(pb["across_m"]), -1.0)
			var xi_km := float(qb["xi_meters"]) / 1000.0
			if bool(qb["active"]) and xi_km >= float(span[0]) and xi_km <= float(span[1]):
				var fxd: Dictionary = _world.get_node("MegastormHazardFx").call("GetHazardFxDiagnostics")
				var tr: Node = _world.get_node("GroundTremor")
				print("BOOMREAL n=%d xi=%.0f shake=%.4f blast=%s burst=%.2f debris=%.2f shock=%.2f attached=%s disp=%s" % [burst_n, xi_km * 1000.0, float(tr.get("ShakeDegrees")), fxd["blast"], fxd["burst"], fxd["debris_level"], fxd["shock_amplitude"], fxd["shock_attached"], fxd["shock_dispatches"]])
				await RenderingServer.frame_post_draw
				var img := root.get_texture().get_image()
				img.resize(img.get_width() / 2, img.get_height() / 2)
				img.save_png(ProjectSettings.globalize_path(_out + "/burst_%04d.png" % burst_n))
				burst_n += 1
		await process_frame
	print("MEGASTORM_REAL=DONE out=", _out)
	quit(0)

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
	cycle.call("set_cycle_fraction", noon)
	cycle.set("Paused", true)
	await create_timer(3.0).timeout
	print("MEGASTORM_REAL noon fraction=%.3f daylight=%.3f full_range=%d%%" % [noon, float(cycle.call("get_daylight_factor")), full.size()])
