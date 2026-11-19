extends SceneTree
# NF-6 near ground fog settings and preset gate (headless).
# Run: Godot --headless --path . --script res://tools/near_fog_settings_smoke_test.gd
# Prints NEAR_FOG_SETTINGS_SMOKE=PASS. Per-process scratch folder, deleted when the test ends.
const SMOKE := preload("res://tools/NearFogSettingsSmoke.cs")
var DIR := "res://.godot/near_fog_settings_smoke_%d" % OS.get_process_id()
func _initialize() -> void:
	var result: Dictionary = SMOKE.new().Run(DIR + "/settings.tres")
	_cleanup()
	if not bool(result.get("passed", false)):
		push_error("NEAR_FOG_SETTINGS_SMOKE=FAIL " + str(result)); quit(1); return
	print("NEAR_FOG_SETTINGS_SMOKE=PASS"); quit(0)
func _cleanup() -> void:
	var abs_dir := ProjectSettings.globalize_path(DIR)
	if not DirAccess.dir_exists_absolute(abs_dir): return
	for f in DirAccess.get_files_at(abs_dir): DirAccess.remove_absolute(abs_dir + "/" + f)
	DirAccess.remove_absolute(abs_dir)
