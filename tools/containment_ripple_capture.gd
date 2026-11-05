extends SceneTree
## Containment field WO4 ripple captures (debug capture tool, windowed, GPU). Not a pass/fail gate in the manifest.
## Real player path (start menu -> warm world -> Continue -> hand-off, as tools/rim_wall_check.gd), then noclip poses, ripples from the real disturbance bus
## (debug spawns on the real ContainmentField node, or natural ones from a spawned megastorm / a parked storm), and every frame saved.
##
## Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (the world is saved on exit):
##   $env:APPDATA = "C:\Projects\Ringworld\.godot\test_runs\appdata_spawncheck_wall"
##   tools/use_godot_48.ps1 --fixed-fps 30 --script res://tools/containment_ripple_capture.gd ++ --mode=forced --out=.godot/captures/field/wo4/raw/forced
## With --fixed-fps 30 every process frame advances the weather clock by exactly 1/30 s, so saving every frame is a true 30 fps sequence (perf mode: run WITHOUT it).
##
## --mode=forced|megastorm|contact|above|empty|perf
##   forced     night, camera 2 km above the floor at across 8,000 m, a FieldStrike at 50 km and at 200 km (sequence forced{D}km_%08d, then forced{D}km_after / _off),
##              plus one plain debug lightning ripple at 50 km (ripple50km_%08d)
##   megastorm  spawn a megastorm through the drawer (--spawn-km=50, --speed=5), aim at the newest FieldStrike's surface, save every frame of its ripple life
##              (ms{km}_s{i}_%08d, up to --max-strikes=3), then ms{km}_after
##   contact    storm placed against the across-0 wall (StormAnvilCapture.PlaceStormAbsolute), camera on the wall top 20 km before it; StormContact ripples
##              (contact_s{i}_%08d, first two), contact_off; falls back to a megastorm contact band (mscontact_*) when none appears in 180 s
##   far        forced strikes at 0.85 / 0.95 / 1.05 x the camera far plane (far strike layer check)
##   above      noclip 400 m above the field where it is 20 km high, a FieldStrike 15 km ahead (above_%08d, abovenoon_ with --noon), above_after / above_off
##   empty      after a forced strike the weather clock is paused: empty_on / empty_strength0 / empty_fieldoff and an all-zero diagnostics check
##   perf       night, clock paused, exposure locked: forced ripples ON/OFF/ON gpu ms (lit and unlit) from 60 km below and from above the field
## --night (default) | --noon   pause the celestial clock at local midnight / noon
## --hide-hud=0                 keep the HUD (default hidden)     --settle=S  seconds to wait at each pose (default 10)
## --reduced                    Reduced flashes on (LightningVisualPolicy.Apply(1, 1, true))
## --spawn-km=N --speed=V --max-strikes=N --seconds=S   megastorm mode: spawn distance, wall speed, strikes to record, give-up time (game seconds)
## --out=DIR                    frame folder, project relative (default .godot/captures/field/wo4/raw/<mode>)
## Prints one CONTAINMENT_RIPPLE line per saved frame (mode, name, weather time, bus live / per-kind counts, far list, patches / post / near / far quads drawn,
## side above, ripple microseconds, draws) and CONTAINMENT_RIPPLE=DONE at the end.

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/ContainmentRippleCaptureProbe.cs")
const STORM := preload("res://tools/StormAnvilCapture.cs")

const FIELD_STRIKE := 8
const STORM_CONTACT := 3
const MEGASTORM_CONTACT := 4

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _out := ""
var _mode := "forced"
var _explorer: Node
var _grid: Node
var _probe: RefCounted
var _noon_cache := -1.0
var _a0 := 0.0
var _circumference := 0.0
var _top := 0.0
var _width := 0.0
var _reduced := false
var _exposure_owner: Node = null
var _exposure_was: Variant = null

func _initialize() -> void:
	create_timer(3500.0).timeout.connect(func(): print("CONTAINMENT_RIPPLE watchdog"); quit(3))
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
	if _arg("hide-hud", "1") == "0":
		return
	for n in _world.get_children():
		if n is CanvasLayer:
			n.visible = false
	for n in root.get_children():
		if n is CanvasLayer:
			n.visible = false

