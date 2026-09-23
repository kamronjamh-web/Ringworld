extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PROBE_SCRIPT := preload("res://tools/GameplayCloudTuningProbe.cs")
const SETTINGS_SCRIPT := preload("res://scripts/ui/GameplayCloudSettings.cs")
const ISOLATED_PATH := "res://.godot/gameplay_cloud_tuning_smoke.tres"
var _scene: Node; var _bridge: Node; var _driver: Node; var _clouds: Resource; var _drawer: Node; var _probe: RefCounted
var _frame := 0; var _stage := 0; var _stage_frame := 0; var _widths: Array[int] = []; var _point_baseline := 1.0
var _completed_checks := false
var _autosave_elapsed := 0.0
var _local_rendering_toggle: CheckButton
var _local_rendering_toggle_phase := 0
var _local_rendering_recovery_frames := 0
var _scales_started := false

func _initialize() -> void:
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	_scene = TEST_SCENE.instantiate(); _bridge = _scene.get_node("SunshineCloudBridge"); _driver = _scene.get_node("SunshineCloudsDriver")
	_clouds = _driver.get("clouds_resource") as Resource; _drawer = _scene.get_node("LiveTuningDrawer"); _point_baseline = float(_driver.get("point_light_power_multiplier"))
	_probe = PROBE_SCRIPT.new()
	if not _probe.InstallShared(_bridge, ISOLATED_PATH): _fail("could not install isolated shared defaults"); return
	# Seed the same persisted-off state a player can leave behind, before the
	# bridge initializes. Writing the resource directly keeps the bridge's
	# authored enabled value intact for GameplayCloudDefaults.Reset().
	var startup_settings := SETTINGS_SCRIPT.new()
	startup_settings.Overrides = { "CloudRenderingEnabled": false }
	if ResourceSaver.save(startup_settings, ISOLATED_PATH) != OK: _fail("could not seed isolated local Sunshine-off default"); return
	root.add_child(_scene)
	_scene.get_node("RingDayNightCycle").set_process(false)

func _process(_delta: float) -> bool:
	_frame += 1
	if _completed_checks:
		_autosave_elapsed += _delta
		if _frame == _stage_frame + 10 and DisplayServer.get_name() != "headless":
			var capture := root.get_texture().get_image()
			if capture == null or capture.save_png("res://.godot/gameplay_cloud_quality.png") != OK: return _fail("quality drawer capture failed")
		if _frame >= _stage_frame + 40 and _autosave_elapsed >= 1.0:
			if bool(_probe.Status().get("dirty", true)): return _fail("live cloud autosave debounce did not save")
			_drawer.call("FlushCloudDefaults")
			DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
			print("GAMEPLAY_CLOUD_TUNING_SMOKE_TEST_PASS widths=%s" % str(_widths))
			quit(0)
			return true
		return false
	if _frame == 2: _scene.get_node("RingDayNightCycle").call("set_cycle_fraction", 0.0)
	if _frame == 10: return _begin_local_rendering_toggle()
	if _local_rendering_toggle_phase == -1: return _click_prepared_local_rendering_checkbox()
	if _local_rendering_toggle_phase == 1 or _local_rendering_toggle_phase == 3: return _verify_local_rendering_recovery()
	if _local_rendering_toggle_phase == 2: return _disable_local_rendering_again()
	if _local_rendering_toggle_phase == 4 and not _scales_started:
		_scales_started = true; _start_scale(0)
	if _stage > 0 and _frame >= _stage_frame + 6:
		if not _verify_scale():
			if _frame >= _stage_frame + 90: return _fail("rendered scale/shadow readiness timed out")
			return false
		if _stage < 3: _start_scale(_stage)
		else: return _finish()
	if _frame > 360: return _fail("timed out")
	return false

