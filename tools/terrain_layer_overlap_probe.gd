extends SceneTree

# Inspect actual installed triangles, independently of the ownership counters.
# Disposable, no saved world, usable headlessly or with Vulkan for an image.
const SOURCE = preload("res://scripts/Voxel/Phase2/VoxelChunkGridManager.cs")
const SMOOTH = preload("res://scripts/Terrain/Editable/SmoothTerrainGrid.cs")
var source: Node3D
var terrain: Node3D
var focus: Node3D
var harness: Node3D
var failed := false
var baseline := false
var driver: RefCounted
var certified := false
var around_offset := 0
var across_offset := 0

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	baseline = "--expect-existing-overlap" in OS.get_cmdline_user_args()
	certified = "--terrain-overlap-certified" in OS.get_cmdline_user_args()
	if certified:
		around_offset = 29795500 - 167
		across_offset = 52100 - 135
	if "--terrain-overlap-wrap" in OS.get_cmdline_user_args():
		around_offset = -160
	driver = load("res://tools/TerrainLayerOverlapProbe.cs").new()
	harness = Node3D.new()
	root.add_child(harness)
	# By default isolate the production mesher on a small ring without caves;
	# optional Certified mode uses the real preset and screenshot coordinates.
	var template := preload("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
	if certified:
		var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
		root.add_child(launcher)
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", template, 5000000.0)
		launcher.free()
	var settings: Resource = template.get_node("ChunkGridManager").get("TerrainSettings").duplicate(true)
	var configured_source: Node3D = template.get_node("ChunkGridManager")
	if certified: template.remove_child(configured_source)
	for pair in [["Seed", 7], ["BaseSurfaceHeightVoxels", 8], ["HeightAmplitudeVoxels", 0], ["DetailAmplitudeVoxels", 0], ["EnableUnified3dDensityTerrain", false], ["EnableDesignedCaveDensityComposition", false]]:
		settings.set(pair[0], pair[1])
	source = configured_source if certified else SOURCE.new()
	source.name = "Source"
	for pair in [["BuildOnReady", false], ["DormantEditableBackend", true], ["EnableChunkStreaming", false], ["TerrainSettings", settings], ["UseDeterministicTerrainGeneration", true], ["UseRingCurvedPlacement", true], ["AroundRingChunkLength", 128], ["RingAcrossWidthMeters", 512.0], ["EnforceRingAcrossBounds", true]]:
		if not certified: source.set(pair[0], pair[1])
	if not certified:
		source.set("UseHabitableRingAxisLayout", true)
		source.set("AroundRingChunkLength", 1280)
		source.set("RingRadiusMeters", 40960.0 / TAU)
	source.set("RunAroundRingSeamSelfTestsOnReady", false)
	source.set("EnableCanonicalFloatingOriginAnchor", true)
	template.free()
	harness.add_child(source)
	driver.call("Anchor", source, around_offset + 167, height_at(167, 135), across_offset + 135)
	focus = Node3D.new()
	focus.name = "Focus"
	harness.add_child(focus)
	focus.global_position = world_point(167, height_at(167, 135) + 101, 135)
	terrain = SMOOTH.new()
	terrain.name = "Smooth"
	for pair in [["SourceGridPath", NodePath("../Source")], ["StreamingFocusPath", NodePath("../Focus")], ["RadiusAround", 1], ["RadiusAcross", 1], ["RadiusVertical", 0], ["CollisionRadius", 0], ["EnableFocusCenteredVisualCoverage", true], ["EnableMediumVolumetricVisualCoverage", true], ["MediumVisualRadiusAround", 1], ["MediumVisualRadiusAcross", 1], ["EnableHotChunkPatches", false], ["MaxWorkers", 2], ["StreamingUpdateIntervalSeconds", 0.01]]:
		terrain.set(pair[0], pair[1])
	harness.add_child(terrain)
	if not await settle(): return
	if not audit("surface-only fine coverage", 0): return
	# Horizontal movement changes partial 32m/64m coverage at both edges.
	focus.global_position = world_point(199, height_at(199, 135) + 101, 135)
	if not await settle(): return
	if not audit("moved one fine chunk", 32): return
	# Descend to the ground: fine surface and near coverage now share entries,
	# including edit-ready 16m hot patches, without double drawing medium.
	terrain.set("EnableHotChunkPatches", true)
	focus.global_position = world_point(199, height_at(199, 135) + 2, 135)
	if not await settle(): return
	if not audit("near surface with hot patches", 32): return
	# Exercise a floating-origin shift without regenerating or re-authoring terrain.
	driver.call("Anchor", source, around_offset + 215, height_at(199, 135), across_offset + 151)
	focus.global_position = world_point(199, height_at(199, 135) + 2, 135)
	if not await settle(): return
	if not audit("floating-origin rebase", 32): return
	if RenderingServer.get_rendering_device() != null: await capture_gpu()
	print("TERRAIN_LAYER_TRIANGLE_PROBE=PASS baseline=", baseline, " certified=", certified)
	harness.queue_free()
	for i in range(10): await process_frame
	quit(0)

func settle() -> bool:
	for i in range(8): await process_frame
	var deadline := Time.get_ticks_msec() + 120000
	while not bool(terrain.get("IsQuiescent")) and Time.get_ticks_msec() < deadline:
		await process_frame
	return check(bool(terrain.get("IsQuiescent")), "terrain did not settle")

func audit(label: String, shift: int) -> bool:
	var meshes: Array[MeshInstance3D] = []
	for node in terrain.find_children("*", "MeshInstance3D", true, false):
		if node.is_visible_in_tree() and node.mesh != null:
			meshes.append(node)
	var duplicates := 0
	var holes := 0
	if baseline and not meshes.is_empty():
		print("PROBE_MESH_SAMPLE=", source.call("WorldToCanonicalRingPosition", meshes[0].global_transform * meshes[0].get_aabb().get_center()), " focus=", source.call("WorldToCanonicalRingPosition", focus.global_position))
	for a in [110, 142, 174, 206, 238]:
		for z in [78, 110, 142, 174, 206]:
			var height := height_at(a + shift, z)
			var start := world_point(a + shift, height + 6, z)
			var finish := world_point(a + shift, height - 6, z)
			var owners := 0
			for node in meshes:
				var inverse := node.global_transform.affine_inverse()
				var ray_start := inverse * start
				var ray_direction := ((inverse * finish) - ray_start).normalized()
				if node.get_aabb().intersects_ray(ray_start, ray_direction) == null: continue
				var arrays: Array = node.mesh.surface_get_arrays(0)
				var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
				var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
				for i in range(0, indices.size(), 3):
					var hit = Geometry3D.ray_intersects_triangle(ray_start, ray_direction, vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]])
					if hit != null and ray_start.distance_to(hit) <= start.distance_to(finish):
						owners += 1
						break
			if owners > 1: duplicates += 1
			if owners == 0: holes += 1
	print("TERRAIN_LAYER_TRIANGLES label=", label, " duplicate_rays=", duplicates, " holes=", holes, " probes=25 meshes=", meshes.size())
	return check(holes == 0 and (duplicates > 0 if baseline else duplicates == 0), label + " unexpected ownership")

