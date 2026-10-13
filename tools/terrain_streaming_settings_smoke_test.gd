extends SceneTree

const SETTINGS_PROBE := preload("res://scripts/Tools/TerrainStreamingSettingsSmoke.cs")
const PATH := "res://.godot/terrain_streaming_settings_smoke.tres"

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var probe: RefCounted = SETTINGS_PROBE.new()
	var result: Dictionary = probe.Run(PATH)
	probe = null
	if not bool(result.get("pass", false)):
		push_error("TERRAIN_STREAMING_SETTINGS_SMOKE_TEST_FAIL " + str(result.get("detail", "preferences failed")))
		quit(1)
		return
	print("TERRAIN_STREAMING_SETTINGS_SMOKE_TEST_PASS %s" % str(result.get("detail", "")))
	quit(0)
