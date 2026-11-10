extends SceneTree

## Landscape audit T0-1 contract test (docs/landscape-audit-t0-spec.md WO T0-1): the pinned generation 1 world, the landscape classes and the committed pose fixture.
## RUN: Godot_console.exe --headless --path . --script res://tools/landscape_world_test.gd [-- --capture | --find | --compare-slot=<abs world.json> | --fixture=<path>]
##   --capture  runs the pose finder and writes tools/fixtures/landscape_poses_v8_s1337.json (refuses to overwrite)
##   --find     runs the finder and prints the differences from the committed fixture without failing
## Prints LANDSCAPE_WORLD=PASS, or LANDSCAPE_WORLD=FAIL <reason> through push_error (exit 1).
const TEST := preload("res://tools/LandscapeWorldTest.cs")

func _initialize() -> void:
	var watchdog := create_timer(240.0)
	watchdog.timeout.connect(func() -> void:
		push_error("LANDSCAPE_WORLD=FAIL watchdog timeout")
		quit(1))
	var test: RefCounted = TEST.new()
	if not bool(test.call("Run")):
		quit(1)
		return
	print("LANDSCAPE_WORLD=PASS")
	quit(0)
