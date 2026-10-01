extends SceneTree

## GPU regression for the production shell + curved clipmap. Captures stay in
## .godot so this test cannot pollute user-facing screenshot folders.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const DEFAULTS_SCRIPT := preload("res://tools/GameplayCloudTuningProbe.cs")
const PROBE_SCRIPT := preload("res://tools/FarCloudDetailProbe.cs")
const OUT_DIR := "res://.godot/far_cloud_detail"
const ISOLATED_PATH := "res://.godot/far_cloud_detail_gpu_defaults.tres"
var _world: Node3D
var _far: Node
var _clouds: Resource
var _probe: RefCounted
var _elapsed := 0.0
var _cloud_nodes: Array[Node] = []
var _shell_nodes: Array[Node] = []

func _initialize() -> void:
	call_deferred("_run")

func _process(delta: float) -> bool:
	_elapsed += delta
	if _elapsed > 60.0: _fail("timed out"); return true
	return false

func _run() -> void:
	if DisplayServer.get_name() == "headless":
		return _fail("requires a GPU display session")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	_world = SCENE.instantiate() as Node3D
	_far = _world.get_node("ScaledFarViewport")
	_clouds = _world.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	_probe = PROBE_SCRIPT.new()
	var defaults: RefCounted = DEFAULTS_SCRIPT.new()
	if not defaults.InstallShared(_world.get_node("SunshineCloudBridge"), ISOLATED_PATH): return _fail("could not install isolated defaults")
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _world, 5000000.0)
	launcher.free()
	_world.get_node("RingDayNightCycle").set_process(false)
	root.add_child(_world)
	for _i in 8: await process_frame
	if _clouds == null: return _fail("Sunshine cloud source unavailable")
	_world.get_node("SunshineCloudsDriver").set_process(false)
	_world.get_node("SunshineCloudBridge").set_process(false)
	_world.get_node("RingVolumetricClouds").set_process(false)
	_world.get_node("Explorer").set_process(false)
	_world.get_node("Explorer").set_physics_process(false)
	_world.get_node("Explorer/Head/Camera3D").set_process(false)
	_far.set("FarStormLightningEnabled", false)
	var far_world := _far.get_node_or_null("FarRingViewport")
	if far_world == null: return _fail("active certified FarRingViewport unavailable")
	for node in far_world.get_children():
		if str(node.name).begins_with("FarCloudClipmapLayer"):
			_cloud_nodes.append(node)
		elif node.name == "ScaledCloudShell":
			_cloud_nodes.append(node)
			_shell_nodes.append(node)
	# The reported failure is most visible at the certified 20.7 km altitude.
	# Move the actual source camera in canonical space before synchronizing the
	# far viewport, then freeze it for controlled image comparisons.
	_place_certified_camera()
	# Synchronize geometry and camera once. All comparisons below deliberately
	# freeze the viewport after their controlled uniforms are installed.
	_far.set_process(true)
	await process_frame
	await process_frame
	_far.set_process(false)
	var empty := await _capture("cloud_disabled", true, false)
	var cloudy_atlas: ImageTexture = _weather_atlas(.95, 0.0)
	var clear_atlas: ImageTexture = _weather_atlas(0.0, 0.0)
	var storm_atlas: ImageTexture = _weather_atlas(0.0, 1.0)
	_clouds.set("clouds_coverage", .95)
	_far.call("ConfigureMeshCloudOwnership", true, 68500.0, 2000.0, 6000.0, 45000.0)
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, false, 0.0, 1.0, 180000.0, 0.0)
	_apply_controlled_weather(cloudy_atlas, .90)
	var fallback := await _capture("fallback", false, false)
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	_apply_controlled_weather(cloudy_atlas, .90)
	var enabled := await _capture("cloudy_enabled", false, false)
	if not _has_finite_visible_pixels(enabled): return _fail("enabled far cloud capture has no finite visible pixels")
	if not _has_variation(enabled): return _fail("enabled far cloud source field has no visible variation")
	if _mean_absolute_delta(fallback, enabled) < 0.002: return _fail("enabled density capture is indistinguishable from fallback")
	# The global shell can make a whole-viewport delta pass while the actual
	# regional clipmap is invisible.  Hide only the shell and require bodies
	# from the three curved regional layers at the certified camera altitude.
	var regional := await _capture("regional_certified_20_7km", false, false, true)
	var regional_empty := await _capture("regional_certified_20_7km_empty", true, false, true)
	var regional_changes: Vector2 = _pixel_change_stats(regional_empty, regional, .01)
	if regional_changes.x < 50.0 or regional_changes.y < .01: return _fail("certified regional clipmap has no visible cloud bodies changed=%d max=%.6f" % [int(regional_changes.x), regional_changes.y])
	# Render the actual ownership endpoints under identical controlled clouds.
	# This catches a handoff that updates uniforms but never affects far pixels.
	# Use the dense legacy fallback field here: it guarantees a cloud body within
	# the overlap, whereas imported density can be legitimately empty along this
	# particular certified camera ray.
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, false, 0.0, 1.0, 180000.0, 0.0)
	_far.call("ConfigureMeshCloudOwnership", true, 6000.0, 2000.0, 6000.0, 5000.0)
	_apply_controlled_weather(cloudy_atlas, .90)
	var short_handoff := await _capture("handoff_5k_6k", false, false, true)
	_far.call("ConfigureMeshCloudOwnership", true, 88500.0, 2000.0, 6000.0, 72000.0)
	_apply_controlled_weather(cloudy_atlas, .90)
	var long_handoff := await _capture("handoff_auto_72k_88_5k", false, false, true)
	var ownership_delta: float = _mean_absolute_delta(short_handoff, long_handoff)
	var ownership_changes: Vector2 = _pixel_change_stats(short_handoff, long_handoff, .01)
	if ownership_changes.x < 50.0 or ownership_changes.y < .01: return _fail("far ownership endpoints did not change enough regional handoff pixels mean=%.7f changed=%d max=%.6f" % [ownership_delta, int(ownership_changes.x), ownership_changes.y])
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	_far.call("ConfigureMeshCloudOwnership", true, 68500.0, 2000.0, 6000.0, 45000.0)
	_apply_controlled_weather(cloudy_atlas, .90)
	_apply_controlled_weather(clear_atlas, 0.0)
	# Geometry/camera were synchronized before controlled inputs. Freeze the far
	# viewport while comparing policy-only weather states so no runtime publish
	# can replace the controlled material inputs.
	var clear := await _capture("clear_enabled", false, false)
	_apply_controlled_weather(storm_atlas, 0.0)
	var storm := await _capture("storm_enabled", false, false)
	if _mean_absolute_delta(enabled, clear) < 0.002: return _fail("controlled clear weather did not remove cloudy far contribution")
	if _mean_absolute_delta(storm, clear) < 0.002: return _fail("storm weather did not rescue far cloud contribution from clear patch")
	_apply_controlled_weather(cloudy_atlas, .90)
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 0.0, 180000.0, 0.0)
	for _i in 3: await process_frame
	var transparent := await _capture("opacity_zero", false, false)
	var paired_empty := await _capture("opacity_zero_cloud_disabled", true, false)
	var zero_delta := _mean_absolute_delta(paired_empty, transparent)
	print("FAR_CLOUD_DETAIL_GPU_DELTAS fallback_cloudy=%.6f handoff_5k_6k_auto_72k_88_5k=%.6f handoff_changed=%d handoff_max=%.6f cloudy_clear=%.6f storm_clear=%.6f initial_empty_zero=%.6f paired_empty_zero=%.6f" % [_mean_absolute_delta(fallback, enabled), ownership_delta, int(ownership_changes.x), ownership_changes.y, _mean_absolute_delta(enabled, clear), _mean_absolute_delta(storm, clear), _mean_absolute_delta(empty, transparent), zero_delta])
	if zero_delta > 0.004: return _fail("zero far-cloud opacity still changes rendered cloud contribution")
	# Night must remain finite even when the detail field is active.
	_far.call("ConfigureSunshineFarCloudDetail", _clouds, true, 1.0, 1.0, 180000.0, 0.0)
	_world.get_node("RingDayNightCycle").call("set_cycle_fraction", 0.5)
	for _i in 3: await process_frame
	var night := await _capture("night")
	if not _all_finite(night): return _fail("night far-cloud capture contains non-finite color")
	print("FAR_CLOUD_DETAIL_GPU_TEST=PASS output=%s delta=%.5f" % [OUT_DIR, _mean_absolute_delta(fallback, enabled)])
	_world.queue_free()
	await process_frame
	await process_frame
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	quit(0)

