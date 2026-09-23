extends SceneTree

## Certified Ring A/B visual diagnostic.  This deliberately changes only the
## instantiated test scene; it is not a terrain or water repair.
const TEST_SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE: PackedScene = preload("res://scenes/WorldSizeLauncher.tscn")
const WATER_PROBE: Script = preload("res://scripts/Tools/WaterRenderingProbe.cs")
const SHARED_EDGE_PROBE: Script = preload("res://tools/terrain_water_shared_edge_probe.gd")
const ARTIFACT_DIRECTORY: String = "res://.godot/terrain_water_seam_diagnostic"
const DEFAULTS_PATH: String = ARTIFACT_DIRECTORY + "/isolated_water_defaults.tres"
const BASELINE_PATH: String = ARTIFACT_DIRECTORY + "/certified_shore_with_skirts.png"
const SKIRTLESS_PATH: String = ARTIFACT_DIRECTORY + "/certified_shore_without_skirts.png"
const REPORT_PATH: String = ARTIFACT_DIRECTORY + "/certified_terrain_water_seam_report.json"
const TERRAIN_SHADER_PATH: String = "res://materials/ring_terrain_proxy.gdshader"
const PROXY_SECTOR_SCRIPT_PATH: String = "res://scripts/Voxel/LOD/RingTerrainProxySector.cs"
const TIMEOUT_SECONDS: float = 180.0
const QUIET_SETTLE_FRAMES: int = 120

var _scene: Node
var _drawer: Node
var _probe: RefCounted
var _shore: Dictionary = {}
var _started_usec: int = 0
var _quiet_frames: int = 0
var _phase: String = "boot"
var _report: Dictionary = {}

func _initialize() -> void:
	_started_usec = Time.get_ticks_usec()
	_boot.call_deferred()

func _boot() -> void:
	_started_usec = Time.get_ticks_usec()
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(ARTIFACT_DIRECTORY))
	DirAccess.remove_absolute(ProjectSettings.globalize_path(DEFAULTS_PATH))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_scene = TEST_SCENE.instantiate()
	var saves: Node = _scene.get_node_or_null("WorldSaveManager")
	if saves != null:
		saves.free()
	_drawer = _scene.get_node_or_null("LiveTuningDrawer")
	if _drawer == null:
		_fail("missing LiveTuningDrawer")
		return
	# Install fresh defaults at the isolated res://.godot path.  This neither
	# reads nor writes the player's user:// preference file.
	_drawer.call("InstallWaterDefaults", DEFAULTS_PATH)
	var launcher: Node = LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _scene, 5000000.0)
	launcher.free()
	root.add_child(_scene)
	_report = {
		"preset": "Certified Ring",
		"requested_radius_m": 5000000.0,
		"water_defaults": {"mode": "fresh_isolated_defaults", "path": DEFAULTS_PATH},
		"artifacts": {"baseline": BASELINE_PATH, "skirts_removed": SKIRTLESS_PATH, "report": REPORT_PATH},
		"initialization": {"world_save_manager_removed_before_add": true, "water_defaults_installed": false},
	}

func _process(_delta: float) -> bool:
	if not is_instance_valid(_scene): return false
	if _elapsed_seconds() > TIMEOUT_SECONDS:
		return _fail("timed out during " + _phase)
	if _phase == "boot":
		if not _water_defaults_ready():
			return false
		_probe = WATER_PROBE.new()
		_shore = _probe.call("FindOceanShore", _scene)
		if not bool(_shore.get("valid", false)):
			return _fail("FindOceanShore found no ocean-to-dry transition")
		if not bool(_probe.call("PlaceShoreCamera", _scene, _shore, 8.0)):
			return _fail("PlaceShoreCamera could not position 8m offshore")
		_freeze_explorer()
		_disable_nonessential_presentation()
		var focus: Dictionary = _shore.duplicate(true)
		focus["offshore_meters"] = 8.0
		_report["focus"] = focus
		var grid: Node = _scene.get_node_or_null("ChunkGridManager")
		if grid != null:
			_report["certified_dimensions"] = {"canonical_circumference_m": grid.call("GetCanonicalRingCircumferenceMeters64"), "across_width_m": grid.get("RingAcrossWidthMeters")}
		_report["initialization"]["water_defaults_installed"] = true
		_phase = "settling"
		return false
	if _phase == "settling":
		var settling: Dictionary = _settling_status()
		_quiet_frames = _quiet_frames + 1 if bool(settling["quiet"]) else 0
		if _quiet_frames < QUIET_SETTLE_FRAMES:
			return false
		_report["settling"] = {"status": "settled", "quiet_frames": _quiet_frames, "counters": settling, "cap_seconds": TIMEOUT_SECONDS}
		_freeze_scene_processes()
		_phase = "capturing"
		_capture_pair.call_deferred()
		return false
	return false

