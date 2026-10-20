extends SceneTree
# Checks the built Weather Lung scenes against the kit manifest. Run build_weather_lung.gd first.
#
#   godot --headless --path . --script res://tools/poi/verify_weather_lung.gd
#
# Prints node/tile counts and load + instantiate times, then WEATHER_LUNG_VERIFY=PASS|FAIL (exit code 1 on FAIL).
# The kit docs/INDEPENDENT_QA.md predates the current export, so these checks are the only independent validation.

const PoiMaterialUtil := preload("res://tools/poi/poi_material_util.gd")
const OUT := "res://assets/poi/weather_lung"
const MANIFEST := OUT + "/source/weather_lung_manifest.json"
const DETAIL := OUT + "/WeatherLungDetail.tscn"
const POI := OUT + "/WeatherLungPOI.tscn"
const COLLISION_DIR := OUT + "/collision"

const EXPECT_STRUCTURE := 73688
const EXPECT_DRESSING := 33164
const EXPECT_MOVING := 8188
const EXPECT_PROXY := 19212
const EXPECT_PIVOTS := 59
const EXPECT_PIVOT_KINDS := {"vane": 5, "ramcyl": 5, "ramrod": 5, "rotor": 2, "ring": 2, "gill": 16, "heliostat": 24}
const EXPECT_NOZZLES := 12
const DRESSING_END := 2000.0
const PROXY_BEGIN := 28000.0
const ORIGIN_TOLERANCE := 1e-3
const MAX_TILES := 450
const MAX_DETAIL_INSTANTIATE_MS := 15.0   # warm (median of 3); the cold first instantiate only escalates above MAX_DETAIL_COLD_MS
const MAX_DETAIL_COLD_MS := 40.0

var _fails := 0
var _notes := []

func _check(ok: bool, msg: String) -> void:
	print("  [%s] %s" % ["ok" if ok else "FAIL", msg])
	if not ok:
		_fails += 1

