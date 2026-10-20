extends SceneTree
# Builds the Weather Lung runtime scenes from the imported kit GLBs (assets/poi/weather_lung/source).
#
#   godot --headless --path . --script res://tools/poi/build_weather_lung.gd [-- all|detail|proxy|collision|test]
#   all        detail + proxy.
#   detail     shared materials, sector scenes, dressing tiles, Arch (chunk statics + Moving rig), collision shapes,
#              WeatherLungDetail.tscn.
#   proxy      FarProxy mesh, Markers and WeatherLungPOI.tscn. Re-runnable: attaches
#              res://scripts/poi/WeatherLung/WeatherLungController.cs to the POI root once that file exists.
#   collision  only rewrites collision/*.res (the Detail scene references them by path).
#   test       scenes/poi/WeatherLungTest.tscn + WeatherLungTestGround.res (standalone look-dev / perf scene, scripts/poi/weather_lung_test.gd).
#
# Writes (all under res://assets/poi/weather_lung/):
#   built/materials/*.tres, built/meshes/WL_*.res, built/sectors/WeatherLungSector_XX.tscn,
#   collision/*.res, WeatherLungDetail.tscn, WeatherLungPOI.tscn
#
# Composition: 12 x (Structure, Dressing) sector GLBs at the manifest origins, the five
# chunk static batches (<Chunk>_Structure / _Dressing nodes only) under Arch, and ALL moving parts from the vane module
# GLB (the chunk GLBs duplicate them). Coordinates: Blender (x, y, z) -> Godot (x, z, -y); GLB meshes are already Godot.
# Safe to re-run; outputs are overwritten.

const PoiMaterialUtil := preload("res://tools/poi/poi_material_util.gd")
const SRC := "res://assets/poi/weather_lung/source"
const OUT := "res://assets/poi/weather_lung"
const BUILT := OUT + "/built"
const COLL := OUT + "/collision"
const MANIFEST := SRC + "/weather_lung_manifest.json"
const MODULE_GLB := SRC + "/modules/Weather_Lung_Intake_Vanes.glb"
const PROXY_GLB := SRC + "/Weather_Lung_Far_Proxy.glb"
const CONTROLLER_SCRIPT := "res://scripts/poi/WeatherLung/WeatherLungController.cs"
const CHUNKS := ["Gills", "Hood_Shell", "Nozzle_Guide", "Throat_Compressor", "Vane_Bank"]

const NORMAL_MERGE_ANGLE := deg_to_rad(60.0)
const NORMAL_SPLIT_ANGLE := deg_to_rad(25.0)

# Dressing tiles: POI-local XZ cells. Godot measures visibility range to the instance AABB centre, so a sector-sized
# dressing mesh would never satisfy a 2 km range near its edges.
const DRESSING_TILE_M := 1000.0
const DRESSING_RANGE_END := 2000.0
const DRESSING_RANGE_MARGIN := 250.0
# Arch dressing tiles per separately instantiated piece (the Detail attach budget works on whole pieces).
const DRESSING_PIECE_TILES := 25
# Arch collision triangles per body / shape (see _build_collision_shapes).
const ARCH_COLLISION_FACES := 5000

# FarProxy: Detail takes this node as visibility_parent (set by the controller), so Detail shows only while the proxy
# is hidden (inside PROXY_BEGIN). Both are runtime-tunable later (S6).
const PROXY_BEGIN := 28000.0
const PROXY_BEGIN_MARGIN := 2000.0
const PROXY_END := 700000.0

const HEAT_COLOR := Color(1.0, 0.42, 0.12)
const NOZZLE_COLOR := Color(0.05, 0.75, 0.66)

# Guide ring axis: arch-plane normal normalize(4475, 0, -2300) in Blender -> (x, z, -y) in Godot.
const RING_AXIS := Vector3(4475.0, -2300.0, 0.0)
# Vane hinge: Blender +Y -> Godot -Z. RamCyl uses the same axis.
const VANE_AXIS := Vector3(0.0, 0.0, -1.0)

const EXPECTED_PIVOTS := {"vane": 5, "ramcyl": 5, "ramrod": 5, "rotor": 2, "ring": 2, "gill": 16, "heliostat": 24}

# Dam crest: lip ellipse (9900, 5650) at t = 322.5 deg (dam spans 310..340 deg), POI-local Godot (x, z).
const DAM_T_DEG := 322.5
const DAM_SEARCH_M := 300.0
const BASIN_A := 9900.0
const BASIN_B := 5650.0

var _manifest: Dictionary
var _scene_cache := {}     # res path -> instantiated root (kept until the end of the run)
var _surf_cache := {}      # "path|filter" -> surfaces
var _mat_src := {}         # material name -> BaseMaterial3D (first source instance)
var _mat_res := {}         # material name -> shared .tres
var _mat_conflicts := []
var _arch_origin := Vector3.ZERO
var _failed := false

func _init() -> void:
	var which := "all"
	var args := OS.get_cmdline_user_args()
	if args.size() > 0:
		which = args[0]
	if not which in ["all", "detail", "proxy", "collision", "test"]:
		_fail("unknown mode '%s' (all|detail|proxy|collision|test)" % which)
		quit(1)
		return
	var t0 := Time.get_ticks_usec()
	var mf := FileAccess.get_file_as_string(MANIFEST)
	_manifest = JSON.parse_string(mf)
	if _manifest == null:
		_fail("cannot parse " + MANIFEST)
		quit(1)
		return
	_arch_origin = _v3(_manifest.vane_module.origin_godot_m)
	for c in CHUNKS:
		var o := _v3(_manifest.arch_chunks[c].origin_godot_m)
		if not o.is_equal_approx(_arch_origin):
			_fail("chunk %s origin %s differs from the vane module origin %s" % [c, o, _arch_origin])
	for d in [BUILT + "/materials", BUILT + "/meshes", BUILT + "/sectors", COLL]:
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(d))

	if which == "test":
		_build_test()
	else:
		_scan_materials()
		if which == "all" or which == "detail":
			_build_detail()
		elif which == "collision":
			_build_collision_shapes()
		if which == "all" or which == "proxy":
			_build_poi()
	print("[%s] done in %.1f ms%s" % [which, (Time.get_ticks_usec() - t0) / 1000.0, " WITH ERRORS" if _failed else ""])
	for k in _scene_cache:
		(_scene_cache[k] as Node).free()
	quit(1 if _failed else 0)

