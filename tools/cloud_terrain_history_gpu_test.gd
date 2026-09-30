extends SceneTree
## Production Forward+ regression for temporal clouds where terrain/ring depth is
## directly behind the cloud.  `--record-baseline` records rather than enforcing
## the contracts, and `--baseline-shader` uses the saved pre-fix compute source.

const PROFILE := preload("res://addons/SunshineClouds2/SunshineCloudsGDTestResource.tres")
const DRIVER := preload("res://addons/SunshineClouds2/SunshineCloudsDriver.gd")
const SIZE := Vector2i(512, 288)
const OUTPUT := "res://.godot/cloud-terrain-history"
const TIMEOUT_SECONDS := 150.0

var failed := false
var record_baseline := false
var use_baseline_shader := false
var elapsed := 0.0
var viewport_3d: SubViewport
var clouds: SunshineCloudsGD
var opaque_plane: MeshInstance3D
var far_depth: ImageTexture
var far_depth_image: Image
var dither_image: Image
var dither_texture: ImageTexture3D
var readbacks := {}
var next_readback := 0

func _init() -> void:
	record_baseline = "--record-baseline" in OS.get_cmdline_user_args()
	use_baseline_shader = "--baseline-shader" in OS.get_cmdline_user_args()
	if "--check-only" in OS.get_cmdline_user_args():
		if PROFILE == null or DRIVER == null:
			_fail("production Sunshine resources unavailable")
		else:
			print("CLOUD_TERRAIN_HISTORY_GPU_TEST=CHECK_ONLY_PASS")
			quit(0)
		return
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required; use --check-only")
		return
	call_deferred("_run")

func _process(delta: float) -> bool:
	elapsed += delta
	if elapsed > TIMEOUT_SECONDS:
		_fail("scene timer watchdog exceeded %.1f seconds" % TIMEOUT_SECONDS)
	return false

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	_build_world()
	if failed:
		return
	var report := {
		"result": "baseline" if record_baseline else "pass",
		"shader": "saved_before" if use_baseline_shader else "current",
		"units": "physical camera-ray metres",
		"cases": []
	}
	for path in ["main_opaque", "physical_far"]:
		for scale in [1, 50]:
			var stable := await _stable_terrain_case(path, false, scale)
			if failed:
				return
			report.cases.append(stable)
			var moving := await _stable_terrain_case(path, true, scale)
			if failed:
				return
			report.cases.append(moving)
		_configure_cloud_scale(1)
		var occluder := await _new_occluder_case(path)
		if failed:
			return
		report.cases.append(occluder)
	var filename := "baseline-report.json" if record_baseline else "report.json"
	var file := FileAccess.open(OUTPUT.path_join(filename), FileAccess.WRITE)
	if file == null:
		_fail("could not write report")
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("CLOUD_TERRAIN_HISTORY_GPU_TEST=%s output=%s" % ["BASELINE_RECORDED" if record_baseline else "PASS", OUTPUT])
	quit(0)

func _build_world() -> void:
	viewport_3d = SubViewport.new()
	viewport_3d.size = SIZE
	viewport_3d.own_world_3d = true
	viewport_3d.render_target_update_mode = SubViewport.UPDATE_DISABLED
	root.add_child(viewport_3d)
	var carrier := TextureRect.new()
	carrier.texture = viewport_3d.get_texture()
	carrier.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.add_child(carrier)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.08, 0.16, 0.28, 1.0)
	var world := WorldEnvironment.new()
	world.environment = environment
	world.compositor = Compositor.new()
	viewport_3d.add_child(world)
	var camera := Camera3D.new()
	camera.far = 100000.0
	camera.fov = 50.0
	camera.position = Vector3(0, 0, 10)
	viewport_3d.add_child(camera)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	camera.current = true
	var sun := DirectionalLight3D.new()
	sun.light_energy = 0.7
	viewport_3d.add_child(sun)
	clouds = PROFILE.duplicate(true) as SunshineCloudsGD
	if clouds == null:
		_fail("Sunshine resource unavailable")
		return
	clouds.effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	clouds.diagnostic_temporal_readback = true
	clouds.diagnostic_menu_history_weights = true
	clouds.clouds_coverage = 0.5
	clouds.clouds_density = 1.0
	clouds.clouds_sharpness = 0.5
	clouds.atmospheric_density = 0.0
	clouds.cloud_floor = 500.0
	clouds.cloud_ceiling = 5000.0
	clouds.background_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
	clouds.storm_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
	clouds.min_step_distance = 958.0
	clouds.max_step_distance = 958.0
	clouds.max_step_count = 128.0
	clouds.max_lighting_steps = 1.0
	clouds.curl_noise_strength = 0.0
	clouds.dither_speed = 0.0
	clouds.ray_start_jitter_strength = 1.0
	clouds.cloud_frame_origin = Vector3(0, 0, 10)
	clouds.cloud_frame_around = Vector4(1, 0, 0, 0)
	clouds.cloud_frame_up = Vector4(0, 0, -1, 5000000)
	clouds.cloud_frame_across = Vector4(0, 1, 0, 0)
	_constant_density()
	if use_baseline_shader:
		var shader_file := _baseline_shader_file()
		if shader_file == null:
			return
		clouds.compute_shader = shader_file
	var driver := DRIVER.new() as SunshineCloudsDriverGD
	driver.update_continuously = false
	driver.clouds_resource = clouds
	driver.tracked_directional_lights = [sun]
	viewport_3d.add_child(driver)
	driver.retrieve_texture_data()
	world.compositor.compositor_effects = [clouds]
	opaque_plane = MeshInstance3D.new()
	var mesh := BoxMesh.new()
	mesh.size = Vector3(450000, 450000, 12)
	opaque_plane.mesh = mesh
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	opaque_plane.material_override = material
	opaque_plane.visible = false
	viewport_3d.add_child(opaque_plane)