func _draws() -> int:
	return int(RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME))

## Waits for the end of this frame's draw, saves the viewport and prints the CONTAINMENT_RIPPLE line.
func _save(name: String, hide := false) -> void:
	if hide:
		_hide_hud()
	await RenderingServer.frame_post_draw
	var err := root.get_texture().get_image().save_png(ProjectSettings.globalize_path(_out + "/" + name + ".png"))
	if err != OK:
		print("CONTAINMENT_RIPPLE save failed ", name, " err=", err, " path=", ProjectSettings.globalize_path(_out + "/" + name + ".png"))
	print("CONTAINMENT_RIPPLE mode=%s name=%s %s draws=%d" % [_mode, name, _probe.call("DiagLine", _world), _draws()])

func _teleport(around: float, vertical: float, across: float, yaw: float, pitch: float) -> void:
	_explorer.call("TeleportToCanonicalMeters", fposmod(around, _circumference), vertical, across, yaw, pitch, true)
	_explorer.call("SetPresentationView", yaw, pitch)

func _cam() -> Dictionary:
	return _probe.call("CameraCanonical", _world)

## Turns the camera to a canonical point (yaw from the canonical deltas, pitch from the world geometry incl. the ring's curvature).
func _aim_canonical(around: float, vertical: float, across: float, pitch_extra := 0.0) -> Vector2:
	var a: Vector2 = _probe.call("AimAngles", _world, around, vertical, across)
	_explorer.call("SetPresentationView", a.x, a.y + pitch_extra)
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
	print("CONTAINMENT_RIPPLE time %s fraction=%.4f daylight=%.3f" % [tod, f, float(cycle.call("get_daylight_factor"))])

func _lock_exposure() -> void:
	for n in _world.find_children("*", "", true, false):
		var sc: Variant = n.get_script()
		if sc != null and (sc as Script).resource_path.ends_with("day_side_atmosphere.gd"):
			_exposure_owner = n
			break
	if _exposure_owner != null:
		_exposure_was = _exposure_owner.get("storm_auto_exposure_enabled")
		_exposure_owner.set("storm_auto_exposure_enabled", false)
	print("CONTAINMENT_RIPPLE exposure owner=", _exposure_owner, " locked=", _exposure_owner != null)

func _weather() -> Node:
	return _world.get_node_or_null("RingVolumetricClouds")

