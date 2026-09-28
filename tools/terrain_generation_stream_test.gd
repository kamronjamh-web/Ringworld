extends SceneTree

const STREAM := preload("res://scripts/Terrain/Generation/terrain_generation_stream.gd")
const MODEL := preload("res://scripts/Terrain/Generation/terrain_height_model.gd")
const WINDOW := preload("res://scripts/Terrain/Generation/terrain_generation_window.gd")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const OUTPUT := "res://.godot/terrain-generation-tests/stream-report.json"
const MIN_CHECKS := 65
var _checks := 0
var _failures := PackedStringArray()
var _complete := false
var _finished := false
var _max_poll_ms := 0.0
var _observations: Dictionary = {}

func _init() -> void:
	call_deferred("_run")
	create_timer(90.0).timeout.connect(_watchdog)

func _run() -> void:
	var stream = STREAM.new()
	stream.configure(2, 1)
	var recipe = RECIPE.new()
	var first: Dictionary = await _request(stream, recipe, -256, 159000, 1, 1, 1, 1, 1)
	_expect(first.get("ok", false), "first request succeeds")
	var expected := WINDOW.prepare(MODEL.new(recipe), -256, 159000, 1, 1)
	_expect(first.absolute_heights == expected.absolute_heights, "first absolute samples match synchronous reference")
	_expect(_images_equal(first.height_images, expected.height_images), "first RF images match synchronous reference")
	_observations.first_window_latency_ms = first.latency_ms
	var second: Dictionary = await _request(stream, recipe, 0, 159000, 1, 2, 1, 1, 2)
	_expect(second.get("ok", false), "frame-only request succeeds")
	_expect(int(second.get("cache_hits", 0)) == 6, "256m frame shift reuses exactly six tiles")
	_observations.recenter_latency_ms = second.latency_ms
	var expected_second := WINDOW.prepare(MODEL.new(recipe), 0, 159000, 1, 2)
	_expect(second.absolute_heights == expected_second.absolute_heights, "frame-only samples match synchronous reference")
	recipe.land_balance += .1
	var third: Dictionary = await _request(stream, recipe, 0, 159000, 1, 3, 2, 1, 3)
	_expect(third.get("ok", false), "recipe change request succeeds")
	_expect(int(third.get("cache_hits", 0)) == 0, "recipe epoch change invalidates cache")
	await _race_cases(stream)
	await _pack_and_cache_cases(stream)
	stream.shutdown()
	var diagnostics: Dictionary = stream.get_diagnostics()
	_expect(diagnostics.tasks_submitted == diagnostics.tasks_joined, "shutdown joins every submitted task")
	_expect(diagnostics.inflight == 0, "shutdown leaves no inflight task")
	_expect(not stream.request_window(recipe, 0, 159000, 1, 100000, 2, 1, 100000), "closed stream rejects otherwise-valid requests")
	_complete = true
	_finish()

func _race_cases(stream) -> void:
	var recipe = RECIPE.new()
	var edits := {Vector2i(0, 159000): 123.375}
	_expect(stream.request_window(recipe, 0, 159000, 1, 10, 10, 10, 10, edits), "snapshot request accepted")
	recipe.land_balance += .2
	edits[Vector2i(0, 159000)] = 234.5
	var snapshot_result: Dictionary = await _wait_result(stream, 10)
	_expect(absf(float(snapshot_result.absolute_heights[128 * 384 + 128]) - 123.375) < .001, "edit caller mutation is isolated")
	var original := WINDOW.prepare(MODEL.new(RECIPE.new()), 0, 159000, 1, 10)
	_expect(snapshot_result.absolute_heights[0] == original.absolute_heights[0], "recipe caller mutation is isolated away from overrides")
	var next_edits := {Vector2i(0, 159000): 234.5}
	var changed: Dictionary = await _request(stream, recipe, 0, 159000, 1, 11, 10, 10, 11)
	# A changed recipe snapshot with the same epochs must not reuse prior tiles.
	_expect(int(changed.get("cache_hits", 0)) == 0, "same epoch changed recipe invalidates cache")
	_expect(stream.request_window(recipe, 0, 159000, 1, 12, 10, 10, 12, next_edits), "changed edit request accepted")
	var edit_result: Dictionary = await _wait_result(stream, 12)
	_expect(int(edit_result.get("cache_hits", 0)) == 0 and absf(float(edit_result.absolute_heights[128 * 384 + 128]) - 234.5) < .001, "same epoch changed edit invalidates cache")
	var last_revision := 0
	for revision in range(20, 40):
		_expect(stream.request_window(recipe, revision * 256, 159000, 1, revision, 20, 20, revision), "rapid request accepted")
		last_revision = revision
	var rapid: Dictionary = await _wait_result(stream, last_revision)
	_expect(int(rapid.get("revision", 0)) == last_revision, "rapid supersession publishes latest only")
	var reversed_recipe = RECIPE.new()
	var reverse: Dictionary = await _request(stream, reversed_recipe, -256, 159000, -1, 50, 50, 50, 50)
	var reverse_expected := WINDOW.prepare(MODEL.new(reversed_recipe), -256, 159000, -1, 50)
	_expect(reverse.absolute_heights == reverse_expected.absolute_heights and _images_equal(reverse.height_images, reverse_expected.height_images), "negative wrap reversed direction parity")
	var teardown = STREAM.new()
	teardown.configure(2, 1)
	_expect(teardown.request_window(RECIPE.new(), 0, 159000, 1, 60, 60, 60, 60), "teardown inflight request accepted")
	teardown.shutdown()
	var diagnostics: Dictionary = teardown.get_diagnostics()
	_expect(diagnostics.tasks_submitted == diagnostics.tasks_joined and diagnostics.inflight == 0 and diagnostics.cache_bytes == 0, "shutdown during sampling joins and clears cache")

