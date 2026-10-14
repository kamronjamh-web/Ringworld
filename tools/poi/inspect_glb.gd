extends SceneTree
# Usage: godot --headless --path . --script res://tools/poi/inspect_glb.gd -- res://path.glb [list]
var st := {"mi": 0, "surf": 0, "tris": 0}
var mats := {}
var kinds := {}
var list_nodes := false

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	list_nodes = args.size() > 1
	var t0 := Time.get_ticks_usec()
	var ps := load(args[0]) as PackedScene
	var t1 := Time.get_ticks_usec()
	var root := ps.instantiate()
	var t2 := Time.get_ticks_usec()
	_walk(root)
	print("load_ms=", (t1 - t0) / 1000.0, " instantiate_ms=", (t2 - t1) / 1000.0)
	print("root=", root.name, " ", root.get_class(), " children=", root.get_child_count())
	print("meshInstances=", st.mi, " surfaces=", st.surf, " tris=", st.tris, " uniqueMaterials(by name)=", mats.size())
	print("kinds=", kinds)
	print("materials=", mats)
	for c in root.get_children():
		print("child ", c.name, " ", c.get_class(), " sub=", _count(c))
	quit()

func _count(n: Node) -> int:
	var c := 1
	for k in n.get_children(): c += _count(k)
	return c

func _walk(n: Node) -> void:
	kinds[n.get_class()] = kinds.get(n.get_class(), 0) + 1
	if n is MeshInstance3D:
		st.mi += 1
		var m: Mesh = (n as MeshInstance3D).mesh
		if list_nodes: print(n.name, " surfaces=", m.get_surface_count())
		st.surf += m.get_surface_count()
		for i in m.get_surface_count():
			var mat := m.surface_get_material(i)
			var key: String = mat.resource_name if mat else "<null>"
			mats[key] = mats.get(key, 0) + 1
			var arr := m.surface_get_arrays(i)
			var idx = arr[Mesh.ARRAY_INDEX]
			st.tris += (idx.size() / 3) if idx != null else (arr[Mesh.ARRAY_VERTEX].size() / 3)
	for c in n.get_children(): _walk(c)
