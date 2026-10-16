extends SceneTree

const TEST := preload("res://tools/TerrainSurfaceFollowerTest.cs")

var runner: Node

func _init() -> void:
	_run.call_deferred()
	create_timer(120.0).timeout.connect(func() -> void:
		push_error("TERRAIN_SURFACE_FOLLOWER_TEST=FAIL timeout")
		quit(1))

func _run() -> void:
	runner = TEST.new()
	root.add_child(runner)
	runner.call("Start")
	while not bool(runner.call("IsFinished")):
		await process_frame
	var passed := bool(runner.call("IsPassed"))
	runner.queue_free()
	await process_frame
	quit(0 if passed else 1)
