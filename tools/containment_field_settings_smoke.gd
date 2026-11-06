extends SceneTree

const SMOKE := preload("res://tools/ContainmentFieldSettingsSmoke.cs")
var path := "res://.godot/containment_field_settings_smoke_%d.tres" % Time.get_ticks_usec()

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	remove_fixture(path)
	remove_fixture(path + ".invalid-parent.tres")
	var smoke := SMOKE.new()
	smoke.set("IsolatedPath", path)
	root.add_child(smoke)
	await process_frame
	if smoke.get("Result") != "PASS":
		push_error("CONTAINMENT_FIELD_SETTINGS=FAIL " + str(smoke.get("Result")))
		cleanup()
		quit(1)
		return
	print("CONTAINMENT_FIELD_SETTINGS=PASS")
	cleanup()
	quit(0)

func cleanup() -> void:
	remove_fixture(path)
	remove_fixture(path + ".invalid-parent.tres")

func remove_fixture(resource_path: String) -> void:
	if FileAccess.file_exists(resource_path): DirAccess.remove_absolute(ProjectSettings.globalize_path(resource_path))
