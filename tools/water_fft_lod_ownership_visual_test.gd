extends SceneTree

## Exercises the production vertex path and fragment discard prefix. Only the
## shading following `bool use_fft_surface` is replaced with an additive marker.
const OUTPUT: String = "res://.godot/water_fft_lod_ownership"
const SOURCE: String = "res://materials/ring_ocean_proxy.gdshader"
const SIZE: Vector2i = Vector2i(256, 256)

var _black: ImageTexture
var _white: ImageTexture

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	_black = _mask(Color.BLACK)
	_white = _mask(Color.WHITE)
	var shader: Shader = Shader.new()
	shader.code = _diagnostic_source(_expand(FileAccess.get_file_as_string(SOURCE)))
	if shader.code.is_empty():
		_fail("could not construct production discard diagnostic")
		return
	var baseline: Image = await _render(shader, [true], true, "baseline_1_layer")
	var unmasked: Image = await _render(shader, [true, true, true], true, "old_unmasked_3_layers")
	var owned: Image = await _render(shader, [true, true, true], true, "owned_3_layers", [_black, _white, _white])
	var fft_disabled: Image = await _render(shader, [true, true, true], false, "owned_fft_disabled", [_black, _white, _white])
	var handoff: Image = await _render(shader, [false, true, true], true, "fine_unloaded_handoff", [_black, _black, _white])
	var distance_handoff: Image = await _render(shader, [false, false, true], true, "distance_handoff_stale_proof", [_black, _black, _white], 20.0)
	var partial: Image = await _render(shader, [true, true, true], true, "partial_ownership", [_black, _mask_2x1(Color.WHITE, Color.BLACK), _white], 1000.0, true)
	for image: Image in [baseline, unmasked, owned, fft_disabled, handoff, distance_handoff, partial]:
		if not _valid(image):
			_fail("empty or non-finite capture")
			return
	var values: Array[float] = [_center_luminance(baseline), _center_luminance(unmasked), _center_luminance(owned), _center_luminance(fft_disabled), _center_luminance(handoff), _center_luminance(distance_handoff)]
	var partial_left: float = _side_luminance(partial, true)
	var partial_right: float = _side_luminance(partial, false)
	print("WATER_FFT_LOD_OWNERSHIP_METRICS baseline=%.5f unmasked=%.5f owned=%.5f fft_disabled=%.5f handoff=%.5f distance_handoff=%.5f partial_left=%.5f partial_right=%.5f" % [values[0], values[1], values[2], values[3], values[4], values[5], partial_left, partial_right])
	if values[0] < 0.03:
		_fail("baseline did not render water")
		return
	if values[1] < values[0] * 2.2:
		_fail("unmasked overlapping layers did not accumulate significantly")
		return
	for value: float in [values[2], values[3], values[4], values[5], partial_left, partial_right]:
		if absf(value - values[0]) > 0.01:
			_fail("replacement ownership did not match one-layer baseline")
			return
	print("WATER_FFT_LOD_OWNERSHIP_VISUAL_TEST=PASS output=%s assertions=unmasked_accumulates,owned_matches,fft_independent,fine_unloaded_handoff,distance_handoff,partial_ownership human_review=OPEN" % OUTPUT)
	quit(0)

func _render(shader: Shader, visible: Array[bool], fft_ready: bool, label: String, masks: Array[ImageTexture] = [], replacement_distance: float = 1000.0, fine_left_half: bool = false) -> Image:
	var viewport: SubViewport = SubViewport.new()
	viewport.size = SIZE
	viewport.own_world_3d = true
	viewport.use_hdr_2d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var environment: Environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world: WorldEnvironment = WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	var camera: Camera3D = Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 32.0
	camera.position = Vector3(0.0, 0.0, 30.0)
	camera.near = 0.1
	camera.far = 100.0
	viewport.add_child(camera)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	for layer: int in visible.size():
		if not visible[layer]:
			continue
		var water: MeshInstance3D = MeshInstance3D.new()
		water.mesh = _quad(Rect2(-8.0, -8.0, 8.0, 16.0) if fine_left_half and layer == 0 else Rect2(-8.0, -8.0, 16.0, 16.0))
		water.material_override = _material(shader, camera.position, fft_ready, masks[layer] if not masks.is_empty() else null, replacement_distance)
		water.set_instance_shader_parameter("ocean_replacement_coverage_rect", Vector4(0.0, 0.0, 1.0, 1.0))
		viewport.add_child(water)
	for frame: int in range(8):
		await process_frame
	await RenderingServer.frame_post_draw
	var image: Image = viewport.get_texture().get_image()
	if not image.is_empty():
		image.save_png("%s/%s.png" % [OUTPUT, label])
	viewport.queue_free()
	await process_frame
	return image