func _weather_atlas(coverage: float, storm: float) -> ImageTexture:
	var image: Image = Image.create(16, 16, false, Image.FORMAT_RGBA8)
	image.fill(Color(coverage, storm, .5, .5))
	return ImageTexture.create_from_image(image)

func _apply_controlled_weather(atlas: ImageTexture, local_coverage: float) -> void:
	var params: Vector4 = Vector4(0.0, 64000.0, .12, 761.0)
	var frame: Vector4 = Vector4(31415926.0, 0.0, 0.0, local_coverage)
	_clouds.set("cloud_weather_params", params)
	_clouds.set("cloud_weather_frame", frame)
	for material: ShaderMaterial in _probe.GetClipmapMaterials(_far):
		_apply_weather_material(material, atlas, params, frame)
	var shell: ShaderMaterial = _probe.GetShellCloudMaterial(_far) as ShaderMaterial
	if shell != null: _apply_weather_material(shell, atlas, params, frame)

func _apply_weather_material(material: ShaderMaterial, atlas: ImageTexture, params: Vector4, frame: Vector4) -> void:
	material.set_shader_parameter("weather_atlas", atlas)
	material.set_shader_parameter("weather_atlas_enabled", true)
	material.set_shader_parameter("weather_time_seconds", 0.0)
	material.set_shader_parameter("weather_wind_uv_per_second", Vector2.ZERO)
	material.set_shader_parameter("cloud_weather_params", params)
	material.set_shader_parameter("cloud_weather_frame", frame)
	material.set_shader_parameter("far_density_coverage_bias", 0.0)

