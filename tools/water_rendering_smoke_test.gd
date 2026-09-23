extends SceneTree

## Rendered integration fixture for the production water path.  It deliberately
## measures finite off/on/off windows instead of turning a machine-specific result into
## a frame-rate certification.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const GeneratedOutputPaths := preload("res://tools/generated_output_paths.gd")
const WATER_PROBE := preload("res://scripts/Tools/WaterRenderingProbe.cs")
const OFF_CAPTURE := "water_fft_off.png"
const ON_CAPTURE := "water_fft_on.png"
const DRAWER_CAPTURE := "water_fft_drawer.png"
const DRAWER_DETAIL_CAPTURE := "water_fft_drawer_detail.png"
const VARIATION_CONTROLS_CAPTURE := "water_variation_controls.png"
const AERIAL_100_CAPTURE := "water_aerial_100m.png"
const AERIAL_1000_CAPTURE := "water_aerial_1000m.png"
const AERIAL_6000_CAPTURE := "water_aerial_6000m.png"
const WARMUP_FRAMES := 180
const SAMPLE_FRAMES := 240
const TIMEOUT_FRAMES := 2000

var _scene: Node
var _drawer: Node
var _fft: Node
var _frame := 0
var _phase := "boot"
var _phase_frame := 0
var _last_usec := 0
var _samples: Array[float] = []
var _off_metrics := {}
var _on_metrics := {}
var _off_after_metrics := {}
var _off_path := ""
var _on_path := ""
var _drawer_path := ""
var _drawer_detail_path := ""
var _variation_controls_path := ""
var _aerial_100_path := ""
var _aerial_1000_path := ""
var _aerial_6000_path := ""
var _isolated_defaults := ""
var _water_probe: RefCounted
var _ocean: Dictionary

func _initialize() -> void:
	_boot_scene.call_deferred()