func _capture_pair() -> void:
	# _process must remain a synchronous SceneTree callback.  Keep all frame
	# awaits in this deferred coroutine so its return value cannot terminate the
	# tree before the second capture/report are produced.
	await RenderingServer.frame_post_draw
	var baseline_dimensions: Dictionary = _capture(BASELINE_PATH)
	if baseline_dimensions.is_empty():
		_fail("could not capture baseline")
		return
	_report["capture_dimensions"] = baseline_dimensions
	var camera: Camera3D = root.get_camera_3d()
	if camera == null:
		_fail("missing active camera for shared-edge probe")
		return
	var edge_probe: RefCounted = SHARED_EDGE_PROBE.new()
	var edge_report: Dictionary = edge_probe.call("inspect", _scene, camera.global_position)
	_report["water_shared_edges"] = edge_report
	if not bool(edge_report.get("pass", false)):
		_fail("inconclusive or discontinuous nearby same-LOD terrain/water shared edges")
		return
	print("CERTIFIED_TERRAIN_WATER_SHARED_EDGES=PASS %s" % JSON.stringify(edge_report.get("per_lod_meters", {})))
	var inventory: Dictionary = _inventory_and_remove_skirts()
	_report["proxy_inventory"] = inventory
	if int(inventory["proxy_sector_mesh_instances"]) <= 0 or int(inventory["terrain_candidate_surfaces"]) <= 0 or int(inventory["ocean_surfaces"]) <= 0 or int(inventory["removed_terrain_skirt_triangles"]) <= 0 or int(inventory["invalid_coverage_uv_skirt_vertices"]) > 0 or int(inventory["legacy_negative_uv_vertices"]) > 0:
		_fail("inconclusive proxy inventory: expected terrain proxy, ocean surface, and removable skirt triangles")
		return
	# Allow the replacement ArrayMeshes to reach the renderer before capture B.
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	if _capture(SKIRTLESS_PATH).is_empty():
		_fail("could not capture skirtless scene")
		return
	_report["capture_limitations"] = "Scene processes were frozen between captures; GPU shader TIME may still advance between rendered frames. Visual outcome is intentionally not inferred programmatically."
	_report["status"] = "completed_diagnostic_only"
	if not _write_report():
		_fail("could not write JSON report")
		return
	print("CERTIFIED_TERRAIN_WATER_SEAM_CAPTURE=PASS report=%s" % REPORT_PATH)
	_cleanup()
	quit(0)

func _water_defaults_ready() -> bool:
	var diagnostics: Dictionary = _drawer.call("GetWaterTuningDiagnostics")
	return str(diagnostics.get("path", "")) == DEFAULTS_PATH

func _freeze_explorer() -> void:
	var explorer: Node = _scene.get_node_or_null("Explorer")
	if explorer == null:
		return
	explorer.set_physics_process(false)
	explorer.set_process_unhandled_input(false)
	explorer.set_process_input(false)
	explorer.call("SetPresentationView", 18.0, -38.0)

func _disable_nonessential_presentation() -> void:
	# Keep presentation live while streaming settles.  Every scene descendant is
	# frozen immediately before the A/B captures instead.
	pass

