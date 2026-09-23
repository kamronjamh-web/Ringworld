extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MAX_FRAMES := 480
const CERTIFIED_AROUND_CHUNKS := 981748
const CERTIFIED_RADIUS_METERS := 5000000.0
const CERTIFIED_WIDTH_METERS := 318000.0
const CERTIFIED_CIRCUMFERENCE_METERS := 31415936.0

var _scene: Node
var _bridge: Node
var _clouds: Resource
var _day_night_cycle: Node
var _frame := 0
var _storm_spawned := false
var _probe_viewport: SubViewport
var _probe_started_frame := -1
var _passed_dispatch_checks := false
var _passed_recenter_sync := false
var _recenter_stage := 0
var _recenter_near_center := Vector2.ZERO
var _recenter_near_dispatches := 0
var _recenter_viewer := Vector3.ZERO
var _recenter_dispatch_seen_frame := -1
var _passed_near_dispatches := 0
var _passed_far_dispatches := 0
var _passed_receivers := 0
var _night_started_frame := -1
var _sunrise_started_frame := -1
var _dispatches_before_night := 0
var _verified_near_map_minimum := 0.0
var _verified_near_map_maximum := 0.0
var _verified_far_map_minimum := 0.0
var _verified_far_map_maximum := 0.0
var _certified_ring_scale := false
var _ring_scale_label := "compact-curvature"


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate()
	_certified_ring_scale = OS.has_environment("SUNSHINE_SHADOW_CERTIFIED_RING")
	if _certified_ring_scale:
		var grid := _scene.get_node("ChunkGridManager")
		grid.set("AroundRingChunkLength", CERTIFIED_AROUND_CHUNKS)
		grid.set("RingRadiusMeters", CERTIFIED_RADIUS_METERS)
		grid.set("RingAcrossWidthMeters", CERTIFIED_WIDTH_METERS)
		grid.set("RingAcrossBaseOffsetMeters", -CERTIFIED_WIDTH_METERS * 0.5)
		# The compact scene starts its explorer 23 m inward from an 80 km ring.
		# Preserve that surface-relative placement when swapping in production
		# topology; otherwise the bridge correctly treats the camera as being
		# roughly 4,920 km above the certified surface and suspends local clouds.
		var explorer := _scene.get_node("Explorer") as Node3D
		var explorer_position := explorer.position
		explorer_position.x = CERTIFIED_CIRCUMFERENCE_METERS / TAU - 23.0
		explorer.position = explorer_position
		_ring_scale_label = "certified-production"
	_bridge = _scene.get_node("SunshineCloudBridge")
	var driver := _scene.get_node("SunshineCloudsDriver")
	_clouds = driver.get("clouds_resource") as Resource
	_bridge.set("CloudResolutionScale", 0)
	_bridge.set("RaymarchSteps", 128)
	_bridge.set("CastCloudShadows", true)
	_bridge.set("PreferGpuCloudShadows", true)
	_bridge.set("GpuNearShadowMapResolution", 512)
	_bridge.set("GpuFarShadowMapResolution", 256)
	_bridge.set("GpuNearShadowRadiusMeters", 24000.0)
	_bridge.set("GpuFarShadowRadiusMeters", 96000.0)
	_bridge.set("GpuNearShadowUpdatesPerSecond", 6.0)
	_bridge.set("GpuFarShadowUpdatesPerSecond", 1.0)
	_bridge.set("GpuShadowVerticalSamples", 4)
	root.add_child(_scene)
	_day_night_cycle = _scene.get_node("RingDayNightCycle")
	_day_night_cycle.set_process(false)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 2:
		if _certified_ring_scale:
			var grid := _scene.get_node("ChunkGridManager")
			var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
			var radius := float(grid.call("GetCanonicalRingRadiusMeters"))
			if absf(circumference - CERTIFIED_CIRCUMFERENCE_METERS) > 0.5 \
					or absf(radius - CERTIFIED_CIRCUMFERENCE_METERS / TAU) > 1.0 \
					or absf(float(grid.get("RingAcrossWidthMeters")) - CERTIFIED_WIDTH_METERS) > 0.5:
				return _fail(
					"Certified ring setup was not applied: circumference=%.3f, radius=%.3f, width=%.3f"
					% [circumference, radius, float(grid.get("RingAcrossWidthMeters"))])
		# Lock to direct overhead light so daylight gating cannot make this test
		# depend on whichever phase the ring-spin cycle starts in.
		_day_night_cycle.call("set_cycle_fraction", 0.0)
	if _frame == 12:
		_call_bridge("SpawnStormFrontAhead", "spawn_storm_front_ahead")
		_storm_spawned = true

	if _night_started_frame >= 0 and _sunrise_started_frame < 0:
		var projector_enabled := bool(_call_bridge_result(
			"IsCloudShadowProjectorEnabled", "is_cloud_shadow_projector_enabled", true))
		var night_status := str(_call_bridge_result(
			"GetGpuCloudShadowStatus", "get_gpu_cloud_shadow_status", "unavailable"))
		if not projector_enabled and "below horizon" in night_status:
			_day_night_cycle.call("set_cycle_fraction", 0.0)
			_sunrise_started_frame = _frame
		elif _frame >= _night_started_frame + 30:
			return _fail("GPU cloud shadows remained active at night: %s" % night_status)
		return false

	if _sunrise_started_frame >= 0:
		var sunrise_status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
		var sunrise_dispatches := int(sunrise_status.get("near_dispatches", 0))
		var projector_restored := bool(_call_bridge_result(
			"IsCloudShadowProjectorEnabled", "is_cloud_shadow_projector_enabled", false))
		if projector_restored and sunrise_dispatches > _dispatches_before_night:
			print(
				"Sunshine GPU shadow smoke passed: scale=%s, near=512x512/%d dispatches/range=%.4f-%.4f, far=256x256/%d dispatches/range=%.4f-%.4f, receivers=%d, nightGate=pass, sunriseRebake=pass, status=%s"
				% [_ring_scale_label, sunrise_dispatches,
					_verified_near_map_minimum, _verified_near_map_maximum,
					_passed_far_dispatches, _verified_far_map_minimum,
					_verified_far_map_maximum, _passed_receivers, str(_call_bridge_result(
						"GetGpuCloudShadowStatus", "get_gpu_cloud_shadow_status", "unknown"))])
			quit(0)
			return true
		if _frame >= _sunrise_started_frame + 60:
			return _fail(
				"GPU cloud shadows did not rebake after sunrise: %s"
				% str(sunrise_status))
		return false

	if _probe_viewport != null and _passed_recenter_sync and _frame >= _probe_started_frame + 4:
		var image := _probe_viewport.get_texture().get_image()
		if image == null or image.is_empty():
			return _fail("GPU shadow texture probe produced no image")
		var near_minimum := 1.0
		var near_maximum := 0.0
		var far_minimum := 1.0
		var far_maximum := 0.0
		for y in range(4, image.get_height(), 8):
			for x in range(4, 128, 8):
				var value := _combined_temporal_occlusion(image.get_pixel(x, y))
				near_minimum = minf(near_minimum, value)
				near_maximum = maxf(near_maximum, value)
			for x in range(132, 256, 8):
				var value := _combined_temporal_occlusion(image.get_pixel(x, y))
				far_minimum = minf(far_minimum, value)
				far_maximum = maxf(far_maximum, value)
		if near_maximum <= 0.005 or near_maximum - near_minimum <= 0.002:
			return _fail(
				"Near GPU shadow texture was blank or constant: min=%.4f, max=%.4f"
				% [near_minimum, near_maximum])
		if far_maximum <= 0.005 or far_maximum - far_minimum <= 0.002:
			return _fail(
				"Far GPU shadow texture was blank or constant: min=%.4f, max=%.4f"
				% [far_minimum, far_maximum])
		_verified_near_map_minimum = near_minimum
		_verified_near_map_maximum = near_maximum
		_verified_far_map_minimum = far_minimum
		_verified_far_map_maximum = far_maximum
		var pre_night_status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
		_dispatches_before_night = int(pre_night_status.get("near_dispatches", 0))
		_probe_viewport.queue_free()
		_probe_viewport = null
		_day_night_cycle.call("set_cycle_fraction", 0.5)
		_night_started_frame = _frame
		return false

	if _storm_spawned and _frame > 90 and not _passed_dispatch_checks:
		var status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
		var near_dispatches := int(status.get("near_dispatches", 0))
		var far_dispatches := int(status.get("far_dispatches", 0))
		var near_resolution := status.get("near_resolution", Vector2i.ZERO) as Vector2i
		var far_resolution := status.get("far_resolution", Vector2i.ZERO) as Vector2i
		var using_gpu := bool(_call_bridge_result(
			"IsUsingGpuCloudShadows", "is_using_gpu_cloud_shadows", false))
		var receivers := int(_call_bridge_result(
			"GetCloudShadowReceiverCount", "get_cloud_shadow_receiver_count", 0))
		var near_texture := _clouds.call("get_gpu_cloud_shadow_texture", 0) as Texture2D
		var far_texture := _clouds.call("get_gpu_cloud_shadow_texture", 1) as Texture2D
		if using_gpu and near_dispatches >= 2 and far_dispatches >= 1 \
				and near_resolution == Vector2i(512, 512) \
				and far_resolution == Vector2i(256, 256) \
				and near_texture != null and far_texture != null and receivers >= 8:
			_passed_dispatch_checks = true
			_passed_near_dispatches = near_dispatches
			_passed_far_dispatches = far_dispatches
			_passed_receivers = receivers
			_probe_viewport = SubViewport.new()
			_probe_viewport.size = Vector2i(256, 128)
			_probe_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
			var near_preview := TextureRect.new()
			near_preview.texture = near_texture
			near_preview.position = Vector2.ZERO
			near_preview.size = Vector2(128, 128)
			near_preview.stretch_mode = TextureRect.STRETCH_SCALE
			_probe_viewport.add_child(near_preview)
			var far_preview := TextureRect.new()
			far_preview.texture = far_texture
			far_preview.position = Vector2(128, 0)
			far_preview.size = Vector2(128, 128)
			far_preview.stretch_mode = TextureRect.STRETCH_SCALE
			_probe_viewport.add_child(far_preview)
			root.add_child(_probe_viewport)
			_probe_started_frame = _frame

	if _passed_dispatch_checks and not _passed_recenter_sync:
		if _run_near_recenter_sync_regression():
			return false
		return false

	if _frame >= MAX_FRAMES:
		var status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
		return _fail(
			"GPU shadow cascades did not become authoritative: bridge=%s, addon=%s"
			% [str(_call_bridge_result(
				"GetGpuCloudShadowStatus", "get_gpu_cloud_shadow_status", "unavailable")),
				str(status)])
	return false


