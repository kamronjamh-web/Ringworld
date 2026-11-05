extends SceneTree
## Rim wall (containment field WO12) real player path check (debug capture tool, windowed, GPU). Not a pass/fail gate in the manifest.
## start menu -> warm world -> Continue (saved world restore) -> hand-off, then noclip teleports to views of the rim wall at a structural
## cue line (the hull shader's around-based blue line), at noon and at night, and a collision walk on the wall top through the
## explorer's own input path (injected V / W / Space key events).
##
## Run ONLY on an isolated APPDATA copy whose path contains "appdata_spawncheck" (the world is saved on exit):
##   $env:APPDATA = "...\.godot\test_runs\appdata_spawncheck_wall"
##   tools/use_godot_48.ps1 --script res://tools/rim_wall_check.gd ++ --out=res://.godot/captures/field/wo12/after [--walk] [--no-views]
##
## --out=DIR     shot folder (default res://.godot/captures/field/wo12/raw).
## --times=a:b   times of day for the views (default noon:night; also low = a low afternoon sun).
## --settle=S    seconds to wait at each view (default 10).
## --walk        after the views, noclip onto the wall top, switch noclip off (V) and walk (W / Space) along and across the cap on both walls.
## --contact     contact ripples and WO10 audio real path (use with --no-views): noclip across the field at 20 km (scripted flight, view looking back at
##               the ripple), then walk on the wall top along the cap (no crossing), across the centre-line root and back. Shots go to <out>/frame%08d.png
##               (a sequence for ffmpeg_tools sheet); prints CONTACT lines (adapter counters, vacuum weight/cutoff/gain, whump) and CONTACT_REAL=PASS|FAIL.
## --hum         field hum hook real path (use with --no-views): injects a 220 Hz tone at runtime (nothing shipped) and flies in at the wall-top skirt height from
##               3.1 km out to the skirt and through it, logging the two voice gains and dB each second (HUM lines, HUM_REAL=PASS|FAIL).
## --no-views    skip the views (walk only).
## --only=a:b    take only these views (floor, top_along, top_across, top_above, band, line, anchor, far50, arch); --only=none skips them all.
## --field       containment field WO3 views (weather clock paused): each pose is shot with the field on, twice (paused clock: identical),
##               with its coverage tint (DebugMode 1) and off (A/B), with the near patch state; frame-time A-B-A at three poses; 150 frames
##               at the grazing wall-top pose (flicker) and a 120-frame approach climb. --field-views=a:b limits them (top_up, top_close, top_along,
##               floor, under20, over20, apex_under, apex_over, vacuum, conduit10, conduit50, conduit_top, far50, flicker, approach).
## Prints RIM_WALL lines, RIM_WALL hull ... triangle counts, RIM_WALL_WALK=PASS|FAIL when --walk, and RIM_WALL=DONE at the end.

const MENU := preload("res://scenes/StartMenu.tscn")
const CONTACT_PROBE := preload("res://tools/ContainmentContactProbe.cs")

var _menu: Control
var _warm: Node
var _world: Node
var _handed_off: Node
var _out := "res://.godot/captures/field/wo12/raw"
var _explorer: Node
var _grid: Node
var _noon_cache := -1.0
var _walk_failures: Array[String] = []

func _initialize() -> void:
	create_timer(1500.0).timeout.connect(func(): print("RIM_WALL watchdog"); quit(3))
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
	var image := root.get_texture().get_image()
	image.save_png(ProjectSettings.globalize_path(_out + "/" + name + ".png"))
	print("RIM_WALL shot ", name, " pose=", _pose_text(), " prims=", RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_PRIMITIVES_IN_FRAME),
		" draws=", RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME))

func _pose() -> Dictionary:
	return _explorer.call("GetCanonicalPoseMeters")

func _pose_text() -> String:
	var p := _pose()
	return "around=%.1f vertical=%.2f across=%.2f yaw=%.1f pitch=%.1f grounded=%s noclip=%s" % [
		float(p["around_m"]), float(p["vertical_m"]), float(p["across_m"]), float(p["yaw_deg"]), float(p["pitch_deg"]),
		str(p.get("grounded", "?")), str(p.get("noclip", "?"))]

func _key(code: Key, pressed: bool) -> void:
	var ev := InputEventKey.new()
	ev.keycode = code
	ev.physical_keycode = code
	ev.pressed = pressed
	Input.parse_input_event(ev)

func _tap(code: Key) -> void:
	_key(code, true)
	await process_frame
	await process_frame
	_key(code, false)
	await process_frame

func _hold(code: Key, seconds: float) -> void:
	_key(code, true)
	await create_timer(seconds).timeout
	_key(code, false)
	await process_frame

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
	# "low": a low afternoon sun (about 11 degrees up), for the glass glint and the etch sparkle (containment field WO3).
	var f := noon if tod == "noon" else (fposmod(noon + 0.22, 1.0) if tod == "low" else fposmod(noon + 0.5, 1.0))
	cycle.call("set_cycle_fraction", f)
	cycle.set("Paused", true)
	await create_timer(2.0).timeout
	print("RIM_WALL time %s fraction=%.4f daylight=%.3f" % [tod, f, float(cycle.call("get_daylight_factor"))])

