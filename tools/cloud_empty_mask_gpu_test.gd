extends SceneTree
# Production compositor parity, with a separately compiled fixture-only fetch counter.
const PROFILE := preload("res://addons/SunshineClouds2/SunshineCloudsGDTestResource.tres")
const DRIVER := preload("res://addons/SunshineClouds2/SunshineCloudsDriver.gd")
const SOURCE := "res://addons/SunshineClouds2/SunshineCloudsCompute.glsl"
const SIZE := Vector2i(257, 145)
var failed := false
var elapsed := 0.0
var viewport_3d: SubViewport
var clouds: SunshineCloudsGD
var mountain: MeshInstance3D
var camera: Camera3D
var world: WorldEnvironment
var far_depth: ImageTexture
var readbacks := {}
var next_readback := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	call_deferred("_run")

func _process(delta: float) -> bool:
	elapsed += delta
	if elapsed > 120.0: _fail("watchdog expired")
	return false

func _run() -> void:
	_build_world()
	if failed: return
	var production: RDShaderFile = clouds.compute_shader
	if production == null: production = load(SOURCE) as RDShaderFile
	var counter := _counter_shader()
	if failed: return
	var report := []
	for scenario in ["main_hidden", "far_hidden", "sky_slit", "below_sky", "inside", "tangent", "history"]:
		_configure_case(scenario)
		var reference := await _snapshot(false, production)
		var optimized := await _snapshot(true, production)
		if failed: return
		if scenario == "inside" and _max_alpha(reference.density_color) <= 0.001:
			_fail("inside-cloud control contained no density"); return
		var deltas := {}
		for channel in ["raw", "density_color", "metadata", "final"]:
			var difference := _max_delta(reference[channel], optimized[channel])
			deltas[channel] = difference
			# RGBA32F production outputs should be equal; final RGBA8 allows one LSB.
			var limit := 1.0 / 255.0 + 0.00001 if channel == "final" else 0.0001
			if difference > limit: _fail("%s %s parity delta %f" % [scenario, channel, difference]); return
		clouds.diagnostic_raw_density_matte = false
		clouds.diagnostic_depth_density_matte = false
		clouds.accumulation_decay = 0.0
		clouds.compute_shader = counter
		clouds.skip_empty_cloud_mask_samples = false
		clouds.refresh_compute()
		await _draws(6)
		var off := await _read_cloud_output(0)
		clouds.skip_empty_cloud_mask_samples = true
		clouds.refresh_compute()
		await _draws(6)
		var on := await _read_cloud_output(0)
		if failed: return
		var counts := _check_counts(off, on)
		if failed: return
		if scenario == "main_hidden" or scenario == "far_hidden":
			if counts.skipped <= 0.0: _fail("hidden positive control skipped no mask fetches: " + scenario); return
		report.append({"case":scenario, "parity":deltas, "fixture_counts":counts})
	var output := "res://.godot/cloud-empty-mask-%d" % OS.get_process_id()
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output)) != OK: _fail("output directory failed"); return
	var file := FileAccess.open(output.path_join("report.json"), FileAccess.WRITE)
	if file == null: _fail("report write failed"); return
	file.store_string(JSON.stringify(report, "\t")); file.close()
	world.compositor.compositor_effects = []
	viewport_3d.queue_free()
	print("CLOUD_EMPTY_MASK_GPU_TEST=PASS output=", output)
	quit(0)

func _configure_case(scenario: String) -> void:
	mountain.visible = scenario == "main_hidden" or scenario == "sky_slit" or scenario == "history"
	mountain.position = Vector3(0, 0, -296)
	mountain.scale = Vector3.ONE
	if scenario == "sky_slit": mountain.position.x = -4500.0
	clouds.far_opaque_depth_texture = _far_depth(300.0) if scenario == "far_hidden" else null
	clouds.cloud_frame_origin = Vector3(0, 0, 1510) if scenario == "inside" else Vector3(0, 0, 10)
	clouds.cloud_frame_up = Vector4(0, 0, -1, 5000000)
	clouds.cloud_frame_around = Vector4(1, 0, 0, 0)
	clouds.cloud_frame_across = Vector4(0, 1, 0, 0)
	if scenario == "tangent":
		clouds.cloud_frame_origin = Vector3(0, -1500, 10)
		clouds.cloud_frame_up = Vector4(0, 1, 0, 5000000)
		clouds.cloud_frame_around = Vector4(0, 0, -1, 0)
		clouds.cloud_frame_across = Vector4(1, 0, 0, 0)
	viewport_3d.msaa_3d = Viewport.MSAA_4X if scenario == "sky_slit" else Viewport.MSAA_DISABLED
	clouds.accumulation_decay = 0.92 if scenario == "history" else 0.0

func _snapshot(skip: bool, shader: RDShaderFile) -> Dictionary:
	clouds.compute_shader = shader
	clouds.skip_empty_cloud_mask_samples = skip
	clouds.diagnostic_raw_density_matte = true
	clouds.refresh_compute()
	await _draws(12)
	var raw := viewport_3d.get_texture().get_image()
	clouds.diagnostic_raw_density_matte = false
	clouds.refresh_compute()
	await _draws(12)
	var metadata := await _read_cloud_output(0)
	var density_color := await _read_cloud_output(1)
	var final := viewport_3d.get_texture().get_image()
	return {"raw":raw, "density_color":density_color, "metadata":metadata, "final":final}

