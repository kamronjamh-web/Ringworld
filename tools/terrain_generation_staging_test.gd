extends SceneTree

const SURFACE := preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const WINDOW := preload("res://scripts/Terrain/Generation/terrain_generation_window.gd")
const OUTPUT := "res://.godot/terrain-generation-tests/staging-report.json"
var checks := 0
var failures := PackedStringArray()
var complete := false
var done := false

func _init() -> void:
	call_deferred("_run")
	create_timer(45).timeout.connect(_timeout)

func _run() -> void:
	var surface = SURFACE.new()
	root.add_child(surface)
	surface.SetMeshLods(3)
	surface.SetMeshSize(24)
	var first := _fixture(10.0)
	_expect(surface.CommitPreparedWindow(first.h, first.i, 0, 1000, 0.0, 1, 1, true), "base commit")
	await _physics_sync()
	var old_body: StaticBody3D = surface.find_child("RingTerrainSurfaceCollision", true, false)
	_expect(_ray_hits(surface, Transform3D.IDENTITY, 10.0, old_body), "base ground ray")
	var second := _fixture(20.0)
	_expect(surface.StagePreparedWindow(second.h, second.i, 0, 1000, 0.0, 1, 2, true), "stage commit")
	var candidate := _candidate(surface)
	if not _expect(candidate != null, "stage owns a candidate"):
		_finish()
		return
	var body: StaticBody3D = candidate.find_child("RingTerrainSurfaceCollision", true, false)
	_expect(body != null and body.collision_layer == 0 and body.collision_mask == 0, "candidate shape is already built but noncollidable")
	var shape := body.get_child(0) as CollisionShape3D
	var shape_id := shape.shape.get_instance_id()
	_expect(not candidate.is_visible_in_tree(), "candidate is hidden")
	_expect(int(candidate.TerrainNode.get("mesh_lods")) == 3 and int(candidate.TerrainNode.get("mesh_size")) == 24, "stage inherits saved native mesh settings")
	_expect(_same(surface.SampleCommittedHeight(0, 1000), 10.0), "stage retains old active height")
	_expect(_same(surface.SampleStagedHeight(0, 1000), 20.0), "staged height query")
	var diag: Dictionary = surface.GetPreparedDiagnostics()
	_expect(diag.ready and diag.revision == 1 and diag.render_revision == 1 and diag.query_revision == 1 and diag.collision_revision == 1, "staging retains every active revision")
	_expect(diag.staged_ready and diag.staged_resident_count == 1 and diag.active_resident_count == 1, "candidate resident diagnostic")
	await _physics_sync()
	_expect(_ray_hits(surface, Transform3D.IDENTITY, 10.0, old_body), "staged ground cannot intercept active ray")
	_expect(not surface.StagePreparedWindow(first.h, first.i, 0, 1000, 0.0, 1, 1, true), "stale stage rejected")
	var invalid: PackedFloat32Array = second.h.duplicate()
	invalid[0] = NAN
	_expect(not surface.StagePreparedWindow(invalid, second.i, 0, 1000, 0.0, 1, 3, true), "invalid newer stage rejected")
	_expect(_candidate(surface) == candidate and surface.GetPreparedDiagnostics().staged_revision == 2, "invalid input retains valid staged resources")
	_expect(not str(surface.GetPreparedDiagnostics().error).is_empty(), "staging failure is diagnosable")
	_expect(not surface.ActivateStagedWindow(999), "wrong activation revision rejected")
	_expect(not surface.CommitPreparedWindow(first.h, first.i, 0, 1000, 0.0, 1, 3, true), "direct mutation rejected while a stage exists")
	_expect(surface.CanActivateStagedWindow(0, 1000, 16.0), "candidate coverage")
	_expect(surface.CanActivateStagedWindow(31415936, 1000, 16.0), "coverage wraps circumference")
	_expect(surface.CanActivateStagedWindow(176.0005, 1000, 16.0), "coverage tolerates physical float round-trip error at the margin")
	_expect(not surface.CanActivateStagedWindow(192.0005, 1000, 0.0), "round-trip allowance never expands actual collider coverage")
	_expect(not surface.CanActivateStagedWindow(177, 1000, 16.0), "safe edge excludes unsupported activation")
	_expect(not surface.CanActivateStagedWindow(NAN, 1000, 16.0) and not surface.CanActivateStagedWindow(0, 1000, NAN), "nonfinite coverage rejected")
	var world := Transform3D(Basis(Vector3.FORWARD, .25), Vector3(17, 9, 5))
	surface.SetWorldFrame(world)
	surface.SetMeshLods(2)
	surface.SetMeshSize(32)
	surface.SetHandoffDistance(84.0)
	surface.UpdateCamera(Vector3(20, 30, 0))
	_expect(body.transform.is_equal_approx(world) and old_body.transform.is_equal_approx(world), "rebase updates active and staged collision")
	_expect(int(candidate.TerrainNode.get("mesh_lods")) == 2 and int(surface.TerrainNode.get("mesh_lods")) == 2 and int(candidate.TerrainNode.get("mesh_size")) == 32 and int(surface.TerrainNode.get("mesh_size")) == 32, "mesh tuning updates both residents")
	await _physics_sync()
	_expect(_ray_hits(surface, world, 10.0, old_body), "old ground remains live after staged rebase")
	_expect(surface.ActivateStagedWindow(2), "first activation")
	_expect(shape.shape.get_instance_id() == shape_id, "activation reuses already-built shape")
	_expect(body.collision_layer == 1 and body.collision_mask == 1 and candidate.is_visible_in_tree(), "activation enables candidate")
	_expect(not is_instance_valid(old_body), "old collider retires after handoff")
	await _physics_sync()
	_expect(_same(surface.SampleCommittedHeight(0, 1000), 20.0), "activation publishes staged height")
	_expect(_ray_hits(surface, world, 20.0, body), "new ground ray matches committed revision")
	diag = surface.GetPreparedDiagnostics()
	_expect(diag.revision == 2 and diag.render_revision == 2 and diag.query_revision == 2 and diag.collision_revision == 2 and diag.staged_resident_count == 0, "activation publishes all revisions together")
	_expect(str(diag.error).is_empty(), "successful activation clears rejected request errors")
	_expect(surface.get_child_count() == 1, "facade owns one active child")
	var next := _fixture(30.0)
	_expect(surface.StagePreparedWindow(next.h, next.i, 256, 1000, 0.0, 1, 3, true), "later frame stage")
	_expect(not surface.CanActivateStagedWindow(0, 1000, 16.0) and surface.CanActivateStagedWindow(128, 1000, 16.0), "recenter waits for overlap")
	_expect(surface.get_child_count() == 2, "at most active and staged residents")
	surface.DiscardStagedWindow()
	_expect(surface.GetPreparedDiagnostics().staged_resident_count == 0, "discard removes stage")
	_expect(surface.get_child_count() == 1 and _same(surface.SampleCommittedHeight(0, 1000), 20.0), "discard retains only playable active resident")
	_expect(surface.StagePreparedWindow(next.h, next.i, 0, 1000, 0.0, 1, 4, true), "stage after discard")
	var next_body: StaticBody3D = _candidate(surface).find_child("RingTerrainSurfaceCollision", true, false)
	_expect(surface.ActivateStagedWindow(4), "second activation")
	_expect(not is_instance_valid(body) and surface.get_child_count() == 1, "second activation retires first child")
	await _physics_sync()
	_expect(_ray_hits(surface, world, 30.0, next_body), "second active collision uses forwarded world frame")
	surface.SetMeshLods(3)
	surface.SetMeshSize(48)
	_expect(int(surface.TerrainNode.get("mesh_lods")) == 3 and int(surface.TerrainNode.get("mesh_size")) == 48, "active facade mesh tuning")
	_expect(_same(surface.GetNativeHeight(Vector3.ZERO), 30.0), "active facade native query")
	var mapped: Vector3 = surface.MapCanonicalPosition(20.25, 1004.5, 35.0)
	var inverse: PackedFloat64Array = surface.GetCanonicalPosition(mapped)
	_expect(absf(inverse[0] - 20.25) < .001 and absf(inverse[1] - 1004.5) < .001, "active facade frame mapping")
	var empty = SURFACE.new()
	root.add_child(empty)
	_expect(empty.StagePreparedWindow(first.h, first.i, 0, 1000, 0.0, 1, 1, true), "initial empty owner stages")
	_expect(not empty.IsReady and is_nan(empty.SampleCommittedHeight(0, 1000)), "empty owner is unready until activation")
	_expect(empty.ActivateStagedWindow(1) and empty.IsReady, "empty owner activates")
	empty.free()
	surface.free()
	_expect(not is_instance_valid(next_body), "scene teardown frees resident collider")
	complete = true
	_finish()