func _boot_scene() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required for this rendered fixture")
		return
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_isolated_defaults = GeneratedOutputPaths.smoke_capture_path("water_rendering_smoke.tres")
	if _isolated_defaults.is_empty():
		_fail("could not allocate generated preference fixture path")
		return
	DirAccess.remove_absolute(_isolated_defaults)
	_off_path = GeneratedOutputPaths.smoke_capture_path(OFF_CAPTURE)
	_on_path = GeneratedOutputPaths.smoke_capture_path(ON_CAPTURE)
	_drawer_path = GeneratedOutputPaths.smoke_capture_path(DRAWER_CAPTURE)
	_drawer_detail_path = GeneratedOutputPaths.smoke_capture_path(DRAWER_DETAIL_CAPTURE)
	_variation_controls_path = GeneratedOutputPaths.smoke_capture_path(VARIATION_CONTROLS_CAPTURE)
	_aerial_100_path = GeneratedOutputPaths.smoke_capture_path(AERIAL_100_CAPTURE)
	_aerial_1000_path = GeneratedOutputPaths.smoke_capture_path(AERIAL_1000_CAPTURE)
	_aerial_6000_path = GeneratedOutputPaths.smoke_capture_path(AERIAL_6000_CAPTURE)
	if _off_path.is_empty() or _on_path.is_empty() or _drawer_path.is_empty() or _drawer_detail_path.is_empty() or _variation_controls_path.is_empty() or _aerial_100_path.is_empty() or _aerial_1000_path.is_empty() or _aerial_6000_path.is_empty():
		_fail("could not allocate generated capture paths")
		return
	_scene = TEST_SCENE.instantiate()
	# This fixture must never read/write a user's active world slot.
	var saves := _scene.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var pre_ready_drawer := _scene.get_node_or_null("LiveTuningDrawer")
	if pre_ready_drawer == null:
		_fail("missing LiveTuningDrawer before scene entry")
		return
	# Install before _Ready so normal user preferences are never loaded.
	pre_ready_drawer.call("InstallWaterDefaults", _isolated_defaults)
	var launcher_scene := load("res://scenes/WorldSizeLauncher.tscn") as PackedScene
	if launcher_scene == null:
		_fail("missing WorldSizeLauncher")
		return
	var launcher := launcher_scene.instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _scene, 5000000.0)
	launcher.free()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frame += 1
	if _frame > TIMEOUT_FRAMES:
		return _fail("timed out in phase " + _phase)
	if _phase == "boot" and _frame >= 8:
		_drawer = _scene.get_node_or_null("LiveTuningDrawer")
		_fft = _scene.get_node_or_null("WaterFftController")
		_water_probe = WATER_PROBE.new()
		if _drawer == null or _fft == null:
			return _fail("missing LiveTuningDrawer or WaterFftController")
		if str(_drawer.call("GetWaterTuningDiagnostics").get("path", "")) != _isolated_defaults:
			return _fail("water drawer did not install isolated defaults")
		if not _select_water_tab(): return _fail("Water tab is unavailable")
		if not _verify_controls(): return false
		if not _verify_ui_callbacks(): return false
		if not _verify_isolated_defaults_contract(): return false
		if not _place_at_bounded_ocean(): return false
		_drawer.call("SetBenchmarkDrawerOpen", false)
		_fft.set("Resolution", 128); _fft.set("CascadeCount", 2); _fft.set("UpdatesPerSecond", 24)
		_fft.set("NormalStrength", 0.25); _fft.set("FoamStrength", 0.35); _fft.set("FollowWeatherWind", true)
		_fft.set("Enabled", false)
		_begin_window("off_before_warmup")
		return false
	if _phase.ends_with("warmup"):
		if _frame >= _phase_frame + WARMUP_FRAMES:
			if _phase == "on_warmup" or _phase == "final_on_warmup":
				if not bool(_fft.get("Ready")): return false
				if not _verify_fft_binding():
					if _frame >= _phase_frame + WARMUP_FRAMES + 180:
						return _fail("FFT became ready without an ocean shader binding")
					return false
			if _phase == "off_after_warmup":
				if not _verify_fft_disabled(): return _fail("FFT shader binding remained active after disabling FFT")
				_begin_window("off_after_sample")
			elif _phase == "final_on_warmup":
				_begin_aerial_capture(100.0)
			else:
				_begin_window("off_before_sample" if _phase == "off_before_warmup" else "on_sample")
		return false
	if _phase.ends_with("sample"):
		_sample_frame()
		if _samples.size() < SAMPLE_FRAMES: return false
		var metrics := _metrics(_samples)
		if _phase == "off_before_sample":
			_off_metrics = metrics
			if not _capture(_off_path, "FFT-off"): return false
			_fft.set("Enabled", true)
			_begin_window("on_warmup")
		elif _phase == "on_sample":
			_on_metrics = metrics
			if not _capture(_on_path, "FFT-on"): return false
			_fft.set("Enabled", false)
			_begin_window("off_after_warmup")
		else:
			_off_after_metrics = metrics
			_fft.set("Enabled", true)
			_begin_window("final_on_warmup")
		return false
	if _phase == "aerial_100" and _frame >= _phase_frame + 90:
		if not _capture(_aerial_100_path, "100m aerial"): return false
		_begin_aerial_capture(1000.0)
		return false
	if _phase == "aerial_1000" and _frame >= _phase_frame + 120:
		if not _capture(_aerial_1000_path, "1000m aerial"): return false
		_begin_aerial_capture(6000.0)
		return false
	if _phase == "aerial_6000" and _frame >= _phase_frame + 180:
		if not _capture(_aerial_6000_path, "6000m aerial"): return false
		_open_drawer_capture()
		return false
	if _phase == "drawer" and _frame >= _phase_frame + 8:
		if not _capture(_drawer_path, "Water drawer"): return false
		if not _show_variation_controls(): return false
		_phase = "drawer_variation"
		_phase_frame = _frame
		return false
	if _phase == "drawer_variation" and _frame >= _phase_frame + 8:
		if not _capture(_variation_controls_path, "Water variation controls"): return false
		_scroll_water_to_bottom()
		_phase = "drawer_detail"
		_phase_frame = _frame
		return false
	if _phase == "drawer_detail" and _frame >= _phase_frame + 8:
		if not _capture(_drawer_detail_path, "Water drawer detail"): return false
		var diagnostics: Dictionary = _drawer.call("GetWaterTuningDiagnostics")
		if bool(diagnostics.get("dirty", true)) or str(diagnostics.get("path", "")) != _isolated_defaults:
			return _fail("fixture dirtied or escaped isolated water defaults: %s" % diagnostics)
		print("WATER_RENDERING_SMOKE_TEST=PASS adapter=%s viewport=%s water_preset=2x128 fft_status=%s off_before=%s on=%s off_after=%s captures=[%s,%s,%s,%s,%s,%s,%s,%s]" % [RenderingServer.get_video_adapter_name(), root.get_visible_rect().size, str(_fft.get("Status")), _format_metrics(_off_metrics), _format_metrics(_on_metrics), _format_metrics(_off_after_metrics), _off_path, _on_path, _aerial_100_path, _aerial_1000_path, _aerial_6000_path, _drawer_path, _variation_controls_path, _drawer_detail_path])
		_cleanup(); quit(0); return true
	return false