# --------------------------------------------------------------------------------------------
# helpers

func _fail(msg: String) -> void:
	_failed = true
	push_error("build_weather_lung: " + msg)

func _v3(a: Array) -> Vector3:
	return Vector3(a[0], a[1], a[2])

func _sanitize(s: String) -> String:
	var out := ""
	for ch in s:
		out += ch if (ch.to_lower() != ch.to_upper() or ch.is_valid_int()) else "_"
	while "__" in out:
		out = out.replace("__", "_")
	return out.strip_edges().trim_prefix("_").trim_suffix("_")

func _idx(i: int) -> String:
	return ("n%d" % -i) if i < 0 else str(i)

func _scene(path: String) -> Node:
	if not _scene_cache.has(path):
		var ps := load(path) as PackedScene
		if ps == null:
			_fail("cannot load " + path)
			return Node3D.new()
		_scene_cache[path] = ps.instantiate()
	return _scene_cache[path]

func _local_to_root(n: Node3D, root: Node) -> Transform3D:
	var xf := Transform3D.IDENTITY
	var cur: Node = n
	while cur != null and cur != root:
		if cur is Node3D:
			xf = (cur as Node3D).transform * xf
		cur = cur.get_parent()
	return xf

func _all_sources() -> Array:
	var list := []
	for s in _manifest.sectors:
		list.append(SRC + "/" + s.structure_file)
		list.append(SRC + "/" + s.dressing_file)
	for c in CHUNKS:
		list.append(SRC + "/" + _manifest.arch_chunks[c].file)
	list.append(MODULE_GLB)
	list.append(PROXY_GLB)
	return list

# Reads the JSON chunk of a .glb: node name -> extras dictionary (independent of importer metadata support).
func _glb_node_extras(res_path: String) -> Dictionary:
	var f := FileAccess.open(res_path, FileAccess.READ)
	if f == null:
		_fail("cannot open " + res_path)
		return {}
	f.get_32() # magic
	f.get_32() # version
	f.get_32() # total length
	var chunk_len := f.get_32()
	f.get_32() # chunk type (JSON)
	var json = JSON.parse_string(f.get_buffer(chunk_len).get_string_from_utf8())
	var out := {}
	if json == null or not json.has("nodes"):
		_fail("no JSON chunk in " + res_path)
		return out
	for nd in json.nodes:
		out[String(nd.get("name", ""))] = nd.get("extras", {})
	return out

# --------------------------------------------------------------------------------------------
# materials: one shared .tres per material name

func _mat_key(m: BaseMaterial3D) -> String:
	return "%s|%.4f|%.4f|%s|%s|%.3f|%d|%d" % [m.albedo_color, m.metallic, m.roughness, m.emission_enabled, m.emission, m.emission_energy_multiplier, m.transparency, m.cull_mode]

func _scan_materials() -> void:
	var seen := {}
	for p in _all_sources():
		var root := _scene(p)
		var stack: Array = [root]
		while stack.size() > 0:
			var n: Node = stack.pop_back()
			for c in n.get_children():
				stack.append(c)
			if not (n is MeshInstance3D):
				continue
			var mesh := (n as MeshInstance3D).mesh
			for s in mesh.get_surface_count():
				var m := mesh.surface_get_material(s) as BaseMaterial3D
				if m == null:
					_fail("surface without BaseMaterial3D in %s/%s" % [p, n.name])
					continue
				var nm := m.resource_name
				if not _mat_src.has(nm):
					_mat_src[nm] = m
					seen[nm] = 0
				elif _mat_key(_mat_src[nm]) != _mat_key(m):
					_mat_conflicts.append("%s (%s)" % [nm, p.get_file()])
				seen[nm] += 1
	var names := _mat_src.keys()
	names.sort()
	for nm in names:
		var m := (_mat_src[nm] as BaseMaterial3D).duplicate() as BaseMaterial3D
		m.resource_name = nm
		if nm == "Heat_Exchanger_Glow":
			m.emission_enabled = true
			m.emission = HEAT_COLOR
			m.emission_energy_multiplier = 0.0
		elif String(nm).begins_with("Nozzle_Glow_"):
			m.emission_enabled = true
			m.emission = NOZZLE_COLOR
			m.emission_energy_multiplier = 0.0
		var path := "%s/materials/%s.tres" % [BUILT, _sanitize(nm)]
		# POI ShaderMaterial (ring haze per fragment); see tools/poi/poi_material_util.gd.
		var poi_mat := PoiMaterialUtil.to_shader_material(m)
		if poi_mat == null:
			_fail("cannot convert material " + String(nm))
			continue
		ResourceSaver.save(poi_mat, path)
		PoiMaterialUtil.strip_runtime_params(path)
		_mat_res[nm] = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE)
	print("materials: %d unique names, %d embedded source copies merged, conflicts=%d" % [names.size(), _total_seen(seen), _mat_conflicts.size()])
	for c in _mat_conflicts:
		_fail("material copies differ: " + c)

func _total_seen(seen: Dictionary) -> int:
	var t := 0
	for k in seen:
		t += seen[k]
	return t

func _mat(nm: String) -> Material:
	return _mat_res[nm]

# --------------------------------------------------------------------------------------------
# geometry extraction / meshes

