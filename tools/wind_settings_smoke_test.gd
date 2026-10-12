extends SceneTree

const SMOKE := preload("res://scripts/Tools/WindSettingsSmoke.cs")
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const PATH := "res://.godot/wind-settings-smoke/preferences.tres"
const ISOLATED_GROUPS := ["AntiAliasing", "Motion", "Water", "Grass", "Vegetation", "Shadow", "SurfaceTerrain", "RainHaze"]

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	var result: Dictionary = SMOKE.new().Run(PATH)
	if not bool(result.get("pass", false)):
		push_error("WIND_SETTINGS_SMOKE_TEST_FAIL " + str(result.get("detail", "")))
		quit(1)
		return
	var drawer := DRAWER.new()
	# Install every drawer-owned initialized preference path before Ready.
	# No scene controllers exist here, so atmosphere/cloud have no loaded user owner.
	for group in ISOLATED_GROUPS:
		var path := "res://.godot/wind-settings-smoke/" + str(group) + ".tres"
		DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
		drawer.call("Install" + str(group) + "Defaults", path)
	drawer.call("InstallWindDefaults", PATH)
	root.add_child(drawer)
	drawer.call("SetBenchmarkDrawerOpen", true)
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	var has_wind := false
	var wind_tab: Node
	var grass_tab: Node
	if tabs != null:
		for index in tabs.get_tab_count():
			if tabs.get_tab_title(index) == "Wind":
				has_wind = true
				wind_tab = tabs.get_child(index)
			if tabs.get_tab_title(index) == "Grass": grass_tab = tabs.get_child(index)
	var enabled := drawer.find_child("WindEnabled", true, false) as CheckButton
	var gust := drawer.find_child("WindGustStrength", true, false) as HSlider
	var entry := drawer.find_child("WindGustStrengthEntry", true, false) as SpinBox
	var preset := drawer.find_child("WindDramaticStormPreset", true, false) as Button
	var reset := drawer.find_child("WindResetDefaults", true, false) as Button
	var diagnostic := drawer.find_child("WindRuntimeStatus", true, false) as Label
	var menu_ok := has_wind and enabled != null and gust != null and entry != null and preset != null and reset != null and diagnostic != null
	if menu_ok:
		for key in ["GrassWindStrengthMultiplier", "EnableGrassGustTexture", "GrassGustStrength"]:
			menu_ok = menu_ok and wind_tab.find_children(key, "", true, false).size() == 1 and grass_tab.find_child(key, true, false) == null
		for key in ["GrassWindGustMultiplier", "GrassGustBaseBreeze", "GrassGustScaleMeters", "GrassGustTravelSpeedMultiplier"]:
			menu_ok = menu_ok and drawer.find_child(key, true, false) == null
		menu_ok = menu_ok and diagnostic.text.contains("Local wind unavailable") and diagnostic.text.contains("analytic fallback")
		enabled.button_pressed = false
		gust.value = 0
		menu_ok = menu_ok and not enabled.button_pressed and entry.value == 0 and drawer.call("FlushWindDefaults")
		preset.emit_signal("pressed")
		menu_ok = menu_ok and enabled.button_pressed and gust.value > .9
		reset.emit_signal("pressed")
		menu_ok = menu_ok and not bool(drawer.call("GetWindTuningDiagnostics").get("dirty", true))
		menu_ok = menu_ok and bool(SMOKE.new().RunTreeControlBindings(drawer))
		menu_ok = menu_ok and bool(SMOKE.new().RunRainHazeControlBindings(drawer))
	drawer.call("SetBenchmarkDrawerOpen", false)
	drawer.queue_free()
	await process_frame
	DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	for group in ISOLATED_GROUPS:
		DirAccess.remove_absolute(ProjectSettings.globalize_path("res://.godot/wind-settings-smoke/" + str(group) + ".tres"))
	if not menu_ok:
		push_error("WIND_SETTINGS_SMOKE_TEST_FAIL menu binding/preset/reset")
		quit(1)
		return
	print("WIND_SETTINGS_SMOKE_TEST_PASS " + str(result.get("detail", "")) + " menu=true")
	quit(0)