func _exercise_drawer() -> bool:
	if not bool(_probe.Status().get("initialized", false)) or bool(_probe.Status().get("dirty", true)): return _fail("startup dirtied defaults")
	_drawer.call("SetBenchmarkDrawerOpen", true); _drawer.call("SetBenchmarkDrawerOpen", false)
	if bool(_probe.Status().get("dirty", true)): return _fail("drawer open/close dirtied defaults")
	if str(_drawer.call("GetCloudTuningDiagnostics").get("path", "")) != ISOLATED_PATH: return _fail("drawer is not using isolated shared service")
	if not bool(_probe.VerifySupportedOverrideEquality()): return _fail("supported override equality regression")
	# Exercise actual control signals, including all shadow controls that were
	# previously wired only to temporary fields.
	for key in ["LightingSteps", "GpuNearShadowMapResolution", "GpuNearShadowRadiusMeters", "GpuNearShadowUpdatesPerSecond", "GpuFarShadowMapResolution", "GpuFarShadowRadiusMeters", "GpuFarShadowUpdatesPerSecond", "GpuShadowVerticalSamples", "ShadowRadiusMeters", "ShadowMapResolution", "ShadowRefreshSeconds"]:
		var slider := _drawer.find_child(key, true, false) as HSlider
		if slider == null: return _fail("missing cloud control " + key)
		slider.value = slider.value - slider.step if slider.value > slider.min_value else slider.value + slider.step
		if _probe.ReadOverride(key) == null: return _fail("slider signal did not persist " + key)
	var stationary := _drawer.find_child("StationaryHistoryWeight", true, false) as HSlider
	stationary.value = .05
	if not is_equal_approx(float(_probe.ReadOverride("MovingHistoryWeight")), .05): return _fail("history slider companion was not persisted")
	_probe.Reset(_bridge)
	_drawer.call("FlushCloudDefaults")
	if _probe.Set(_bridge, "UnknownCloudKey", 1.0) or _probe.Set(_bridge, "Enabled", false) or _probe.Set(_bridge, "CloudDensityMultiplier", NAN): return _fail("invalid override accepted")
	# UI sliders send floats for integer fields.
	for pair in [["CloudResolutionScale", 2.0], ["RaymarchSteps", 192.0], ["LightingSteps", 7.0], ["MinStepDistanceMeters", 490.0], ["MaxStepDistanceMeters", 100.0]]: _drawer.call("SetCloudTuningValue", pair[0], pair[1])
	if int(_bridge.get("CloudResolutionScale")) != 2 or int(_bridge.get("RaymarchSteps")) != 192: return _fail("drawer integer values not applied")
	if float(_bridge.get("MaxStepDistanceMeters")) < float(_bridge.get("MinStepDistanceMeters")) + 10.0: return _fail("min/max companion failed")
	if not _verify_live(): return true
	_drawer.call("SetBenchmarkDrawerOpen", false)
	if bool(_probe.Status().get("dirty", true)): return _fail("drawer close did not flush")
	return false

func _begin_local_rendering_toggle() -> bool:
	_drawer.call("SetBenchmarkDrawerOpen", true)
	for tabs: TabContainer in _drawer.find_children("*", "TabContainer", true, false):
		for index in tabs.get_tab_count():
			if tabs.get_tab_title(index) == "Clouds": tabs.current_tab = index
	_local_rendering_toggle = _drawer.find_child("CloudRenderingEnabled", true, false) as CheckButton
	if _local_rendering_toggle == null: return _fail("missing local Sunshine rendering control")
	if _local_rendering_toggle.button_pressed or bool(_bridge.get("RuntimeVisible")) or bool(_clouds.get("enabled")):
		return _fail("isolated persisted-off local Sunshine default was not applied")
	# Wait for the next process frame so the selected tab has a resolved rect.
	_local_rendering_toggle_phase = -1
	return false

func _click_prepared_local_rendering_checkbox() -> bool:
	# Route a real press/release through the root viewport, then wait for both
	# persistence and fresh compositor output.
	_click_local_rendering_checkbox_input()
	_local_rendering_toggle_phase = 1
	return false

func _disable_local_rendering_again() -> bool:
	if bool(_bridge.get("RuntimeVisible")) or bool(_clouds.get("enabled")):
		return _fail("local Sunshine rendering did not disable through its checkbox")
	if bool(_probe.ReadOverride("CloudRenderingEnabled")):
		return _fail("local Sunshine rendering checkbox did not persist its disabled state")
	_click_local_rendering_checkbox_input()
	_local_rendering_toggle_phase = 3
	_local_rendering_recovery_frames = 0
	return false

func _verify_local_rendering_recovery() -> bool:
	_local_rendering_recovery_frames += 1
	if not bool(_bridge.get("RuntimeVisible")) or not bool(_clouds.get("enabled")):
		if _local_rendering_recovery_frames < 30: return false
		return _fail("local Sunshine rendering did not recover through its checkbox")
	if DisplayServer.get_name() != "headless":
		var backdrop := _bridge.call("GetCloudBackdropTexture") as Texture2D
		if backdrop == null or backdrop.get_width() <= 0:
			if _local_rendering_recovery_frames < 90: return false
			return _fail("local Sunshine rendering recovered without a fresh cloud backdrop publication")
	if not bool(_probe.ReadOverride("CloudRenderingEnabled")):
		return _fail("local Sunshine rendering checkbox did not persist its enabled state")
	if _local_rendering_toggle_phase == 1:
		_click_local_rendering_checkbox_input()
		_local_rendering_toggle_phase = 2
		return false
	_local_rendering_toggle_phase = 4
	_drawer.call("SetBenchmarkDrawerOpen", false)
	return _exercise_drawer()