func _run_near_recenter_sync_regression() -> bool:
	var status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
	var near_dispatches := int(status.get("near_dispatches", 0))
	var installed_center := _call_bridge_result(
		"GetInstalledGpuShadowCenter", "get_installed_gpu_shadow_center", Vector2.ZERO, [0]) as Vector2
	var pending := bool(_call_bridge_result(
		"IsGpuShadowCascadePublishPending", "is_gpu_shadow_cascade_publish_pending", false, [0]))
	match _recenter_stage:
		0:
			# Remove ordinary 6 Hz refreshes from this focused relocation check.
			# If one was already queued, let it publish before taking the baseline.
			_bridge.set("GpuNearShadowUpdatesPerSecond", 0.1)
			if pending:
				return false
			_recenter_near_center = installed_center
			_recenter_near_dispatches = near_dispatches
			_recenter_viewer = _call_bridge_result(
				"GetExplorerCanonicalPosition", "get_explorer_canonical_position", Vector3.ZERO) as Vector3
			# Emulate two one-second samples of a 1 km/s noclip route. The second
			# sample crosses the 1,920 m (8% of 24 km) relocation threshold.
			_bridge.call("SetBenchmarkViewerPosition", _recenter_viewer.x + 1000.0, _recenter_viewer.y, _recenter_viewer.z)
			_recenter_stage = 1
		1:
			if installed_center != _recenter_near_center or near_dispatches != _recenter_near_dispatches or pending:
				return _fail("Near cascade changed before the 1,920 m recenter threshold")
			_bridge.call("SetBenchmarkViewerPosition", _recenter_viewer.x + 2000.0, _recenter_viewer.y, _recenter_viewer.z)
			_recenter_stage = 2
		2:
			if installed_center != _recenter_near_center and near_dispatches <= _recenter_near_dispatches:
				return _fail("Near cascade changed center before its requested GPU dispatch")
			if near_dispatches > _recenter_near_dispatches:
				_recenter_dispatch_seen_frame = _frame
				_recenter_stage = 4
			elif pending:
				_recenter_stage = 3
		3:
			if installed_center != _recenter_near_center and near_dispatches <= _recenter_near_dispatches:
				return _fail("Near cascade changed center while its GPU dispatch was still pending")
			if near_dispatches <= _recenter_near_dispatches:
				return false
			# SceneTree ordering may observe the producer count just before the
			# bridge's next _Process promotes the staged receiver state.
			_recenter_dispatch_seen_frame = _frame
			_recenter_stage = 4
		4:
			if near_dispatches <= _recenter_near_dispatches:
				return false
			if pending or installed_center == _recenter_near_center:
				if _frame <= _recenter_dispatch_seen_frame + 2:
					return false
				return _fail("Near cascade did not publish its recenter after the GPU dispatch")
			_passed_recenter_sync = true
			print("Sunshine GPU shadow recenter sync passed: 1 km/s, threshold=1920m")
	return _passed_recenter_sync


func _call_bridge(pascal_name: StringName, snake_name: StringName) -> void:
	var method_name := pascal_name if _bridge.has_method(pascal_name) else snake_name
	if not _bridge.has_method(method_name):
		_fail("Sunshine bridge method is unavailable: %s / %s" % [pascal_name, snake_name])
		return
	_bridge.call(method_name)


func _call_bridge_result(
		pascal_name: StringName,
		snake_name: StringName,
		fallback: Variant,
		arguments: Array = []) -> Variant:
	var method_name := pascal_name if _bridge.has_method(pascal_name) else snake_name
	return _bridge.callv(method_name, arguments) if _bridge.has_method(method_name) else fallback


func _combined_temporal_occlusion(value: Color) -> float:
	# GPU maps keep the two interleaved vertical-integration phases in R/G.
	# Legacy single-channel maps have G=0, for which this remains exactly R.
	return 1.0 - (1.0 - value.r) * (1.0 - value.g)


func _fail(message: String) -> bool:
	push_error(message)
	quit(1)
	return true
