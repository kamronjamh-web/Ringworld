extends SceneTree

## T4-2 contract test of the dressing pose fixture (tools/fixtures/landscape_poses_dressing_s1337.json) on the pinned generation 1 world (steep, scree and cliff face rules, class, ground, site-free ring).
## RUN: Godot_console.exe --headless --path . --script res://tools/landscape_dressing_poses_test.gd [-- --capture | --find | --fixture=<path>]
##   --capture  runs the finder and writes the fixture (refuses to overwrite)
##   --find     runs the finder and prints the differences from the committed fixture without failing
## Prints LANDSCAPE_DRESSING_POSES=PASS, or LANDSCAPE_DRESSING_POSES=FAIL <reason> through push_error (exit 1).
const TEST := preload("res://tools/LandscapeDressingPosesTest.cs")

func _initialize() -> void:
	var watchdog := create_timer(600.0)
	watchdog.timeout.connect(func() -> void:
		push_error("LANDSCAPE_DRESSING_POSES=FAIL watchdog timeout")
		quit(1))
	var test: RefCounted = TEST.new()
	if not bool(test.call("Run")):
		quit(1)
		return
	print("LANDSCAPE_DRESSING_POSES=PASS")
	quit(0)
