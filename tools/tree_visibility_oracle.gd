extends SceneTree
## Tree visibility oracle (docs/occlusion-culling-plan.md sections 3.1 and 6, OC-0), promoted from the 2026-11-16 occlusion study probe.
## Boots the real start menu on the isolated slot 3 copy and presses Continue. At each ground pose and yaw it captures the main camera's opaque
## depth (metres, via RingFarDepthCapture) with everything and with trees + grass hidden ("terrain only"), builds a max-depth pyramid (Hi-Z), and
## classifies every submitted tree / grass instance and other geometry node as outside the frustum, occluded (by everything, by terrain only) or
## visible. Then the oracle timing pass: tree camera renderers are replaced by CPU-filled copies holding all / in-frustum / unoccluded instances
## (shadows untouched), A-X-A, VSync off; the vertex-kill modes collapse the same instances in the vertex shader instead of removing them.
## Use it as the before/after harness for the tree frustum filter (OC-2): "frustum" mode is the oracle for what the filter should leave.
## Run (windowed Vulkan, not --headless; never on the player's save; takes the GPU queue):
##   tools/tree_visibility_run.ps1 -Test oracle [-ScriptArgs "--yaws=0 --timing=0"]
## Args (after --): --out=<dir> [--poses=<json>] [--names=a,b] [--yaws=0,90,180,270] [--timing=1] [--win=8] [--grass-cap=250000]
##   [--wind-margin=<fraction of height>] [--modes=all,frustum,vis_terrain,vis_any,none]
## Prints TREE_VISIBILITY_ORACLE=PASS (report.json written) or =FAIL <reason>. Summarise with tools/tree_visibility_summary.py.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const DEPTH := preload("res://scripts/render/RingFarDepthCapture.gd")
const INF := 1.0e30
const STAMP := preload("res://tools/landscape_pass_timestamp_effect.gd")
var stamp_effects: Array = []
var stamp_env: Environment
var names_logged := false
var lod_regex := RegEx.create_from_string("(?:Lod|_L)([0-9])")

var args := {}
var out_dir := ""
var report := {"poses": {}, "events": [], "errors": []}
var game: Node
var explorer: Node
var surface: Node
var trees: Node
var grass: Node
var t_start := 0
var wind_y := 0.6
var restore_stack: Array = []
var copies: Array = []   # [instance rid, multimesh rid]


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			if eq > 0:
				args[text.substr(2, eq - 2)] = text.substr(eq + 1)
			else:
				args[text.substr(2)] = "1"
	call_deferred("_run")


func _log(text: String) -> void:
	var line := "OCC %7.1fs %s" % [float(Time.get_ticks_msec() - t_start) / 1000.0, text]
	print(line)
	report["events"].append(line)


func _write() -> void:
	var file := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(report, " "))
		file.close()


func _finish(code: int) -> void:
	_clear_copies()
	_restore()
	_write()
	_log("OCC_DONE code=%d" % code)
	if code == 0 and report["errors"].is_empty():
		print("TREE_VISIBILITY_ORACLE=PASS")
	else:
		push_error("TREE_VISIBILITY_ORACLE=FAIL code=%d %s" % [code, str(report["errors"])])
	quit(code)


