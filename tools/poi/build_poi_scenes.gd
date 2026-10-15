extends SceneTree
# Builds the POI wrapper scenes and the standalone test scene from the imported GLBs and the
# output of merge_poi_scenes.gd. Run merge_poi_scenes.gd first.
#
#   godot --headless --path . --script res://tools/poi/build_poi_scenes.gd [-- emitter|reactor|main_spire|main_spire_proxy|main_reactor|test|all]
#   main_spire_proxy writes only MainSpire_FarProxy.res (no wrapper, merged detail, or collision changes).
#
# Writes:
#   res://assets/poi/beam_emitter/BeamEmitterPOI.tscn  (+ collision/*.res, merged/meshes/BeamEmitter_FarProxy.res)
#   res://assets/poi/reactor/ReactorPOI.tscn           (+ collision/*.res)
#   res://assets/poi/main_spire/MainSpirePOI.tscn, res://assets/poi/main_reactor/MainReactorPOI.tscn (+ collision/*.res, MainSpire_FarProxy.res)
#   res://scenes/poi/EmitterReactorTest.tscn           (+ [Main]EmitterReactorGround[Shape].res); holds the regular and the main pair
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

# Main spire (4,000 m tall, 1.64 km footprint) and main reactor (chamber-centre origin). Ranges are the regular
# emitter's scaled by the height ratio (~2.2) for the spire and by the footprint ratio (~2) for the facility.
const MAIN_SPIRE_GLB := "res://assets/poi/main_spire/Main_Spire_Outer_Ring_Godot.glb"
const MAIN_REACTOR_GLB := "res://assets/poi/main_reactor/Main_Reactor_Architecture.glb"
const MAIN_SPIRE_MERGED := "res://assets/poi/main_spire/merged/MainSpireMerged.tscn"
const MAIN_REACTOR_MERGED := "res://assets/poi/main_reactor/merged/MainReactorMerged.tscn"
const MAIN_SPIRE_DETAIL_END := 28000.0
const MAIN_PROXY_BEGIN := 26000.0
const MAIN_RANGE_MARGIN := 2000.0
const MAIN_FACILITY_DETAIL_END := 12000.0
const MAIN_FACILITY_MARGIN := 1000.0
const MAIN_EXHAUST_TOP_Y := 2078.0   # reactor-local roof interface; world -1,922 at the intended placement
const MAIN_CROWN_FOCUS_Y := 3989.0   # outer open crown focus ring (Y 3,984..3,994)
const MAIN_ENTRY_APPROACH_R := 930.0 # hall floor ends at R ~890 on the 292.5 deg (Godot XZ 67.5 deg) axis
const MAIN_BORE_RADIUS := 150.0
const MAIN_EXTRA_FLOOR_PATTERNS_GROUND := ["Outer_Entrance_Continuous_Walkable_Floor", "Outer_Entrance_Under_Apron_Join_Floor"]

