extends SceneTree
## Forward+ terminal opaque-depth regression. --record-baseline collects known pre-fix violations.
const PROFILE := preload("res://addons/SunshineClouds2/SunshineCloudsGDTestResource.tres")
const DRIVER := preload("res://addons/SunshineClouds2/SunshineCloudsDriver.gd")
const OUTPUT := "res://.godot/cloud-mountain-depth"
const SIZE := Vector2i(512, 288)
const TIMEOUT_SECONDS := 90.0
var failed := false
var record_baseline := false
var elapsed := 0.0
var viewport_3d: SubViewport
var clouds: SunshineCloudsGD
var mountain: MeshInstance3D
var far_depth: ImageTexture
var readbacks := {}
var next_readback := 0

func _init() -> void:
	record_baseline = "--record-baseline" in OS.get_cmdline_user_args()
	if "--check-only" in OS.get_cmdline_user_args():
		if PROFILE == null or DRIVER == null:
			_fail("production Sunshine resources unavailable")
		else:
			print("CLOUD_MOUNTAIN_DEPTH_GPU_TEST=CHECK_ONLY_PASS")
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
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	_build_world()
	if failed:
		return
	var report := {"result": "baseline" if record_baseline else "pass", "units": "physical camera-ray metres", "cases": []}
	for step_m: float in [500.0, 958.0]:
		for path: String in ["main_opaque", "physical_far"]:
			var fractional := await _fractional_terminal_case(path, step_m)
			if failed:
				return
			report.cases.append(fractional)
		for boundary: Dictionary in [{"name":"near_before_cloud","depth":300.0}, {"name":"inside_cloud","depth":1700.0}]:
			for path: String in ["main_opaque", "physical_far"]:
				var case := await _case(path, str(boundary.name), float(boundary.depth), step_m)
				if failed:
					return
				report.cases.append(case)
	var file := FileAccess.open(OUTPUT.path_join("report.json"), FileAccess.WRITE)
	if file == null:
		_fail("could not write report")
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("CLOUD_MOUNTAIN_DEPTH_GPU_TEST=%s output=%s" % ["BASELINE_RECORDED" if record_baseline else "PASS", OUTPUT])
	quit(0)

func _build_world() -> void:
	viewport_3d = SubViewport.new()
	viewport_3d.size = SIZE
	viewport_3d.own_world_3d = true
	viewport_3d.render_target_update_mode = SubViewport.UPDATE_ALWAYS
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
	clouds.clouds_coverage = 0.5
	clouds.clouds_density = 0.2
	# Production maps this UI control to exponent (1 - sharpness) * 2.
	clouds.clouds_sharpness = 0.5
	clouds.atmospheric_density = 0.0
	clouds.cloud_floor = 500.0
	clouds.cloud_ceiling = 5000.0
	clouds.background_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
	clouds.storm_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
	clouds.max_step_count = 128.0
	clouds.max_lighting_steps = 1.0
	clouds.curl_noise_strength = 0.0
	clouds.dither_speed = 0.0
	clouds.ray_start_jitter_strength = 0.0
	# The camera is already inside the 500-5000 m cloud band for the
	# fractional terminal-interval control.
	clouds.cloud_frame_origin = Vector3(0, 0, 1510)
	clouds.cloud_frame_around = Vector4(1, 0, 0, 0)
	clouds.cloud_frame_up = Vector4(0, 0, -1, 5000000)
	clouds.cloud_frame_across = Vector4(0, 1, 0, 0)
	_constant_density()
	var driver := DRIVER.new() as SunshineCloudsDriverGD
	driver.update_continuously = false
	driver.clouds_resource = clouds
	driver.tracked_directional_lights = [sun]
	viewport_3d.add_child(driver)
	driver.retrieve_texture_data()
	world.compositor.compositor_effects = [clouds]
	mountain = MeshInstance3D.new()
	var mesh := BoxMesh.new()
	mesh.size = Vector3(9000, 9000, 12)
	mountain.mesh = mesh
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mountain.material_override = material
	mountain.visible = false
	viewport_3d.add_child(mountain)

func _constant_density() -> void:
	clouds.extra_large_noise_patterns = _flat2(Color.WHITE)
	clouds.height_gradient = _flat2(Color.WHITE)
	clouds.large_scale_noise = _flat3(Color.WHITE)
	clouds.medium_scale_noise = _flat3(Color(0, 0, 0, 1))
	clouds.small_scale_noise = _flat3(Color(0, 0, 0, 1))
	clouds.curl_noise = _flat3(Color(0.5, 0.5, 0.5, 1))
	clouds.dither_noise = _flat3(Color(0.5, 0.5, 0.5, 1))