func _hull_counts() -> void:
	var groups := {"proxy": 0, "global_walls": 0, "global_roof": 0, "analytic": 0, "far_shell": 0}
	var stack: Array[Node] = [_world]
	while not stack.is_empty():
		var n: Node = stack.pop_back()
		for c in n.get_children():
			stack.append(c)
		if not (n is MeshInstance3D) or (n as MeshInstance3D).mesh == null:
			continue
		var key := ""
		if n.name == "StructuralHullLod":
			key = "proxy"
		elif String(n.name).begins_with("GlobalStructuralEdgeWalls_"):
			key = "global_walls"
		elif String(n.name).begins_with("GlobalStructuralRoof_"):
			key = "global_roof"
		elif n.name == "AnalyticGlobalStructuralShell":
			key = "analytic"
		elif n.name == "ScaledStructuralShell":
			key = "far_shell"
		if key == "":
			continue
		var mesh: Mesh = (n as MeshInstance3D).mesh
		var tris := 0
		for s in mesh.get_surface_count():
			var arrays := mesh.surface_get_arrays(s)
			var idx: Variant = arrays[Mesh.ARRAY_INDEX]
			tris += (idx.size() if idx != null else (arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()) / 3
		groups[key] = int(groups[key]) + tris
	print("RIM_WALL hull triangles ", groups)

func _cue_line_around(around: float, circumference: float, spacing: float) -> float:
	# The hull shader measures around from atan(z, x), i.e. canonical around in (-C/2, C/2].
	var shader_around := around if around <= circumference * 0.5 else around - circumference
	return fposmod(round(shader_around / spacing) * spacing, circumference)

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
		print("RIM_WALL no RingWarmWorld")
		quit(1)
		return
	_warm.handed_off.connect(func(w: Node) -> void: _handed_off = w)
	var warm_deadline := Time.get_ticks_msec() + 300000
	var next_status := Time.get_ticks_msec() + 15000
	while int(_warm.state) != 2 and Time.get_ticks_msec() < warm_deadline:
		if Time.get_ticks_msec() >= next_status:
			next_status += 15000
			var loading: Node = root.get_node_or_null("RingWorldLoading")
			var b: Variant = loading.get("_bridge") if loading != null else null
			print("RIM_WALL warm status state=", _warm.state, " stage=", loading.call("get_stage") if loading != null else "?",
				" readiness=", b.call("CaptureReadiness") if b != null and is_instance_valid(b) else "no bridge")
		await process_frame
	if int(_warm.state) != 2:
		print("RIM_WALL warm world never ready")
		quit(1)
		return
	_world = _warm.world
	await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), 180000)
	_menu.continue_button.pressed.emit()
	if not await _wait_until(func() -> bool: return _handed_off != null, 240000):
		print("RIM_WALL hand-off never completed")
		quit(1)
		return
	_explorer = _world.get_node("Explorer")
	_grid = _world.get_node("ChunkGridManager")
	var circumference := float(_grid.call("GetCanonicalRingCircumferenceMeters64"))
	var width := float(_grid.get("RingAcrossWidthMeters"))
	var top := float(_grid.call("GetStructuralWallTopGlobalY"))
	var line := _cue_line_around(float(_pose()["around_m"]), circumference, circumference / 126.0)
	var terrain: Node = _world.get_node_or_null("Terrain3DGameplay")
	print("RIM_WALL handed off circumference=%.1f width=%.1f wall_top=%.1f cue_line_around=%.1f %s" % [circumference, width, top, line, _pose_text()])
	var settle := float(_arg("settle", "10"))
	if _arg("no-views", "") == "":
		# The first teleport sets the local clock position for the noon search.
		_teleport(line, top + 1.7, -64.0, 0.0, -8.0)
		await create_timer(settle).timeout
		for tod in _arg("times", "noon:night").split(":"):
			await _set_time_of_day(tod)
			var floor_h := 0.0
			if terrain != null:
				floor_h = float(terrain.call("SampleHeight", line, 1500.0))
			if not is_finite(floor_h):
				floor_h = 0.0
			# Stay above the sea (sea level is vertical 0).
			floor_h = maxf(floor_h, 0.0)
			# Habitat floor, 1.5 km from the across-0 wall, looking at its face and top edge at the cue line.
			if _want("floor"):
				_teleport(line, floor_h + 40.0, 1500.0, -90.0, 78.0)
				await create_timer(settle).timeout
				await _shot("%s_floor" % tod)
			# On the wall top (across -64), 2.5 km before the cue line, looking along the cap over the line.
			if _want("top_along"):
				_teleport(line - 2500.0, top + 1.7, -64.0, 0.0, -8.0)
				await create_timer(settle).timeout
				await _shot("%s_top_along" % tod)
			# On the wall top at the cue line, looking across the cap toward the habitat.
			if _want("top_across"):
				_teleport(line, top + 1.7, -100.0, 90.0, -25.0)
				await create_timer(settle).timeout
				await _shot("%s_top_across" % tod)
			# Above the wall top, looking down onto the cap and both faces at the cue line.
			if _want("top_above"):
				_teleport(line - 600.0, top + 400.0, 300.0, -150.0, -45.0)
				await create_timer(settle).timeout
				await _shot("%s_top_above" % tod)
			# Just inside the habitat, 300 m above the wall top, 4 km before the cue line, looking along the wall: the cue line crosses the
			# cap and the inner face 2.8-5.2 km ahead (proxy tier near, global hull beyond the 4.75 km hand-off).
			if _want("band"):
				_teleport(line - 4000.0, top + 300.0, 250.0, 0.0, -5.0)
				await create_timer(settle).timeout
				await _shot("%s_band" % tod)
			# Above the cap centre, 2 km before the cue line, looking along it: the cap must stay plain (no rail, no cross grid) until the blue
			# line's fade starts 2.4 km before the line, and carry the line from there on.
			if _want("line"):
				_teleport(line - 2600.0, top + 150.0, -64.0, 0.0, -30.0)
				await create_timer(settle).timeout
				await _shot("%s_line" % tod)
			# WO8: a red marker at the wall-top anchor (around = cue line, across = -64 centre line, vertical = wall top), seen along the wall and from
			# straight above; the blue line's band must be centred on it.
			if _want("anchor"):
				var marker := MeshInstance3D.new()
				var marker_mesh := CylinderMesh.new()
				marker_mesh.top_radius = 10.0
				marker_mesh.bottom_radius = 10.0
				marker_mesh.height = 800.0
				marker.mesh = marker_mesh
				var marker_mat := StandardMaterial3D.new()
				marker_mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
				marker_mat.albedo_color = Color(1.0, 0.1, 0.1)
				marker.material_override = marker_mat
				_world.add_child(marker)
				# Along the wall: 1.8 km before the anchor, 250 m above the wall top.
				_teleport(line - 1800.0, top + 250.0, -64.0, 0.0, -8.0)
				await create_timer(settle).timeout
				_place_anchor_marker(marker, 1800.0, 250.0)
				await _shot("%s_anchor_along" % tod)
				# Straight above: 60 m before the anchor, 1.5 km up.
				_teleport(line - 60.0, top + 1500.0, -64.0, 0.0, -89.0)
				await create_timer(settle).timeout
				_place_anchor_marker(marker, 60.0, 1500.0)
				await _shot("%s_anchor_above" % tod)
				# From the habitat at the anchor's around (2.2 km out, 1.1 km below the wall top), looking at the across-0 face: the band on the face must be symmetric about the view centre. (The marker is not drawn here: it did not show at this range.)
				_teleport(line, top - 1100.0, 2200.0, -90.0, 26.5)
				await create_timer(settle).timeout
				await _shot("%s_anchor_face" % tod)
				marker.queue_free()
			# 50 km into the habitat, 3 km above the wall top, looking back at the wall.
			if _want("far50"):
				_teleport(line, top + 3000.0, 50000.0, -90.0, -3.0)
				await create_timer(settle).timeout
				await _shot("%s_far50" % tod)
			if _arg("field", "") != "":
				await _field_views(tod, line, top, width, floor_h, settle)
			if tod == "noon":
				_hull_counts()
			# Habitat floor at the centre line looking up the arch (far shell walls along the far side).
			var mid_h := 0.0
			if terrain != null:
				mid_h = float(terrain.call("SampleHeight", line, width * 0.25))
			if not is_finite(mid_h):
				mid_h = 0.0
			mid_h = maxf(mid_h, 0.0)
			if _want("arch"):
				_teleport(line, mid_h + 40.0, width * 0.25, 0.0, 55.0)
				await create_timer(settle).timeout
				await _shot("%s_arch" % tod)
	if _arg("contact", "") != "":
		await _contact(line, top, width)
	if _arg("hum", "") != "":
		await _hum(line, top, width)
	if _arg("walk", "") != "":
		await _walk(line, top, width)
		print("RIM_WALL_WALK=%s %s" % ["PASS" if _walk_failures.is_empty() else "FAIL", "; ".join(_walk_failures)])
	print("RIM_WALL=DONE out=", _out)
	quit(0)

