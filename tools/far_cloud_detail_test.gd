extends SceneTree

## Isolated contract coverage for Sunshine-backed far cloud detail.  It never
## writes the gameplay override or world save paths.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const PROBE_SCRIPT := preload("res://tools/FarCloudDetailProbe.cs")
const DEFAULTS_SCRIPT := preload("res://tools/GameplayCloudTuningProbe.cs")
const ISOLATED_PATH := "res://.godot/far_cloud_detail_defaults.tres"

var _world: Node3D
var _far: Node
var _bridge: Node
var _clouds: Resource
var _probe: RefCounted
var _defaults: RefCounted
var _elapsed := 0.0

func _initialize() -> void:
	call_deferred("_run")

func _process(delta: float) -> bool:
	_elapsed += delta
	if _elapsed > 60.0: _fail("timed out"); return true
	return false

func _run() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_world = SCENE.instantiate() as Node3D
	_far = _world.get_node("ScaledFarViewport")
	_bridge = _world.get_node("SunshineCloudBridge")
	_clouds = _world.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	_probe = PROBE_SCRIPT.new()
	if _clouds == null: return _fail("Sunshine clouds resource unavailable")
	_defaults = DEFAULTS_SCRIPT.new()
	if not _defaults.InstallShared(_bridge, ISOLATED_PATH): return _fail("could not install isolated shared defaults")
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _world, 5000000.0)
	launcher.free()
	_world.get_node("RingDayNightCycle").set_process(false)
	root.add_child(_world)
	await process_frame
	await process_frame
	_world.get_node("SunshineCloudsDriver").set_process(false)
	_bridge.set_process(false)
	_world.get_node("RingVolumetricClouds").set_process(false)
	if not _verify_persistence(): return
	if not await _verify_live_wiring(): return
	print("FAR_CLOUD_DETAIL_TEST=PASS")
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	_world.queue_free()
	await process_frame
	await process_frame
	quit(0)

