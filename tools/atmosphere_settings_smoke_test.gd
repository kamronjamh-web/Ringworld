extends SceneTree
const SMOKE := preload("res://tools/AtmosphereSettingsSmoke.cs")
const PREFS := "res://.godot/atmosphere_settings_smoke/settings.tres"
func _initialize() -> void:
	var result: Dictionary = SMOKE.new().Run(PREFS)
	if not bool(result.get("passed",false)): push_error("ATMOSPHERE_SETTINGS_SMOKE_TEST="+str(result)); quit(1); return
	print("ATMOSPHERE_SETTINGS_SMOKE_TEST=PASS "+str(result)); quit(0)
