extends SceneTree

## Rendered SSR regression.  The fixture retains FFT readiness but freezes its
## geometry and normal displacement through the real Water drawer controls so
## the off/on/off image comparison is deterministic.  Those are test-only
## overrides; gameplay defaults are not changed.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const OUTPUT_PATHS := preload("res://tools/generated_output_paths.gd")
const WATER_PROBE := preload("res://scripts/Tools/WaterRenderingProbe.cs")
const TIMEOUT_FRAMES := 2200
const SETTLE_FRAMES := 150

var _scene: Node
var _drawer: Node
var _fft: Node
var _probe: RefCounted
var _ocean: Dictionary
var _frame := 0
var _phase := "boot"
var _phase_frame := 0
var _defaults := ""
var _off: Image
var _on: Image
var _off_again: Image
var _hit: Image
var _paths := {}
var _legacy_roughness := false
var _medium_ssr := false
var _legacy_shader: Shader

func _initialize() -> void:
	_boot.call_deferred()

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_legacy_roughness = OS.get_cmdline_user_args().has("--ssr-legacy-roughness")
	_medium_ssr = OS.get_cmdline_user_args().has("--ssr-medium")
	if _legacy_roughness:
		# A/B baseline only: modify the already-loaded resource for this process;
		# never write a shader asset to disk.
		_legacy_shader = load("res://materials/ring_ocean_proxy.gdshader") as Shader
		if _legacy_shader == null: _fail("could not load ocean shader for legacy A/B"); return
		const FIXED_ROUGHNESS := "use_fft_surface ? ring_fft_shading_fade(distance_to_camera) : wave_normal_detail"
		if not _legacy_shader.code.contains(FIXED_ROUGHNESS): _fail("legacy A/B roughness target missing"); return
		_legacy_shader.code = _legacy_shader.code.replace(FIXED_ROUGHNESS, "wave_normal_detail")
	_defaults = OUTPUT_PATHS.smoke_capture_path("water_ssr_live_controls_gpu_test.tres")
	for label in ["off", "on", "off_again", "hit_mask"]:
		_paths[label] = OUTPUT_PATHS.smoke_capture_path("water_ssr_%s%s.png" % ["legacy_" if _legacy_roughness else "", label])
	if _defaults.is_empty() or _paths.values().any(func(path): return str(path).is_empty()):
		_fail("could not allocate user://generated fixture outputs")
		return
	DirAccess.remove_absolute(_defaults)
	_scene = TEST_SCENE.instantiate()
	# Never allow the fixture to load or save a user world slot.
	var saves := _scene.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	_drawer = _scene.get_node_or_null("LiveTuningDrawer")
	if _drawer == null: _fail("missing LiveTuningDrawer before tree entry"); return
	_drawer.call("InstallWaterDefaults", _defaults)
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _scene, 5000000.0)
	launcher.free()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frame += 1
	if _frame > TIMEOUT_FRAMES: return _fail("timed out in " + _phase)
	if _phase == "boot" and _frame >= 8:
		_fft = _scene.get_node_or_null("WaterFftController")
		_probe = WATER_PROBE.new()
		if _fft == null or str(_drawer.call("GetWaterTuningDiagnostics").get("path", "")) != _defaults:
			return _fail("missing FFT controller or isolated water defaults")
		if not _select_water_tab() or not _configure_through_controls(): return false
		_ocean = _probe.call("FindOcean", _scene)
		if not bool(_ocean.get("valid", false)): return _fail("bounded terrain probes found no ocean")
		var placed: Dictionary = _probe.call("PlaceCamera", _scene, _ocean)
		if not bool(placed.get("valid", false)): return _fail("could not place camera above real ocean")
		var explorer := _scene.get_node_or_null("Explorer")
		explorer.set_physics_process(false); explorer.set_process_unhandled_input(false)
		explorer.call("SetPresentationView", 25.0, -12.0)
		_phase = "wait_fft"; _phase_frame = _frame
		return false
	if _phase == "wait_fft":
		var binding: Dictionary = _probe.call("GetFftBindingStatus", _scene)
		# Do not accept a merely allocated FFT texture: a streamed, visible ocean
		# mesh must be consuming it at the deterministic camera placement.
		if not bool(binding.get("controller_ready", false)) or int(binding.get("bound_materials", 0)) <= 0 or (int(binding.get("geometry_meshes", 0)) <= 0 and int(binding.get("covered_coarse_meshes", 0)) <= 0): return false
		if _frame < _phase_frame + SETTLE_FRAMES: return false
		_add_reflection_marker()
		_freeze_presentation()
		_drawer.call("SetBenchmarkDrawerOpen", false)
		_set_ssr(false, 0, 0.0)
		_phase = "off"; _phase_frame = _frame
		return false
	if _phase == "capturing" or _frame < _phase_frame + 20: return false
	if _phase in ["off", "on", "hit", "off_again"]:
		var capture_phase := _phase
		_phase = "capturing"
		call_deferred("_capture_phase", capture_phase)
	return false

