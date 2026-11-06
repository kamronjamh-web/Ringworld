extends SceneTree
# Opt-in actual local inference transport/lifecycle probe; generated answer quality requires separate review.
const PROBE := preload("res://tools/PdaLocalModelTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(115.0).timeout.connect(func():
		push_error("PDA_LOCAL_MODEL_WRAPPER=FAIL timeout")
		quit(1))
	var probe = PROBE.new()
	root.add_child(probe)
	while str(probe.get("Result")) == "RUNNING":
		await process_frame
	var result := str(probe.get("Result"))
	probe.queue_free()
	await process_frame
	if result == "PASS":
		print("PDA_LOCAL_MODEL_WRAPPER=PASS")
		quit(0)
	else:
		push_error("PDA_LOCAL_MODEL_WRAPPER=FAIL " + result)
		quit(1)