# Surfaces of one MeshInstance3D baked by `xf`: [{mat, v, n, i}] (indices re-wound when xf mirrors).
func _surfaces_of(mi: MeshInstance3D, xf: Transform3D) -> Array:
	var out := []
	var basis_n := xf.basis.inverse().transposed()
	var flip := xf.basis.determinant() < 0.0
	var mesh := mi.mesh
	for s in mesh.get_surface_count():
		var arrays := mesh.surface_get_arrays(s)
		var verts: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
		var norms: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
		var idx: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
		var tv := PackedVector3Array()
		var tn := PackedVector3Array()
		tv.resize(verts.size())
		tn.resize(verts.size())
		for k in verts.size():
			tv[k] = xf * verts[k]
			tn[k] = (basis_n * norms[k]).normalized()
		var ti := idx.duplicate()
		if flip:
			for k in range(0, ti.size(), 3):
				var t := ti[k + 1]
				ti[k + 1] = ti[k + 2]
				ti[k + 2] = t
		var mat := mesh.surface_get_material(s) as BaseMaterial3D
		out.append({"mat": mat.resource_name, "v": tv, "n": tn, "i": ti})
	return out

# All surfaces of a source GLB, optionally restricted to top-level nodes whose name ends with `suffix`.
func _source_surfaces(path: String, suffix: String = "") -> Array:
	var key := path + "|" + suffix
	if _surf_cache.has(key):
		return _surf_cache[key]
	var root := _scene(path)
	var out := []
	var stack: Array = [root]
	while stack.size() > 0:
		var n: Node = stack.pop_back()
		if n != root and suffix != "" and n.get_parent() == root and not String(n.name).ends_with(suffix):
			continue
		for c in n.get_children():
			stack.append(c)
		if n is MeshInstance3D:
			out.append_array(_surfaces_of(n as MeshInstance3D, _local_to_root(n as Node3D, root)))
	_surf_cache[key] = out
	return out

func _tris(surfs: Array) -> int:
	var t := 0
	for s in surfs:
		t += (s.i as PackedInt32Array).size() / 3
	return t

# Merges surfaces that share a material (sorted by material name).
func _merge_by_material(surfs: Array) -> Array:
	var by := {}
	for s in surfs:
		if not by.has(s.mat):
			by[s.mat] = {"mat": s.mat, "v": PackedVector3Array(), "n": PackedVector3Array(), "i": PackedInt32Array()}
		var b: Dictionary = by[s.mat]
		var base: int = b.v.size()
		b.v.append_array(s.v)
		b.n.append_array(s.n)
		for k in (s.i as PackedInt32Array):
			b.i.append(base + k)
	var names := by.keys()
	names.sort()
	var out := []
	for nm in names:
		out.append(by[nm])
	return out

func _save_mesh(surfs: Array, path: String) -> ArrayMesh:
	var im := ImporterMesh.new()
	for s in surfs:
		var arrays := []
		arrays.resize(Mesh.ARRAY_MAX)
		arrays[Mesh.ARRAY_VERTEX] = s.v
		arrays[Mesh.ARRAY_NORMAL] = s.n
		arrays[Mesh.ARRAY_INDEX] = s.i
		im.add_surface(Mesh.PRIMITIVE_TRIANGLES, arrays, [], {}, _mat(s.mat), s.mat)
	im.generate_lods(NORMAL_MERGE_ANGLE, NORMAL_SPLIT_ANGLE, [])
	var am := im.get_mesh()
	am.resource_name = path.get_file().get_basename()
	ResourceSaver.save(am, path)
	return ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE) as ArrayMesh

# Bins triangles into DRESSING_TILE_M cells of the POI-local XZ centroid. `offset` = source-local -> POI-local.
# Returns {Vector2i: {mat: {mat, v, n, i}}}. Appends into `tiles` so several sources can share tiles.
func _bin_tiles(surfs: Array, offset: Vector3, tiles: Dictionary) -> void:
	for s in surfs:
		var verts: PackedVector3Array = s.v
		var norms: PackedVector3Array = s.n
		var idx: PackedInt32Array = s.i
		var remaps := {}   # tile -> PackedInt32Array(old vertex -> new vertex or -1)
		for k in range(0, idx.size(), 3):
			var i0 := idx[k]
			var i1 := idx[k + 1]
			var i2 := idx[k + 2]
			var cen := (verts[i0] + verts[i1] + verts[i2]) / 3.0 + offset
			var tile := Vector2i(int(floor(cen.x / DRESSING_TILE_M)), int(floor(cen.z / DRESSING_TILE_M)))
			if not tiles.has(tile):
				tiles[tile] = {}
			var td: Dictionary = tiles[tile]
			if not td.has(s.mat):
				td[s.mat] = {"mat": s.mat, "v": PackedVector3Array(), "n": PackedVector3Array(), "i": PackedInt32Array()}
			var b: Dictionary = td[s.mat]
			if not remaps.has(tile):
				var rm := PackedInt32Array()
				rm.resize(verts.size())
				rm.fill(-1)
				remaps[tile] = rm
			var rmap: PackedInt32Array = remaps[tile]
			for old in [i0, i1, i2]:
				if rmap[old] < 0:
					rmap[old] = b.v.size()
					b.v.append(verts[old])
					b.n.append(norms[old])
				b.i.append(rmap[old])
			remaps[tile] = rmap

# Writes one mesh per tile and returns a Node3D "Dressing" holding the tile instances; `prefix` names the mesh files.
func _make_dressing_node(tiles: Dictionary, prefix: String, owner_node: Node, parent: Node, node_name: String = "Dressing") -> Node3D:
	var dn := Node3D.new()
	dn.name = node_name
	parent.add_child(dn)
	dn.owner = owner_node
	var keys := tiles.keys()
	keys.sort()
	for t in keys:
		var td: Dictionary = tiles[t]
		var names := td.keys()
		names.sort()
		var surfs := []
		for nm in names:
			surfs.append(td[nm])
		var tag := "%s_%s" % [_idx(t.x), _idx(t.y)]
		var mi := MeshInstance3D.new()
		mi.name = "Tile_" + tag
		mi.mesh = _save_mesh(surfs, "%s/meshes/WL_%s_Dressing_%s.res" % [BUILT, prefix, tag])
		mi.visibility_range_end = DRESSING_RANGE_END
		mi.visibility_range_end_margin = DRESSING_RANGE_MARGIN
		mi.visibility_range_fade_mode = GeometryInstance3D.VISIBILITY_RANGE_FADE_SELF
		dn.add_child(mi)
		mi.owner = owner_node
	return dn

