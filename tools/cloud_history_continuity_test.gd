extends SceneTree

# CPU-only contract coverage for the wind-advection-aware cloud history
# continuity measure (SunshineClouds._evaluate_density_continuity).
const CLOUDS := preload("res://addons/SunshineClouds2/SunshineClouds.gd")
const DETAIL_VELOCITY := Vector3(250.0, 250.0, 0.0)
const MEDIUM_VELOCITY := Vector3(140.0, 0.0, 0.0)
const WEATHER_VELOCITY := 30.0
const FRAME := 1.0 / 30.0
var failed := false

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_test_steady_storm_advection()
	_test_hitch_frame()
	_test_teleport()
	_test_noise_wrap()
	_test_shape_edit()
	_test_small_shape_drift()
	_test_moderate_excess()
	if failed: quit(1)
	print("CLOUD_HISTORY_CONTINUITY_TEST=PASS")
	quit(0)

func _require(condition: bool, reason: String) -> void:
	if not condition and not failed:
		failed = true
		push_error("CLOUD_HISTORY_CONTINUITY_TEST=FAIL " + reason)
		print("CLOUD_HISTORY_CONTINUITY_TEST=FAIL " + reason)

func _new_clouds() -> Variant:
	var clouds = CLOUDS.new()
	clouds.cloud_weather_frame = Vector4(1_000_000.0, 0.0, 0.0, 0.0)
	return clouds

# Places the advected quantities at time t of the steady storm.
func _place(clouds: Variant, t: float) -> void:
	clouds.detail_clouds_position = DETAIL_VELOCITY * t
	clouds.medium_scale_clouds_position = MEDIUM_VELOCITY * t
	clouds.cloud_weather_frame = Vector4(1_000_000.0, WEATHER_VELOCITY * t, 0.0, 0.0)

# Returns the evaluation of the current clouds state against the previous capture.
func _step(clouds: Variant, previous: Array, dt: float) -> Dictionary:
	var current: Array = clouds._capture_density_state()
	var result: Dictionary = clouds._evaluate_density_continuity(current, previous, dt)
	result["state"] = current
	return result

# Runs `frames` steady frames; returns [clouds, previous capture, time].
func _steady_stream(frames: int, check_after: int = 2) -> Array:
	var clouds = _new_clouds()
	var t := 0.0
	_place(clouds, t)
	var previous: Array = clouds._capture_density_state()
	for frame in frames:
		t += FRAME
		_place(clouds, t)
		var result := _step(clouds, previous, FRAME)
		previous = result.state
		if frame >= check_after:
			_require(float(result.excess) <= 1.0 and not bool(result.hard), "steady frame %d excess %s term %s" % [frame, result.excess, result.term])
	return [clouds, previous, t]

func _test_steady_storm_advection() -> void:
	_steady_stream(30)

func _test_hitch_frame() -> void:
	var stream := _steady_stream(10)
	var clouds = stream[0]
	var t: float = stream[2] + 0.25
	_place(clouds, t)
	var result := _step(clouds, stream[1], 0.25)
	_require(float(result.excess) <= 1.0 and not bool(result.hard), "hitch frame excess %s term %s" % [result.excess, result.term])

func _test_teleport() -> void:
	var stream := _steady_stream(10)
	var clouds = stream[0]
	var t: float = stream[2] + FRAME
	_place(clouds, t)
	clouds.detail_clouds_position += Vector3(5000.0, 0.0, 0.0)
	var result := _step(clouds, stream[1], FRAME)
	_require(bool(result.hard) and float(result.multiplier) == 0.0, "teleport was not a hard reset: excess %s" % result.excess)
	# The teleported slot lost its velocity, so the next steady frame is judged
	# against the unpredicted-advection allowance: it is not hard and in fact <= 1.
	t += FRAME
	_place(clouds, t)
	clouds.detail_clouds_position += Vector3(5000.0, 0.0, 0.0)
	var after := _step(clouds, result.state, FRAME)
	_require(not bool(after.hard) and float(after.excess) <= 1.0, "frame after teleport excess %s term %s" % [after.excess, after.term])

func _test_noise_wrap() -> void:
	var stream := _steady_stream(10)
	var clouds = stream[0]
	var t: float = stream[2] + FRAME
	_place(clouds, t)
	clouds.medium_scale_clouds_position += Vector3(-clouds.medium_noise_scale, 0.0, 0.0)
	var result := _step(clouds, stream[1], FRAME)
	_require(not bool(result.hard) and float(result.excess) <= 1.0, "noise wrap excess %s term %s" % [result.excess, result.term])

func _test_shape_edit() -> void:
	var stream := _steady_stream(6)
	var clouds = stream[0]
	var t: float = stream[2] + FRAME
	_place(clouds, t)
	clouds.clouds_detail_power += 0.25
	var result := _step(clouds, stream[1], FRAME)
	_require(bool(result.hard), "shape edit was not a hard reset: excess %s" % result.excess)

func _test_small_shape_drift() -> void:
	var stream := _steady_stream(6)
	var clouds = stream[0]
	var t: float = stream[2] + FRAME
	_place(clouds, t)
	clouds.clouds_detail_power *= 1.0005
	var result := _step(clouds, stream[1], FRAME)
	_require(float(result.excess) <= 1.0 and not bool(result.hard), "small shape drift excess %s term %s" % [result.excess, result.term])

func _test_moderate_excess() -> void:
	var clouds = _new_clouds()
	clouds.history_continuity_hard_reset_ratio = 8.0
	clouds.history_continuity_allowance_scale = 1.0
	var previous: Array = clouds._capture_density_state()
	# Stationary field: velocities settle at zero.
	for frame in 4:
		var settled := _step(clouds, previous, FRAME)
		previous = settled.state
	var base := minf(maxf(clouds.min_step_distance, 1.0) * 0.1, maxf(clouds.small_noise_scale, 1.0) * 0.001)
	clouds.detail_clouds_position += Vector3(base * 3.0, 0.0, 0.0)
	var result := _step(clouds, previous, FRAME)
	_require(not bool(result.hard) and float(result.multiplier) > 0.0 and float(result.multiplier) < 1.0, "moderate excess multiplier %s excess %s" % [result.multiplier, result.excess])
	_require(absf(float(result.excess) - 3.0) < 0.01, "moderate excess ratio %s" % result.excess)
