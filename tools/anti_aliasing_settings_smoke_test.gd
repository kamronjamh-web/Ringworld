extends SceneTree

const PROBE_SCRIPT := preload("res://scripts/Tools/AntiAliasingSettingsSmoke.cs")
# Per-process path: two runs of this test may overlap.
var ISOLATED_PATH := "res://.godot/anti_aliasing_settings_smoke_%d.tres" % OS.get_process_id()


func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var absolute_path := ProjectSettings.globalize_path(ISOLATED_PATH)
	if FileAccess.file_exists(ISOLATED_PATH):
		DirAccess.remove_absolute(absolute_path)
	var probe: RefCounted = PROBE_SCRIPT.new()
	var result: Dictionary = probe.Run(ISOLATED_PATH)
	probe = null
	await process_frame
	if FileAccess.file_exists(ISOLATED_PATH):
		DirAccess.remove_absolute(absolute_path)
	if not bool(result.get("pass", false)):
		push_error("ANTI_ALIASING_SETTINGS_SMOKE_TEST=FAIL %s" % str(result.get("detail", "unknown failure")))
		call_deferred("quit", 1)
		return
	print("ANTI_ALIASING_SETTINGS_SMOKE_TEST=PASS %s" % str(result.get("detail", "")))
	call_deferred("quit", 0)