func _verify_persistence() -> bool:
	for key in ["FarCloudDetailEnabled", "FarCloudDetailStrength", "FarCloudOpacityMultiplier", "FarCloudRadiusMeters", "HandoffStartMeters", "HandoffEndMeters", "AutomaticCloudHandoff", "CloudHandoffOverlapMeters", "NearCloudRangeExtensionMeters", "RaymarchSteps", "LightingSteps", "MinStepDistanceMeters", "MaxStepDistanceMeters", "LightingTravelKm", "CloudLodBias", "CloudPatchiness", "CloudPatchScaleMeters", "ClearSkyThreshold"]:
		if _bridge.get(key) == null: return _fail("bridge property missing " + key)
	if not _defaults.Set(_bridge, "FarCloudDetailEnabled", false): return _fail("far detail enabled override rejected")
	if not _defaults.Set(_bridge, "FarCloudDetailStrength", 4.0): return _fail("far detail strength override rejected")
	if not _defaults.Set(_bridge, "FarCloudOpacityMultiplier", -2.0): return _fail("far opacity override rejected")
	if not _defaults.Set(_bridge, "FarCloudRadiusMeters", 9000000.0): return _fail("far radius override rejected")
	if not _defaults.Set(_bridge, "HandoffStartMeters", 9000000.0): return _fail("handoff start override rejected")
	if not _defaults.Set(_bridge, "HandoffEndMeters", -1000.0): return _fail("handoff end override rejected")
	if float(_bridge.get("HandoffEndMeters")) <= float(_bridge.get("HandoffStartMeters")): return _fail("handoff pair enforcement failed at expanded start")
	if not _defaults.Set(_bridge, "HandoffEndMeters", 9000000.0): return _fail("expanded handoff end override rejected")
	if not _defaults.Set(_bridge, "AutomaticCloudHandoff", false): return _fail("automatic handoff override rejected")
	if not _defaults.Set(_bridge, "CloudHandoffOverlapMeters", 9000000.0): return _fail("handoff overlap override rejected")
	if not _defaults.Set(_bridge, "NearCloudRangeExtensionMeters", 9000000.0): return _fail("near range extension override rejected")
	if not _defaults.Set(_bridge, "RaymarchSteps", 9000000.0): return _fail("raymarch steps override rejected")
	if not _defaults.Set(_bridge, "LightingSteps", 9000000.0): return _fail("lighting steps override rejected")
	if not _defaults.Set(_bridge, "MinStepDistanceMeters", 9000000.0): return _fail("minimum step override rejected")
	if not _defaults.Set(_bridge, "MaxStepDistanceMeters", 9000000.0): return _fail("maximum step override rejected")
	if not _defaults.Set(_bridge, "LightingTravelKm", 9000000.0): return _fail("lighting travel override rejected")
	if not _defaults.Set(_bridge, "CloudLodBias", 9000000.0): return _fail("cloud lod bias override rejected")
	if not _defaults.Set(_bridge, "CloudPatchiness", 2.0): return _fail("patchiness override rejected")
	if not _defaults.Set(_bridge, "CloudPatchScaleMeters", 999999.0): return _fail("patch scale override rejected")
	if not _defaults.Set(_bridge, "ClearSkyThreshold", -2.0): return _fail("clear threshold override rejected")
	if float(_bridge.get("FarCloudDetailStrength")) > 1.001 or float(_bridge.get("FarCloudOpacityMultiplier")) < -0.001 or not is_equal_approx(float(_bridge.get("FarCloudRadiusMeters")), 2000000.0):
		return _fail("isolated far detail override clamp failed")
	if not is_equal_approx(float(_bridge.get("HandoffStartMeters")), 320000.0) or not is_equal_approx(float(_bridge.get("HandoffEndMeters")), 400000.0):
		return _fail("persisted handoff controls did not preserve start < end")
	if bool(_bridge.get("AutomaticCloudHandoff")) or not is_equal_approx(float(_bridge.get("CloudHandoffOverlapMeters")), 200000.0) or not is_equal_approx(float(_bridge.get("NearCloudRangeExtensionMeters")), 120000.0):
		return _fail("isolated dynamic handoff override clamp failed")
	if int(_bridge.get("RaymarchSteps")) != 1024 or int(_bridge.get("LightingSteps")) != 32 or not is_equal_approx(float(_bridge.get("MinStepDistanceMeters")), 2000.0) or not is_equal_approx(float(_bridge.get("MaxStepDistanceMeters")), 16000.0) or not is_equal_approx(float(_bridge.get("LightingTravelKm")), 80.0) or not is_equal_approx(float(_bridge.get("CloudLodBias")), 8.0):
		return _fail("isolated expanded raymarch override clamp failed")
	if float(_bridge.get("CloudPatchiness")) > 1.001 or float(_bridge.get("CloudPatchScaleMeters")) > 256000.1 or float(_bridge.get("ClearSkyThreshold")) < -0.001:
		return _fail("isolated cloud weather policy clamp failed")
	if not _defaults.Save(): return _fail("could not save isolated far detail defaults")
	_defaults.Reload(_bridge)
	if bool(_bridge.get("FarCloudDetailEnabled")) or not is_equal_approx(float(_bridge.get("FarCloudOpacityMultiplier")), 0.0): return _fail("isolated far detail defaults did not reload")
	if not is_equal_approx(float(_bridge.get("FarCloudRadiusMeters")), 2000000.0) or not is_equal_approx(float(_bridge.get("HandoffStartMeters")), 320000.0) or not is_equal_approx(float(_bridge.get("HandoffEndMeters")), 400000.0) or bool(_bridge.get("AutomaticCloudHandoff")) or not is_equal_approx(float(_bridge.get("CloudHandoffOverlapMeters")), 200000.0) or not is_equal_approx(float(_bridge.get("NearCloudRangeExtensionMeters")), 120000.0) or int(_bridge.get("RaymarchSteps")) != 1024 or int(_bridge.get("LightingSteps")) != 32 or not is_equal_approx(float(_bridge.get("MinStepDistanceMeters")), 2000.0) or not is_equal_approx(float(_bridge.get("MaxStepDistanceMeters")), 16000.0) or not is_equal_approx(float(_bridge.get("LightingTravelKm")), 80.0) or not is_equal_approx(float(_bridge.get("CloudLodBias")), 8.0) or float(_bridge.get("CloudPatchiness")) > 1.001 or float(_bridge.get("CloudPatchScaleMeters")) > 256000.1 or float(_bridge.get("ClearSkyThreshold")) < -0.001:
		return _fail("isolated cloud weather defaults did not reload")
	if not is_equal_approx(float(_clouds.get("max_step_count")), 1024.0) or not is_equal_approx(float(_clouds.get("max_lighting_steps")), 32.0) or not is_equal_approx(float(_clouds.get("min_step_distance")), 2000.0) or not is_equal_approx(float(_clouds.get("max_step_distance")), 16000.0) or not is_equal_approx(float(_clouds.get("lighting_travel_distance")), 80000.0) or not is_equal_approx(float(_clouds.get("lod_bias")), 8.0):
		return _fail("reloaded raymarch defaults were not published to the Sunshine resource")
	var drawer := _world.get_node_or_null("LiveTuningDrawer")
	if drawer == null: return _fail("live tuning drawer unavailable")
	for key in ["FarCloudDetailEnabled", "FarCloudDetailStrength", "FarCloudOpacityMultiplier", "FarCloudRadiusMeters", "HandoffStartMeters", "HandoffEndMeters", "AutomaticCloudHandoff", "CloudHandoffOverlapMeters", "NearCloudRangeExtensionMeters", "RaymarchSteps", "LightingSteps", "MinStepDistanceMeters", "MaxStepDistanceMeters", "LightingTravelKm", "CloudLodBias", "CloudPatchiness", "CloudPatchScaleMeters", "ClearSkyThreshold"]:
		var control := drawer.find_child(key, true, false)
		if control == null: return _fail("live tuning control missing " + key)
		if control is HSlider:
			var expected_max: float = float({"FarCloudRadiusMeters": 2000000.0, "HandoffStartMeters": 320000.0, "HandoffEndMeters": 400000.0, "CloudHandoffOverlapMeters": 200000.0, "NearCloudRangeExtensionMeters": 120000.0, "RaymarchSteps": 1024.0, "LightingSteps": 32.0, "MinStepDistanceMeters": 2000.0, "MaxStepDistanceMeters": 16000.0, "LightingTravelKm": 80.0, "CloudLodBias": 8.0}.get(key, control.max_value))
			if not is_equal_approx(control.max_value, expected_max): return _fail("live tuning maximum mismatch " + key)
		if control is HSlider: control.value = control.min_value if control.value > control.min_value else control.max_value
		elif control is CheckButton: control.button_pressed = not control.button_pressed
		if _defaults.ReadOverride(key) == null: return _fail("live tuning signal did not persist " + key)
	return true