# ---- Containment field WO3 -----------------------------------------------------------------------------------------------------------------

# Field profile height at an across (runtime value of the WO1 profile: 128 m centre-line roots, 1,600 m skirt, 40 km apex).
func _field_h(across: float, top: float, width: float) -> float:
	var r0 := -64.0
	var rw := width + 64.0
	if across < r0 or across > rw:
		return NAN
	var crown := top + 1600.0
	var a := (rw - r0) * 0.5
	var x := (across - (r0 + rw) * 0.5) / a
	return crown + (40000.0 - crown) * sqrt(maxf(0.0, 1.0 - x * x))

# Across (near the across-0 wall) where the field is at height h (ellipse part).
func _field_across_at(h: float, top: float, width: float) -> float:
	var r0 := -64.0
	var rw := width + 64.0
	var crown := top + 1600.0
	var a := (rw - r0) * 0.5
	var y := clampf((h - crown) / (40000.0 - crown), 0.0, 1.0)
	return (r0 + rw) * 0.5 - a * sqrt(1.0 - y * y)

func _field_node() -> Node:
	return _world.get_node_or_null("ContainmentField")

func _field_state() -> String:
	var f := _field_node()
	if f == null:
		return "no field node"
	return "near=%s post=%s builds=%d post_dispatches=%d cpu_us=%.1f cpu_avg_us=%.1f rebuild_us=%.1f" % [str(f.get("NearPatchVisible")), str(f.get("PostPassActive")),
		int(f.get("PatchBuildCount")), int(f.get("PostPassDispatches")), float(f.get("LastProcessMicroseconds")), float(f.get("AverageProcessMicroseconds")), float(f.get("LastRebuildMicroseconds"))] + " " + str(f.get("TimingBreakdown"))

func _want_field(view: String) -> bool:
	var only := _arg("field-views", "")
	return only == "" or view in only.split(":")

