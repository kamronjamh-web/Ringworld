extends SceneTree

const SMOKE := preload("res://scripts/Tools/WaterSettingsSmoke.cs")
const FFT_CONTROLLER := preload("res://scripts/Voxel/LOD/RingWaterFftController.cs")
const PATH := "user://generated/water_settings_smoke.tres"

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	var result: Dictionary = SMOKE.new().Run(PATH)
	var fallback := FFT_CONTROLLER.new()
	root.add_child(fallback)
	var headless_fallback := DisplayServer.get_name() != "headless" or str(fallback.get("Status")).contains("analytic water fallback")
	fallback.queue_free()
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	if not bool(result.get("pass", false)) or not headless_fallback:
		push_error("WATER_SETTINGS_SMOKE_TEST_FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	print("WATER_SETTINGS_SMOKE_TEST_PASS " + str(result.get("detail", "")))
	quit(0)
