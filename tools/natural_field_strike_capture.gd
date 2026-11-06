extends SceneTree
## Containment field WO14 real-path capture (debug capture tool, windowed, GPU; manual, not a gate): a natural storm parked against the across-0 wall
## (StormAnvilCapture.PlaceStormAbsolute, the registered kind 4 storm the natural stream treats like any front) makes a natural field strike through the
## real layer, adapter, bus, channel layer and WO4 ripple, at night. Real player path (start menu -> warm world -> Continue -> hand-off).
##
## Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (the world is saved on exit):
##   $env:APPDATA = "C:\Projects\Ringworld\.godot\test_runs\appdata_spawncheck_wall"
##   tools/use_godot_48.ps1 --fixed-fps 30 --fixed-fps 30 --script res://tools/natural_field_strike_capture.gd ++ --pose=vantage --out=.godot/captures/field/wo14/raw/vantage
## --pose=vantage|floor   vantage: 19.2 km up (above every cloud, under the field), 28 km in from the wall, 30 km before the strike, aimed at the contact point;
##                        floor: 1.5 km above the floor, 14 km in, 25 km before the strike, aimed at the contact point (the storm is between)
## --perf                 A-B-A-B-A-B of the viewport GPU time (run WITHOUT --fixed-fps): natural stream on (the same strike replayed every 2.4 s) vs off (strike scale 0)
## --min-gap=M --dist=D   first strike with a gap of at least M metres (longer channel); vantage distance D before the strike (default 30000)
## --noon                 day instead of night      --settle=S  seconds to settle at the pose (default 8)
## Prints NATURAL_FIELD lines and NATURAL_FIELD=DONE.

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/ContainmentRippleCaptureProbe.cs")
const NPROBE := preload("res://tools/NaturalFieldStrikeCaptureProbe.cs")
const STORM := preload("res://tools/StormAnvilCapture.cs")
const STORM_ID := -1003

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _out := ""
var _explorer: Node
var _grid: Node
var _probe: RefCounted
var _np: RefCounted
var _noon_cache := -1.0
var _a0 := 0.0
var _circumference := 0.0
var _width := 0.0
var _top := 0.0

func _initialize() -> void:
	create_timer(3000.0).timeout.connect(func(): print("NATURAL_FIELD watchdog"); quit(3))
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

func _frames(n: int) -> void:
	for i in n:
		await process_frame

func _hide_hud() -> void:
	for n in _world.get_children():
		if n is CanvasLayer:
			n.visible = false
	for n in root.get_children():
		if n is CanvasLayer:
			n.visible = false

func _save(name: String) -> void:
	await RenderingServer.frame_post_draw
	var err := root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/" + name + ".png"))
	if err != OK:
		print("NATURAL_FIELD save failed ", name, " err=", err)

func _teleport(around: float, vertical: float, across: float, yaw: float, pitch: float) -> void:
	_explorer.call("TeleportToCanonicalMeters", fposmod(around, _circumference), vertical, across, yaw, pitch, true)
	_explorer.call("SetPresentationView", yaw, pitch)

func _aim(around: float, vertical: float, across: float) -> Vector2:
	var a: Vector2 = _probe.call("AimAngles", _world, around, vertical, across)
	_explorer.call("SetPresentationView", a.x, a.y)
	return a

func _find_local_noon(cycle: Node) -> float:
	if _noon_cache >= 0.0:
		return _noon_cache
	var full: Array[float] = []
	for i in 100:
		var f := float(i) / 100.0
		cycle.call("set_cycle_fraction", f)
		await process_frame
		await process_frame
		if float(cycle.call("get_daylight_factor")) >= 0.999:
			full.append(f)
	if full.is_empty():
		return 0.5
	var start := full[0]
	for i in range(1, full.size()):
		if full[i] - full[i - 1] > 0.015:
			start = full[i]
			break
	_noon_cache = fposmod(start + 0.5 * (full.size() / 100.0 - 0.01), 1.0)
	return _noon_cache

func _set_time_of_day(tod: String) -> void:
	var cycle: Node = _world.get_node_or_null("RingDayNightCycle")
	if cycle == null:
		return
	var noon := await _find_local_noon(cycle)
	var f := noon if tod == "noon" else fposmod(noon + 0.5, 1.0)
	cycle.call("set_cycle_fraction", f)
	cycle.set("Paused", true)
	await create_timer(2.0).timeout
	print("NATURAL_FIELD time %s fraction=%.4f daylight=%.3f" % [tod, f, float(cycle.call("get_daylight_factor"))])

