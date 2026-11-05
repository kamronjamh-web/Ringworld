extends SceneTree
## Megastorm WO13 field-strike capture (debug tool, windowed, GPU). The real player path of tools/megastorm_bolt_capture.gd (start menu -> Continue -> grave-key
## Spawn megastorm), then a stub containment field is installed (the field is not built, so the game has no provider) and frames are saved while a field strike is
## live and on screen. Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (it saves the world on exit):
##   $env:APPDATA = "...\.godot\test_runs\appdata_spawncheck_wo13"
##   tools/use_godot_48.ps1 --fixed-fps 30 --script res://tools/megastorm_field_strike_capture.gd ++ --spawn-km=50 --speed=5 --midnight --pitch=18 --out=res://.godot/captures/megastorm/wo13/raw
## --spawn-km=N --speed=V   spawn distance ahead (km) and wall speed (5 m/s keeps the wall where it was spawned)
## --noon | --midnight      pause the celestial clock at local noon / midnight
## --pitch=D --yaw-offset=D hold the camera (degrees)
## --freq=F                 Lightning frequency scale for the capture (strike stream rate multiplier, default 2)
## --reduced                Reduced flashes on
## --max-strikes=N          stop after N distinct strikes were saved (default 6); --seconds=S hard time limit; --frame-stride=K save every K-th frame of a strike
## --settle=S               seconds to wait after the hand-off before spawning
## --no-field               do not install the stub field (the null-provider check: no field strikes, no layer draw)
## --altitude=M           fly M metres above the saved spot (above the trees) before the capture
## --aim [--aim-pitch-extra=D]  turn the camera toward each new strike (the yaw sign is measured and flipped when wrong)
## --sunshine-off          disable the Sunshine cloud compositor (shows the channels without cloud in front: a rendering proof, not the player view)
## --hide-hud
## Prints MEGASTORM_FIELD_STRIKE lines per saved frame and MEGASTORM_REAL=DONE.

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/MegastormRealPathProbe.cs")
const FIELD := preload("res://tools/MegastormFieldStrikeCaptureProbe.cs")

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _probe: RefCounted
var _field: RefCounted
var _out := "res://.godot/captures/megastorm/wo13/raw"

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

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("appdata_spawncheck"):
		push_error("Refusing to run: user:// (%s) is not an appdata_spawncheck copy." % user_dir)
		quit(2)
		return
	_out = _arg("out", _out)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out))
	_probe = PROBE.new()
	_field = FIELD.new()
	_menu = MENU.instantiate()
	_menu.live_world_settings_path = "user://menu_live_world_spawncheck.tres"
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	await _wait_until(func() -> bool: return root.get_node_or_null("RingWarmWorld") != null, 120000)
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
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
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
	if _arg("noon", "") != "":
		await _set_local_noon()
	elif _arg("midnight", "") != "":
		await _set_local_noon(true)
	if _arg("no-field", "") == "":
		_field.call("InstallStubField", 10000.0, 40000.0, 318000.0)
	_field.call("SetStrikeScale", float(_arg("freq", "2")), _arg("reduced", "") != "")
	var spawn_km := float(_arg("spawn-km", "50"))
	await create_timer(float(_arg("settle", "10"))).timeout
	var drawer: Node = _world.get_node("LiveTuningDrawer")
	drawer.call("SetBenchmarkDrawerOpen", true)
	await process_frame
	if _arg("speed", "") != "":
		drawer.call("SetMegastormTuningValue", "SpeedMetersPerSecond", float(_arg("speed", "5")))
	var distance := drawer.find_child("MegastormSpawnDistanceKm", true, false) as HSlider
	var button := drawer.find_child("MegastormSpawn", true, false) as Button
	if distance != null:
		distance.max_value = maxf(distance.max_value, spawn_km)
		distance.value = spawn_km
	if button != null:
		button.pressed.emit()
	await process_frame
	drawer.call("SetBenchmarkDrawerOpen", false)
	await process_frame
	print("MEGASTORM_REAL spawned ", _probe.State(_world))
	var explorer: Node = _world.get_node("Explorer")
	if _arg("sunshine-off", "") != "":
		_set_sunshine_enabled(false)
	if _arg("altitude", "") != "":
		var pose_a: Dictionary = explorer.call("GetCanonicalPoseMeters")
		explorer.call("TeleportToCanonicalMeters", float(pose_a["around_m"]), float(pose_a["vertical_m"]) + float(_arg("altitude", "300")), float(pose_a["across_m"]), float(pose_a["yaw_deg"]), float(pose_a["pitch_deg"]), true)
		await create_timer(2.0).timeout
	if _arg("pitch", "") != "" or _arg("yaw-offset", "") != "":
		var pose_p: Dictionary = explorer.call("GetCanonicalPoseMeters")
		explorer.call("SetPresentationView", float(pose_p["yaw_deg"]) + float(_arg("yaw-offset", "0")), float(_arg("pitch", str(pose_p["pitch_deg"]))))
	await create_timer(3.0).timeout
	if _arg("aba", "") != "":
		await _aba(float(_arg("aba", "10")))
		print("MEGASTORM_REAL=DONE out=", _out)
		quit(0)
		return
	var seconds := float(_arg("seconds", "300"))
	if _arg("baseline-shot", "") != "":
		await RenderingServer.frame_post_draw
		root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/baseline.png"))
		print("MEGASTORM_FIELD_STRIKE baseline saved count=", int((_field.call("Live", _world) as Dictionary)["count"]))
	var max_strikes := int(_arg("max-strikes", "6"))
	var stride := maxi(1, int(_arg("frame-stride", "2")))
	var start := Time.get_ticks_msec()
	var saved_ids := {}
	var frame_of := {}
	var next_log := 0.0
	var aim := _arg("aim", "") != ""
	var aim_pitch_extra := float(_arg("aim-pitch-extra", "0"))
	var yaw_sign := 1.0
	var aimed_id := 0
	var ab_done := false
	while true:
		var elapsed := float(Time.get_ticks_msec() - start) / 1000.0
		var live: Dictionary = _field.call("Live", _world)
		if elapsed > seconds or (saved_ids.size() >= max_strikes and int(live["count"]) == 0):
			break
		if elapsed >= next_log:
			next_log += 10.0
			print("MEGASTORM_FIELD_STRIKE e=%.0f count=%d arcs=%d far_bound=%s far_live=%d saved=%d" % [elapsed, int(live["count"]), int(live["arcs"]), str(live["far_bound"]), int(live["far_live"]), saved_ids.size()])
		var cam := root.get_viewport().get_camera_3d()
		if aim and cam != null and int(live["count"]) > 0 and int(live["id"]) != aimed_id and float(live["age"]) < 0.5:
			aimed_id = int(live["id"])
			await _aim_at(explorer, cam, (live["world_base"] as Vector3).lerp(live["world_contact"] as Vector3, 0.55), yaw_sign, aim_pitch_extra)
			var err_after := _azimuth_error(explorer, cam, (live["world_base"] as Vector3).lerp(live["world_contact"] as Vector3, 0.55))
			if absf(err_after) > 25.0:
				yaw_sign = -yaw_sign
				await _aim_at(explorer, cam, (live["world_base"] as Vector3).lerp(live["world_contact"] as Vector3, 0.55), yaw_sign, aim_pitch_extra)
				print("MEGASTORM_FIELD_STRIKE aim sign flipped to ", yaw_sign, " error now ", _azimuth_error(explorer, cam, (live["world_base"] as Vector3).lerp(live["world_contact"] as Vector3, 0.55)))
		if int(live["count"]) > 0 and cam != null and (saved_ids.size() < max_strikes or saved_ids.has(int(live["id"]))):
			var b: Vector3 = live["world_base"]
			var c: Vector3 = live["world_contact"]
			var rect := Rect2(Vector2.ZERO, root.get_viewport().get_visible_rect().size).grow(120.0)
			var on_screen := (not cam.is_position_behind(b) and rect.has_point(cam.unproject_position(b))) or (not cam.is_position_behind(c) and rect.has_point(cam.unproject_position(c)))
			if on_screen:
				var id := int(live["id"])
				var n := int(frame_of.get(id, 0))
				frame_of[id] = n + 1
				if n % stride == 0:
					if not saved_ids.has(id):
						saved_ids[id] = saved_ids.size()
					var name := "s%02d_f%03d" % [int(saved_ids[id]), n]
					print("MEGASTORM_FIELD_STRIKE ", name, " id=", id, " kind=", int(live["kind"]), " dist=%.0f" % float(live["distance"]), " length=%.0f" % float(live["length"]),
						" pulse=%.3f progress=%.2f age=%.2f/%.2f" % [float(live["pulse"]), float(live["progress"]), float(live["age"]), float(live["duration"])],
						" scr_base=%s scr_contact=%s" % [str(cam.unproject_position(b).round()), str(cam.unproject_position(c).round())],
						" dbg=", str(live.get("channel_dbg", "")), " main_w=%.2f far_live=%d channel_bolts=%d segs=%d ch_us=%.0f" % [float(live["main_weight"]), int(live["far_live"]), int(live.get("channel_bolts", 0)), int(live.get("channel_segments", 0)), float(live.get("channel_us", 0.0))])
					await RenderingServer.frame_post_draw
					root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/" + name + ".png"))
					if _arg("ab-layer", "") != "" and not ab_done and float(live["pulse"]) > 0.3 and int(live["far_live"]) > 0:
						ab_done = true
						root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/ab_on.png"))
						_field.call("SetLayerEnabled", false)
						await process_frame
						await RenderingServer.frame_post_draw
						root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/ab_off.png"))
						_field.call("SetLayerEnabled", true)
		await process_frame
	print("MEGASTORM_REAL=DONE out=", _out, " strikes=", saved_ids.size())
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