func _pause_weather(paused: bool) -> void:
	var w := _weather()
	if w != null:
		w.set("CloudSimulationPaused", paused)

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("appdata_spawncheck"):
		push_error("Refusing to run: user:// (%s) is not an appdata_spawncheck copy." % user_dir)
		quit(2)
		return
	_mode = _arg("mode", "forced")
	_out = _arg("out", ".godot/captures/field/wo4/raw/" + _mode)
	if not _out.begins_with("res://"):
		_out = "res://" + _out
	print("CONTAINMENT_RIPPLE out=", _out, " args=", OS.get_cmdline_user_args())
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out))
	_probe = PROBE.new()
	_reduced = _arg("reduced", "") != ""
	_menu = MENU.instantiate()
	_menu.live_world_settings_path = "user://menu_live_world_spawncheck.tres"
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if _warm == null:
		print("CONTAINMENT_RIPPLE no RingWarmWorld")
		quit(1)
		return
	_warm.handed_off.connect(func(w: Node) -> void: _handed_off = w)
	var warm_deadline := Time.get_ticks_msec() + 300000
	while int(_warm.state) != 2 and Time.get_ticks_msec() < warm_deadline:
		await process_frame
	if int(_warm.state) != 2:
		print("CONTAINMENT_RIPPLE warm world never ready")
		quit(1)
		return
	_world = _warm.world
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
	_menu.continue_button.pressed.emit()
	if not await _wait_until(func() -> bool: return _handed_off != null, 240000):
		print("CONTAINMENT_RIPPLE hand-off never completed")
		quit(1)
		return
	_explorer = _world.get_node("Explorer")
	_grid = _world.get_node("ChunkGridManager")
	_circumference = float(_grid.call("GetCanonicalRingCircumferenceMeters64"))
	_width = float(_grid.get("RingAcrossWidthMeters"))
	_top = float(_grid.call("GetStructuralWallTopGlobalY"))
	var pose: Dictionary = _explorer.call("GetCanonicalPoseMeters")
	_a0 = float(pose["around_m"])
	print("CONTAINMENT_RIPPLE handed off mode=%s circumference=%.1f width=%.1f wall_top=%.1f around=%.1f" % [_mode, _circumference, _width, _top, _a0])
	if not await _wait_until(func() -> bool: return bool(_probe.call("Ready", _world)), 60000):
		print("CONTAINMENT_RIPPLE field or bus not ready: ", _probe.call("ProfileInfo", _world))
		quit(1)
		return
	print("CONTAINMENT_RIPPLE profile ", _probe.call("ProfileInfo", _world), " ", _probe.call("DiagLine", _world))
	if _reduced:
		_probe.call("SetStrikeScale", 1.0, true)
	_hide_hud()
	_teleport(_a0, _top + 1.7, -64.0, 0.0, -8.0)
	await create_timer(2.0).timeout
	await _set_time_of_day("noon" if _arg("noon", "") != "" else "night")
	_lock_exposure()
	match _mode:
		"forced":
			await _mode_forced()
		"megastorm":
			await _mode_megastorm()
		"contact":
			await _mode_contact()
		"stop":
			await _mode_stop()
		"far":
			await _mode_far()
		"above":
			await _mode_above()
		"empty":
			await _mode_empty()
		"perf":
			await _mode_perf()
		_:
			print("CONTAINMENT_RIPPLE unknown mode ", _mode)
	if _exposure_owner != null:
		_exposure_owner.set("storm_auto_exposure_enabled", _exposure_was)
	print("CONTAINMENT_RIPPLE=DONE mode=%s out=%s" % [_mode, _out])
	quit(0)

# ---- Poses --------------------------------------------------------------------------------------------------------------------------------

func _floor_height(around: float, across: float) -> float:
	var terrain: Node = _world.get_node_or_null("Terrain3DGameplay")
	var h := 0.0
	if terrain != null:
		h = float(terrain.call("SampleHeight", around, across))
	if not is_finite(h):
		h = 0.0
	return maxf(h, 0.0)

## Camera 2 km above the floor at across 8,000 m (noclip), looking along +around.
func _floor_pose() -> void:
	_teleport(_a0, 2500.0, 8000.0, 0.0, 0.0)
	await create_timer(float(_arg("settle", "10"))).timeout
	var floor_h := _floor_height(_a0, 8000.0)
	_teleport(_a0, floor_h + 2000.0, 8000.0, 0.0, 0.0)
	await create_timer(float(_arg("settle", "10"))).timeout
	print("CONTAINMENT_RIPPLE pose floor_h=%.1f camera=%s" % [floor_h, _cam()])

func _above_pose(pitch: float, alt := 400.0) -> float:
	var x20: float = _probe.call("FieldAcrossAt", _world, 20000.0)
	var h: float = _probe.call("FieldHeight", _world, x20)
	_teleport(_a0, h + alt, x20, 0.0, pitch)
	await create_timer(float(_arg("settle", "10"))).timeout
	print("CONTAINMENT_RIPPLE pose above across=%.1f field_h=%.1f camera=%s" % [x20, h, _cam()])
	return x20

# ---- Sequences ----------------------------------------------------------------------------------------------------------------------------

## Saves every frame: `pre` frames before `spawn` is called, then until `frames` frames after it. Names are prefix + %08d.
func _sequence(prefix: String, pre: int, post: int, spawn: Callable) -> void:
	var index := 0
	for i in pre:
		await _save("%s%08d" % [prefix, index])
		index += 1
	var result: Variant = spawn.call()
	print("CONTAINMENT_RIPPLE spawn ", prefix, " result=", result, " camera=", _cam())
	for i in post:
		await _save("%s%08d" % [prefix, index])
		index += 1
		if i % 30 == 29:
			_hide_hud()

