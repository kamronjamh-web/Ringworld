extends SceneTree

## Headless behavioral contract: unfinished streaming cannot become a valid stationary baseline,
## and an arbitrary requested speed cannot silently select a different gameplay flight tier.
## Haze label undo must preserve the actual model, integration quality and enabled state.
## Run through tools/run_tests.ps1 -Tests quality_preset_capture_policy_test.
const POLICY := preload("res://tools/quality_preset_capture_policy.gd")

class HazeDrawer:
	extends RefCounted
	var state := {"model": 0, "quality": 8, "enabled": false}
	func SetAtmosphereHazeModel(value: int) -> void:
		state["model"] = value
	func SetAtmosphereHazeQuality(value: int) -> void:
		state["quality"] = value
	func SetAtmosphereEnabled(value: bool) -> void:
		state["enabled"] = value

func _init() -> void:
	var errors: Array[String] = []
	for baseline in [{"haze_model": 1, "haze_quality": 4, "enabled": true}, {"haze_model": 0, "haze_quality": 8, "enabled": false}, {"haze_model": 1, "haze_quality": 8, "enabled": false}]:
		var snapshot := POLICY.haze_state(baseline)
		var drawer := HazeDrawer.new()
		for quality in [4, 8]:
			drawer.SetAtmosphereHazeModel(1)
			drawer.SetAtmosphereHazeQuality(quality)
			drawer.SetAtmosphereEnabled(true)
			POLICY.restore_haze(drawer, snapshot)
			if drawer.state != snapshot:
				errors.append("haze label contaminated restored baseline: " + str(snapshot))
		# The ablation must restore a Physical baseline too, preserving its model and integration quality.
		drawer.SetAtmosphereEnabled(false)
		POLICY.restore_haze(drawer, snapshot)
		if drawer.state != snapshot:
			errors.append("haze ablation did not restore baseline")
	if not POLICY.stationary_invalid(true, 0, 100).is_empty():
		errors.append("settled nonempty stationary window rejected")
	if POLICY.stationary_invalid(false, 0, 100).is_empty():
		errors.append("timeout accepted")
	if POLICY.stationary_invalid(true, 1, 100).is_empty():
		errors.append("resumed streaming accepted")
	if POLICY.stationary_invalid(true, 0, 0).is_empty():
		errors.append("empty window accepted")
	if POLICY.flight_tier(72.0) != 1 or POLICY.flight_tier(240.0) != 2:
		errors.append("real gameplay tiers rejected")
	for speed in [0.0, 12.0, 71.0, 150.0, 241.0]:
		if POLICY.flight_tier(speed) != -1:
			errors.append("unsupported flight speed accepted: " + str(speed))
	if not POLICY.flight_invalid(true, true, 72.0, 71.9).is_empty():
		errors.append("measured gameplay flight rejected")
	for actual in [0.0, 12.0, 240.0, NAN]:
		if POLICY.flight_invalid(true, true, 72.0, actual).is_empty():
			errors.append("wrong measured flight speed accepted")
	if POLICY.flight_invalid(false, true, 72.0, 72.0).is_empty():
		errors.append("refused flight teleport accepted")
	if not POLICY.walk_invalid(true, true, true, false, 7.5, 0.95, 0.0).is_empty():
		errors.append("grounded traversal rejected")
	if POLICY.walk_invalid(true, true, true, false, 0.0, 1.0, 1.0).is_empty():
		errors.append("zero movement accepted as walking")
	if POLICY.walk_invalid(true, true, true, false, 7.5, 0.1, 0.0).is_empty():
		errors.append("airborne traversal accepted as walking")
	if POLICY.walk_invalid(true, true, true, false, 2.0, 1.0, 0.8).is_empty():
		errors.append("mostly blocked traversal accepted")
	if errors.is_empty():
		print("QP_CAPTURE_POLICY=PASS")
		quit(0)
	else:
		push_error("QP_CAPTURE_POLICY=FAIL " + str(errors))
		quit(1)
