extends SceneTree
## Containment field WO6 real-path check (debug capture tool, windowed, GPU; manual, not a gate): clouds flatten under the field near a rim wall.
## start menu -> warm world -> Continue (saved world restore, the player stands on the wall top) -> hand-off, then a mature anvil storm is parked
## against the across-0 wall and the camera looks at it from the habitat, with the cloud ceiling under the field ON and OFF (StormAnvilCapture
## SetFieldCeiling: the zero profile is the pre-WO6 behaviour), plus ONE quick A-B-A of the cloud GPU scopes at that pose.
##
## Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (the world is saved on exit):
##   $env:APPDATA = "...\.godot\test_runs\appdata_spawncheck_wall"
##   tools/use_godot_48.ps1 --script res://tools/rim_cloud_ceiling_check.gd ++ --out=res://.godot/captures/field/wo6/run [--no-perf] [--settle=S]
## Prints RIM_CLOUD lines (shots, perf windows) and RIM_CLOUD=DONE.

const MENU := preload("res://scenes/StartMenu.tscn")

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _out := "res://.godot/captures/field/wo6/run"
var _explorer: Node
var _grid: Node
var _helper: RefCounted
var _noon_cache := -1.0
var _width := 318000.0
var _far_side := false

func _initialize() -> void:
	create_timer(1500.0).timeout.connect(func(): print("RIM_CLOUD watchdog"); quit(3))
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
	print("RIM_CLOUD shot ", name)

# Poses are written for the across-0 wall; on the across-W wall (the saved player's side) across and yaw mirror.
func _teleport(around: float, vertical: float, across: float, yaw: float, pitch: float) -> void:
	if _far_side:
		across = _width - across
		yaw = -yaw
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

func _set_noon() -> void:
	var cycle: Node = _world.get_node_or_null("RingDayNightCycle")
	if cycle == null:
		return
	var f := await _find_local_noon(cycle)
	cycle.call("set_cycle_fraction", f)
	cycle.set("Paused", true)
	await create_timer(2.0).timeout
	print("RIM_CLOUD noon fraction=%.4f daylight=%.3f" % [f, float(cycle.call("get_daylight_factor"))])

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
		print("RIM_CLOUD no RingWarmWorld")
		quit(1)
		return
	_warm.handed_off.connect(func(w: Node) -> void: _handed_off = w)
	if not await _wait_until(func() -> bool: return int(_warm.state) == 2, 300000):
		print("RIM_CLOUD warm world never ready")
		quit(1)
		return
	_world = _warm.world
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
	_menu.continue_button.pressed.emit()
	if not await _wait_until(func() -> bool: return _handed_off != null, 240000):
		print("RIM_CLOUD hand-off never completed")
		quit(1)
		return
	_explorer = _world.get_node("Explorer")
	_grid = _world.get_node("ChunkGridManager")
	var pose: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	var around := float(pose["around_m"])
	var top := float(_grid.call("GetStructuralWallTopGlobalY"))
	_width = float(_grid.get("RingAcrossWidthMeters"))
	_far_side = float(pose["across_m"]) > _width * 0.5
	print("RIM_CLOUD handed off around=%.1f across=%.1f wall_top=%.1f far_side=%s" % [around, float(pose["across_m"]), top, str(_far_side)])
	var settle := float(_arg("settle", "12"))
	_helper = load("res://tools/StormAnvilCapture.cs").new()
	var clouds: Node = _world.get_node("RingVolumetricClouds")
	clouds.set("CloudSimulationPaused", true)
	_helper.RainOff(_world)
	_helper.SetLifecycle(1.6)
	await _set_noon()
	# A mature anvil storm against the across-0 wall (centre 1.5 km in, radius 12 km, 20 km ahead along the wall).
	if not _helper.PlaceStormAbsolute(_world, around + 20000.0, _width - 1500.0 if _far_side else 1500.0, 0.0, 12000.0):
		print("RIM_CLOUD storm placement failed: ", _helper.ErrorText)
		quit(1)
		return
	# Habitat, 22 km in from the wall, looking at the storm's wall side (it is ~28 km away at yaw -46).
	_teleport(around, 600.0, 22000.0, -46.0, 22.0)
	await create_timer(settle + 8.0).timeout
	for state in ["on", "off", "on2"]:
		if not _helper.SetFieldCeiling(_world, state != "off") and state != "off":
			print("RIM_CLOUD field profile not published (field off in this world)")
		await create_timer(settle).timeout
		await _shot("storm_" + state)
	# Profile view: 15 km in from the wall, abreast of the storm, looking straight at the wall and up at the storm tops above the wall edge:
	# the lid under the field slopes from about 13 km at the wall to 17.7 km 4 km in.
	_teleport(around + 20000.0, 600.0, 15000.0, -90.0, 36.0)
	await create_timer(settle + 6.0).timeout
	for state in ["on", "off"]:
		_helper.SetFieldCeiling(_world, state == "on")
		await create_timer(settle).timeout
		await _shot("profile_" + state)
	if _arg("no-perf", "") == "":
		_teleport(around, 600.0, 22000.0, -46.0, 22.0)
		_helper.SetFieldCeiling(_world, true)
		await create_timer(settle + 8.0).timeout
		for state in ["on", "off", "on2"]:
			_helper.SetFieldCeiling(_world, state != "off")
			await create_timer(4.0).timeout
			_helper.PerfBegin(_world)
			await create_timer(10.0).timeout
			print("RIM_CLOUD perf ", state, " ", _helper.PerfEnd(_world))
	_helper.SetFieldCeiling(_world, true)
	print("RIM_CLOUD=DONE out=", _out)
	quit(0)
