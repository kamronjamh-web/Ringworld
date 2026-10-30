extends SceneTree
# Headless: megastorm settings smoke (Megastorm WO2): the live-tuning checks plus the drawer controls in the Clouds tab.
# Run: Godot --headless --path . --script res://tools/megastorm_settings_smoke_test.gd. Prints MEGASTORM_SETTINGS=PASS.
const SMOKE := preload("res://tools/MegastormSettingsSmoke.cs")
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const PATH := "res://.godot/megastorm-settings-smoke/preferences.tres"
const ISOLATED_GROUPS := ["AntiAliasing", "Motion", "Water", "Grass", "Vegetation", "Shadow", "SurfaceTerrain", "RainHaze", "Wind", "StormArrival"]

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	var result: Dictionary = SMOKE.new().Run(PATH)
	if not bool(result.get("pass", false)):
		push_error("MEGASTORM_SETTINGS=FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	var drawer := DRAWER.new()
	for group in ISOLATED_GROUPS:
		var path := "res://.godot/megastorm-settings-smoke/" + str(group) + ".tres"
		DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
		drawer.call("Install" + str(group) + "Defaults", path)
	drawer.call("InstallMegastormDefaults", PATH)
	root.add_child(drawer)
	drawer.call("SetBenchmarkDrawerOpen", true)
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	var in_clouds_tab := false
	if tabs != null:
		for index in tabs.get_tab_count():
			if tabs.get_tab_title(index) == "Clouds":
				in_clouds_tab = tabs.get_child(index).find_child("MegastormSpeedMetersPerSecond", true, false) != null
	var menu_ok: bool = in_clouds_tab and bool(SMOKE.new().RunControlBindings(drawer))
	drawer.call("SetBenchmarkDrawerOpen", false)
	drawer.queue_free()
	await process_frame
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	for group in ISOLATED_GROUPS:
		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://.godot/megastorm-settings-smoke/" + str(group) + ".tres"))
	if not menu_ok:
		push_error("MEGASTORM_SETTINGS=FAIL menu bindings (in_clouds_tab=%s)" % str(in_clouds_tab))
		quit(1)
		return
	print("MEGASTORM_SETTINGS=PASS " + str(result.get("detail", "")) + " menu=true")
	quit(0)