func _begin_window(phase: String) -> void:
	_phase = phase; _phase_frame = _frame; _samples.clear(); _last_usec = Time.get_ticks_usec()

func _sample_frame() -> void:
	var now := Time.get_ticks_usec()
	if _last_usec > 0: _samples.append((now - _last_usec) / 1000.0)
	_last_usec = now

func _select_water_tab() -> bool:
	_drawer.call("SetBenchmarkDrawerOpen", true)
	for tabs: TabContainer in _drawer.find_children("*", "TabContainer", true, false):
		for i in range(tabs.get_tab_count()):
			if tabs.get_tab_title(i) == "Water": tabs.current_tab = i; return true
	return false

func _verify_controls() -> bool:
	for name in ["FftEnabled", "FftResolution", "FftCascadeCount", "FftUpdatesPerSecond", "FftGeometryUpdatesPerSecond", "FftGeometryHeightScale", "FftGeometryChoppiness", "FftGeometryDistanceMeters", "FftGeometryFilterMeters", "FftCrestLightingStrength", "FftDeepWaterWaveDepthMeters", "WaterSwellVariation", "WaterFoamPatchiness", "WaterDistantFoamVisibility", "WaterDetailFilteringStrength", "WaterSsrEnabled", "WaterSsrStrength", "WaterSsrCoarseSteps", "WaterSsrRefinementSteps", "WaterSsrMaxRayDistance", "WaterSsrCameraDistance", "WaterSsrDepthToleranceNear", "WaterSsrDepthToleranceFar", "WaterSsrRayBias", "WaterSsrEdgeFade", "WaterSsrRoughnessCutoff", "WaterSsrDebugMode", "WaterRuntimeStatus"]:
		if _drawer.find_child(name, true, false) == null:
			_fail("missing Water selector " + name); return false
	return true