func _constant_density() -> void:
	clouds.extra_large_noise_patterns = _flat2(Color.WHITE)
	clouds.height_gradient = _flat2(Color.WHITE)
	clouds.large_scale_noise = _flat3(Color.WHITE)
	clouds.medium_scale_noise = _flat3(Color(0, 0, 0, 1))
	clouds.small_scale_noise = _flat3(Color(0, 0, 0, 1))
	clouds.curl_noise = _flat3(Color(0.5, 0.5, 0.5, 1))
	dither_image = Image.create(1, 1, false, Image.FORMAT_RGBA8)
	dither_image.set_pixel(0, 0, Color(0.1, 0.1, 0.1, 1))
	dither_texture = ImageTexture3D.new()
	if dither_texture.create(Image.FORMAT_RGBA8, 1, 1, 1, false, [dither_image]) != OK:
		_fail("could not create dither texture")
		return
	clouds.dither_noise = dither_texture

func _stable_terrain_case(path: String, moving: bool, scale: int = 1) -> Dictionary:
	# Terrain depth lies inside the cloud band. Alternating the first interval's
	# dither phase makes the density march alternate between terminal conditions.
	_configure_cloud_scale(scale)
	var terrain_depth := 60000.0 if scale == 50 else 1200.0
	_configure_depth(path, terrain_depth, true)
	var start_camera := viewport_3d.get_camera_3d()
	start_camera.position = Vector3(0, 0, 10)
	start_camera.look_at(Vector3.ZERO, Vector3.UP)
	clouds.accumulation_decay = 0.95
	clouds.diagnostic_disable_temporal_history = false
	clouds.refresh_compute()
	# Warm the ping-pong buffers at a fixed initial phase rather than measuring
	# their initial clear state as temporal noise.
	await _draws(32)
	var weights := []
	var raw_alphas := []
	var temporal_alphas := []
	var depth_breaks := []
	for frame in range(16):
		# With callback phase cancelled in _draws, these are the actual first-ray
		# dither values instead of merely texture seeds.
		_set_dither(0.1 if frame % 2 == 0 else 0.9)
		if moving:
			var camera := viewport_3d.get_camera_3d()
			camera.position.x = float(frame + 1) * 0.1
			camera.look_at(Vector3(camera.position.x, 0, 0), Vector3.UP)
			camera.rotate_y(deg_to_rad(0.05 * float(frame + 1)))
		await _draws(1)
		var diagnostic := await _read_cloud_output(1)
		var data := await _read_cloud_output(0)
		var temporal := await _read_latest_history_color()
		weights.append(_mean_channel(diagnostic, 0))
		raw_alphas.append(_mean_channel(diagnostic, 3))
		temporal_alphas.append(_mean_alpha(temporal))
		depth_breaks.append(_mean_channel(data, 3))
	var result := {
		"name": "moving_terrain" if moving else "static_terrain",
		"path": path,
		"scale": scale,
		"opaque_depth_m": terrain_depth,
		"step_m": clouds.max_step_distance,
		"cloud_floor_m": clouds.cloud_floor,
		"retained_weight_mean": _mean(weights),
		"retained_weight_min": _minimum(weights),
		"raw_alpha_variance": _variance(raw_alphas),
		"temporal_alpha_variance": _variance(temporal_alphas),
		"depth_break_variance": _variance(depth_breaks),
		"weights": weights
	}
	if not record_baseline:
		_require(float(result.retained_weight_min) >= 0.94, "stable terrain rejected valid .95 history " + JSON.stringify(result))
		_require(float(result.depth_break_variance) > 0.001, "jitter sequence did not exercise terminal depth-break changes " + JSON.stringify(result))
		_require(float(result.raw_alpha_variance) > 0.001, "jitter sequence did not vary the raw terminal cloud sample " + JSON.stringify(result))
		_require(float(result.temporal_alpha_variance) < float(result.raw_alpha_variance), "history did not suppress stable terrain grain " + JSON.stringify(result))
	return result

