extends SceneTree

## GPU-only ownership-ratio contract for the three production lightning shaders.
## It checks black cloud-clear and lit cloud-body deltas separately.
const SIZE := 64
var _viewport: SubViewport
var _quad: MeshInstance3D
var _default_mesh: Mesh
var _bright_background := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		return _fail("RenderingDevice required; headless is not a blend result")
	call_deferred("_run")

func _run() -> void:
	_bright_background = "--lightning-blend-bright" in OS.get_cmdline_user_args()
	_viewport = SubViewport.new()
	_viewport.size = Vector2i(SIZE, SIZE)
	_viewport.own_world_3d = true
	_viewport.transparent_bg = false
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var environment := WorldEnvironment.new()
	var settings := Environment.new()
	settings.background_mode = Environment.BG_COLOR
	settings.background_color = Color(0.35, 0.35, 0.35, 1.0) if _bright_background else Color.BLACK
	settings.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	settings.glow_enabled = false
	environment.environment = settings
	_viewport.add_child(environment)
	var camera := Camera3D.new()
	camera.position = Vector3(0, 0, 1)
	camera.current = true
	_viewport.add_child(camera)
	_quad = MeshInstance3D.new()
	var mesh := QuadMesh.new()
	mesh.size = Vector2(2, 2)
	_quad.mesh = mesh
	_default_mesh = mesh
	_viewport.add_child(_quad)
	for shader_path in ["res://materials/ring_lightning_bolt.gdshader", "res://materials/ring_cloud_layer.gdshader", "res://materials/ring_far_cloud_clipmap.gdshader"]:
		var values := await _measure_shader(shader_path)
		if values.clear.full <= 0.0001 or (not _bright_background and values.clear.base > 0.001) or values.body.full <= 0.0001 or absf(values.clear.half / values.clear.full - 0.5) > 0.05 or absf(values.clear.quarter / values.clear.full - 0.25) > 0.05 or absf(values.body.half / values.body.full - 0.5) > 0.05 or absf(values.body.quarter / values.body.full - 0.25) > 0.05:
			return _fail("ownership ratio mismatch shader=%s values=%s" % [shader_path, values])
		if not bool(values.get("phase_ok", true)):
			return _fail("pixel-phase continuity mismatch shader=%s values=%s" % [shader_path, values])
		print("LIGHTNING_BLEND_RATIO shader=%s bright_background=%s values=%s" % [shader_path, _bright_background, values])
	print("LIGHTNING_BLEND_VISUAL_TEST=PASS")
	quit(0)

func _measure_shader(path: String) -> Dictionary:
	var material := ShaderMaterial.new()
	material.shader = load(path) as Shader
	_quad.material_override = material
	if path.ends_with("ring_lightning_bolt.gdshader"):
		material.set_shader_parameter("bolt_color", Color(0.2, 0.2, 0.2, 1.0))
		material.set_shader_parameter("intensity", 0.5)
		material.set_shader_parameter("emission_energy", 0.0)
	else:
		var image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
		image.fill(Color(0, 0, 0, 0))
		var atlas := ImageTexture.create_from_image(image)
		material.set_shader_parameter("weather_atlas", atlas)
		material.set_shader_parameter("weather_atlas_enabled", true)
		material.set_shader_parameter("ring_width_km", 20.0)
		material.set_shader_parameter("ring_circumference_km", 100.0)
		# Keep the sampled pixel well inside the event core and several RGBA8 steps
		# above black, while remaining far below display saturation.
		material.set_shader_parameter("far_lightning_event_a", Vector4(0.5, 0.5, 0.8, 0.4))
		material.set_shader_parameter("far_lightning_event_b", Vector4.ZERO)
		material.set_shader_parameter("opacity", 0.2)
		material.set_shader_parameter("coverage", 0.99)
		material.set_shader_parameter("storm_factor", 0.0)
		material.set_shader_parameter("patch_radius_meters", 180000.0)
		material.set_shader_parameter("patch_across_meters", 20000.0)
		# Isolate blending from the camera-local geometry fade: the body case
		# must contain cloud opacity, not a nearly transparent annulus centre.
		material.set_shader_parameter("handoff_start_meters", -24000.0)
		material.set_shader_parameter("canonical_origin_km", Vector2(50.0, 10.0))
		material.set_shader_parameter("camera_world_position", Vector3(0, 0, 1))
		material.set_shader_parameter("ring_center", Vector3.ZERO)
		material.set_shader_parameter("far_space_enabled", false)
	for settle in range(4): await RenderingServer.frame_post_draw
	var clear := await _measure_weights(material)
	if not path.ends_with("ring_lightning_bolt.gdshader"):
		var body_image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
		body_image.fill(Color(1, 0, 0, 1))
		material.set_shader_parameter("weather_atlas", ImageTexture.create_from_image(body_image))
	var body := await _measure_weights(material)
	var phase_ok := true
	var phase_values := []
	if "--lightning-blend-phase" in OS.get_cmdline_user_args() and not path.ends_with("ring_lightning_bolt.gdshader"):
		var phase_result: Dictionary = await _verify_far_pixel_phase(material, path)
		phase_ok = bool(phase_result.get("ok", false))
		phase_values = phase_result.get("samples", [])
	return {"clear": clear, "body": body, "phase_ok": phase_ok, "phase": phase_values}

