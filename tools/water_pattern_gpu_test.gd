extends SceneTree

## Rendered regression and review fixture for the production water materials.
## It deliberately checks only objective contracts (finite pixels, deterministic
## rebasing, and reduced screen-frequency energy).  The PNGs remain the human
## gate for whether the distant pattern is actually attractive.
const OUTPUT := "res://.godot/water_pattern_gpu"
const SIZE := Vector2i(960, 540)
const PROXY_SHADER := "res://materials/ring_ocean_proxy.gdshader"
const GLOBAL_SHADER := "res://materials/ring_ocean_global.gdshader"
const RING_RADIUS := 5000000.0
var _frozen_shaders: Dictionary = {}

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create capture directory")
		return
	# The near capture makes the off/on material change reviewable.  The far
	# capture puts the same material under a deliberately large footprint, where
	# screen filtering must retire detail rather than alias it.
	var near_off := await _render(PROXY_SHADER, 100.0, _controls(0.0, 0.0, 0.0, 1.0), "proxy_100m_off")
	var near_on := await _render(PROXY_SHADER, 100.0, _controls(0.65, 0.65, 0.20, 1.0), "proxy_100m_on")
	var far_unfiltered := await _render(PROXY_SHADER, 1000.0, _controls(0.65, 0.65, 0.20, 0.0), "proxy_1000m_unfiltered")
	var far_filtered := await _render(PROXY_SHADER, 1000.0, _controls(0.65, 0.65, 0.20, 1.0), "proxy_1000m_filtered")
	# The global material is the real far LOD path.  It has no foam path by
	# design, but it must accept the shared swell controls and remain finite.
	var global_off := await _render(GLOBAL_SHADER, 1000.0, _controls(0.0, 0.0, 0.0, 1.0), "global_1000m_off")
	var global_on := await _render(GLOBAL_SHADER, 1000.0, _controls(0.65, 0.65, 0.20, 1.0), "global_1000m_on")
	var global_6000 := await _render(GLOBAL_SHADER, 6000.0, _controls(0.65, 0.65, 0.20, 1.0), "global_6000m_on")
	# Translating the complete ring/camera fixture must not change the result.
	# This catches camera-anchored noise and local-coordinate swimming without
	# claiming a subjective quality metric.
	var rebased := await _render(PROXY_SHADER, 100.0, _controls(0.65, 0.65, 0.20, 1.0), "proxy_100m_rebased", Vector3(17321.0, -811.0, 4013.0))
	var helper_errors := await _helper_contract_errors()
	for image: Image in [near_off, near_on, far_unfiltered, far_filtered, global_off, global_on, global_6000, rebased]:
		if not _valid(image):
			_fail("empty or non-finite production material capture")
			return
	var near_delta := _mean_luma_delta(near_off, near_on)
	var global_delta := _mean_luma_delta(global_off, global_on)
	var rebase_delta := _mean_luma_delta(near_on, rebased)
	var unfiltered_energy := _high_frequency_energy(far_unfiltered)
	var filtered_energy := _high_frequency_energy(far_filtered)
	var seam_error: float = helper_errors.seam
	var scaled_error: float = helper_errors.scaled
	print("WATER_PATTERN_GPU_METRICS near_delta=%.6f global_delta=%.6f rebase_delta=%.6f seam_error=%.6f scaled_error=%.6f far_energy_unfiltered=%.6f far_energy_filtered=%.6f" % [near_delta, global_delta, rebase_delta, seam_error, scaled_error, unfiltered_energy, filtered_energy])
	if near_delta < 0.0005:
		_fail("shared controls made no measurable near proxy change")
		return
	if global_delta < 0.0002:
		_fail("swell_variation made no measurable global-water change")
		return
	if rebase_delta > 0.004:
		_fail("world-anchored proxy pattern changed after equivalent ring rebase: %.6f" % rebase_delta)
		return
	if seam_error > 0.003 or scaled_error > 0.003:
		_fail("production patch helper broke seam/scaled coordinate equivalence seam=%.6f scaled=%.6f" % [seam_error, scaled_error])
		return
	if filtered_energy >= unfiltered_energy * 0.995:
		_fail("detail filtering did not attenuate far screen-frequency energy")
		return
	print("WATER_PATTERN_GPU_TEST=PASS output=%s assertions=finite,controls,rebase,seam,scaled,filter_attenuation human_review=OPEN" % OUTPUT)
	quit(0)

func _controls(swell: float, patchiness: float, distant_foam: float, filtering: float) -> Dictionary:
	return {"swell_variation": swell, "foam_patchiness": patchiness, "distant_foam_visibility": distant_foam, "detail_filtering_strength": filtering}