func _settling_status() -> Dictionary:
	var coordinator: Node = _scene.get_node_or_null("TerrainLodCoordinator")
	var grid: Node = _scene.get_node_or_null("ChunkGridManager")
	var smooth: Node = _scene.get_node_or_null("SmoothTerrainGrid")
	var binding: Dictionary = _probe.call("GetFftBindingStatus", _scene)
	var coordinator_workers: int = -1 if coordinator == null else int(coordinator.get("ActiveWorkerCount"))
	var coordinator_uploads: int = -1 if coordinator == null else int(coordinator.get("PendingUploadCount"))
	var grid_quiet: bool = grid == null or (int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0)
	var smooth_quiet: bool = smooth == null or bool(smooth.get("IsQuiescent"))
	var water_ready: bool = bool(binding.get("controller_ready", false)) and int(binding.get("geometry_meshes", 0)) > 0
	return {"quiet": coordinator != null and coordinator_workers == 0 and coordinator_uploads == 0 and grid_quiet and smooth_quiet and water_ready, "coordinator_active_workers": coordinator_workers, "coordinator_pending_uploads": coordinator_uploads, "grid_quiet": grid_quiet, "smooth_terrain_quiescent": smooth_quiet, "water_binding": binding, "water_ready": water_ready}

func _freeze_scene_processes() -> void:
	_scene.process_mode = Node.PROCESS_MODE_DISABLED
	for node: Node in _scene.find_children("*", "", true, false):
		node.process_mode = Node.PROCESS_MODE_DISABLED

func _capture(path: String) -> Dictionary:
	var image: Image = root.get_texture().get_image()
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(path)) != OK:
		return {}
	return {"width": image.get_width(), "height": image.get_height()}

func _inventory_and_remove_skirts() -> Dictionary:
	var candidate_instances: int = 0
	var candidate_surfaces: int = 0
	var negative_uv_vertices: int = 0
	var proxy_sector_instances: int = 0
	var coverage_atlas_rect_nonzero: int = 0
	var ocean_surfaces: int = 0
	var skirt_triangles: int = 0
	var removed_triangles: int = 0
	var invalid_coverage_uv_skirt_vertices: int = 0
	var depth_min: float = INF
	var depth_max: float = -INF
	var camera: Camera3D = root.get_camera_3d()
	for node: Node in _scene.find_children("*", "MeshInstance3D", true, false):
		if node.get_parent() == null or not _is_proxy_sector(node.get_parent()):
			continue
		# Structural closures reuse the terrain shader but are not sector skirts.
		if not str(node.name).begins_with("Sector_"): continue
		var instance: MeshInstance3D = node as MeshInstance3D
		if not instance.is_visible_in_tree() or instance.mesh == null: continue
		# Bound the diagnostic to the nearby view the user reported. Test-only
		# replacement of far, unseen sectors adds allocation without evidence.
		if camera != null:
			var bounds: AABB = instance.global_transform * instance.mesh.get_aabb()
			var nearest: Vector3 = camera.global_position.clamp(bounds.position, bounds.end)
			if camera.global_position.distance_to(nearest) > 1500.0: continue
		proxy_sector_instances += 1
		var coverage_rect: Variant = instance.get_instance_shader_parameter("coverage_atlas_rect")
		if typeof(coverage_rect) == TYPE_VECTOR4 and coverage_rect != Vector4.ZERO:
			coverage_atlas_rect_nonzero += 1
		var source: ArrayMesh = instance.mesh as ArrayMesh
		if source == null:
			continue
		var replacement: ArrayMesh = ArrayMesh.new()
		var changed: bool = false
		for surface_index: int in range(source.get_surface_count()):
			var arrays: Array = source.surface_get_arrays(surface_index)
			var material: Material = source.surface_get_material(surface_index)
			if not _is_terrain_surface(material):
				if _is_ocean_surface(material):
					ocean_surfaces += 1
				replacement.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
				replacement.surface_set_material(replacement.get_surface_count() - 1, material)
				continue
			candidate_surfaces += 1
			var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			var uvs: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV]
			var uv2s: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV2]
			var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
			if indices.is_empty():
				indices = PackedInt32Array(range(vertices.size()))
			var retained: PackedInt32Array = PackedInt32Array()
			var found_surface_skirt: bool = false
			var negative_vertex_indices: Dictionary = {}
			var skirt_vertex_indices: Dictionary = {}
			for index: int in indices:
				if index >= 0 and index < uvs.size() and uvs[index].is_equal_approx(Vector2(-1.0, -1.0)):
					negative_vertex_indices[index] = true
			negative_uv_vertices += negative_vertex_indices.size()
			for i: int in range(0, indices.size() - 2, 3):
				var a: int = indices[i]
				var b: int = indices[i + 1]
				var c: int = indices[i + 2]
				var skirt: bool = _skirt_vertex(uv2s, a) or _skirt_vertex(uv2s, b) or _skirt_vertex(uv2s, c)
				if skirt:
					found_surface_skirt = true
					skirt_triangles += 1
					removed_triangles += 1
					for vertex_index: int in [a, b, c]:
						skirt_vertex_indices[vertex_index] = true
						if vertex_index >= 0 and vertex_index < vertices.size():
							depth_min = minf(depth_min, vertices[vertex_index].y)
							depth_max = maxf(depth_max, vertices[vertex_index].y)
				else:
					retained.append_array(PackedInt32Array([a, b, c]))
			for skirt_index_variant: Variant in skirt_vertex_indices:
				var skirt_index: int = int(skirt_index_variant)
				if not _valid_coverage_uv(uvs, skirt_index):
					invalid_coverage_uv_skirt_vertices += 1
			arrays[Mesh.ARRAY_INDEX] = retained
			replacement.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
			replacement.surface_set_material(replacement.get_surface_count() - 1, material)
			if found_surface_skirt:
				changed = true
		if changed:
			candidate_instances += 1
			instance.mesh = replacement
			var shadow_caster: MeshInstance3D = instance.get_node_or_null("OpaqueShadowCaster") as MeshInstance3D
			if shadow_caster != null:
				shadow_caster.mesh = replacement
	return {"proxy_sector_mesh_instances": proxy_sector_instances, "coverage_atlas_rect_nonzero_instances": coverage_atlas_rect_nonzero, "terrain_surface_instances_with_skirts": candidate_instances, "terrain_candidate_surfaces": candidate_surfaces, "ocean_surfaces": ocean_surfaces, "legacy_negative_uv_vertices": negative_uv_vertices, "skirt_triangles_uv2_marker": skirt_triangles, "removed_terrain_skirt_triangles": removed_triangles, "invalid_coverage_uv_skirt_vertices": invalid_coverage_uv_skirt_vertices, "local_vertex_y_range_across_axis": [] if depth_min == INF else [depth_min, depth_max], "opaque_shadow_casters_replaced_with_filtered_mesh": candidate_instances}

