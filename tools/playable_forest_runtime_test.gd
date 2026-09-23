extends SceneTree

const FOREST := preload("res://scenes/PlayableForest.tscn")
var tour: Node
const OUTPUT := "res://.godot/forest_playable"

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	tour = FOREST.instantiate()
	root.add_child(tour)
	var route := {}
	for frame in range(900):
		route = tour.call("GetRouteDiagnostics")
		if bool(route.get("ready", false)):
			break
		await process_frame
	if not bool(route.get("ready", false)):
		fail("seeded route did not resolve")
		return
	var world := tour.get_node_or_null("VoxelRingIntegrationTest")
	var decorations := world.get_node_or_null("BiomeDecorations") if world else null
	if not world or not decorations:
		fail("production integration scene or decoration layer missing")
		return
	if decorations.has_method("GetImportedForestRuntimeDiagnostics"):
		var diagnostics: Dictionary = {}
		for frame in range(900):
			diagnostics = decorations.call("GetImportedForestRuntimeDiagnostics")
			if int(diagnostics.get("canonical_record_count", 0)) > 0 and int(diagnostics.get("collision_count", 0)) > 0 and forest_renderer_ready(diagnostics) and explorer_grounded(world):
				break
			await process_frame
		if int(diagnostics.get("variant_count", 0)) != 30 or int(diagnostics.get("authored_lod_count", 0)) != 4 or not forest_renderer_ready(diagnostics) or int(diagnostics.get("canonical_record_count", 0)) < 1 or int(diagnostics.get("collision_count", 0)) < 1 or not explorer_grounded(world):
			fail("production imported forest diagnostics incomplete: %s" % diagnostics)
			return
		if not await verify_trunk_physics(world): return
		if "--capture" in OS.get_cmdline_user_args():
			if not await capture_route(world, route): return
	else:
		fail("production imported forest diagnostics API missing")
		return
	print("PLAYABLE_FOREST_RUNTIME=PASS route=(%d,%d)->(%d,%d)->(%d,%d)" % [
		int(route.start_around), int(route.start_across), int(route.woodland_around),
		int(route.woodland_across), int(route.grove_around), int(route.grove_across)])
	tour.queue_free()
	quit(0)

func fail(message: String) -> void:
	push_error("PLAYABLE_FOREST_RUNTIME=FAIL " + message)
	if tour: tour.queue_free()
	quit(1)

func forest_renderer_ready(diagnostics: Dictionary) -> bool:
	# Exact indirect counts are checked by gpu_tree_integration_test.gd.
	# Gameplay does not stall the render thread to read them back.
	if bool(diagnostics.get("gpu_active", false)):
		return int(diagnostics.get("gpu_batch_count", 0)) > 0 and int(diagnostics.get("gpu_resident_root_count", 0)) > 0
	return int(diagnostics.get("visible_instance_count", 0)) > 0

func verify_trunk_physics(world: Node) -> bool:
	var trunks := world.find_children("ImportedTreeTrunk_*", "StaticBody3D", true, false)
	if trunks.is_empty():
		fail("no installed imported trunk collision body")
		return false
	await physics_frame
	var trunk := trunks[0] as StaticBody3D
	var up := trunk.global_transform.basis.y.normalized()
	var cylinder := trunk.get_node_or_null("Trunk") as CollisionShape3D
	var half_height := (cylinder.shape as CylinderShape3D).height * .5 if cylinder and cylinder.shape is CylinderShape3D else 4.0
	var query := PhysicsRayQueryParameters3D.create(trunk.global_position + up * (half_height + 1.0), trunk.global_position - up * (half_height + 1.0))
	var hit := trunk.get_world_3d().direct_space_state.intersect_ray(query)
	if hit.is_empty() or hit.collider != trunk:
		fail("physics ray missed imported trunk: %s" % hit)
		return false
	print("PLAYABLE_FOREST_TRUNK_PHYSICS=PASS ", trunk.name)
	return true

func capture_route(world: Node, route: Dictionary) -> bool:
	if RenderingServer.get_rendering_device() == null:
		fail("--capture requires a Forward+ rendering device")
		return false
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	var explorer := world.get_node_or_null("Explorer") as CharacterBody3D
	var grid := world.get_node_or_null("ChunkGridManager")
	if not explorer or not grid:
		fail("capture world missing explorer or terrain")
		return false
	var worst_frame_ms := 0.0
	for stop in [["clearing", route.start_around, route.start_across], ["woodland", route.woodland_around, route.woodland_across], ["grove", route.grove_around, route.grove_across]]:
		if not bool(tour.call("WarpToRoutePoint", int(stop[1]), int(stop[2]))):
			fail("route warp failed: " + str(stop))
			return false
		var last_usec := Time.get_ticks_usec()
		var grounded_frames := 0
		var loaded := {}
		for frame in range(480):
			await physics_frame
			await RenderingServer.frame_post_draw
			var now_usec := Time.get_ticks_usec()
			if frame >= 30: worst_frame_ms = maxf(worst_frame_ms, (now_usec - last_usec) / 1000.0)
			last_usec = now_usec
			loaded = world.get_node("BiomeDecorations").call("GetImportedForestRuntimeDiagnostics")
			grounded_frames = grounded_frames + 1 if explorer_grounded(world) else 0
			if frame >= 90 and grounded_frames >= 5 and forest_renderer_ready(loaded): break
		if grounded_frames < 5 or not forest_renderer_ready(loaded):
			await capture_failure(world, str(stop[0]), loaded, grounded_frames)
			fail("capture stop did not reach stable grounded terrain and visible trees: %s" % loaded)
			return false
		var image := root.get_texture().get_image()
		var path := OUTPUT.path_join(str(stop[0]) + ".png")
		if image == null or image.is_empty() or image.save_png(path) != OK:
			fail("capture write failed: " + path)
			return false
		print("PLAYABLE_FOREST_CAPTURE=PASS ", path)
	print("PLAYABLE_FOREST_FRAME_STATS worst_frame_ms=%.3f final_fps=%.1f" % [worst_frame_ms, Engine.get_frames_per_second()])
	return true

func explorer_grounded(world: Node) -> bool:
	var explorer := world.get_node_or_null("Explorer") as CharacterBody3D
	return explorer != null and explorer.is_on_floor()

func capture_failure(world: Node, label: String, diagnostics: Dictionary, grounded_frames: int) -> void:
	var explorer := world.get_node_or_null("Explorer")
	var image := root.get_texture().get_image()
	if image != null and not image.is_empty(): image.save_png(OUTPUT.path_join(label + "_unsettled.png"))
	var position: Vector3 = explorer.global_position if explorer else Vector3.ZERO
	var velocity: Vector3 = explorer.velocity if explorer is CharacterBody3D else Vector3.ZERO
	var up: Vector3 = explorer.call("GetCanonicalSurfaceUp") if explorer and explorer.has_method("GetCanonicalSurfaceUp") else Vector3.ZERO
	var minimum_trunk_distance := INF
	for trunk in world.find_children("ImportedTreeTrunk_*", "StaticBody3D", true, false):
		minimum_trunk_distance = minf(minimum_trunk_distance, position.distance_to((trunk as Node3D).global_position))
	var settlement: Dictionary = tour.call("GetTourSettlementDiagnostics") if tour else {}
	push_error("PLAYABLE_FOREST_CAPTURE_UNSETTLED label=%s grounded_frames=%d world=%s velocity=%s up=%s nearest_trunk_m=%.3f settlement=%s diagnostics=%s" % [label, grounded_frames, position, velocity, up, minimum_trunk_distance, settlement, diagnostics])
