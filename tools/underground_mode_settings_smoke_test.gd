extends SceneTree

const SETTINGS_PROBE := preload("res://scripts/Tools/UndergroundModeSettingsSmoke.cs")
const PATH := "res://.godot/underground_mode_settings_smoke.tres"

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var probe: RefCounted = SETTINGS_PROBE.new()
	var result: Dictionary = probe.Run(PATH)
	probe = null
	if not bool(result.get("pass", false)):
		push_error("UNDERGROUND_MODE_SETTINGS_SMOKE_TEST_FAIL " + str(result.get("detail", "settings failed")))
		quit(1)
		return
	print("UNDERGROUND_MODE_SETTINGS_SMOKE_TEST_PASS %s" % str(result.get("detail", "")))
	quit(0)
