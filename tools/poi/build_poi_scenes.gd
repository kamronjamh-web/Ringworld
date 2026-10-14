extends SceneTree
# Builds the POI wrapper scenes and the standalone test scene from the imported GLBs and the
# output of merge_poi_scenes.gd. Run merge_poi_scenes.gd first.
#
#   godot --headless --path . --script res://tools/poi/build_poi_scenes.gd
#
# Writes:
#   res://assets/poi/beam_emitter/BeamEmitterPOI.tscn  (+ collision/*.res, merged/meshes/BeamEmitter_FarProxy.res)
#   res://assets/poi/reactor/ReactorPOI.tscn           (+ collision/*.res)
#   res://scenes/poi/EmitterReactorTest.tscn           (+ EmitterReactorGround.res, EmitterReactorGroundShape.res)
#
# Coordinates: Blender (x, y, z) -> Godot (x, z, -y); the GLB meshes are already in Godot space.

const EMITTER_GLB := "res://assets/poi/beam_emitter/Surface_Spire_and_Facility_Godot.glb"
const REACTOR_GLB := "res://assets/poi/reactor/Reactor_Architecture_Godot.glb"
const EMITTER_MERGED := "res://assets/poi/beam_emitter/merged/BeamEmitterMerged.tscn"
const REACTOR_MERGED := "res://assets/poi/reactor/merged/ReactorMerged.tscn"

# Visibility ranges (metres). The detailed spire hands over to the far proxy around 12 km.
const SPIRE_DETAIL_END := 12500.0
const PROXY_BEGIN := 11500.0
const RANGE_MARGIN := 1000.0
const FACILITY_DETAIL_END := 6000.0
const FACILITY_MARGIN := 500.0

const EXHAUST_TOP_Y := 1039.0       # reactor-local; see Reactor IMPORT_NOTES
const REACTOR_CHAMBER_Y := -4000.0
const CROWN_FOCUS_Y := 1743.0
const BORE_RADIUS := 75.0

const EMITTER_FLOOR_PATTERNS_GROUND := [
	"Entry_Throat_Level_", "Entry_Level_Exterior_Approach_Apron", "Ring_Continuous_Level_",
	"_Connection_Floor", "_Foundation_Floor", "Ramp_Lower_Access_Floor", "Ramp_Lower_Flat_Turn_Landing", "Ground_Throat_150m_Clear",
]
const EMITTER_FLOOR_PATTERNS_RAISED := [
	"Gallery_Raised_Observation_Floor", "Ramp_Walkable_Floor_Segment_", "Ramp_Upper_Access_Floor", "Ramp_Upper_Flat_Turn_Landing",
]
const REACTOR_WALK_PATTERNS := [
	" | wall terrace ", " | floating core terrace ", "ENTRY | continuous levitating causeway", "ENTRY | core docking tongue",
	"ENTRY | core vestibule gap closure", "upper | continuous levitating causeway", "lower damaged | fractured route",
	"lower damaged | dislocated fragment", " | levitating landing ", "Arrival | faceted landing tongue",
]

func _init() -> void:
	_build_emitter()
	_build_reactor()
	_build_test_scene()
	quit()

# --------------------------------------------------------------------------------------------
# helpers
func _local_to_root(n: Node3D, root: Node) -> Transform3D:
	var xf := Transform3D.IDENTITY
	var cur: Node = n
	while cur != null and cur != root:
		if cur is Node3D:
			xf = (cur as Node3D).transform * xf
		cur = cur.get_parent()
	return xf

func _collect_meshes(root: Node) -> Array:
	var out := []
	var stack: Array = [root]
	while stack.size() > 0:
		var n: Node = stack.pop_back()
		for c in n.get_children():
			stack.append(c)
		if n is MeshInstance3D:
			out.append(n)
	return out

func _faces_of(mi: MeshInstance3D, root: Node) -> PackedVector3Array:
	var xf := _local_to_root(mi, root)
	var f := mi.mesh.get_faces()
	for i in f.size():
		f[i] = xf * f[i]
	return f

func _matches(name: String, patterns: Array, prefix_only := false) -> bool:
	for p in patterns:
		if prefix_only:
			if name.begins_with(p):
				return true
		elif p in name:
			return true
	return false

