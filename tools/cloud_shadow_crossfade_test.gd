extends SceneTree

## GPU test for the cloud shadow crossfade (S2). Windowed (needs a RenderingDevice).
##
## Runs the integration scene twice with the same settings: crossfade off, then on. At a grid of fixed
## ground receivers it CPU-samples the map the receivers bind (Texture2DRD wrapper) every frame with the
## receiver projection (bilinear, offsets from the shader globals) and reports the largest per-frame change
## in cloud occlusion. Crossfade on must (a) lower that maximum when the off run has real pops, (b) finish
## every fade within one publish interval (+ slack) and (c) end each fade on exactly the front map.
##
## Usage: Godot --path . --resolution 1280x720 --script tools/cloud_shadow_crossfade_test.gd
## Flags: --seconds=14 (sample length per stage), --near-hz=1 --far-hz=0.5 (publish rates), --fade=0.4 (crossfade seconds, 0 = publish interval)

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MAX_FRAMES := 12000
const WARMUP_DISPATCHES := 3

var _scene: Node
var _bridge: Node
var _clouds: Resource
var _day_night: Node
var _frame := 0
var _stage := 0
var _sample_seconds := 14.0
var _near_hz := 1.0
var _far_hz := 0.5
var _fade_seconds := 0.4
var _stage_start_msec := 0
var _stage_dispatch_base := 0
var _points: Array[Vector3] = []
var _prev_values := PackedFloat32Array()
var _have_prev := false
var _max_delta := {}
var _sum_delta := {}
var _samples := {}
var _fade_publish_msec := 0
var _last_dispatches := 0
var _fade_durations: Array[float] = []
var _fade_open := false
var _was_running := false
var _equal_checks := 0
var _equal_fail := 0


func _initialize() -> void:
	for arg in OS.get_cmdline_user_args() + OS.get_cmdline_args():
		if arg.begins_with("--seconds="): _sample_seconds = float(arg.get_slice("=", 1))
		elif arg.begins_with("--near-hz="): _near_hz = float(arg.get_slice("=", 1))
		elif arg.begins_with("--far-hz="): _far_hz = float(arg.get_slice("=", 1))
		elif arg.begins_with("--fade="): _fade_seconds = float(arg.get_slice("=", 1))
	_scene = TEST_SCENE.instantiate()
	_bridge = _scene.get_node("SunshineCloudBridge")
	_clouds = _scene.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	root.add_child(_scene)
	_day_night = _scene.get_node("RingDayNightCycle")
	_day_night.set_process(false)
	_day_night.set_physics_process(false)
	_day_night.set("Paused", true)


func _apply_settings() -> void:
	_bridge.set("CastCloudShadows", true)
	_bridge.set("PreferGpuCloudShadows", true)
	_bridge.set("GpuNearShadowMapResolution", 512)
	_bridge.set("GpuFarShadowMapResolution", 128)
	_bridge.set("GpuNearShadowRadiusMeters", 40000.0)
	_bridge.set("GpuFarShadowRadiusMeters", 100000.0)
	_bridge.set("GpuNearShadowUpdatesPerSecond", _near_hz)
	_bridge.set("GpuFarShadowUpdatesPerSecond", _far_hz)
	_bridge.set("GpuNearShadowSliceCount", 4)
	_bridge.set("CloudWindMultiplier", 3.0)
	_bridge.set("GpuShadowCrossfadeEnabled", false)
	_bridge.set("GpuShadowCrossfadeSeconds", _fade_seconds)


func _status() -> Dictionary:
	return _clouds.call("get_gpu_cloud_shadow_status") as Dictionary


func _read_texture(texture: Texture2DRD) -> PackedByteArray:
	return RenderingServer.get_rendering_device().texture_get_data(texture.texture_rd_rid, 0)


func _near_frame() -> Dictionary:
	return _bridge.call("GetCloudShadowReceiverFrame", 0) as Dictionary


