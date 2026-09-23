extends SceneTree

## Static GPU regression for the production near-ocean material.  This creates
## no saved world and only writes disposable captures below .godot.
const OUTPUT: String = "res://.godot/water_depth_visual"
const WATER_SHADER: String = "res://materials/ring_ocean_proxy.gdshader"
const SIZE: Vector2i = Vector2i(640, 360)
const OLD_DEPTH_HELPER: String = """
vec2 ring_water_depth_from_scene(vec4 scene_h, float raw_scene_depth, float surface_depth) {
	float scene_depth = -scene_h.z / max(0.00001, scene_h.w);
	float thickness = clamp(scene_depth - surface_depth, 0.0, 5000.0);
	return vec2(thickness, 1.0);
}
"""

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; this is not valid on Compatibility/headless")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	var cases: Array[Dictionary] = [
		{"name": "deep_60km", "distance": 60000.0, "bed": 1000.0},
		{"name": "deep_110km", "distance": 110000.0, "bed": 1000.0},
		{"name": "deep_150km", "distance": 150000.0, "bed": 1000.0},
		{"name": "sky_150km", "distance": 150000.0, "bed": -1.0},
		{"name": "shore_20m", "distance": 20.0, "bed": 0.2},
	]
	for fixture: Dictionary in cases:
		var on_a: Image = await _render(fixture, true, false, "%s_on_a" % fixture.name)
		var on_b: Image = await _render(fixture, true, false, "%s_on_b" % fixture.name)
		var off: Image = await _render(fixture, false, false, "%s_off" % fixture.name)
		if not _valid(on_a) or not _valid(on_b) or not _valid(off):
			_fail("empty/non-finite capture: %s" % fixture.name)
			return
		var baseline: float = _roi_max_luma(on_a, on_b)
		var foam_delta: float = _roi_max_luma(on_a, off)
		print("WATER_DEPTH_CASE name=%s baseline=%.6f foam_delta=%.6f" % [fixture.name, baseline, foam_delta])
		if baseline > 0.004:
			_fail("ON/ON foam control drifted: %s %.6f" % [fixture.name, baseline])
			return
		var shallow: bool = fixture.name == "shore_20m"
		if (shallow and foam_delta < 0.01) or (not shallow and foam_delta > 0.004):
			_fail("foam expectation failed: %s delta=%.6f" % [fixture.name, foam_delta])
			return
	# The legacy source is intentionally in memory: it has the old reciprocal-w
	# clamp and old unconditional shore-contact semantics, never touches production.
	var old_fixture: Dictionary = {"name": "old_150km", "distance": 150000.0, "bed": 1000.0}
	var legacy_on: Image = await _render(old_fixture, true, true, "old_150km_on")
	var legacy_off: Image = await _render(old_fixture, false, true, "old_150km_off")
	var current_on: Image = await _render(old_fixture, true, false, "current_150km_on")
	if not _valid(legacy_on) or not _valid(legacy_off) or not _valid(current_on):
		_fail("legacy comparison capture invalid")
		return
	var legacy_delta: float = _roi_max_luma(legacy_on, legacy_off)
	var old_white: float = _roi_luma(legacy_on)
	var current_white: float = _roi_luma(current_on)
	print("WATER_DEPTH_LEGACY old_foam_delta=%.6f old_luma=%.6f current_luma=%.6f" % [legacy_delta, old_white, current_white])
	if legacy_delta < 0.01 or old_white <= current_white + 0.01:
		_fail("legacy >100km reference did not whiten relative to current")
		return
	print("WATER_DEPTH_VISUAL_TEST=PASS output=%s" % OUTPUT)
	quit(0)

func _render(fixture: Dictionary, foam_enabled: bool, old_depth: bool, label: String) -> Image:
	var viewport: SubViewport = SubViewport.new()
	viewport.size = SIZE
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	viewport.transparent_bg = false
	viewport.own_world_3d = true
	root.add_child(viewport)
	var environment: Environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.08, 0.13, 0.20)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.WHITE
	environment.ambient_light_energy = 0.7
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.glow_enabled = false
	var world_environment: WorldEnvironment = WorldEnvironment.new()
	world_environment.environment = environment
	viewport.add_child(world_environment)
	var camera: Camera3D = Camera3D.new()
	camera.fov = 80.0
	camera.near = 0.1
	camera.far = 1200000.0
	viewport.add_child(camera)
	var light: DirectionalLight3D = DirectionalLight3D.new()
	light.light_energy = 0.8
	light.rotation_degrees = Vector3.ZERO
	viewport.add_child(light)
	var distance: float = float(fixture.distance)
	var water: MeshInstance3D = MeshInstance3D.new()
	water.mesh = _quad(maxf(1000.0, distance * 3.0), Color(0.15, 0.45, 0.68, 0.0))
	water.position.z = -distance
	water.material_override = _water_material(foam_enabled, old_depth)
	viewport.add_child(water)
	var bed_depth: float = float(fixture.bed)
	if bed_depth >= 0.0:
		var bed: MeshInstance3D = MeshInstance3D.new()
		bed.mesh = _quad(maxf(1000.0, distance * 3.0), Color(0.50, 0.21, 0.07, 1.0))
		bed.position.z = -(distance + bed_depth)
		var bed_material: StandardMaterial3D = StandardMaterial3D.new()
		bed_material.albedo_color = Color(0.50, 0.21, 0.07)
		bed_material.roughness = 1.0
		bed.material_override = bed_material
		viewport.add_child(bed)
	for frame: int in range(12): await process_frame
	await RenderingServer.frame_post_draw
	var image: Image = viewport.get_texture().get_image()
	if not image.is_empty(): image.save_png("%s/%s.png" % [OUTPUT, label])
	viewport.queue_free()
	await process_frame
	return image