# Exercise the real emitted control signals, not the drawer's direct test hook.
# This catches a disconnected widget or a property/material propagation break.
func _verify_ui_callbacks() -> bool:
	var quality := _scene.get_node_or_null("GraphicsQuality")
	var strength := _drawer.find_child("WaterSsrStrength", true, false) as HSlider
	var enabled := _drawer.find_child("WaterSsrEnabled", true, false) as CheckButton
	var steps := _drawer.find_child("WaterSsrCoarseSteps", true, false) as HSlider
	var debug := _drawer.find_child("WaterSsrDebugMode", true, false) as OptionButton
	if quality == null or strength == null or enabled == null or steps == null or debug == null:
		return _fail("SSR UI callback controls are unavailable")
	enabled.button_pressed = true
	strength.value = .73
	steps.value = 19
	debug.select(3)
	debug.item_selected.emit(3)
	if not bool(quality.get("WaterSsrEnabled")) or not is_equal_approx(float(quality.get("WaterSsrStrength")), .73) or int(quality.get("WaterSsrCoarseSteps")) != 19 or int(quality.get("WaterSsrDebugMode")) != 3:
		return _fail("Water UI signals did not update GraphicsQualityController")
	var found_uniform := false
	for node in _scene.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		if mesh == null or mesh.mesh == null: continue
		for surface in mesh.mesh.get_surface_count():
			var material := mesh.get_active_material(surface) as ShaderMaterial
			if material != null and material.shader != null and material.shader.resource_path == "res://materials/ring_ocean_proxy.gdshader":
				if is_equal_approx(float(material.get_shader_parameter("water_ssr_strength")), .73) and int(material.get_shader_parameter("water_ssr_coarse_steps")) == 19:
					found_uniform = true
					break
		if found_uniform: break
	if not found_uniform: return _fail("Water UI changes did not reach near-ocean material uniforms")
	if not _drawer.call("FlushWaterDefaults"): return _fail("Water UI preference did not persist")
	return true

func _verify_isolated_defaults_contract() -> bool:
	var enabled_before := bool(_fft.get("Enabled"))
	var resolution_before := int(_fft.get("Resolution"))
	_drawer.call("SetWaterTuningValue", "UnknownWaterKey", 1.0)
	_drawer.call("SetWaterTuningValue", "FftResolution", 129)
	_drawer.call("SetWaterTuningValue", "FftGeometryUpdatesPerSecond", 999.0)
	_drawer.call("SetWaterTuningValue", "FftDeepWaterWaveDepthMeters", 999.0)
	_drawer.call("SetWaterTuningValue", "FftShorelineShallowDepthMeters", 20.0)
	_drawer.call("SetWaterTuningValue", "WaterFresnelF0", NAN)
	if int(_fft.get("Resolution")) != resolution_before:
		_fail("defaults accepted an invalid FFT resolution or non-finite value")
		return false
	if not is_equal_approx(float(_fft.get("GeometryUpdatesPerSecond")), 120.0):
		_fail("geometry FFT update rate was not clamped to its persisted 5..120 Hz range")
		return false
	if not is_equal_approx(float(_fft.get("DeepWaterWaveDepthMeters")), 500.0):
		_fail("full-wave depth was not clamped to its persisted 5..500m range")
		return false
	_drawer.call("SetWaterTuningValue", "FftDeepWaterWaveDepthMeters", 5.0)
	if float(_fft.get("DeepWaterWaveDepthMeters")) < float(_fft.get("ShorelineShallowDepthMeters")) + 1.0:
		_fail("FFT shallow anchor/full-wave depth ordering was not enforced")
		return false
	_drawer.call("SetWaterTuningValue", "FftEnabled", false)
	if bool(_fft.get("Enabled")) or not _drawer.call("FlushWaterDefaults"):
		_fail("isolated FFT-off preference did not persist")
		return false
	var reset := _drawer.find_child("WaterResetDefaults", true, false) as Button
	if reset == null:
		_fail("missing Water reset button")
		return false
	reset.emit_signal("pressed")
	if bool(_fft.get("Enabled")) != enabled_before:
		_fail("water defaults reset did not restore the startup FFT state")
		return false
	return true

func _place_at_bounded_ocean() -> bool:
	var explorer := _scene.get_node_or_null("Explorer")
	if explorer == null or _water_probe == null:
		_fail("ocean fixture missing terrain or explorer")
		return false
	_ocean = _water_probe.call("FindOcean", _scene)
	if not bool(_ocean.get("valid", false)) or int(_ocean.get("water_kind", 0)) != 1:
		_fail("bounded terrain probes found no ocean")
		return false
	var placement: Dictionary = _water_probe.call("PlaceCamera", _scene, _ocean)
	if not bool(placement.get("valid", false)) or int(placement.get("water_kind", 0)) != 1:
		_fail("could not place benchmark camera over ocean")
		return false
	if float(placement.get("actual_y", 0.0)) <= float(placement.get("surface", 0.0)):
		_fail("benchmark camera was not placed above the selected ocean surface")
		return false
	# Keep the exact same view during both measurements; falling/swimming would
	# otherwise change the amount of water and terrain visible between windows.
	explorer.set_physics_process(false)
	explorer.set_process_unhandled_input(false)
	return true