## Runs frames without saving until the bus has no live ripples and no far strikes (or `max_s` of game time).
func _drain(max_s: float) -> bool:
	var frames := int(max_s * 30.0)
	for i in frames:
		if int(_probe.call("Live", _world)) == 0 and int(_probe.call("FarCount", _world)) == 0:
			return true
		await process_frame
	return false

func _after_off(prefix: String) -> void:
	await _frames(10)
	await _save(prefix + "_after")
	_probe.call("SetRippleStrength", _world, 0.0)
	await _frames(10)
	await _save(prefix + "_off")
	_probe.call("SetRippleStrength", _world, 1.0)
	await _frames(5)

func _mode_forced() -> void:
	await _floor_pose()
	var suffix := _arg("suffix", "")
	var kms: Array = [int(_arg("only-km", "0"))] if _arg("only-km", "0") != "0" else [50, 200]
	for km in kms:
		var d := float(km) * 1000.0
		var cam := _cam()
		var around := float(cam["around"]) + d
		var h: float = _probe.call("FieldHeight", _world, 8000.0)
		var a := _aim_canonical(around, h, 8000.0)
		print("CONTAINMENT_RIPPLE aim D=%dkm yaw=%.3f pitch=%.3f field_h=%.1f" % [km, a.x, a.y, h])
		await _frames(30)
		var world_pos: Vector3 = _probe.call("ToWorld", _world, around, h, 8000.0)
		var camera := root.get_viewport().get_camera_3d()
		print("CONTAINMENT_RIPPLE contact on screen at ", camera.unproject_position(world_pos), " behind=", camera.is_position_behind(world_pos))
		await _sequence("forced%dkm%s_" % [km, suffix], 6, 120, func() -> Variant: return _probe.call("SpawnFieldStrike", _world, around, 8000.0, 1.0))
		var drained := await _drain(25.0)
		print("CONTAINMENT_RIPPLE drained=", drained, " ", _probe.call("DiagLine", _world))
		await _after_off("forced%dkm%s" % [km, suffix])
	if _arg("skip-ripple", "") != "":
		return
	# One plain lit debug ripple (Lightning kind, Rmax 8 km, flash 0.3 s) at 50 km.
	var cam2 := _cam()
	var around2 := float(cam2["around"]) + 50000.0
	var h2: float = _probe.call("FieldHeight", _world, 8000.0)
	_aim_canonical(around2, h2, 8000.0)
	await _frames(30)
	await _sequence("ripple50km_", 6, 150, func() -> Variant: return _probe.call("SpawnLightningRipple", _world, around2, 8000.0))
	await _drain(25.0)

## Far list check: forced strikes 400 km and 700 km ahead (beyond the 350 km far-list distance); frames far{km}_%08d.
func _mode_far() -> void:
	await _floor_pose()
	for km in [400, 700]:
		var d := float(km) * 1000.0
		var around := float(_cam()["around"]) + d
		var h: float = _probe.call("FieldHeight", _world, 8000.0)
		var a := _aim_canonical(around, h, 8000.0)
		print("CONTAINMENT_RIPPLE aim far%d D=%.0f yaw=%.3f pitch=%.3f" % [km, d, a.x, a.y])
		await _frames(30)
		await _sequence("far%d_" % km, 6, 120, func() -> Variant: return _probe.call("SpawnFieldStrike", _world, around, 8000.0, 1.0))
		await _drain(25.0)

## (old) Far layer check: forced strikes at 0.85 / 0.95 / 1.05 x the camera far plane (the far fade band is 0.9-0.98 x far); frames far{pct}_%08d, 4 s each.
func _mode_stop() -> void:
	var drawer: Node = _world.get_node("LiveTuningDrawer")
	var node: Node = _world.get_node_or_null("RingMegastorm")
	var removed: Variant = node.call("RemoveMegastorm") if node != null else "no node"
	drawer.call("StopMegastormFromMenu")
	await create_timer(3.0).timeout
	var again: Variant = node.call("RemoveMegastorm") if node != null else "no node"
	print("CONTAINMENT_RIPPLE megastorm stop removed_first=%s removed_second(false = none active)=%s %s" % [str(removed), str(again), _probe.call("DiagLine", _world)])

