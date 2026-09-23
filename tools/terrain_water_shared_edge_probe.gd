extends RefCounted

## CPU indexed border comparison. Terrain UV2.y > .5 marks a skirt.
const OCEAN_SHADER: String = "res://materials/ring_ocean_proxy.gdshader"
const TERRAIN_SHADER: String = "res://materials/ring_terrain_proxy.gdshader"
const SECTOR_SCRIPT_SUFFIX: String = "RingTerrainProxySector.cs"
const CIRCUMFERENCE_METERS: float = 31415936.0
const CAMERA_RADIUS_METERS: float = 1000.0
const SPACINGS: Array[int] = [2, 4, 8]
const MIN_PAIRS: int = 64
const MAX_GAP_METERS: float = 0.001

func inspect(scene: Node, camera_position: Vector3) -> Dictionary:
	var keyed: Dictionary = {}
	var metrics: Dictionary = {}
	for spacing: int in SPACINGS:
		metrics[str(spacing)] = {"water": _metric(), "terrain": _metric()}
	var meshes: int = 0
	for sector: MeshInstance3D in _meshes(scene):
		var owner: Node = sector.get_parent()
		if owner == null or sector.mesh == null or not _is_owner(owner) or not _near(sector, camera_position): continue
		var spacing: int = roundi(float(owner.get("SampleSpacingMeters")))
		var size: float = float(owner.get("SectorSizeMeters"))
		var coord: Vector2i = _coordinates(String(sector.name))
		if not SPACINGS.has(spacing) or size <= 0.0 or coord.x == -2147483648: continue
		var cells: int = maxi(1, roundi(size / float(spacing)))
		for surface: int in sector.mesh.get_surface_count():
			var material: Material = sector.mesh.surface_get_material(surface)
			var kind: String = "water" if _ocean(material) else "terrain" if _terrain(material) else ""
			if kind.is_empty(): continue
			var arrays: Array = sector.mesh.surface_get_arrays(surface)
			var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			var uvs: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV]
			var uv2s: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV2]
			var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
			if vertices.is_empty() or uvs.size() != vertices.size() or indices.is_empty(): continue
			meshes += 1
			var used: Dictionary = {}
			for index: int in indices:
				if index >= 0 and index < vertices.size(): used[index] = true
			for raw_index: Variant in used:
				var index: int = int(raw_index)
				if kind == "terrain" and index < uv2s.size() and uv2s[index].y > 0.5: continue
				var uv: Vector2 = uvs[index]
				var ix: int = roundi(uv.x * float(cells)); var iz: int = roundi(uv.y * float(cells))
				if absf(uv.x * float(cells) - float(ix)) > .0001 or absf(uv.y * float(cells) - float(iz)) > .0001: continue
				if ix < 0 or ix > cells or iz < 0 or iz > cells or (ix != 0 and ix != cells and iz != 0 and iz != cells): continue
				var around: float = fposmod(float(coord.x) * size + float(ix * spacing), CIRCUMFERENCE_METERS)
				var across: float = float(coord.y) * size + float(iz * spacing)
				var key: String = "%s:%d:%.3f:%.3f" % [kind, spacing, around, across]
				var entry: Dictionary = {"owner": str(owner.get_path()), "node": str(sector.get_path()), "world": sector.global_transform * vertices[index], "local": sector.position, "parent": (owner as Node3D).global_position, "around": around, "across": across}
				if keyed.has(key):
					var first: Dictionary = keyed[key]
					# Neighbouring sectors belong to the SAME LOD owner. Compare
					# distinct sector paths, not their common parent path.
					if String(first["node"]) != String(entry["node"]): _add(metrics[str(spacing)][kind], first, entry)
				else: keyed[key] = entry
	var passed: bool = true
	for spacing: int in SPACINGS:
		for kind: String in ["water", "terrain"]:
			var metric: Dictionary = metrics[str(spacing)][kind]
			metric["rms_gap_m"] = sqrt(float(metric["sum_sq"]) / maxf(1.0, float(metric["shared_pairs"])))
			metric["pass"] = int(metric["shared_pairs"]) >= MIN_PAIRS and bool(metric["finite"]) and float(metric["max_gap_m"]) <= MAX_GAP_METERS
			passed = passed and bool(metric["pass"])
	return {"kind": "terrain_water_shared_edge_cpu_probe", "camera_radius_m": CAMERA_RADIUS_METERS, "max_allowed_gap_m": MAX_GAP_METERS, "minimum_pairs_per_kind": MIN_PAIRS, "surface_mesh_count": meshes, "per_lod_meters": metrics, "pass": passed, "limitations": "CPU indexed lattice-border vertices only; terrain UV2.y>0.5 skirts excluded; no FFT displacement comparison."}