# Imported mesh compression introduces up to 0.055 m of same-profile radial spread and 0.000043 rad
# of angular displacement in this GLB. These weld tolerances stay well below its distinct profile/row gaps.
const PROXY_PROFILE_RADIUS_EPSILON := 0.075
const PROXY_PROFILE_HEIGHT_EPSILON := 0.002
const PROXY_ANGLE_EPSILON := 0.0001

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
	var which := "all"
	var args := OS.get_cmdline_user_args()
	if args.size() > 0:
		which = args[0]
	if which == "main_spire_proxy":
		var source_scene := load(MAIN_SPIRE_GLB) as PackedScene
		if source_scene == null:
			push_error("Could not load main spire far proxy source: " + MAIN_SPIRE_GLB)
			quit(1)
			return
		var src := source_scene.instantiate()
		var proxy_mesh := _build_main_spire_far_proxy(src)
		src.free()
		quit(0 if proxy_mesh != null else 1)
		return
	if which in ["all", "emitter"]:
		_build_emitter()
	if which in ["all", "reactor"]:
		_build_reactor()
	if which in ["all", "main_spire"]:
		if not _build_main_spire():
			quit(1)
			return
	if which in ["all", "main_reactor"]:
		_build_main_reactor()
	if which in ["all", "test"]:
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
# far proxy: loft columns following the real arm cross-sections. `arms` = [[mesh name, azimuth deg (Blender), slice heights]];
# empty = the regular emitter's 8 arms.
func _build_far_proxy(src_root: Node, mat: Material, out_path: String, arms: Array = [], extra_faces: PackedVector3Array = PackedVector3Array()) -> ArrayMesh:
	if arms.is_empty():
		for k in range(1, 9):
			arms.append(["Arm_%02d_Faceted_Monolith" % k, 45.0 * (k - 1), [1.0, 250.0, 600.0, 1000.0, 1400.0, 1799.0]])
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	st.set_material(mat)
	var min_radius := 1e9
	for arm_def in arms:
		var arm: MeshInstance3D = null
		for mi in _collect_meshes(src_root):
			if String(mi.name) == arm_def[0]:
				arm = mi
		if arm == null or arm.mesh == null:
			push_error("Missing far proxy arm mesh: " + String(arm_def[0]))
			return null
		var slice_h: Array = arm_def[2]
		var faces := _faces_of(arm, src_root)
		var theta := deg_to_rad(float(arm_def[1]))
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
			if not is_finite(rmin) or not is_finite(rmax) or not is_finite(tmin) or not is_finite(tmax) or rmin >= rmax or tmin >= tmax:
				push_error("Invalid far proxy arm section: %s at Y %s" % [arm_def[0], h])
				return null
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
		print("  proxy ", arm_def[0], " flipped ", flipped, "/", tv.size() / 3, " triangles to face outward")
	for v in extra_faces:
		st.add_vertex(v)
		min_radius = minf(min_radius, Vector2(v.x, v.z).length())
	st.generate_normals()
	var mesh := st.commit()
	mesh.resource_name = out_path.get_file().get_basename()
	var error := ResourceSaver.save(mesh, out_path)
	if error != OK:
		push_error("Could not save far proxy %s: %s" % [out_path, error_string(error)])
		return null
	print("far proxy: triangles=", mesh.get_faces().size() / 3, " min vertex axis radius=", min_radius)
	return load(out_path)

# The main spire's annuli and collectors are surfaces of revolution with polygonal end caps.
# Collapse alternate angular rows, retaining every source radial/Y profile point, cap, and winding.
# Welding the profile coordinates first removes float noise from the baked GLB's repeated vertices.
func _proxy_angle(v: Vector3) -> float:
	var angle := fposmod(atan2(v.z, v.x), TAU)
	return 0.0 if minf(angle, TAU - angle) < PROXY_ANGLE_EPSILON else angle

