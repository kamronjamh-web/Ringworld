class_name TerrainGenerationStream
extends RefCounted

const JOB := preload("res://scripts/Terrain/Generation/terrain_generation_job.gd")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")

const PRIORITY: Array[int] = [4, 3, 1, 0, 5, 2, 7, 6, 8]

var _workers := 2
var _cache_mib := 8
var _tile_cells := 32
var _cache := {}
var _cache_lru: Array[String] = []
var _cache_bytes := 0
var _cache_compat := ""
var _cache_hits := 0
var _cache_misses := 0
var _cache_evictions := 0
var _cache_peak_pending := 0
var _request: Dictionary = {}
var _token
var _pending: Array[int] = []
var _tasks: Array[Dictionary] = [] # Retains cancelled tasks until poll joins them.
var _pack_task: Dictionary = {}
var _ready: Dictionary = {}
var _last_revision := 0
var _submitted := 0
var _joined := 0
var _cancelled := 0
var _stale := 0
var _high_water := 0
var _error := ""
var _closed := false

func configure(workers: int, cache_mib: int, tile_cells: int = 32) -> void:
	_workers = clampi(workers, 1, 4)
	_cache_mib = clampi(cache_mib, 1, 64)
	_tile_cells = 16 if tile_cells == 16 else 32
	_evict_cache()

func request_window(recipe: TerrainGenerationRecipe, anchor_around: int, anchor_across: int, direction: int, revision: int, recipe_epoch: int, edit_epoch: int, frame_epoch: int, edits: Dictionary = {}) -> bool:
	if _closed or recipe == null or revision <= _last_revision or recipe_epoch < 0 or edit_epoch < 0 or frame_epoch < 0:
		return false
	if not recipe.validation_errors().is_empty(): return false
	if direction not in [-1, 1] or anchor_around % 2 != 0 or anchor_across % 2 != 0:
		return false
	var first_across := anchor_across - direction * 256
	var last_across := anchor_across + direction * 510
	if min(first_across, last_across) < 0 or max(first_across, last_across) >= int(RECIPE.CERTIFIED_WIDTH_M):
		return false
	var copied_edits: Dictionary = {}
	for source_key in edits:
		if not source_key is Vector2i:
			return false
		var coordinate: Vector2i = source_key
		if not (edits[source_key] is float or edits[source_key] is int): return false
		var value: float = float(edits[source_key])
		if coordinate.x % 2 != 0 or coordinate.y % 2 != 0 or coordinate.y < 0 or coordinate.y >= int(RECIPE.CERTIFIED_WIDTH_M) or not is_finite(value) or value < -8000.0 or value > 8000.0:
			return false
		var canonical := Vector2i(posmod(coordinate.x, int(RECIPE.CERTIFIED_CIRCUMFERENCE_M)), coordinate.y)
		if copied_edits.has(canonical) and copied_edits[canonical] != value:
			return false
		copied_edits[canonical] = value
	var snapshot: Resource = recipe.snapshot()
	var compatibility := _compatibility(snapshot, recipe_epoch, edit_epoch, copied_edits)
	if not _cache_compat.is_empty() and _cache_compat != compatibility:
		_clear_cache()
	_cache_compat = compatibility
	_cancel_current()
	_last_revision = revision
	_token = JOB.Token.new()
	_request = {
		"recipe": snapshot, "anchor_around": posmod(anchor_around, int(RECIPE.CERTIFIED_CIRCUMFERENCE_M)),
		"anchor_across": anchor_across, "direction": direction, "revision": revision,
		"recipe_epoch": recipe_epoch, "edit_epoch": edit_epoch, "frame_epoch": frame_epoch,
		"edits": copied_edits, "compatibility": compatibility, "tiles": [], "sampling_ms": 0.0, "request_cache_hits": 0, "started": Time.get_ticks_usec(), "tile_cells": _tile_cells
	}
	_request.tiles.resize(9)
	_pending.assign(PRIORITY)
	_error = ""
	_cache_peak_pending = max(_cache_peak_pending, _pending.size())
	_launch_tiles()
	return true

func poll() -> Dictionary:
	_join_tiles()
	_join_pack()
	if not _request.is_empty():
		_launch_tiles()
		_schedule_pack_if_ready()
	return _take_ready()

