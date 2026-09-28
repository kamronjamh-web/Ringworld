extends SceneTree

# Synthetic canonical flight through the real controller, scheduler and native
# publication path. This is isolated coverage/performance evidence, not M6.
const PLAYABLE := preload("res://scenes/TerrainGenerationPlayable.tscn")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const STORE := preload("res://scripts/Terrain/Generation/terrain_generation_preview_store.gd")
const SETTINGS := preload("res://scripts/ui/GameplaySurfaceTerrainSettings.cs")
var _failures := PackedStringArray()
var _done := false
var _completed := false
var _speed := 108.0
var _workers := 2
var _force_late := false
var _directory := ""
var _report: Dictionary = {}
var _playable: Node3D
var _surface: Node
var _player: CharacterBody3D
var _stream: RefCounted
var _driving := false
var _start_around := 0.0
var _target_distance := 0.0
var _travelled := 0.0
var _frames: Array[float] = []
var _last_frame := 0
var _segment: Dictionary = {}
var _last_revision := -1
var _last_probe := 0
var _route_started := 0

func _init() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--speed="): _speed = float(argument.trim_prefix("--speed="))
		if argument.begins_with("--workers="): _workers = clampi(int(argument.trim_prefix("--workers=")), 1, 4)
		if argument == "--force-late": _force_late = true
	_directory = "res://.godot/terrain-generation-tests/m4-travel/%d-%d" % [int(_speed), _workers]
	if _force_late: _directory += "-late"
	call_deferred("_run")
	create_timer(120.0).timeout.connect(func():
		if not _done:
			_failures.append("120 second watchdog expired")
			_finish())

func _run() -> void:
	if not is_finite(_speed) or _speed <= 0:
		_failures.append("speed must be positive and finite")
		_finish()
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_directory))
	var recipe_path := _directory.path_join("recipe.tres")
	var recipe_error: String = STORE.save_recipe(RECIPE.new(), recipe_path)
	if not recipe_error.is_empty(): _failures.append(recipe_error)
	var settings := SETTINGS.new()
	settings.GenerationWorkerCount = _workers
	settings.GenerationCacheMiB = 8
	settings.GenerationColliderTileCells = 16 if _force_late else 32
	settings.GenerationCollisionBudgetMs = .1 if _force_late else .75
	if ResourceSaver.save(settings, _directory.path_join("playable_preferences.tres")) != OK:
		_failures.append("isolated preferences could not be written")
	_playable = PLAYABLE.instantiate()
	_playable.configure_test_fixture(recipe_path, 0, 159000)
	root.add_child(_playable)
	var deadline := Time.get_ticks_msec() + 30000
	while not _playable.get_preview_diagnostics().ready and Time.get_ticks_msec() < deadline:
		await process_frame
	if not _playable.get_preview_diagnostics().ready:
		_failures.append("initial terrain timed out")
		_finish()
		return
	_stream = _playable.get("_stream")
	if int(_stream.get("_workers")) != _workers:
		_failures.append("requested worker count was not applied")
	_surface = _playable.get_node("RingTerrain3DSurface")
	_player = _playable.get_node("Player") as CharacterBody3D
	_playable.set("_flying", true)
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	await physics_frame
	await process_frame
	_report = {
		"synthetic_mapped_flight": true, "speed_mps": _speed, "workers": _workers,
		"upload_budget_ms": 1.0, "collision_budget_ms": settings.GenerationCollisionBudgetMs, "tile_cells": settings.GenerationColliderTileCells,
		"hardware": {"processor": OS.get_processor_name(), "logical_cpus": OS.get_processor_count(),
			"gpu": RenderingServer.get_video_adapter_name(), "driver": RenderingServer.get_current_rendering_driver_name(),
			"render_resolution": root.get_texture().get_size(), "window_resolution": DisplayServer.window_get_size()},
		"gpu_timestamps": "unavailable; frame intervals include engine scheduling and presentation",
		"environment": "Isolated playable preview; no production weather, water or vegetation",
		"cold": {}, "warm": {},
	}
	physics_frame.connect(_drive_physics)
	process_frame.connect(_observe_frame)
	_report.cold = await _fly(1024.0, "cold_out")
	_report.warm = await _fly(-1024.0, "warm_return")
	if _force_late and int(_report.cold.edge_waits) + int(_report.warm.edge_waits) == 0:
		_failures.append("late-coverage fixture did not exercise the supported boundary pause")
	_completed = true
	_finish()

