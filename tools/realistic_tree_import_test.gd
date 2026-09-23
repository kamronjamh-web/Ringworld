extends SceneTree

const PACKS := {
	"fir": {"scene": "res://resources/trees/lolipop/fir/scene.gltf", "license": "res://resources/trees/lolipop/fir/license.txt", "variants": 3},
	"pine": {"scene": "res://resources/trees/lolipop/pine/scene.gltf", "license": "res://resources/trees/lolipop/pine/license.txt", "variants": 15},
	"maple": {"scene": "res://resources/trees/lolipop/maple/scene.gltf", "license": "res://resources/trees/lolipop/maple/license.txt", "variants": 12},
}
const LOD_PATTERN := "^(.+?)(?:_Billboard)?_LOD([0-3])$"
const PREVIEW := "res://scenes/TreeAssetPreview.tscn"
const OUTPUT := "res://.godot/realistic_tree_import"
const FIR_MASK_CUTOFFS := [0.21790542599748874, 0.37638209945020784, 0.3032390193951067, 0.19352439931245502]

func _init() -> void: call_deferred("_run")

func _run() -> void:
	var failures: Array[String] = []
	for name in PACKS: _validate_pack(name, PACKS[name], failures)
	await _validate_preview(failures)
	if "--capture" in OS.get_cmdline_user_args() and failures.is_empty():
		if RenderingServer.get_rendering_device() == null: failures.append("GPU capture requested but Dummy/Compatibility rendering is active")
		else: await _capture_preview(failures)
	if failures.is_empty(): print("Realistic tree import validation: PASS (fir, pine, maple)"); quit(0)
	else:
		for failure in failures: push_error(failure)
		quit(1)

func _validate_pack(name: String, pack: Dictionary, failures: Array[String]) -> void:
	for path in [pack.scene, pack.license]:
		if not FileAccess.file_exists(path): failures.append("%s missing %s" % [name, path])
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(pack.scene))
	if not parsed is Dictionary: failures.append("%s glTF is not valid JSON" % name); return
	var gltf: Dictionary = parsed
	_validate_dependencies(name, pack.scene, gltf, failures)
	var scene := load(pack.scene) as PackedScene
	if scene == null: failures.append("%s glTF did not import as PackedScene" % name); return
	var root := scene.instantiate()
	var anchors := _lod_anchors(root)
	var variants: Dictionary = {}
	for key in anchors:
		var entry: Dictionary = anchors[key]
		if _nonzero_meshes(entry.node) == 0: failures.append("%s %s LOD%d has no geometry" % [name, entry.variant, entry.lod])
		variants[entry.variant] = true
	if variants.size() != int(pack.variants): failures.append("%s has %d variants; expected %d" % [name, variants.size(), pack.variants])
	for variant in variants:
		for lod in 4:
			if not anchors.has("%s:%d" % [variant, lod]): failures.append("%s %s is missing LOD%d" % [name, variant, lod])
	root.queue_free()

func _validate_dependencies(name: String, scene: String, gltf: Dictionary, failures: Array[String]) -> void:
	var base := scene.get_base_dir()
	for buffer in gltf.get("buffers", []): _validate_uri(name, base, str(buffer.get("uri", "")), "buffer", failures)
	var images: Array = gltf.get("images", [])
	for i in images.size():
		var path := _validate_uri(name, base, str(images[i].get("uri", "")), "image", failures)
		if path != "":
			var image := Image.load_from_file(ProjectSettings.globalize_path(path))
			if image == null or image.get_width() <= 0 or image.get_height() <= 0: failures.append("%s image %d has invalid dimensions" % [name, i])
	var textures: Array = gltf.get("textures", [])
	for material in gltf.get("materials", []):
		var pbr: Dictionary = material.get("pbrMetallicRoughness", {})
		for ref in [pbr.get("baseColorTexture", {}), pbr.get("metallicRoughnessTexture", {}), material.get("normalTexture", {}), material.get("occlusionTexture", {}), material.get("emissiveTexture", {})]:
			if ref.is_empty(): continue
			var texture_index := int(ref.get("index", -1))
			if texture_index < 0 or texture_index >= textures.size() or int(textures[texture_index].get("source", -1)) < 0 or int(textures[texture_index].get("source", -1)) >= images.size(): failures.append("%s material references an invalid texture/image" % name)