func cancel() -> void:
	_cancel_current()

func shutdown() -> void:
	_cancel_current()
	for entry in _tasks:
		WorkerThreadPool.wait_for_task_completion(entry.id)
		_joined += 1
	_tasks.clear()
	if not _pack_task.is_empty():
		WorkerThreadPool.wait_for_task_completion(_pack_task.id)
		_joined += 1
	_pack_task.clear()
	_ready.clear()
	_clear_cache()
	_closed = true

func _cancel_current() -> void:
	if _token != null:
		_token.cancel()
		_cancelled += 1
	_pending.clear()
	_request.clear()
	_ready.clear()

func _launch_tiles() -> void:
	while not _request.is_empty() and _tasks.size() + (0 if _pack_task.is_empty() else 1) < _workers and not _pending.is_empty():
		var index: int = _pending.pop_front()
		var region_x := index % 3
		var region_z := index / 3
		var cache_key := _tile_key(region_x, region_z)
		if _cache.has(cache_key):
			_request.tiles[index] = _cache[cache_key].samples
			_request.request_cache_hits += 1
			_cache_hits += 1
			_touch_cache(cache_key)
			continue
		_cache_misses += 1
		var job = JOB.new()
		job.recipe = _request.recipe.snapshot()
		job.edits = _request.edits.duplicate()
		job.around = _request.anchor_around + (region_x - 1) * 256
		job.across = _request.anchor_across + _request.direction * (region_z - 1) * 256
		job.direction = _request.direction
		job.token = _token
		var task_id: int = WorkerThreadPool.add_task(job.run, false, "terrain-generation-tile")
		_tasks.append({"id": task_id, "job": job, "revision": _request.revision, "index": index, "cache_key": cache_key})
		_submitted += 1
	_high_water = max(_high_water, _tasks.size() + (0 if _pack_task.is_empty() else 1))

func _join_tiles() -> void:
	for i in range(_tasks.size() - 1, -1, -1):
		var entry: Dictionary = _tasks[i]
		if not WorkerThreadPool.is_task_completed(entry.id):
			continue
		WorkerThreadPool.wait_for_task_completion(entry.id)
		_joined += 1
		_tasks.remove_at(i)
		var job = entry.job
		if job.result.get("cancelled", false):
			_cancelled += 1
			continue
		if _request.is_empty() or entry.revision != _request.revision:
			_stale += 1
			continue
		if not job.result.get("ok", false):
			_publish_error(str(job.result.get("error", "worker failed")))
			continue
		_request.tiles[entry.index] = job.result.samples
		_request.sampling_ms += float(job.result.sampling_ms)
		_store_cache(entry.cache_key, job.result.samples)

func _schedule_pack_if_ready() -> void:
	if _request.is_empty() or not _pack_task.is_empty() or not _pending.is_empty() or not _tasks.is_empty():
		return
	for tile in _request.tiles:
		if tile == null:
			return
	var job = JOB.new()
	job.mode = "pack"
	job.tiles = _request.tiles.duplicate()
	job.token = _token
	job.frame_around = _request.anchor_around
	job.frame_across = _request.anchor_across
	job.direction = _request.direction
	job.tile_cells = _request.tile_cells
	_pack_task = {"id": WorkerThreadPool.add_task(job.run, false, "terrain-generation-pack"), "job": job, "revision": _request.revision}
	_submitted += 1
	_high_water = max(_high_water, _tasks.size() + 1)