func _weather() -> Node:
	return _world.get_node_or_null("RingVolumetricClouds")

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("appdata_spawncheck"):
		push_error("Refusing to run: user:// (%s) is not an appdata_spawncheck copy." % user_dir)
		quit(2)
		return
	var pose := _arg("pose", "vantage")
	_out = _arg("out", ".godot/captures/field/wo14/raw/" + pose)
	if not _out.begins_with("res://"):
		_out = "res://" + _out
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out))
	_probe = PROBE.new()
	_np = NPROBE.new()
	_menu = MENU.instantiate()
	_menu.live_world_settings_path = "user://menu_live_world_spawncheck.tres"
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if _warm == null:
		print("NATURAL_FIELD no RingWarmWorld")
		quit(1)
		return
	_warm.handed_off.connect(func(w: Node) -> void: _handed_off = w)
	if not await _wait_until(func() -> bool: return int(_warm.state) == 2, 300000):
		print("NATURAL_FIELD warm world never ready")
		quit(1)
		return
	_world = _warm.world
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
	_menu.continue_button.pressed.emit()
	if not await _wait_until(func() -> bool: return _handed_off != null, 240000):
		print("NATURAL_FIELD hand-off never completed")
		quit(1)
		return
	_explorer = _world.get_node("Explorer")
	_grid = _world.get_node("ChunkGridManager")
	_circumference = float(_grid.call("GetCanonicalRingCircumferenceMeters64"))
	_width = float(_grid.get("RingAcrossWidthMeters"))
	_top = float(_grid.call("GetStructuralWallTopGlobalY"))
	var p: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	_a0 = float(p["around_m"])
	var far_side := float(p["across_m"]) > _width * 0.5
	print("NATURAL_FIELD handed off circumference=%.1f width=%.1f wall_top=%.1f around=%.1f far_side=%s" % [_circumference, _width, _top, _a0, str(far_side)])
	if not await _wait_until(func() -> bool: return bool(_probe.call("Ready", _world)), 60000):
		print("NATURAL_FIELD field or bus not ready: ", _probe.call("ProfileInfo", _world))
		quit(1)
		return
	print("NATURAL_FIELD profile ", _probe.call("ProfileInfo", _world))
	_hide_hud()
	_teleport(_a0, _top + 1.7, -64.0, 0.0, -8.0)
	await create_timer(2.0).timeout
	await _set_time_of_day("noon" if _arg("noon", "") != "" else "night")
	var helper: RefCounted = STORM.new()
	helper.call("RainOff", _world)
	helper.call("SetLifecycle", 1.6)
	# The across-0 wall (the saved player may stand on the across-W one; the poses below are for across-0).
	var storm_around := _a0 + 20000.0
	if not helper.call("PlaceStormAbsolute", _world, storm_around, 1500.0, 0.0, 12000.0):
		print("NATURAL_FIELD storm placement failed: ", helper.get("ErrorText"))
		quit(1)
		return
	var weather := _weather()
	weather.set("CloudSimulationPaused", true)
	var t0: float = _np.call("Clock", _world)
	var strike: Dictionary = _np.call("NextStrike", _world, t0, 14400.0, STORM_ID, false, float(_arg("min-gap", "0")))
	if not bool(strike["found"]):
		print("NATURAL_FIELD no strike of the parked storm in 2 h from t=%.1f" % t0)
		quit(1)
		return
	print("NATURAL_FIELD strike id=%d start=%.2f (t0=%.2f) duration=%.2f around=%.0f across=%.0f contact=%.0f base=%.0f gap=%.0f wall=%.0f energy=%.2f" % [int(strike["id"]), float(strike["start"]), t0,
		float(strike["duration"]), float(strike["around"]), float(strike["across"]), float(strike["contact"]), float(strike["base"]), float(strike["gap"]), float(strike["wall"]), float(strike["energy"])])
	var ga: float = float(strike["around"])
	var gx: float = float(strike["across"])
	var gv: float = float(strike["contact"])
	var settle := float(_arg("settle", "8"))
	if pose == "floor":
		_teleport(ga - 25000.0, 1500.0, 14000.0, 0.0, 0.0)
	else:
		_teleport(ga - float(_arg("dist", "30000")), 19200.0, 28000.0 if _arg("dist", "") == "" else 20000.0, 0.0, 0.0)
	await create_timer(settle).timeout
	var aim := _aim(ga, gv, gx)
	print("NATURAL_FIELD pose=%s aim yaw=%.2f pitch=%.2f camera=%s" % [pose, aim.x, aim.y, _probe.call("CameraCanonical", _world)])
	await create_timer(2.0).timeout
	if _arg("perf", "") != "":
		await _perf(strike)
	else:
		await _record(strike)
	helper.call("SetLifecycle", NAN)
	print("NATURAL_FIELD=DONE pose=%s out=%s" % [pose, _out])
	quit(0)