# On, a second "on" frame (paused weather clock), off (A/B), the lattice alone, then the coverage tint and the lattice mask.
func _field_shots(name: String, settle: float) -> void:
	var f := _field_node()
	f.set("Enabled", true)
	f.set("DebugMode", 0)
	await create_timer(settle).timeout
	await _shot(name)
	print("RIM_WALL field ", name, " ", _field_state())
	await _shot(name + "_on2")
	f.set("Enabled", false)
	await create_timer(1.5).timeout
	await _shot(name + "_off")
	f.set("Enabled", true)
	# The lattice alone (shimmer and sheen 0), to judge it against the "off" shot.
	var shimmer: float = f.get("ShimmerStrength")
	var sheen: float = f.get("SheenStrength")
	f.set("ShimmerStrength", 0.0)
	f.set("SheenStrength", 0.0)
	await create_timer(1.0).timeout
	await _shot(name + "_lattice")
	f.set("ShimmerStrength", shimmer)
	f.set("SheenStrength", sheen)
	# Debug views last, away from the A/B pair.
	f.set("DebugMode", 1)
	await create_timer(0.5).timeout
	await _shot(name + "_dbg")
	f.set("DebugMode", 3)
	await create_timer(0.5).timeout
	await _shot(name + "_dbglattice")
	f.set("DebugMode", 0)
	await create_timer(1.0).timeout

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

func _field_aba(label: String) -> void:
	var f := _field_node()
	var a1 := await _gpu_ms(240)
	var d1 := RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME)
	f.set("Enabled", false)
	var b := await _gpu_ms(240)
	var d0 := RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME)
	f.set("Enabled", true)
	var a2 := await _gpu_ms(240)
	print("RIM_WALL field perf %s gpu_ms on=%.3f off=%.3f on=%.3f draws on=%d off=%d %s" % [label, a1, b, a2, d1, d0, _field_state()])

func _field_views(tod: String, line: float, top: float, width: float, floor_h: float, settle: float) -> void:
	var f := _field_node()
	if f == null:
		print("RIM_WALL field node missing")
		return
	var weather: Node = _world.get_node_or_null("RingVolumetricClouds")
	if weather != null:
		weather.set("CloudSimulationPaused", true)
	# Lock auto exposure for the A/B shots (--field-exposure=auto keeps it): with it on, any change in what is on screen, the field's sheen
	# and lattice included, nudges the exposure of the whole frame, and a debug tint shot bleeds into the next shot.
	var exposure_owner: Node = null
	for n in _world.find_children("*", "", true, false):
		var sc: Variant = n.get_script()
		if sc != null and (sc as Script).resource_path.ends_with("day_side_atmosphere.gd"):
			exposure_owner = n
			break
	var exposure_was: Variant = exposure_owner.get("storm_auto_exposure_enabled") if exposure_owner != null else null
	var lock_exposure := exposure_owner != null and _arg("field-exposure", "") != "auto"
	if lock_exposure:
		exposure_owner.set("storm_auto_exposure_enabled", false)
	print("RIM_WALL field exposure owner=", exposure_owner, " locked=", lock_exposure)
	var x20 := _field_across_at(20000.0, top, width)
	var mid := width * 0.5
	print("RIM_WALL field profile crown=%.1f across_at_20km=%.1f h(200)=%.1f" % [top + 1600.0, x20, _field_h(200.0, top, width)])
	# Wall top, habitat side (across 200 m, 50 m up): looking up and along, the skirt and arch pass 264 m away.
	if _want_field("top_up"):
		_teleport(line - 1500.0, top + 50.0, 200.0, -35.0, 30.0)
		await _field_shots("%s_field_top_up" % tod, settle)
	# Close-up: wall top, 300 m from the skirt and 150 m up, looking up at it (the seams at 300-500 m).
	if _want_field("top_close"):
		_teleport(line - 1500.0, top + 150.0, 236.0, -70.0, 32.0)
		await _field_shots("%s_field_top_close" % tod, settle)
	# Same place, grazing along the skirt and the wall-top line.
	if _want_field("top_along"):
		_teleport(line - 1500.0, top + 50.0, 200.0, -12.0, 3.0)
		await _field_shots("%s_field_top_along" % tod, settle)
		if tod == "noon":
			await _field_aba("near(top_along)")
	# Habitat floor 1.5 km from the wall, looking at the wall top: the field is >= 10 km away (idle, identity).
	if _want_field("floor"):
		_teleport(line, floor_h + 40.0, 1500.0, -90.0, 60.0)
		await _field_shots("%s_field_floor" % tod, settle)
		if tod == "noon":
			await _field_aba("idle(floor)")
	# Noclip at ~20 km: 400 m under the field looking up and along it, then 400 m above it looking down at the clouds (post-Sunshine pass).
	if _want_field("under20"):
		_teleport(line, 20000.0 - 400.0, x20, -60.0, 35.0)
		await _field_shots("%s_field_under20" % tod, settle)
		if tod == "noon":
			await _field_aba("near_below(under20)")
	if _want_field("over20"):
		_teleport(line, 20000.0 + 400.0, x20, 60.0, -35.0)
		await _field_shots("%s_field_over20" % tod, settle)
		if tod == "noon":
			await _field_aba("near_above(over20)")
	# Apex: 300 m under looking up, 300 m above looking down.
	if _want_field("apex_under"):
		_teleport(line, 40000.0 - 300.0, mid, 30.0, 40.0)
		await _field_shots("%s_field_apex_under" % tod, settle)
	if _want_field("apex_over"):
		_teleport(line, 40000.0 + 300.0, mid, 30.0, -40.0)
		await _field_shots("%s_field_apex_over" % tod, settle)
	# Vacuum side of the wall top (across -400, 300 m up), looking back into the habitat through the skirt.
	if _want_field("vacuum"):
		_teleport(line - 1500.0, top + 300.0, -400.0, 70.0, -12.0)
		await _field_shots("%s_field_vacuum" % tod, settle)
	# Power conduits (the 180 m blue lines under the projector sites): the across-0 wall at the anchor from the habitat floor 10 km and 50 km out,
	# and from the wall top standing on the conduit at the anchor.
	if _want_field("conduit10"):
		_teleport(line, floor_h + 40.0, 10000.0, -90.0, 30.0)
		await create_timer(settle).timeout
		await _shot("%s_conduit_floor10" % tod)
	if _want_field("conduit50"):
		# 2 km above the floor so the hills and trees 50 km out do not hide the wall.
		_teleport(line, floor_h + 2000.0, 50000.0, -90.0, 8.0)
		await create_timer(settle).timeout
		await _shot("%s_conduit_floor50" % tod)
	if _want_field("conduit_top"):
		_teleport(line - 350.0, top + 60.0, 120.0, -10.0, -24.0)
		await create_timer(settle).timeout
		await _shot("%s_conduit_top" % tod)
		_teleport(line, top + 1.7, -64.0, 90.0, -30.0)
		await create_timer(settle).timeout
		await _shot("%s_conduit_on_anchor" % tod)
	# 50 km into the habitat, looking back at the wall and the arch: invisible.
	if _want_field("far50"):
		_teleport(line, top + 3000.0, 50000.0, -90.0, 8.0)
		await _field_shots("%s_field_far50" % tod, settle)
	# 150 consecutive frames at the grazing wall-top pose with the weather clock running (flicker / TAA check).
	if _want_field("flicker") and tod == "noon":
		if weather != null:
			weather.set("CloudSimulationPaused", false)
		_teleport(line - 1500.0, top + 50.0, 200.0, -12.0, 3.0)
		await create_timer(settle).timeout
		var dir := _out + "/flicker_" + tod
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
		_hide_hud()
		for i in 150:
			await RenderingServer.frame_post_draw
			root.get_texture().get_image().save_png(ProjectSettings.globalize_path("%s/frame%08d.png" % [dir, i]))
		print("RIM_WALL field flicker frames=150 dir=", dir, " ", _field_state())
		if weather != null:
			weather.set("CloudSimulationPaused", true)
	# Approach: climb at across 3 km from 6,000 m to 300 m below the field (the patch switches on at 3.3 km clearance).
	if _want_field("approach") and tod == "noon":
		var h3 := _field_h(3000.0, top, width)
		var dir2 := _out + "/approach_" + tod
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir2))
		_teleport(line, h3 - 6000.0, 3000.0, -60.0, 30.0)
		await create_timer(settle).timeout
		_hide_hud()
		for i in 120:
			var v := lerpf(h3 - 6000.0, h3 - 300.0, float(i) / 119.0)
			_teleport(line, v, 3000.0, -60.0, 30.0)
			await RenderingServer.frame_post_draw
			root.get_texture().get_image().save_png(ProjectSettings.globalize_path("%s/frame%08d.png" % [dir2, i]))
		print("RIM_WALL field approach frames=120 dir=", dir2, " ", _field_state())
	if weather != null:
		weather.set("CloudSimulationPaused", false)
	if lock_exposure:
		exposure_owner.set("storm_auto_exposure_enabled", exposure_was)

