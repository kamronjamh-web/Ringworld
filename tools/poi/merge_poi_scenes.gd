extends SceneTree
# Build-time merge of the imported Blender POI GLBs into draw-call friendly scenes.
#
#   godot --headless --path . --script res://tools/poi/merge_poi_scenes.gd [-- emitter|reactor|main_spire|main_reactor|all [sectors=N]]
#
# Loads the imported .glb scenes (the originals stay untouched), bakes every MeshInstance3D into
# parent-space, and writes ONE surface per (group, material) with an importer-style LOD chain.
# Outputs under res://assets/poi/<poi>/merged/: materials/*.tres, meshes/*.res and <Prefix>Merged.tscn.
# Run after the GLBs are imported. Safe to re-run; outputs are overwritten.

const NORMAL_MERGE_ANGLE := deg_to_rad(60.0)
const NORMAL_SPLIT_ANGLE := deg_to_rad(25.0)

# Emission tweaks so the project's default glow (HDR threshold 1.0) picks the glowing inlays up.
# Value = target for max(emission channel) * energy multiplier.
const EMISSION_GLOW_TARGET := {
	"Facility_Soft_Teal_Guide": 1.6,
	"Facility_Control_Surface": 1.2,
}

const JOBS := {
	"emitter": {
		"src": "res://assets/poi/beam_emitter/Surface_Spire_and_Facility_Godot.glb",
		"out_dir": "res://assets/poi/beam_emitter/merged",
		"prefix": "BeamEmitter",
		"classify": "_classify_emitter",
		"groups": ["Spire", "Facility", "Roof"],
		"sectors": 8,
	},
	"reactor": {
		"src": "res://assets/poi/reactor/Reactor_Architecture_Godot.glb",
		"out_dir": "res://assets/poi/reactor/merged",
		"prefix": "Reactor",
		"classify": "_classify_reactor",
		"groups": ["Core", "Routes", "Exhaust"],
	},
	# Main spire: 16 sectors, one per arm (8 inner at 0..315 deg, 8 outer at 22.5 + 45k deg), see README.
	# Groups come from the top-level Blender collection (classify receives the collection node name).
	"main_spire": {
		"src": "res://assets/poi/main_spire/Main_Spire_Outer_Ring_Godot.glb",
		"out_dir": "res://assets/poi/main_spire/merged",
		"prefix": "MainSpire",
		"classify": "_classify_main_spire",
		"classify_by_collection": true,
		"groups": ["Spire", "Facility", "Roof"],
		"sectors": 16,
	},
	"main_reactor": {
		"src": "res://assets/poi/main_reactor/Main_Reactor_Architecture.glb",
		"out_dir": "res://assets/poi/main_reactor/merged",
		"prefix": "MainReactor",
		"classify": "_classify_main_reactor",
		"groups": ["Core", "Routes", "Exhaust"],
	},
}

func _init() -> void:
	var which := "all"
	var sectors_override := 0
	var args := OS.get_cmdline_user_args()
	for a in args:
		if a.begins_with("sectors="):
			sectors_override = int(a.get_slice("=", 1))
		else:
			which = a
	for key in JOBS:
		if which == "all" or which == key:
			var job: Dictionary = JOBS[key].duplicate()
			if sectors_override > 0 and job.has("sectors"):
				job["sectors"] = sectors_override
			_run_job(key, job)
	quit()

func _classify_main_spire(collection: String) -> String:
	# 11/24/35 = removable roofs and roof details; 10/12/22/23/32/34 = facility, outer foundations, entrance hall.
	for p in ["11_", "24_", "35_"]:
		if collection.begins_with(p):
			return "Roof"
	for p in ["10_", "12_", "22_", "23_", "32_", "34_"]:
		if collection.begins_with(p):
			return "Facility"
	return "Spire"  # 00 inner arms, 20/21 main spire, 25 accelerator stages, 31 outer arms, 33 outer rings, 36 crown

func _classify_main_reactor(node_name: String) -> String:
	var u := node_name.to_upper()
	if u.begins_with("EXHAUST") or u.begins_with("CROWN"):
		return "Exhaust"
	for p in ["HEART", "CONTAINMENT", "CORE L", "MASTER CORE", "HALO", "POWER MODULE", "POWER TRUNK", "POWER B"]:
		if u.begins_with(p):
			return "Core"
	if u.begins_with("B") and u.length() > 2 and u[1].is_valid_int() and (" | " in u) and not (u.begins_with("B1 UPPER") or u.begins_with("B2 LOWER DAMAGED")):
		return "Core"  # buttress bodies: sweeping suspension, recessed upper channel, embedded field channel, lower separated root
	return "Routes"  # terraces, causeways, anchors, entry/arrival tongues, wayfinding