func _reduce_proxy_angular_rows(faces: PackedVector3Array, mesh_name: String, source_segments: int, closed: bool, stride := 2) -> PackedVector3Array:
	var angles: Array[float] = []
	var profiles: Array[Vector2] = []
	var angle_sums: Array[float] = []
	var angle_counts: Array[int] = []
	var profile_sums: Array[Vector2] = []
	var profile_counts: Array[int] = []
	for v in faces:
		var angle := _proxy_angle(v)
		var profile := Vector2(Vector2(v.x, v.z).length(), v.y)
		var angle_index := -1
		for i in angles.size():
			if absf(angles[i] - angle) < PROXY_ANGLE_EPSILON:
				angle_index = i
				break
		if angle_index < 0:
			angles.append(angle)
			angle_sums.append(angle)
			angle_counts.append(1)
		else:
			angle_sums[angle_index] += angle
			angle_counts[angle_index] += 1
		var profile_index := -1
		for i in profiles.size():
			if absf(profiles[i].x - profile.x) < PROXY_PROFILE_RADIUS_EPSILON and absf(profiles[i].y - profile.y) < PROXY_PROFILE_HEIGHT_EPSILON:
				profile_index = i
				break
		if profile_index < 0:
			profiles.append(profile)
			profile_sums.append(profile)
			profile_counts.append(1)
		else:
			profile_sums[profile_index] += profile
			profile_counts[profile_index] += 1
	for i in angles.size():
		angles[i] = angle_sums[i] / angle_counts[i]
	for i in profiles.size():
		profiles[i] = profile_sums[i] / profile_counts[i]
	angles.sort()
	var expected_rows := source_segments if closed else source_segments + 1
	if angles.size() != expected_rows or profiles.size() < 3:
		push_error("Invalid far proxy angular profile: %s (%s rows, %s profile points)" % [mesh_name, angles.size(), profiles.size()])
		return PackedVector3Array()
	var step: float = TAU / source_segments if closed else (angles.back() - angles.front()) / source_segments
	for i in range(1, angles.size()):
		if absf(angles[i] - angles[i - 1] - step) > 2.0 * PROXY_ANGLE_EPSILON:
			push_error("Nonuniform far proxy angular rows: " + mesh_name)
			return PackedVector3Array()
	if closed and absf(angles.front() + TAU - angles.back() - step) > 2.0 * PROXY_ANGLE_EPSILON:
		push_error("Incomplete far proxy annulus: " + mesh_name)
		return PackedVector3Array()
	if stride == 1:
		return faces
	var reduced := PackedVector3Array()
	var triangle: Array[Vector3] = []
	for v in faces:
		var angle := _proxy_angle(v)
		var row := -1
		for i in angles.size():
			if absf(angles[i] - angle) < PROXY_ANGLE_EPSILON:
				row = i
				break
		var profile := Vector2(Vector2(v.x, v.z).length(), v.y)
		var found_profile := false
		for p in profiles:
			if absf(p.x - profile.x) < PROXY_PROFILE_RADIUS_EPSILON and absf(p.y - profile.y) < PROXY_PROFILE_HEIGHT_EPSILON:
				profile = p
				found_profile = true
				break
		if row < 0 or not found_profile:
			push_error("Unmatched far proxy angular/profile vertex: " + mesh_name)
			return PackedVector3Array()
		var target_angle := angles[row - row % stride]
		triangle.append(Vector3(cos(target_angle) * profile.x, profile.y, sin(target_angle) * profile.x))
		if triangle.size() == 3:
			if (triangle[1] - triangle[0]).cross(triangle[2] - triangle[0]).length_squared() > 0.000001:
				reduced.append_array(PackedVector3Array(triangle))
			triangle.clear()
	if reduced.is_empty():
		push_error("Empty reduced far proxy mesh: " + mesh_name)
	return reduced