func _want(view: String) -> bool:
	var only := _arg("only", "")
	return only == "" or view in only.split(":")

func _expect(ok: bool, what: String) -> void:
	print("RIM_WALL_WALK check %s %s :: %s" % ["ok" if ok else "FAILED", what, _pose_text()])
	if not ok:
		_walk_failures.append(what)

func _walk(line: float, top: float, width: float) -> void:
	for side in [0, 1]:
		var centre := -64.0 if side == 0 else width + 64.0
		var outward_yaw := -90.0 if side == 0 else 90.0
		_teleport(line - 2500.0, top + 15.0, centre, 0.0, -8.0)
		await create_timer(4.0).timeout
		await _tap(KEY_V)
		await create_timer(4.0).timeout
		var p := _pose()
		_expect(not bool(p.get("noclip", true)), "side%d noclip off after V" % side)
		_expect(absf(float(p["vertical_m"]) - top) < 0.25, "side%d landed on the wall top (vertical %.2f, top %.1f)" % [side, float(p["vertical_m"]), top])
		var around0 := float(p["around_m"])
		_explorer.call("SetPresentationView", 0.0, -8.0)
		await _hold(KEY_W, 4.0)
		p = _pose()
		_expect(float(p["around_m"]) - around0 > 10.0, "side%d walked along the cap (%.1f m)" % [side, float(p["around_m"]) - around0])
		_expect(absf(float(p["vertical_m"]) - top) < 0.25, "side%d stayed on the cap while walking" % side)
		if side == 0:
			await _shot("walk_side0_along")
		# Toward the vacuum side: clamp at the outer face.
		_explorer.call("SetPresentationView", outward_yaw, -8.0)
		await _hold(KEY_W, 12.0)
		p = _pose()
		var across := float(p["across_m"])
		var outer := -128.0 if side == 0 else width + 128.0
		_expect(absf(across - outer) < 1.5 and (across >= outer if side == 0 else across <= outer), "side%d clamped at the outer face (across %.2f, face %.1f)" % [side, across, outer])
		_expect(absf(float(p["vertical_m"]) - top) < 0.25, "side%d still on the cap at the outer face" % side)
		if side == 0:
			await _shot("walk_side0_outer_edge")
		# Toward the habitat: clamp at the inner face (no walking off the 10 km drop).
		_explorer.call("SetPresentationView", outward_yaw + 180.0, -8.0)
		await _hold(KEY_W, 20.0)
		p = _pose()
		across = float(p["across_m"])
		var inner := 0.0 if side == 0 else width
		_expect(absf(across - inner) < 1.5 and (across <= inner if side == 0 else across >= inner), "side%d clamped at the inner face (across %.2f, face %.1f)" % [side, across, inner])
		_expect(absf(float(p["vertical_m"]) - top) < 0.25, "side%d still on the cap at the inner face" % side)
		if side == 0:
			await _shot("walk_side0_inner_edge")
		# Jump and land again.
		await _tap(KEY_SPACE)
		await create_timer(0.25).timeout
		var lifted := float(_pose()["vertical_m"]) - top
		await create_timer(3.0).timeout
		p = _pose()
		_expect(lifted > 0.2 and absf(float(p["vertical_m"]) - top) < 0.25, "side%d jumped (%.2f m) and landed on the cap" % [side, lifted])