func _mode_far_old() -> void:
	await _floor_pose()
	var far_m: float = _probe.call("CameraFar", _world)
	print("CONTAINMENT_RIPPLE far camera far=%.0f" % far_m)
	for pct in [85, 95, 105]:
		var d := far_m * float(pct) / 100.0
		var around := float(_cam()["around"]) + d
		var h: float = _probe.call("FieldHeight", _world, 8000.0)
		var a := _aim_canonical(around, h, 8000.0)
		print("CONTAINMENT_RIPPLE aim far%d D=%.0f yaw=%.3f pitch=%.3f" % [pct, d, a.x, a.y])
		await _frames(30)
		await _sequence("far%d_" % pct, 6, 120, func() -> Variant: return _probe.call("SpawnFieldStrike", _world, around, 8000.0, 1.0))
		await _drain(25.0)

func _mode_above() -> void:
	var prefix := _arg("prefix", "abovenoon" if _arg("noon", "") != "" else "above")
	var x20 := await _above_pose(float(_arg("pitch", "-35")), float(_arg("alt", "400")))
	_pause_weather(false)
	var around := float(_cam()["around"]) + float(_arg("ahead", "15000"))
	var h: float = _probe.call("FieldHeight", _world, x20)
	await _frames(30)
	await _sequence(prefix + "_", 6, 150, func() -> Variant: return _probe.call("SpawnFieldStrike", _world, around, x20, 1.0))
	await _drain(25.0)
	await _after_off(prefix)

func _mode_empty() -> void:
	await _floor_pose()
	var around := float(_cam()["around"]) + 50000.0
	var h: float = _probe.call("FieldHeight", _world, 8000.0)
	_aim_canonical(around, h, 8000.0)
	await _frames(30)
	_probe.call("SpawnFieldStrike", _world, around, 8000.0, 1.0)
	for i in 120:
		await process_frame
	var drained := await _drain(25.0)
	_pause_weather(true)
	await _frames(15)
	print("CONTAINMENT_RIPPLE empty drained=", drained)
	await _save("empty_on", true)
	_probe.call("SetRippleStrength", _world, 0.0)
	await _frames(10)
	await _save("empty_strength0")
	_probe.call("SetRippleStrength", _world, 1.0)
	_probe.call("SetFieldEnabled", _world, false)
	await _frames(10)
	await _save("empty_fieldoff")
	_probe.call("SetFieldEnabled", _world, true)
	await _frames(10)
	var line: String = _probe.call("DiagLine", _world)
	var ok := int(_probe.call("Live", _world)) == 0 and int(_probe.call("FarCount", _world)) == 0 and line.contains("patches=0 post=0 near=0 farquads=0 above=False") and line.contains(" us=0.0 ")
	print("CONTAINMENT_RIPPLE EMPTY_CHECK=%s %s" % ["PASS" if ok else "FAIL", line])
	_pause_weather(false)

# ---- Natural ripples ----------------------------------------------------------------------------------------------------------------------