func _case(path: String, boundary: String, opaque_m: float, step_m: float) -> Dictionary:
	clouds.clouds_density = 1.0
	clouds.ray_start_jitter_strength = 1.0
	clouds.cloud_frame_origin = Vector3(0, 0, 10)
	_configure(path, opaque_m, step_m)
	await _draws(6)
	var occluded := await _read_output_data()
	mountain.visible = false
	clouds.far_opaque_depth_texture = null
	clouds.refresh_compute()
	await _draws(6)
	var unoccluded := await _read_output_data()
	_configure(path, opaque_m, step_m)
	var history := await _history("%s_%s_step%d" % [path, boundary, int(step_m)])
	var result := {"path":path, "boundary":boundary, "opaque_depth_m":opaque_m, "step_m":step_m, "unoccluded":_metrics(unoccluded), "occluded":_metrics(occluded), "normal_lit":history}
	if not record_baseline:
		_assert_contract(result)
	return result

func _fractional_terminal_case(path: String, step_m: float) -> Dictionary:
	# Both rays share the same complete intervals in a uniform-density volume.
	# Extending the boundary by half a step must add precisely 0.2 * 0.5 alpha.
	# Compare the increment so the legacy first-sample contribution cancels out.
	clouds.clouds_density = 0.2
	clouds.ray_start_jitter_strength = 0.0
	clouds.cloud_frame_origin = Vector3(0, 0, 1510)
	_configure(path, step_m, step_m)
	await _draws(6)
	var reference := await _read_cloud_output(1)
	_configure(path, step_m * 1.5, step_m)
	await _draws(6)
	var color := await _read_cloud_output(1)
	if failed:
		return {}
	var alpha := _mean_alpha(color) - _mean_alpha(reference)
	var result := {"path":path, "boundary":"fractional_terminal", "step_m":step_m, "opaque_depth_m":step_m * 1.5, "expected_alpha_increment":0.1, "visible_alpha_increment":alpha}
	if not record_baseline:
		_require(absf(alpha - 0.1) <= 0.003, "terminal interval alpha was not 0.1 " + JSON.stringify(result))
	return result

func _configure(path: String, opaque_m: float, step_m: float) -> void:
	clouds.min_step_distance = step_m
	clouds.max_step_distance = step_m
	clouds.accumulation_decay = 0.0
	clouds.diagnostic_disable_temporal_history = false
	clouds.diagnostic_raw_density_matte = false
	clouds.diagnostic_depth_density_matte = false
	mountain.visible = path == "main_opaque"
	# Box front face is at the requested centre-ray depth.
	mountain.position.z = 10.0 - opaque_m - 6.0
	clouds.far_opaque_depth_texture = _far_depth(opaque_m) if path == "physical_far" else null
	clouds.refresh_compute()

func _history(label: String) -> Dictionary:
	var result := {}
	for setting: Dictionary in [{"name":"off","decay":0.0,"disabled":true}, {"name":"on","decay":0.92,"disabled":false}]:
		clouds.accumulation_decay = float(setting.decay)
		clouds.diagnostic_disable_temporal_history = bool(setting.disabled)
		clouds.refresh_compute()
		await _draws(10)
		var image := viewport_3d.get_texture().get_image()
		var name := "%s_history_%s.png" % [label, str(setting.name)]
		if image == null or image.is_empty() or image.save_png(OUTPUT.path_join(name)) != OK:
			_fail("normal lit capture failed " + name)
			return {}
		result[str(setting.name)] = _rgb(image)
	return result

func _assert_contract(result: Dictionary) -> void:
	var raw: Dictionary = result.unoccluded
	var depth: Dictionary = result.occluded
	var opaque := float(result.opaque_depth_m)
	_require(int(raw.cloud_pixels) > 0, "controlled unoccluded cloud absent " + JSON.stringify(result))
	_require(float(depth.max_terminal_m) <= opaque + 1.0, "terminal distance exceeded opaque depth " + JSON.stringify(result))
	if str(result.boundary) == "near_before_cloud":
		_require(int(depth.cloud_pixels) == 0, "300m opaque admitted cloud before 500m band " + JSON.stringify(result))
	else:
		_require(int(depth.cloud_pixels) > 0 and float(depth.min_initial_m) < opaque, "inside-cloud depth erased foreground " + JSON.stringify(result))