# Puts the WO8 marker at the wall-top anchor, ahead_m along the wall (the camera's horizontal heading, yaw 0 = increasing around) and down_m below the
# camera, standing on the anchor (the cylinder is centred 400 m above its base). Short offsets only: the ring's curvature is negligible at 2 km.
func _place_anchor_marker(marker: MeshInstance3D, ahead_m: float, down_m: float) -> void:
	var cam := root.get_viewport().get_camera_3d()
	var up: Vector3 = _explorer.call("GetCanonicalSurfaceUp")
	var forward := -cam.global_basis.z
	var tangent := (forward - up * forward.dot(up)).normalized()
	var base := cam.global_position + tangent * ahead_m - up * down_m
	marker.global_position = base + up * 400.0
	marker.global_basis = Basis(Quaternion(Vector3.UP, up))
	print("RIM_WALL anchor marker base=", base, " pose=", _pose_text())

# ---- Contact ripples and WO10 audio -----------------------------------------------------------------------------------------------------------

var _contact_probe: RefCounted
var _contact_failures: Array[String] = []
var _contact_frame := 0

func _cexpect(ok: bool, what: String) -> void:
	print("CONTACT check %s %s" % ["ok" if ok else "FAILED", what])
	if not ok:
		_contact_failures.append(what)

func _cstate() -> Dictionary:
	return _contact_probe.call("State")

func _cshot() -> void:
	_hide_hud()
	for i in 2:
		await process_frame
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	image.save_png(ProjectSettings.globalize_path("%s/frame%08d.png" % [_out, _contact_frame]))
	_contact_frame += 1

func _clog(tag: String, t: float) -> void:
	var s := _cstate()
	var p := _pose()
	print("CONTACT %s t=%.2f across=%.2f vertical=%.1f crossings=%d published=%d suppressed=%d live_body=%d weight=%.3f cutoff_hz=%.0f gain_db=%.2f whumps=%d playing=%s thunder_fx=%s" % [
		tag, t, float(p["across_m"]), float(p["vertical_m"]), int(s.get("crossings", -1)), int(s.get("published", -1)), int(s.get("suppressed", -1)), int(s.get("live_body", -1)),
		float(s.get("weight", -1.0)), float(s.get("cutoff_hz", -1.0)), float(s.get("gain_db", 0.0)), int(s.get("whumps", -1)), str(s.get("whump_playing", false)), str(s.get("engaged_thunder", false))])