func _record(strike: Dictionary) -> void:
	var start := float(strike["start"])
	var duration := float(strike["duration"])
	var weather := _weather()
	# OFF baseline at the same pose: the stream scaled to 0, the clock where the strike would be at its peak.
	_probe.call("SetStrikeScale", 0.0, false)
	_np.call("SetClock", _world, start + 0.45 * duration)
	await _frames(10)
	_hide_hud()
	await _save("off_%08d" % 0)
	print("NATURAL_FIELD off ", _np.call("StreamInfo", _world), " ", _probe.call("DiagLine", _world))
	_probe.call("SetStrikeScale", 1.0, false)
	_np.call("SetClock", _world, start - 2.0)
	weather.set("CloudSimulationPaused", false)
	var i := 0
	var end := start + duration + 2.5
	while float(_np.call("Clock", _world)) < end and i < 400:
		await _save("n_%08d" % i)
		if i % 3 == 0:
			print("NATURAL_FIELD frame=%d %s | %s" % [i, _np.call("StreamInfo", _world), _probe.call("DiagLine", _world)])
			print("NATURAL_FIELD channel frame=%d %s" % [i, _np.call("ChannelInfo", _world)])
		i += 1
		if i % 30 == 0:
			_hide_hud()
	weather.set("CloudSimulationPaused", true)
	print("NATURAL_FIELD recorded frames=%d" % i)

func _gpu_ms(seconds: float, strike: Dictionary) -> Array:
	var vp := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp, true)
	var start := float(strike["start"])
	var samples: Array[float] = []
	var live := 0
	var frames := 0
	var deadline := Time.get_ticks_msec() + int(seconds * 1000.0)
	var last_reset := -100.0
	while Time.get_ticks_msec() < deadline:
		var now := Time.get_ticks_msec() / 1000.0
		if now - last_reset >= 2.4:
			last_reset = now
			_np.call("SetClock", _world, start - 0.2)
		await process_frame
		samples.append(RenderingServer.viewport_get_measured_render_time_gpu(vp))
		frames += 1
		if int(_probe.call("LiveOfKind", _world, 8)) > 0:
			live += 1
	samples.sort()
	var median: float = samples[samples.size() / 2] if samples.size() > 0 else -1.0
	return [median, float(live) / maxf(1.0, float(frames)), frames]

func _perf(strike: Dictionary) -> void:
	var weather := _weather()
	weather.set("CloudSimulationPaused", false)
	var rows := {"on": [], "off": []}
	for rep in 3:
		for state in ["on", "off"]:
			_probe.call("SetStrikeScale", 1.0 if state == "on" else 0.0, false)
			_np.call("SetClock", _world, float(strike["start"]) - 0.2)
			await _frames(30)
			var r: Array = await _gpu_ms(12.0, strike)
			rows[state].append(r)
			print("NATURAL_FIELD perf %s rep=%d median_gpu_ms=%.3f live_strike_share=%.2f frames=%d" % [state, rep, r[0], r[1], r[2]])
	_probe.call("SetStrikeScale", 1.0, false)
	var on_mean := 0.0
	var off_mean := 0.0
	for r in rows["on"]:
		on_mean += float(r[0]) / 3.0
	for r in rows["off"]:
		off_mean += float(r[0]) / 3.0
	print("NATURAL_FIELD perf summary on=%.3f off=%.3f delta=%.3f ms" % [on_mean, off_mean, on_mean - off_mean])