func _capture_phase(capture_phase: String) -> void:
	var image := await _capture("hit_mask" if capture_phase == "hit" else capture_phase)
	if image == null:
		_fail("frame capture failed: " + capture_phase)
		return
	if capture_phase == "off":
		_off = image; _set_ssr(true, 0, 1.0); _phase = "on"
	elif capture_phase == "on":
		_on = image; _set_ssr(true, 2, 1.0); _phase = "hit"
	elif capture_phase == "hit":
		_hit = image; _set_ssr(false, 0, 0.0); _phase = "off_again"
	else:
		_off_again = image
		_finish_comparison()
		return
	_phase_frame = _frame

func _finish_comparison() -> void:
	if _off == null or _on == null or _hit == null or _off_again == null:
		_fail("frame capture failed")
		return
	var response := _mean_difference(_off, _on)
	var restore := _mean_difference(_off, _off_again)
	var hit := _bright_fraction(_hit)
	print("WATER_SSR_LIVE_CONTROLS_GPU_TEST=METRICS legacy=%s response=%.5f hit=%.5f restore=%.5f captures=%s" % [_legacy_roughness, response, hit, restore, _paths])
	if response < 0.003: _fail("SSR on/off water ROI response too small %.5f" % response); return
	if hit < 0.002: _fail("SSR hit-mask ROI has no measurable hits %.5f" % hit); return
	if restore > maxf(0.02, response * 0.45): _fail("SSR off-again did not restore baseline %.5f" % restore); return
	print("WATER_SSR_LIVE_CONTROLS_GPU_TEST=PASS")
	_cleanup(); quit(0)

func _select_water_tab() -> bool:
	_drawer.call("SetBenchmarkDrawerOpen", true)
	for tabs: TabContainer in _drawer.find_children("*", "TabContainer", true, false):
		for i in range(tabs.get_tab_count()):
			if tabs.get_tab_title(i) == "Water": tabs.current_tab = i; return true
	return _fail("Water tab unavailable") == false

func _configure_through_controls() -> bool:
	# These assignments emit the production signals rather than calling test hooks.
	var enabled := _drawer.find_child("WaterSsrEnabled", true, false) as CheckButton
	var strength := _drawer.find_child("WaterSsrStrength", true, false) as HSlider
	var debug := _drawer.find_child("WaterSsrDebugMode", true, false) as OptionButton
	var cutoff := _drawer.find_child("WaterSsrRoughnessCutoff", true, false) as HSlider
	var coarse := _drawer.find_child("WaterSsrCoarseSteps", true, false) as HSlider
	var refine := _drawer.find_child("WaterSsrRefinementSteps", true, false) as HSlider
	var ray_distance := _drawer.find_child("WaterSsrMaxRayDistance", true, false) as HSlider
	var camera_distance := _drawer.find_child("WaterSsrCameraDistance", true, false) as HSlider
	var far_roughness := _drawer.find_child("WaterFarRoughness", true, false) as HSlider
	var geometry := _drawer.find_child("FftGeometryHeightScale", true, false) as HSlider
	var normal := _drawer.find_child("FftNormalStrength", true, false) as HSlider
	if enabled == null or strength == null or debug == null or cutoff == null or coarse == null or refine == null or ray_distance == null or camera_distance == null or far_roughness == null or geometry == null or normal == null: return _fail("required SSR/FFT controls unavailable") == false
	_fft.set("Enabled", true); _fft.set("Resolution", 128); _fft.set("CascadeCount", 2); _fft.set("UpdatesPerSecond", 24)
	geometry.value = 0.0 # fixture-only deterministic geometry override
	normal.value = 0.0 # fixture-only deterministic normal override
	# Regression baseline: old FFT shading forced this .46 far roughness above
	# the .32 SSR cutoff, regardless of the live SSR controls.
	far_roughness.value = 0.46; cutoff.value = 0.32
	if _medium_ssr:
		cutoff.value = 0.25; coarse.value = 12; refine.value = 3; ray_distance.value = 120; camera_distance.value = 250
	enabled.button_pressed = false; strength.value = 0.0; debug.select(0); debug.item_selected.emit(0)
	return true