func _max_alpha(image: Image) -> float:
	var value := 0.0
	for y in image.get_height():
		for x in image.get_width(): value = maxf(value, image.get_pixel(x,y).a)
	return value

func _max_delta(a: Image, b: Image) -> float:
	if a == null or b == null or a.is_empty() or b.is_empty() or a.get_size() != b.get_size(): _fail("invalid parity image"); return INF
	var maximum := 0.0
	for y in a.get_height():
		for x in a.get_width():
			var av := a.get_pixel(x,y); var bv := b.get_pixel(x,y)
			for i in 4:
				if not is_finite(av[i]) or not is_finite(bv[i]): _fail("nonfinite production output"); return INF
				maximum = maxf(maximum, absf(av[i] - bv[i]))
	return maximum

func _check_counts(off: Image, on: Image) -> Dictionary:
	var saved := 0.0
	var fetched := 0.0
	for y in off.get_height():
		for x in off.get_width():
			var a := off.get_pixel(x,y); var b := on.get_pixel(x,y)
			# Counter channels: actual mask calls, visited outside-band intervals.
			if absf(a.r - b.r - b.g) > 0.01 or absf(a.g - b.g) > 0.01:
				_fail("fetch counter did not remove precisely the unused calls"); return {}
			saved += b.g; fetched += b.r
	return {"skipped":saved, "remaining_fetches":fetched}

func _counter_shader() -> RDShaderFile:
	var source := _expand_includes(SOURCE, []).replace("#[compute]", "")
	var fetch := "\t\t\tmaskSample = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoiseScale);"
	if source.count(fetch) != 1: _fail("production mask fetch instrumentation anchor changed"); return null
	source = source.replace("void main() {", "void main() { float fixtureFetches = 0.0; float fixtureEmptyIntervals = 0.0;")
	source = source.replace(fetch, "\t\t\tfixtureFetches += 1.0; " + fetch.strip_edges())
	var band := "bool inCloudAltitudeBand = clamp(cloudPos.y, cloudfloor, cloudceiling) == cloudPos.y;"
	if source.count(band) != 1: _fail("production band instrumentation anchor changed"); return null
	source = source.replace(band, band + " if (!inCloudAltitudeBand) fixtureEmptyIntervals += 1.0;")
	var output := "imageStore(output_data_image, uv, currentDataAccumilation);"
	if source.count(output) != 1: _fail("production output instrumentation anchor changed"); return null
	source = source.replace(output, "imageStore(output_data_image, uv, vec4(fixtureFetches, fixtureEmptyIntervals, 0.0, 1.0));")
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null: _fail("local compiler device missing"); return null
	var shader_source := RDShaderSource.new(); shader_source.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	rd.free()
	if spirv.compile_error_compute != "": _fail(spirv.compile_error_compute); return null
	var result := RDShaderFile.new(); result.set_bytecode(spirv)
	return result

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
	world = WorldEnvironment.new()
	world.environment = environment
	world.compositor = Compositor.new()
	viewport_3d.add_child(world)
	camera = Camera3D.new()
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
	clouds.atmospheric_density = 0.15
	clouds.cloud_floor = 500.0
	clouds.cloud_ceiling = 5000.0
	clouds.background_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
	clouds.storm_altitude = Vector4(500.0, 5000.0, 0.0, 0.0)
	clouds.max_step_count = 32.0
	clouds.max_lighting_steps = 1.0
	clouds.curl_noise_strength = 0.0
	clouds.dither_speed = 0.0
	clouds.ray_start_jitter_strength = 0.0
	clouds.resolution_scale = 1
	clouds.min_step_distance = 500.0
	clouds.max_step_distance = 500.0
	clouds.accumulation_decay = 0.0
	# The camera is already inside the 500-5000 m cloud band for the
	# fractional terminal-interval control.
	clouds.cloud_frame_origin = Vector3(0, 0, 10)
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

func _read_cloud_output(index: int) -> Image:
	var rids: Array[RID] = clouds.get("accumulation_textures") as Array[RID]
	if rids.size() <= index or not rids[index].is_valid():
		_fail("cloud output image unavailable")
		return Image.new()
	var request := next_readback
	next_readback += 1
	RenderingServer.call_on_render_thread(_read_on_render_thread.bind(request, rids[index], SIZE.x / 2, SIZE.y / 2))
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

func _expand_includes(path: String, stack: Array[String]) -> String:
	if path in stack:
		_fail("recursive include: %s" % path)
		return ""
	var text: String = FileAccess.get_file_as_string(path)
	if text.is_empty():
		_fail("could not read shader source: %s" % path)
		return ""
	var result := ""
	var next_stack: Array[String] = stack.duplicate()
	next_stack.append(path)
	for line in text.split("\n"):
		var stripped: String = line.strip_edges()
		if stripped.begins_with("#include"):
			var first_quote: int = stripped.find("\"")
			var last_quote: int = stripped.rfind("\"")
			if first_quote < 0 or last_quote <= first_quote:
				_fail("malformed include in %s: %s" % [path, line])
				return ""
			var relative: String = stripped.substr(first_quote + 1, last_quote - first_quote - 1)
			var child: String = path.get_base_dir().path_join(relative).simplify_path()
			var included: String = _expand_includes(child, next_stack)
			if included.is_empty():
				return ""
			result += included + "\n"
		else:
			result += line + "\n"
	return result

func _fail(reason: String) -> void:
	if failed: return
	failed = true
	push_error("CLOUD_EMPTY_MASK_GPU_TEST=FAIL " + reason)
	quit(1)