func _run() -> void:
	t_start = Time.get_ticks_msec()
	if not WORLD.profile_is_isolated():
		push_error("TREE_VISIBILITY_ORACLE=FAIL user:// (%s) is not an isolated test profile; refusing to touch the player's save." % ProjectSettings.globalize_path("user://"))
		quit(2)
		return
	out_dir = String(args.get("out", ""))
	DirAccess.make_dir_recursive_absolute(out_dir)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	report["machine"] = {"adapter": RenderingServer.get_video_adapter_name(), "engine": Engine.get_version_info().get("string", ""), "args": args}
	change_scene_to_file("res://scenes/StartMenu.tscn")
	await process_frame
	await process_frame
	var menu: Node = current_scene
	if menu == null or menu.name != "StartMenu":
		report["errors"].append("start menu did not load")
		_finish(3)
		return
	var deadline := Time.get_ticks_msec() + 900000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		if bool(menu.get("_cover_retired")) and menu.get("flight_director") != null and not bool(menu.get("menu_locked")):
			break
	var warm: Node = root.get_node_or_null("RingWarmWorld")
	var handed := [null]
	warm.handed_off.connect(func(w: Node): handed[0] = w)
	(menu.get("continue_button") as Button).pressed.emit()
	deadline = Time.get_ticks_msec() + 180000
	while handed[0] == null and Time.get_ticks_msec() < deadline:
		await process_frame
	if handed[0] == null:
		report["errors"].append("continue hand-off never happened")
		_finish(5)
		return
	game = handed[0]
	_log("handed off")
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	trees = game.get_node_or_null("BiomeDecorations")
	grass = game.get_node_or_null("StandingGrass")
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	var wr = RenderingServer.global_shader_parameter_get("ring_tree_wind_response")
	if wr is Vector4:
		wind_y = clampf(wr.y, 0.0, 0.6)
	if args.has("wind-margin"):
		wind_y = float(args["wind-margin"])
	report["wind_response_y"] = wind_y
	report["wind_globals"] = {}
	for gname in ["ring_tree_wind_response", "ring_tree_wind_canopy", "ring_tree_wind_parts", "ring_wind_field_enabled", "ring_local_wind_vector", "ring_wind_mean_vector"]:
		report["wind_globals"][gname] = str(RenderingServer.global_shader_parameter_get(gname))
	await COMMON.settle(self, 1.0)
	var poses: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(String(args.get("poses", "res://tools/fixtures/tree_visibility_poses.json"))))
	poses["spawn_ground"] = {"name": "spawn_ground", "around_m": poses["save_around_m"], "across_m": poses["save_across_m"], "yaw_deg": 0.0, "pitch_deg": 0.0}
	var names := String(args.get("names", "forest_interior_eye,forest_edge_eye,plains_eye,spawn_ground")).split(",", false)
	var yaw_offsets := String(args.get("yaws", "0,90,180,270")).split(",", false)
	for name in names:
		if not poses.has(name):
			_log("pose %s missing" % name)
			continue
		var pose: Dictionary = poses[name]
		var entry := {"pose": pose, "views": {}}
		report["poses"][name] = entry
		for yo in yaw_offsets:
			var yaw := fposmod(float(pose["yaw_deg"]) + float(yo), 360.0)
			await _go(pose, yaw)
			var view := await _measure_view()
			view["yaw"] = yaw
			var sets: Array = view.get("_tree_sets", [])
			view.erase("_tree_sets")
			entry["views"]["yaw%+d" % int(yo)] = view
			if int(yo) == 0 and args.get("timing", "1") == "1" and not sets.is_empty():
				entry["timing"] = await _timing(sets)
			_write()
	_finish(0)


func _go(pose: Dictionary, yaw: float) -> void:
	var a := float(pose["around_m"])
	var z := float(pose["across_m"])
	var ground := float(surface.call("SampleHeight", a, z))
	explorer.call("TeleportToCanonicalMeters", a, ground + 1.7, z, yaw, float(pose.get("pitch_deg", 0.0)), true)
	await COMMON.settle(self, 0.3)
	var idle: bool = await COMMON.wait_builds_idle(self, game, 180.0)
	COMMON.apply_time(game, {"mode": "noon"})
	await COMMON.settle(self, 4.0)
	idle = idle and await COMMON.wait_builds_idle(self, game, 60.0)
	_log("pose %s yaw %.0f ground %.1f idle=%s" % [str(pose.get("name", "?")), yaw, ground, str(idle)])


# ------------------------------------------------------------------ depth capture and Hi-Z

func _capture_depth() -> Dictionary:
	var cam := root.get_camera_3d()
	var effect = DEPTH.new()
	effect.coordinate_scale = 1.0
	var comp := Compositor.new()
	var list: Array[CompositorEffect] = [effect]
	comp.compositor_effects = list
	var prev: Compositor = cam.compositor
	cam.compositor = comp
	for i in 4:
		await RenderingServer.frame_post_draw
	var tex: Texture2DRD = effect.get_depth_texture()
	var img: Image = tex.get_image() if tex != null else null
	cam.compositor = prev
	await RenderingServer.frame_post_draw
	if img == null:
		return {}
	var w := img.get_width()
	var h := img.get_height()
	var d := img.get_data().to_float32_array()
	# Sky (0) is infinitely far.
	for i in d.size():
		if d[i] <= 0.0:
			d[i] = INF
	# Orientation: the bottom rows see the ground 2-4 m away.
	var top := PackedFloat32Array()
	var bottom := PackedFloat32Array()
	for x in range(0, w, 8):
		top.append(d[2 * w + x])
		bottom.append(d[(h - 3) * w + x])
	top.sort()
	bottom.sort()
	var flip := top[top.size() / 2] < bottom[bottom.size() / 2]
	if flip:
		var f := PackedFloat32Array()
		f.resize(d.size())
		for y in h:
			for x in w:
				f[y * w + x] = d[(h - 1 - y) * w + x]
		d = f
	var levels: Array = [d]
	var sizes: Array = [Vector2i(w, h)]
	var lw := w
	var lh := h
	while lw > 1 or lh > 1:
		var nw := (lw + 1) / 2
		var nh := (lh + 1) / 2
		var src: PackedFloat32Array = levels[-1]
		var dst := PackedFloat32Array()
		dst.resize(nw * nh)
		for y in nh:
			var y0 := 2 * y
			var y1 := mini(2 * y + 1, lh - 1)
			for x in nw:
				var x0 := 2 * x
				var x1 := mini(2 * x + 1, lw - 1)
				dst[y * nw + x] = maxf(maxf(src[y0 * lw + x0], src[y0 * lw + x1]), maxf(src[y1 * lw + x0], src[y1 * lw + x1]))
		levels.append(dst)
		sizes.append(Vector2i(nw, nh))
		lw = nw
		lh = nh
	var sky := 0
	for i in range(0, d.size(), 16):
		if d[i] >= INF:
			sky += 1
	return {"levels": levels, "sizes": sizes, "w": w, "h": h, "flipped": flip, "sky_fraction": float(sky) * 16.0 / d.size(),
		"bottom_median_m": bottom[bottom.size() / 2] if not flip else top[top.size() / 2]}