func _verify_live_wiring() -> bool:
	# Restore the source-backed detail after exercising persisted disabled state.
	_bridge.set("FarCloudDetailEnabled", true)
	_bridge.set("FarCloudDetailStrength", 1.0)
	_bridge.set("FarCloudOpacityMultiplier", 1.0)
	_bridge.set("FarCloudRadiusMeters", 180000.0)
	_bridge.set("RaymarchSteps", 256)
	_bridge.set("LightingSteps", 8)
	_bridge.set("MinStepDistanceMeters", 90.0)
	_bridge.set("MaxStepDistanceMeters", 1052.0)
	_bridge.set("LightingTravelKm", 7.0)
	_bridge.set("CloudLodBias", .9)
	if not _probe.ApplyBridgeTemporalQuality(_bridge): return _fail("could not restore certified raymarch fixture")
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	var materials: Array = _probe.GetClipmapMaterials(_far)
	if materials.size() != 3: return _fail("far clipmap materials unavailable")
	# Expanded manual limits must reach both renderers without being silently
	# reduced to the legacy sliders. The 2 Mm radius remains below the certified
	# 5 Mm ring radius and must preserve the curved canopy contract.
	_bridge.set("AutomaticCloudHandoff", false)
	_bridge.set("HandoffStartMeters", 320000.0)
	_bridge.set("HandoffEndMeters", 400000.0)
	_bridge.set("CloudHandoffOverlapMeters", 200000.0)
	_bridge.set("NearCloudRangeExtensionMeters", 120000.0)
	_bridge.set("FarCloudRadiusMeters", 2000000.0)
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	var expanded_handoff: Vector2 = _probe.GetClipmapHandoff(_far)
	if not is_equal_approx(expanded_handoff.x, 320000.0) or not is_equal_approx(expanded_handoff.y, 400000.0): return _fail("expanded manual handoff was reduced %s" % expanded_handoff)
	if not _probe.CurvedCanopyMatchesRing(_far) or not _probe.CanopyHeightsStayInSourceBand(_far): return _fail("expanded far radius broke curved canopy geometry")
	_bridge.set("FarCloudRadiusMeters", 9000000.0)
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	if not is_equal_approx(float(_bridge.get("FarCloudRadiusMeters")), 2000000.0): return _fail("runtime far radius did not clamp to 2 Mm")
	# The expanded automatic overlap and extension must survive the runtime
	# sanitization path, not merely persistence. A 1024 * 16 km near budget has
	# ample reach for the requested 220–520 km automatic blend.
	_bridge.set("AutomaticCloudHandoff", true)
	_bridge.set("RaymarchSteps", 1024)
	_bridge.set("LightingSteps", 32)
	_bridge.set("MinStepDistanceMeters", 2000.0)
	_bridge.set("MaxStepDistanceMeters", 16000.0)
	_bridge.set("LightingTravelKm", 80.0)
	_bridge.set("CloudLodBias", 8.0)
	if not _probe.ApplyBridgeTemporalQuality(_bridge): return _fail("could not apply expanded automatic quality")
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	var expanded_auto: Vector3 = _probe.GetEffectiveCloudHandoff(_bridge)
	if not is_equal_approx(expanded_auto.x, 220000.0) or not is_equal_approx(expanded_auto.y, 520000.0) or not is_equal_approx(expanded_auto.z, 16384000.0): return _fail("expanded automatic controls were runtime-clamped %s" % expanded_auto)
	# Restore the regular certified fixture before its existing short-range and
	# step-budget assertions.
	_bridge.set("AutomaticCloudHandoff", false)
	_bridge.set("RaymarchSteps", 256)
	_bridge.set("LightingSteps", 8)
	_bridge.set("MinStepDistanceMeters", 90.0)
	_bridge.set("MaxStepDistanceMeters", 1052.0)
	_bridge.set("LightingTravelKm", 7.0)
	_bridge.set("CloudLodBias", .9)
	if not _probe.ApplyBridgeTemporalQuality(_bridge): return _fail("could not restore certified quality after expanded automatic case")
	_bridge.set("FarCloudRadiusMeters", 180000.0)
	_bridge.set("HandoffStartMeters", 45000.0)
	_bridge.set("HandoffEndMeters", 68500.0)
	_bridge.set("CloudHandoffOverlapMeters", 16000.0)
	_bridge.set("NearCloudRangeExtensionMeters", 8000.0)
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	if not _probe.MaterialsShareSunshineResources(_far, _clouds): return _fail("far detail copied or missed Sunshine texture resources")
	if not _probe.MaterialsMatchSourceOffsets(_far, _clouds): return _fail("far detail offsets do not match Sunshine")
	_clouds.set("large_scale_clouds_position", _clouds.get("large_scale_clouds_position") + Vector3(17.0, 0.0, -11.0))
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	if not _probe.MaterialsMatchSourceOffsets(_far, _clouds): return _fail("updated Sunshine offsets were frozen in far detail")
	var before: Array = _probe.GetClipmapMeshes(_far)
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	if not _probe.AllMeshesUnchanged(before, _probe.GetClipmapMeshes(_far)): return _fail("unchanged far detail republished mesh geometry")
	for radius in [48000.0, 180000.0, 300000.0]:
		var prior: Array = _probe.GetClipmapMeshes(_far)
		_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, radius, 0.0)
		if not _probe.AllMeshesChanged(prior, _probe.GetClipmapMeshes(_far)): return _fail("radius %.0f did not rebuild curved mesh" % radius)
		if not _probe.CurvedCanopyMatchesRing(_far): return _fail("curved vertices failed at %.0f m" % radius)
	if not _probe.CanopyHeightsStayInSourceBand(_far): return _fail("far canopy heights leave Sunshine background band")
	for material: ShaderMaterial in materials:
		if not bool(material.get_shader_parameter("far_density_enabled")): return _fail("enabled detail did not publish density flag")
	_far.call("ConfigureSunshineFarCloudDetail", null, true, 1.0, 1.0, 180000.0, 0.0)
	for material: ShaderMaterial in materials:
		if bool(material.get_shader_parameter("far_density_enabled")): return _fail("missing source did not safely fall back")
	# A zero multiplier must be a transparent cloud contribution, not merely a
	# disabled resource flag; the GPU companion verifies actual pixels.
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 0.0, 180000.0, 0.0)
	for material: ShaderMaterial in materials:
		if float(material.get_shader_parameter("far_density_opacity_multiplier")) != 0.0: return _fail("zero opacity was not published")
	# The shared controls must publish to Sunshine's near compute resource and
	# the far shell/clipmap on the same bridge update, not merely persist UI.
	# Certified quality reaches 256 * 1052 m.  Automatic ownership must pull
	# the far tier toward that reachable edge while preserving the selected
	# overlap, and it must publish in the same bridge frame as the menu edit.
	_bridge.set("AutomaticCloudHandoff", true)
	_bridge.set("CloudHandoffOverlapMeters", 16000.0)
	_bridge.set("NearCloudRangeExtensionMeters", 8000.0)
	_bridge.set("RaymarchSteps", 256)
	_bridge.set("MaxStepDistanceMeters", 1052.0)
	if not _probe.ApplyBridgeTemporalQuality(_bridge): return _fail("could not apply certified quality resource tuning")
	_bridge.set("HandoffStartMeters", 80000.0)
	_bridge.set("HandoffEndMeters", 80500.0)
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	var near_handoff: Vector4 = _clouds.get("cloud_handoff") as Vector4
	var effective: Vector3 = _probe.GetEffectiveCloudHandoff(_bridge)
	if not is_equal_approx(effective.z, 269312.0) or not is_equal_approx(effective.y, 88500.0) or not is_equal_approx(effective.x, 72000.0): return _fail("automatic certified handoff did not track the near-cloud edge got=%s" % effective)
	if not is_equal_approx(near_handoff.x, effective.x) or not is_equal_approx(near_handoff.y, effective.y): return _fail("automatic handoff did not publish to Sunshine near range")
	var published_auto: Vector2 = _probe.GetClipmapHandoff(_far)
	if not is_equal_approx(published_auto.x, effective.x) or not is_equal_approx(published_auto.y, effective.y): return _fail("visible Sunshine did not own the published far handoff")
	var shell: ShaderMaterial = _probe.GetShellCloudMaterial(_far) as ShaderMaterial
	if shell == null: return _fail("far shell material unavailable")
	var shell_end: float = float(shell.get_shader_parameter("local_handoff_end_meters"))
	if not is_equal_approx(shell_end, effective.y * _far.get("CoordinateScale")): return _fail("automatic handoff end did not publish to far shell")
	var before_handoff: Array = _probe.GetClipmapMeshes(_far)
	# In manual mode the exact selected range remains authoritative. This also
	# checks that a mode toggle does not leave stale far ownership uniforms.
	_bridge.set("AutomaticCloudHandoff", false)
	_bridge.set("HandoffStartMeters", 52000.0)
	_bridge.set("HandoffEndMeters", 84000.0)
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	near_handoff = _clouds.get("cloud_handoff") as Vector4
	effective = _probe.GetEffectiveCloudHandoff(_bridge)
	if not is_equal_approx(effective.x, 52000.0) or not is_equal_approx(effective.y, 84000.0): return _fail("manual handoff did not retain the configured range")
	if not is_equal_approx(near_handoff.x, effective.x) or not is_equal_approx(near_handoff.y, effective.y): return _fail("changed handoff controls were stale in Sunshine near range")
	for material: ShaderMaterial in materials:
		if not is_equal_approx(float(material.get_shader_parameter("handoff_start_meters")), 52000.0) or not is_equal_approx(float(material.get_shader_parameter("handoff_end_meters")), 84000.0): return _fail("handoff controls were not published to far clipmap distance uniforms")
	if not is_equal_approx(float(shell.get_shader_parameter("local_handoff_start_meters")), 52000.0 * _far.get("CoordinateScale")) or not is_equal_approx(float(shell.get_shader_parameter("local_handoff_end_meters")), 84000.0 * _far.get("CoordinateScale")): return _fail("handoff controls were not published to scaled far shell uniforms")
	if not _probe.AllMeshesUnchanged(before_handoff, _probe.GetClipmapMeshes(_far)): return _fail("handoff range unnecessarily rebuilt far geometry")
	# A fast-travel suspension cannot retain a stale nominal fallback radius: the
	# far clipmap takes complete ownership while no local mesh is ready. Exercise
	# that exact weather-owned synchronization path without moving the test world
	# at 49.7 km/s, then restore the visible Sunshine-owned manual pair.
	var weather: Node = _world.get_node("RingVolumetricClouds")
	if not _probe.SetWeatherFastTravelFallbackFixture(weather): return _fail("could not activate weather fast-travel fixture")
	var fast_handoff: Vector2 = _probe.GetClipmapHandoff(_far)
	if fast_handoff.x > .01 or fast_handoff.y > 1.01: return _fail("fast travel retained stale local handoff %s" % fast_handoff)
	# A detail-radius geometry rebuild must retain the weather-owned fast-travel
	# range; rebuilding previously restored the nominal cached overlap.
	var fast_meshes: Array = _probe.GetClipmapMeshes(_far)
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 300000.0, 0.0)
	if not _probe.AllMeshesChanged(fast_meshes, _probe.GetClipmapMeshes(_far)): return _fail("detail radius change did not rebuild fast-travel canopy")
	fast_handoff = _probe.GetClipmapHandoff(_far)
	if fast_handoff.x > .01 or fast_handoff.y > 1.01: return _fail("detail rebuild restored stale fast-travel handoff %s" % fast_handoff)
	if not _probe.SetWeatherFallbackFixtureSlow(weather): return _fail("could not enter cached weather fallback fixture")
	var mesh_fallback: Vector2 = _probe.GetClipmapHandoff(_far)
	var transition_radius: float = _probe.GetWeatherFallbackTransitionRadius(weather)
	if not is_equal_approx(mesh_fallback.y, transition_radius) or mesh_fallback.x <= .0 or mesh_fallback.x >= mesh_fallback.y: return _fail("slow cached fallback did not use actual weather transition %s radius=%.2f" % [mesh_fallback, transition_radius])
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	if not _probe.RestoreWeatherFixture(weather, 52000.0, 84000.0): return _fail("could not restore weather fixture")
	var restored_handoff: Vector2 = _probe.GetClipmapHandoff(_far)
	if not is_equal_approx(restored_handoff.x, 52000.0) or not is_equal_approx(restored_handoff.y, 84000.0): return _fail("slow travel did not restore actual Sunshine handoff %s" % restored_handoff)
	var cached_handoff: Array = _probe.GetClipmapMeshes(_far)
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	if not _probe.AllMeshesUnchanged(cached_handoff, _probe.GetClipmapMeshes(_far)): return _fail("unchanged handoff republished far geometry")
	# Auto mode reads the actual resource budget that the quality menu publishes,
	# rather than keeping a stale bridge-side estimate.
	_bridge.set("AutomaticCloudHandoff", true)
	_bridge.set("MinStepDistanceMeters", 10.0)
	_bridge.set("RaymarchSteps", 64)
	_bridge.set("MaxStepDistanceMeters", 100.0)
	if not _probe.ApplyBridgeTemporalQuality(_bridge): return _fail("could not apply low-budget quality resource tuning")
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	effective = _probe.GetEffectiveCloudHandoff(_bridge)
	if not is_equal_approx(float(_clouds.get("max_step_count")), 64.0) or not is_equal_approx(float(_clouds.get("max_step_distance")), 100.0) or not is_equal_approx(effective.z, 6400.0) or not is_equal_approx(effective.y, 6400.0) or effective.x > 1.1: return _fail("quality change did not immediately constrain automatic near reach got=%s" % effective)
	# An automatic range cannot extend past 70% of the actual curved far
	# radius. This guards a small-detail-radius preset from a canopy gap.
	_bridge.set("RaymarchSteps", 256)
	_bridge.set("MaxStepDistanceMeters", 1052.0)
	if not _probe.ApplyBridgeTemporalQuality(_bridge): return _fail("could not restore certified quality resource tuning")
	_bridge.set("FarCloudRadiusMeters", 30000.0)
	_bridge.set("HandoffStartMeters", 80000.0)
	_bridge.set("HandoffEndMeters", 80500.0)
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	effective = _probe.GetEffectiveCloudHandoff(_bridge)
	if effective.y > 21000.1 or effective.x < .9: return _fail("automatic handoff exceeded 70 percent of actual far radius got=%s" % effective)
	# The clipmap geometry must cover the full handoff end even when an old
	# radius setting lies below it.
	_bridge.set("AutomaticCloudHandoff", false)
	_bridge.set("FarCloudRadiusMeters", 30000.0)
	_bridge.set("HandoffStartMeters", 45000.0)
	_bridge.set("HandoffEndMeters", 80000.0)
	_bridge.set_process(true)
	await process_frame
	_bridge.set_process(false)
	if not _probe.CurvedCanopyMatchesRing(_far): return _fail("far fallback geometry did not cover handoff end")
	return true

func _fail(message: String) -> bool:
	push_error("FAR_CLOUD_DETAIL_TEST=FAIL " + message)
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	if _world != null: _world.queue_free()
	quit(1)
	return false