## Waits (up to timeout_s of game time) for new live ripples of a kind (FIELD_STRIKE also takes the far list), and for each (up to max_n) saves every frame of
## its life as prefix + index + _%08d. Returns the number recorded.
func _record_ripples(kind: int, prefix: String, max_n: int, aim: bool, timeout_s: float, max_life_s := 25.0, close := false) -> int:
	var seen: Array[int] = []
	var recorded := 0
	var waited := 0
	var limit := int(timeout_s * 30.0)
	while recorded < max_n and waited < limit:
		var nw: Dictionary = _probe.call("Newest", _world, kind, seen)
		if not bool(nw["found"]):
			waited += 1
			if waited % 300 == 0:
				print("CONTAINMENT_RIPPLE waiting kind=%d waited=%ds %s" % [kind, waited / 30, _probe.call("DiagLine", _world)])
			await process_frame
			continue
		var id := int(nw["id"])
		var far := bool(nw["far"])
		seen.append(id)
		if close:
			# 2 km below the ripple's surface point, 3 km before it along the ring, looking up and forward at it.
			_teleport(float(nw["around"]) - 3000.0, float(nw["vertical"]) - 2000.0, float(nw["across"]), 0.0, 20.0)
			await _frames(3)
			aim = true
		if aim:
			var a := _aim_canonical(float(nw["around"]), float(nw["vertical"]), float(nw["across"]))
			print("CONTAINMENT_RIPPLE aim yaw=%.2f pitch=%.2f" % [a.x, a.y])
		print("CONTAINMENT_RIPPLE ripple %s%d id=%d kind=%d far=%s distance_m=%.0f age=%.2f around=%.0f across=%.0f vertical=%.0f camera=%s" % [prefix, recorded, id, kind, str(far),
			float(nw["distance"]), float(nw["age"]), float(nw["around"]), float(nw["across"]), float(nw["vertical"]), _cam()])
		await process_frame
		var i := 0
		while bool(_probe.call("Alive", _world, id, far)) and i < int(max_life_s * 30.0):
			await _save("%s%d_%08d" % [prefix, recorded, i])
			i += 1
			if i % 30 == 0:
				_hide_hud()
		print("CONTAINMENT_RIPPLE ripple %s%d frames=%d freed=%s" % [prefix, recorded, i, str(not bool(_probe.call("Alive", _world, id, far)))])
		recorded += 1
	return recorded

func _spawn_megastorm(km: float) -> void:
	var drawer: Node = _world.get_node("LiveTuningDrawer")
	drawer.call("SetBenchmarkDrawerOpen", true)
	await process_frame
	drawer.call("SetMegastormTuningValue", "SpeedMetersPerSecond", float(_arg("speed", "5")))
	var distance := drawer.find_child("MegastormSpawnDistanceKm", true, false) as HSlider
	var button := drawer.find_child("MegastormSpawn", true, false) as Button
	if distance != null:
		distance.max_value = maxf(distance.max_value, km)
		distance.value = km
	if button != null:
		button.pressed.emit()
	await process_frame
	drawer.call("SetBenchmarkDrawerOpen", false)
	await process_frame
	_hide_hud()
	print("CONTAINMENT_RIPPLE megastorm spawned km=%.0f button=%s" % [km, str(button != null)])

func _mode_megastorm() -> void:
	var km := float(_arg("spawn-km", "50"))
	var tag := "v" if _arg("vantage", "") != "" else ""
	if tag == "v":
		# Above every cloud (tops <= 18 km), below the field (40 km at the centre): vertical 19,000 m at across W/2.
		_teleport(_a0, 19000.0, _width * 0.5, 0.0, 0.0)
		await create_timer(float(_arg("settle", "10"))).timeout
		print("CONTAINMENT_RIPPLE pose vantage camera=%s" % _cam())
	else:
		await _floor_pose()
	_probe.call("SetStrikeScale", 2.0, _reduced)
	await _spawn_megastorm(km)
	await _frames(60)
	var n := await _record_ripples(FIELD_STRIKE, "ms%d%s_s" % [int(km), tag], int(_arg("max-strikes", "3")), true, float(_arg("seconds", "400")))
	print("CONTAINMENT_RIPPLE megastorm recorded strikes=", n)
	var drained := await _wait_no_field_strike(120.0)
	print("CONTAINMENT_RIPPLE megastorm field strikes gone=", drained)
	await _save("ms%d%s_after" % [int(km), tag], true)

func _wait_no_field_strike(max_s: float) -> bool:
	for i in int(max_s * 30.0):
		if int(_probe.call("LiveOfKind", _world, FIELD_STRIKE)) == 0 and int(_probe.call("FarCount", _world)) == 0:
			return true
		await process_frame
	return false