func _faces(surfs: Array) -> PackedVector3Array:
	var faces := PackedVector3Array()
	for s in surfs:
		var v: PackedVector3Array = s.v
		var idx: PackedInt32Array = s.i
		for k in idx:
			faces.append(v[k])
	return faces

func _arch_structure_surfaces() -> Dictionary:
	# chunk -> structure surfaces (chunk-local == Arch-local)
	var out := {}
	for c in CHUNKS:
		var surfs := _source_surfaces(SRC + "/" + _manifest.arch_chunks[c].file, "_Structure")
		# _source_surfaces keeps only top-level nodes named *_Structure, but moving meshes live in sub-pivots and
		# in top-level nodes with other names, so this only ever returns the static batch.
		out[c] = surfs
	return out

# --------------------------------------------------------------------------------------------
# collision

func _trimesh(faces: PackedVector3Array, path: String) -> ConcavePolygonShape3D:
	var s := ConcavePolygonShape3D.new()
	s.set_faces(faces)
	s.resource_name = path.get_file().get_basename()
	ResourceSaver.save(s, path)
	return ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE) as ConcavePolygonShape3D

# {"Sector_XX": shape, "Arch": shape}
func _build_collision_shapes() -> Dictionary:
	var out := {}
	var total := 0
	for s in _manifest.sectors:
		var id: int = s.id
		var surfs := _source_surfaces(SRC + "/" + s.structure_file)
		var faces := _faces(surfs)
		total += faces.size() / 3
		out["Sector_%02d" % id] = _trimesh(faces, "%s/WeatherLungCollision_Sector_%02d.res" % [COLL, id])
	var arch_faces := PackedVector3Array()
	var cs := _arch_structure_surfaces()
	for c in CHUNKS:
		arch_faces.append_array(_faces(cs[c]))
	total += arch_faces.size() / 3
	# The Arch faces are split into bodies of about ARCH_COLLISION_FACES triangles (sorted along the arch axis, so each body is spatially compact): one big
	# trimesh costs about 15 ms to register with the physics server, and the runtime attaches one body per frame.
	var tri_count := arch_faces.size() / 3
	var order := range(tri_count)
	var centroid_z := PackedFloat32Array()
	centroid_z.resize(tri_count)
	for t in tri_count:
		centroid_z[t] = (arch_faces[3 * t].z + arch_faces[3 * t + 1].z + arch_faces[3 * t + 2].z) / 3.0
	order.sort_custom(func(a: int, b: int) -> bool: return centroid_z[a] < centroid_z[b] if centroid_z[a] != centroid_z[b] else a < b)
	var pieces := ceili(float(tri_count) / ARCH_COLLISION_FACES)
	for piece in pieces:
		var piece_faces := PackedVector3Array()
		for o in range(piece * tri_count / pieces, (piece + 1) * tri_count / pieces):
			piece_faces.append(arch_faces[3 * order[o]])
			piece_faces.append(arch_faces[3 * order[o] + 1])
			piece_faces.append(arch_faces[3 * order[o] + 2])
		out["Arch_%d" % piece] = _trimesh(piece_faces, COLL + "/WeatherLungCollision_Arch_%d.res" % piece)
	print("collision: %d sector shapes + %d Arch shapes, %d faces total" % [_manifest.sectors.size(), pieces, total])
	return out

# --------------------------------------------------------------------------------------------
# Moving rig (module GLB -> pivot hierarchy with metadata)

func _pivot_kind(nm: String) -> String:
	if nm.begins_with("Gill_Pivot_"): return "gill"
	if nm.begins_with("Guide_Ring_"): return "ring"
	if nm.begins_with("Heliostat_Pivot_"): return "heliostat"
	if nm.begins_with("Turbine_Rotor_"): return "rotor"
	if nm.begins_with("VanePivot_"): return "vane"
	if nm.begins_with("Vane_RamCyl_"): return "ramcyl"
	if nm.begins_with("Vane_RamRod_"): return "ramrod"
	return ""

func _bl_to_godot(a) -> Vector3:
	# Blender (x, y, z) -> Godot (x, z, -y)
	return Vector3(a[0], a[2], -a[1])

# Signed rest angle of `q` about `axis` (the glTF rest pose is the exported breath phase, not exactly phase 0).
func _rest_angle(q: Quaternion, axis: Vector3) -> float:
	var ang := q.get_angle()
	if ang < 1e-7:
		return 0.0
	if q.get_axis().dot(axis) < 0.0:
		ang = -ang
	return ang