func _begin_aerial_capture(height_meters: float) -> void:
	if _water_probe == null or _ocean.is_empty():
		_fail("aerial capture lost deterministic ocean probe")
		return
	var placement: Dictionary = _water_probe.call("PlaceAerialCamera", _scene, _ocean, height_meters)
	if not bool(placement.get("valid", false)) or absf(float(placement.get("actual_y", 0.0)) - float(placement.get("requested_y", 0.0))) > 2.0:
		_fail("could not place %dm aerial observer" % height_meters)
		return
	_phase = "aerial_%d" % int(height_meters)
	_phase_frame = _frame

func _verify_fft_binding() -> bool:
	var binding: Dictionary = _water_probe.call("GetFftBindingStatus", _scene)
	var valid := bool(binding.get("controller_ready", false)) and int(binding.get("active_cascades", 0)) == 2 and int(binding.get("bound_materials", 0)) > 0 and int(binding.get("geometry_meshes", 0)) > 0 and int(binding.get("covered_coarse_meshes", 0)) > 0 and bool(binding.get("surface_query_ready", false))
	if valid or _frame % 60 == 0: print("WATER_FFT_GEOMETRY_BINDING ", binding)
	return valid

func _verify_fft_disabled() -> bool:
	var binding: Dictionary = _water_probe.call("GetFftBindingStatus", _scene)
	return not bool(binding.get("controller_enabled", true)) and int(binding.get("bound_materials", -1)) == 0

func _capture(path: String, label: String) -> bool:
	var image := root.get_texture().get_image()
	if image == null or image.is_empty() or image.save_png(path) != OK:
		_fail(label + " capture failed: " + path)
		return false
	return true

func _open_drawer_capture() -> void:
	if not _select_water_tab(): _fail("could not reopen Water tab"); return
	_phase = "drawer"; _phase_frame = _frame

func _scroll_water_to_bottom() -> void:
	var water_scroll := _drawer.find_child("Water", true, false) as ScrollContainer
	if water_scroll == null:
		_fail("Water tab scroll container is unavailable")
		return
	water_scroll.scroll_vertical = int(water_scroll.get_v_scroll_bar().max_value)

func _show_variation_controls() -> bool:
	var water_scroll := _drawer.find_child("Water", true, false) as ScrollContainer
	var filtering := _drawer.find_child("WaterDetailFilteringStrength", true, false) as Control
	if water_scroll == null or filtering == null:
		_fail("Water variation controls are unavailable")
		return false
	water_scroll.ensure_control_visible(filtering)
	return true

func _metrics(values: Array[float]) -> Dictionary:
	var sorted := values.duplicate(); sorted.sort()
	var total := 0.0
	for value in sorted: total += value
	return {"count": sorted.size(), "mean_ms": total / sorted.size(), "p95_ms": sorted[mini(sorted.size() - 1, int(floor(sorted.size() * .95)))], "p99_ms": sorted[mini(sorted.size() - 1, int(floor(sorted.size() * .99)))]}

func _format_metrics(metrics: Dictionary) -> String:
	return "n=%d mean=%.3fms p95=%.3fms p99=%.3fms" % [int(metrics.get("count", 0)), float(metrics.get("mean_ms", 0.0)), float(metrics.get("p95_ms", 0.0)), float(metrics.get("p99_ms", 0.0))]

func _cleanup() -> void:
	if not _isolated_defaults.is_empty(): DirAccess.remove_absolute(_isolated_defaults)

func _fail(reason: String) -> bool:
	push_error("WATER_RENDERING_SMOKE_TEST=FAIL " + reason)
	_cleanup(); quit(1); return true
