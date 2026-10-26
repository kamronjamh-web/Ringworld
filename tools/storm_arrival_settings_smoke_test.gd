extends SceneTree
# Headless: storm arrival settings smoke (S3 WO-A3): the ten live-tuning checks plus the drawer controls in the Wind tab.
# Run: Godot --headless --path . --script res://tools/storm_arrival_settings_smoke_test.gd. Prints STORM_ARRIVAL_SETTINGS=PASS.
const SMOKE := preload("res://scripts/Tools/StormArrivalSettingsSmoke.cs")
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const PATH := "res://.godot/storm-arrival-settings-smoke/preferences.tres"
const ISOLATED_GROUPS := ["AntiAliasing", "Motion", "Water", "Grass", "Vegetation", "Shadow", "SurfaceTerrain", "RainHaze", "Wind"]

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	var result: Dictionary = SMOKE.new().Run(PATH)
	if not bool(result.get("pass", false)):
		push_error("STORM_ARRIVAL_SETTINGS=FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	var drawer := DRAWER.new()
	for group in ISOLATED_GROUPS:
		var path := "res://.godot/storm-arrival-settings-smoke/" + str(group) + ".tres"
		DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
		drawer.call("Install" + str(group) + "Defaults", path)
	drawer.call("InstallStormArrivalDefaults", PATH)
	root.add_child(drawer)
	drawer.call("SetBenchmarkDrawerOpen", true)
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	var in_wind_tab := false
	if tabs != null:
		for index in tabs.get_tab_count():
			if tabs.get_tab_title(index) == "Wind":
				in_wind_tab = tabs.get_child(index).find_child("StormArrivalHail", true, false) != null
	var menu_ok: bool = in_wind_tab and bool(SMOKE.new().RunControlBindings(drawer))
	drawer.call("SetBenchmarkDrawerOpen", false)
	drawer.queue_free()
	await process_frame
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	for group in ISOLATED_GROUPS:
		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://.godot/storm-arrival-settings-smoke/" + str(group) + ".tres"))
	if not menu_ok:
		push_error("STORM_ARRIVAL_SETTINGS=FAIL menu bindings (in_wind_tab=%s)" % str(in_wind_tab))
		quit(1)
		return
	print("STORM_ARRIVAL_SETTINGS=PASS " + str(result.get("detail", "")) + " menu=true")
	quit(0)
