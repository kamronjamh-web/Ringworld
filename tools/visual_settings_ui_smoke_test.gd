extends SceneTree

# Headless UI fixture: no terrain, world scene, render captures, or user preferences.
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const GRAPHICS := preload("res://scripts/Voxel/LOD/RingGraphicsQualityController.cs")
const DAY_NIGHT := preload("res://scripts/RingDayNightCycle.cs")
const AA_PREFS := "res://.godot/visual_settings_ui_aa.tres"
const SHADOW_PREFS := "res://.godot/visual_settings_ui_shadow.tres"
const GRASS_PREFS := "res://.godot/visual_settings_ui_grass.tres"
const WATER_PREFS := "res://.godot/visual_settings_ui_water.tres"

var fixture: Node3D
var drawer: CanvasLayer
var graphics: Node
var sun: DirectionalLight3D
var cycle: Node

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_cleanup_prefs()
	fixture = Node3D.new()
	sun = DirectionalLight3D.new(); sun.name = "StarLight"; sun.shadow_enabled = true
	cycle = DAY_NIGHT.new(); cycle.name = "RingDayNightCycle"; cycle.process_mode = Node.PROCESS_MODE_DISABLED
	graphics = GRAPHICS.new(); graphics.name = "GraphicsQuality"; graphics.set("DefaultPreset", 3)
	drawer = DRAWER.new(); drawer.name = "LiveTuningDrawer"
	# These setters retain their path override before _Ready initializes defaults.
	drawer.call("InstallAntiAliasingDefaults", AA_PREFS)
	drawer.call("InstallShadowDefaults", SHADOW_PREFS)
	drawer.call("InstallGrassDefaults", GRASS_PREFS)
	drawer.call("InstallWaterDefaults", WATER_PREFS)
	fixture.add_child(sun); fixture.add_child(cycle); fixture.add_child(graphics); fixture.add_child(drawer)
	root.add_child(fixture)
	for frame in range(3): await process_frame
	drawer.call("SetBenchmarkDrawerOpen", true)
	await process_frame

	var aa := drawer.find_child("AntiAliasingMode", true, false) as OptionButton
	if aa == null: return _fail("AA option missing")
	aa.select(5); aa.item_selected.emit(5)
	await process_frame
	if root.msaa_3d != Viewport.MSAA_4X: return _fail("AA option did not set root MSAA 4x")
	var aa_save := drawer.find_child("AntiAliasingSaveNow", true, false) as Button
	if aa_save == null: return _fail("AA save button missing")
	aa_save.pressed.emit()
	if not FileAccess.file_exists(AA_PREFS): return _fail("AA save failed")
	drawer.call("InstallAntiAliasingDefaults", AA_PREFS)
	await process_frame
	if root.msaa_3d != Viewport.MSAA_4X: return _fail("AA install did not reload MSAA 4x")

	var atlas := drawer.find_child("ShadowAtlasSize", true, false) as OptionButton
	var depth := drawer.find_child("ShadowDepthBits", true, false) as OptionButton
	var near := drawer.find_child("ShadowNearCascadeMeters", true, false) as SpinBox
	var bias := drawer.find_child("ShadowBias", true, false) as HSlider
	var shadows := drawer.find_child("SunShadows", true, false) as CheckButton
	var softness := drawer.find_child("ShadowSunAngularDistance", true, false) as HSlider
	var stabilize := drawer.find_child("SunStabilizationEnabled", true, false) as CheckButton
	var step := drawer.find_child("ShadowSunStepDegrees", true, false) as HSlider
	var pause := drawer.find_child("PauseDayNightTest", true, false) as CheckButton
	if atlas == null or depth == null or near == null or bias == null or shadows == null or softness == null or stabilize == null or step == null or pause == null: return _fail("shadow controls missing")
	if depth.get_item_index(16) < 0 or depth.get_item_text(depth.get_item_index(16)) != "16-bit (lower memory)" or depth.get_item_index(32) < 0 or depth.get_item_text(depth.get_item_index(32)) != "32-bit (higher precision)": return _fail("shadow depth options missing")
	for atlas_size in [2048, 4096, 8192, 16384, 32768, 65536, 131072]:
		if atlas.get_item_index(atlas_size) < 0: return _fail("atlas option missing: %d" % atlas_size)
	var high_atlas_index := atlas.get_item_index(65536)
	if high_atlas_index < 0 or not atlas.get_popup().is_item_disabled(high_atlas_index): return _fail("unsupported 65536 atlas was not disabled")
	var effective_before_disabled := int(graphics.get("EffectiveDirectionalShadowAtlasSize"))
	var requested_before_disabled := int(graphics.get("RequestedDirectionalShadowAtlasSize"))
	atlas.select(high_atlas_index); atlas.item_selected.emit(high_atlas_index)
	await process_frame
	if int(graphics.get("EffectiveDirectionalShadowAtlasSize")) != effective_before_disabled: return _fail("disabled atlas selection changed effective atlas")
	if int(graphics.get("RequestedDirectionalShadowAtlasSize")) != requested_before_disabled or atlas.get_selected_id() != effective_before_disabled: return _fail("disabled atlas selection changed requested atlas or UI selection")
	if FileAccess.file_exists(SHADOW_PREFS): return _fail("disabled atlas selection wrote shadow preferences")
	var atlas_index := atlas.get_item_index(4096)
	if atlas_index < 0: return _fail("4096 atlas option missing")
	atlas.select(atlas_index); atlas.item_selected.emit(atlas_index)
	var sixteen_index := depth.get_item_index(16)
	var thirty_two_index := depth.get_item_index(32)
	depth.select(sixteen_index); depth.item_selected.emit(sixteen_index)
	await process_frame
	if int(graphics.get("EffectiveDirectionalShadowDepthBits")) != 16 or not atlas.get_item_text(atlas_index).contains("32 MiB"): return _fail("16-bit depth did not refresh atlas memory label")
	depth.select(thirty_two_index); depth.item_selected.emit(thirty_two_index)
	await process_frame
	if int(graphics.get("EffectiveDirectionalShadowDepthBits")) != 32 or not atlas.get_item_text(atlas_index).contains("64 MiB"): return _fail("32-bit depth did not refresh atlas memory label")
	near.value = 180; bias.value = .5; softness.value = 1.5; step.value = .02; stabilize.button_pressed = false; shadows.button_pressed = false
	await process_frame
	var shadow_save := drawer.find_child("ShadowSaveNow", true, false) as Button
	if shadow_save == null: return _fail("shadow save missing")
	shadow_save.pressed.emit()
	var saved_overrides := load(SHADOW_PREFS).get("Overrides") as Dictionary
	if not saved_overrides.has("DepthBits") or saved_overrides.get("DepthBits") != 32 or not saved_overrides.has("NearCascadeMeters") or not is_equal_approx(float(saved_overrides.get("NearCascadeMeters")), 180): return _fail("shadow depth or near cascade values were not saved")
	# Mutate live values first so InstallShadowDefaults must restore the persisted overrides.
	graphics.call("SetShadowTuningOverride", "DepthBits", 16)
	graphics.call("SetShadowTuningOverride", "NearCascadeMeters", 90)
	sun.light_angular_distance = .33
	cycle.set("SunShadowStepDegrees", .004)
	cycle.set("SunShadowStabilizationEnabled", true)
	drawer.call("InstallShadowDefaults", SHADOW_PREFS)
	await process_frame
	if int(graphics.get("EffectiveDirectionalShadowAtlasSize")) != 4096: return _fail("shadow atlas did not reload")
	if int(graphics.get("EffectiveDirectionalShadowDepthBits")) != 32 or not is_equal_approx(float(graphics.get("EffectiveNearCascadeMeters")), 180) or not is_equal_approx(near.value, 180) or not is_equal_approx(sun.shadow_bias, .5) or not is_equal_approx(sun.light_angular_distance, 1.5) or not is_equal_approx(cycle.get("SunShadowStepDegrees"), .02) or cycle.get("SunShadowStabilizationEnabled") or sun.shadow_enabled: return _fail("shadow values did not reload")
	graphics.call("ApplyPreset", 2, true, false)
	await process_frame
	if not is_equal_approx(float(graphics.get("EffectiveNearCascadeMeters")), 180) or not is_equal_approx(near.value, 180) or not is_equal_approx(sun.light_angular_distance, 1.5) or not is_equal_approx(cycle.get("SunShadowStepDegrees"), .02) or cycle.get("SunShadowStabilizationEnabled"): return _fail("shadow overrides did not survive quality preset")
	pause.button_pressed = true
	if not cycle.get("Paused"): return _fail("pause day/night did not reach cycle")
	shadow_save.pressed.emit()
	var shadow_resource := load(SHADOW_PREFS)
	if shadow_resource == null or (shadow_resource.get("Overrides") as Dictionary).has("PauseDayNightTest"): return _fail("pause test control persisted")

	var reset := drawer.find_child("ShadowResetDefaults", true, false) as Button
	var softness_value := drawer.find_child("ShadowSunAngularDistanceValue", true, false) as Label
	var step_value := drawer.find_child("ShadowSunStepDegreesValue", true, false) as Label
	if reset == null or softness_value == null or step_value == null: return _fail("shadow reset controls missing")
	reset.pressed.emit()
	await process_frame
	if not sun.shadow_enabled: return _fail("shadow reset did not restore sun")
	if not cycle.get("SunShadowStabilizationEnabled") or not is_equal_approx(cycle.get("SunShadowStepDegrees"), .01): return _fail("shadow reset did not restore stabilization")
	if not is_equal_approx(sun.light_angular_distance, .08): return _fail("shadow reset did not restore profile softness")
	if depth.get_selected_id() != 16 or not is_equal_approx(near.value, 60): return _fail("shadow reset did not restore depth or near cascade controls")
	if not is_equal_approx(softness.value, sun.light_angular_distance) or not is_equal_approx(softness_value.text.to_float(), sun.light_angular_distance): return _fail("shadow reset did not refresh softness controls")
	if not is_equal_approx(step.value, cycle.get("SunShadowStepDegrees")) or not is_equal_approx(step_value.text.to_float(), cycle.get("SunShadowStepDegrees")) or not stabilize.button_pressed: return _fail("shadow reset did not refresh stabilization controls")

	graphics.call("ApplyPreset", 2, true, false)
	for frame in range(10): await process_frame
	# Lambdas capture scalar locals by value; use a shared dictionary for counters.
	var refresh_events := {"depth": 0, "near": 0}
	depth.item_selected.connect(func(_index): refresh_events["depth"] += 1)
	near.value_changed.connect(func(_value): refresh_events["near"] += 1)
	var prefs_before_refresh := FileAccess.get_file_as_string(ProjectSettings.globalize_path(SHADOW_PREFS))
	drawer.call("SetBenchmarkDrawerOpen", false); drawer.call("SetBenchmarkDrawerOpen", true)
	for frame in range(10): await process_frame
	var prefs_after_refresh := FileAccess.get_file_as_string(ProjectSettings.globalize_path(SHADOW_PREFS))
	if prefs_before_refresh != prefs_after_refresh or refresh_events["depth"] != 0 or refresh_events["near"] != 0: return _fail("shadow menu refresh wrote preferences or emitted control events")
	var status := drawer.find_child("ShadowRuntimeStatus", true, false) as Label
	var filter := drawer.find_child("ShadowFilter", true, false) as OptionButton
	if status == null or not status.text.contains("Shadow settings saved.") or not status.text.contains("total range 12000 m") or not status.text.contains("estimated raw depth") or not status.text.contains("near cascade 60 m") or filter == null or filter.get_selected_id() != 2: return _fail("quality refresh did not update shadow status/filter")
	print("VISUAL_SETTINGS_UI_SMOKE_TEST_PASS")
	_finish(0)

func _fail(reason: String) -> void:
	push_error("VISUAL_SETTINGS_UI_SMOKE_TEST_FAIL " + reason)
	_finish(1)

func _finish(code: int) -> void:
	if fixture != null and is_instance_valid(fixture): fixture.free()
	_cleanup_prefs()
	call_deferred("quit", code)

func _cleanup_prefs() -> void:
	for path in [AA_PREFS, SHADOW_PREFS, GRASS_PREFS, WATER_PREFS]:
		if FileAccess.file_exists(path): DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
