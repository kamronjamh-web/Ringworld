extends SceneTree

const TEST := preload("res://tools/TreeStreamingContractTest.cs")

func _initialize() -> void:
	call_deferred("run_probe")

func run_probe() -> void:
	var watchdog := create_timer(120.0)
	watchdog.timeout.connect(func():
		push_error("TREE_STREAMING_CONTRACT=FAIL watchdog")
		quit(1))
	var fixture := TEST.new()
	get_root().add_child(fixture)
	var result: Dictionary = fixture.Run()
	fixture.queue_free()
	if not result.get("pass", false):
		push_error("TREE_STREAMING_CONTRACT=FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	print("TREE_STREAMING_CONTRACT=PASS " + str(result.get("detail", "")))
	quit()