func _build_main_spire_far_proxy(src: Node) -> ArrayMesh:
	# Keep the existing 8 inner + 8 staggered outer arm lofts and entrance-arm start height.
	var arms := []
	var inner_slices := [1.0, 300.0, 700.0, 1200.0, 1800.0, 2400.0, 3000.0, 3590.0]
	var entry_slices := [26.0, 300.0, 700.0, 1200.0, 1800.0, 2400.0, 3000.0, 3500.0, 3990.0]
	var outer_slices := [1.0, 300.0, 700.0, 1200.0, 1800.0, 2400.0, 3000.0, 3500.0, 3990.0]
	var required := []
	for k in range(1, 9):
		arms.append(["Arm_%02d_Faceted_Monolith" % k, 45.0 * (k - 1), inner_slices])
		# Outer arm 07's monolith starts at Y 24; its portal jambs carry the lower part.
		arms.append(["Outer_Arm_%02d_Main_Monolith" % k, 22.5 + 45.0 * (k - 1), entry_slices if k == 7 else outer_slices])
	for arm in arms:
		required.append(arm[0])
	var structures := []
	for stage in range(1, 7):
		structures.append(["Outer_Connector_Ring_Stage_%02d" % stage, 64, true, 2])
		for k in range(1, 9):
			structures.append(["Outer_Ring_Stage_%02d_Radial_Tie_%02d" % [stage, k], 0, false, 1])
			structures.append(["Accelerator_Stage_%02d_Collector_Sector_%02d" % [stage, k], 4, false, 2])
			structures.append(["Accelerator_Stage_%02d_Arm_Feed_Clamp_%02d" % [stage, k], 0, false, 1])
	structures.append(["Outer_Crown_Open_4000m_Focus_Ring", 128, true, 2])
	# The inner crown's R 150.2609 bore needs all 64 source segments to keep triangle interiors outside R 150.
	structures.append(["Crown_Open_Annular_Focus", 64, true, 1])
	for structure in structures:
		required.append(structure[0])
	var source_faces := {}
	for mi in _collect_meshes(src):
		var mesh_name := String(mi.name)
		if mesh_name not in required:
			continue
		if source_faces.has(mesh_name) or mi.mesh == null:
			push_error("Duplicate or invalid main spire proxy source mesh: " + mesh_name)
			return null
		var faces := _faces_of(mi, src)
		if faces.is_empty() or faces.size() % 3 != 0:
			push_error("Invalid main spire proxy source faces: " + mesh_name)
			return null
		for v in faces:
			if not v.is_finite():
				push_error("Non-finite main spire proxy source vertex: " + mesh_name)
				return null
		for i in range(0, faces.size(), 3):
			if (faces[i + 1] - faces[i]).cross(faces[i + 2] - faces[i]).length_squared() <= 0.000001:
				push_error("Degenerate main spire proxy source triangle: " + mesh_name)
				return null
		source_faces[mesh_name] = faces
	for mesh_name in required:
		if not source_faces.has(mesh_name):
			push_error("Missing main spire proxy source mesh: " + mesh_name)
			return null
	var extra_faces := PackedVector3Array()
	for structure in structures:
		var faces: PackedVector3Array = source_faces[structure[0]]
		if structure[1] > 0:
			faces = _reduce_proxy_angular_rows(faces, structure[0], structure[1], structure[2], structure[3])
			if faces.is_empty():
				return null
		extra_faces.append_array(faces)
	print("main spire proxy structure: 6 annuli (32 segments), 48 radial ties, 48 collectors (2 spans), 48 arm clamps, 2 crowns (64 segments each); triangles=", extra_faces.size() / 3)
	var proxy_mat := load("res://assets/poi/main_spire/merged/materials/Raised_Facet_Alloy.tres") as Material
	if proxy_mat == null:
		push_error("Missing main spire far proxy material")
		return null
	var out_path := "res://assets/poi/main_spire/merged/meshes/MainSpire_FarProxy.res"
	_mkdir(out_path.get_base_dir())
	return _build_far_proxy(src, proxy_mat, out_path, arms, extra_faces)

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
# Main spire / main reactor (see assets/poi/README.md). Same structure as the regular wrappers.
func _build_main_spire() -> bool:
	var src := (load(MAIN_SPIRE_GLB) as PackedScene).instantiate()
	var coll_dir := "res://assets/poi/main_spire/collision"
	_mkdir(coll_dir)

	var root := Node3D.new()
	root.name = "MainSpirePOI"

	var detail := Node3D.new()
	detail.name = "Detail"
	root.add_child(detail)
	detail.owner = root
	_clone_merged(MAIN_SPIRE_MERGED, detail, root, {
		"Spire": {"end": MAIN_SPIRE_DETAIL_END, "margin": MAIN_RANGE_MARGIN},
		"Facility": {"end": MAIN_FACILITY_DETAIL_END, "margin": MAIN_FACILITY_MARGIN},
		"Roof": {"end": MAIN_FACILITY_DETAIL_END, "margin": MAIN_FACILITY_MARGIN},
	})

	var proxy_mesh := _build_main_spire_far_proxy(src)
	if proxy_mesh == null:
		root.free()
		src.free()
		return false
	var proxy := MeshInstance3D.new()
	proxy.name = "FarProxy"
	proxy.mesh = proxy_mesh
	proxy.visibility_range_begin = MAIN_PROXY_BEGIN
	proxy.visibility_range_begin_margin = MAIN_RANGE_MARGIN
	proxy.visibility_range_fade_mode = GeometryInstance3D.VISIBILITY_RANGE_FADE_SELF
	proxy.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	root.add_child(proxy)
	proxy.owner = root

	# Collision: walkable floors only (same rule as the regular emitter) + convex hulls of the 8 inner and 8+1 outer feet.
	var body := StaticBody3D.new()
	body.name = "Collision"
	root.add_child(body)
	body.owner = root
	var ground_faces := PackedVector3Array()
	var raised_faces := PackedVector3Array()
	var counts := {"ground": 0, "raised": 0}
	var feet := []
	var ground_patterns: Array = EMITTER_FLOOR_PATTERNS_GROUND + MAIN_EXTRA_FLOOR_PATTERNS_GROUND
	for mi in _collect_meshes(src):
		var n := String(mi.name)
		if _matches(n, ground_patterns):
			ground_faces.append_array(_faces_of(mi, src)); counts.ground += 1
		elif _matches(n, EMITTER_FLOOR_PATTERNS_RAISED):
			raised_faces.append_array(_faces_of(mi, src)); counts.raised += 1
		elif (n.begins_with("Foot_") and n.ends_with("_Ground_Anchor")) or (n.begins_with("Outer_Foot_") and n.ends_with("Foundation")) \
				or (n.begins_with("Outer_Foot_") and "Foundation_" in n):
			feet.append(mi)
	print("main spire collision: ground meshes=", counts.ground, " tris=", ground_faces.size() / 3, " raised meshes=", counts.raised, " tris=", raised_faces.size() / 3, " feet=", feet.size())
	_add_shape(body, root, _trimesh(ground_faces, coll_dir + "/MainSpire_GroundFloors.res"), "GroundFloors")
	_add_shape(body, root, _trimesh(raised_faces, coll_dir + "/MainSpire_GalleryAndRamp.res"), "GalleryAndRamp")
	feet.sort_custom(func(a, b): return String(a.name) < String(b.name))
	for mi in feet:
		var hull := (mi as MeshInstance3D).mesh.create_convex_shape(true, false)
		var p := coll_dir + "/MainSpire_%s.res" % String(mi.name)
		hull.resource_name = p.get_file().get_basename()
		ResourceSaver.save(hull, p)
		_add_shape(body, root, load(p), String(mi.name))

	var markers := Node3D.new()
	markers.name = "Markers"
	root.add_child(markers)
	markers.owner = root
	_marker(markers, root, "BeamOrigin", Vector3.ZERO)
	_marker(markers, root, "ShaftBottom", Vector3(0, -2, 0))          # foundation slab bottom
	_marker(markers, root, "BeamCrownFocus", Vector3(0, MAIN_CROWN_FOCUS_Y, 0))
	_marker(markers, root, "EntryApproach", Vector3(MAIN_ENTRY_APPROACH_R * cos(deg_to_rad(292.5)), 0, -MAIN_ENTRY_APPROACH_R * sin(deg_to_rad(292.5))))

	var beam := MeshInstance3D.new()
	beam.name = "PlaceholderBeam"
	var cyl := CylinderMesh.new()
	cyl.top_radius = 140.0
	cyl.bottom_radius = 140.0
	cyl.height = 16000.0
	cyl.radial_segments = 32
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
	beam.position = Vector3(0, 8000, 0)
	beam.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	beam.visible = false
	root.add_child(beam)
	beam.owner = root

	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://assets/poi/main_spire/MainSpirePOI.tscn")
	root.free()
	src.free()
	return true

