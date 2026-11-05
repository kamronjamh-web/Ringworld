extends SceneTree

# Run: Godot --headless --path . --script res://tools/vegetation_work_trace_completion_test.gd
const TEST := preload("res://tools/VegetationWorkTraceCompletionTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner := TEST.new()
	var code: int = runner.Run()
	print("VEGETATION_WORK_TRACE_COMPLETION_WRAPPER=", "PASS" if code == 0 else "FAIL")
	quit(code)
