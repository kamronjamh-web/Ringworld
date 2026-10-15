extends SceneTree

const TEST := preload("res://tools/EmitterSiteUndergroundTest.cs")

var runner: Node

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	runner = TEST.new()
	root.add_child(runner)
	runner.call("Start")
	while not bool(runner.call("IsFinished")):
		await process_frame
	var passed := bool(runner.call("IsPassed"))
	runner.queue_free()
	quit(0 if passed else 1)
