extends SceneTree

## T3R-10d: the water sound measured on the real output (see tools/WaterAmbienceCaptureTest.cs). Headless. Run with tools/run_tests.ps1 -Tests water_ambience_capture_test.
const TEST := preload("res://tools/WaterAmbienceCaptureTest.cs")

func _initialize() -> void:
	var runner = TEST.new()
	root.add_child(runner)