func _init() -> void:
	if "rig" in OS.get_cmdline_user_args():
		_rig()
		return
	var mf = JSON.parse_string(FileAccess.get_file_as_string(MANIFEST))
	if mf == null:
		print("cannot read " + MANIFEST)
		print("WEATHER_LUNG_VERIFY=FAIL")
		quit(1)
		return

	var t0 := Time.get_ticks_usec()
	var detail_ps := load(DETAIL) as PackedScene
	var t1 := Time.get_ticks_usec()
	var detail := detail_ps.instantiate() as Node3D
	var t2 := Time.get_ticks_usec()
	var poi_ps := load(POI) as PackedScene
	var t3 := Time.get_ticks_usec()
	var poi := poi_ps.instantiate() as Node3D
	var t4 := Time.get_ticks_usec()
	var detail_load_ms := (t1 - t0) / 1000.0
	var detail_inst_ms := (t2 - t1) / 1000.0
	var poi_load_ms := (t3 - t2) / 1000.0
	var poi_inst_ms := (t4 - t3) / 1000.0

	print("-- sectors")
	var sectors := detail.get_node("Sectors")
	_check(sectors.get_child_count() == 12, "12 sector roots (found %d)" % sectors.get_child_count())
	var worst := 0.0
	for s in mf.sectors:
		var n := sectors.get_node_or_null("WeatherLungSector_%02d" % int(s.id)) as Node3D
		if n == null:
			_check(false, "sector %d missing" % int(s.id))
			continue
		var o := Vector3(s.origin_godot_m[0], s.origin_godot_m[1], s.origin_godot_m[2])
		worst = maxf(worst, (n.position - o).length())
		if n.get_node_or_null("Structure") == null or n.get_node_or_null("Dressing") == null:
			_check(false, "sector %d lacks Structure or Dressing" % int(s.id))
	_check(worst <= ORIGIN_TOLERANCE, "sector origins match the manifest, worst error %.6f m" % worst)
	var arch := detail.get_node("Arch") as Node3D
	var ao := Vector3(mf.vane_module.origin_godot_m[0], mf.vane_module.origin_godot_m[1], mf.vane_module.origin_godot_m[2])
	_check((arch.position - ao).length() <= ORIGIN_TOLERANCE, "Arch at %s (manifest %s)" % [arch.position, ao])

	print("-- triangle totals")
	var structure := 0
	var dressing := 0
	var moving := 0
	var ram_dressing := 0
	var tiles := 0
	var dress_bad := 0
	var struct_move_bad := 0
	var stack: Array = [detail]
	while stack.size() > 0:
		var n: Node = stack.pop_back()
		for c in n.get_children():
			stack.append(c)
		if n is MeshInstance3D:
			var mi := n as MeshInstance3D
			var cls := _class_of(mi, detail)
			var t := _mesh_tris(mi.mesh)
			match cls:
				"structure":
					structure += t
					if mi.visibility_range_end != 0.0:
						struct_move_bad += 1
				"dressing":
					dressing += t
					tiles += 1
					if not is_equal_approx(mi.visibility_range_end, DRESSING_END):
						dress_bad += 1
				"moving":
					if mi.visibility_range_end != 0.0:
						struct_move_bad += 1
					# per-pivot metadata splits moving from ram-dressing parts that were merged into the same mesh
					var pv := mi.get_parent()
					moving += int(pv.get_meta("wl_tris_moving"))
					ram_dressing += int(pv.get_meta("wl_tris_dressing"))
					_check_equal_quiet(_mesh_tris(mi.mesh), int(pv.get_meta("wl_tris_moving")) + int(pv.get_meta("wl_tris_dressing")), "pivot mesh tris " + String(pv.name))
	_check(structure == EXPECT_STRUCTURE, "structure tris %d (expected %d)" % [structure, EXPECT_STRUCTURE])
	_check(dressing + ram_dressing == EXPECT_DRESSING, "dressing tris %d tiled + %d ram parts inside Moving = %d (expected %d)" % [dressing, ram_dressing, dressing + ram_dressing, EXPECT_DRESSING])
	_check(moving == EXPECT_MOVING, "moving tris %d (expected %d)" % [moving, EXPECT_MOVING])
	_check(dress_bad == 0, "every dressing tile has visibility_range_end %d (%d of %d wrong)" % [int(DRESSING_END), dress_bad, tiles])
	_check(struct_move_bad == 0, "every Structure/Moving instance has visibility_range_end 0 (%d wrong)" % struct_move_bad)

	print("-- moving rig")
	var moving_node := arch.get_node("Moving")
	var kinds := {}
	var pivots := 0
	stack = [moving_node]
	while stack.size() > 0:
		var n: Node = stack.pop_back()
		for c in n.get_children():
			stack.append(c)
		if n == moving_node or not (n is Node3D) or n is MeshInstance3D:
			continue
		pivots += 1
		var kind := String(n.get_meta("wl_kind", ""))
		kinds[kind] = kinds.get(kind, 0) + 1
		var need := ["wl_kind", "wl_index", "wl_axis", "wl_tris_moving", "wl_tris_dressing"]
		if kind == "ramrod":
			need.append("wl_rest_x")
		else:
			need.append("wl_rest_angle")
		if kind == "heliostat":
			need.append_array(["wl_track_angle", "wl_row", "wl_col"])
		if kind == "ramcyl":
			need.append_array(["wl_pin", "wl_base", "wl_l0", "wl_vane_pos"])
		for k in need:
			if not n.has_meta(k):
				_check(false, "%s missing meta %s" % [n.name, k])
		var ax: Vector3 = n.get_meta("wl_axis", Vector3.ZERO)
		if absf(ax.length() - 1.0) > 1e-4:
			_check(false, "%s: wl_axis not unit (%s)" % [n.name, ax])
	_check(pivots == EXPECT_PIVOTS, "%d pivots with metadata (expected %d)" % [pivots, EXPECT_PIVOTS])
	_check(kinds == EXPECT_PIVOT_KINDS, "pivot kinds %s" % [kinds])

	print("-- materials")
	var by_name := {}   # name -> {instance id: true}
	var water_nodes := []
	for root in [detail, poi]:
		stack = [root]
		while stack.size() > 0:
			var n: Node = stack.pop_back()
			for c in n.get_children():
				stack.append(c)
			if "water" in String(n.name).to_lower() and not (n is Marker3D):
				water_nodes.append(String(n.name))
			if n is MeshInstance3D:
				var mesh := (n as MeshInstance3D).mesh
				if "water" in mesh.resource_name.to_lower():
					water_nodes.append("mesh " + mesh.resource_name)
				for s in mesh.get_surface_count():
					var m := mesh.surface_get_material(s)
					if not by_name.has(m.resource_name):
						by_name[m.resource_name] = {}
					by_name[m.resource_name][m.get_instance_id()] = m.resource_path
	var multi := []
	var heat := 0
	var nozzle := 0
	for nm in by_name:
		if by_name[nm].size() != 1:
			multi.append(nm)
		if nm == "Heat_Exchanger_Glow":
			heat += by_name[nm].size()
		elif String(nm).begins_with("Nozzle_Glow_"):
			nozzle += by_name[nm].size()
		for p in by_name[nm].values():
			if not String(p).begins_with(OUT + "/built/materials/"):
				_check(false, "material %s not a shared built .tres (%s)" % [nm, p])
	_check(multi.is_empty(), "one material instance per name across %d names (violations: %s)" % [by_name.size(), multi])
	_check(heat == 1, "Heat_Exchanger_Glow x%d" % heat)
	_check(nozzle == EXPECT_NOZZLES, "Nozzle_Glow_XX x%d" % nozzle)
	for nm in by_name:
		if String(nm) == "Heat_Exchanger_Glow" or String(nm).begins_with("Nozzle_Glow_"):
			var mat := load(by_name[nm].values()[0]) as ShaderMaterial
			_check(mat != null and PoiMaterialUtil.is_poi_material(mat), "%s is a POI ShaderMaterial" % nm)
			if mat == null:
				continue
			var emission_color: Color = PoiMaterialUtil.param(mat, "emission_color")
			_check(float(PoiMaterialUtil.param(mat, "emission_energy")) == 0.0 and emission_color != Color(0, 0, 0, 1), "%s emission colour set, energy 0" % nm)
	_check(water_nodes.is_empty(), "no node/mesh matching *Water* %s" % [water_nodes])

	print("-- FarProxy")
	var proxy := poi.get_node("FarProxy") as MeshInstance3D
	_check(is_equal_approx(proxy.visibility_range_begin, PROXY_BEGIN), "FarProxy visibility_range_begin %s" % proxy.visibility_range_begin)
	_check(proxy.cast_shadow == GeometryInstance3D.SHADOW_CASTING_SETTING_OFF, "FarProxy casts no shadow")
	_check(_mesh_tris(proxy.mesh) == EXPECT_PROXY, "FarProxy tris %d (expected %d)" % [_mesh_tris(proxy.mesh), EXPECT_PROXY])
	for k in ["Datum", "BasinCentre", "Waterline", "PlumeNozzle", "PlumeCrest", "DamCrest", "IntakeOverlook"]:
		_check(poi.get_node_or_null("Markers/" + k) != null, "marker " + k)
	_check(poi.get_node_or_null("DetailMount") != null, "DetailMount exists")

	print("-- collision")
	var faces := 0
	var bodies := 0
	for body in detail.get_node("Collision").get_children():
		bodies += 1
		_check((body as StaticBody3D).process_mode == Node.PROCESS_MODE_DISABLED, "%s starts disabled" % body.name)
		for cs in body.get_children():
			faces += ((cs as CollisionShape3D).shape as ConcavePolygonShape3D).get_faces().size() / 3
	_check(bodies >= 16 and bodies <= 20, "collision bodies: 12 sectors + Arch parts (found %d, expected 16 to 20)" % bodies)
	_check(faces == EXPECT_STRUCTURE, "collision faces %d (expected %d)" % [faces, EXPECT_STRUCTURE])

	print("-- counts and timing")
	var dn := _count(detail)
	var pn := _count(poi)
	print("  Detail: nodes=%d dressing_tiles=%d load_ms=%.2f instantiate_ms=%.2f" % [dn, tiles, detail_load_ms, detail_inst_ms])
	print("  POI:    nodes=%d load_ms=%.2f instantiate_ms=%.2f" % [pn, poi_load_ms, poi_inst_ms])
	if tiles > MAX_TILES:
		print("  ESCALATE: dressing tile count %d > %d" % [tiles, MAX_TILES])
	if detail_inst_ms > MAX_DETAIL_COLD_MS:
		print("  ESCALATE: Detail cold instantiate %.2f ms > %.0f ms" % [detail_inst_ms, MAX_DETAIL_COLD_MS])

	# Warm re-instantiate (resources cached) to separate first-touch costs from steady state.
	var warm: Array = []
	for r in 3:
		var w0 := Time.get_ticks_usec()
		var w := detail_ps.instantiate()
		warm.append((Time.get_ticks_usec() - w0) / 1000.0)
		w.free()
	print("  Detail warm instantiate_ms: %s (cold %.2f ms, informational)" % [warm, detail_inst_ms])
	warm.sort()
	if warm[1] > MAX_DETAIL_INSTANTIATE_MS:
		print("  ESCALATE: Detail warm instantiate (median) %.2f ms > %.0f ms" % [warm[1], MAX_DETAIL_INSTANTIATE_MS])

	detail.free()
	poi.free()
	print("WEATHER_LUNG_VERIFY=" + ("PASS" if _fails == 0 else "FAIL"))
	quit(0 if _fails == 0 else 1)

