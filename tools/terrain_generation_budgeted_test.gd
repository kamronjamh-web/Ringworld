extends SceneTree

const SURFACE := preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const GEOMETRY := preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
const WINDOW := preload("res://scripts/Terrain/Generation/terrain_generation_window.gd")
const OUTPUT := "res://.godot/terrain-generation-tests/budgeted-report.json"
const SIDE := 384
var _checks := 0
var _failures := PackedStringArray()
var _complete := false
var _finished := false

func _init() -> void:
	call_deferred("_run")
	create_timer(120.0).timeout.connect(_timeout)

func _run() -> void:
	var surface = SURFACE.new()
	root.add_child(surface)
	var first := _fixture(10.0)
	_expect(surface.CommitPreparedWindow(first.heights, first.images, 0, 1000, 10.0, 1, 1, true), "base direct window")
	await _physics_sync()
	_expect(_ray_height(surface, 0, 1000, 10.0), "base curved ray")
	var second := _fixture(20.0)
	var geometry = _geometry(second.heights, 0, 1000, 20.0, 1, 16)
	_expect(geometry.GetDiagnostics().tile_count == 144, "16-cell geometry has 144 tiles")
	_expect(geometry.GetDiagnostics().geometry_bytes == 192 * 192 * 6 * 12, "bounded CPU geometry payload")
	_expect(surface.BeginBudgetedPreparedWindow(second.heights, second.images, geometry, 0, 1000, 20.0, 1, 2), "begin budgeted stage")
	var candidate: Node3D = _candidate(surface)
	_expect(candidate != null and not candidate.is_visible_in_tree(), "stage starts hidden")
	_expect(_same(surface.SampleCommittedHeight(0, 1000), 10.0) and _ray_height(surface, 0, 1000, 10.0), "old terrain stays live while staged")
	var zero: Dictionary = surface.AdvanceBudgetedPreparation(0.0, 0.0)
	_expect(zero.tiles_done == 0 and zero.regions_done == 0 and zero.units_last == 0 and not zero.ready, "zero budgets pause preparation")
	_expect(not surface.AdvanceBudgetedPreparation(NAN, 1.0).ok, "nonfinite budget rejected")
	_expect(not surface.ActivateStagedWindow(2), "incomplete stage cannot activate")
	var upload_bounded := true
	for i in 12:
		var status: Dictionary = surface.AdvanceBudgetedPreparation(.000001, 0.0)
		upload_bounded = upload_bounded and status.units_last <= 1 and status.tiles_done == 0
	_expect(upload_bounded, "tiny upload budget starts at most one unit per call")
	var invalid_images: Array[Image] = [second.images[0]]
	_expect(not surface.BeginBudgetedPreparedWindow(second.heights, invalid_images, geometry, 0, 1000, 20.0, 1, 3), "invalid RF rejected")
	var wrong := _fixture(30.0)
	_expect(not surface.BeginBudgetedPreparedWindow(wrong.heights, wrong.images, geometry, 0, 1000, 30.0, 1, 3), "geometry mismatch rejected")
	_expect(_candidate(surface) == candidate and surface.GetPreparedDiagnostics().staged_revision == 2, "rejected input preserves candidate identity and revision")
	await _drain(surface, .000001)
	_expect(not candidate.is_visible_in_tree() and _body(candidate).collision_layer == 0, "complete candidate stays hidden and noncollidable")
	_expect(surface.CanActivateStagedWindow(0, 1000, 16.0), "completed stage covers activation point")
	_expect(surface.ActivateStagedWindow(2), "activate completed budgeted stage")
	await _physics_sync()
	_expect(_same(surface.SampleCommittedHeight(0, 1000), 20.0) and _ray_height(surface, 0, 1000, 20.0), "activated query and collider agree")
	var first_native_id: int = surface.TerrainNode.get_instance_id()
	var first_region = surface.TerrainNativeData.call("get_region", Vector2i.ZERO)
	var first_height_id: int = first_region.call("get_height_map").get_instance_id()
	var color_map: Image = first_region.call("get_color_map")
	var control_map: Image = first_region.call("get_control_map")
	var color_id := color_map.get_instance_id()
	var control_id := control_map.get_instance_id()
	color_map.set_pixel(5, 6, Color(.2, .4, .6, 1.0))
	var painted_color := color_map.get_pixel(5, 6)
	var control_bytes := control_map.get_data()
	_expect(_body(_active(surface)).get_child_count() == 144, "16-cell active collider count")
	_expect(_face_count(_body(_active(surface))) == 192 * 192 * 6, "tiles cover original triangle count")
	_expect(_seam_rays(surface, 0, 1000, 20.0, false, 1, Transform3D.IDENTITY), "16-cell tile edges and corners have ray support")
	var third := _fixture(30.125, true)
	var third_geometry = _geometry(third.heights, 256, 1000, 30.125, 1, 32)
	_expect(third_geometry.GetDiagnostics().tile_count == 36, "32-cell geometry has 36 tiles")
	_expect(surface.BeginBudgetedPreparedWindow(third.heights, third.images, third_geometry, 256, 1000, 30.125, 1, 3), "later budgeted stage")
	await _drain(surface)
	_expect(surface.ActivateStagedWindow(3), "later activation")
	await _physics_sync()
	var second_native_id: int = surface.TerrainNode.get_instance_id()
	_expect(first_native_id != second_native_id, "two distinct native owners")
	_expect(_resident_bound(surface), "active plus idle resident bound")
	_expect(_body(_active(surface)).get_child_count() == 36, "32-cell active collider count")
	_expect(_seam_rays(surface, 256, 1000, 30.125, true, 1, Transform3D.IDENTITY), "fractional sloped terrain agrees at tile boundaries and outer edge")
	# Partially replace the idle owner's existing 144 tiles, then cancel.
	var partial := _fixture(40.0)
	var partial_geometry = _geometry(partial.heights, 512, 1000, 40.0, 1, 16)
	_expect(surface.BeginBudgetedPreparedWindow(partial.heights, partial.images, partial_geometry, 512, 1000, 40.0, 1, 4), "partial stage begins")
	var partial_status: Dictionary = surface.AdvanceBudgetedPreparation(1000.0, 0.0)
	for i in 3: partial_status = surface.AdvanceBudgetedPreparation(0.0, .000001)
	_expect(partial_status.tiles_done == 3 and not partial_status.ready, "cancellation fixture installs exactly three inactive tiles")
	await _physics_sync()
	_expect(_same(surface.SampleCommittedHeight(256, 1000), 30.125) and _ray_height(surface, 256, 1000, 30.125), "partial shape replacement preserves active coverage")
	surface.DiscardStagedWindow()
	_expect(not surface.ActivateStagedWindow(4), "discarded candidate cannot activate")
	_expect(_resident_bound(surface) and surface.GetPreparedDiagnostics().idle_resident_count == 1, "discard retains only one bounded inactive owner")
	var final_fixture := _fixture(50.25, true)
	var final_geometry = _geometry(final_fixture.heights, -256, 317000, 50.25, -1, 32)
	_expect(surface.BeginBudgetedPreparedWindow(final_fixture.heights, final_fixture.images, final_geometry, -256, 317000, 50.25, -1, 5), "replacement after discard begins")
	_expect(not surface.BeginBudgetedPreparedWindow(final_fixture.heights, final_fixture.images, final_geometry, -256, 317000, 50.25, -1, 5), "equal staged revision rejected")
	await _drain(surface)
	var world := Transform3D(Basis(Vector3.FORWARD, .5), Vector3(125, -75, 250))
	surface.SetWorldFrame(world)
	_expect(surface.ActivateStagedWindow(5), "wrapped reversed activation")
	await _physics_sync()
	_expect(_same(surface.SampleCommittedHeight(-256, 317000), 50.25), "wrapped reversed height query")
	_expect(_seam_rays(surface, -256, 317000, 50.25, true, -1, world), "transformed wrapped reversed tile rays")
	var reused_region = surface.TerrainNativeData.call("get_region", Vector2i.ZERO)
	_expect(surface.TerrainNode.get_instance_id() == first_native_id, "first native owner reused after swap and cancellation")
	_expect(reused_region.call("get_height_map").get_instance_id() == first_height_id, "resident height image identity reused")
	var reused_color: Image = reused_region.call("get_color_map")
	var reused_control: Image = reused_region.call("get_control_map")
	_expect(reused_color.get_instance_id() == color_id and reused_color.get_pixel(5, 6) == painted_color, "height publication preserves painted color image and value")
	_expect(reused_control.get_instance_id() == control_id and reused_control.get_data() == control_bytes, "height publication preserves control image and bytes")
	_expect(_body(_active(surface)).get_child_count() == 36, "reused owner retires excess 16-cell shapes")
	var reuse_fixture := _fixture(60.0)
	var reuse_geometry = _geometry(reuse_fixture.heights, 0, 1000, 60.0, 1, 16)
	_expect(surface.BeginBudgetedPreparedWindow(reuse_fixture.heights, reuse_fixture.images, reuse_geometry, 0, 1000, 60.0, 1, 6), "second owner reuse begins")
	await _drain(surface)
	_expect(surface.ActivateStagedWindow(6), "second owner reuse activation")
	await _physics_sync()
	_expect(surface.TerrainNode.get_instance_id() == second_native_id, "second native owner reused")
	_expect(_body(_active(surface)).get_child_count() == 144 and _seam_rays(surface, 0, 1000, 60.0, false, 1, world), "reused owner grows back to 144 supported tiles")
	var diag: Dictionary = surface.GetPreparedDiagnostics()
	_expect(diag.revision == 6 and diag.render_revision == 6 and diag.query_revision == 6 and diag.collision_revision == 6, "all published revisions agree")
	_expect(diag.preparation.pending_geometry_bytes == 0 and diag.upload_bytes == SIDE * SIDE * 4, "finished preparation releases CPU geometry and reports height upload bytes")
	_expect(_resident_bound(surface), "resident bound after repeated swaps")
	var active_body := _body(_active(surface))
	surface.free()
	_expect(not is_instance_valid(active_body), "teardown frees active collider")
	_complete = true
	_finish()