func _click_local_rendering_checkbox_input() -> void:
	# Headless has no viewport GUI dispatch; use the control's native setter
	# there while graphical runs exercise the actual pointer route below.
	if DisplayServer.get_name() == "headless":
		_local_rendering_toggle.button_pressed = not _local_rendering_toggle.button_pressed
		return
	var center := _local_rendering_toggle.get_global_rect().get_center()
	var down := InputEventMouseButton.new()
	down.button_index = MOUSE_BUTTON_LEFT; down.position = center; down.global_position = center; down.pressed = true
	root.push_input(down, true)
	var up := InputEventMouseButton.new()
	up.button_index = MOUSE_BUTTON_LEFT; up.position = center; up.global_position = center; up.pressed = false
	root.push_input(up, true)


func _start_scale(scale: int) -> void:
	_stage = scale + 1; _stage_frame = _frame; _bridge.set("CloudResolutionScale", scale); _bridge.call("ApplyTemporalQualityTuning", false)

func _verify_scale() -> bool:
	var scale := _stage - 1
	if int(_clouds.get("resolution_scale")) != scale: return _fail("resolution %d not applied" % scale)
	if DisplayServer.get_name() == "headless": return true
	var texture := _bridge.call("GetCloudBackdropTexture") as Texture2D
	if texture == null or texture.get_width() <= 0: return false
	var status: Dictionary = _clouds.call("get_gpu_cloud_shadow_status")
	var near_size: Vector2i = status.get("near_published_resolution", Vector2i.ZERO)
	var far_size: Vector2i = status.get("far_published_resolution", Vector2i.ZERO)
	if near_size.x <= 0 or far_size.x <= 0 or not bool(_bridge.call("IsCloudShadowProjectorEnabled")): return false
	_widths.append(texture.get_width())
	if _widths.size() > 1 and _widths[-1] >= _widths[-2]: return _fail("scale %d did not reduce cloud texture" % scale)
	print("Gameplay cloud scale=%d texture=%d near=%s far=%s" % [scale, texture.get_width(), near_size, far_size])
	return true

func _finish() -> bool:
	var saved_before := FileAccess.get_file_as_bytes(ISOLATED_PATH)
	var failing: RefCounted = PROBE_SCRIPT.new()
	if not failing.Open(_bridge, "res://.godot/missing_cloud_tuning_fixture/defaults.tres") or not failing.Set(_bridge, "CloudSharpness", .4) or failing.Save(): return _fail("isolated save-failure fixture did not fail")
	if FileAccess.get_file_as_bytes(ISOLATED_PATH) != saved_before: return _fail("failed save disturbed prior isolated defaults")
	if not _probe.Reset(_bridge) or int(_bridge.get("CloudResolutionScale")) != 0: return _fail("reset did not restore baseline")
	_probe.Reload(_bridge)
	if int(_probe.ReadOverride("CloudResolutionScale")) != 2: return _fail("sparse reload failed")
	_scene.get_node("GraphicsQuality").call("ApplyPreset", 0, false, false)
	if int(_bridge.get("CloudResolutionScale")) != 2: return _fail("profile overwrote sparse override")
	if bool(_probe.Status().get("dirty", true)): return _fail("graphics profile dirtied defaults")
	_bridge.call("SetProviderActive", false); _probe.Set(_bridge, "CloudAmbientMultiplier", 1.4); _bridge.call("ApplyCloudAppearanceTuning")
	if bool(_bridge.get("RuntimeVisible")) or bool(_driver.get("update_continuously")): return _fail("inactive edit reclaimed provider")
	_bridge.call("SetProviderActive", true)
	_drawer.call("SetBenchmarkDrawerOpen", true)
	var tabs := _drawer.find_children("*", "TabContainer", true, false)
	for container: TabContainer in tabs:
		for index in container.get_tab_count():
			if container.get_tab_title(index) == "Quality": container.current_tab = index
	# Keep the normal renderer active long enough to drain retired generations
	# after the provider round-trip, and let the real autosave debounce run.
	_completed_checks = true
	_stage_frame = _frame
	return false

func _verify_live() -> bool:
	if int(_clouds.get("resolution_scale")) != 2 or int(_clouds.get("max_step_count")) != 192 or int(_clouds.get("max_lighting_steps")) != 7:
		_fail("resource quality missing")
		return false
	var steps: Array = _driver.get("tracked_directional_light_shadow_steps")
	if steps.is_empty() or int(steps[0]) != 7:
		_fail("tracked sun steps missing")
		return false
	if absf(float(_driver.get("directional_light_power_multiplier")) - .34) > .001:
		_fail("directional baseline changed")
		return false
	if not is_equal_approx(float(_driver.get("point_light_power_multiplier")), _point_baseline):
		_fail("point baseline changed")
		return false
	return true

func _fail(reason: String) -> bool:
	push_error("GAMEPLAY_CLOUD_TUNING_SMOKE_TEST_FAIL: " + reason); DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH)); quit(1); return true