func _check_equal_quiet(a: int, b: int, msg: String) -> void:
	if a != b:
		_check(false, "%s: %d != %d" % [msg, a, b])

func _count(n: Node) -> int:
	var c := 1
	for k in n.get_children():
		c += _count(k)
	return c

# structure | dressing | moving, from the position in the scene tree.
func _class_of(mi: Node, detail: Node) -> String:
	var cur: Node = mi
	while cur != null and cur != detail:
		if cur.name == "Dressing":
			return "dressing"
		if cur.name == "Moving":
			return "moving"
		cur = cur.get_parent()
	return "structure"

func _mesh_tris(m: Mesh) -> int:
	var t := 0
	for i in m.get_surface_count():
		t += (m.surface_get_arrays(i)[Mesh.ARRAY_INDEX] as PackedInt32Array).size() / 3
	return t

# `-- rig`: poses the C# WeatherLungRig and compares it with the kit's baked animation (tools/WeatherLungRigCheck.cs).
# GLTFDocument is not bound to C#, so the animated GLB is read here, straight from the ignored prototypes/ path.
const ANIMATED_GLB := "res://prototypes/Weather_Lung_Central_Station_Kit/exports/modules/Weather_Lung_Intake_Vanes_Animated.glb"

func _rig() -> void:
	var doc := GLTFDocument.new()
	var st := GLTFState.new()
	var err := doc.append_from_file(ProjectSettings.globalize_path(ANIMATED_GLB), st)
	if err != OK:
		print("WEATHER_LUNG_RIG=FAIL GLTFDocument cannot read the animated GLB (err %d)" % err)
		quit(1)
		return
	var anim_root := doc.generate_scene(st)
	var check = load("res://tools/WeatherLungRigCheck.cs").new()
	var code: int = check.Run(DETAIL, anim_root)
	anim_root.free()
	quit(code)