func _validate_uri(name: String, base: String, uri: String, kind: String, failures: Array[String]) -> String:
	if uri.is_empty() or uri.contains("://") or uri.begins_with("/") or uri.contains(".."):
		failures.append("%s has unsafe %s URI: %s" % [name, kind, uri]); return ""
	var path := base.path_join(uri)
	if not FileAccess.file_exists(path): failures.append("%s missing %s: %s" % [name, kind, uri]); return ""
	return path

func _validate_preview(failures: Array[String]) -> void:
	var packed := load(PREVIEW) as PackedScene
	if packed == null: failures.append("Preview does not load as PackedScene"); return
	var preview := packed.instantiate() as Node3D
	get_root().add_child(preview); await process_frame
	var specimens := preview.get_node_or_null("Specimens") as Node3D
	if specimens == null or specimens.get_child_count() != 3: failures.append("Preview did not construct exactly three specimens")
	else:
		for expected in ["FirSpecimen", "PineSpecimen", "MapleSpecimen"]:
			if specimens.get_node_or_null(expected) == null: failures.append("Preview missing %s" % expected)
		for specimen_node in specimens.get_children():
			var specimen: Node3D = specimen_node as Node3D
			if specimen != null and _has_demo_node(specimen): failures.append("Preview specimen %s includes Checker/demo geometry" % specimen.name)
		var prior_bounds: Dictionary = {}
		for lod in 4:
			preview.set("_lod", lod)
			for specimen_node in specimens.get_children():
				var selected_specimen: Node3D = specimen_node as Node3D
				if selected_specimen != null: preview.call("_select_lod", selected_specimen, lod)
			await process_frame
			for specimen_node in specimens.get_children():
				var specimen: Node3D = specimen_node as Node3D
				if specimen == null: continue
				var bounds := _visible_world_bounds(specimen)
				if _active_lod_roots(specimen, lod) != 1 or bounds.size.length() <= 0.01: failures.append("%s LOD%d needs exactly one active representation with geometry" % [specimen.name, lod])
				else:
					print("TREE_LOD %s lod=%d origin=%s size=%s" % [specimen.name, lod, bounds.position, bounds.size])
					if bounds.size.y < 1.0 or bounds.size.y > 60.0: failures.append("%s LOD%d is outside the expected 1-60m tree scale" % [specimen.name, lod])
					if absf(bounds.position.y) > maxf(2.0, bounds.size.y * 0.35): failures.append("%s LOD%d is severely off ground" % [specimen.name, lod])
					var center: Vector3 = bounds.get_center()
					var crown_radius: float = maxf(bounds.size.x, bounds.size.z) * 0.75 + 1.0
					var center_offset := Vector2(center.x - specimen.global_position.x, center.z - specimen.global_position.z).length()
					if center_offset > crown_radius: failures.append("%s LOD%d has a misplaced horizontal bounds center" % [specimen.name, lod])
					var up: Vector3 = specimen.global_transform.basis.y.normalized()
					if absf(up.dot(Vector3.UP)) < 0.5: failures.append("%s LOD%d has a severe orientation issue" % [specimen.name, lod])
					var prior: AABB = prior_bounds.get(specimen.name, AABB())
					if lod > 0 and prior.size.y > 0.01:
						var height_ratio: float = maxf(bounds.size.y / prior.size.y, prior.size.y / bounds.size.y)
						if height_ratio > 3.0: failures.append("%s LOD%d has discontinuous height" % [specimen.name, lod])
						if absf(center.y - prior.get_center().y) > maxf(2.0, maxf(bounds.size.y, prior.size.y) * 0.35): failures.append("%s LOD%d has discontinuous vertical center" % [specimen.name, lod])
					prior_bounds[specimen.name] = bounds
		_validate_foliage(specimens, failures)
	preview.queue_free()
	# Let the preview's _exit_tree restore its viewport state before a capture
	# instance establishes its own manual-LOD threshold.
	await process_frame

func _lod_anchors(node: Node) -> Dictionary:
	var regex := RegEx.new(); regex.compile(LOD_PATTERN)
	var result: Dictionary = {}
	for candidate in _descendants(node):
		if not candidate is Node3D or str(candidate.name).contains("_Mat_0"): continue
		var match := regex.search(str(candidate.name))
		if match != null: result["%s:%s" % [match.get_string(1), match.get_string(2)]] = {"node": candidate, "variant": match.get_string(1), "lod": int(match.get_string(2))}
	return result

func _nonzero_meshes(node: Node) -> int:
	var count := 0
	for candidate in _descendants(node):
		if candidate is MeshInstance3D and (candidate as MeshInstance3D).mesh != null and (candidate as MeshInstance3D).get_aabb().size.length() > 0.0001: count += 1
	return count