func _measure_weights(material: ShaderMaterial) -> Dictionary:
	var zero := await _sample(material, 0.0)
	var full := await _sample(material, 1.0) - zero
	var half := await _sample(material, 0.5) - zero
	var quarter := await _sample(material, 0.25) - zero
	return {"full": full, "half": half, "quarter": quarter, "base": zero}

func _sample(material: ShaderMaterial, weight: float) -> float:
	if material.shader.resource_path.ends_with("ring_lightning_bolt.gdshader"):
		material.set_shader_parameter("ownership_weight", weight)
	else:
		material.set_shader_parameter("far_lightning_event_weights", Vector2(weight, 0.0))
	await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty(): return -1.0
	var color := image.get_pixel(image.get_width() / 2, image.get_height() / 2).srgb_to_linear()
	print("LIGHTNING_BLEND_IMAGE format=%s" % image.get_format())
	return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722

## Exercises the production wrapped-distance function on both sides of its seam.
## Half- and quarter-pixel offsets catch a sub-pixel core that disappears merely
## because the source crosses U=0/U=1 or falls between pixel centres.
func _verify_far_pixel_phase(material: ShaderMaterial, path: String) -> Dictionary:
	var samples: Array[float] = []
	# Keep the physical U=0/1 seam at the centre of the quad. The shader sees
	# raw UVs across 0.98..1.02, while the event source moves by half pixels.
	_quad.mesh = _make_seam_mesh()
	var phases := PackedFloat32Array([-0.5, -0.25, 0.0, 0.25, 0.5])
	for resolution in [64, 128]:
		_viewport.size = Vector2i(resolution, resolution)
		for settle in range(3): await RenderingServer.frame_post_draw
		var clear_image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
		clear_image.fill(Color(0, 0, 0, 0))
		material.set_shader_parameter("weather_atlas", ImageTexture.create_from_image(clear_image))
		material.set_shader_parameter("patch_radius_meters", 50000.0)
		material.set_shader_parameter("patch_across_meters", 20000.0)
		material.set_shader_parameter("canonical_origin_km", Vector2(50.0, 10.0))
		material.set_shader_parameter("far_lightning_event_weights", Vector2.ZERO)
		await RenderingServer.frame_post_draw
		var off_image := _viewport.get_texture().get_image()
		for phase in phases:
			var source_u: float = wrapf(1.0 + phase * 0.04 / float(resolution), 0.0, 1.0)
			material.set_shader_parameter("far_lightning_event_a", Vector4(source_u, 0.5, 0.001, 0.4))
			material.set_shader_parameter("far_lightning_event_b", Vector4.ZERO)
			material.set_shader_parameter("far_lightning_event_weights", Vector2.ONE)
			await RenderingServer.frame_post_draw
			var image := _viewport.get_texture().get_image()
			if off_image == null or off_image.is_empty() or image == null or image.is_empty() or image.get_size() != Vector2i(resolution, resolution) or off_image.get_size() != image.get_size():
				_viewport.size = Vector2i(SIZE, SIZE)
				_quad.mesh = _default_mesh
				return {"ok": false, "samples": samples}
			var result: Dictionary = _positive_flash_metrics(off_image, image)
			samples.append(float(result.get("peak", 0.0)))
			if int(result.get("pixels", 0)) < 4:
				_viewport.size = Vector2i(SIZE, SIZE)
				_quad.mesh = _default_mesh
				return {"ok": false, "samples": samples}
	_viewport.size = Vector2i(SIZE, SIZE)
	_quad.mesh = _default_mesh
	for settle in range(3): await RenderingServer.frame_post_draw
	var minimum := INF
	var maximum := 0.0
	for value in samples:
		minimum = minf(minimum, value)
		maximum = maxf(maximum, value)
	# The end samples straddle the same physical seam by half a pixel.
	var seam_ratio := minf(samples[0], samples[4]) / maxf(maxf(samples[0], samples[4]), 0.000001)
	var ok := minimum > 0.00005 and seam_ratio >= 0.65 and maximum / minimum <= 2.25
	print("LIGHTNING_BLEND_PIXEL_PHASE shader=%s samples=%s seam_ratio=%.4f" % [path, samples, seam_ratio])
	return {"ok": ok, "samples": samples}

func _make_seam_mesh() -> ArrayMesh:
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = PackedVector3Array([
		Vector3(-1, -1, 0), Vector3(1, -1, 0), Vector3(1, 1, 0), Vector3(-1, 1, 0)
	])
	arrays[ArrayMesh.ARRAY_TEX_UV] = PackedVector2Array([
		Vector2(0.98, 0.4), Vector2(1.02, 0.4), Vector2(1.02, 0.6), Vector2(0.98, 0.6)
	])
	arrays[ArrayMesh.ARRAY_INDEX] = PackedInt32Array([0, 1, 2, 0, 2, 3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh

func _positive_flash_metrics(off_image: Image, on_image: Image) -> Dictionary:
	var positive_pixels := 0
	var peak := 0.0
	for y in range(on_image.get_height()):
		for x in range(on_image.get_width()):
			var off := off_image.get_pixel(x, y).srgb_to_linear()
			var on := on_image.get_pixel(x, y).srgb_to_linear()
			var delta := (on.r - off.r) * 0.2126 + (on.g - off.g) * 0.7152 + (on.b - off.b) * 0.0722
			if delta > 0.00005:
				positive_pixels += 1
				peak = maxf(peak, delta)
	return {"pixels": positive_pixels, "peak": peak}

func _fail(message: String) -> void:
	push_error("LIGHTNING_BLEND_VISUAL_TEST=FAIL " + message)
	quit(1)
