extends SceneTree

# Run (windowed Vulkan, not --headless; no other Godot process running):
# Godot --path . --script res://tools/height_shadow_failure_gpu_test.gd
# Verifies typed startup/failure/disable globals, failed-init latch, exit retirement and storm-map owner replacement with a live receiver.
const TEST := preload("res://tools/HeightShadowFailureGpuTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(30.0).timeout.connect(func():
		push_error("HEIGHT_SHADOW_FAILURE_WRAPPER=FAIL timeout")
		quit(1))
	var test := TEST.new()
	root.add_child(test)
	while str(test.get("Result")) == "RUNNING":
		await process_frame
	var passed := str(test.get("Result")) == "PASS"
	print("HEIGHT_SHADOW_FAILURE_WRAPPER=", "PASS" if passed else "FAIL")
	test.queue_free()
	quit(0 if passed else 1)
