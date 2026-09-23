extends SceneTree

# Deterministic companion coverage for RingSunshineCloudBridge's atan2
# estimator. It deliberately exercises the small 1x solar motion at high FPS
# in several orientations, plus a sustained accelerated rotation and a single
# discontinuity. Keep this independent of a rendered scene so it is suitable
# for CI/headless execution.
const THRESHOLD_DEGREES_PER_SECOND := 3.0
const DISCONTINUITY_DEGREES := 8.0
const HISTORY_ALPHA_AGREEMENT_START := 0.20
const HISTORY_ALPHA_AGREEMENT_END := 0.65

func _initialize() -> void:
	for axis in [Vector3.UP, Vector3.RIGHT, Vector3(1.0, 2.0, 3.0).normalized()]:
		for fps in [60.0, 240.0, 1000.0]:
			var previous := Vector3.FORWARD.rotated(axis, 0.41).normalized()
			var current := previous.rotated(axis, deg_to_rad(0.3 / fps)).normalized()
			var speed: float = _angular_distance_degrees(previous, current) * fps
			_assert(speed < THRESHOLD_DEGREES_PER_SECOND, "1x 0.3 deg/s falsely suppresses at %s FPS: %s" % [fps, speed])

	var accelerated_previous := Vector3.FORWARD
	var accelerated_current := accelerated_previous.rotated(Vector3.UP, deg_to_rad(9.0 / 60.0))
	_assert(_angular_distance_degrees(accelerated_previous, accelerated_current) * 60.0 > THRESHOLD_DEGREES_PER_SECOND, "accelerated rotation was not detected")
	var discontinuous := accelerated_previous.rotated(Vector3.UP, deg_to_rad(35.0))
	_assert(_angular_distance_degrees(accelerated_previous, discontinuous) >= DISCONTINUITY_DEGREES, "large rotation was not detected as discontinuous")

	# Soft-edge density jitter must retain the requested history weight. A true
	# silhouette replacement still has to retire it completely to avoid trails.
	_assert(is_equal_approx(_history_agreement_decay(0.95, 0.12), 0.95), "ordinary cloud jitter discarded temporal history")
	_assert(_history_agreement_decay(0.95, 0.35) > 0.0, "moderate alpha disagreement discarded all temporal history")
	_assert(is_zero_approx(_history_agreement_decay(0.95, 0.80)), "new cloud silhouette retained stale history")
	var source := FileAccess.get_file_as_string("res://addons/SunshineClouds2/SunshineCloudsCompute.glsl")
	_assert(source.contains("smoothstep(0.20, 0.65, alphaMismatch)"), "compute shader history-agreement policy drifted from the deterministic test")
	print("Sunshine sun-history angular-speed deterministic test: PASS")
	quit(0)

func _angular_distance_degrees(previous: Vector3, current: Vector3) -> float:
	var a := previous.normalized()
	var b := current.normalized()
	return rad_to_deg(atan2(a.cross(b).length(), clampf(a.dot(b), -1.0, 1.0)))

func _history_agreement_decay(decay: float, alpha_mismatch: float) -> float:
	var normalized := clampf(
		(alpha_mismatch - HISTORY_ALPHA_AGREEMENT_START)
		/ (HISTORY_ALPHA_AGREEMENT_END - HISTORY_ALPHA_AGREEMENT_START),
		0.0,
		1.0)
	var rejection := normalized * normalized * (3.0 - 2.0 * normalized)
	return decay * (1.0 - rejection)

func _assert(condition: bool, message: String) -> void:
	if condition:
		return
	push_error(message)
	quit(1)