func _fixture(height: float) -> Dictionary:
	var h := PackedFloat32Array()
	h.resize(WINDOW.SIDE * WINDOW.SIDE)
	h.fill(height)
	return {"h": h, "i": WINDOW.pack(h, 0.0)}

func _candidate(surface: Node) -> Node3D:
	for child in surface.get_children():
		if child.get_script() == SURFACE and not child.visible:
			return child
	return null

func _physics_sync() -> void:
	await physics_frame
	await process_frame

func _ray_hits(surface: Node3D, world: Transform3D, height: float, expected_body: StaticBody3D) -> bool:
	var ray := PhysicsRayQueryParameters3D.create(world * Vector3(0, 60, 0), world * Vector3(0, -20, 0), 1)
	var hit := surface.get_world_3d().direct_space_state.intersect_ray(ray)
	return not hit.is_empty() and hit.collider == expected_body and (world.affine_inverse() * Vector3(hit.position)).distance_to(Vector3(0, height, 0)) < .01

func _same(a: float, b: float) -> bool:
	return absf(a - b) < .001

func _expect(value: bool, label: String) -> bool:
	checks += 1
	if not value:
		failures.append(label)
	return value

func _timeout() -> void:
	if not done:
		failures.append("watchdog")
		_finish()

func _finish() -> void:
	if done:
		return
	done = true
	if not complete: failures.append("test did not complete all stages")
	if checks < 45: failures.append("too few checks")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/terrain-generation-tests"))
	var f := FileAccess.open(OUTPUT, FileAccess.WRITE)
	if f != null:
		f.store_string(JSON.stringify({"checks": checks, "complete": complete, "failures": failures}))
		f.close()
	else:
		failures.append("could not write report")
	if complete and failures.is_empty():
		print("TERRAIN_GENERATION_STAGING=PASS ", JSON.stringify({"checks": checks}))
		quit(0)
		return
	push_error("TERRAIN_GENERATION_STAGING=FAIL " + "; ".join(failures))
	quit(1)
