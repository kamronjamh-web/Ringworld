extends SceneTree

const MODE_PROBE := preload("res://scripts/Tools/UndergroundModeSmoke.cs")

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var probe: RefCounted = MODE_PROBE.new()
	var result: Dictionary = probe.Run()
	probe = null
	if not bool(result.get("pass", false)):
		push_error("UNDERGROUND_MODE_SMOKE_TEST_FAIL " + str(result.get("detail", "checks failed")))
		quit(1)
		return
	print("UNDERGROUND_MODE_SMOKE_TEST_PASS %s" % str(result.get("detail", "")))
	quit(0)
