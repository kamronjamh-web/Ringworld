extends SceneTree

# Regression fixture for the live Atmosphere tab.  The production scene adds
# LiveTuningDrawer before GraphicsQuality, so this deliberately preserves that
# ready order and never installs an atmosphere profile before startup.
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const GRAPHICS := preload("res://scripts/Voxel/LOD/RingGraphicsQualityController.cs")
const DAY_NIGHT := preload("res://scripts/RingDayNightCycle.cs")
const BINDING_SMOKE := preload("res://tools/AtmosphereMenuSmoke.cs")

var _fixture: Node3D
var _drawer: CanvasLayer
var _graphics: Node
var _prefs := ""
var _aa_prefs := ""
var _shadow_prefs := ""
var _grass_prefs := ""
var _water_prefs := ""
var _motion_prefs := ""

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var base := "res://.godot/atmosphere_menu_smoke/%d" % Time.get_ticks_msec()
	_prefs = base.path_join("atmosphere.tres")
	_aa_prefs = base.path_join("aa.tres")
	_shadow_prefs = base.path_join("shadows.tres")
	_grass_prefs = base.path_join("grass.tres")
	_water_prefs = base.path_join("water.tres")
	_motion_prefs = base.path_join("motion.tres")
	_cleanup()

	_fixture = Node3D.new()
	_fixture.name = "AtmosphereMenuSmokeFixture"
	var sun := DirectionalLight3D.new()
	sun.name = "StarLight"
	var cycle := DAY_NIGHT.new()
	cycle.name = "RingDayNightCycle"
	cycle.process_mode = Node.PROCESS_MODE_DISABLED
	_drawer = DRAWER.new()
	_drawer.name = "LiveTuningDrawer"
	# Keep all unrelated live-tuning persistence in this fixture directory. Do
	# not call InstallAtmosphereDefaults here: that would mask the startup bug.
	_drawer.call("InstallAntiAliasingDefaults", _aa_prefs)
	_drawer.call("InstallShadowDefaults", _shadow_prefs)
	_drawer.call("InstallGrassDefaults", _grass_prefs)
	_drawer.call("InstallWaterDefaults", _water_prefs)
	_drawer.call("InstallMotionDefaults", _motion_prefs)
	_graphics = GRAPHICS.new()
	_graphics.name = "GraphicsQuality"
	_graphics.set("DefaultPreset", 3)
	# This order is intentional: drawer _Ready must acquire the controller's
	# lazy initialized owner before GraphicsQuality._Ready has run.
	_fixture.add_child(sun)
	_fixture.add_child(cycle)
	_fixture.add_child(_drawer)
	_fixture.add_child(_graphics)
	root.add_child(_fixture)
	for frame in range(4):
		await process_frame
	_drawer.call("SetBenchmarkDrawerOpen", true)
	await process_frame

	var enabled := _drawer.find_child("AtmosphereEnabled", true, false) as CheckButton
	var preset := _drawer.find_child("AtmospherePreset", true, false) as OptionButton
	var density := _drawer.find_child("AtmosphereDensity", true, false) as HSlider
	var status := _drawer.find_child("AtmosphereStatus", true, false) as Label
	var save := _drawer.find_child("AtmosphereSaveNow", true, false) as Button
	var reset := _drawer.find_child("AtmosphereReset", true, false) as Button
	if enabled == null or preset == null or density == null or status == null or save == null or reset == null:
		return _fail("atmosphere controls were not built for drawer-before-controller startup")
	if enabled.disabled or preset.disabled or density.editable == false:
		return _fail("atmosphere controls stayed disabled with a live controller")
	# This is read-only. It must pass before an isolated profile is installed;
	# original code cached null here because GraphicsQuality had not readied.
	var startup_binding: Dictionary = BINDING_SMOKE.new().VerifyBinding(_drawer, _graphics)
	if not bool(startup_binding.get("passed", false)):
		return _fail("drawer did not bind the controller's lazy atmosphere owner: " + str(startup_binding))

	# Replace directly on GraphicsQuality. The drawer must notice that replacement
	# itself; all following model mutations and persistence are fixture-local.
	_graphics.call("InstallAtmosphereDefaults", _prefs)
	await create_timer(0.3).timeout
	var replacement_binding: Dictionary = BINDING_SMOKE.new().VerifyBinding(_drawer, _graphics)
	var replacement_diagnostics := _graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary
	if not bool(replacement_binding.get("passed", false)) or replacement_diagnostics.get("settings_path", "") != _prefs:
		return _fail("drawer did not rebind to the controller replacement: " + str(replacement_binding))

	# Physical is the default haze model (and the user's current look): a fresh profile starts Physical with the Physical controls shown.
	var default_diag := _graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary
	var default_model := _drawer.find_child("AtmosphereHazeModel", true, false) as OptionButton
	if int(default_diag.get("haze_model", -1)) != 1 or int(default_diag.get("preset", -1)) != 1 or int(default_diag.get("haze_quality", -1)) != 4 \
			or not is_equal_approx(float(default_diag.get("visibility_km", -1.0)), 400.0) or not is_equal_approx(float(default_diag.get("density", -1.0)), 0.5) \
			or default_model == null or default_model.selected != 1:
		return _fail("fresh atmosphere profile did not default to the Physical model and the current in-game values: " + str(default_diag))

	# Use controls and their connected signals, not the drawer's direct helpers.
	preset.select(1)
	preset.item_selected.emit(1)
	density.value = 0.63
	await process_frame
	if int((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("preset", -1)) != 1 or not is_equal_approx(float((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("density", -1.0)), 0.63):
		return _fail("preset or density UI signal did not update the atmosphere model")

	# A/B haze model toggle (P2a): a real control that drives the model and survives a status refresh.
	var haze_model := _drawer.find_child("AtmosphereHazeModel", true, false) as OptionButton
	if haze_model == null:
		return _fail("haze model toggle was not built")
	haze_model.select(1)
	haze_model.item_selected.emit(1)
	await process_frame
	if int((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("haze_model", -1)) != 1 or not status.text.contains("Physical"):
		return _fail("haze model toggle did not switch the model to Physical")
	# P4 physical controls: visible only for the Physical model, and each drives the shared model through its signal.
	var physical_group := _drawer.find_child("AtmospherePhysicalGroup", true, false) as Control
	var rayleigh := _drawer.find_child("AtmosphereRayleighStrength", true, false) as HSlider
	var haze_quality := _drawer.find_child("AtmosphereHazeQuality", true, false) as OptionButton
	if physical_group == null or rayleigh == null or haze_quality == null or not physical_group.visible:
		return _fail("physical haze controls missing or hidden under the Physical model")
	for slider_name in ["MistDensity", "MistHeight", "AerosolHeight", "GroundAlbedo", "MultiScatter", "SkyTintR", "SkyTintG", "SkyTintB", "CloudShadowing", "StormHaze"]:
		if _drawer.find_child("Atmosphere" + slider_name, true, false) as HSlider == null:
			return _fail("physical haze slider missing: " + slider_name)
	# POI haze and cloud shadows follow the regular haze and terrain cloud shadows: no per-POI toggles in the menu.
	for check_name in ["AtmospherePoiHaze", "AtmospherePoiCloudShadow"]:
		if _drawer.find_child(check_name, true, false) != null:
			return _fail("%s must not be exposed in the menu" % check_name)
	if int((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("haze_quality", -1)) != 4 or not is_equal_approx(rayleigh.value, 1.0):
		return _fail("physical haze controls do not default to the current effective values")
	var cloud_shadowing := _drawer.find_child("AtmosphereCloudShadowing", true, false) as HSlider
	if not is_equal_approx(cloud_shadowing.value, 1.0):
		return _fail("cloud shadowing slider does not default to 1")
	var storm_haze := _drawer.find_child("AtmosphereStormHaze", true, false) as HSlider
	if not is_equal_approx(storm_haze.value, 1.0):
		return _fail("storm haze slider does not default to 1")
	cloud_shadowing.value = 0.4
	storm_haze.value = 0.6
	rayleigh.value = 1.5
	haze_quality.select(2)
	haze_quality.item_selected.emit(2)
	await process_frame
	var p4_diag := _graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary
	if not is_equal_approx(float(p4_diag.get("haze_rayleigh_strength", -1.0)), 1.5) or int(p4_diag.get("haze_quality", -1)) != 16 \
			or not is_equal_approx(float(p4_diag.get("haze_cloud_shadowing", -1.0)), 0.4) \
			or not is_equal_approx(float(p4_diag.get("haze_storm_strength", -1.0)), 0.6):
		return _fail("physical haze slider/quality signals did not update the model")
	haze_model.select(0)
	haze_model.item_selected.emit(0)
	await process_frame
	if int((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("haze_model", -1)) != 0:
		return _fail("haze model toggle did not return to Legacy")
	if physical_group.visible:
		return _fail("physical haze controls stayed visible under the Legacy model")

	# The live drawer processes while gameplay is paused. A slider still must
	# update the shared model immediately, without depending on an unpaused frame.
	paused = true
	density.value = 0.41
	if not is_equal_approx(float((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("density", -1.0)), 0.41):
		paused = false
		return _fail("paused atmosphere slider signal did not update the model")
	paused = false

	# An outside runtime change must refresh the menu without calling its helper.
	_graphics.call("SetAtmosphereEnabled", false)
	await create_timer(0.3).timeout
	if enabled.button_pressed or bool((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("enabled", true)) or not status.text.to_lower().contains("off"):
		return _fail("external haze state did not synchronize the menu")
	enabled.button_pressed = true
	await process_frame
	density.value = 0.0
	await process_frame
	if not status.text.to_lower().contains("zero"):
		return _fail("zero-density haze state is not reported accurately")

	# Persist one actual UI change, then exercise graphics preset and drawer reset.
	density.value = 0.58
	save.pressed.emit()
	await process_frame
	if not FileAccess.file_exists(_prefs):
		return _fail("atmosphere save control did not persist to the isolated profile")
	_graphics.call("InstallAtmosphereDefaults", _prefs)
	await create_timer(0.3).timeout
	if not is_equal_approx(float((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("density", -1.0)), 0.58) or not is_equal_approx(density.value, 0.58):
		return _fail("atmosphere profile reload did not preserve the saved UI value")
	_graphics.call("ApplyPreset", 2, true, false)
	await process_frame
	if not is_equal_approx(float((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("density", -1.0)), 0.58):
		return _fail("atmosphere UI override did not survive a graphics preset")
	reset.pressed.emit()
	await process_frame
	p4_diag = _graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary
	if not is_equal_approx(float(p4_diag.get("haze_rayleigh_strength", -1.0)), 1.0) or int(p4_diag.get("haze_quality", -1)) != 4 or not is_equal_approx(rayleigh.value, 1.0):
		return _fail("atmosphere reset did not restore the physical haze controls")
	if int(p4_diag.get("haze_model", -1)) != 1 or int(p4_diag.get("preset", -1)) != 1 or not is_equal_approx(float(p4_diag.get("visibility_km", -1.0)), 400.0) or haze_model.selected != 1:
		return _fail("atmosphere reset did not restore the Physical model default")
	if not is_equal_approx(float((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("density", -1.0)), 0.5) or not is_equal_approx(density.value, 0.5):
		return _fail("atmosphere reset did not update both model and slider")

	# Replacing the controller profile after the drawer is ready must rebind the
	# tab to the exact new owner rather than writing its stale startup cache.
	density.value = 0.71
	await process_frame
	if not is_equal_approx(float((_graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("density", -1.0)), 0.71):
		return _fail("drawer did not rebind to the replacement atmosphere owner")

	# Make the configured provider path unavailable without disposing the
	# controller still cached by unrelated tabs in this minimal fixture.
	_graphics.name = "UnavailableGraphicsQuality"
	await create_timer(0.3).timeout
	if not enabled.disabled or not preset.disabled or density.editable or not status.text.to_lower().contains("unavailable"):
		return _fail("atmosphere controls did not disable when their controller disappeared")

	print("ATMOSPHERE_MENU_SMOKE_TEST=PASS")
	_finish(0)

func _fail(reason: String) -> void:
	push_error("ATMOSPHERE_MENU_SMOKE_TEST=FAIL " + reason)
	_finish(1)

func _finish(code: int) -> void:
	paused = false
	if _fixture != null and is_instance_valid(_fixture):
		_fixture.free()
	_cleanup()
	call_deferred("quit", code)

func _cleanup() -> void:
	for path in [_prefs, _aa_prefs, _shadow_prefs, _grass_prefs, _water_prefs, _motion_prefs]:
		if not path.is_empty() and FileAccess.file_exists(path):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