func _render(shader_path: String, distance_meters: float, controls: Dictionary, label: String, translation := Vector3.ZERO) -> Image:
	var viewport := SubViewport.new()
	viewport.size = SIZE
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	viewport.transparent_bg = false
	viewport.own_world_3d = true
	root.add_child(viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.055, 0.09, 0.14)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.WHITE
	environment.ambient_light_energy = 0.65
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	viewport.add_child(world_environment)
	var camera := Camera3D.new()
	camera.position = translation
	camera.fov = 70.0
	camera.near = 0.1
	camera.far = 20000.0
	viewport.add_child(camera)
	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-25.0, 35.0, 0.0)
	light.light_energy = 1.1
	viewport.add_child(light)
	var water := MeshInstance3D.new()
	water.mesh = _quad(maxf(1200.0, distance_meters * 2.6))
	water.position = translation + Vector3(0.0, 0.0, -distance_meters)
	water.material_override = _material(shader_path, distance_meters, controls, translation)
	viewport.add_child(water)
	# A fully opaque bed gives the transparent near material stable depth and
	# copied-scene inputs.  It is deliberately far enough away to suppress shore foam.
	if shader_path == PROXY_SHADER:
		var bed := MeshInstance3D.new()
		bed.mesh = _quad(maxf(1200.0, distance_meters * 2.6))
		bed.position = water.position + Vector3(0.0, 0.0, -350.0)
		var bed_material := StandardMaterial3D.new()
		bed_material.albedo_color = Color(0.08, 0.045, 0.025)
		bed_material.roughness = 1.0
		bed.material_override = bed_material
		viewport.add_child(bed)
	for frame: int in range(16): await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if not image.is_empty(): image.save_png("%s/%s.png" % [OUTPUT, label])
	viewport.queue_free()
	await process_frame
	return image

func _material(shader_path: String, distance_meters: float, controls: Dictionary, translation: Vector3) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = _frozen_shader(shader_path)
	var ring_center := translation + Vector3(0.0, 0.0, RING_RADIUS - distance_meters)
	material.set_shader_parameter("camera_world_position", translation)
	material.set_shader_parameter("ring_center", ring_center)
	material.set_shader_parameter("ring_radius_meters", RING_RADIUS)
	material.set_shader_parameter("ring_width_meters", 20000.0)
	material.set_shader_parameter("sun_direction", Vector3(0.0, 0.0, -1.0))
	material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	material.set_shader_parameter("ring_atmosphere_enabled", false)
	material.set_shader_parameter("far_fade_enabled", false)
	material.set_shader_parameter("fade_start_meters", 0.0)
	material.set_shader_parameter("fade_end_meters", 0.01)
	material.set_shader_parameter("wave_geometry_strength", 0.0)
	material.set_shader_parameter("wave_height_meters", 0.9)
	material.set_shader_parameter("wave_length_meters", 8.0)
	material.set_shader_parameter("wave_detail_strength", 1.0)
	material.set_shader_parameter("wave_normal_strength", 1.0)
	material.set_shader_parameter("wave_normal_fade_start_meters", 0.0)
	material.set_shader_parameter("wave_normal_fade_end_meters", 20000.0)
	material.set_shader_parameter("shore_foam_strength", 0.0)
	material.set_shader_parameter("breaking_foam_strength", 1.0)
	material.set_shader_parameter("refraction_strength", 0.0)
	for key: String in controls: material.set_shader_parameter(key, controls[key])
	return material

func _frozen_shader(path: String) -> Shader:
	if _frozen_shaders.has(path): return _frozen_shaders[path]
	var shader := Shader.new()
	shader.code = _expand_includes(FileAccess.get_file_as_string(path)).replace("TIME", "13.75")
	_frozen_shaders[path] = shader
	return shader

func _expand_includes(source: String) -> String:
	var expanded := ""
	for line: String in source.split("\n"):
		if line.begins_with("#include \""):
			var start := line.find("\"") + 1
			var stop := line.find("\"", start)
			if stop > start:
				expanded += _expand_includes(FileAccess.get_file_as_string(line.substr(start, stop - start))) + "\n"
				continue
		expanded += line + "\n"
	return expanded

func _helper_contract_errors() -> Dictionary:
	var maximum := {"seam": 0.0, "scaled": 0.0}
	for coordinates: Vector2 in [Vector2(12345.25, -738.5), Vector2(-172.75, 4488.125), Vector2(31999321.0, -913.75)]:
		for component: int in [0, 1, 2]:
			var image := await _render_helper_contract(coordinates, component)
			if not _valid(image):
				return {"seam": INF, "scaled": INF}
			var color := image.get_pixel(image.get_width() / 2, image.get_height() / 2)
			maximum.seam = maxf(maximum.seam, absf(color.r - color.g))
			var scaled := await _render_helper_contract(coordinates * 0.001, component, 0.001)
			if not _valid(scaled): return {"seam": INF, "scaled": INF}
			maximum.scaled = maxf(maximum.scaled, absf(color.r - scaled.get_pixel(scaled.get_width() / 2, scaled.get_height() / 2).r))
	return maximum

