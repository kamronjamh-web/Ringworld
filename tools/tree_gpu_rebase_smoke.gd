extends SceneTree

const SMOKE := preload("res://tools/TreeGpuRebaseSmoke.cs")

func _initialize() -> void:
	call_deferred("run_probe")

func run_probe() -> void:
	var watchdog := create_timer(60.0)
	watchdog.timeout.connect(func():
		push_error("TREE_GPU_REBASE_SMOKE=FAIL watchdog")
		quit(1))
	var fixture := SMOKE.new()
	get_root().add_child(fixture)
	var result: Dictionary = fixture.Run()
	fixture.queue_free()
	if not result.get("pass", false):
		push_error("TREE_GPU_REBASE_SMOKE=FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	print("TREE_GPU_REBASE_SMOKE=PASS " + str(result.get("detail", "")))
	quit()