func _trimesh(faces: PackedVector3Array, path: String) -> ConcavePolygonShape3D:
	var s := ConcavePolygonShape3D.new()
	s.set_faces(faces)
	s.resource_name = path.get_file().get_basename()
	ResourceSaver.save(s, path)
	return load(path)

func _mkdir(res_dir: String) -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(res_dir))

func _add_shape(body: StaticBody3D, owner_node: Node, shape: Shape3D, shape_name: String) -> void:
	var cs := CollisionShape3D.new()
	cs.name = shape_name
	cs.shape = shape
	body.add_child(cs)
	cs.owner = owner_node

func _marker(parent: Node, owner_node: Node, marker_name: String, pos: Vector3) -> Marker3D:
	var m := Marker3D.new()
	m.name = marker_name
	m.position = pos
	parent.add_child(m)
	m.owner = owner_node
	return m

func _clone_merged(merged_path: String, parent: Node, owner_node: Node, group_ranges: Dictionary) -> void:
	# Copies MeshInstance3D nodes of the merged scene into `parent`, grouped, with visibility ranges.
	var src := (load(merged_path) as PackedScene).instantiate()
	for g in src.get_children():
		var gn := Node3D.new()
		gn.name = g.name
		parent.add_child(gn)
		gn.owner = owner_node
		var rng: Dictionary = group_ranges.get(String(g.name), {})
		for m in g.get_children():
			var mi := MeshInstance3D.new()
			mi.name = m.name
			mi.mesh = (m as MeshInstance3D).mesh
			if rng.has("end"):
				mi.visibility_range_end = rng.end
				mi.visibility_range_end_margin = rng.margin
				mi.visibility_range_fade_mode = GeometryInstance3D.VISIBILITY_RANGE_FADE_SELF
			gn.add_child(mi)
			mi.owner = owner_node
	src.free()

# --------------------------------------------------------------------------------------------
# far proxy: 8 loft columns following the real arm cross-sections
func _build_far_proxy(src_root: Node, mat: Material, out_path: String) -> ArrayMesh:
	var slice_h := [1.0, 250.0, 600.0, 1000.0, 1400.0, 1799.0]
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	st.set_material(mat)
	var min_radius := 1e9
	for k in range(1, 9):
		var arm: MeshInstance3D = null
		for mi in _collect_meshes(src_root):
			if String(mi.name) == "Arm_%02d_Faceted_Monolith" % k:
				arm = mi
		var faces := _faces_of(arm, src_root)
		var theta := deg_to_rad(45.0 * (k - 1))
		var d := Vector3(cos(theta), 0.0, -sin(theta))
		var t := Vector3(sin(theta), 0.0, cos(theta))
		var rings := []
		for h in slice_h:
			var rmin := 1e9; var rmax := -1e9; var tmin := 1e9; var tmax := -1e9
			for i in range(0, faces.size(), 3):
				for e in 3:
					var a := faces[i + e]
					var b := faces[i + (e + 1) % 3]
					if (a.y - h) * (b.y - h) < 0.0:
						var p := a.lerp(b, (h - a.y) / (b.y - a.y))
						var r := p.dot(d); var tt := p.dot(t)
						rmin = minf(rmin, r); rmax = maxf(rmax, r); tmin = minf(tmin, tt); tmax = maxf(tmax, tt)
			rings.append([
				d * rmin + t * tmin + Vector3(0, h, 0), d * rmax + t * tmin + Vector3(0, h, 0),
				d * rmax + t * tmax + Vector3(0, h, 0), d * rmin + t * tmax + Vector3(0, h, 0)])
			for c in rings.back():
				min_radius = minf(min_radius, Vector2(c.x, c.z).length())
		var tv := []
		for i in rings.size() - 1:
			for c in 4:
				var c2 := (c + 1) % 4
				var a0: Vector3 = rings[i][c]; var a1: Vector3 = rings[i][c2]
				var b0: Vector3 = rings[i + 1][c]; var b1: Vector3 = rings[i + 1][c2]
				tv.append_array([a0, b0, a1, a1, b0, b1])
		tv.append_array([rings[0][0], rings[0][2], rings[0][1], rings[0][0], rings[0][3], rings[0][2]])
		var top: Array = rings.back()
		tv.append_array([top[0], top[1], top[2], top[0], top[2], top[3]])
		# Godot front faces are clockwise; make every triangle's Plane normal point away from the arm centre.
		var centre := Vector3.ZERO
		var cn := 0
		for ring in rings:
			for corner in ring:
				centre += corner
				cn += 1
		centre /= cn
		var flipped := 0
		for i in range(0, tv.size(), 3):
			var pn := Plane(tv[i], tv[i + 1], tv[i + 2]).normal
			var flip_tri := pn.dot((tv[i] + tv[i + 1] + tv[i + 2]) / 3.0 - centre) < 0.0
			if flip_tri:
				flipped += 1
			st.add_vertex(tv[i])
			st.add_vertex(tv[i + 2] if flip_tri else tv[i + 1])
			st.add_vertex(tv[i + 1] if flip_tri else tv[i + 2])
		print("  proxy arm ", k, " flipped ", flipped, "/", tv.size() / 3, " triangles to face outward")
	st.generate_normals()
	var mesh := st.commit()
	mesh.resource_name = "BeamEmitter_FarProxy"
	ResourceSaver.save(mesh, out_path)
	print("far proxy: triangles=", mesh.get_faces().size() / 3, " min axis radius=", min_radius)
	return load(out_path)