func _build_main_reactor() -> void:
	var src := (load(MAIN_REACTOR_GLB) as PackedScene).instantiate()
	var coll_dir := "res://assets/poi/main_reactor/collision"
	_mkdir(coll_dir)
	var root := Node3D.new()
	root.name = "MainReactorPOI"
	var detail := Node3D.new()
	detail.name = "Detail"
	root.add_child(detail)
	detail.owner = root
	_clone_merged(MAIN_REACTOR_MERGED, detail, root, {})

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
		if n.begins_with("Exhaust") or n.begins_with("EXHAUST") or n.begins_with("Crown"):
			var a: AABB = _local_to_root(mi, src) * (mi as MeshInstance3D).get_aabb()
			exhaust_max_y = maxf(exhaust_max_y, a.end.y)
	print("main reactor collision: walkable meshes=", count, " tris=", faces.size() / 3, " exhaust mesh max y=", exhaust_max_y)
	_add_shape(body, root, _trimesh(faces, coll_dir + "/MainReactor_WalkableSurfaces.res"), "WalkableSurfaces")

	var markers := Node3D.new()
	markers.name = "Markers"
	root.add_child(markers)
	markers.owner = root
	_marker(markers, root, "ChamberCenter", Vector3.ZERO)
	_marker(markers, root, "ExhaustTop", Vector3(0, MAIN_EXHAUST_TOP_Y, 0))
	_marker(markers, root, "ArrivalOverlook", Vector3(0, 160, 2770))  # Blender (0,-2770,160)

	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://assets/poi/main_reactor/MainReactorPOI.tscn")
	root.free()
	src.free()