## Occlusion (1 - (1-r)(1-g)) of the near map at a fixed world point, flat projection (valid within ~10 km).
func _occlusion(bytes: PackedByteArray, size: int, frame: Dictionary, world: Vector3, radius: float) -> float:
	var relative: Vector3 = world - (frame["center"] as Vector3)
	var planar := Vector2(relative.dot(frame["around"]), relative.dot(frame["across"]))
	var uv: Vector2 = planar / (radius * 2.0) + Vector2(0.5, 0.5) + (frame["offset"] as Vector2)
	if uv.x < 0.0 or uv.y < 0.0 or uv.x > 1.0 or uv.y > 1.0:
		return 0.0
	var fx := clampf(uv.x * size - 0.5, 0.0, size - 1.0)
	var fy := clampf(uv.y * size - 0.5, 0.0, size - 1.0)
	var x0 := int(fx)
	var y0 := int(fy)
	var x1 := mini(x0 + 1, size - 1)
	var y1 := mini(y0 + 1, size - 1)
	var tx := fx - x0
	var ty := fy - y0
	var total := 0.0
	for corner in [[x0, y0, (1.0 - tx) * (1.0 - ty)], [x1, y0, tx * (1.0 - ty)], [x0, y1, (1.0 - tx) * ty], [x1, y1, tx * ty]]:
		var offset: int = (int(corner[1]) * size + int(corner[0])) * 4
		var r := bytes.decode_half(offset)
		var g := bytes.decode_half(offset + 2)
		total += (1.0 - (1.0 - r) * (1.0 - g)) * float(corner[2])
	return total


## Fixed ground receivers placed on soft cloud-shadow edges of the live near map (pops show there).
func _pick_receivers(frame: Dictionary) -> Array[Vector3]:
	var texture := _clouds.call("get_gpu_cloud_shadow_texture", 0) as Texture2DRD
	var picked: Array[Vector3] = []
	if texture == null:
		return picked
	var bytes := _read_texture(texture)
	var size := int(sqrt(bytes.size() / 4.0))
	var radius := float(frame["radius"])
	var edge: Array[Vector3] = []
	var any: Array[Vector3] = []
	for y in range(int(size * 0.2), int(size * 0.8), 3):
		for x in range(int(size * 0.2), int(size * 0.8), 3):
			var offset := (y * size + x) * 4
			var r := bytes.decode_half(offset)
			var g := bytes.decode_half(offset + 2)
			var value := 1.0 - (1.0 - r) * (1.0 - g)
			var uv := Vector2((x + 0.5) / size, (y + 0.5) / size) - (frame["offset"] as Vector2)
			var planar := (uv - Vector2(0.5, 0.5)) * (radius * 2.0)
			var world: Vector3 = (frame["center"] as Vector3) + (frame["around"] as Vector3) * planar.x + (frame["across"] as Vector3) * planar.y
			if value > 0.15 and value < 0.85:
				edge.append(world)
			elif value > 0.02:
				any.append(world)
	var source := edge if edge.size() >= 20 else edge + any
	var stride := maxi(1, source.size() / 40)
	for i in range(0, source.size(), stride):
		picked.append(source[i])
	return picked


func _begin_stage(label: String) -> void:
	_stage_start_msec = Time.get_ticks_msec()
	_have_prev = false
	_max_delta[label] = 0.0
	_sum_delta[label] = 0.0
	_samples[label] = 0
	_fade_open = false
	_last_dispatches = int(_status().get("near_dispatches", 0))


