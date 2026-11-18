extends SceneTree
const SMOKE := preload("res://tools/AtmosphereSettingsSmoke.cs")
# Per-process scratch folder: two runs of this test may overlap. Deleted when the test ends.
var DIR := "res://.godot/atmosphere_settings_smoke_%d" % OS.get_process_id()
func _initialize() -> void:
	var result: Dictionary = SMOKE.new().Run(DIR + "/settings.tres")
	_cleanup()
	if not bool(result.get("passed",false)): push_error("ATMOSPHERE_SETTINGS_SMOKE_TEST="+str(result)); quit(1); return
	print("ATMOSPHERE_SETTINGS_SMOKE_TEST=PASS "+str(result)); quit(0)
func _cleanup() -> void:
	var abs_dir := ProjectSettings.globalize_path(DIR)
	if not DirAccess.dir_exists_absolute(abs_dir): return
	for f in DirAccess.get_files_at(abs_dir): DirAccess.remove_absolute(abs_dir + "/" + f)
	DirAccess.remove_absolute(abs_dir)
