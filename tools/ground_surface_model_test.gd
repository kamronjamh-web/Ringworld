extends SceneTree

## T4-3: shape checks of the C# ground surface model (rock exposure, scree, strata filter, slope bias) and the rock-mean constant.
## RUN: Godot_console.exe --headless --path . --script res://tools/ground_surface_model_test.gd
## Prints GROUND_SURFACE_MODEL=PASS, or GROUND_SURFACE_MODEL=FAIL <reason> through push_error (exit 1).
const TEST := preload("res://tools/GroundSurfaceModelTest.cs")

func _initialize() -> void:
	var test: RefCounted = TEST.new()
	if not bool(test.call("Run")):
		quit(1)
		return
	print("GROUND_SURFACE_MODEL=PASS")
	quit(0)