func _fixture(height: float, slope: bool = false) -> Dictionary:
	var heights := PackedFloat32Array()
	heights.resize(SIDE * SIDE)
	for z in SIDE:
		for x in SIDE:
			heights[z * SIDE + x] = height + ((x - 128) * .03125 + (z - 128) * .015625 if slope else 0.0)
	return {"heights": heights, "images": WINDOW.pack(heights, height)}

func _geometry(heights: PackedFloat32Array, around: int, across: int, height: float, direction: int, cells: int):
	var geometry = GEOMETRY.new()
	_expect(geometry.Prepare(heights, around, across, height, direction, cells), "prepare private collision geometry")
	return geometry

func _drain(surface: Node, collision_budget: float = 2.0) -> void:
	var deadline := Time.get_ticks_msec() + 15000
	var bounded := true
	var status: Dictionary = {}
	while Time.get_ticks_msec() < deadline:
		status = surface.AdvanceBudgetedPreparation(2.0, collision_budget)
		if not status.ok: break
		bounded = bounded and _resident_bound(surface)
		if collision_budget < .001 and status.regions_done == 9 and status.phase in ["collision", "trim", "finish", "ready"]:
			bounded = bounded and status.units_last <= 1
		if status.ready: break
		await process_frame
	_expect(bool(status.get("ok", false)) and bool(status.get("ready", false)), "preparation completes without error or timeout")
	_expect(bounded, "preparation respects resident and requested unit bounds")

