extends SceneTree

const SMOKE := preload("res://tools/TreeGpuFrameCacheSmoke.cs")

func _initialize() -> void:
	call_deferred("run_probe")

func run_probe() -> void:
	var smoke = SMOKE.new()
	root.add_child(smoke)
	var result: Dictionary = smoke.Run()
	smoke.queue_free()
	if result.get("pass", false):
		print("TREE_GPU_FRAME_CACHE=PASS " + str(result.get("detail", "")))
		quit(0)
		return
	push_error("TREE_GPU_FRAME_CACHE=FAIL " + str(result.get("detail", "")))
	quit(1)