func _material(shader: Shader, camera_position: Vector3, fft_ready: bool, coverage: ImageTexture, replacement_distance: float) -> ShaderMaterial:
	var material: ShaderMaterial = ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("camera_world_position", camera_position)
	material.set_shader_parameter("ring_center", Vector3(0.0, 0.0, 5000000.0))
	material.set_shader_parameter("ring_radius_meters", 5000000.0)
	material.set_shader_parameter("geomorph_enabled", false)
	material.set_shader_parameter("far_fade_enabled", false)
	material.set_shader_parameter("fft_ready", fft_ready)
	material.set_shader_parameter("fft_cascade_count", 2)
	material.set_shader_parameter("fft_geometry_spacing_meters", 0.0)
	material.set_shader_parameter("wave_geometry_strength", 0.0)
	material.set_shader_parameter("ocean_replacement_coverage_enabled", coverage != null)
	material.set_shader_parameter("ocean_replacement_coverage_atlas", coverage)
	material.set_shader_parameter("ocean_replacement_distance_meters", replacement_distance)
	return material

func _quad(bounds: Rect2) -> ArrayMesh:
	var tool: SurfaceTool = SurfaceTool.new()
	tool.begin(Mesh.PRIMITIVE_TRIANGLES)
	var points: Array[Vector3] = [Vector3(bounds.position.x, bounds.position.y, 0.0), Vector3(bounds.position.x, bounds.end.y, 0.0), Vector3(bounds.end.x, bounds.position.y, 0.0), Vector3(bounds.end.x, bounds.end.y, 0.0)]
	var indices: Array[int] = [0, 1, 2, 2, 1, 3]
	for index: int in indices:
		var point: Vector3 = points[index]
		tool.set_normal(Vector3(0.0, 0.0, 1.0))
		tool.set_color(Color(0.1, 0.4, 0.7, 0.0))
		tool.set_uv(Vector2((point.x - bounds.position.x) / bounds.size.x, (point.y - bounds.position.y) / bounds.size.y))
		tool.add_vertex(point)
	return tool.commit()

func _mask(color: Color) -> ImageTexture:
	var image: Image = Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, color)
	return ImageTexture.create_from_image(image)

func _mask_2x1(left: Color, right: Color) -> ImageTexture:
	var image: Image = Image.create(2, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, left)
	image.set_pixel(1, 0, right)
	return ImageTexture.create_from_image(image)

func _diagnostic_source(source: String) -> String:
	var fragment: int = source.find("void fragment()")
	var shading: int = source.find("bool use_fft_surface", fragment)
	if fragment < 0 or shading < fragment:
		return ""
	var diagnostic: String = source.replace("render_mode blend_mix, cull_disabled, depth_draw_always, shadows_disabled;", "render_mode blend_add, unshaded, depth_test_disabled, depth_draw_never, cull_disabled;")
	shading = diagnostic.find("bool use_fft_surface", diagnostic.find("void fragment()"))
	return diagnostic.substr(0, shading) + "ALBEDO = vec3(0.2); ALPHA = 1.0; }\n"

func _expand(source: String) -> String:
	var expanded: String = ""
	for line: String in source.split("\n"):
		if line.begins_with("#include \""):
			var first_quote: int = line.find("\"") + 1
			var second_quote: int = line.find("\"", first_quote)
			if second_quote > first_quote:
				expanded += _expand(FileAccess.get_file_as_string(line.substr(first_quote, second_quote - first_quote))) + "\n"
				continue
		expanded += line + "\n"
	return expanded

func _center_luminance(image: Image) -> float:
	var total: float = 0.0
	var samples: int = 0
	for y: int in range(image.get_height() / 2 - 16, image.get_height() / 2 + 16):
		for x: int in range(image.get_width() / 2 - 16, image.get_width() / 2 + 16):
			total += image.get_pixel(x, y).get_luminance()
			samples += 1
	return total / maxf(1.0, float(samples))

func _side_luminance(image: Image, left: bool) -> float:
	var total: float = 0.0
	var samples: int = 0
	var x0: int = 80 if left else 144
	var x1: int = 112 if left else 176
	for y: int in range(96, 160):
		for x: int in range(x0, x1):
			total += image.get_pixel(x, y).get_luminance()
			samples += 1
	return total / maxf(1.0, float(samples))

func _valid(image: Image) -> bool:
	if image == null or image.is_empty():
		return false
	for y: int in range(0, image.get_height(), 16):
		for x: int in range(0, image.get_width(), 16):
			var pixel: Color = image.get_pixel(x, y)
			if is_nan(pixel.r) or is_inf(pixel.r):
				return false
	return true

func _fail(message: String) -> void:
	push_error("WATER_FFT_LOD_OWNERSHIP_VISUAL_TEST=FAIL " + message)
	quit(1)