func _read_output_data() -> Image:
	return await _read_cloud_output(0)

func _read_cloud_output(index: int) -> Image:
	var rids: Array[RID] = clouds.get("accumulation_textures") as Array[RID]
	if rids.size() <= index or not rids[index].is_valid():
		_fail("cloud output image unavailable")
		return Image.new()
	var request := next_readback
	next_readback += 1
	RenderingServer.call_on_render_thread(_read_on_render_thread.bind(request, rids[index], SIZE.x, SIZE.y))
	while not readbacks.has(request):
		if failed:
			return Image.new()
		await RenderingServer.frame_post_draw
	var result: Dictionary = readbacks[request]
	readbacks.erase(request)
	if not bool(result.ok):
		_fail("output data readback: " + str(result.error))
		return Image.new()
	return Image.create_from_data(int(result.width), int(result.height), false, Image.FORMAT_RGBAF, result.bytes)

func _read_on_render_thread(request: int, rid: RID, width: int, height: int) -> void:
	var rd := RenderingServer.get_rendering_device()
	if rd == null or not rid.is_valid():
		call_deferred("_complete_readback", request, false, PackedByteArray(), width, height, "invalid texture")
		return
	var format := rd.texture_get_format(rid)
	var bytes := rd.texture_get_data(rid, 0)
	var ok := format.format == RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT and bytes.size() == width * height * 16
	call_deferred("_complete_readback", request, ok, bytes, width, height, "expected RGBA32F output_data_image")

func _complete_readback(request: int, ok: bool, bytes: PackedByteArray, width: int, height: int, error: String) -> void:
	readbacks[request] = {"ok":ok, "bytes":bytes, "width":width, "height":height, "error":error}

func _metrics(image: Image) -> Dictionary:
	var first := INF
	var terminal := 0.0
	var clouds_seen := 0
	var samples := 0
	# This central 8x8 ROI keeps the planar proxy's physical ray depth within
	# 0.25 m of its axial distance, including the 1700 m case.
	for y in range(SIZE.y / 2 - 4, SIZE.y / 2 + 4):
		for x in range(SIZE.x / 2 - 4, SIZE.x / 2 + 4):
			var pixel := image.get_pixel(x, y)
			samples += 1
			terminal = maxf(terminal, pixel.g)
			if pixel.r > 0.0:
				clouds_seen += 1
				first = minf(first, pixel.r)
	return {"roi_samples":samples, "cloud_pixels":clouds_seen, "min_initial_m":first if clouds_seen > 0 else 0.0, "max_terminal_m":terminal}

func _rgb(image: Image) -> Dictionary:
	var total := Vector3.ZERO
	var count := 0
	for y in range(SIZE.y / 2 - 8, SIZE.y / 2 + 8, 2):
		for x in range(SIZE.x / 2 - 8, SIZE.x / 2 + 8, 2):
			var pixel := image.get_pixel(x, y)
			total += Vector3(pixel.r, pixel.g, pixel.b)
			count += 1
	return {"mean_rgb":[total.x / count, total.y / count, total.z / count], "samples":count}

func _mean_alpha(image: Image) -> float:
	var total := 0.0
	var count := 0
	for y in range(SIZE.y / 2 - 8, SIZE.y / 2 + 8):
		for x in range(SIZE.x / 2 - 8, SIZE.x / 2 + 8):
			total += image.get_pixel(x, y).a
			count += 1
	return total / count

func _far_depth(distance: float) -> ImageTexture:
	var image := Image.create(SIZE.x, SIZE.y, false, Image.FORMAT_RF)
	image.fill(Color(distance, 0, 0, 1))
	far_depth = ImageTexture.create_from_image(image)
	return far_depth

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

func _draws(count: int) -> void:
	for ignored in count:
		if failed:
			return
		await RenderingServer.frame_post_draw

func _require(value: bool, reason: String) -> void:
	if not value:
		_fail(reason)

func _fail(reason: String) -> void:
	if failed:
		return
	failed = true
	push_error("CLOUD_MOUNTAIN_DEPTH_GPU_TEST=FAIL " + reason)
	call_deferred("_exit_failed")

func _exit_failed() -> void:
	await RenderingServer.frame_post_draw
	quit(1)
