extends SceneTree
## Megastorm bolt capture (debug tool, windowed, GPU): a copy of tools/megastorm_real_path_check.gd (same real player path) plus a per-frame
## saving window and bolt diagnostics for the megastorm lightning channel overlay (docs/megastorm-design.md, "Bolt channel visibility").
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
## --midnight    the same at the observer's local midnight (night sheets).
## --save-xi-km=A:B  save every --frame-stride'th frame (default 2) as <out>/fNNNNN.png while the observer's xi is in [A, B] km, log one
##               MEGASTORM_BOLT line per saved frame (rendered bolt id, kind, pulse, distance, screen points) and end the run once xi passes B.
##               Launch with the engine flag --fixed-fps 30 so every frame is 1/30 s of game time (Movie Maker timing without its full-run output).
## --sunshine-ab  diagnostic: while saving, every second megastorm bolt renders with the Sunshine compositor effect disabled.
## --channels-off  set LocalWeatherEffects.EnableLightningChannels false (the pre-fix spatial bolt mesh, for A/B evidence).
## --old-wide    with --channels-off: widen the old spatial bolt mesh (core 10 m, glow 80 m) so width is not the reason it is unseen (A/B evidence).
## --aba=S       perf: after the spawn, 6 phases of S seconds alternating channels on/off/on/off/on/off (A-B-A-B-A-B), printing the median and
##               p95 viewport GPU ms of each phase (MEGASTORM_ABA lines). Run WITHOUT --fixed-fps and only with no other Godot process live.
## --pitch=D     hold the camera pitch (degrees) from the spawn on (yaw stays facing the wall).
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
	if _arg("channels-off", "") != "":
		_world.get_node("LocalWeatherEffects").set("EnableLightningChannels", false)
	if _arg("old-wide", "") != "":
		_world.get_node("LocalWeatherEffects").set("BoltCoreWidthMeters", 10.0)
		_world.get_node("LocalWeatherEffects").set("BoltGlowWidthMeters", 80.0)
	if _arg("noon", "") != "":
		await _set_local_noon()
	elif _arg("midnight", "") != "":
		await _set_local_noon(true)
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
	var explorer: Node = _world.get_node("Explorer")
	if _arg("pitch", "") != "":
		var pose_p: Dictionary = explorer.call("GetCanonicalPoseMeters")
		explorer.call("SetPresentationView", float(pose_p["yaw_deg"]), float(_arg("pitch", "0")))
	if _arg("aba", "") != "":
		await _aba(float(_arg("aba", "8")))
		print("MEGASTORM_REAL=DONE out=", _out)
		quit(0)
		return
	var save_range := _arg("save-xi-km", "")
	var save_lo := NAN
	var save_hi := NAN
	if save_range.contains(":"):
		save_lo = float(save_range.get_slice(":", 0)) * 1000.0
		save_hi = float(save_range.get_slice(":", 1)) * 1000.0
	var stride := maxi(1, int(_arg("frame-stride", "2")))
	var sunshine_ab := _arg("sunshine-ab", "") != ""
	var weather_fx: Node = _world.get_node_or_null("LocalWeatherEffects")
	var window_frames := 0
	var saved := 0
	var last_event := 0
	var bolt_index := 0
	var sunshine_off := false
	while true:
		var elapsed := float(Time.get_ticks_msec() - start) / 1000.0
		if elapsed > seconds:
			break
		if not is_nan(save_lo):
			var mega_s: Node = _world.get_node("RingMegastorm")
			var pose_s: Dictionary = explorer.call("GetCanonicalPoseMeters")
			var q_s: Dictionary = mega_s.call("query_megastorm", float(pose_s["around_m"]), float(pose_s["across_m"]), -1.0)
			var xi_s := float(q_s["xi_meters"]) if bool(q_s["active"]) else -INF
			if xi_s > save_hi:
				break
			if xi_s >= save_lo:
				var diag: Dictionary = weather_fx.call("get_local_shared_lightning_diagnostics") if weather_fx != null else {}
				var ev := int(diag.get("event_id", 0))
				var kind := int(diag.get("system_kind", -1))
				if ev != last_event:
					if sunshine_off:
						_set_sunshine_enabled(true)
						sunshine_off = false
					if ev != 0 and kind == 6:
						bolt_index += 1
						if sunshine_ab and bolt_index % 2 == 0:
							_set_sunshine_enabled(false)
							sunshine_off = true
					last_event = ev
				if window_frames % stride == 0:
					var name := "f%05d" % saved
					saved += 1
					print("MEGASTORM_BOLT ", name, " xi=%.0f" % xi_s, " ", _bolt_line(diag, sunshine_off))
					await RenderingServer.frame_post_draw
					root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/" + name + ".png"))
				window_frames += 1
				await process_frame
				continue
		if elapsed >= next_log:
			next_log += log_every
			print("MEGASTORM_REAL e=%.0f " % elapsed, _probe.State(_world))
		if elapsed >= next_shot:
			next_shot += shot_every
			await _shot("t%03d" % int(elapsed))
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
	if sunshine_off:
		_set_sunshine_enabled(true)
	print("MEGASTORM_REAL=DONE out=", _out)
	quit(0)