func _quad(side: float, vertex_color: Color) -> ArrayMesh:
	# QuadMesh has no vertex-color channel.  The production shader deliberately
	# stores water kind in COLOR.a, so make a small explicit mesh (alpha 0 = the
	# ordinary open-water profile) instead of accidentally exercising kind 3.
	var surface: SurfaceTool = SurfaceTool.new()
	surface.begin(Mesh.PRIMITIVE_TRIANGLES)
	var half: float = side * 0.5
	var positions: Array[Vector3] = [
		Vector3(-half, -half, 0.0), Vector3(half, half, 0.0), Vector3(half, -half, 0.0),
		Vector3(-half, -half, 0.0), Vector3(-half, half, 0.0), Vector3(half, half, 0.0),
	]
	var uvs: Array[Vector2] = [
		Vector2(0.0, 0.0), Vector2(1.0, 1.0), Vector2(1.0, 0.0),
		Vector2(0.0, 0.0), Vector2(0.0, 1.0), Vector2(1.0, 1.0),
	]
	for index: int in positions.size():
		surface.set_normal(Vector3(0.0, 0.0, 1.0))
		surface.set_color(vertex_color)
		surface.set_uv(uvs[index])
		surface.add_vertex(positions[index])
	return surface.commit()

func _water_material(foam_enabled: bool, old_depth: bool) -> ShaderMaterial:
	var shader: Shader
	if old_depth:
		var source: String = FileAccess.get_file_as_string(WATER_SHADER)
		source = source.replace("#include \"res://materials/ring_water_depth.gdshaderinc\"", OLD_DEPTH_HELPER)
		shader = Shader.new()
		shader.code = source
	else:
		shader = load(WATER_SHADER) as Shader
	var material: ShaderMaterial = ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("camera_world_position", Vector3.ZERO)
	material.set_shader_parameter("ring_center", Vector3(0.0, 0.0, 5000000.0))
	material.set_shader_parameter("ring_radius_meters", 5000000.0)
	material.set_shader_parameter("ring_width_meters", 20000.0)
	material.set_shader_parameter("sun_direction", Vector3(0.0, 0.0, 1.0))
	material.set_shader_parameter("far_fade_enabled", false)
	material.set_shader_parameter("geomorph_enabled", false)
	material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	material.set_shader_parameter("ring_atmosphere_enabled", false)
	material.set_shader_parameter("wave_geometry_strength", 0.0)
	material.set_shader_parameter("wave_normal_strength", 0.0)
	material.set_shader_parameter("wave_normal_fade_start_meters", 0.0)
	material.set_shader_parameter("wave_normal_fade_end_meters", 1.0)
	material.set_shader_parameter("shore_foam_strength", 0.82 if foam_enabled else 0.0)
	material.set_shader_parameter("breaking_foam_strength", 0.0)
	material.set_shader_parameter("refraction_strength", 0.0)
	return material

func _valid(image: Image) -> bool:
	if image.is_empty() or image.get_width() < 2 or image.get_height() < 2: return false
	var non_empty: int = 0
	for y: int in range(0, image.get_height(), 8):
		for x: int in range(0, image.get_width(), 8):
			var color: Color = image.get_pixel(x, y)
			if is_nan(color.r) or is_nan(color.g) or is_nan(color.b) or is_inf(color.r) or is_inf(color.g) or is_inf(color.b): return false
			if color.get_luminance() > 0.001: non_empty += 1
	return non_empty > 10

func _roi_max_luma(a: Image, b: Image) -> float:
	var maximum: float = 0.0
	var cx: int = a.get_width() / 2
	var cy: int = a.get_height() / 2
	for y: int in range(cy - 48, cy + 48):
		for x: int in range(cx - 48, cx + 48):
			maximum = maxf(maximum, absf(a.get_pixel(x, y).get_luminance() - b.get_pixel(x, y).get_luminance()))
	return maximum

func _roi_luma(image: Image) -> float:
	var sum: float = 0.0
	var samples: int = 0
	var cx: int = image.get_width() / 2
	var cy: int = image.get_height() / 2
	for y: int in range(cy - 32, cy + 32):
		for x: int in range(cx - 32, cx + 32):
			sum += image.get_pixel(x, y).get_luminance()
			samples += 1
	return sum / float(maxi(1, samples))

func _fail(message: String) -> void:
	push_error("WATER_DEPTH_VISUAL_TEST=FAIL %s" % message)
	quit(1)