# --------------------------------------------------------------------------------------------
func _build_emitter() -> void:
	var src := (load(EMITTER_GLB) as PackedScene).instantiate()
	var coll_dir := "res://assets/poi/beam_emitter/collision"
	_mkdir(coll_dir)

	var root := Node3D.new()
	root.name = "BeamEmitterPOI"

	var detail := Node3D.new()
	detail.name = "Detail"
	root.add_child(detail)
	detail.owner = root
	_clone_merged(EMITTER_MERGED, detail, root, {
		"Spire": {"end": SPIRE_DETAIL_END, "margin": RANGE_MARGIN},
		"Facility": {"end": FACILITY_DETAIL_END, "margin": FACILITY_MARGIN},
		"Roof": {"end": FACILITY_DETAIL_END, "margin": FACILITY_MARGIN},
	})

	var proxy_mat := load("res://assets/poi/beam_emitter/merged/materials/Raised_Facet_Alloy.tres") as Material
	var proxy_mesh := _build_far_proxy(src, proxy_mat, "res://assets/poi/beam_emitter/merged/meshes/BeamEmitter_FarProxy.res")
	var proxy := MeshInstance3D.new()
	proxy.name = "FarProxy"
	proxy.mesh = proxy_mesh
	proxy.visibility_range_begin = PROXY_BEGIN
	proxy.visibility_range_begin_margin = RANGE_MARGIN
	proxy.visibility_range_fade_mode = GeometryInstance3D.VISIBILITY_RANGE_FADE_SELF
	proxy.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	root.add_child(proxy)
	proxy.owner = root

	# Collision ------------------------------------------------------------------------------
	var body := StaticBody3D.new()
	body.name = "Collision"
	root.add_child(body)
	body.owner = root
	var ground_faces := PackedVector3Array()
	var raised_faces := PackedVector3Array()
	var counts := {"ground": 0, "raised": 0}
	var feet := []
	for mi in _collect_meshes(src):
		var n := String(mi.name)
		if _matches(n, EMITTER_FLOOR_PATTERNS_GROUND):
			ground_faces.append_array(_faces_of(mi, src)); counts.ground += 1
		elif _matches(n, EMITTER_FLOOR_PATTERNS_RAISED):
			raised_faces.append_array(_faces_of(mi, src)); counts.raised += 1
		elif n.begins_with("Foot_") and n.ends_with("_Ground_Anchor"):
			feet.append(mi)
	print("emitter collision: ground meshes=", counts.ground, " tris=", ground_faces.size() / 3, " raised meshes=", counts.raised, " tris=", raised_faces.size() / 3, " feet=", feet.size())
	_add_shape(body, root, _trimesh(ground_faces, coll_dir + "/BeamEmitter_GroundFloors.res"), "GroundFloors")
	_add_shape(body, root, _trimesh(raised_faces, coll_dir + "/BeamEmitter_GalleryAndRamp.res"), "GalleryAndRamp")
	feet.sort_custom(func(a, b): return String(a.name) < String(b.name))
	for mi in feet:
		var hull := (mi as MeshInstance3D).mesh.create_convex_shape(true, false)
		var p := coll_dir + "/BeamEmitter_%s.res" % String(mi.name)
		hull.resource_name = p.get_file().get_basename()
		ResourceSaver.save(hull, p)
		_add_shape(body, root, load(p), String(mi.name))

	# Markers --------------------------------------------------------------------------------
	var markers := Node3D.new()
	markers.name = "Markers"
	root.add_child(markers)
	markers.owner = root
	_marker(markers, root, "BeamOrigin", Vector3.ZERO)               # bore centre at the ground datum
	_marker(markers, root, "ShaftBottom", Vector3(0, -1, 0))         # lowest point of the facility/throat, shaft attaches here
	_marker(markers, root, "BeamCrownFocus", Vector3(0, CROWN_FOCUS_Y, 0))
	_marker(markers, root, "EntryApproach", Vector3(232.0 * cos(deg_to_rad(292.5)), 0, -232.0 * sin(deg_to_rad(292.5))))

	# Placeholder beam (hidden) --------------------------------------------------------------
	var beam := MeshInstance3D.new()
	beam.name = "PlaceholderBeam"
	var cyl := CylinderMesh.new()
	cyl.top_radius = 70.0
	cyl.bottom_radius = 70.0
	cyl.height = 8000.0
	cyl.radial_segments = 24
	cyl.rings = 1
	var bm := StandardMaterial3D.new()
	bm.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	bm.albedo_color = Color(0.55, 1.0, 0.95, 0.35)
	bm.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	bm.emission_enabled = true
	bm.emission = Color(0.35, 1.0, 0.9)
	bm.emission_energy_multiplier = 3.0
	bm.cull_mode = BaseMaterial3D.CULL_DISABLED
	cyl.material = bm
	beam.mesh = cyl
	beam.position = Vector3(0, 4000, 0)
	beam.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	beam.visible = false
	root.add_child(beam)
	beam.owner = root

	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://assets/poi/beam_emitter/BeamEmitterPOI.tscn")
	root.free()
	src.free()

