extends SceneTree

const TEST := preload("res://tools/RuntimeSurfaceDefinitionTest.cs")

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	var test = TEST.new()
	var passed: bool = test.Run()
	test = null
	quit(0 if passed else 1)