func _set_ssr(enabled_value: bool, debug_index: int, strength_value: float) -> void:
	var enabled := _drawer.find_child("WaterSsrEnabled", true, false) as CheckButton
	var strength := _drawer.find_child("WaterSsrStrength", true, false) as HSlider
	var debug := _drawer.find_child("WaterSsrDebugMode", true, false) as OptionButton
	enabled.button_pressed = enabled_value; strength.value = strength_value; debug.select(debug_index); debug.item_selected.emit(debug_index)

func _add_reflection_marker() -> void:
	var camera := root.get_viewport().get_camera_3d()
	if camera == null: return
	var marker := MeshInstance3D.new()
	var box := BoxMesh.new(); box.size = Vector3(20.0, 14.0, 1.0)
	marker.mesh = box
	var material := StandardMaterial3D.new(); material.albedo_color = Color(1.0, .92, .25); material.emission_enabled = true; material.emission = Color(1.0, .7, .08); material.emission_energy_multiplier = 4.0
	marker.material_override = material
	_scene.add_child(marker)
	var explorer := _scene.get_node_or_null("Explorer")
	var up: Vector3 = explorer.call("GetCanonicalSurfaceUp")
	var planar_forward := -camera.global_transform.basis.z
	planar_forward = (planar_forward - up * planar_forward.dot(up)).normalized()
	# Bottom sits one metre above the ocean: centre is at +8m for the 14m box.
	marker.global_position = camera.global_position + planar_forward * 40.0 + up * 2.0
	marker.global_basis = Basis(planar_forward.cross(up).normalized(), up, -planar_forward)

func _freeze_presentation() -> void:
	# Freeze time, weather, clouds, and local storm effects after streaming settles.
	for node_name in ["RingDayNightCycle", "SunshineCloudsDriver", "SunshineCloudBridge", "CloudProviderController", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience"]:
		var node := _scene.get_node_or_null(node_name)
		if node != null: node.set_process(false); node.set_physics_process(false)
	# Fixture-only: remove post-process animation and particle precipitation from
	# the comparison, so frame deltas are attributable to the SSR control.
	for environment_node: WorldEnvironment in _scene.find_children("*", "WorldEnvironment", true, false):
		if environment_node.compositor != null:
			for effect: CompositorEffect in environment_node.compositor.compositor_effects:
				effect.enabled = false
		var environment_attributes := environment_node.camera_attributes as CameraAttributesPractical
		if environment_attributes != null: environment_attributes.auto_exposure_enabled = false
	for camera: Camera3D in _scene.find_children("*", "Camera3D", true, false):
		var camera_attributes := camera.attributes as CameraAttributesPractical
		if camera_attributes != null: camera_attributes.auto_exposure_enabled = false
	for particles: GPUParticles3D in _scene.find_children("*", "GPUParticles3D", true, false):
		particles.visible = false
	# Once the ocean mesh is streamed and FFT textures are ready, freeze all
	# scene callbacks. Rendering continues, while moving sky/daylight/weather can
	# no longer contaminate the off/on/off image deltas. Drawer signals remain
	# synchronous, and the drawer/quality controller stay available for them.
	var graphics_quality := _scene.get_node_or_null("GraphicsQuality")
	for node in _scene.find_children("*", "Node", true, false):
		if node == _drawer or node == graphics_quality: continue
		node.set_process(false)
		node.set_physics_process(false)

func _capture(label: String) -> Image:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image == null or image.is_empty() or image.save_png(str(_paths[label])) != OK: return null
	return image

func _mean_difference(a: Image, b: Image) -> float:
	var sum := 0.0; var count := 0
	# Lower-middle water ROI; exclude the upper marker/horizon and drawer UI.
	for y in range(int(a.get_height() * .48), int(a.get_height() * .88), 4):
		for x in range(int(a.get_width() * .12), int(a.get_width() * .88), 4):
			var left := a.get_pixel(x, y); var right := b.get_pixel(x, y)
			sum += absf(left.r - right.r) + absf(left.g - right.g) + absf(left.b - right.b); count += 1
	return sum / max(1, count)

func _bright_fraction(image: Image) -> float:
	var bright := 0; var count := 0
	for y in range(int(image.get_height() * .48), int(image.get_height() * .88), 4):
		for x in range(int(image.get_width() * .12), int(image.get_width() * .88), 4):
			# Debug hit mode preserves lit water albedo; it is blue-gray rather than
			# white, so .2 measures actual lit hit pixels without admitting darkness.
			if image.get_pixel(x, y).get_luminance() > .2: bright += 1
			count += 1
	return float(bright) / max(1, count)

func _cleanup() -> void:
	if not _defaults.is_empty(): DirAccess.remove_absolute(_defaults)

func _fail(reason: String) -> bool:
	push_error("WATER_SSR_LIVE_CONTROLS_GPU_TEST=FAIL " + reason)
	_cleanup(); quit(1); return true