func _set_pivot_meta(p: Node3D, src: Node3D, kind: String, ctx: Dictionary) -> void:
	var nm := String(src.name)
	var extras: Dictionary = ctx.extras.get(nm, {})
	p.set_meta("wl_kind", kind)
	var axis := Vector3.ZERO
	var idx := -1
	match kind:
		"gill":
			idx = int(nm.get_slice("_", 2))
			if not extras.has("hinge_axis_blender"):
				_fail(nm + ": no hinge_axis_blender extra")
			else:
				axis = _bl_to_godot(extras.hinge_axis_blender).normalized()
		"rotor":
			idx = 0 if nm.ends_with("_A") else 1
			if not extras.has("spin_axis_blender"):
				_fail(nm + ": no spin_axis_blender extra")
			else:
				axis = _bl_to_godot(extras.spin_axis_blender).normalized()
		"ring":
			idx = 0 if nm.ends_with("_A") else 1
			axis = RING_AXIS.normalized()
			if absf(src.quaternion.get_axis().dot(axis)) < 0.999 and src.quaternion.get_angle() > 1e-4:
				_fail(nm + ": rest axis does not match the arch-plane normal")
		"vane":
			idx = int(nm.right(2))
			axis = VANE_AXIS
		"ramcyl", "ramrod":
			idx = int(nm.right(2))
			axis = VANE_AXIS if kind == "ramcyl" else Vector3.RIGHT
		"heliostat":
			var parts := nm.split("_")
			var row := int(parts[2])
			var col := int(parts[3])
			idx = row * 3 + col
			p.set_meta("wl_row", row)
			p.set_meta("wl_col", col)
			var purpose := String(extras.get("purpose", ""))
			var rx := RegEx.create_from_string("\\.\\. ([0-9.]+) rad")
			var mt := rx.search(purpose)
			if mt == null:
				_fail(nm + ": cannot parse th1 from purpose '" + purpose + "'")
			else:
				p.set_meta("wl_track_angle", mt.get_string(1).to_float())
			if src.quaternion.get_angle() < 1e-4:
				_fail(nm + ": rest rotation has no axis")
			else:
				axis = src.quaternion.get_axis().normalized()
	p.set_meta("wl_index", idx)
	p.set_meta("wl_axis", axis)
	if kind == "ramrod":
		p.set_meta("wl_rest_x", src.position.x)
	else:
		p.set_meta("wl_rest_angle", _rest_angle(src.quaternion, axis))
	if kind == "ramcyl":
		# Ram IK (K8): A = pin AABB centre in vane-pivot space, B = RamCyl translation, L0 = |P + A - B| at vane angle 0.
		var vp := ctx.root.get_node("VanePivot_%02d" % idx) as Node3D
		var pin := vp.get_node("Vane_ram_pin_%02d" % idx) as MeshInstance3D
		var a := pin.transform * pin.mesh.get_aabb().get_center()
		var b := src.position
		var rel0 := vp.position + a - b
		var l0 := Vector2(rel0.x, rel0.y).length()   # in-plane (x, y) length, see the ram check in tools/WeatherLungRigCheck.cs
		p.set_meta("wl_pin", a)
		p.set_meta("wl_base", b)
		p.set_meta("wl_vane_pos", vp.position)
		p.set_meta("wl_l0", l0)

func _build_pivot(src: Node3D, dst_parent: Node, owner_node: Node, ctx: Dictionary) -> void:
	var kind := _pivot_kind(String(src.name))
	if kind == "":
		_fail("unrecognised module node " + String(src.name))
		return
	var p := Node3D.new()
	p.name = src.name
	p.transform = src.transform
	dst_parent.add_child(p)
	p.owner = owner_node
	_set_pivot_meta(p, src, kind, ctx)
	ctx.counts[kind] = ctx.counts.get(kind, 0) + 1
	var surfs := []
	var moving_tris := 0
	var dressing_tris := 0
	var child_pivots := []
	for c in src.get_children():
		if c is MeshInstance3D:
			var mi := c as MeshInstance3D
			var sf := _surfaces_of(mi, mi.transform)
			surfs.append_array(sf)
			var cls := String((ctx.extras.get(String(mi.name), {}) as Dictionary).get("detail_class", "moving"))
			if cls == "dressing":
				dressing_tris += _tris(sf)
			else:
				moving_tris += _tris(sf)
		elif c is Node3D:
			child_pivots.append(c)
	p.set_meta("wl_tris_moving", moving_tris)
	p.set_meta("wl_tris_dressing", dressing_tris)
	ctx.moving_tris += moving_tris
	ctx.dressing_tris += dressing_tris
	if surfs.size() > 0:
		var mi := MeshInstance3D.new()
		mi.name = "Mesh"
		mi.mesh = _save_mesh(_merge_by_material(surfs), "%s/meshes/WL_Moving_%s.res" % [BUILT, src.name])
		p.add_child(mi)
		mi.owner = owner_node
	for c in child_pivots:
		_build_pivot(c, p, owner_node, ctx)

func _build_moving(owner_node: Node, parent: Node) -> void:
	var root := _scene(MODULE_GLB)
	var moving := Node3D.new()
	moving.name = "Moving"
	parent.add_child(moving)
	moving.owner = owner_node
	var ctx := {"root": root, "extras": _glb_node_extras(MODULE_GLB), "counts": {}, "moving_tris": 0, "dressing_tris": 0}
	var pivots := []
	for c in root.get_children():
		if c is Node3D:
			pivots.append(c)
	pivots.sort_custom(func(a, b): return String(a.name) < String(b.name))
	for c in pivots:
		_build_pivot(c as Node3D, moving, owner_node, ctx)
	var total := 0
	for k in EXPECTED_PIVOTS:
		var got: int = ctx.counts.get(k, 0)
		total += got
		if got != EXPECTED_PIVOTS[k]:
			_fail("pivot kind %s: %d found, %d expected" % [k, got, EXPECTED_PIVOTS[k]])
	print("moving: %d pivots %s, moving tris=%d, ram-dressing tris=%d" % [total, ctx.counts, ctx.moving_tris, ctx.dressing_tris])

# --------------------------------------------------------------------------------------------
# detail scene

func _clean_dir(dir_path: String, prefix: String, ext: String, keep_prefix: String = "~~~") -> void:
	var d := DirAccess.open(dir_path)
	if d == null:
		return
	for fn in d.get_files():
		if fn.begins_with(prefix) and fn.ends_with(ext) and not fn.begins_with(keep_prefix):
			d.remove(fn)

func _add(parent: Node, owner_node: Node, n: Node, nm: String) -> Node:
	n.name = nm
	parent.add_child(n)
	n.owner = owner_node
	return n

