extends SceneTree
# Usage: godot --headless --path . --script res://tools/poi/inspect_materials.gd -- res://path.glb
func _init() -> void:
	var root := (load(OS.get_cmdline_user_args()[0]) as PackedScene).instantiate()
	var seen := {}
	_walk(root, seen)
	quit()
func _walk(n: Node, seen: Dictionary) -> void:
	if n is MeshInstance3D:
		var m: Mesh = n.mesh
		for i in m.get_surface_count():
			var mat := m.surface_get_material(i) as BaseMaterial3D
			if mat and not seen.has(mat.resource_name):
				seen[mat.resource_name] = true
				print(mat.resource_name, " albedo=", mat.albedo_color, " metal=", mat.metallic, " rough=", mat.roughness, " emis=", mat.emission_enabled, " ", mat.emission, " e=", mat.emission_energy_multiplier, " tex=", mat.albedo_texture != null, " nrm=", mat.normal_enabled, " cull=", mat.cull_mode, " transp=", mat.transparency, " id=", mat.get_instance_id())
			var a := m.surface_get_arrays(i)
			if not seen.has("fmt"):
				seen["fmt"] = true
				var s := ""
				for k in a.size(): s += str(k) + ":" + ("Y" if a[k] != null else "n") + " "
				print("arrays ", s)
	for c in n.get_children(): _walk(c, seen)
