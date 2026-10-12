extends SceneTree
## Forward+ regression for native allocation cleanup when final references die.

const SUNSHINE := "res://resources/clouds/sunshine_ring_local.tres"
const CLP := "res://addons/clouds_lp/CloudsLPTemplate.tres"
const TIMEOUT_MS := 60000
var _mutex := Mutex.new()
var _result: Dictionary = {}
var _errors := PackedStringArray()
var _checks := 0

func _initialize() -> void:
	call_deferred("_run")

func _expect(condition: bool, message: String) -> void:
	_checks += 1
	if not condition:
		_errors.append(message)

func _publish(value: Dictionary) -> void:
	_mutex.lock()
	_result = value
	_mutex.unlock()

func _take_result() -> Dictionary:
	_mutex.lock()
	var value := _result.duplicate()
	_mutex.unlock()
	return value

func _wait_result() -> Dictionary:
	var deadline := Time.get_ticks_msec() + TIMEOUT_MS
	while Time.get_ticks_msec() < deadline:
		var value := _take_result()
		if not value.is_empty():
			return value
		await process_frame
	_errors.append("render-thread operation timed out")
	return {}

func _capture_initialized(effect: Resource, clp: bool) -> void:
	var device := RenderingServer.get_rendering_device()
	if device == null:
		_publish({"error": "RenderingDevice unavailable"})
		return
	if clp:
		if not bool(effect.call("_load_and_init")):
			_publish({"error": "CLP shader initialization failed"})
			return
		effect.call("_alloc_longterm_data", Vector2i(64, 64))
	# Sunshine queues initialize_compute during _init; this queued capture runs
	# after it. Store no effect reference in the cross-thread result.
	var names := ["_pipeline_pass_1", "_pipeline_pass_2", "_pipeline_pass_3", "_pipeline_pass_4"] if clp else ["pipeline", "prepass_pipeline", "postpass_pipeline"]
	var pipelines: Array[RID] = []
	var valid := true
	for property in names:
		var rid: RID = effect.get(property)
		pipelines.append(rid)
		valid = valid and rid.is_valid() and device.compute_pipeline_is_valid(rid)
	_publish({"pipelines": pipelines, "allocated": valid})

func _check_freed(pipelines: Array[RID]) -> void:
	var device := RenderingServer.get_rendering_device()
	var freed := device != null
	for rid in pipelines:
		if device != null and device.compute_pipeline_is_valid(rid):
			freed = false
	_publish({"freed": freed})

func _frames(count: int) -> void:
	for frame in count:
		await process_frame
		await RenderingServer.frame_post_draw

func _prepare_case(path: String, explicit_release: bool) -> Dictionary:
	var clp := path == CLP
	var effect: Resource = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE_DEEP)
	_expect(effect != null, "could not load " + path)
	if effect == null:
		return {}
	var weak: WeakRef = weakref(effect)
	_publish({})
	RenderingServer.call_on_render_thread(_capture_initialized.bind(effect, clp))
	var initialized := await _wait_result()
	_expect(bool(initialized.get("allocated", false)), "%s did not allocate all pipelines: %s" % [path, str(initialized)])
	var pipelines: Array[RID] = initialized.get("pipelines", [])
	if explicit_release:
		effect.call("release_runtime_resources")
		effect.call("release_runtime_resources")
		await _frames(2)
		_publish({})
		RenderingServer.call_on_render_thread(_check_freed.bind(pipelines))
		var released := await _wait_result()
		_expect(bool(released.get("freed", false)), "idempotent CLP release left live pipelines")
	effect = null
	# Return completely before checking destruction. Coroutine/callable argument
	# temporaries in this allocation scope must no longer retain the resource.
	return {"weak": weak, "pipelines": pipelines, "allocated": initialized.get("allocated", false)}

func _run_case(path: String, explicit_release: bool, iteration: int) -> void:
	var metadata := await _prepare_case(path, explicit_release)
	if metadata.is_empty():
		return
	var weak: WeakRef = metadata.weak
	var pipelines: Array[RID] = metadata.pipelines
	await _frames(3)
	var alive := is_instance_valid(weak.get_ref())
	_expect(not alive, "%s final resource reference survived iteration %d" % [path, iteration])
	_publish({})
	RenderingServer.call_on_render_thread(_check_freed.bind(pipelines))
	var result := await _wait_result()
	_expect(bool(result.get("freed", false)), "%s destructor left live pipelines iteration %d" % [path, iteration])
	print("CLOUD_COMPOSITOR_CLEANUP_CASE provider=%s iteration=%d explicit_release=%s allocated=%s freed=%s weak_alive=%s" % ["CLP" if path == CLP else "Sunshine", iteration, explicit_release, metadata.allocated, result.get("freed", false), alive])

func _run() -> void:
	if DisplayServer.get_name() == "headless" or RenderingServer.get_current_rendering_method() != "forward_plus":
		push_error("CLOUD_COMPOSITOR_CLEANUP_GPU=FAIL requires displayed Forward+ rendering")
		quit(1)
		return
	for iteration in 2:
		await _run_case(SUNSHINE, false, iteration)
		await _run_case(CLP, false, iteration)
	await _run_case(CLP, true, 2)
	await _frames(2)
	if _errors.is_empty():
		print("CLOUD_COMPOSITOR_CLEANUP_GPU=PASS checks=%d" % _checks)
		quit(0)
	else:
		for error in _errors:
			push_error("CLOUD_COMPOSITOR_CLEANUP_GPU_ERROR=" + error)
		print("CLOUD_COMPOSITOR_CLEANUP_GPU=FAIL checks=%d" % _checks)
		quit(1)
