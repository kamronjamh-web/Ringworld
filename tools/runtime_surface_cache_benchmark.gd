extends SceneTree

const TEST := preload("res://tools/RuntimeSurfaceCacheBenchmark.cs")

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	var test := TEST.new()
	var passed: bool = test.Run(OS.get_cmdline_user_args().has("--benchmark"))
	test = null
	quit(0 if passed else 1)
