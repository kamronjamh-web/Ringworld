extends SceneTree

const PROBE_SCRIPT := preload("res://scripts/Tools/LodTransitionSettingsSmoke.cs")
const ISOLATED_PATH := "res://.godot/lod_transition_settings_smoke.tres"

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_cleanup()
	var probe: RefCounted = PROBE_SCRIPT.new()
	var result: Dictionary = probe.Run(ISOLATED_PATH)
	probe = null
	await process_frame
	_cleanup()
	if not bool(result.get("pass", false)):
		push_error("LOD_TRANSITION_SETTINGS_SMOKE_TEST_FAIL %s" % str(result.get("detail", "unknown failure")))
		quit(1)
		return
	print("LOD_TRANSITION_SETTINGS_SMOKE_TEST_PASS %s" % str(result.get("detail", "")))
	quit(0)

func _cleanup() -> void:
	if FileAccess.file_exists(ISOLATED_PATH):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
