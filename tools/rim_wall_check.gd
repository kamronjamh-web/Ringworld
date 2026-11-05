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
## --times=a:b   times of day for the views (default noon:night).
## --settle=S    seconds to wait at each view (default 10).
## --walk        after the views, noclip onto the wall top, switch noclip off (V) and walk (W / Space) along and across the cap on both walls.
## --no-views    skip the views (walk only).
## --only=a:b    take only these views (floor, top_along, top_across, top_above, band, line, anchor, far50, arch).
## Prints RIM_WALL lines, RIM_WALL hull ... triangle counts, RIM_WALL_WALK=PASS|FAIL when --walk, and RIM_WALL=DONE at the end.

const MENU := preload("res://scenes/StartMenu.tscn")

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
	var f := noon if tod == "noon" else fposmod(noon + 0.5, 1.0)
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
	if _arg("walk", "") != "":
		await _walk(line, top, width)
		print("RIM_WALL_WALK=%s %s" % ["PASS" if _walk_failures.is_empty() else "FAIL", "; ".join(_walk_failures)])
	print("RIM_WALL=DONE out=", _out)
	quit(0)

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