## --aba=S: perf A-B-A-B-A-B of the field strikes (stub field installed = on, provider null = off) over S seconds each, viewport GPU ms median / p95 and the
## mean number of live strikes and channel slots. Run WITHOUT --fixed-fps and with no other Godot process live.
func _aba(period: float) -> void:
	var vp := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp, true)
	await create_timer(5.0).timeout
	for phase in 6:
		var on := phase % 2 == 0
		if on:
			_field.call("InstallStubField", 10000.0, 40000.0, 318000.0)
		else:
			_field.call("ClearField")
		await create_timer(2.0).timeout
		var gpu: Array[float] = []
		var live := 0
		var slots := 0
		var frames := 0
		var start := Time.get_ticks_msec()
		while Time.get_ticks_msec() - start < int(period * 1000.0):
			await RenderingServer.frame_post_draw
			gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(vp))
			var l: Dictionary = _field.call("Live", _world)
			live += int(l["count"])
			slots += int(l.get("channel_bolts", 0))
			frames += 1
		gpu.sort()
		print("MEGASTORM_ABA phase=%d field=%s frames=%d median_gpu_ms=%.3f p95_gpu_ms=%.3f mean_live=%.2f mean_slots=%.2f" % [phase, "on" if on else "off", frames,
			gpu[gpu.size() / 2], gpu[int(gpu.size() * 0.95)], float(live) / max(frames, 1), float(slots) / max(frames, 1)])
	_field.call("InstallStubField", 10000.0, 40000.0, 318000.0)

