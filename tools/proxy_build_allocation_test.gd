extends SceneTree

const ProxyBuildAllocationTest = preload("res://tools/ProxyBuildAllocationTest.cs")

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	var test := ProxyBuildAllocationTest.new()
	quit(0 if test.Run() else 1)
