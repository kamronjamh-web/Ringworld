extends SceneTree
## Containment field WO9 (atmosphere lid) real player path capture tool (debug, windowed, GPU). Not a pass/fail gate.
## start menu -> warm world -> Continue (saved world restore) -> hand-off, then noclip teleports. Each view is shot with the lid (the haze stops at the
## field) and with the pre-WO9 box (AtmosphereLidCaptureProbe.SetLid(false): 20 km cap, inner-face side planes, old camera interior factor; Sunshine
## keeps its own copy of the field in both). Weather clock paused, auto exposure locked.
##
## Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (the world is saved on exit):
##   $env:APPDATA = "...\.godot\test_runs\appdata_spawncheck_wall"
##   tools/use_godot_48.ps1 --script res://tools/atmosphere_lid_capture.gd ++ --out=res://.godot/captures/field/wo9/shots
## --out=DIR       shot folder.
## --times=a:b     times of day (noon, low (about 11 degrees), rise (about 2 degrees), night); default noon:low:rise:night. Views other than the
##                 core set (top_up, faces, floor, vacuum) are taken at noon only.
## --settle=S      seconds at each view (default 6).
## --no-climb      skip the two noclip climbs (frame sequences <out>/climb20 and <out>/climbapex, lid only).
## --no-perf       skip the GPU A-B-A.
## Prints ATMOS_LID lines and ATMOS_LID=DONE.

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/AtmosphereLidCaptureProbe.cs")

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _out := "res://.godot/captures/field/wo9/shots"
var _explorer: Node
var _grid: Node
var _probe: RefCounted
var _noon_cache := -1.0

func _initialize() -> void:
	create_timer(2400.0).timeout.connect(func(): print("ATMOS_LID watchdog"); quit(3))
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

func _shot(name: String) -> void:
	_hide_hud()
	for i in 3:
		await process_frame
	await RenderingServer.frame_post_draw
	root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/" + name + ".png"))
	print("ATMOS_LID shot ", name, " lid=", _probe.call("HasLid"), " pose=", _pose_text())

func _pose_text() -> String:
	var p: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	return "around=%.1f vertical=%.2f across=%.2f yaw=%.1f pitch=%.1f" % [float(p["around_m"]), float(p["vertical_m"]), float(p["across_m"]),
		float(p["yaw_deg"]), float(p["pitch_deg"])]

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
	var offsets := {"noon": 0.0, "low": 0.22, "rise": -0.245, "night": 0.5}
	var f := fposmod(noon + float(offsets.get(tod, 0.0)), 1.0)
	cycle.call("set_cycle_fraction", f)
	cycle.set("Paused", true)
	await create_timer(2.0).timeout
	print("ATMOS_LID time %s fraction=%.4f daylight=%.3f" % [tod, f, float(cycle.call("get_daylight_factor"))])

func _set_lid(on: bool, settle: float) -> void:
	_probe.call("SetLid", on)
	await create_timer(settle).timeout

# Shot pair: lid (after) then box (before), lid restored.
func _pair(name: String, settle: float) -> void:
	await create_timer(settle).timeout
	await _shot(name + "_lid")
	await _set_lid(false, settle)
	await _shot(name + "_box")
	await _set_lid(true, 1.0)

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

func _aba(label: String) -> void:
	var a1 := await _gpu_ms(240)
	await _set_lid(false, 2.0)
	var b := await _gpu_ms(240)
	await _set_lid(true, 2.0)
	var a2 := await _gpu_ms(240)
	print("ATMOS_LID perf %s gpu_ms lid=%.3f box=%.3f lid=%.3f" % [label, a1, b, a2])