func _contact(line: float, top: float, width: float) -> void:
	_contact_probe = CONTACT_PROBE.new()
	_contact_frame = 0
	await _set_time_of_day("noon")
	var s0 := _cstate()
	_cexpect(not s0.is_empty(), "contact state available (host, adapter, audio, bus)")
	# A: noclip across the arch at 20 km (the field is at 20 km about 6.8 km from the across-0 root; start 1.2 km inside it, fly outward at 150 m/s).
	var cross_across := _field_across_at(20000.0, top, width)
	_teleport(line, 20000.0, cross_across + 1200.0, -90.0, 0.0)
	await create_timer(10.0).timeout
	_explorer.call("BeginScriptedFlight")
	await process_frame
	_explorer.call("SetScriptedFlightCommand", Vector3.ZERO, 90.0, 0.0)
	await create_timer(3.0).timeout
	var base := _cstate()
	_clog("A start", 0.0)
	_cexpect(float(base.get("weight", 1.0)) == 0.0 and not bool(base.get("engaged_thunder", true)), "A: contained side before the crossing (weight 0, buses clean)")
	_explorer.call("SetScriptedFlightCommand", Vector3(0.0, 0.0, -150.0), 90.0, 0.0)
	var c0 := int(base.get("crossings", 0))
	var t := 0.0
	var t_cross := -1.0
	var shots_at := [0.1, 0.4, 0.8, 1.3]
	var shot_i := 0
	var weight_lo := -1.0
	var weight_hi := -1.0
	var pre_shot := false
	_last_tick_us = 0
	while t < 14.0:
		await process_frame
		t += _frame_dt()
		var s := _cstate()
		var p := _pose()
		var across := float(p["across_m"])
		if not pre_shot and across < cross_across + 250.0 and t_cross < 0.0:
			pre_shot = true
			await _cshot()
		if weight_lo < 0.0 and float(s.get("weight", 0.0)) > 0.05:
			weight_lo = across
		if weight_hi < 0.0 and float(s.get("weight", 0.0)) > 0.95:
			weight_hi = across
		if t_cross < 0.0 and int(s.get("crossings", 0)) > c0:
			t_cross = t
			_clog("A crossing", 0.0)
			_cexpect(int(s.get("published", 0)) > int(base.get("published", 0)) and int(s.get("live_body", 0)) >= 1, "A: BodyContact ripple published and live on the bus")
			_cexpect(int(s.get("whumps", 0)) > int(base.get("whumps", 0)), "A: whump fired (count %d, %.1f dB, pitch %.2f)" % [int(s.get("whumps", 0)), float(s.get("whump_db", 0.0)), float(s.get("whump_pitch", 0.0))])
			print("CONTACT A crossing speed=%.1f m/s radius=%.1f m energy=%.2f to_vacuum=%s at across=%.1f vertical=%.1f" % [float(s.get("last_speed", 0.0)), float(s.get("last_radius", 0.0)),
				float(s.get("last_energy", 0.0)), str(s.get("last_to_vacuum", false)), float(s.get("last_across", 0.0)), float(s.get("last_vertical", 0.0))])
		if t_cross >= 0.0:
			var since := t - t_cross
			if shot_i < shots_at.size() and since >= float(shots_at[shot_i]):
				_clog("A +%.1fs" % since, since)
				await _cshot()
				shot_i += 1
			if since > 4.0:
				break
	_explorer.call("SetScriptedFlightCommand", Vector3.ZERO, 90.0, 0.0)
	await create_timer(1.0).timeout
	var after := _cstate()
	_clog("A end", 0.0)
	_cexpect(t_cross > 0.0 and int(after.get("crossings", 0)) == c0 + 1, "A: exactly one crossing (%d)" % (int(after.get("crossings", 0)) - c0))
	_cexpect(float(after.get("weight", 0.0)) > 0.99 and absf(float(after.get("cutoff_hz", 0.0)) - 600.0) < 10.0 and absf(float(after.get("gain_db", 0.0)) + 12.0) < 0.2,
		"A: vacuum low-pass engaged (weight %.3f, %.0f Hz, %.1f dB)" % [float(after.get("weight", 0.0)), float(after.get("cutoff_hz", 0.0)), float(after.get("gain_db", 0.0))])
	_cexpect(bool(after.get("engaged_thunder", false)) and bool(after.get("engaged_wind", false)) and bool(after.get("engaged_rainbed", false)), "A: effects enabled on Thunder, Wind and RainBed")
	print("CONTACT A ramp: weight 0.05 at across %.2f, 0.95 at across %.2f (%.2f m)" % [weight_lo, weight_hi, absf(weight_hi - weight_lo)])
	# Back inside: the weight must return to 0.
	_explorer.call("SetScriptedFlightCommand", Vector3(0.0, 0.0, 150.0), 90.0, 0.0)
	await create_timer(14.0).timeout
	_explorer.call("SetScriptedFlightCommand", Vector3.ZERO, 90.0, 0.0)
	await create_timer(1.0).timeout
	var back := _cstate()
	_clog("A back inside", 0.0)
	_cexpect(float(back.get("weight", 1.0)) == 0.0 and int(back.get("crossings", 0)) == c0 + 2, "A: back inside, weight 0, second crossing counted (%d)" % (int(back.get("crossings", 0)) - c0))
	_explorer.call("EndScriptedFlight")
	await create_timer(1.0).timeout
	# B: walk on the wall top (the cap spans across -128..0, the root is at -64).
	_teleport(line - 2500.0, top + 15.0, -100.0, 0.0, -8.0)
	await create_timer(4.0).timeout
	var pp := _pose()
	if bool(pp.get("noclip", false)):
		await _tap(KEY_V)
	await create_timer(4.0).timeout
	pp = _pose()
	_cexpect(not bool(pp.get("noclip", true)) and absf(float(pp["vertical_m"]) - top) < 0.25, "B: walking on the wall top")
	var b0 := _cstate()
	_clog("B start (outboard half)", 0.0)
	_cexpect(float(b0.get("weight", 0.0)) > 0.99, "B: outboard half of the cap is the vacuum side (weight %.3f)" % float(b0.get("weight", 0.0)))
	var bc0 := int(b0.get("crossings", 0))
	_explorer.call("SetPresentationView", 0.0, -8.0)
	await _hold(KEY_W, 5.0)
	var along := _cstate()
	_clog("B along the cap", 0.0)
	_cexpect(int(along.get("crossings", 0)) == bc0, "B: no false crossing walking along the cap beside the root (%d)" % (int(along.get("crossings", 0)) - bc0))
	# Toward the habitat (+across), across the root.
	_explorer.call("SetPresentationView", 90.0, -8.0)
	var t2 := 0.0
	var seen := false
	_last_tick_us = 0
	_key(KEY_W, true)
	while t2 < 30.0:
		await process_frame
		t2 += _frame_dt()
		var s := _cstate()
		if not seen and int(s.get("crossings", 0)) > bc0:
			seen = true
			_clog("B crossing inward", t2)
			_cexpect(int(s.get("whumps", 0)) > int(along.get("whumps", 0)) and int(s.get("live_body", 0)) >= 1, "B: whump and ripple at the root crossing")
			await _cshot()
		if seen and float(_pose()["across_m"]) > -64.0 + 12.0:
			break
	_key(KEY_W, false)
	await create_timer(0.5).timeout
	var inw := _cstate()
	_clog("B inboard", 0.0)
	_cexpect(seen and int(inw.get("crossings", 0)) == bc0 + 1, "B: one crossing walking inward (%d)" % (int(inw.get("crossings", 0)) - bc0))
	_cexpect(float(inw.get("weight", 1.0)) == 0.0, "B: inboard of the root the weight is 0 (%.3f)" % float(inw.get("weight", 1.0)))
	# And back out.
	await create_timer(1.5).timeout
	_explorer.call("SetPresentationView", -90.0, -8.0)
	seen = false
	t2 = 0.0
	_last_tick_us = 0
	_key(KEY_W, true)
	while t2 < 30.0:
		await process_frame
		t2 += _frame_dt()
		var s := _cstate()
		if not seen and int(s.get("crossings", 0)) > bc0 + 1:
			seen = true
			_clog("B crossing outward", t2)
		if seen and float(_pose()["across_m"]) < -64.0 - 12.0:
			break
	_key(KEY_W, false)
	await create_timer(1.5).timeout
	var outw := _cstate()
	_clog("B outboard", 0.0)
	_cexpect(seen and int(outw.get("crossings", 0)) == bc0 + 2 and float(outw.get("weight", 0.0)) > 0.99, "B: one crossing walking back out, vacuum side again")
	_cexpect(int(outw.get("published", 0)) - int(b0.get("published", 0)) == 2 and int(outw.get("whumps", 0)) - int(b0.get("whumps", 0)) == 2, "B: two ripples and two whumps for the two crossings")
	print("CONTACT_REAL=%s %s" % ["PASS" if _contact_failures.is_empty() else "FAIL", "; ".join(_contact_failures)])