func _fly(distance: float, label: String) -> Dictionary:
	var position: PackedFloat64Array = _surface.call("GetCanonicalPosition", _player.global_position)
	_start_around = position[0]
	_target_distance = distance
	_travelled = 0.0
	_frames.clear()
	_last_frame = Time.get_ticks_usec()
	_route_started = _last_frame
	_last_probe = 0
	var diagnostics: Dictionary = _playable.get_preview_diagnostics()
	_last_revision = int(diagnostics.committed_revision)
	_segment = {"segment": label, "edge_waits_before": diagnostics.edge_waits,
		"max_inflight": 0, "max_pending": 0, "max_cache_bytes": 0, "max_native_residents": 0,
		"max_static_memory_bytes": 0, "support_checks": 0, "support_failures": 0, "publications": []}
	_driving = true
	var deadline := Time.get_ticks_msec() + 90000
	while _driving and Time.get_ticks_msec() < deadline:
		await process_frame
	_driving = false
	if Time.get_ticks_msec() >= deadline: _failures.append(label + " timed out")
	_segment["requested_distance_m"] = distance
	_segment["actual_distance_m"] = _travelled
	_segment["wall_duration_ms"] = float(Time.get_ticks_usec() - _route_started) / 1000.0
	_segment["frame_interval_ms"] = _stats(_frames)
	_segment["edge_waits"] = int(_playable.get_preview_diagnostics().edge_waits) - int(_segment.edge_waits_before)
	_segment.erase("edge_waits_before")
	if absf(_travelled - distance) > .05: _failures.append(label + " did not travel requested distance")
	if int(_segment.support_failures) > 0 or int(_segment.support_checks) < 10: _failures.append(label + " terrain support verification failed")
	if int(_segment.max_inflight) > _workers or int(_segment.max_pending) > 9 or int(_segment.max_cache_bytes) > 8 * 1024 * 1024 or int(_segment.max_native_residents) > 2:
		_failures.append(label + " resident or scheduler bounds exceeded")
	return _segment.duplicate(true)

# Emitted before the controller's physics callback. Read the previous tick's
# clamped result before applying the next requested displacement.
func _drive_physics() -> void:
	if not _driving: return
	var position: PackedFloat64Array = _surface.call("GetCanonicalPosition", _player.global_position)
	_travelled = _around_delta(position[0], _start_around)
	var remaining := absf(_target_distance) - _travelled * signf(_target_distance)
	if remaining <= .01:
		_driving = false
		return
	var delta_m := minf(remaining, _speed * _playable.get_physics_process_delta_time()) * signf(_target_distance)
	var next_around := position[0] + delta_m
	var height: float = _surface.call("SampleCommittedHeight", next_around, position[1])
	if not is_finite(height):
		_failures.append("next physics displacement has no active height")
		_driving = false
		return
	_player.global_position = _surface.call("MapCanonicalPosition", next_around, position[1], height + 4.0)