func _join_pack() -> void:
	if _pack_task.is_empty() or not WorkerThreadPool.is_task_completed(_pack_task.id):
		return
	WorkerThreadPool.wait_for_task_completion(_pack_task.id)
	_joined += 1
	var entry: Dictionary = _pack_task.duplicate()
	_pack_task = {}
	var job = entry.job
	if _request.is_empty() or entry.revision != _request.revision:
		_stale += 1
		return
	if job.result.get("cancelled", false):
		_cancelled += 1
		return
	if not job.result.get("ok", false):
		_publish_error(str(job.result.get("error", "pack worker failed")))
		return
	_ready = {"ok": true, "anchor_around": _request.anchor_around, "anchor_across": _request.anchor_across, "across_direction": _request.direction, "revision": _request.revision, "recipe_epoch": _request.recipe_epoch, "edit_epoch": _request.edit_epoch, "frame_epoch": _request.frame_epoch, "sampling_ms": _request.sampling_ms, "packing_ms": float(job.result.packing_ms), "projection_ms": float(job.result.get("projection_ms", 0.0)), "geometry": job.result.geometry, "latency_ms": float(Time.get_ticks_usec() - int(_request.started)) / 1000.0, "cache_hits": _request.request_cache_hits, "absolute_heights": job.result.absolute_heights, "height_images": job.result.height_images, "frame_height": job.result.frame_height}
	_request.clear()

func _take_ready() -> Dictionary:
	if _ready.is_empty():
		return {}
	var payload := _ready
	_ready = {}
	return payload

func _publish_error(message: String) -> void:
	if _request.is_empty(): return
	_token.cancel()
	_error = message
	_ready = {"ok": false, "error": message, "revision": _request.revision, "recipe_epoch": _request.recipe_epoch, "edit_epoch": _request.edit_epoch, "frame_epoch": _request.frame_epoch}
	_pending.clear()
	_request.clear()

func get_diagnostics() -> Dictionary:
	return {"pending": _pending.size(), "inflight": _tasks.size() + (0 if _pack_task.is_empty() else 1), "ready": not _ready.is_empty(), "error": _error, "tasks_submitted": _submitted, "tasks_joined": _joined, "cancelled": _cancelled, "stale": _stale, "high_water": _high_water, "cache_mib": _cache_mib, "cache_bytes": _cache_bytes, "cache_limit_bytes": _cache_mib * 1024 * 1024, "cache_count": _cache.size(), "cache_hits": _cache_hits, "cache_misses": _cache_misses, "cache_evictions": _cache_evictions, "cache_peak_pending": _cache_peak_pending}

func _tile_key(region_x: int, region_z: int) -> String:
	var around: int = posmod(int(_request.anchor_around) + (region_x - 1) * 256, int(RECIPE.CERTIFIED_CIRCUMFERENCE_M))
	var across: int = int(_request.anchor_across) + int(_request.direction) * (region_z - 1) * 256
	return "%s|%s|%s|%s" % [_request.compatibility, around, across, _request.direction]

func _store_cache(key: String, samples: PackedFloat32Array) -> void:
	if _cache.has(key):
		_touch_cache(key)
		return
	_cache[key] = {"samples": samples}
	_cache_lru.append(key)
	_cache_bytes += 128 * 128 * 4
	_evict_cache()

func _touch_cache(key: String) -> void:
	_cache_lru.erase(key)
	_cache_lru.append(key)

func _evict_cache() -> void:
	var limit: int = _cache_mib * 1024 * 1024
	while _cache_bytes > limit and not _cache_lru.is_empty():
		var key: String = _cache_lru.pop_front()
		_cache.erase(key)
		_cache_bytes -= 128 * 128 * 4
		_cache_evictions += 1

func _clear_cache() -> void:
	_cache.clear()
	_cache_lru.clear()
	_cache_bytes = 0

func _compatibility(snapshot: Resource, recipe_epoch: int, edit_epoch: int, edits: Dictionary) -> String:
	var values: Array = []
	for property in snapshot.get_property_list():
		var usage: int = int(property.get("usage", 0))
		if usage & PROPERTY_USAGE_SCRIPT_VARIABLE and usage & PROPERTY_USAGE_STORAGE:
			values.append([property.name, snapshot.get(property.name)])
	values.sort_custom(func(a, b): return str(a[0]) < str(b[0]))
	var edit_values: Array = []
	for key in edits: edit_values.append([key.x, key.y, edits[key]])
	edit_values.sort_custom(func(a, b): return int(a[0]) < int(b[0]) or (int(a[0]) == int(b[0]) and int(a[1]) < int(b[1])))
	var context := HashingContext.new()
	context.start(HashingContext.HASH_SHA256)
	context.update(var_to_bytes([recipe_epoch, edit_epoch, values, edit_values]))
	return context.finish().hex_encode()