func _classify_emitter(node_name: String) -> String:
	if "Removable" in node_name:
		return "Roof"
	if node_name.begins_with("Arm_") or node_name.begins_with("Foot_") or node_name.begins_with("Crown_") or node_name.begins_with("Ground_Throat"):
		return "Spire"
	return "Facility"

func _classify_reactor(node_name: String) -> String:
	if node_name.begins_with("Scale figure"):
		return ""  # human reference figure, not part of the asset
	if node_name.begins_with("Exhaust") or node_name.begins_with("Crown"):
		return "Exhaust"
	if node_name.begins_with("Heart") or node_name.begins_with("Containment") or " | sweeping suspension" in node_name \
			or " | recessed upper channel" in node_name or " | embedded field channel" in node_name or " | lower separated root" in node_name:
		return "Core"
	return "Routes"

func _sanitize(s: String) -> String:
	var out := ""
	for ch in s:
		out += ch if (ch.to_lower() != ch.to_upper() or ch.is_valid_int()) else "_"
	while "__" in out:
		out = out.replace("__", "_")
	return out.strip_edges().trim_prefix("_").trim_suffix("_")

func _local_to_root(n: Node3D, root: Node) -> Transform3D:
	var xf := Transform3D.IDENTITY
	var cur: Node = n
	while cur != null and cur != root:
		if cur is Node3D:
			xf = (cur as Node3D).transform * xf
		cur = cur.get_parent()
	return xf

func _collection_of(n: Node, root: Node) -> String:
	# Name of the top-level Blender collection node (child of the single assembly node under the GLB root).
	var assembly := root.get_child(0)
	var cur: Node = n
	while cur != null and cur.get_parent() != assembly:
		cur = cur.get_parent()
	return String(cur.name) if cur != null else ""

