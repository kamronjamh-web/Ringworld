extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const SETTLE_FRAMES := 600
const PHASE_WARMUP_FRAMES := 90
const SAMPLE_FRAMES := 300

var _scene: Node
var _clouds: Resource
var _viewport_rid: RID
var _frame := 0
var _phase := 0
var _phase_frame := 0
var _samples: Array[float] = []
var _on_samples: Array[float] = []
var _off_samples: Array[float] = []


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate()
	root.add_child(_scene)
	var driver := _scene.get_node("SunshineCloudsDriver")
	_clouds = driver.get("clouds_resource") as Resource
	_viewport_rid = root.get_viewport_rid()
	_set_sunshine_enabled(true)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame <= SETTLE_FRAMES:
		return false
	if _frame == SETTLE_FRAMES + 1:
		_scene.process_mode = Node.PROCESS_MODE_DISABLED
		_set_sunshine_enabled(true)

	_phase_frame += 1
	if _phase_frame > PHASE_WARMUP_FRAMES:
		var gpu_ms := RenderingServer.viewport_get_measured_render_time_gpu(_viewport_rid)
		if gpu_ms > 0.0 and is_finite(gpu_ms):
			_samples.append(gpu_ms)

	if _samples.size() < SAMPLE_FRAMES:
		return false

	var average := _trimmed_average(_samples)
	if _phase == 0 or _phase == 2:
		_on_samples.append(average)
	else:
		_off_samples.append(average)
	print("Sunshine GPU phase %d [%s]: %.3f ms" % [
		_phase + 1,
		"ON" if _phase != 1 else "OFF",
		average])

	_phase += 1
	if _phase >= 3:
		var on_ms := _average(_on_samples)
		var off_ms := _average(_off_samples)
		var cloud_ms := maxf(0.0, on_ms - off_ms)
		var cloud_percent := cloud_ms / maxf(on_ms, 0.001) * 100.0
		print("Sunshine GPU result: ON=%.3f ms OFF=%.3f ms COST=%.3f ms (%.1f%% of GPU frame)" % [
			on_ms, off_ms, cloud_ms, cloud_percent])
		quit(0)
		return true

	_samples.clear()
	_phase_frame = 0
	_set_sunshine_enabled(_phase != 1)
	return false


func _set_sunshine_enabled(enabled: bool) -> void:
	if _clouds != null:
		_clouds.set("enabled", enabled)
		if enabled:
			_clouds.call("refresh_compute")


func _trimmed_average(values: Array[float]) -> float:
	var sorted := values.duplicate()
	sorted.sort()
	var trim := sorted.size() / 10
	var total := 0.0
	for index in range(trim, sorted.size() - trim):
		total += sorted[index]
	return total / maxf(1.0, sorted.size() - trim * 2)


func _average(values: Array[float]) -> float:
	var total := 0.0
	for value in values:
		total += value
	return total / maxf(1.0, values.size())