func _capture(label: String, hide_clouds := false, sync_far := true, hide_shell := false) -> Image:
	var viewport := _far.get_node_or_null("FarRingViewport") as SubViewport
	if viewport == null: _fail("production far viewport unavailable"); return Image.new()
	if sync_far:
		_far.set_process(true)
		await process_frame
		await process_frame
		_far.set_process(false)
	if hide_clouds or hide_shell:
		var hidden_nodes: Array[Node] = _cloud_nodes if hide_clouds else _shell_nodes
		for node in hidden_nodes:
			var geometry := node as GeometryInstance3D
			if geometry != null: geometry.visible = false
	await RenderingServer.frame_post_draw
	# Mesh/resource swaps can be deferred past the first callback even while
	# process is frozen; sample the following completed draw for every capture.
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if hide_clouds or hide_shell:
		var restored_nodes: Array[Node] = _cloud_nodes if hide_clouds else _shell_nodes
		for node in restored_nodes:
			var geometry := node as GeometryInstance3D
			if geometry != null: geometry.visible = true
	if image == null or image.is_empty(): _fail("empty far viewport capture " + label); return Image.new()
	if image.save_png(OUT_DIR.path_join(label + ".png")) != OK: _fail("could not save capture " + label)
	return image

func _place_certified_camera() -> void:
	var grid: Node = _world.get_node_or_null("ChunkGridManager") as Node
	var camera: Camera3D = _world.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	if grid == null or camera == null: return
	const ACROSS_CENTER_METERS := 159000.0
	var camera_world: Vector3 = grid.call("CanonicalRingPositionToWorld", Vector3(0.0, 20700.0, ACROSS_CENTER_METERS)) as Vector3
	var radial_probe: Vector3 = grid.call("CanonicalRingPositionToWorld", Vector3(0.0, 20710.0, ACROSS_CENTER_METERS)) as Vector3
	var target_world: Vector3 = grid.call("CanonicalRingPositionToWorld", Vector3(60000.0, 3000.0, ACROSS_CENTER_METERS)) as Vector3
	var ring_up: Vector3 = (radial_probe - camera_world).normalized()
	camera.global_position = camera_world
	camera.look_at(target_world, ring_up)

func _all_finite(image: Image) -> bool:
	for y in range(0, image.get_height(), 4):
		for x in range(0, image.get_width(), 4):
			var c := image.get_pixel(x, y)
			if is_nan(c.r) or is_nan(c.g) or is_nan(c.b) or is_inf(c.r) or is_inf(c.g) or is_inf(c.b): return false
	return true

func _has_finite_visible_pixels(image: Image) -> bool:
	if not _all_finite(image): return false
	for y in range(0, image.get_height(), 4):
		for x in range(0, image.get_width(), 4):
			var c := image.get_pixel(x, y)
			if max(c.r, max(c.g, c.b)) > 0.01: return true
	return false

func _has_variation(image: Image) -> bool:
	var lo := 1.0e20; var hi := -1.0e20
	for y in range(0, image.get_height(), 6):
		for x in range(0, image.get_width(), 6):
			var c := image.get_pixel(x, y); var l := c.r * .2126 + c.g * .7152 + c.b * .0722
			lo = min(lo, l); hi = max(hi, l)
	return hi - lo > 0.015

func _mean_absolute_delta(a: Image, b: Image) -> float:
	if a.get_size() != b.get_size(): return INF
	var sum := 0.0; var count := 0
	for y in range(0, a.get_height(), 4):
		for x in range(0, a.get_width(), 4):
			var ca := a.get_pixel(x, y); var cb := b.get_pixel(x, y)
			sum += absf(ca.r - cb.r) + absf(ca.g - cb.g) + absf(ca.b - cb.b); count += 3
	return sum / max(1, count)

func _pixel_change_stats(a: Image, b: Image, threshold: float) -> Vector2:
	if a.get_size() != b.get_size(): return Vector2(-1.0, INF)
	var changed: int = 0
	var maximum: float = 0.0
	for y: int in a.get_height():
		for x: int in a.get_width():
			var ca: Color = a.get_pixel(x, y)
			var cb: Color = b.get_pixel(x, y)
			var delta: float = absf(ca.r - cb.r) + absf(ca.g - cb.g) + absf(ca.b - cb.b)
			maximum = maxf(maximum, delta)
			if delta > threshold: changed += 1
	return Vector2(float(changed), maximum)

func _fail(message: String) -> void:
	push_error("FAR_CLOUD_DETAIL_GPU_TEST=FAIL " + message)
	if _world != null: _world.queue_free()
	DirAccess.remove_absolute(ProjectSettings.globalize_path(ISOLATED_PATH))
	quit(1)