## Max scene distance over the pixel rect [x0,x1]x[y0,y1], read at the pyramid level where the rect spans at most ~4 texels.
func _hiz_max(hz: Dictionary, x0: float, y0: float, x1: float, y1: float) -> float:
	var span := maxf(x1 - x0, y1 - y0)
	var level := 0
	if span > 4.0:
		level = mini(int(ceil(log(span / 4.0) / log(2.0))), hz["levels"].size() - 1)
	var size: Vector2i = hz["sizes"][level]
	var data: PackedFloat32Array = hz["levels"][level]
	var s := float(1 << level)
	var tx0 := clampi(int(floor(x0 / s)), 0, size.x - 1)
	var tx1 := clampi(int(floor(x1 / s)), 0, size.x - 1)
	var ty0 := clampi(int(floor(y0 / s)), 0, size.y - 1)
	var ty1 := clampi(int(floor(y1 / s)), 0, size.y - 1)
	var m := 0.0
	for ty in range(ty0, ty1 + 1):
		var row := ty * size.x
		for tx in range(tx0, tx1 + 1):
			m = maxf(m, data[row + tx])
	return m


# ------------------------------------------------------------------ classification

var _cam_inv: Transform3D
var _p00 := 1.0
var _p11 := 1.0
var _near := 0.05
var _w := 1.0
var _h := 1.0


func _setup_camera(w: int, h: int) -> void:
	var cam := root.get_camera_3d()
	_cam_inv = cam.global_transform.affine_inverse()
	var p: Projection = cam.get_camera_projection()
	_p00 = p.x.x
	_p11 = p.y.y
	_near = cam.near
	_w = float(w)
	_h = float(h)


## 0 visible (or touching the near plane), 1 outside the frustum, 2 occluded by full depth, 3 occluded by terrain-only depth too.
## Classified against a world sphere (centre, radius).
func _classify_sphere(c: Vector3, r: float, full: Dictionary, terrain: Dictionary) -> int:
	var v := _cam_inv * c
	var zn := -v.z - r
	if zn <= _near:
		return 0
	var zf := -v.z + r
	var xl := v.x - r
	var xr := v.x + r
	var yb := v.y - r
	var yt := v.y + r
	var nl := minf(xl / zn, xl / zf) * _p00
	var nr := maxf(xr / zn, xr / zf) * _p00
	var nb := minf(yb / zn, yb / zf) * _p11
	var nt := maxf(yt / zn, yt / zf) * _p11
	if nr < -1.0 or nl > 1.0 or nt < -1.0 or nb > 1.0:
		return 1
	var px0 := (clampf(nl, -1.0, 1.0) * 0.5 + 0.5) * _w
	var px1 := (clampf(nr, -1.0, 1.0) * 0.5 + 0.5) * _w
	var py0 := (0.5 - clampf(nt, -1.0, 1.0) * 0.5) * _h
	var py1 := (0.5 - clampf(nb, -1.0, 1.0) * 0.5) * _h
	var nearest := v.length() - r
	var eps := 0.1 + nearest * 0.002
	if _hiz_max(full, px0, py0, px1, py1) >= nearest - eps:
		return 0
	if _hiz_max(terrain, px0, py0, px1, py1) < nearest - eps:
		return 3
	return 2


func _classify_box(aabb: AABB, xf: Transform3D, full: Dictionary, terrain: Dictionary) -> int:
	var c := xf * aabb.get_center()
	var r := (xf.basis * (aabb.size * 0.5)).length()
	var sx := xf.basis.x.length()
	var sy := xf.basis.y.length()
	var sz := xf.basis.z.length()
	r = 0.5 * Vector3(aabb.size.x * sx, aabb.size.y * sy, aabb.size.z * sz).length()
	return _classify_sphere(c, r, full, terrain)


