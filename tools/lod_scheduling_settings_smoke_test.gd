extends SceneTree

const SETTINGS_PROBE := preload("res://scripts/Tools/LodSchedulingSettingsSmoke.cs")
const PATH := "res://.godot/lod_scheduling_settings_smoke.tres"

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var probe: RefCounted = SETTINGS_PROBE.new()
	var result: Dictionary = probe.Run(PATH)
	probe = null
	if not bool(result.get("pass", false)):
		push_error("LOD_SCHEDULING_SETTINGS_SMOKE_TEST_FAIL " + str(result.get("detail", "preferences failed")))
		quit(1)
		return
	print("LOD_SCHEDULING_SETTINGS_SMOKE_TEST_PASS %s" % str(result.get("detail", "")))
	quit(0)