func _pack_and_cache_cases(stream) -> void:
	var recipe = RECIPE.new()
	_expect(stream.request_window(recipe, -256, 159000, -1, 51, 50, 50, 51), "cached pack request accepted")
	_expect(stream.poll().is_empty(), "pack job scheduled without synchronous publication")
	_expect(not stream.get("_pack_task").is_empty(), "pack cancellation fixture really has a retained worker")
	_expect(not stream.request_window(recipe, 0, 159000, 0, 52, 50, 50, 52), "invalid request rejected during packing")
	_expect(int(stream.get("_request").revision) == 51, "invalid input leaves pending request intact")
	_expect(not stream.request_window(recipe, 0, 159000, 1, 50, 50, 50, 50), "older request rejected")
	var cancel_start := Time.get_ticks_usec()
	stream.cancel()
	_observations.cancel_ms = float(Time.get_ticks_usec() - cancel_start) / 1000.0
	_expect(stream.request_window(recipe, 0, 159000, 1, 53, 50, 50, 53), "new window supersedes pack work")
	var latest: Dictionary = await _wait_result(stream, 53)
	_expect(latest.revision == 53, "cancelled pack cannot publish old window")
	_expect(stream.get_diagnostics().cache_evictions > 0, "distinct directions exercise bounded LRU eviction")
	_expect(not latest.has("tiles") and not latest.has("recipe") and not latest.has("edits"), "payload exposes no private cache inputs")
	latest.absolute_heights[0] = 7000.25
	(latest.height_images[0] as Image).set_pixel(0, 0, Color(100, 0, 0, 1))
	var reused: Dictionary = await _request(stream, recipe, 0, 159000, 1, 54, 50, 50, 54)
	var expected := WINDOW.prepare(MODEL.new(recipe), 0, 159000, 1, 54)
	_expect(reused.cache_hits == 9 and reused.absolute_heights == expected.absolute_heights, "caller output mutation cannot corrupt cached samples")
	_expect(_images_equal(reused.height_images, expected.height_images), "cached canonical data repacks fresh RF images")
	var edits_a := {Vector2i(0, 159000): 10.25, Vector2i(2, 159000): 11.5}
	var edits_b := {Vector2i(2, 159000): 11.5, Vector2i(0, 159000): 10.25}
	_expect(stream.request_window(recipe, 0, 159000, 1, 55, 50, 51, 55, edits_a), "two-edit cache fixture accepted")
	await _wait_result(stream, 55)
	_expect(stream.request_window(recipe, 0, 159000, 1, 56, 50, 51, 56, edits_b), "reordered edit snapshot accepted")
	var reordered: Dictionary = await _wait_result(stream, 56)
	_expect(reordered.cache_hits == 9, "edit insertion order does not invalidate compatible samples")
	edits_b[Vector2i(0, 159000)] = 12.75
	_expect(stream.request_window(recipe, 0, 159000, 1, 57, 50, 51, 57, edits_b), "same edit epoch changed values accepted")
	var changed: Dictionary = await _wait_result(stream, 57)
	_expect(changed.cache_hits == 0 and changed.absolute_heights[128 * 384 + 128] == 12.75, "same-epoch edit changes cannot reuse old heights")
	_expect(not stream.request_window(recipe, 0, 159000, 1, 58, 50, 51, 58, {Vector2i.ZERO: "bad"}), "nonnumeric edits are rejected safely")
	_expect(not stream.request_window(recipe, 0, 159000, 1, 58, 50, 51, 58, {Vector2i(0, 159000): 10.0, Vector2i(31415936, 159000): 20.0}), "conflicting wrapped overrides are rejected deterministically")
	stream.configure(4, 1)
	_expect(stream.request_window(recipe, 10000, 159000, 1, 60, 60, 60, 60), "four-worker fixture starts")
	_expect(stream.get_diagnostics().inflight == 4, "configured four-worker dispatch is bounded")
	stream.configure(1, 1)
	await _wait_result(stream, 60, 4)
	_expect(stream.get_diagnostics().tasks_submitted == stream.get_diagnostics().tasks_joined, "lowering worker count drains existing jobs without losing joins")
	var before: Dictionary = stream.get_diagnostics()
	_expect(stream.request_window(recipe, 20000, 159000, 1, 61, 60, 60, 61), "single-worker fixture starts")
	_expect(stream.get_diagnostics().inflight == 1, "new jobs obey reduced worker limit")
	await _wait_result(stream, 61, 1)
	_observations.final_scheduler = stream.get_diagnostics()
	_expect(stream.get_diagnostics().cache_bytes <= before.cache_limit_bytes, "longer route retains cache bound")