# --------------------------------------------------------------------------------------------
func _build_reactor() -> void:
	var src := (load(REACTOR_GLB) as PackedScene).instantiate()
	var coll_dir := "res://assets/poi/reactor/collision"
	_mkdir(coll_dir)
	var root := Node3D.new()
	root.name = "ReactorPOI"
	var detail := Node3D.new()
	detail.name = "Detail"
	root.add_child(detail)
	detail.owner = root
	_clone_merged(REACTOR_MERGED, detail, root, {})

	var body := StaticBody3D.new()
	body.name = "Collision"
	root.add_child(body)
	body.owner = root
	var faces := PackedVector3Array()
	var count := 0
	var exhaust_max_y := -1e9
	for mi in _collect_meshes(src):
		var n := String(mi.name)
		if _matches(n, REACTOR_WALK_PATTERNS) and not ("underside" in n) and not ("glyph" in n) and not ("wayfinding" in n) \
				and not ("seam" in n) and not ("transfer" in n) and not ("stele" in n):
			faces.append_array(_faces_of(mi, src)); count += 1
		if n.begins_with("Exhaust") or n.begins_with("Crown"):
			var a: AABB = _local_to_root(mi, src) * (mi as MeshInstance3D).get_aabb()
			exhaust_max_y = maxf(exhaust_max_y, a.end.y)
	print("reactor collision: walkable meshes=", count, " tris=", faces.size() / 3, " exhaust mesh max y=", exhaust_max_y)
	_add_shape(body, root, _trimesh(faces, coll_dir + "/Reactor_WalkableSurfaces.res"), "WalkableSurfaces")

	var markers := Node3D.new()
	markers.name = "Markers"
	root.add_child(markers)
	markers.owner = root
	_marker(markers, root, "ChamberCenter", Vector3.ZERO)
	_marker(markers, root, "ExhaustTop", Vector3(0, EXHAUST_TOP_Y, 0))
	_marker(markers, root, "ArrivalOverlook", Vector3(0, 80, 1385))  # Blender (0,-1385,80)

	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://assets/poi/reactor/ReactorPOI.tscn")
	root.free()
	src.free()