func _active_lod_roots(specimen: Node, lod: int) -> int:
	var count := 0
	for entry in _lod_anchors(specimen).values():
		if _visible_mesh_count(entry.node):
			if entry.lod != lod: return -1
			count += 1
	return count

func _visible_mesh_count(node: Node) -> int:
	var count := 0
	for candidate in _descendants(node):
		if candidate is MeshInstance3D and (candidate as MeshInstance3D).visible and (candidate as MeshInstance3D).mesh != null: count += 1
	return count

func _visible_world_bounds(node: Node) -> AABB:
	var found := false; var result := AABB()
	for candidate in _descendants(node):
		if not candidate is MeshInstance3D or not (candidate as MeshInstance3D).visible: continue
		var mesh := candidate as MeshInstance3D; var local := mesh.get_aabb()
		for x in [local.position.x, local.end.x]:
			for y in [local.position.y, local.end.y]:
				for z in [local.position.z, local.end.z]:
					var point := mesh.global_transform * Vector3(x, y, z)
					result = AABB(point, Vector3.ZERO) if not found else result.expand(point); found = true
	return result

func _has_demo_node(node: Node) -> bool:
	for candidate in _descendants(node):
		if str(candidate.name).contains("Checker") or str(candidate.name).contains("Demo"): return true
	return false

func _validate_foliage(specimens: Node3D, failures: Array[String]) -> void:
	var foliage := 0
	var retained_fir_cutoff := false
	for candidate in _descendants(specimens):
		if not candidate is MeshInstance3D: continue
		var mesh := candidate as MeshInstance3D
		if mesh.mesh == null: continue
		for surface in mesh.mesh.get_surface_count():
			var material := mesh.get_active_material(surface) as BaseMaterial3D
			if material != null and material.transparency != BaseMaterial3D.TRANSPARENCY_DISABLED:
				foliage += 1
				var source_material := mesh.mesh.surface_get_material(surface) as BaseMaterial3D
				if material.transparency != BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR or material.albedo_texture == null or material.normal_texture == null: failures.append("Foliage material %s is missing alpha-scissor/albedo/normal" % mesh.name)
				elif source_material != null and ((source_material.albedo_texture != null and material.albedo_texture != source_material.albedo_texture) or (source_material.normal_texture != null and material.normal_texture != source_material.normal_texture)):
					failures.append("Foliage material %s did not preserve its source textures" % mesh.name)
				if mesh.name.contains("Christmas tree"):
					for cutoff: float in FIR_MASK_CUTOFFS:
						if is_equal_approx(material.alpha_scissor_threshold, cutoff): retained_fir_cutoff = true
	if foliage == 0: failures.append("Preview has no foliage materials")
	if not retained_fir_cutoff: failures.append("Fir's authored MASK cutoff was not retained")

func _capture_preview(failures: Array[String]) -> void:
	var packed := load(PREVIEW) as PackedScene
	if packed == null: failures.append("Preview unavailable for capture"); return
	var preview := packed.instantiate() as Node3D; get_root().add_child(preview)
	await RenderingServer.frame_post_draw; await RenderingServer.frame_post_draw
	if not is_zero_approx(get_root().get_viewport().mesh_lod_threshold): failures.append("Preview did not disable automatic mesh LODs for capture")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)); preview.call("_update_status"); _capture("gallery_lod0", failures)
	preview.call("_focus_species", "Fir"); preview.call("_update_status"); await RenderingServer.frame_post_draw; _capture("fir_closeup_lod0", failures)
	preview.set("_lod", 3)
	for specimen in preview.get_node("Specimens").get_children(): preview.call("_select_lod", specimen, 3)
	preview.get_node("CameraTarget").position = Vector3.ZERO
	preview.set("_distance", 24.0)
	preview.call("_update_camera")
	preview.call("_update_status"); await RenderingServer.frame_post_draw; _capture("gallery_lod3", failures); preview.queue_free(); await process_frame

func _capture(label: String, failures: Array[String]) -> void:
	var image := get_root().get_viewport().get_texture().get_image(); var path := "%s/%s.png" % [OUTPUT, label]
	if image == null or image.is_empty() or image.get_width() < 64 or image.get_height() < 64 or image.save_png(ProjectSettings.globalize_path(path)) != OK: failures.append("Could not write GPU capture: %s" % path)

func _descendants(node: Node) -> Array[Node]:
	var result: Array[Node] = []
	for child in node.get_children(): result.append(child); result.append_array(_descendants(child))
	return result