# --------------------------------------------------------------------------------------------
func _build_ground(path_mesh: String, path_shape: String, hole := 75.0) -> void:
	var radii := [hole]
	for r in [75.0, 100.0, 150.0, 250.0, 400.0, 650.0, 1000.0, 1600.0, 2500.0, 4000.0, 6500.0, 10000.0, 16000.0, 25000.0, 40000.0, 70000.0]:
		if r > hole:
			radii.append(r)
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
	mesh.resource_name = path_mesh.get_file().get_basename()
	ResourceSaver.save(mesh, path_mesh)
	var shape := ConcavePolygonShape3D.new()
	shape.set_faces(mesh.get_faces())
	shape.resource_name = path_shape.get_file().get_basename()
	ResourceSaver.save(shape, path_shape)

func _bookmark(parent: Node, owner_node: Node, bname: String, pos: Vector3, look: Vector3) -> void:
	var m := Marker3D.new()
	m.name = bname
	parent.add_child(m)
	m.owner = owner_node
	var up := Vector3.UP if absf((look - pos).normalized().dot(Vector3.UP)) < 0.999 else Vector3.FORWARD
	m.transform = Transform3D(Basis.looking_at(look - pos, up), pos)

func _add_node(parent: Node, owner_node: Node, n: Node, node_name: String) -> Node:
	n.name = node_name
	parent.add_child(n)
	n.owner = owner_node
	return n

# One POI pair (spire + reactor + ground with a bore hole + shaft liner + reactor lights + bookmarks) under `pair`.
func _build_pair(pair: Node3D, root: Node, cfg: Dictionary) -> void:
	var ground := StaticBody3D.new()
	_add_node(pair, root, ground, "Ground")
	var gmi := MeshInstance3D.new()
	gmi.mesh = load(cfg.ground_mesh)
	var gmat := StandardMaterial3D.new()
	gmat.albedo_color = Color(0.17, 0.2, 0.13)
	gmat.roughness = 1.0
	gmi.material_override = gmat
	_add_node(ground, root, gmi, "GroundMesh")
	var gcs := CollisionShape3D.new()
	gcs.shape = load(cfg.ground_shape)
	_add_node(ground, root, gcs, "GroundShape")

	var spire := (load(cfg.spire_scene) as PackedScene).instantiate()
	_add_node(pair, root, spire, cfg.spire_name)
	var reactor := (load(cfg.reactor_scene) as PackedScene).instantiate()
	reactor.position = Vector3(0, REACTOR_CHAMBER_Y, 0)
	_add_node(pair, root, reactor, cfg.reactor_name)

	# Shaft liner placeholder: open tube from the exhaust top to the spire's shaft bottom.
	var top_y: float = REACTOR_CHAMBER_Y + cfg.exhaust_top
	var shaft_bottom: float = cfg.shaft_bottom
	var liner := MeshInstance3D.new()
	var tube := CylinderMesh.new()
	tube.top_radius = cfg.liner_radius
	tube.bottom_radius = cfg.liner_radius
	tube.height = shaft_bottom - top_y
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
	liner.position = Vector3(0, (top_y + shaft_bottom) * 0.5, 0)
	liner.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	liner.layers = 2
	_add_node(pair, root, liner, "ShaftLinerPlaceholder")

	# Reactor lights (layer 2 only so they never light the surface)
	var l1 := OmniLight3D.new()
	l1.position = Vector3(0, REACTOR_CHAMBER_Y, 0)
	l1.light_color = Color(0.35, 1.0, 0.9)
	l1.light_energy = 12.0
	l1.omni_range = cfg.core_light_range
	l1.light_cull_mask = 2
	_add_node(pair, root, l1, "ReactorCoreLight")
	var l2 := OmniLight3D.new()
	l2.position = cfg.arrival_light_pos
	l2.light_color = Color(0.7, 0.95, 0.95)
	l2.light_energy = 2.5
	l2.omni_range = cfg.arrival_light_range
	l2.light_cull_mask = 2
	_add_node(pair, root, l2, "ArrivalLight")

	var bm := Node3D.new()
	_add_node(pair, root, bm, "Bookmarks")
	for b in cfg.bookmarks:
		_bookmark(bm, root, b[0], b[1], b[2])

