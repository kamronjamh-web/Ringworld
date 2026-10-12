extends SceneTree

const PROBE := preload("res://scripts/Tools/FarRenderingSettingsSmoke.cs")
const ISOLATED_PATH := "res://.godot/far_rendering_settings_smoke.tres"

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var probe: RefCounted = PROBE.new()
	var result: Dictionary = probe.Run(ISOLATED_PATH)
	probe = null
	await process_frame
	if not bool(result.get("pass", false)):
		push_error("FAR_RENDERING_SETTINGS_SMOKE_TEST_FAIL %s" % str(result.get("detail", "")))
		quit(1)
		return
	print("FAR_RENDERING_SETTINGS_SMOKE_TEST_PASS %s" % str(result.get("detail", "")))
	quit(0)