func _is_terrain_surface(material: Material) -> bool:
	var shader_material: ShaderMaterial = material as ShaderMaterial
	return shader_material != null and shader_material.shader != null and shader_material.shader.resource_path == TERRAIN_SHADER_PATH

func _is_ocean_surface(material: Material) -> bool:
	var shader_material: ShaderMaterial = material as ShaderMaterial
	return shader_material != null and shader_material.shader != null and shader_material.shader.resource_path == "res://materials/ring_ocean_proxy.gdshader"

func _is_proxy_sector(node: Node) -> bool:
	var script: Script = node.get_script() as Script
	return script != null and script.resource_path == PROXY_SECTOR_SCRIPT_PATH

func _negative_uv(uvs: PackedVector2Array, index: int) -> bool:
	return index >= 0 and index < uvs.size() and uvs[index].is_equal_approx(Vector2(-1.0, -1.0))

func _valid_coverage_uv(uvs: PackedVector2Array, index: int) -> bool:
	if index < 0 or index >= uvs.size(): return false
	var uv: Vector2 = uvs[index]
	return uv.is_finite() and uv.x >= 0.0 and uv.x <= 1.0 and uv.y >= 0.0 and uv.y <= 1.0

func _skirt_vertex(uv2s: PackedVector2Array, index: int) -> bool:
	return index >= 0 and index < uv2s.size() and uv2s[index].y > 0.5

func _write_report() -> bool:
	var file: FileAccess = FileAccess.open(ProjectSettings.globalize_path(REPORT_PATH), FileAccess.WRITE)
	if file == null:
		return false
	file.store_string(JSON.stringify(_report, "\t") + "\n")
	return file.get_error() == OK

func _elapsed_seconds() -> float:
	return float(Time.get_ticks_usec() - _started_usec) / 1000000.0

func _cleanup() -> void:
	if is_instance_valid(_scene):
		_scene.queue_free()

func _fail(reason: String) -> bool:
	_report["status"] = "failed"
	_report["failure"] = reason
	_write_report()
	push_error("CERTIFIED_TERRAIN_WATER_SEAM_CAPTURE=FAIL " + reason)
	_cleanup()
	quit(1)
	return true