func _render_helper_contract(coordinates: Vector2, component: int, coordinate_scale := 1.0) -> Image:
	# This tiny material calls the production helper directly on GPU.  The three
	# channels are respectively p, p + circumference, and p.  A second material
	# invocation supplies the scaled-far coordinate-scale uniform, avoiding a
	# fragile CPU duplicate of the helper's physical-unit conversion.
	var shader := Shader.new()
	shader.code = """
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform vec2 test_coordinates = vec2(12345.25, -738.5);
uniform float test_radius = 5000000.0;
uniform float ring_radius_meters = 5000000.0;
uniform int component = 0;
#include \"res://materials/ring_water_common.gdshaderinc\"
void fragment() {
	vec2 a_gradient = ring_water_far_wave_gradient(test_coordinates, 13.75, 1.0);
	vec2 b_gradient = ring_water_far_wave_gradient(test_coordinates + vec2(6.28318530718 * test_radius, 0.0), 13.75, 1.0);
	float a = component == 0 ? ring_water_patch_value(test_coordinates, test_radius, 430.0) : (component == 1 ? a_gradient.x : a_gradient.y);
	float b = component == 0 ? ring_water_patch_value(test_coordinates + vec2(6.28318530718 * test_radius, 0.0), test_radius, 430.0) : (component == 1 ? b_gradient.x : b_gradient.y);
	float c = a;
	ALBEDO = vec3(a, b, c) * 0.5 + vec3(0.5);
}
"""
	var viewport := SubViewport.new()
	viewport.size = Vector2i(32, 32)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	viewport.transparent_bg = false
	viewport.own_world_3d = true
	root.add_child(viewport)
	var camera := Camera3D.new()
	viewport.add_child(camera)
	var quad := MeshInstance3D.new()
	quad.mesh = QuadMesh.new()
	var material := ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("test_coordinates", coordinates)
	material.set_shader_parameter("component", component)
	material.set_shader_parameter("water_coordinate_scale", coordinate_scale)
	material.set_shader_parameter("test_radius", RING_RADIUS * coordinate_scale)
	material.set_shader_parameter("ring_radius_meters", RING_RADIUS * coordinate_scale)
	# Gradient is dimensionless slope: scale height and wavelength together so
	# the scaled-far evaluation represents the same physical surface.
	material.set_shader_parameter("wave_height_meters", 0.32 * coordinate_scale)
	material.set_shader_parameter("wave_length_meters", 28.0 * coordinate_scale)
	quad.material_override = material
	quad.position.z = -1.0
	viewport.add_child(quad)
	for frame: int in range(4): await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if not image.is_empty(): image.save_png("%s/helper_%d_%d_%d.png" % [OUTPUT, component, int(absf(coordinates.x)), int(coordinate_scale * 1000.0)])
	viewport.queue_free()
	await process_frame
	return image

func _quad(side: float) -> ArrayMesh:
	var surface := SurfaceTool.new()
	surface.begin(Mesh.PRIMITIVE_TRIANGLES)
	var half := side * 0.5
	var positions: Array[Vector3] = [Vector3(-half, -half, 0.0), Vector3(half, half, 0.0), Vector3(half, -half, 0.0), Vector3(-half, -half, 0.0), Vector3(-half, half, 0.0), Vector3(half, half, 0.0)]
	var uvs: Array[Vector2] = [Vector2(0, 0), Vector2(1, 1), Vector2(1, 0), Vector2(0, 0), Vector2(0, 1), Vector2(1, 1)]
	for index: int in positions.size():
		surface.set_normal(Vector3(0.0, 0.0, 1.0))
		surface.set_color(Color(0.10, 0.42, 0.68, 0.0)) # alpha 0 encodes ocean.
		surface.set_uv(uvs[index])
		surface.add_vertex(positions[index])
	return surface.commit()

func _valid(image: Image) -> bool:
	if image.is_empty() or image.get_width() < 8: return false
	var lit := 0
	for y: int in range(0, image.get_height(), 8):
		for x: int in range(0, image.get_width(), 8):
			var color := image.get_pixel(x, y)
			if is_nan(color.r) or is_nan(color.g) or is_nan(color.b) or is_inf(color.r) or is_inf(color.g) or is_inf(color.b): return false
			if color.get_luminance() > 0.001: lit += 1
	return lit > (2 if image.get_width() <= 32 else 20)

func _mean_luma_delta(a: Image, b: Image) -> float:
	var total := 0.0
	var count := 0
	for y: int in range(32, a.get_height() - 32, 4):
		for x: int in range(32, a.get_width() - 32, 4):
			total += absf(a.get_pixel(x, y).get_luminance() - b.get_pixel(x, y).get_luminance())
			count += 1
	return total / maxf(1.0, count)

func _high_frequency_energy(image: Image) -> float:
	var total := 0.0
	var count := 0
	for y: int in range(34, image.get_height() - 34, 4):
		for x: int in range(34, image.get_width() - 34, 4):
			var c := image.get_pixel(x, y).get_luminance()
			var blur := (image.get_pixel(x - 2, y).get_luminance() + image.get_pixel(x + 2, y).get_luminance() + image.get_pixel(x, y - 2).get_luminance() + image.get_pixel(x, y + 2).get_luminance()) * 0.25
			total += absf(c - blur)
			count += 1
	return total / maxf(1.0, count)

func _fail(message: String) -> void:
	push_error("WATER_PATTERN_GPU_TEST=FAIL " + message)
	quit(1)