func _climb(dir_name: String, around: float, across: float, from_v: float, to_v: float, yaw: float, pitch: float, frames: int, settle: float) -> void:
	var dir := _out + "/" + dir_name
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	_teleport(around, from_v, across, yaw, pitch)
	await create_timer(settle).timeout
	_hide_hud()
	for i in frames:
		var v := lerpf(from_v, to_v, float(i) / float(frames - 1))
		_teleport(around, v, across, yaw, pitch)
		await RenderingServer.frame_post_draw
		root.get_texture().get_image().save_png(ProjectSettings.globalize_path("%s/frame%08d.png" % [dir, i]))
	print("ATMOS_LID climb %s frames=%d vertical %.0f -> %.0f across=%.1f" % [dir_name, frames, from_v, to_v, across])

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("appdata_spawncheck"):
		push_error("Refusing to run: user:// (%s) is not an appdata_spawncheck copy." % user_dir)
		quit(2)
		return
	_out = _arg("out", _out)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out))
	_probe = PROBE.new()
	_menu = MENU.instantiate()
	_menu.live_world_settings_path = "user://menu_live_world_spawncheck.tres"
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if _warm == null:
		print("ATMOS_LID no RingWarmWorld")
		quit(1)
		return
	_warm.handed_off.connect(func(w: Node) -> void: _handed_off = w)
	var warm_deadline := Time.get_ticks_msec() + 300000
	while int(_warm.state) != 2 and Time.get_ticks_msec() < warm_deadline:
		await process_frame
	if int(_warm.state) != 2:
		print("ATMOS_LID warm world never ready")
		quit(1)
		return
	_world = _warm.world
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
	_menu.continue_button.pressed.emit()
	if not await _wait_until(func() -> bool: return _handed_off != null, 240000):
		print("ATMOS_LID hand-off never completed")
		quit(1)
		return
	_explorer = _world.get_node("Explorer")
	_grid = _world.get_node("ChunkGridManager")
	var circumference := float(_grid.call("GetCanonicalRingCircumferenceMeters64"))
	var width := float(_grid.get("RingAcrossWidthMeters"))
	var top := float(_grid.call("GetStructuralWallTopGlobalY"))
	var line := fposmod(round(float((_explorer.call("GetCanonicalPoseMeters") as Dictionary)["around_m"]) / (circumference / 126.0)) * (circumference / 126.0), circumference)
	var settle := float(_arg("settle", "6"))
	_teleport(line, top + 1.7, -30.0, 90.0, 60.0)
	await create_timer(settle).timeout
	if not await _wait_until(func() -> bool: return bool(_probe.call("HasLid")), 30000):
		print("ATMOS_LID no field profile published (bridge missing?)")
		quit(1)
		return
	var prof: PackedFloat32Array = _probe.call("Profile")
	print("ATMOS_LID handed off width=%.1f wall_top=%.1f profile=%s line=%.1f" % [width, top, prof, line])
	var weather: Node = _world.get_node_or_null("RingVolumetricClouds")
	if weather != null:
		weather.set("CloudSimulationPaused", true)
	var atmosphere: Node = null
	for n in _world.find_children("*", "", true, false):
		var sc: Variant = n.get_script()
		if sc != null and (sc as Script).resource_path.ends_with("day_side_atmosphere.gd"):
			atmosphere = n
			break
	if atmosphere != null:
		atmosphere.set("storm_auto_exposure_enabled", false)
	var terrain: Node = _world.get_node_or_null("Terrain3DGameplay")
	# Across where the field is at 20 km near the across-0 wall (ellipse between the roots, crown = prof[2]).
	var r0 := float(prof[3])
	var rw := float(prof[4])
	var crown := float(prof[2])
	var apex := float(prof[1])
	var y20 := clampf((20000.0 - crown) / (apex - crown), 0.0, 1.0)
	var x20 := (r0 + rw) * 0.5 - (rw - r0) * 0.5 * sqrt(1.0 - y20 * y20)
	var mid := width * 0.5
	for tod in _arg("times", "noon:low:rise:night").split(":"):
		await _set_time_of_day(tod)
		var floor_h := 0.0
		var mid_h := 0.0
		if terrain != null:
			floor_h = float(terrain.call("SampleHeight", line, 1500.0))
			mid_h = float(terrain.call("SampleHeight", line, mid))
		floor_h = maxf(floor_h if is_finite(floor_h) else 0.0, 0.0)
		mid_h = maxf(mid_h if is_finite(mid_h) else 0.0, 0.0)
		var all := tod == "noon"
		# Wall top, on the cap's habitat half (across -30, inside the field), looking up toward the habitat, and straight up.
		_teleport(line, top + 1.7, -30.0, 90.0, 55.0)
		await _pair("%s_top_up" % tod, settle)
		if all:
			_teleport(line, top + 1.7, -30.0, 0.0, 85.0)
			await _pair("%s_top_zenith" % tod, settle)
			# The WO3 pose (across 200, 50 m up) whose sky was black space.
			_teleport(line - 1500.0, top + 50.0, 200.0, -35.0, 30.0)
			await _pair("%s_top_up200" % tod, settle)
		# Wall faces and cap from above the habitat edge (WO12 top_above pose).
		_teleport(line - 600.0, top + 400.0, 300.0, -150.0, -45.0)
		await _pair("%s_faces" % tod, settle)
		if all:
			# The inner face from the habitat, 300 m above the wall top, 1.5 km out.
			_teleport(line, top + 300.0, 1500.0, -90.0, -20.0)
			await _pair("%s_face" % tod, settle)
		# Habitat floor 1.5 km from the wall looking at the wall top and the sky above it.
		_teleport(line, floor_h + 40.0, 1500.0, -90.0, 35.0)
		await _pair("%s_floor" % tod, settle)
		if all:
			# Mid-habitat: toward the horizon and up the arch.
			_teleport(line, mid_h + 40.0, mid, 0.0, 10.0)
			await _pair("%s_mid" % tod, settle)
			_teleport(line, mid_h + 40.0, mid, 0.0, 60.0)
			await _pair("%s_mid_up" % tod, settle)
		# Vacuum side of the wall top (across -400, 300 m up) looking back into the habitat, and away into space.
		_teleport(line - 1500.0, top + 300.0, -400.0, 70.0, -12.0)
		await _pair("%s_vacuum" % tod, settle)
		if all:
			_teleport(line - 1500.0, top + 300.0, -400.0, -90.0, 15.0)
			await _pair("%s_vacuum_out" % tod, settle)
			# Flight: 400 m under / over the field where it is at 20 km, and above the apex.
			_teleport(line, 19600.0, x20, 60.0, 10.0)
			await _pair("%s_under20" % tod, settle)
			_teleport(line, 20400.0, x20, 60.0, 10.0)
			await _pair("%s_over20" % tod, settle)
			_teleport(line, apex + 1000.0, mid, 30.0, 5.0)
			await _pair("%s_over_apex" % tod, settle)
			_teleport(line, apex - 1000.0, mid, 30.0, 5.0)
			await _pair("%s_under_apex" % tod, settle)
		if tod == "noon" and _arg("no-perf", "") == "":
			_teleport(line, top + 1.7, -30.0, 90.0, 55.0)
			await create_timer(settle).timeout
			await _aba("wall_top_up")
			_teleport(line, mid_h + 40.0, mid, 0.0, 10.0)
			await create_timer(settle).timeout
			await _aba("mid_floor")
		if tod == "noon" and _arg("no-climb", "") == "":
			# Climb through the field where it is at 20 km (19.4 -> 20.6 km), looking along the wall slightly down, then through the apex.
			await _climb("climb20", line, x20, 19400.0, 20600.0, 60.0, -5.0, 120, settle)
			await _climb("climbapex", line, mid, 39400.0, 40600.0, 30.0, -5.0, 120, settle)
	if weather != null:
		weather.set("CloudSimulationPaused", false)
	print("ATMOS_LID=DONE out=", _out)
	quit(0)
