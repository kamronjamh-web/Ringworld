extends SceneTree

## Verifies that a sliced near cloud-shadow refresh publishes a map bit-identical
## to the legacy single-dispatch refresh for the same inputs, and that receivers
## never see a partially written map while slices are in flight.

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MAX_FRAMES := 1500
const SLICES := 4

var _scene: Node
var _bridge: Node
var _clouds: Resource
var _day_night: Node
var _frame := 0
var _stage := 0
var _legacy: PackedByteArray
var _legacy_dispatches := 0
var _sliced: PackedByteArray
var _torn_checks := 0
var _saw_progress := false
var _stage_frame := 0
var _final_dispatches := 0


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate()
	_bridge = _scene.get_node("SunshineCloudBridge")
	_clouds = _scene.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	_bridge.set("CloudResolutionScale", 0)
	_bridge.set("RaymarchSteps", 64)
	_bridge.set("CastCloudShadows", true)
	_bridge.set("PreferGpuCloudShadows", true)
	_bridge.set("GpuNearShadowMapResolution", 1024)
	_bridge.set("GpuFarShadowMapResolution", 256)
	_bridge.set("GpuNearShadowRadiusMeters", 40000.0)
	_bridge.set("GpuNearShadowUpdatesPerSecond", 10.0)
	_bridge.set("GpuFarShadowUpdatesPerSecond", 1.0)
	_bridge.set("GpuShadowVerticalSamples", 64)
	_bridge.set("GpuNearShadowSliceCount", 1)
	root.add_child(_scene)
	_day_night = _scene.get_node("RingDayNightCycle")
	_day_night.set_process(false)
	_day_night.set_physics_process(false)
	_day_night.set("Paused", true)


func _status() -> Dictionary:
	return _clouds.call("get_gpu_cloud_shadow_status") as Dictionary


func _read_front() -> PackedByteArray:
	var texture := _clouds.call("get_gpu_cloud_shadow_texture", 0) as Texture2DRD
	var rd := RenderingServer.get_rendering_device()
	return rd.texture_get_data(texture.texture_rd_rid, 0)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 2:
		_day_night.call("set_cycle_fraction", 0.0)
	if _frame == 3:
		var weather := _scene.get_node_or_null("RingVolumetricClouds")
		if weather != null:
			weather.set("CloudSimulationPaused", true)
		_scene.get_node("SunshineCloudsDriver").set_process(false)
	if _frame >= MAX_FRAMES:
		push_error("Timed out in stage %d: %s" % [_stage, str(_status())])
		quit(1)
		return true
	if _frame < 40:
		return false
	var status := _status()
	var dispatches := int(status.get("near_dispatches", 0))
	var pending := bool(_bridge.call("IsGpuShadowCascadePublishPending", 0))
	match _stage:
		0:
			# Legacy single dispatch: wait until a refresh has fully published.
			if dispatches >= 3 and not pending:
				_legacy = _read_front()
				_legacy_dispatches = dispatches
				_stage = 5
		5:
			if dispatches > _legacy_dispatches and not pending:
				var control := _read_front()
				var drift := 0
				for i in control.size():
					if control[i] != _legacy[i]: drift += 1
				print("LEGACY_VS_LEGACY drift_bytes=", drift)
				_legacy = control
				_legacy_dispatches = dispatches
				_bridge.set("GpuNearShadowSliceCount", SLICES)
				_stage = 1
				_stage_frame = _frame
		1:
			# Receivers must keep the previous complete map until the final slice.
			var progress := int(status.get("near_slice_progress", 0))
			if progress > 0 and dispatches == _legacy_dispatches:
				_saw_progress = true
				_torn_checks += 1
				if _read_front() != _legacy:
					push_error("Front map changed while slices were still in flight")
					quit(1)
					return true
			if dispatches > _legacy_dispatches and not pending:
				_sliced = _read_front()
				_final_dispatches = dispatches
				_stage = 2
			elif _frame > _stage_frame + 400:
				push_error("Sliced refresh never completed: %s" % str(status))
				quit(1)
				return true
		2:
			if not _saw_progress:
				push_error("Never observed slice progress; slicing did not engage")
				quit(1)
				return true
			var same_size := _legacy.size() == _sliced.size() and _legacy.size() > 0
			var identical := same_size and _legacy == _sliced
			var differing := 0
			var rows := {}
			if same_size and not identical:

				for i in _legacy.size():
					if _legacy[i] != _sliced[i]:
						differing += 1
						rows[i / 4096] = int(rows.get(i / 4096, 0)) + 1
			if differing > 0: print("DIFF_ROWS ", rows.size(), " first=", rows.keys().slice(0, 40))
			var nonzero := 0
			for i in range(0, _legacy.size(), 4):
				if _legacy[i] != 0 or _legacy[i + 1] != 0:
					nonzero += 1
			print("SLICE_EQUIVALENCE bytes=%d identical=%s differing=%d nonzero_samples=%d torn_checks=%d slices=%d dispatches=%d->%d"
				% [_legacy.size(), str(identical), differing, nonzero, _torn_checks, SLICES, _legacy_dispatches, _final_dispatches])
			if not identical or nonzero == 0:
				push_error("Sliced map differs from the legacy single-dispatch map (or map is blank)")
				quit(1)
				return true
			print("Sunshine shadow slice equivalence passed")
			quit(0)
			return true
	return false