func _build_test_scene() -> void:
	_mkdir("res://scenes/poi")
	_build_ground("res://scenes/poi/EmitterReactorGround.res", "res://scenes/poi/EmitterReactorGroundShape.res", 75.0)
	_build_ground("res://scenes/poi/MainEmitterReactorGround.res", "res://scenes/poi/MainEmitterReactorGroundShape.res", MAIN_BORE_RADIUS)

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
	we.environment = env
	_add_node(root, root, we, "WorldEnvironment")

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
	sun.rotation_degrees = Vector3(-42, 35, 0)
	sun.light_energy = 1.6
	sun.shadow_enabled = true
	sun.directional_shadow_max_distance = 700.0  # the script raises this for the main pair
	sun.light_cull_mask = 1  # reactor geometry lives on layer 2 and is not sunlit
	_add_node(root, root, sun, "Sun")

	# Regular pair (default) and main pair (key M); the script shows one at a time.
	var regular := Node3D.new()
	_add_node(root, root, regular, "Regular")
	var entry := Vector3(232.0 * cos(deg_to_rad(292.5)), 1.7, -232.0 * sin(deg_to_rad(292.5)))
	_build_pair(regular, root, {
		"ground_mesh": "res://scenes/poi/EmitterReactorGround.res",
		"ground_shape": "res://scenes/poi/EmitterReactorGroundShape.res",
		"spire_scene": "res://assets/poi/beam_emitter/BeamEmitterPOI.tscn", "spire_name": "BeamEmitterPOI",
		"reactor_scene": "res://assets/poi/reactor/ReactorPOI.tscn", "reactor_name": "ReactorPOI",
		"exhaust_top": EXHAUST_TOP_Y, "shaft_bottom": -1.0, "liner_radius": 76.0,
		"core_light_range": 1400.0,
		"arrival_light_pos": Vector3(0, REACTOR_CHAMBER_Y + 110, 1385), "arrival_light_range": 500.0,
		"bookmarks": [
			["1_GroundEntry", entry, Vector3(0, 40, 0)],
			["2_Gallery", Vector3(112, 17.7, 0), Vector3(0, 8, 0)],
			["3_Far20km", Vector3(20000, 200, 0), Vector3(0, 900, 0)],
			["4_ReactorOverlook", Vector3(0, REACTOR_CHAMBER_Y + 81.7, 1385), Vector3(0, REACTOR_CHAMBER_Y + 40, 0)],
			["5_ShaftTop", Vector3(0, 30, 0), Vector3(0, -3000, 0)],
		],
	})

	var main_pair := Node3D.new()
	_add_node(root, root, main_pair, "Main")
	# Entry axis: Blender 292.5 deg = Godot XZ 67.5 deg; the hall floor ends at R ~890.
	var main_entry := Vector3(960.0 * cos(deg_to_rad(292.5)), 1.7, -960.0 * sin(deg_to_rad(292.5)))
	_build_pair(main_pair, root, {
		"ground_mesh": "res://scenes/poi/MainEmitterReactorGround.res",
		"ground_shape": "res://scenes/poi/MainEmitterReactorGroundShape.res",
		"spire_scene": "res://assets/poi/main_spire/MainSpirePOI.tscn", "spire_name": "MainSpirePOI",
		"reactor_scene": "res://assets/poi/main_reactor/MainReactorPOI.tscn", "reactor_name": "MainReactorPOI",
		"exhaust_top": MAIN_EXHAUST_TOP_Y, "shaft_bottom": -2.0, "liner_radius": MAIN_BORE_RADIUS + 1.0,
		"core_light_range": 2800.0,
		"arrival_light_pos": Vector3(0, REACTOR_CHAMBER_Y + 220, 2770), "arrival_light_range": 1000.0,
		"bookmarks": [
			["1_GroundEntry", main_entry, Vector3(0, 12, 0)],
			["2_InnerGallery", Vector3(224, 33.7, 0), Vector3(0, 30, 0)],
			["3_Far40km", Vector3(40000, 400, 0), Vector3(0, 1800, 0)],
			["4_ReactorOverlook", Vector3(0, REACTOR_CHAMBER_Y + 164.7, 2770), Vector3(0, REACTOR_CHAMBER_Y + 80, 0)],
			["5_ShaftTop", Vector3(0, 30, 0), Vector3(0, -3000, 0)],
		],
	})

	var cam := Camera3D.new()
	cam.set_script(load("res://scripts/poi/poi_fly_camera.gd"))
	cam.far = 120000.0
	cam.near = 0.2
	cam.fov = 70.0
	cam.bookmarks_path = ^"../Regular/Bookmarks"
	_add_node(root, root, cam, "FlyCamera")
	cam.current = true

	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://scenes/poi/EmitterReactorTest.tscn")
	root.free()