func world_point(a: float, y: float, z: float) -> Vector3:
	return driver.call("WorldPoint", source, a + around_offset, y, z + across_offset)

func height_at(a: int, z: int) -> int:
	return int(driver.call("SurfaceHeight", source, a + around_offset, z + across_offset))

func capture_gpu() -> void:
	var env := WorldEnvironment.new()
	env.environment = Environment.new()
	env.environment.background_mode = Environment.BG_COLOR
	env.environment.background_color = Color(0.05, 0.07, 0.12)
	env.environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.environment.ambient_light_color = Color.WHITE
	env.environment.ambient_light_energy = 1.0
	harness.add_child(env)
	var camera := Camera3D.new()
	camera.far = 1000
	harness.add_child(camera)
	var center := world_point(199, height_at(199, 135), 135)
	camera.global_position = world_point(199, height_at(199, 135) + 220, 135)
	camera.look_at(center, Vector3.UP)
	camera.make_current()
	for i in range(8): await RenderingServer.frame_post_draw
	root.get_texture().get_image().save_png("res://.godot/terrain_overlap_%s.png" % ("certified" if certified else "small_ring"))

func check(ok: bool, reason: String) -> bool:
	if not ok and not failed:
		failed = true
		push_error("TERRAIN_LAYER_TRIANGLE_PROBE=FAIL " + reason)
		quit(1)
	return ok