func _metric() -> Dictionary:
	return {"shared_pairs": 0, "sum_sq": 0.0, "max_gap_m": 0.0, "rms_gap_m": 0.0, "finite": true, "worst": []}

func _meshes(node: Node) -> Array[MeshInstance3D]:
	var found: Array[MeshInstance3D] = []; _collect(node, found); return found

func _collect(node: Node, found: Array[MeshInstance3D]) -> void:
	if node is MeshInstance3D and String(node.name).begins_with("Sector_") and node.is_visible_in_tree(): found.append(node as MeshInstance3D)
	for child: Node in node.get_children(): _collect(child, found)

func _near(sector: MeshInstance3D, camera_position: Vector3) -> bool:
	var bounds: AABB = sector.global_transform * sector.get_aabb()
	return camera_position.distance_to(camera_position.clamp(bounds.position, bounds.end)) <= CAMERA_RADIUS_METERS

func _is_owner(owner: Node) -> bool:
	var script: Script = owner.get_script() as Script
	return script != null and script.resource_path.ends_with(SECTOR_SCRIPT_SUFFIX)

func _ocean(material: Material) -> bool:
	var value: ShaderMaterial = material as ShaderMaterial
	return value != null and value.shader != null and value.shader.resource_path == OCEAN_SHADER

func _terrain(material: Material) -> bool:
	var value: ShaderMaterial = material as ShaderMaterial
	return value != null and value.shader != null and value.shader.resource_path == TERRAIN_SHADER

func _coordinates(node_name: String) -> Vector2i:
	var parts: PackedStringArray = node_name.split("_")
	if parts.size() != 3 or not parts[1].is_valid_int() or not parts[2].is_valid_int(): return Vector2i(-2147483648, 0)
	return Vector2i(parts[1].to_int(), parts[2].to_int())

func _add(metric: Dictionary, first: Dictionary, second: Dictionary) -> void:
	var gap: float = (first["world"] as Vector3).distance_to(second["world"] as Vector3)
	if not is_finite(gap):
		metric["finite"] = false
		return
	metric["shared_pairs"] = int(metric["shared_pairs"]) + 1; metric["sum_sq"] = float(metric["sum_sq"]) + gap * gap; metric["max_gap_m"] = maxf(float(metric["max_gap_m"]), gap)
	var record: Dictionary = {"gap_m": gap, "owner_a": first["owner"], "owner_b": second["owner"], "node_a": first["node"], "node_b": second["node"], "canonical_around_m": first["around"], "canonical_across_m": first["across"], "world_a": _vector(first["world"] as Vector3), "world_b": _vector(second["world"] as Vector3), "local_a": _vector(first["local"] as Vector3), "local_b": _vector(second["local"] as Vector3), "parent_a": _vector(first["parent"] as Vector3), "parent_b": _vector(second["parent"] as Vector3)}
	var worst: Array = metric["worst"]
	if worst.size() < 5: worst.append(record); return
	var smallest: int = 0
	for i: int in range(1, worst.size()):
		if float(worst[i]["gap_m"]) < float(worst[smallest]["gap_m"]): smallest = i
	if gap > float(worst[smallest]["gap_m"]): worst[smallest] = record

func _vector(value: Vector3) -> Array[float]:
	return [value.x, value.y, value.z]