# This records every rendered process interval independently of physics ticks.
func _observe_frame() -> void:
	if not _driving: return
	var now := Time.get_ticks_usec()
	_frames.append(float(now - _last_frame) / 1000.0)
	_last_frame = now
	var diagnostics: Dictionary = _playable.get_preview_diagnostics()
	var stream: Dictionary = diagnostics.stream
	var native: Dictionary = diagnostics.native
	_segment.max_inflight = maxi(_segment.max_inflight, int(stream.inflight))
	_segment.max_pending = maxi(_segment.max_pending, int(stream.pending))
	_segment.max_cache_bytes = maxi(_segment.max_cache_bytes, int(stream.cache_bytes))
	_segment.max_native_residents = maxi(_segment.max_native_residents, int(native.active_resident_count) + int(native.staged_resident_count) + int(native.idle_resident_count))
	_segment.max_static_memory_bytes = maxi(_segment.max_static_memory_bytes, int(Performance.get_monitor(Performance.MEMORY_STATIC)))
	if int(diagnostics.committed_revision) != _last_revision:
		_last_revision = int(diagnostics.committed_revision)
		_segment.publications.append({"revision": _last_revision, "readiness_ms": diagnostics.readiness_ms,
			"sampling_accumulated_ms": diagnostics.sampling_ms, "packing_ms": diagnostics.packing_ms,
			"projection_ms": diagnostics.projection_ms, "cache_hits": diagnostics.cache_hits,
			"native": {"upload_ms": native.upload_ms, "shape_ms": native.shape_ms, "installation_ms": native.installation_ms,
				"native_setup_ms": native.native_setup_ms, "upload_bytes": native.upload_bytes}})
	if now - _last_probe >= 50000:
		_last_probe = now
		_segment.support_checks += 1
		var position: PackedFloat64Array = _surface.call("GetCanonicalPosition", _player.global_position)
		var height: float = _surface.call("SampleCommittedHeight", position[0], position[1])
		var covered := absf(_around_delta(position[0], float(diagnostics.anchor_around))) <= 176.01 and absf(position[1] - float(diagnostics.anchor_across)) <= 176.01
		if not covered or not is_finite(height) or not _ground_ray(): _segment.support_failures += 1
		if not str(diagnostics.error).is_empty() and not _failures.has(str(diagnostics.error)): _failures.append(str(diagnostics.error))

func _ground_ray() -> bool:
	var ray := PhysicsRayQueryParameters3D.create(_player.global_position + Vector3.UP * 5, _player.global_position + Vector3.DOWN * 24, 1)
	ray.exclude = [_player.get_rid()]
	return not _playable.get_world_3d().direct_space_state.intersect_ray(ray).is_empty()

func _around_delta(value: float, anchor: float) -> float:
	return fposmod(value - anchor + RECIPE.CERTIFIED_CIRCUMFERENCE_M * .5, RECIPE.CERTIFIED_CIRCUMFERENCE_M) - RECIPE.CERTIFIED_CIRCUMFERENCE_M * .5

func _stats(values: Array) -> Dictionary:
	if values.is_empty(): return {}
	var sorted := values.duplicate(); sorted.sort(); var count := sorted.size()
	return {"count": count, "p50": sorted[clampi(int(ceil(count * .5)) - 1, 0, count - 1)],
		"p95": sorted[clampi(int(ceil(count * .95)) - 1, 0, count - 1)], "p99": sorted[clampi(int(ceil(count * .99)) - 1, 0, count - 1)], "max": sorted.back()}

func _finish() -> void:
	if _done: return
	_done = true
	_driving = false
	if is_instance_valid(_playable): _playable.free()
	if _stream != null:
		_report["stream_after_exit"] = _stream.get_diagnostics()
		if _report.stream_after_exit.tasks_submitted != _report.stream_after_exit.tasks_joined: _failures.append("scheduler tasks were not joined")
	if not _completed: _failures.append("benchmark did not complete")
	_report["completed"] = _completed
	_report["failures"] = _failures
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_directory))
	var file := FileAccess.open(_directory.path_join("report.json"), FileAccess.WRITE)
	if file == null: _failures.append("report could not be written")
	else:
		file.store_string(JSON.stringify(_report, "  "))
		file.close()
	if _failures.is_empty():
		print("TERRAIN_GENERATION_TRAVEL=PASS ", JSON.stringify({"speed_mps": _speed, "workers": _workers}))
		quit(0)
	else:
		push_error("TERRAIN_GENERATION_TRAVEL=FAIL " + "; ".join(_failures))
		quit(1)