func _new_occluder_case(path: String) -> Dictionary:
	_configure_depth(path, 1200.0, true)
	var camera := viewport_3d.get_camera_3d()
	camera.position = Vector3(0, 0, 10)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	clouds.accumulation_decay = 0.95
	clouds.diagnostic_disable_temporal_history = false
	clouds.refresh_compute()
	await _draws(6)
	_configure_depth(path, 300.0, false)
	await _draws(1)
	var diagnostic := await _read_cloud_output(1)
	var cloud_color := await _read_latest_history_color()
	var result := {
		"name": "new_foreground_occluder",
		"path": path,
		"retained_weight": _mean_channel(diagnostic, 0),
		"reason": _mean_channel(diagnostic, 1),
		"current_alpha": _mean_channel(diagnostic, 3),
		"visible_alpha": _mean_alpha(cloud_color)
	}
	if not record_baseline:
		_require(float(result.retained_weight) <= 0.10, "new terrain occluder retained old cloud history " + JSON.stringify(result))
		_require(absf(float(result.reason) - 0.5) <= 0.01, "new terrain occluder did not report a visibility rejection " + JSON.stringify(result))
		_require(float(result.visible_alpha) <= 0.02, "cloud history leaked through new terrain occluder " + JSON.stringify(result))
	return result

func _configure_cloud_scale(scale: int) -> void:
	if scale == 50:
		clouds.cloud_floor = 25000.0
		clouds.cloud_ceiling = 250000.0
		clouds.background_altitude = Vector4(25000.0, 250000.0, 0.0, 0.0)
		clouds.storm_altitude = Vector4(25000.0, 250000.0, 0.0, 0.0)
		clouds.min_step_distance = 47900.0
		clouds.max_step_distance = 47900.0
	else:
		clouds.cloud_floor = 500.0
		clouds.cloud_ceiling = 5000.0
		clouds.background_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
		clouds.storm_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
		clouds.min_step_distance = 958.0
		clouds.max_step_distance = 958.0
	clouds.cloud_frame_origin = Vector3(0, 0, 10)

func _configure_depth(path: String, depth_m: float, reset_history: bool) -> void:
	opaque_plane.visible = path == "main_opaque"
	opaque_plane.position.z = 10.0 - depth_m - 6.0
	if path == "physical_far":
		_update_far_depth(depth_m)
		clouds.far_opaque_depth_texture = far_depth
	else:
		clouds.far_opaque_depth_texture = null
	if reset_history:
		clouds.refresh_compute()

func _set_dither(value: float) -> void:
	dither_image.set_pixel(0, 0, Color(value, value, value, 1))
	dither_texture.update([dither_image])

func _read_cloud_output(index: int) -> Image:
	var rids: Array[RID] = clouds.get("accumulation_textures") as Array[RID]
	if rids.size() <= index or not rids[index].is_valid():
		_fail("cloud output image unavailable")
		return Image.new()
	var request := next_readback
	next_readback += 1
	RenderingServer.call_on_render_thread(_read_on_render_thread.bind(request, rids[index]))
	while not readbacks.has(request):
		if failed:
			return Image.new()
		await RenderingServer.frame_post_draw
	var result: Dictionary = readbacks[request]
	readbacks.erase(request)
	if not bool(result.ok):
		_fail("output readback: " + str(result.error))
		return Image.new()
	return Image.create_from_data(SIZE.x, SIZE.y, false, Image.FORMAT_RGBAF, result.bytes)

func _read_latest_history_color() -> Image:
	# The callback flips this selector before packing the next dispatch. Thus true
	# means the preceding draw wrote B (index 3), while false means it wrote A.
	var selector := bool(clouds.get("accumulation_is_a"))
	return await _read_cloud_output(3 if selector else 2)

func _read_on_render_thread(request: int, rid: RID) -> void:
	var rd := RenderingServer.get_rendering_device()
	var bytes := rd.texture_get_data(rid, 0) if rd != null and rid.is_valid() else PackedByteArray()
	call_deferred("_complete_readback", request, bytes.size() == SIZE.x * SIZE.y * 16, bytes)

func _complete_readback(request: int, ok: bool, bytes: PackedByteArray) -> void:
	readbacks[request] = {"ok": ok, "bytes": bytes, "error": "expected RGBA32F"}