var _last_tick_us := 0

func _frame_dt() -> float:
	var now := Time.get_ticks_usec()
	var dt := 0.0 if _last_tick_us == 0 else float(now - _last_tick_us) / 1000000.0
	_last_tick_us = now
	return dt

func _hum(line: float, top: float, width: float) -> void:
	_contact_probe = CONTACT_PROBE.new()
	_contact_failures.clear()
	var injected: bool = _contact_probe.call("InjectTone")
	_cexpect(injected, "HUM: test tone injected")
	# Fly in at 800 m above the wall top (inside the 1.6 km skirt) from 3.1 km out, at 150 m/s toward the across-0 skirt (across -64), and through it.
	_teleport(line, top + 800.0, 3100.0, -90.0, 0.0)
	await create_timer(10.0).timeout
	_explorer.call("BeginScriptedFlight")
	await process_frame
	_explorer.call("SetScriptedFlightCommand", Vector3.ZERO, -90.0, 0.0)
	await create_timer(2.0).timeout
	var far := _cstate()
	print("HUM far d=%.1f playing=%s pos_gain=%.4f st_gain=%.4f" % [float(far.get("hum_distance", -1.0)), str(far.get("hum_playing", false)), float(far.get("hum_pos_gain", -1.0)), float(far.get("hum_st_gain", -1.0))])
	_explorer.call("SetScriptedFlightCommand", Vector3(0.0, 0.0, -150.0), -90.0, 0.0)
	var t := 0.0
	var next_log := 0.0
	var max_pos := 0.0
	var max_st := 0.0
	var min_pos_when_close := 1.0
	var seen_stereo_only := false
	_last_tick_us = 0
	while t < 30.0:
		await process_frame
		t += _frame_dt()
		var s := _cstate()
		var across := float(_pose()["across_m"])
		max_pos = maxf(max_pos, float(s.get("hum_pos_gain", 0.0)))
		max_st = maxf(max_st, float(s.get("hum_st_gain", 0.0)))
		if float(s.get("hum_distance", 9999.0)) < 15.0 and float(s.get("hum_pos_gain", 1.0)) < 0.05:
			seen_stereo_only = true
		if t >= next_log:
			next_log += 1.0
			print("HUM t=%.1f across=%.1f d=%.1f playing=%s pos_gain=%.3f st_gain=%.3f pos_db=%.1f st_db=%.1f src=%s weight=%.2f hum_bus_fx=%s" % [t, across, float(s.get("hum_distance", -1.0)), str(s.get("hum_playing", false)),
				float(s.get("hum_pos_gain", 0.0)), float(s.get("hum_st_gain", 0.0)), float(s.get("hum_pos_db", -80.0)), float(s.get("hum_st_db", -80.0)), str(s.get("hum_pos", Vector3.ZERO)), float(s.get("weight", 0.0)), str(s.get("engaged_hum_bus", false))])
		if across < -140.0:
			break
	var end := _cstate()
	_explorer.call("SetScriptedFlightCommand", Vector3.ZERO, -90.0, 0.0)
	_cexpect(not bool(far.get("hum_playing", true)) and float(far.get("hum_distance", 0.0)) > 3000.0, "HUM: silent beyond 3 km (distance %.0f)" % float(far.get("hum_distance", 0.0)))
	_cexpect(max_pos > 0.5 and max_st > 0.9, "HUM: positional voice peaks %.2f, stereo voice reaches %.2f" % [max_pos, max_st])
	_cexpect(seen_stereo_only, "HUM: within 15 m the positional voice is silent (stereo only)")
	_cexpect(bool(end.get("engaged_hum_bus", false)) and float(end.get("weight", 0.0)) > 0.99, "HUM: past the field the hum bus carries the vacuum low-pass (weight %.2f)" % float(end.get("weight", 0.0)))
	_explorer.call("EndScriptedFlight")
	print("HUM_REAL=%s %s" % ["PASS" if _contact_failures.is_empty() else "FAIL", "; ".join(_contact_failures)])