func _wait_result(stream, revision: int, limit: int = 2) -> Dictionary:
	var deadline := Time.get_ticks_msec() + 30000
	var max_inflight := 0
	var max_pending := 0
	var max_cache := 0
	while Time.get_ticks_msec() < deadline:
		var diagnostics: Dictionary = stream.get_diagnostics()
		max_inflight = max(max_inflight, int(diagnostics.inflight))
		max_pending = max(max_pending, int(diagnostics.pending))
		max_cache = max(max_cache, int(diagnostics.cache_bytes))
		var started := Time.get_ticks_usec()
		var result: Dictionary = stream.poll()
		_max_poll_ms = maxf(_max_poll_ms, float(Time.get_ticks_usec() - started) / 1000.0)
		if not result.is_empty():
			_expect(result.get("ok", false) and int(result.get("revision", -1)) == revision, "only requested revision publishes %d" % revision)
			_expect(max_inflight <= limit and max_pending <= 9 and max_cache <= int(diagnostics.cache_limit_bytes), "bounded scheduler %d" % revision)
			return result
		await create_timer(.005).timeout
	_failures.append("race request timed out %d" % revision)
	return {}

func _request(stream, recipe, around: int, across: int, direction: int, revision: int, recipe_epoch: int, edit_epoch: int, frame_epoch: int) -> Dictionary:
	_expect(stream.request_window(recipe, around, across, direction, revision, recipe_epoch, edit_epoch, frame_epoch), "request %d accepted" % revision)
	return await _wait_result(stream, revision)

func _images_equal(left: Array, right: Array) -> bool:
	if left.size() != right.size(): return false
	for i in left.size():
		if (left[i] as Image).get_data() != (right[i] as Image).get_data(): return false
	return true

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition: _failures.append(label)

func _watchdog() -> void:
	if not _finished:
		_failures.append("watchdog expired")
		_finish()

func _finish() -> void:
	if _finished: return
	_finished = true
	if _checks < MIN_CHECKS: _failures.append("too few checks")
	if not _complete: _failures.append("test did not complete all stages")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/terrain-generation-tests"))
	var file := FileAccess.open(OUTPUT, FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"checks": _checks, "complete": _complete, "failures": _failures, "max_poll_ms": _max_poll_ms, "observations": _observations}, "  "))
		file.close()
	else:
		_failures.append("could not write report")
	if _complete and _failures.is_empty():
		print("TERRAIN_GENERATION_STREAM=PASS ", JSON.stringify({"checks": _checks}))
		quit(0)
		return
	push_error("TERRAIN_GENERATION_STREAM=FAIL " + "; ".join(_failures))
	quit(1)
