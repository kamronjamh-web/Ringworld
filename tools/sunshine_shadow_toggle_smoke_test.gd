extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MAX_FRAMES := 600

var _scene: Node
var _bridge: Node
var _clouds: Resource
var _day_night_cycle: Node
var _frame := 0
var _state := 0
var _state_started := 0
var _dispatches_before_pause := Vector2i.ZERO
var _dispatches_before_producer_refresh := Vector2i.ZERO
var _dispatches_before_resize := Vector2i.ZERO
var _near_texture_before_resize_id := 0
var _far_texture_before_resize_id := 0


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate()
	root.add_child(_scene)
	_bridge = _scene.get_node("SunshineCloudBridge")
	_clouds = _scene.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	_day_night_cycle = _scene.get_node("RingDayNightCycle")
	_day_night_cycle.set_process(false)
	_bridge.set("CastCloudShadows", true)
	_bridge.set("PreferGpuCloudShadows", true)
	_bridge.set("GpuNearShadowMapResolution", 256)
	_bridge.set("GpuFarShadowMapResolution", 128)
	_bridge.set("GpuNearShadowUpdatesPerSecond", 6.0)
	_bridge.set("GpuFarShadowUpdatesPerSecond", 1.0)
	_bridge.set("GpuShadowVerticalSamples", 4)
	_bridge.set("GpuShadowGenerationEnabled", true)
	_bridge.set("CloudShadowReceiversEnabled", true)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 2:
		_day_night_cycle.call("set_cycle_fraction", 0.0)

	var status := _clouds.call("get_gpu_cloud_shadow_status") as Dictionary
	var dispatches := Vector2i(
		int(status.get("near_dispatches", 0)),
		int(status.get("far_dispatches", 0)))
	var projector_enabled := bool(_bridge.call("IsCloudShadowProjectorEnabled"))

	match _state:
		0:
			if dispatches.x >= 2 and dispatches.y >= 1 and projector_enabled:
				_dispatches_before_pause = dispatches
				_bridge.set("GpuShadowGenerationEnabled", false)
				_bridge.set("CloudShadowReceiversEnabled", false)
				_clouds.call("set_gpu_cloud_shadow_generation_enabled", false)
				# Force Sunshine's normal viewport-resource recreation path while
				# generation is paused. Receivers must release the retired RD maps.
				_clouds.call("refresh_compute")
				_advance_state()
		1:
			if _frame >= _state_started + 45:
				if bool(status.get("generation_enabled", true)):
					return _fail("Addon producer did not pause")
				if dispatches != _dispatches_before_pause:
					return _fail("Paused producer dispatched after resource refresh: %s -> %s" % [
						str(_dispatches_before_pause), str(dispatches)])
				if projector_enabled:
					return _fail("Receivers remained enabled while profiling toggle was off")
				_bridge.set("GpuShadowGenerationEnabled", true)
				_clouds.call("set_gpu_cloud_shadow_generation_enabled", true)
				_advance_state()
		2:
			if dispatches.x > _dispatches_before_pause.x \
					and dispatches.y > _dispatches_before_pause.y:
				if projector_enabled:
					return _fail("Producer-only mode unexpectedly enabled receivers")
				_dispatches_before_producer_refresh = dispatches
				# Repeat the rebuild with generation active and receivers disabled;
				# this catches stale Texture2DRD bindings in producer-only mode.
				_clouds.call("refresh_compute")
				_advance_state()
			elif _frame >= _state_started + 120:
				return _fail("Producer did not recover after paused resource refresh: %s" % str(status))
		3:
			if dispatches.x > _dispatches_before_producer_refresh.x \
					and dispatches.y > _dispatches_before_producer_refresh.y:
				if projector_enabled:
					return _fail("Producer-only refresh unexpectedly enabled receivers")
				_bridge.set("CloudShadowReceiversEnabled", true)
				_advance_state()
			elif _frame >= _state_started + 120:
				return _fail("Producer-only resource refresh did not recover: %s" % str(status))
		4:
			if projector_enabled:
				var near_texture := _clouds.call("get_gpu_cloud_shadow_texture", 0) as Texture2D
				var far_texture := _clouds.call("get_gpu_cloud_shadow_texture", 1) as Texture2D
				if near_texture == null or far_texture == null:
					return _fail("Receivers recovered without both published textures")
				_dispatches_before_resize = dispatches
				_near_texture_before_resize_id = near_texture.get_instance_id()
				_far_texture_before_resize_id = far_texture.get_instance_id()
				_bridge.set("GpuNearShadowMapResolution", 384)
				_bridge.set("GpuFarShadowMapResolution", 192)
				_bridge.call("ApplyTemporalQualityTuning", true)
				if bool(_bridge.call("IsCloudShadowProjectorEnabled")):
					return _fail("Live resize did not detach cloud-shadow receivers synchronously")
				_advance_state()
			elif _frame >= _state_started + 30:
				return _fail("Receivers did not recover after profiling toggle was restored")
		5:
			var near_published := status.get("near_published_resolution", Vector2i.ZERO) as Vector2i
			var far_published := status.get("far_published_resolution", Vector2i.ZERO) as Vector2i
			var replacements_ready := near_published == Vector2i(384, 384) \
					and far_published == Vector2i(192, 192) \
					and dispatches.x > _dispatches_before_resize.x \
					and dispatches.y > _dispatches_before_resize.y
			if projector_enabled and not replacements_ready:
				return _fail("Receivers rebound before both resized cascades were published: %s" % str(status))
			if projector_enabled and replacements_ready:
				var near_texture := _clouds.call("get_gpu_cloud_shadow_texture", 0) as Texture2D
				var far_texture := _clouds.call("get_gpu_cloud_shadow_texture", 1) as Texture2D
				if near_texture == null or far_texture == null:
					return _fail("Resized cascades reported ready without published textures")
				if near_texture.get_instance_id() == _near_texture_before_resize_id \
						or far_texture.get_instance_id() == _far_texture_before_resize_id:
					return _fail("Live resize reused a superseded Texture2DRD wrapper")
				_advance_state()
			if _frame >= _state_started + 160:
				return _fail("Live cloud-shadow resize did not publish and rebind: %s" % str(status))
		6:
			var retired_cascades := int(status.get("retired_cascade_generations", -1))
			var retired_compositors := int(status.get("retired_compositor_generations", -1))
			if retired_cascades == 0 and retired_compositors == 0:
				print("SUNSHINE_SHADOW_TOGGLE_SMOKE=PASS paused=%s recovered=%s producerRefresh=%s resized=%s retired=0" % [
					str(_dispatches_before_pause), str(_dispatches_before_producer_refresh), str(dispatches),
					str(Vector2i(384, 192))])
				quit(0)
				return true
			if _frame >= _state_started + 30:
				return _fail("Retired cloud-shadow GPU generations did not drain: %s" % str(status))

	if _frame >= MAX_FRAMES:
		return _fail("Timed out in toggle state %d with status=%s" % [_state, str(status)])
	return false


func _advance_state() -> void:
	_state += 1
	_state_started = _frame


func _fail(message: String) -> bool:
	push_error(message)
	quit(1)
	return true