func _mode_contact() -> void:
	_teleport(_a0, _top + 50.0, 200.0, 0.0, 10.0)
	await create_timer(float(_arg("settle", "10"))).timeout
	var cam := _cam()
	var storm_around := float(cam["around"]) + 20000.0
	var helper: RefCounted = STORM.new()
	if not helper.call("PlaceStormAbsolute", _world, storm_around, 1500.0, 0.0, 12000.0):
		print("CONTAINMENT_RIPPLE storm placement failed: ", helper.get("ErrorText"))
		return
	print("CONTAINMENT_RIPPLE storm placed around=%.0f across=1500 radius=12000 camera=%s" % [storm_around, cam])
	var close := _arg("close", "") != ""
	var cprefix := _arg("prefix", "contact_s")
	var n := await _record_ripples(STORM_CONTACT, cprefix, int(_arg("max-n", "2")), false, 180.0, 25.0, close)
	if n > 0:
		await _frames(10)
		_probe.call("SetRippleStrength", _world, 0.0)
		await _frames(10)
		await _save(cprefix.trim_suffix("_s") + "_off")
		_probe.call("SetRippleStrength", _world, 1.0)
		return
	print("CONTAINMENT_RIPPLE no StormContact ripple in 180 s: storm top 18000 m (as placed) vs field height at across 1500 m = %.0f m; %s" % [_probe.call("FieldHeight", _world, 1500.0), _probe.call("DiagLine", _world)])
	print("CONTAINMENT_RIPPLE FALLBACK megastorm contact band")
	_probe.call("SetStrikeScale", 2.0, _reduced)
	await _spawn_megastorm(25.0)
	var m := await _record_ripples(MEGASTORM_CONTACT, "mscontact_s", 2, false, 300.0)
	print("CONTAINMENT_RIPPLE mscontact recorded=", m)
	_probe.call("SetRippleStrength", _world, 0.0)
	await _frames(10)
	await _save("mscontact_off")
	_probe.call("SetRippleStrength", _world, 1.0)

# ---- Perf ---------------------------------------------------------------------------------------------------------------------------------

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

func _perf_row(label: String, around: float, across: float, lit: bool, shot_on: String, shot_off: String) -> void:
	_probe.call("ForceRipples", _world, around, across, 16, 6000.0, lit)
	await _frames(20)
	var on1 := await _gpu_ms(240)
	var d_on := _draws()
	var us_on: float = _probe.call("LastRippleMicroseconds", _world)
	var diag_on: String = _probe.call("DiagLine", _world)
	if shot_on != "":
		await _save(shot_on, true)
	_probe.call("ClearForced", _world)
	await _frames(20)
	var off := await _gpu_ms(240)
	var d_off := _draws()
	var us_off: float = _probe.call("LastRippleMicroseconds", _world)
	if shot_off != "":
		await _save(shot_off, true)
	_probe.call("ForceRipples", _world, around, across, 16, 6000.0, lit)
	await _frames(20)
	var on2 := await _gpu_ms(240)
	var d_on2 := _draws()
	_probe.call("ClearForced", _world)
	print("CONTAINMENT_RIPPLE perf %s gpu_ms on=%.3f off=%.3f on=%.3f delta=%.3f draws on=%d off=%d on=%d ripple_us on=%.1f off=%.1f | %s" % [label, on1, off, on2,
		(on1 + on2) * 0.5 - off, d_on, d_off, d_on2, us_on, us_off, diag_on])

func _mode_perf() -> void:
	_pause_weather(true)
	await _floor_pose()
	var around := float(_cam()["around"]) + 60000.0
	_aim_canonical(around, _probe.call("FieldHeight", _world, 8000.0), 8000.0)
	await create_timer(3.0).timeout
	await _perf_row("below60km_lit", around, 8000.0, true, "perf_lit_on", "perf_off")
	await _perf_row("below60km_unlit", around, 8000.0, false, "perf_unlit_on", "")
	var x20 := await _above_pose(-35.0)
	_pause_weather(true)
	var around_a := float(_cam()["around"]) + 15000.0
	await create_timer(3.0).timeout
	await _perf_row("above15km_lit", around_a, x20, true, "perf_above_on", "perf_above_off")
	await _perf_row("above15km_unlit", around_a, x20, false, "", "")
	_pause_weather(false)
