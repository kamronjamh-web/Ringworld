extends SceneTree
# Windowed worldless capture fixture; run through the GPU queue. No model or user-profile access.
const TEST := preload("res://tools/PdaCameraCaptureTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(30.0).timeout.connect(func(): push_error("PDA_CAMERA_CAPTURE=FAIL watchdog"); quit(1))
	var probe = TEST.new()
	root.add_child(probe)
	while str(probe.get("Result")) == "RUNNING":
		await process_frame
	var result := str(probe.get("Result"))
	probe.queue_free()
	await process_frame
	if result == "PASS":
		print("PDA_CAMERA_CAPTURE_WRAPPER=PASS")
		quit(0)
	else:
		push_error("PDA_CAMERA_CAPTURE_WRAPPER=FAIL " + result)
		quit(1)