func _run_job(key: String, job: Dictionary) -> void:
	var t0 := Time.get_ticks_usec()
	var root := (load(job.src) as PackedScene).instantiate()
	var out_dir: String = job.out_dir
	for sub in ["", "/meshes", "/materials"]:
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir + sub))
	# Drop stale generated meshes from earlier runs (sector layout may have changed).
	var mdir := DirAccess.open(out_dir + "/meshes")
	if mdir:
		for fn in mdir.get_files():
			if fn.ends_with(".res"):
				mdir.remove(fn)

	var buckets := {}  # "Group|MatName" -> accumulators
	var src_mi := 0
	var src_surf := 0
	var src_tris := 0
	var kept_tris := 0
	var dropped := 0
	var materials := {}  # name -> BaseMaterial3D (first instance)
	var stack: Array = [root]
	while stack.size() > 0:
		var n: Node = stack.pop_back()
		for c in n.get_children():
			stack.append(c)
		if not (n is MeshInstance3D):
			continue
		var mi := n as MeshInstance3D
		var mesh := mi.mesh
		src_mi += 1
		src_surf += mesh.get_surface_count()
		var xf := _local_to_root(mi, root)
		var basis_n := xf.basis.inverse().transposed()
		var flip := xf.basis.determinant() < 0.0
		var group: String
		if job.get("classify_by_collection", false):
			group = call(job.classify, _collection_of(mi, root))
		else:
			group = call(job.classify, String(mi.name))
		for s in mesh.get_surface_count():
			var arrays := mesh.surface_get_arrays(s)
			var verts: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			var norms: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
			var idx: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
			var tri_count := idx.size() / 3
			src_tris += tri_count
			if group == "":
				dropped += 1
				continue
			kept_tris += tri_count
			var mat := mesh.surface_get_material(s) as BaseMaterial3D
			var mname: String = mat.resource_name if mat else "default"
			if not materials.has(mname):
				materials[mname] = mat
			var tv := PackedVector3Array()
			var tn := PackedVector3Array()
			tv.resize(verts.size())
			tn.resize(verts.size())
			for k in verts.size():
				tv[k] = xf * verts[k]
				tn[k] = (basis_n * norms[k]).normalized()
			# Triangles are binned by the azimuth of their centroid (sectors are centred on the 8 spire arms)
			# so frustum/shadow culling still works on the merged meshes.
			var sectors: int = job.get("sectors", 1)
			var remaps := {}  # sector -> PackedInt32Array(old vertex -> new vertex or -1)
			for k in range(0, idx.size(), 3):
				var i0 := idx[k]
				var i1 := idx[k + 1] if not flip else idx[k + 2]
				var i2 := idx[k + 2] if not flip else idx[k + 1]
				var sector := 0
				if sectors > 1:
					var cen := (tv[i0] + tv[i1] + tv[i2]) / 3.0
					var ang := fposmod(atan2(-cen.z, cen.x) + PI / sectors, TAU)
					sector = mini(int(ang / (TAU / sectors)), sectors - 1)
				var bkey := "%s|%s|%d" % [group, mname, sector]
				if not buckets.has(bkey):
					buckets[bkey] = {"group": group, "mat": mname, "sector": sector, "v": PackedVector3Array(), "n": PackedVector3Array(), "i": PackedInt32Array()}
				var b: Dictionary = buckets[bkey]
				if not remaps.has(bkey):
					var rm := PackedInt32Array()
					rm.resize(verts.size())
					rm.fill(-1)
					remaps[bkey] = rm
				var rmap: PackedInt32Array = remaps[bkey]
				for old in [i0, i1, i2]:
					if rmap[old] < 0:
						rmap[old] = b.v.size()
						b.v.append(tv[old])
						b.n.append(tn[old])
					b.i.append(rmap[old])
				remaps[bkey] = rmap

	# Materials: extracted to .tres so every merged mesh shares one instance per material.
	var mat_res := {}
	for mname in materials:
		var m := (materials[mname] as BaseMaterial3D).duplicate() as BaseMaterial3D
		if m.emission_enabled and EMISSION_GLOW_TARGET.has(mname):
			var e := m.emission
			var peak := maxf(e.r, maxf(e.g, e.b))
			if peak > 0.0:
				m.emission_energy_multiplier = maxf(m.emission_energy_multiplier, EMISSION_GLOW_TARGET[mname] / peak)
		var mpath: String = "%s/materials/%s.tres" % [out_dir, _sanitize(mname)]
		ResourceSaver.save(m, mpath)
		mat_res[mname] = load(mpath)

	# Meshes
	var out_root := Node3D.new()
	out_root.name = String(job.prefix) + "Merged"
	var group_nodes := {}
	for g in job.groups:
		var gn := Node3D.new()
		gn.name = g
		out_root.add_child(gn)
		gn.owner = out_root
		group_nodes[g] = gn
	var out_mi := 0
	var out_surf := 0
	var out_tris := 0
	var keys := buckets.keys()
	keys.sort()
	for bkey in keys:
		var b: Dictionary = buckets[bkey]
		var im := ImporterMesh.new()
		var arrays := []
		arrays.resize(Mesh.ARRAY_MAX)
		arrays[Mesh.ARRAY_VERTEX] = b.v
		arrays[Mesh.ARRAY_NORMAL] = b.n
		arrays[Mesh.ARRAY_INDEX] = b.i
		im.add_surface(Mesh.PRIMITIVE_TRIANGLES, arrays, [], {}, mat_res[b.mat], b.mat)
		im.generate_lods(NORMAL_MERGE_ANGLE, NORMAL_SPLIT_ANGLE, [])
		var am := im.get_mesh()
		var suffix: String = "_S%d" % b.sector if job.get("sectors", 1) > 1 else ""
		var base_name := "%s_%s_%s%s" % [job.prefix, b.group, _sanitize(b.mat), suffix]
		var mesh_path := "%s/meshes/%s.res" % [out_dir, base_name]
		am.resource_name = base_name
		ResourceSaver.save(am, mesh_path)
		var mi := MeshInstance3D.new()
		mi.name = _sanitize(b.mat) + suffix
		mi.mesh = load(mesh_path)
		(group_nodes[b.group] as Node3D).add_child(mi)
		mi.owner = out_root
		out_mi += 1
		out_surf += 1
		out_tris += (b.i as PackedInt32Array).size() / 3
		print("  ", base_name, " tris=", (b.i as PackedInt32Array).size() / 3)

	var ps := PackedScene.new()
	ps.pack(out_root)
	var scene_path := "%s/%sMerged.tscn" % [out_dir, job.prefix]
	ResourceSaver.save(ps, scene_path)
	print("[%s] source: meshInstances=%d surfaces=%d tris=%d | merged: meshInstances=%d surfaces=%d tris=%d (dropped surfaces=%d) | %.1f ms -> %s" % [
		key, src_mi, src_surf, src_tris, out_mi, out_surf, out_tris, dropped, (Time.get_ticks_usec() - t0) / 1000.0, scene_path])
	root.free()
	out_root.free()