func _candidate(surface: Node) -> Node3D:
	for child in surface.get_children():
		if child.get_script() == SURFACE and not child.visible:
			return child
	return null

func _active(surface: Node) -> Node3D:
	return surface.TerrainNode.get_parent()

func _body(owner: Node) -> StaticBody3D:
	return owner.get_node_or_null("RingTerrainSurfaceCollision") as StaticBody3D

func _resident_bound(surface: Node) -> bool:
	var diag: Dictionary = surface.GetPreparedDiagnostics()
	return int(diag.active_resident_count) + int(diag.staged_resident_count) + int(diag.idle_resident_count) <= 2

func _face_count(body: StaticBody3D) -> int:
	var count := 0
	for shape: CollisionShape3D in body.get_children(): count += shape.shape.get_faces().size()
	return count

func _seam_rays(surface: Node, around: int, across: int, height: float, slope: bool, direction: int, world: Transform3D) -> bool:
	for x in [-191.5, -160.0, -128.0, -64.0, 0.0, 32.0, 128.0, 191.5]:
		for z in [-191.5, -160.0, -128.0, 0.0, 64.0, 160.0, 191.5]:
			var expected: float = height + (x * .015625 + z * .0078125 if slope else 0.0)
			if not _ray_height(surface, around + x, across + direction * z, expected, world): return false
	return true

func _ray_height(surface: Node3D, around: float, across: float, expected: float, world: Transform3D = Transform3D.IDENTITY) -> bool:
	var point: Vector3 = surface.MapCanonicalPosition(around, across, expected)
	var query := PhysicsRayQueryParameters3D.create(world * (point + Vector3.UP * 80), world * (point - Vector3.UP * 80), 1)
	var hit: Dictionary = surface.get_world_3d().direct_space_state.intersect_ray(query)
	if hit.is_empty(): return false
	var canonical: PackedFloat64Array = surface.GetCanonicalPosition(world.affine_inverse() * Vector3(hit.position))
	return absf(canonical[2] - expected) < .05 and hit.collider == _body(_active(surface))

func _physics_sync() -> void:
	await physics_frame
	await process_frame

func _same(a: float, b: float) -> bool:
	return absf(a - b) < .001

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition: _failures.append(label)

func _timeout() -> void:
	if not _finished:
		_failures.append("watchdog expired")
		_finish()

func _finish() -> void:
	if _finished: return
	_finished = true
	if not _complete: _failures.append("fixture did not complete")
	if _checks < 50: _failures.append("too few checks")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT.get_base_dir()))
	var file := FileAccess.open(OUTPUT, FileAccess.WRITE)
	if file == null: _failures.append("could not write report")
	else:
		file.store_string(JSON.stringify({"checks": _checks, "complete": _complete, "failures": _failures}, "  "))
		file.close()
	if _complete and _failures.is_empty():
		print("TERRAIN_GENERATION_BUDGETED=PASS ", JSON.stringify({"checks": _checks}))
		quit(0)
	else:
		push_error("TERRAIN_GENERATION_BUDGETED=FAIL " + "; ".join(_failures))
		quit(1)