## Horizontal angle (degrees, + = to the right) from the camera heading to a world point, in the surface-up horizontal plane.
func _azimuth_error(explorer: Node, cam: Camera3D, point: Vector3) -> float:
	var up: Vector3 = (explorer as Node3D).global_transform.basis.y
	var fwd := -cam.global_transform.basis.z
	fwd = (fwd - up * fwd.dot(up)).normalized()
	var right := fwd.cross(up).normalized()
	var d := point - cam.global_position
	var dh := (d - up * d.dot(up)).normalized()
	return rad_to_deg(atan2(dh.dot(right), dh.dot(fwd)))

func _aim_at(explorer: Node, cam: Camera3D, point: Vector3, yaw_sign: float, pitch_extra: float) -> void:
	var up: Vector3 = (explorer as Node3D).global_transform.basis.y
	var d := point - cam.global_position
	var horizontal := (d - up * d.dot(up)).length()
	var elevation := rad_to_deg(atan2(d.dot(up), horizontal))
	var pose: Dictionary = explorer.call("GetCanonicalPoseMeters")
	explorer.call("SetPresentationView", float(pose["yaw_deg"]) + yaw_sign * _azimuth_error(explorer, cam, point), clampf(elevation + pitch_extra, 0.0, 75.0))
	await process_frame
	await process_frame

func _set_sunshine_enabled(on: bool) -> void:
	var env := _world.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if env == null or env.compositor == null:
		return
	for e in env.compositor.compositor_effects:
		var sc: Script = e.get_script() if e != null else null
		if sc != null and sc.resource_path.contains("SunshineClouds2/SunshineClouds"):
			e.enabled = on