# --------------------------------------------------------------------------------------------
func _build_ground(path_mesh: String, path_shape: String) -> void:
	var radii := [75.0, 100.0, 150.0, 250.0, 400.0, 650.0, 1000.0, 1600.0, 2500.0, 4000.0, 6500.0, 10000.0, 16000.0, 25000.0, 40000.0, 70000.0]
	var segs := 96
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	var y := -0.05  # slightly below Y0 so the facility floor tops do not z-fight with the ground
	for r in radii.size() - 1:
		for s in segs:
			var a0 := TAU * s / segs
			var a1 := TAU * (s + 1) / segs
			var p00 := Vector3(cos(a0) * radii[r], y, sin(a0) * radii[r])
			var p01 := Vector3(cos(a1) * radii[r], y, sin(a1) * radii[r])
			var p10 := Vector3(cos(a0) * radii[r + 1], y, sin(a0) * radii[r + 1])
			var p11 := Vector3(cos(a1) * radii[r + 1], y, sin(a1) * radii[r + 1])
			for v in [p00, p10, p01, p01, p10, p11]:  # clockwise seen from above (Godot front face)
				st.set_normal(Vector3.UP)
				st.add_vertex(v)
	var mesh := st.commit()
	mesh.resource_name = "EmitterReactorGround"
	ResourceSaver.save(mesh, path_mesh)
	var shape := ConcavePolygonShape3D.new()
	shape.set_faces(mesh.get_faces())
	shape.resource_name = "EmitterReactorGroundShape"
	ResourceSaver.save(shape, path_shape)

func _bookmark(parent: Node, owner_node: Node, bname: String, pos: Vector3, look: Vector3) -> void:
	var m := Marker3D.new()
	m.name = bname
	parent.add_child(m)
	m.owner = owner_node
	var up := Vector3.UP if absf((look - pos).normalized().dot(Vector3.UP)) < 0.999 else Vector3.FORWARD
	m.transform = Transform3D(Basis.looking_at(look - pos, up), pos)

