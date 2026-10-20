extends SceneTree

const ISOLATED_PATH := "res://.godot/shadow_settings_smoke.tres"

func _initialize() -> void:
	call_deferred("run_probe")

func run_probe() -> void:
	if FileAccess.file_exists(ISOLATED_PATH):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	var fixture := preload("res://tools/ShadowSettingsSmoke.cs").new()
	var result: Dictionary = fixture.Run(ISOLATED_PATH)
	if FileAccess.file_exists(ISOLATED_PATH):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	if FileAccess.file_exists(ISOLATED_PATH + ".s1.tres"):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH + ".s1.tres"))
	if not result.get("pass", false):
		push_error("SHADOW_SETTINGS_SMOKE=FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	print("SHADOW_SETTINGS_SMOKE=PASS " + str(result.get("detail", "")))
	quit()
