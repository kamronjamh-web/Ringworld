extends SceneTree
## Emitter field shots real player path capture (debug tool, windowed, GPU). Not a pass/fail gate.
## start menu -> warm world -> Continue (saved world restore) -> hand-off, then noclip views of the regular emitter nearest the player while it fires
## an orb up to the containment field (rise, contact, dispersal), at noon and at night: "close" (2 km from the bore, looking up), "near" (14 km,
## bore and contact in frame) and "far" (150 km). Every second frame from 0.5 s before the launch to 0.5 s after the flare is saved as
## <out>/<tod>_<view>/frameNNNNNNNN.png (ffmpeg_tools.ps1 -Path <dir>/frame.png). Launch with the engine flag --fixed-fps 30 so a frame is 1/30 s of weather time.
##
## Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (the world is saved on exit):
##   $env:APPDATA = "...\.godot\test_runs\appdata_spawncheck_wall"
##   tools/use_godot_48.ps1 --fixed-fps 30 --script res://tools/emitter_field_shot_capture.gd ++ --out=res://.godot/captures/field/emitter_shots/raw
##
## --out=DIR       frame folder root (default res://.godot/captures/field/emitter_shots/raw).
## --times=a:b     times of day (default noon:night; none = no views, e.g. with --perf).
## --views=a:b     views (default close:near:far).
## --interval=S    emitter field shot interval for the run (default 10 s, so the chosen emitter fires every slot).
## --perf          after the views: one A-B-A of the whole-frame GPU ms at the near noon pose with the field shots on / off / on.
## Prints EMITTER_SHOT lines and EMITTER_SHOT=DONE at the end.

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/EmitterFieldShotProbe.cs")

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _out := "res://.godot/captures/field/emitter_shots/raw"
var _explorer: Node
var _grid: Node
var _layer: Node
var _host: Node
var _weather: Node
var _probe: RefCounted
var _noon_cache := -1.0

func _initialize() -> void:
	create_timer(1500.0).timeout.connect(func(): print("EMITTER_SHOT watchdog"); quit(3))
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

func _hide_hud() -> void:
	for n in _world.get_children():
		if n is CanvasLayer:
			n.visible = false
	for n in root.get_children():
		if n is CanvasLayer:
			n.visible = false

func _pose_text() -> String:
	var p: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	return "around=%.1f vertical=%.2f across=%.2f yaw=%.1f pitch=%.1f" % [
		float(p["around_m"]), float(p["vertical_m"]), float(p["across_m"]), float(p["yaw_deg"]), float(p["pitch_deg"])]

func _teleport(around: float, vertical: float, across: float, yaw: float, pitch: float) -> void:
	var c := float(_grid.call("GetCanonicalRingCircumferenceMeters64"))
	_explorer.call("TeleportToCanonicalMeters", fposmod(around, c), vertical, across, yaw, pitch, true)
	_explorer.call("SetPresentationView", yaw, pitch)

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
	print("EMITTER_SHOT time %s fraction=%.4f daylight=%.3f" % [tod, f, float(cycle.call("get_daylight_factor"))])

func _now() -> float:
	return float(_weather.get("CloudSimulationTimeSeconds"))

## Camera pose for a view of the beam: (around, vertical, across, yaw, pitch). The camera stands behind the emitter along -around, 500 m above
## the bore, pitched so the bore and the field contact are both inside the frame (close: the contact is near the top edge).
func _view_pose(view: String, beam: Dictionary) -> Array:
	var d := 2000.0 if view == "close" else (14000.0 if view == "near" else 150000.0)
	var ov := float(beam["origin_vertical"])
	var cam_v := ov + 500.0
	var e_bore := rad_to_deg(atan2(ov - cam_v, d))
	var e_contact := rad_to_deg(atan2(float(beam["contact_vertical"]) - cam_v, d))
	var pitch := 60.0 if view == "close" else 0.5 * (e_bore + e_contact)
	return [float(beam["origin_around"]) - d, cam_v, float(beam["origin_across"]), 0.0, pitch]

