extends SceneTree
# Headless: wet world settings smoke (S2 WO8): the live-tuning checks, the migration from the retired Surface terrain keys, and the drawer controls in the Clouds tab (Rain group).
# Run: Godot --headless --path . --script res://tools/wet_world_settings_smoke_test.gd. Prints WET_WORLD_SETTINGS=PASS.
const SMOKE := preload("res://scripts/Tools/WetWorldSettingsSmoke.cs")
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const PATH := "res://.godot/wet-world-settings-smoke/preferences.tres"
const LEGACY := "res://.godot/wet-world-settings-smoke/surface-terrain-legacy.tres"
const ISOLATED_GROUPS := ["AntiAliasing", "Motion", "Water", "Grass", "Vegetation", "Shadow", "SurfaceTerrain", "RainHaze", "Wind"]

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	var result: Dictionary = SMOKE.new().Run(PATH, LEGACY)
	if not bool(result.get("pass", false)):
		push_error("WET_WORLD_SETTINGS=FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	var drawer := DRAWER.new()
	for group in ISOLATED_GROUPS:
		var path := "res://.godot/wet-world-settings-smoke/" + str(group) + ".tres"
		DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
		drawer.call("Install" + str(group) + "Defaults", path)
	drawer.call("InstallWetWorldDefaults", PATH, LEGACY)
	root.add_child(drawer)
	drawer.call("SetBenchmarkDrawerOpen", true)
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	var in_clouds_tab := false
	var retired_gone := true
	if tabs != null:
		for index in tabs.get_tab_count():
			if tabs.get_tab_title(index) == "Clouds":
				in_clouds_tab = tabs.get_child(index).find_child("WetWorldPuddles", true, false) != null
	for retired in ["Wet roughness", "Rain wetness strength"]:
		for node in drawer.find_children("*", "Label", true, false):
			if (node as Label).text == retired:
				retired_gone = false
	var menu_ok: bool = in_clouds_tab and retired_gone and bool(SMOKE.new().RunControlBindings(drawer))
	drawer.call("SetBenchmarkDrawerOpen", false)
	drawer.queue_free()
	await process_frame
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	for group in ISOLATED_GROUPS:
		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://.godot/wet-world-settings-smoke/" + str(group) + ".tres"))
	if not menu_ok:
		push_error("WET_WORLD_SETTINGS=FAIL menu bindings (in_clouds_tab=%s retired_rows_gone=%s)" % [str(in_clouds_tab), str(retired_gone)])
		quit(1)
		return
	print("WET_WORLD_SETTINGS=PASS " + str(result.get("detail", "")) + " menu=true")
	quit(0)