func _build_detail() -> void:
	_clean_dir(BUILT + "/meshes", "WL_", ".res", "WL_FarProxy")
	_clean_dir(BUILT + "/sectors", "WeatherLungSector_", ".tscn")
	_clean_dir(COLL, "WeatherLungCollision_", ".res")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(BUILT + "/arch"))
	_clean_dir(BUILT + "/arch", "WeatherLungPiece_", ".tscn")

	var root := Node3D.new()
	root.name = "WeatherLungDetail"
	var sectors_node := _add(root, root, Node3D.new(), "Sectors")
	var arch := _add(root, root, Node3D.new(), "Arch") as Node3D
	arch.position = _arch_origin

	var tile_total := 0
	var struct_tris := 0
	var dress_tris := 0

	# Sectors ------------------------------------------------------------------------------
	for s in _manifest.sectors:
		var id: int = s.id
		var org := _v3(s.origin_godot_m)
		var sroot := Node3D.new()
		sroot.name = "WeatherLungSector_%02d" % id
		sroot.position = org
		var st := _source_surfaces(SRC + "/" + s.structure_file)
		struct_tris += _tris(st)
		var smi := MeshInstance3D.new()
		smi.mesh = _save_mesh(st, "%s/meshes/WL_Sector_%02d_Structure.res" % [BUILT, id])
		_add(sroot, sroot, smi, "Structure")
		var dr := _source_surfaces(SRC + "/" + s.dressing_file)
		dress_tris += _tris(dr)
		var tiles := {}
		_bin_tiles(dr, org, tiles)
		tile_total += tiles.size()
		_make_dressing_node(tiles, "Sector_%02d" % id, sroot, sroot)
		var ps := PackedScene.new()
		ps.pack(sroot)
		var sp := "%s/sectors/WeatherLungSector_%02d.tscn" % [BUILT, id]
		ResourceSaver.save(ps, sp)
		sroot.free()
		var inst := (ResourceLoader.load(sp, "", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene).instantiate()
		sectors_node.add_child(inst)
		inst.owner = root
		print("  sector %02d origin=%s structure=%d dressing=%d tiles=%d" % [id, org, _tris(st), _tris(dr), tiles.size()])

	# Arch: chunk static batches, as separate scenes (the runtime attaches the Detail scene one piece per frame) --------------------
	var arch_struct := Node3D.new()
	arch_struct.name = "Structure"
	var arch_tiles := {}
	for c in CHUNKS:
		var path: String = SRC + "/" + _manifest.arch_chunks[c].file
		var cst := _source_surfaces(path, "_Structure")
		if cst.size() > 0:
			struct_tris += _tris(cst)
			var cmi := MeshInstance3D.new()
			cmi.mesh = _save_mesh(cst, "%s/meshes/WL_Arch_%s_Structure.res" % [BUILT, c])
			cmi.name = c
			arch_struct.add_child(cmi)
		var cdr := _source_surfaces(path, "_Dressing")
		dress_tris += _tris(cdr)
		_bin_tiles(cdr, _arch_origin, arch_tiles)
		print("  chunk %s structure=%d dressing=%d" % [c, _tris(cst), _tris(cdr)])
	tile_total += arch_tiles.size()
	_add_piece(arch, root, arch_struct, "Structure")
	# Tile meshes are built in Arch-local space (vertices stay chunk-local), keyed by POI-local cells. Tiles are grouped into pieces of DRESSING_PIECE_TILES.
	var arch_dressing := _add(arch, root, Node3D.new(), "Dressing")
	var tile_keys := arch_tiles.keys()
	tile_keys.sort()
	for piece_index in ceili(float(tile_keys.size()) / DRESSING_PIECE_TILES):
		var subset := {}
		for k in tile_keys.slice(piece_index * DRESSING_PIECE_TILES, (piece_index + 1) * DRESSING_PIECE_TILES):
			subset[k] = arch_tiles[k]
		var holder := Node3D.new()
		var tiles_node := _make_dressing_node(subset, "Arch", null, holder, "Tiles_%d" % piece_index)
		holder.remove_child(tiles_node)
		holder.free()
		_add_piece(arch_dressing, root, tiles_node, "Tiles_%d" % piece_index)
	var moving_holder := Node3D.new()
	_build_moving(null, moving_holder)
	var moving_node := moving_holder.get_child(0)
	moving_holder.remove_child(moving_node)
	moving_holder.free()
	_add_piece(arch, root, moving_node, "Moving")

	# Collision ------------------------------------------------------------------------------
	var shapes := _build_collision_shapes()
	var keys := shapes.keys()
	keys.sort()
	# One piece per body: creating a ConcavePolygonShape3D body registers its mesh shape with the physics server, which costs several ms to tens of ms
	# for the big ones, so each body is its own separately attached piece.
	var coll_root := _add(root, root, Node3D.new(), "Collision")
	for k in keys:
		var body := StaticBody3D.new()
		body.process_mode = Node.PROCESS_MODE_DISABLED
		if String(k).begins_with("Arch"):
			body.position = _arch_origin
		else:
			body.position = _v3(_manifest.sectors[int(String(k).right(2))].origin_godot_m)
		var cs := CollisionShape3D.new()
		cs.shape = shapes[k]
		cs.name = "Shape"
		body.add_child(cs)
		_add_piece(coll_root, root, body, "Body_" + String(k), String(k))

	var ps := PackedScene.new()
	ps.pack(root)
	var path := OUT + "/WeatherLungDetail.tscn"
	var err := ResourceSaver.save(ps, path)
	root.free()
	print("detail: saved %s (err=%d) structure tris=%d dressing tris (tiled)=%d dressing tiles=%d" % [path, err, struct_tris, dress_tris, tile_total])
	if tile_total > 450:
		push_error("ESCALATE (plan S1): dressing tile count %d > 450; build left in place for review" % tile_total)

# --------------------------------------------------------------------------------------------
# POI wrapper: FarProxy + Markers + DetailMount

func _build_proxy_mesh() -> ArrayMesh:
	var surfs := _source_surfaces(PROXY_GLB)
	print("proxy: %d surfaces, %d tris" % [surfs.size(), _tris(surfs)])
	return _save_mesh(surfs, BUILT + "/meshes/WL_FarProxy.res")

func _compute_markers() -> Dictionary:
	var m := {}
	m["Datum"] = Vector3.ZERO
	m["BasinCentre"] = Vector3(0.0, -800.0, 0.0)   # sealed floor top, basin centre
	m["Waterline"] = Vector3(0.0, -135.0, 0.0)
	# PlumeNozzle: centre of the Nozzle_Guide static batch (it holds the nozzle glow surfaces).
	var ng := _scene(SRC + "/" + _manifest.arch_chunks.Nozzle_Guide.file)
	var ngn := ng.get_node_or_null("Nozzle_Guide_Structure") as MeshInstance3D
	if ngn != null:
		m["PlumeNozzle"] = _arch_origin + ngn.transform * ngn.mesh.get_aabb().get_center()
	else:
		_fail("Nozzle_Guide_Structure node not found")
	# PlumeCrest: top-centre of the Hood_Shell static batch.
	var hs := _scene(SRC + "/" + _manifest.arch_chunks.Hood_Shell.file)
	var hsn := hs.get_node_or_null("Hood_Shell_Structure") as MeshInstance3D
	if hsn != null:
		var bb := hsn.mesh.get_aabb()
		var c := bb.get_center()
		m["PlumeCrest"] = _arch_origin + hsn.transform * Vector3(c.x, bb.end.y, c.z)
	else:
		_fail("Hood_Shell_Structure node not found")
	# DamCrest: highest structure vertex within DAM_SEARCH_M (XZ) of the lip ellipse at t = DAM_T_DEG.
	var t := deg_to_rad(DAM_T_DEG)
	var dx := BASIN_A * cos(t)
	var dz := -BASIN_B * sin(t)   # Blender y -> Godot -z
	var top := -1.0e9
	for s in _manifest.sectors:
		var org := _v3(s.origin_godot_m)
		for sf in _source_surfaces(SRC + "/" + s.structure_file):
			for v in (sf.v as PackedVector3Array):
				var w := org + v
				if absf(w.x - dx) < DAM_SEARCH_M and absf(w.z - dz) < DAM_SEARCH_M:
					top = maxf(top, w.y)
	m["DamCrest"] = Vector3(dx, top if top > -1.0e8 else 0.0, dz)
	if top < -1.0e8:
		print("WARNING: no structure vertex near the dam crest search point; DamCrest y left at 0")
	# IntakeOverlook: provisional point above the water, inside the lip, in front of the +X facing intake mouth. Tune in S3/S8.
	m["IntakeOverlook"] = Vector3(-7000.0, 150.0, 0.0)
	return m

func _build_poi() -> void:
	var root := Node3D.new()
	root.name = "WeatherLungPOI"
	# The controller (S2) is attached here once it exists; re-run `-- proxy` afterwards.
	if FileAccess.file_exists(CONTROLLER_SCRIPT):
		var sc := load(CONTROLLER_SCRIPT)
		if sc != null:
			root.set_script(sc)
			print("poi: attached " + CONTROLLER_SCRIPT)
		else:
			print("WARNING: " + CONTROLLER_SCRIPT + " exists but did not load (build the C# project first); root left without a script")
	else:
		print("poi: TODO S2 - " + CONTROLLER_SCRIPT + " does not exist yet; root has no script")

	var proxy := MeshInstance3D.new()
	proxy.mesh = _build_proxy_mesh()
	proxy.visibility_range_begin = PROXY_BEGIN
	proxy.visibility_range_begin_margin = PROXY_BEGIN_MARGIN
	proxy.visibility_range_end = PROXY_END
	proxy.visibility_range_fade_mode = GeometryInstance3D.VISIBILITY_RANGE_FADE_SELF
	proxy.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_add(root, root, proxy, "FarProxy")

	var markers := _add(root, root, Node3D.new(), "Markers")
	var mk := _compute_markers()
	for k in ["Datum", "BasinCentre", "Waterline", "PlumeNozzle", "PlumeCrest", "DamCrest", "IntakeOverlook"]:
		var mm := Marker3D.new()
		mm.position = mk.get(k, Vector3.ZERO)
		_add(markers, root, mm, k)
		print("  marker %-14s %s" % [k, mm.position])

	_add(root, root, Node3D.new(), "DetailMount")

	var ps := PackedScene.new()
	ps.pack(root)
	var path := OUT + "/WeatherLungPOI.tscn"
	var err := ResourceSaver.save(ps, path)
	root.free()
	print("poi: saved %s (err=%d)" % [path, err])

# --------------------------------------------------------------------------------------------
# standalone test scene (S3)

const TEST_SCENE := "res://scenes/poi/WeatherLungTest.tscn"
const TEST_GROUND := "res://scenes/poi/WeatherLungTestGround.res"
const TEST_SCRIPT := "res://scripts/poi/weather_lung_test.gd"
const FLY_CAMERA_SCRIPT := "res://scripts/poi/poi_fly_camera.gd"
const GROUND_Y := -0.05
const GROUND_R_INNER := 0.15
const GROUND_R_OUTER := 2.0
const GROUND_SEGMENTS := 192
const GROUND_RINGS := 10

# Elliptic basin coordinates (K13): pt(t, r) = ((9900 + 4100 r) cos t, (5650 + 3350 r) sin t) in Blender (x, y); Godot z = -y.
func _basin_point(t: float, r: float, y: float) -> Vector3:
	return Vector3((9900.0 + 4100.0 * r) * cos(t), y, -(5650.0 + 3350.0 * r) * sin(t))

func _build_ground() -> ArrayMesh:
	var verts := PackedVector3Array()
	var norms := PackedVector3Array()
	var idx := PackedInt32Array()
	for ri in GROUND_RINGS + 1:
		var r := lerpf(GROUND_R_INNER, GROUND_R_OUTER, float(ri) / GROUND_RINGS)
		for si in GROUND_SEGMENTS:
			verts.append(_basin_point(TAU * si / GROUND_SEGMENTS, r, GROUND_Y))
			norms.append(Vector3.UP)
	for ri in GROUND_RINGS:
		for si in GROUND_SEGMENTS:
			var a := ri * GROUND_SEGMENTS + si
			var b := ri * GROUND_SEGMENTS + (si + 1) % GROUND_SEGMENTS
			var c := (ri + 1) * GROUND_SEGMENTS + si
			var d := (ri + 1) * GROUND_SEGMENTS + (si + 1) % GROUND_SEGMENTS
			idx.append_array([a, c, b, b, c, d])
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = norms
	arrays[Mesh.ARRAY_INDEX] = idx
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.17, 0.2, 0.13)
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	mesh.surface_set_material(0, mat)
	mesh.resource_name = "WeatherLungTestGround"
	ResourceSaver.save(mesh, TEST_GROUND)
	return ResourceLoader.load(TEST_GROUND, "", ResourceLoader.CACHE_MODE_REPLACE) as ArrayMesh