func _capture_shot(label: String, beam: Dictionary) -> void:
	var dir := _out + "/" + label
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var shot: Dictionary = _probe.call("NextShot", _layer, int(beam["beam"]), _now() + 1.0, 120.0)
	if shot.is_empty():
		print("EMITTER_SHOT %s no shot within 120 s" % label)
		return
	var launch := float(shot["launch"])
	var stop := launch + float(beam["life"]) + 0.5
	print("EMITTER_SHOT %s shot id=%d launch=%.3f arrival=%.3f energy=%.3f now=%.3f pose %s" % [label, int(shot["id"]), launch, float(shot["arrival"]),
		float(shot["energy"]), _now(), _pose_text()])
	_hide_hud()
	while _now() < launch - 0.5:
		await process_frame
	var saved := 0
	var frame := 0
	var peak_logged := false
	while _now() < stop:
		await RenderingServer.frame_post_draw
		if frame % 2 == 0:
			var image := root.get_texture().get_image()
			image.save_png(ProjectSettings.globalize_path(dir + "/frame%08d.png" % saved))
			saved += 1
		if frame % 15 == 0 or (not peak_logged and _now() >= float(shot["arrival"]) + 0.2):
			if _now() >= float(shot["arrival"]) + 0.2:
				peak_logged = true
			print("EMITTER_SHOT %s f=%d %s | %s" % [label, saved, _probe.call("ShotState", _layer, int(shot["id"])), _probe.call("BusState", _host, int(shot["id"]))])
		frame += 1
		await process_frame
	print("EMITTER_SHOT %s saved %d frames" % [label, saved])

func _gpu_ms(frames: int) -> float:
	var vp := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp, true)
	for i in 10:
		await process_frame
	var total := 0.0
	for i in frames:
		await process_frame
		total += RenderingServer.viewport_get_measured_render_time_gpu(vp)
	return total / float(frames)

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("appdata_spawncheck"):
		push_error("Refusing to run: user:// (%s) is not an appdata_spawncheck copy." % user_dir)
		quit(2)
		return
	_out = _arg("out", _out)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out))
	_menu = MENU.instantiate()
	_menu.live_world_settings_path = "user://menu_live_world_spawncheck.tres"
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if _warm == null:
		print("EMITTER_SHOT no RingWarmWorld")
		quit(1)
		return
	_warm.handed_off.connect(func(w: Node) -> void: _handed_off = w)
	var warm_deadline := Time.get_ticks_msec() + 300000
	while int(_warm.state) != 2 and Time.get_ticks_msec() < warm_deadline:
		await process_frame
	if int(_warm.state) != 2:
		print("EMITTER_SHOT warm world never ready")
		quit(1)
		return
	_world = _warm.world
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
	_menu.continue_button.pressed.emit()
	if not await _wait_until(func() -> bool: return _handed_off != null, 240000):
		print("EMITTER_SHOT hand-off never completed")
		quit(1)
		return
	_explorer = _world.get_node("Explorer")
	_grid = _world.get_node("ChunkGridManager")
	_layer = _world.get_node_or_null("EmitterFieldShots")
	_host = _world.get_node_or_null("ContainmentDisturbances")
	_weather = _world.get_node("RingVolumetricClouds")
	_probe = PROBE.new()
	if _layer == null:
		print("EMITTER_SHOT no EmitterFieldShots node")
		quit(1)
		return
	var grid_layer: Node = _world.get_node("EmitterGrid")
	var settings: Resource = grid_layer.get("Settings")
	var interval := float(_arg("interval", "10"))
	settings.set("FieldShotIntervalSeconds", interval)
	if not await _wait_until(func() -> bool: return not str(_layer.call("DescribeStatus")).contains("no schedule"), 60000):
		print("EMITTER_SHOT schedule never built: ", _layer.call("DescribeStatus"))
		quit(1)
		return
	var circumference := float(_grid.call("GetCanonicalRingCircumferenceMeters64"))
	var p: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	var beam: Dictionary = _probe.call("NearestBeam", _layer, float(p["around_m"]), float(p["across_m"]), circumference)
	print("EMITTER_SHOT handed off interval=%.0f %s beam=%s" % [interval, _pose_text(), str(beam)])
	print("EMITTER_SHOT ", _layer.call("DescribeStatus"))
	for tod in _arg("times", "noon:night").split(":"):
		if tod == "none":
			continue
		var first := true
		for view in _arg("views", "close:near:far").split(":"):
			var pose := _view_pose(view, beam)
			_teleport(pose[0], pose[1], pose[2], pose[3], pose[4])
			if first:
				await _set_time_of_day(tod)
				first = false
			await create_timer(6.0).timeout
			await _capture_shot(tod + "_" + view, beam)
	if _arg("perf", "") != "":
		await _set_time_of_day("noon")
		var pose := _view_pose("near", beam)
		_teleport(pose[0], pose[1], pose[2], pose[3], pose[4])
		await create_timer(6.0).timeout
		var a1 := await _gpu_ms(600)
		_layer.set("Enabled", false)
		var b := await _gpu_ms(600)
		_layer.set("Enabled", true)
		var a2 := await _gpu_ms(600)
		print("EMITTER_SHOT perf near noon interval=%.0f gpu_ms on=%.3f off=%.3f on=%.3f %s" % [interval, a1, b, a2, _layer.call("DescribeStatus")])
	settings.set("FieldShotIntervalSeconds", 300.0)
	print("EMITTER_SHOT=DONE")
	quit(0)