func _sample(label: String) -> void:
	var texture := _clouds.call("get_gpu_cloud_shadow_texture", 0) as Texture2DRD
	if texture == null:
		return
	var bytes := _read_texture(texture)
	var size := int(sqrt(bytes.size() / 4.0))
	var frame := _near_frame()
	var radius := float(frame["radius"])
	var values := PackedFloat32Array()
	for point in _points:
		values.append(_occlusion(bytes, size, frame, point, radius))
	if _have_prev and values.size() == _prev_values.size():
		for i in values.size():
			var delta := absf(values[i] - _prev_values[i])
			_max_delta[label] = maxf(_max_delta[label], delta)
			_sum_delta[label] += delta
			_samples[label] += 1
	_prev_values = values
	_have_prev = true


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 1:
		# Applied after _ready so the gameplay cloud defaults cannot override them.
		_apply_settings()
	if _frame == 2:
		_day_night.call("set_cycle_fraction", 0.0)
	if _frame >= MAX_FRAMES:
		push_error("Timed out in stage %d: %s" % [_stage, str(_status())])
		quit(1)
		return true
	if _frame < 40:
		return false
	var status := _status()
	var dispatches := int(status.get("near_dispatches", 0))
	var far_dispatches := int(status.get("far_dispatches", 0))
	match _stage:
		0:
			# Crossfade off: warm up, then pick the receivers from the live frame.
			if dispatches >= WARMUP_DISPATCHES and far_dispatches >= 1 and not bool(status.get("near_crossfade", false)) 					and status.get("near_published_resolution", Vector2i.ZERO) == Vector2i(512, 512) 					and not bool(_bridge.call("IsGpuShadowCascadePublishPending", 0)):
				var frame := _near_frame()
				if frame["center"] == null or (frame["center"] as Vector3) == Vector3.ZERO:
					return false
				_points = _pick_receivers(frame)
				if _points.size() < 10:
					return false
				_begin_stage("off")
				_stage = 1
		1:
			_sample("off")
			if (Time.get_ticks_msec() - _stage_start_msec) * 0.001 >= _sample_seconds:
				_bridge.set("GpuShadowCrossfadeEnabled", true)
				_stage = 2
				_stage_dispatch_base = dispatches
		2:
			# Switching rebuilds the maps: wait for the display texture and a few publishes.
			if bool(status.get("near_crossfade", false)) and bool(status.get("far_crossfade", false)) \
					and dispatches >= _stage_dispatch_base + WARMUP_DISPATCHES:
				_begin_stage("on")
				_fade_publish_msec = Time.get_ticks_msec()
				_stage = 3
		3:
			_sample("on")
			var now := Time.get_ticks_msec()
			if dispatches > _last_dispatches:
				_last_dispatches = dispatches
				_fade_publish_msec = now
				_fade_open = true
			var running := bool(status.get("near_fade_running", false))
			if _fade_open and _was_running and not running:
				# Fade just finished: the display must now equal the front map exactly.
				_fade_durations.append((now - _fade_publish_msec) * 0.001)
				_fade_open = false
				var front_rid: RID = (_clouds.get("cloud_shadow_textures") as Array)[0]
				var display := _clouds.call("get_gpu_cloud_shadow_texture", 0) as Texture2DRD
				if front_rid.is_valid() and display != null:
					var rd := RenderingServer.get_rendering_device()
					var front := rd.texture_get_data(front_rid, 0)
					var shown := rd.texture_get_data(display.texture_rd_rid, 0)
					var again := _status()
					if int(again.get("near_dispatches", 0)) == dispatches and int(again.get("near_slice_progress", 0)) == 0:
						_equal_checks += 1
						if front != shown:
							_equal_fail += 1
			_was_running = running
			if (now - _stage_start_msec) * 0.001 >= _sample_seconds:
				_stage = 4
		4:
			_report()
			return true
	return false


func _report() -> void:
	var interval := 1.0 / _near_hz
	var expected := _fade_seconds if _fade_seconds > 0.0 else interval
	var worst_fade := 0.0
	for duration in _fade_durations:
		worst_fade = maxf(worst_fade, duration)
	var off_max: float = _max_delta["off"]
	var on_max: float = _max_delta["on"]
	var off_mean: float = _sum_delta["off"] / maxf(1.0, _samples["off"])
	var on_mean: float = _sum_delta["on"] / maxf(1.0, _samples["on"])
	print("CROSSFADE_MAX_FRAME_DELTA off=%.5f on=%.5f ratio=%.3f mean_off=%.6f mean_on=%.6f points=%d samples=%d/%d"
		% [off_max, on_max, on_max / maxf(off_max, 1e-9), off_mean, on_mean, _points.size(), _samples["off"], _samples["on"]])
	print("CROSSFADE_FADE interval=%.2fs fades=%d worst=%.3fs settled_checks=%d mismatches=%d"
		% [interval, _fade_durations.size(), worst_fade, _equal_checks, _equal_fail])
	var failed := false
	if _samples["off"] < 100 or _samples["on"] < 100 or _fade_durations.size() < 3:
		push_error("Too few samples or publishes to judge the crossfade")
		failed = true
	if off_max > 0.002 and on_max > off_max * 0.75:
		push_error("Crossfade did not reduce the per-frame shadow change (off %.5f on %.5f)" % [off_max, on_max])
		failed = true
	elif off_max <= 0.002:
		push_warning("Crossfade-off run had no visible pops (max delta %.5f); reduction not judged" % off_max)
	if worst_fade > minf(expected, interval) * 1.5 + 0.15:
		push_error("A crossfade lagged more than one publish interval (%.3fs > %.3fs)" % [worst_fade, minf(expected, interval)])
		failed = true
	# With fade = publish interval the fade ends as the next publish arrives, so settle checks only exist for shorter fades.
	var settle_required := _fade_seconds > 0.0 and _fade_seconds < interval
	if (settle_required and _equal_checks < 5) or _equal_fail > 0:
		push_error("Display map did not settle exactly on the front map (%d/%d mismatches)" % [_equal_fail, _equal_checks])
		failed = true
	if failed:
		quit(1)
		return
	print("Cloud shadow crossfade test passed")
	quit(0)