func _look(pos: Vector3, target: Vector3) -> Transform3D:
	return Transform3D(Basis.looking_at(target - pos, Vector3.UP), pos)

func _build_test() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://scenes/poi"))
	var poi_ps := load(OUT + "/WeatherLungPOI.tscn") as PackedScene
	var poi_probe := poi_ps.instantiate()
	var mk := {}
	for m in poi_probe.get_node("Markers").get_children():
		mk[String(m.name)] = (m as Node3D).position
	poi_probe.free()

	var root := Node3D.new()
	root.name = "WeatherLungTest"
	root.set_script(load(TEST_SCRIPT))

	# Sky / light: the script swaps energies for the day / night looks.
	var sky_mat := ProceduralSkyMaterial.new()
	sky_mat.sky_top_color = Color(0.17, 0.34, 0.58)
	sky_mat.sky_horizon_color = Color(0.67, 0.77, 0.86)
	sky_mat.ground_bottom_color = Color(0.03, 0.04, 0.05)
	sky_mat.ground_horizon_color = Color(0.5, 0.58, 0.52)
	var sky := Sky.new()
	sky.sky_material = sky_mat
	var env := Environment.new()
	env.background_mode = Environment.BG_SKY
	env.sky = sky
	env.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	env.tonemap_mode = Environment.TONE_MAPPER_ACES
	env.glow_enabled = true
	var we := WorldEnvironment.new()
	we.environment = env
	_add(root, root, we, "WorldEnvironment")

	var sun := DirectionalLight3D.new()
	sun.transform = Transform3D(Basis.looking_at(Vector3(-0.45, -0.75, -0.35), Vector3.UP), Vector3.ZERO)
	sun.shadow_enabled = true
	sun.directional_shadow_max_distance = 6000.0
	_add(root, root, sun, "Sun")

	var ground := MeshInstance3D.new()
	ground.mesh = _build_ground()
	ground.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_add(root, root, ground, "Ground")

	# Test-only translucent water (the ring world supplies the real water; none exists outside it). Toggled with W.
	var water := MeshInstance3D.new()
	var pm := PlaneMesh.new()
	pm.size = Vector2(2.0 * 14000.0, 2.0 * 9000.0)
	var wm := StandardMaterial3D.new()
	wm.albedo_color = Color(0.05, 0.25, 0.35, 0.7)
	wm.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	wm.roughness = 0.1
	wm.metallic = 0.3
	pm.material = wm
	water.mesh = pm
	water.position = Vector3(0.0, -135.0, 0.0)
	water.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_add(root, root, water, "TestWaterPlaceholder")

	var poi := poi_ps.instantiate()
	poi.name = "WeatherLungPOI"
	root.add_child(poi)
	poi.owner = root

	# Bookmarks (POI-local). Provisional framing: tune after looking at the captures.
	var bm := _add(root, root, Node3D.new(), "Bookmarks")
	var dam: Vector3 = mk.get("DamCrest", Vector3(7854.0, 655.0, 3439.0))
	var views := [
		["DamCrest", dam + Vector3(0, 40, 0), Vector3(-9000.0, 1200.0, 0.0)],
		["BasinUnderHood", Vector3(-8200.0, 120.0, 0.0), Vector3(-11250.0, 1500.0, 0.0)],
		["TurbineThroat", Vector3(-11000.0, 1790.0, 0.0), Vector3(-12279.0, 1790.0, 0.0)],
		["GillCloseUp", Vector3(-9582.0, 3330.0, -3889.0), Vector3(-9751.0, 3002.0, -3423.0)],  # 600 m outside the hood shell along the arch radial (0.281, 0.547, -0.776)
		["Station12km", Vector3(9000.0, 1500.0, 8000.0), Vector3(0.0, 800.0, 0.0)],
		["Handoff40km", Vector3(24000.0, 2500.0, 32000.0), Vector3(0.0, 800.0, 0.0)],
	]
	for v in views:
		var m := Marker3D.new()
		m.transform = _look(v[1], v[2])
		_add(bm, root, m, v[0])

	var cam := Camera3D.new()
	cam.set_script(load(FLY_CAMERA_SCRIPT))
	cam.near = 1.0
	cam.far = 400000.0
	cam.current = true
	cam.set("base_speed", 400.0)
	cam.transform = (bm.get_child(0) as Node3D).transform
	_add(root, root, cam, "FlyCamera")

	var hud_layer := CanvasLayer.new()
	_add(root, root, hud_layer, "Hud")
	var label := Label.new()
	label.position = Vector2(12, 8)
	label.add_theme_color_override("font_color", Color(1, 1, 1))
	label.add_theme_color_override("font_outline_color", Color(0, 0, 0))
	label.add_theme_constant_override("outline_size", 4)
	_add(hud_layer, root, label, "Status")

	var ps := PackedScene.new()
	ps.pack(root)
	var err := ResourceSaver.save(ps, TEST_SCENE)
	root.free()
	print("test: saved %s (err=%d)" % [TEST_SCENE, err])

# --------------------------------------------------------------------------------------------
# Detail pieces

# Makes `n` (built without owners) the root of its own scene: owners are set recursively, the scene is saved under built/arch/ and its instance is
# added to `parent` of the Detail scene under `piece_name`. The runtime (WeatherLungStreamer) instantiates the Detail scene piece by piece from the
# loaded PackedScene's state, so every piece comes from the same threaded load.
func _add_piece(parent: Node, detail_root: Node, n: Node, piece_name: String, node_name: String = "") -> void:
	n.name = piece_name
	_own_all(n, n)
	var ps := PackedScene.new()
	ps.pack(n)
	var path := "%s/arch/WeatherLungPiece_%s.tscn" % [BUILT, _sanitize(piece_name)]
	ResourceSaver.save(ps, path)
	n.free()
	var inst := (ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene).instantiate()
	if node_name != "":
		inst.name = node_name
	parent.add_child(inst)
	inst.owner = detail_root

func _own_all(n: Node, root: Node) -> void:
	for c in n.get_children():
		c.owner = root
		_own_all(c, root)