func _set_sunshine_enabled(on: bool) -> void:
	var env := _world.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if env == null or env.compositor == null:
		return
	for e in env.compositor.compositor_effects:
		var sc: Script = e.get_script() if e != null else null
		if sc != null and sc.resource_path.contains("SunshineClouds2/SunshineClouds"):
			e.enabled = on

## One MEGASTORM_BOLT field set: the rendered bolt (event id, kind, scythe, pulse), its ground distance and the screen points of its ground and cloud ends.
func _bolt_line(diag: Dictionary, sunshine_off: bool) -> String:
	var ev := int(diag.get("event_id", 0))
	var ch := " channels=%d segs=%d ch_us=%.0f" % [int(diag.get("channel_bolts", 0)), int(diag.get("channel_segments", 0)), float(diag.get("channel_us", 0.0))]
	if ev == 0:
		return "bolt=none sunshine=%s" % ("off" if sunshine_off else "on") + ch
	var cam := root.get_viewport().get_camera_3d()
	var g: Vector3 = diag.get("world_ground", Vector3.ZERO)
	var c: Vector3 = diag.get("world_cloud", Vector3.ZERO)
	var text := "bolt=%d kind=%d scythe=%s pulse=%.3f weight=%.2f" % [ev, int(diag.get("system_kind", -1)), str(diag.get("scythe", false)), float(diag.get("pulse", 0.0)), float(diag.get("visual_weight", 0.0))]
	if cam != null:
		var size := root.get_viewport().get_visible_rect().size
		var gs := cam.unproject_position(g)
		var cs := cam.unproject_position(c)
		text += " dist=%.0fm ground_scr=(%.0f,%.0f,%s) cloud_scr=(%.0f,%.0f,%s) cloud_h=%.0fm" % [cam.global_position.distance_to(g), gs.x, gs.y,
			str(not cam.is_position_behind(g) and Rect2(Vector2.ZERO, size).has_point(gs)), cs.x, cs.y,
			str(not cam.is_position_behind(c) and Rect2(Vector2.ZERO, size).has_point(cs)), g.distance_to(c)]
	return text + " sunshine=%s" % ("off" if sunshine_off else "on") + ch

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
func _set_local_noon(midnight := false) -> void:
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
		var daylight := float(cycle.call("get_daylight_factor"))
		if (daylight <= 0.001) if midnight else (daylight >= 0.999):
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
	cycle.call("set_cycle_fraction", noon)
	cycle.set("Paused", true)
	await create_timer(3.0).timeout
	print("MEGASTORM_REAL noon fraction=%.3f daylight=%.3f full_range=%d%%" % [noon, float(cycle.call("get_daylight_factor")), full.size()])

func _aba(period: float) -> void:
	var vp := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp, true)
	var fx: Node = _world.get_node("LocalWeatherEffects")
	await create_timer(5.0).timeout
	for phase in 6:
		var on := phase % 2 == 0
		fx.set("EnableLightningChannels", on)
		await create_timer(1.0).timeout
		var gpu: Array[float] = []
		var bolts := 0
		var frames := 0
		var start := Time.get_ticks_msec()
		while Time.get_ticks_msec() - start < int(period * 1000.0):
			await RenderingServer.frame_post_draw
			gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(vp))
			bolts += int(fx.call("get_local_shared_lightning_diagnostics").get("channel_bolts", 0))
			frames += 1
		gpu.sort()
		print("MEGASTORM_ABA phase=%d channels=%s frames=%d median_gpu_ms=%.3f p95_gpu_ms=%.3f mean_slots=%.1f" % [phase, "on" if on else "off", frames,
			gpu[gpu.size() / 2], gpu[int(gpu.size() * 0.95)], float(bolts) / max(frames, 1)])
	fx.set("EnableLightningChannels", true)
