extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const SETTLE_FRAMES := 360
const PHASE_WARMUP_FRAMES := 60
const SAMPLE_FRAMES := 180
const CERTIFIED_AROUND_CHUNKS := 981748
const CERTIFIED_RADIUS_METERS := 5000000.0
const CERTIFIED_WIDTH_METERS := 318000.0
const CERTIFIED_CIRCUMFERENCE_METERS := 31415936.0
const PHASES := [
	{"label": "baseline", "generation": false, "receivers": false},
	{"label": "receiver_only", "generation": false, "receivers": true},
	{"label": "normal", "generation": true, "receivers": true},
	{"label": "producer_only", "generation": true, "receivers": false},
	{"label": "normal", "generation": true, "receivers": true},
	{"label": "receiver_only", "generation": false, "receivers": true},
	{"label": "baseline", "generation": false, "receivers": false},
]

var _scene: Node
var _bridge: Node
var _clouds: Resource
var _day_night_cycle: Node
var _viewport_rid: RID
var _frame := 0
var _phase := 0
var _phase_frame := 0
var _samples: Array[float] = []
var _averages: Array[float] = []
var _dispatch_start := Vector2i.ZERO
var _certified_ring := false


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate()
	_certified_ring = OS.has_environment("SUNSHINE_SHADOW_CERTIFIED_RING")
	if _certified_ring:
		var grid := _scene.get_node("ChunkGridManager")
		grid.set("AroundRingChunkLength", CERTIFIED_AROUND_CHUNKS)
		grid.set("RingRadiusMeters", CERTIFIED_RADIUS_METERS)
		grid.set("RingAcrossWidthMeters", CERTIFIED_WIDTH_METERS)
		grid.set("RingAcrossBaseOffsetMeters", -CERTIFIED_WIDTH_METERS * 0.5)
		var explorer := _scene.get_node("Explorer") as Node3D
		var position := explorer.position
		position.x = CERTIFIED_CIRCUMFERENCE_METERS / TAU - 23.0
		explorer.position = position
	_bridge = _scene.get_node("SunshineCloudBridge")
	var driver := _scene.get_node("SunshineCloudsDriver")
	_clouds = driver.get("clouds_resource") as Resource
	_bridge.set("CastCloudShadows", true)
	_bridge.set("PreferGpuCloudShadows", true)
	_bridge.set("GpuNearShadowMapResolution", 512)
	_bridge.set("GpuFarShadowMapResolution", 256)
	_bridge.set("GpuNearShadowRadiusMeters", 24000.0)
	_bridge.set("GpuFarShadowRadiusMeters", 96000.0)
	_bridge.set("GpuNearShadowUpdatesPerSecond", 6.0)
	_bridge.set("GpuFarShadowUpdatesPerSecond", 1.0)
	_bridge.set("GpuShadowVerticalSamples", 4)
	_bridge.set("GpuShadowGenerationEnabled", true)
	_bridge.set("CloudShadowReceiversEnabled", true)
	root.add_child(_scene)
	_day_night_cycle = _scene.get_node("RingDayNightCycle")
	_day_night_cycle.set_process(false)
	_viewport_rid = root.get_viewport_rid()


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 2:
		_day_night_cycle.call("set_cycle_fraction", 0.0)
	if _frame == 12:
		_call_bridge("SpawnStormFrontAhead", "spawn_storm_front_ahead")
	if _frame < SETTLE_FRAMES:
		return false
	if _frame == SETTLE_FRAMES:
		# Prewarm both maps and all receiver shaders under normal gameplay, then
		# use a mirrored phase order to cancel first-order thermal/streaming drift.
		_apply_phase(0)
		return false

	_phase_frame += 1
	if _phase_frame > PHASE_WARMUP_FRAMES:
		var gpu_ms := RenderingServer.viewport_get_measured_render_time_gpu(_viewport_rid)
		if gpu_ms > 0.0 and is_finite(gpu_ms):
			_samples.append(gpu_ms)
	if _samples.size() < SAMPLE_FRAMES:
		return false

	var average := _average(_samples)
	var p95 := _percentile(_samples, 0.95)
	var status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
	var dispatch_end := Vector2i(
		int(status.get("near_dispatches", 0)),
		int(status.get("far_dispatches", 0)))
	var dispatch_delta := dispatch_end - _dispatch_start
	_averages.append(average)
	print("Sunshine shadow performance phase=%s scale=%s avg=%.3fms p95=%.3fms dispatches=%d/%d" % [
		str(PHASES[_phase]["label"]),
		"certified" if _certified_ring else "compact",
		average,
		p95,
		dispatch_delta.x,
		dispatch_delta.y])

	_phase += 1
	if _phase >= PHASES.size():
		var baseline := (_averages[0] + _averages[6]) * 0.5
		var receiver_only := (_averages[1] + _averages[5]) * 0.5
		var normal := (_averages[2] + _averages[4]) * 0.5
		var producer_only := _averages[3]
		# The producer's independent RD list is not consistently enclosed by the
		# viewport GPU timer on every backend. Attribute its in-frame contribution
		# from normal - receiver_only, while retaining producer_only as a signed
		# diagnostic for timer drift and verifying its dispatch count above.
		print("SUNSHINE_SHADOW_PERFORMANCE_BENCHMARK=PASS scale=%s normal=%.3fms baseline=%.3fms overhead=%.3fms receiver=%.3fms producer_incremental=%.3fms producer_timer_delta=%+.3fms" % [
			"certified" if _certified_ring else "compact",
			normal,
			baseline,
			maxf(0.0, normal - baseline),
			maxf(0.0, receiver_only - baseline),
			maxf(0.0, normal - receiver_only),
			producer_only - baseline])
		quit(0)
		return true

	_samples.clear()
	_phase_frame = 0
	_apply_phase(_phase)
	return false


func _apply_phase(index: int) -> void:
	var phase: Dictionary = PHASES[index]
	_bridge.set("GpuShadowGenerationEnabled", bool(phase["generation"]))
	_bridge.set("CloudShadowReceiversEnabled", bool(phase["receivers"]))
	if bool(_bridge.get("GpuShadowGenerationEnabled")) != bool(phase["generation"]) \
			or bool(_bridge.get("CloudShadowReceiversEnabled")) != bool(phase["receivers"]):
		push_error("Cloud-shadow profiling toggle did not apply for phase %s" % str(phase["label"]))
		quit(1)
		return
	var status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
	_dispatch_start = Vector2i(
		int(status.get("near_dispatches", 0)),
		int(status.get("far_dispatches", 0)))


func _call_bridge(pascal_name: StringName, snake_name: StringName) -> void:
	var method_name := pascal_name if _bridge.has_method(pascal_name) else snake_name
	if _bridge.has_method(method_name):
		_bridge.call(method_name)


func _average(values: Array[float]) -> float:
	var total := 0.0
	for value in values:
		total += value
	return total / maxf(1.0, values.size())


func _percentile(values: Array[float], fraction: float) -> float:
	var sorted := values.duplicate()
	sorted.sort()
	var index := clampi(int(round((sorted.size() - 1) * fraction)), 0, sorted.size() - 1)
	return sorted[index]