func _tris(mesh: Mesh) -> int:
	if mesh == null:
		return 0
	var t := 0
	if mesh is ArrayMesh:
		var am := mesh as ArrayMesh
		for s in am.get_surface_count():
			var n := am.surface_get_array_index_len(s)
			t += (n if n > 0 else am.surface_get_array_len(s)) / 3
	else:
		for s in mesh.get_surface_count():
			var arr := mesh.surface_get_arrays(s)
			var idx = arr[Mesh.ARRAY_INDEX]
			t += (idx.size() if idx != null and idx.size() > 0 else (arr[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()) / 3
	return t


func _all_nodes(node: Node) -> Array[Node]:
	var result: Array[Node] = [node]
	for child in node.get_children(true):
		result.append_array(_all_nodes(child))
	return result


func _camera_geometry(under: Node) -> Array:
	var out: Array = []
	var cam := root.get_camera_3d()
	var cpos := cam.global_position
	for n in _all_nodes(under):
		if n is GeometryInstance3D:
			var g := n as GeometryInstance3D
			if not g.is_visible_in_tree() or (g.layers & cam.cull_mask) == 0 or g.cast_shadow == GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY:
				continue
			if g.visibility_range_end > 0.0 or g.visibility_range_begin > 0.0:
				var d := cpos.distance_to(g.global_transform * g.get_aabb().get_center())
				if g.visibility_range_end > 0.0 and d > g.visibility_range_end + g.visibility_range_end_margin:
					continue
				if d < g.visibility_range_begin - g.visibility_range_begin_margin:
					continue
			out.append(g)
	return out


## Reads one multimesh: [count, stride, buffer].
func _mm_read(mm_rid: RID) -> Array:
	var cap := RenderingServer.multimesh_get_instance_count(mm_rid)
	if cap <= 0:
		return [0, 0, PackedFloat32Array()]
	var buf := RenderingServer.multimesh_get_buffer(mm_rid)
	var stride := buf.size() / cap
	var count := cap
	var cmd := RenderingServer.multimesh_get_command_buffer_rd_rid(mm_rid)
	if cmd.is_valid():
		var bytes := RenderingServer.get_rendering_device().buffer_get_data(cmd, 0, 8)
		count = mini(int(bytes.decode_u32(4)), cap)
	else:
		var vis := RenderingServer.multimesh_get_visible_instances(mm_rid)
		if vis >= 0:
			count = mini(vis, cap)
	return [count, stride, buf]


func _new_bucket() -> Dictionary:
	return {"renderers": 0, "renderers_culled": 0, "instances": 0, "tris": 0, "outside": 0, "outside_tris": 0, "occl_any": 0, "occl_any_tris": 0,
		"occl_terrain": 0, "occl_terrain_tris": 0, "visible": 0, "visible_tris": 0, "sampled_stride": 1}


func _bump(b: Dictionary, cls: int, tris: int, weight: int) -> void:
	b["instances"] += weight
	b["tris"] += tris * weight
	match cls:
		1:
			b["outside"] += weight
			b["outside_tris"] += tris * weight
		2:
			b["occl_any"] += weight
			b["occl_any_tris"] += tris * weight
		3:
			b["occl_any"] += weight
			b["occl_any_tris"] += tris * weight
			b["occl_terrain"] += weight
			b["occl_terrain_tris"] += tris * weight
		_:
			b["visible"] += weight
			b["visible_tris"] += tris * weight


func _hide_vegetation() -> void:
	for root_node in [trees, grass]:
		if root_node == null:
			continue
		for n in _all_nodes(root_node):
			if n is GeometryInstance3D:
				var g := n as GeometryInstance3D
				var was := g.layers
				g.layers = 0
				restore_stack.append(func(): if is_instance_valid(g): g.layers = was)


func _restore() -> void:
	while not restore_stack.is_empty():
		var undo: Callable = restore_stack.pop_back()
		undo.call()


func _measure_view() -> Dictionary:
	var t0 := Time.get_ticks_msec()
	var full := await _capture_depth()
	_hide_vegetation()
	await RenderingServer.frame_post_draw
	var terrain := await _capture_depth()
	_restore()
	await RenderingServer.frame_post_draw
	if full.is_empty() or terrain.is_empty():
		report["errors"].append("depth capture failed")
		return {}
	_setup_camera(full["w"], full["h"])
	var view := {"depth": {"w": full["w"], "h": full["h"], "flipped": full["flipped"], "sky_fraction_full": full["sky_fraction"],
		"sky_fraction_terrain": terrain["sky_fraction"], "bottom_median_m": full["bottom_median_m"]}, "trees": {}, "grass": {}, "other": {},
		"camera": {"p00": _p00, "p11": _p11, "near": _near}}
	# Trees, per LOD slot; per-instance class kept for the timing oracle.
	var tree_sets := []
	for g in _camera_geometry(trees):
		if not (g is MultiMeshInstance3D) or (g as MultiMeshInstance3D).multimesh == null:
			continue
		var mmi := g as MultiMeshInstance3D
		var nm := String(mmi.name)
		var lod := "L?"
		var lod_match := lod_regex.search(nm)   # "ImportedForestLod2_3" -> L2 (older builds named the renderers "..._L2")
		if lod_match != null:
			lod = "L" + lod_match.get_string(1)
		if not view["trees"].has(lod):
			view["trees"][lod] = _new_bucket()
		var b: Dictionary = view["trees"][lod]
		if not b.has("names"):
			b["names"] = []
		if b["names"].size() < 4:
			b["names"].append("%s (%s)" % [str(trees.get_path_to(mmi)), mmi.multimesh.mesh.resource_name if mmi.multimesh.mesh != null else ""])
		var mesh: Mesh = mmi.multimesh.mesh
		var tris := _tris(mesh)
		var rd := _mm_read(mmi.multimesh.get_rid())
		var count: int = rd[0]
		var stride: int = rd[1]
		var buf: PackedFloat32Array = rd[2]
		b["renderers"] += 1
		var gt := mmi.global_transform
		var raabb := gt * mmi.get_aabb().grow(mmi.extra_cull_margin)
		var rcls := _classify_box(raabb, Transform3D.IDENTITY, full, terrain)
		var classes := PackedByteArray()
		classes.resize(count)
		if rcls >= 2:
			b["renderers_aabb_occluded"] = int(b.get("renderers_aabb_occluded", 0)) + 1
			b["instances_in_aabb_occluded"] = int(b.get("instances_in_aabb_occluded", 0)) + count
		if rcls == 3:
			b["renderers_aabb_terrain_occluded"] = int(b.get("renderers_aabb_terrain_occluded", 0)) + 1
			b["instances_in_aabb_terrain_occluded"] = int(b.get("instances_in_aabb_terrain_occluded", 0)) + count
		if rcls == 1:
			b["renderers_culled"] += 1
			tree_sets.append({"node": mmi, "count": count, "stride": stride, "buf": buf, "classes": classes, "culled": true})
			continue
		var maabb := mesh.get_aabb()
		var mc := maabb.get_center()
		var half := maabb.size * 0.5
		var all_occl := true
		for i in count:
			var o := i * stride
			var bx := Vector3(buf[o], buf[o + 4], buf[o + 8])
			var by := Vector3(buf[o + 1], buf[o + 5], buf[o + 9])
			var bz := Vector3(buf[o + 2], buf[o + 6], buf[o + 10])
			var org := Vector3(buf[o + 3], buf[o + 7], buf[o + 11])
			var lc := org + bx * mc.x + by * mc.y + bz * mc.z
			var wc := gt * lc
			var gb := gt.basis
			var sx := (gb * bx).length()
			var sy := (gb * by).length()
			var sz := (gb * bz).length()
			var r := Vector3(half.x * sx, half.y * sy, half.z * sz).length() + wind_y * maabb.size.y * sy + 0.25
			var cls := _classify_sphere(wc, r, full, terrain)
			classes[i] = cls
			if cls == 0:
				all_occl = false
			_bump(b, cls, tris, 1)
		if all_occl and count > 0:
			b["renderers_all_hidden"] = int(b.get("renderers_all_hidden", 0)) + 1
		tree_sets.append({"node": mmi, "count": count, "stride": stride, "buf": buf, "classes": classes, "culled": false})
	view["_tree_sets"] = tree_sets
	# Grass: sampled.
	var grass_nodes := _camera_geometry(grass) if grass != null else []
	var total := 0
	var grass_reads := []
	for g in grass_nodes:
		if g is MultiMeshInstance3D and (g as MultiMeshInstance3D).multimesh != null:
			var rd := _mm_read((g as MultiMeshInstance3D).multimesh.get_rid())
			grass_reads.append([g, rd])
			total += int(rd[0])
	var cap := int(args.get("grass-cap", "250000"))
	var gstride := maxi(1, int(ceil(float(total) / float(cap))))
	var gb_all := _new_bucket()
	gb_all["sampled_stride"] = gstride
	for pair in grass_reads:
		var mmi := pair[0] as MultiMeshInstance3D
		var rd: Array = pair[1]
		var count: int = rd[0]
		var stride: int = rd[1]
		var buf: PackedFloat32Array = rd[2]
		gb_all["renderers"] += 1
		var gt := mmi.global_transform
		var raabb := gt * mmi.get_aabb().grow(mmi.extra_cull_margin)
		var tris := _tris(mmi.multimesh.mesh)
		if _classify_box(raabb, Transform3D.IDENTITY, full, terrain) == 1:
			gb_all["renderers_culled"] += 1
			continue
		var maabb := mmi.multimesh.mesh.get_aabb()
		var mc := maabb.get_center()
		var hd := (maabb.size * 0.5).length()
		for i in range(0, count, gstride):
			var o := i * stride
			var bx := Vector3(buf[o], buf[o + 4], buf[o + 8])
			var by := Vector3(buf[o + 1], buf[o + 5], buf[o + 9])
			var bz := Vector3(buf[o + 2], buf[o + 6], buf[o + 10])
			var org := Vector3(buf[o + 3], buf[o + 7], buf[o + 11])
			var wc := gt * (org + bx * mc.x + by * mc.y + bz * mc.z)
			var s := maxf(maxf(bx.length(), by.length()), bz.length())
			var r := 2.25 * maxf(hd * s, by.length() * maabb.size.y) + 0.1
			_bump(gb_all, _classify_sphere(wc, r, full, terrain), tris, gstride)
	view["grass"]["all"] = gb_all
	# Everything else in the game: proxy terrain, POIs, other meshes (per node, AABB test).
	for g in _camera_geometry(game):
		if trees != null and trees.is_ancestor_of(g):
			continue
		if grass != null and grass.is_ancestor_of(g):
			continue
		var cat := "other"
		var p: Node = g
		while p != null and p != game:
			var sc: Script = p.get_script()
			var path := sc.resource_path if sc != null else ""
			if "RingTerrainProxySector" in path:
				cat = "terrain_proxy"
				break
			if "/poi/" in path.to_lower() or String(p.name).to_lower().contains("poi"):
				cat = "poi"
				break
			p = p.get_parent()
		if cat == "other" and g is MeshInstance3D and String(g.name).begins_with("GlobalTerrain"):
			cat = "terrain_global"
		if not view["other"].has(cat):
			view["other"][cat] = _new_bucket()
		var b: Dictionary = view["other"][cat]
		b["renderers"] += 1
		if not b.has("names"):
			b["names"] = []
		if b["names"].size() < 8 and cat == "other":
			b["names"].append("%s %dk" % [str(game.get_path_to(g)), (_tris((g as MeshInstance3D).mesh) if g is MeshInstance3D else 0) / 1000])
		var tris := 0
		if g is MeshInstance3D:
			tris = _tris((g as MeshInstance3D).mesh)
		_bump(b, _classify_box(g.get_aabb(), g.global_transform, full, terrain), tris, 1)
	view["probe_ms"] = Time.get_ticks_msec() - t0
	var lines := []
	for lod in view["trees"]:
		var b: Dictionary = view["trees"][lod]
		lines.append("%s n=%d out=%d occ=%d occT=%d vis=%d tris=%dk rend=%d/%d" % [lod, b["instances"], b["outside"], b["occl_any"], b["occl_terrain"], b["visible"], b["tris"] / 1000, b["renderers"] - b["renderers_culled"], b["renderers"]])
	var gg: Dictionary = view["grass"]["all"]
	lines.append("grass n=%d out=%d occ=%d occT=%d (stride %d)" % [gg["instances"], gg["outside"], gg["occl_any"], gg["occl_terrain"], gstride])
	for cat in view["other"]:
		var b: Dictionary = view["other"][cat]
		lines.append("%s n=%d out=%d occ=%d occT=%d" % [cat, b["instances"], b["outside"], b["occl_any"], b["occl_terrain"]])
	_log("VIEW sky=%.2f flip=%s bottom=%.1fm %s (%d ms)" % [full["sky_fraction"], str(full["flipped"]), float(full["bottom_median_m"]), " | ".join(lines), view["probe_ms"]])
	return view


# ------------------------------------------------------------------ oracle timing

var kill_shaders := {}
var kill_materials := {}


func _kill_shader(sh: Shader) -> Shader:
	if kill_shaders.has(sh):
		return kill_shaders[sh]
	var code := sh.code
	var i := code.find("void vertex() {")
	if i < 0:
		kill_shaders[sh] = null
		return null
	var open := code.find("{", i)
	var depth := 0
	var j := open
	while j < code.length():
		var ch := code[j]
		if ch == "{":
			depth += 1
		elif ch == "}":
			depth -= 1
			if depth == 0:
				break
		j += 1
	var body := code.substr(open + 1, j - open - 1)
	var out := Shader.new()
	out.code = code.substr(0, open + 1) + "
	if (INSTANCE_CUSTOM.w > 50.0) { VERTEX = vec3(0.0); } else {" + body + "}
" + code.substr(j)
	kill_shaders[sh] = out
	return out


func _kill_material(m: Material) -> Material:
	if m == null or not (m is ShaderMaterial):
		return m
	if kill_materials.has(m):
		return kill_materials[m]
	var sm := m as ShaderMaterial
	var ks := _kill_shader(sm.shader) if sm.shader != null else null
	if ks == null:
		kill_materials[m] = m
		return m
	var d := sm.duplicate() as ShaderMaterial
	d.shader = ks
	kill_materials[m] = d
	return d


var kill_meshes := {}


## Surface override materials are ignored on multimesh instances: draw a mesh copy whose surfaces carry the kill materials.
func _kill_mesh(m: Mesh) -> Mesh:
	if kill_meshes.has(m):
		return kill_meshes[m]
	var d: Mesh = m.duplicate()
	for si in d.get_surface_count():
		var sm: Material = m.surface_get_material(si)
		if sm != null and d is ArrayMesh:
			(d as ArrayMesh).surface_set_material(si, _kill_material(sm))
	var killed := 0
	for si in d.get_surface_count():
		var km: Material = d.surface_get_material(si)
		if km is ShaderMaterial and (km as ShaderMaterial).shader != null and kill_shaders.values().has((km as ShaderMaterial).shader):
			killed += 1
	_log("kill mesh %s: %d/%d surfaces with kill shader" % [m.resource_path if m.resource_path != "" else str(m.get_instance_id()), killed, d.get_surface_count()])
	kill_meshes[m] = d
	return d


func _clear_copies() -> void:
	for c in copies:
		RenderingServer.free_rid(c[0])
		RenderingServer.free_rid(c[1])
	copies.clear()


## mode: "all" | "frustum" | "vis_any" | "vis_terrain" | "none"
func _install_copies(sets: Array, mode: String) -> int:
	_clear_copies()
	var kept := 0
	for s in sets:
		var mmi := s["node"] as MultiMeshInstance3D
		if not is_instance_valid(mmi):
			continue
		if mmi.cast_shadow == GeometryInstance3D.SHADOW_CASTING_SETTING_OFF:
			var was := mmi.layers
			mmi.layers = 0
			restore_stack.append(func(): if is_instance_valid(mmi): mmi.layers = was)
		else:
			var was_cs := mmi.cast_shadow
			mmi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY
			restore_stack.append(func(): if is_instance_valid(mmi): mmi.cast_shadow = was_cs)
		var kill := mode.begins_with("kill_")
		if mode == "none" or bool(s["culled"]) and mode != "all" and not kill:
			continue
		var count: int = s["count"]
		var stride: int = s["stride"]
		var buf: PackedFloat32Array = s["buf"]
		var cls: PackedByteArray = s["classes"]
		var out := PackedFloat32Array()
		out.resize(count * stride)
		var n := 0
		for i in count:
			var c := 0 if bool(s["culled"]) else cls[i]
			var m2 := mode.trim_prefix("kill_")
			var keep := m2 == "all" or (m2 == "frustum" and c != 1) or (m2 == "vis_any" and c == 0) or (m2 == "vis_terrain" and (c == 0 or c == 2))
			if bool(s["culled"]) and kill:
				keep = false
			if not keep and not kill:
				continue
			for k in stride:
				out[n * stride + k] = buf[i * stride + k]
			if not keep and stride >= 16:
				out[n * stride + 15] = 100.0
			n += 1
		if n == 0:
			continue
		out.resize(n * stride)
		kept += n
		var mm := RenderingServer.multimesh_create()
		RenderingServer.multimesh_allocate_data(mm, n, RenderingServer.MULTIMESH_TRANSFORM_3D, stride == 20, stride >= 16)
		var draw_mesh: Mesh = mmi.multimesh.mesh
		if kill and mmi.material_override == null:
			draw_mesh = _kill_mesh(draw_mesh)
		RenderingServer.multimesh_set_mesh(mm, draw_mesh.get_rid())
		RenderingServer.multimesh_set_buffer(mm, out)
		RenderingServer.multimesh_set_custom_aabb(mm, mmi.multimesh.custom_aabb if mmi.multimesh.custom_aabb.size != Vector3.ZERO else mmi.get_aabb())
		var inst := RenderingServer.instance_create2(mm, mmi.get_world_3d().scenario)
		RenderingServer.instance_set_transform(inst, mmi.global_transform)
		RenderingServer.instance_set_layer_mask(inst, 1)
		RenderingServer.instance_geometry_set_cast_shadows_setting(inst, RenderingServer.SHADOW_CASTING_SETTING_OFF)
		RenderingServer.instance_set_extra_visibility_margin(inst, mmi.extra_cull_margin)
		if mmi.material_override != null:
			var mo: Material = _kill_material(mmi.material_override) if kill else mmi.material_override
			RenderingServer.instance_geometry_set_material_override(inst, mo.get_rid())

		for pinfo in RenderingServer.instance_geometry_get_shader_parameter_list(mmi.get_instance()):
			var pn := StringName(pinfo["name"])
			RenderingServer.instance_geometry_set_shader_parameter(inst, pn, RenderingServer.instance_geometry_get_shader_parameter(mmi.get_instance(), pn))
		copies.append([inst, mm])
	return kept


func _window(tag: String, seconds: float) -> Dictionary:
	var subs: Array = []
	for n in _all_nodes(root):
		if n is SubViewport:
			subs.append(n)
			RenderingServer.viewport_set_measure_render_time(n.get_viewport_rid(), true)
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	var wall := PackedFloat64Array()
	var gpu := PackedFloat64Array()
	var gpu_root := PackedFloat64Array()
	var prims := PackedFloat64Array()
	var opaque := PackedFloat64Array()
	var rdev := RenderingServer.get_rendering_device()
	await RenderingServer.frame_post_draw
	var start := Time.get_ticks_usec()
	var last := start
	while (Time.get_ticks_usec() - start) * 0.000001 < seconds:
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		wall.append((now - last) * 0.001)
		last = now
		var g := RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())
		gpu_root.append(g)
		for vp in subs:
			if is_instance_valid(vp):
				g += RenderingServer.viewport_get_measured_render_time_gpu(vp.get_viewport_rid())
		gpu.append(g)
		prims.append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		if not stamp_effects.is_empty():
			var pre := []
			var post := []
			var allnames := []
			for k in rdev.get_captured_timestamps_count():
				var nm := rdev.get_captured_timestamp_name(k)
				if not names_logged:
					allnames.append(nm)
				if nm == "lp_pre_opaque":
					pre.append(rdev.get_captured_timestamp_gpu_time(k))
				elif nm == "lp_post_opaque":
					post.append(rdev.get_captured_timestamp_gpu_time(k))
			if not names_logged and not allnames.is_empty():
				names_logged = true
				_log("timestamp names: %s" % ", ".join(allnames))
			var best := -1.0
			for k in mini(pre.size(), post.size()):
				best = maxf(best, float(post[k] - pre[k]) / 1.0e6)
			if best >= 0.0:
				opaque.append(best)
	var r := {"tag": tag, "frames": wall.size(), "wall_p50": _p(wall, 0.5), "wall_p95": _p(wall, 0.95), "gpu_p50": _p(gpu, 0.5), "gpu_root_p50": _p(gpu_root, 0.5),
		"prims_p50": _p(prims, 0.5), "opaque_p50": _p(opaque, 0.5), "opaque_n": opaque.size()}
	_log("WIN %-34s wall p50 %6.2f p95 %6.2f gpu %6.2f root %6.2f opaque %6.2f prims %.2fM" % [tag, r["wall_p50"], r["wall_p95"], r["gpu_p50"], r["gpu_root_p50"], r["opaque_p50"], float(r["prims_p50"]) / 1e6])
	return r


func _p(v: PackedFloat64Array, q: float) -> float:
	if v.is_empty():
		return NAN
	var s := v.duplicate()
	s.sort()
	return s[mini(s.size() - 1, int(s.size() * q))]


func _stamps_on() -> void:
	var we := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if we == null or we.compositor == null:
		return
	var effects := we.compositor.compositor_effects
	for t in [CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_OPAQUE, CompositorEffect.EFFECT_CALLBACK_TYPE_POST_OPAQUE]:
		var e: CompositorEffect = STAMP.make(t)
		stamp_effects.append(e)
		effects.append(e)
	we.compositor.compositor_effects = effects
	stamp_env = we.environment


func _stamps_off() -> void:
	var we := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if we != null and we.compositor != null:
		var effects := we.compositor.compositor_effects
		for e in stamp_effects:
			effects.erase(e)
		we.compositor.compositor_effects = effects
	stamp_effects.clear()


func _timing(sets: Array) -> Dictionary:
	_stamps_on()
	var secs := float(args.get("win", "8"))
	var result := {"windows": [], "kept": {}}
	await COMMON.settle(self, 1.0)
	result["windows"].append(await _window("base_0", secs))
	var i := 0
	var mode_list := String(args.get("modes", "all,frustum,vis_terrain,vis_any,none")).split(",", false)
	if "kill_all" in mode_list or "kill_frustum" in mode_list or "kill_vis_any" in mode_list:
		_install_copies(sets, "kill_vis_any")
		await COMMON.settle(self, 8.0)
		_clear_copies()
		_restore()
		await COMMON.settle(self, 2.0)
	for mode in mode_list:
		var kept := _install_copies(sets, mode)
		result["kept"][mode] = kept
		await COMMON.settle(self, 1.5)
		result["windows"].append(await _window("copy_%s (%d)" % [mode, kept], secs))
		_clear_copies()
		_restore()
		await COMMON.settle(self, 1.5)
		i += 1
		result["windows"].append(await _window("base_%d" % i, secs))
	_stamps_off()
	return result