func _build_test_scene() -> void:
	_mkdir("res://scenes/poi")
	_build_ground("res://scenes/poi/EmitterReactorGround.res", "res://scenes/poi/EmitterReactorGroundShape.res")

	var root := Node3D.new()
	root.name = "EmitterReactorTest"
	root.set_script(load("res://scripts/poi/emitter_reactor_test.gd"))

	# Environments: surface (procedural sky) and dark underground (used when the camera is below the ground).
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
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.glow_enabled = true
	var we := WorldEnvironment.new()
	we.name = "WorldEnvironment"
	we.environment = env
	root.add_child(we)
	we.owner = root

	var dark := Environment.new()
	dark.background_mode = Environment.BG_COLOR
	dark.background_color = Color(0.004, 0.006, 0.008)
	dark.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	dark.ambient_light_color = Color(0.10, 0.16, 0.18)
	dark.ambient_light_energy = 0.9
	dark.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	dark.glow_enabled = true
	dark.fog_enabled = true
	dark.fog_light_color = Color(0.02, 0.05, 0.06)
	dark.fog_density = 0.0004
	ResourceSaver.save(dark, "res://scenes/poi/EmitterReactorDarkEnvironment.tres")

	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.rotation_degrees = Vector3(-42, 35, 0)
	sun.light_energy = 1.6
	sun.shadow_enabled = true
	sun.directional_shadow_max_distance = 700.0
	sun.light_cull_mask = 1  # reactor geometry lives on layer 2 and is not sunlit
	root.add_child(sun)
	sun.owner = root

	# Ground
	var ground := StaticBody3D.new()
	ground.name = "Ground"
	root.add_child(ground)
	ground.owner = root
	var gmi := MeshInstance3D.new()
	gmi.name = "GroundMesh"
	gmi.mesh = load("res://scenes/poi/EmitterReactorGround.res")
	var gmat := StandardMaterial3D.new()
	gmat.albedo_color = Color(0.17, 0.2, 0.13)
	gmat.roughness = 1.0
	gmi.material_override = gmat
	ground.add_child(gmi)
	gmi.owner = root
	var gcs := CollisionShape3D.new()
	gcs.name = "GroundShape"
	gcs.shape = load("res://scenes/poi/EmitterReactorGroundShape.res")
	ground.add_child(gcs)
	gcs.owner = root

	# POIs
	var emitter := (load("res://assets/poi/beam_emitter/BeamEmitterPOI.tscn") as PackedScene).instantiate()
	emitter.name = "BeamEmitterPOI"
	root.add_child(emitter)
	emitter.owner = root
	var reactor := (load("res://assets/poi/reactor/ReactorPOI.tscn") as PackedScene).instantiate()
	reactor.name = "ReactorPOI"
	reactor.position = Vector3(0, REACTOR_CHAMBER_Y, 0)
	root.add_child(reactor)
	reactor.owner = root

	# Shaft liner placeholder: open tube from the exhaust top to the emitter's shaft bottom.
	var top_y := REACTOR_CHAMBER_Y + EXHAUST_TOP_Y
	var liner := MeshInstance3D.new()
	liner.name = "ShaftLinerPlaceholder"
	var tube := CylinderMesh.new()
	tube.top_radius = 76.0
	tube.bottom_radius = 76.0
	tube.height = -1.0 - top_y
	tube.radial_segments = 48
	tube.rings = 1
	tube.cap_top = false
	tube.cap_bottom = false
	var lm := StandardMaterial3D.new()
	lm.albedo_color = Color(0.12, 0.16, 0.18)
	lm.metallic = 0.4
	lm.roughness = 0.5
	lm.cull_mode = BaseMaterial3D.CULL_DISABLED
	lm.emission_enabled = true
	lm.emission = Color(0.1, 0.5, 0.5)
	lm.emission_energy_multiplier = 0.15
	tube.material = lm
	liner.mesh = tube
	liner.position = Vector3(0, (top_y - 1.0) * 0.5, 0)
	liner.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	liner.layers = 2
	root.add_child(liner)
	liner.owner = root

	# Reactor lights (layer 2 only so they never light the surface)
	var l1 := OmniLight3D.new()
	l1.name = "ReactorCoreLight"
	l1.position = Vector3(0, REACTOR_CHAMBER_Y, 0)
	l1.light_color = Color(0.35, 1.0, 0.9)
	l1.light_energy = 12.0
	l1.omni_range = 1400.0
	l1.light_cull_mask = 2
	root.add_child(l1)
	l1.owner = root
	var l2 := OmniLight3D.new()
	l2.name = "ArrivalLight"
	l2.position = Vector3(0, REACTOR_CHAMBER_Y + 110, 1385)
	l2.light_color = Color(0.7, 0.95, 0.95)
	l2.light_energy = 2.5
	l2.omni_range = 500.0
	l2.light_cull_mask = 2
	root.add_child(l2)
	l2.owner = root

	# Camera + bookmarks
	var bm := Node3D.new()
	bm.name = "Bookmarks"
	root.add_child(bm)
	bm.owner = root
	var entry := Vector3(232.0 * cos(deg_to_rad(292.5)), 1.7, -232.0 * sin(deg_to_rad(292.5)))
	_bookmark(bm, root, "1_GroundEntry", entry, Vector3(0, 40, 0))
	_bookmark(bm, root, "2_Gallery", Vector3(112, 17.7, 0), Vector3(0, 8, 0))
	_bookmark(bm, root, "3_Far20km", Vector3(20000, 200, 0), Vector3(0, 900, 0))
	_bookmark(bm, root, "4_ReactorOverlook", Vector3(0, REACTOR_CHAMBER_Y + 81.7, 1385), Vector3(0, REACTOR_CHAMBER_Y + 40, 0))
	_bookmark(bm, root, "5_ShaftTop", Vector3(0, 30, 0), Vector3(0, -3000, 0))

	var cam := Camera3D.new()
	cam.name = "FlyCamera"
	cam.set_script(load("res://scripts/poi/poi_fly_camera.gd"))
	cam.far = 120000.0
	cam.near = 0.2
	cam.fov = 70.0
	root.add_child(cam)
	cam.owner = root
	cam.current = true

	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://scenes/poi/EmitterReactorTest.tscn")
	root.free()