func _mean_channel(image: Image, channel: int) -> float:
	var total := 0.0
	var count := 0
	for y in range(SIZE.y / 2 - 8, SIZE.y / 2 + 8):
		for x in range(SIZE.x / 2 - 8, SIZE.x / 2 + 8):
			var pixel := image.get_pixel(x, y)
			total += [pixel.r, pixel.g, pixel.b, pixel.a][channel]
			count += 1
	return total / max(1, count)

func _mean_alpha(image: Image) -> float:
	return _mean_channel(image, 3)

func _mean(values: Array) -> float:
	var total := 0.0
	for value in values:
		total += float(value)
	return total / max(1, values.size())

func _minimum(values: Array) -> float:
	var result := INF
	for value in values:
		result = minf(result, float(value))
	return result if result < INF else 0.0

func _variance(values: Array) -> float:
	var average := _mean(values)
	var total := 0.0
	for value in values:
		total += pow(float(value) - average, 2.0)
	return total / max(1, values.size())

func _flat2(color: Color) -> ImageTexture:
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, color)
	return ImageTexture.create_from_image(image)

func _flat3(color: Color) -> ImageTexture3D:
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, color)
	var texture := ImageTexture3D.new()
	if texture.create(Image.FORMAT_RGBA8, 1, 1, 1, false, [image]) != OK:
		_fail("could not create constant texture")
	return texture

func _update_far_depth(distance: float) -> void:
	if far_depth_image == null:
		far_depth_image = Image.create(SIZE.x, SIZE.y, false, Image.FORMAT_RF)
		far_depth = ImageTexture.create_from_image(far_depth_image)
	far_depth_image.fill(Color(distance, 0, 0, 1))
	far_depth.update(far_depth_image)

func _draws(count: int) -> void:
	for ignored in count:
		if failed:
			return
		# Sunshine folds the callback serial into its golden-ratio dither phase.
		# Set the opposing phase immediately before the callback so every run gets
		# the same ray-start sequence, independent of prior render activity.
		var next_callback := float(int(clouds.get("render_callback_serial")) + 1)
		clouds.current_time = -next_callback * 0.6180339887498949
		viewport_3d.render_target_update_mode = SubViewport.UPDATE_ONCE
		await RenderingServer.frame_post_draw

func _baseline_shader_file() -> RDShaderFile:
	var source_path := "res://.godot/cloud_terrain_history_before.glsl"
	if not FileAccess.file_exists(source_path):
		_fail("baseline shader missing: " + source_path)
		return null
	var expanded := _expand_includes(source_path, [])
	if expanded.is_empty():
		return null
	var source := RDShaderSource.new()
	source.source_compute = expanded.replace("#[compute]", "")
	var rd := RenderingServer.get_rendering_device()
	var spirv := rd.shader_compile_spirv_from_source(source)
	if spirv.compile_error_compute != "":
		_fail("baseline shader compile failed: " + spirv.compile_error_compute)
		return null
	var shader_file := RDShaderFile.new()
	shader_file.set_bytecode(spirv)
	return shader_file

func _expand_includes(path: String, stack: Array[String]) -> String:
	if path in stack:
		_fail("recursive baseline include: " + path)
		return ""
	var text := FileAccess.get_file_as_string(path)
	if text.is_empty():
		_fail("could not read baseline shader: " + path)
		return ""
	var result := ""
	var next_stack := stack.duplicate()
	next_stack.append(path)
	for line in text.split("\n"):
		var stripped := line.strip_edges()
		if stripped.begins_with("#include"):
			var first_quote := stripped.find("\"")
			var last_quote := stripped.rfind("\"")
			if first_quote < 0 or last_quote <= first_quote:
				_fail("malformed include: " + line)
				return ""
			var relative := stripped.substr(first_quote + 1, last_quote - first_quote - 1)
			var child := path.get_base_dir().path_join(relative).simplify_path()
			# Saved source lives under .godot; its original relative includes do not.
			if path.begins_with("res://.godot/"):
				child = "res://addons/SunshineClouds2/".path_join(relative).simplify_path()
			var included := _expand_includes(child, next_stack)
			if included.is_empty():
				return ""
			result += included + "\n"
		else:
			result += line + "\n"
	return result

func _require(value: bool, reason: String) -> void:
	if not value:
		_fail(reason)

func _fail(reason: String) -> void:
	if failed:
		return
	failed = true
	push_error("CLOUD_TERRAIN_HISTORY_GPU_TEST=FAIL " + reason)
	call_deferred("_exit_failed")

func _exit_failed() -> void:
	await RenderingServer.frame_post_draw
	quit(1)
