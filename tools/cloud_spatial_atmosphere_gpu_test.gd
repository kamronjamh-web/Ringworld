extends SceneTree

# GPU contract for the production spatial cloud shaders. This creates a small
# isolated World3D and renders each shader with the same physical camera frame.
# Run from a normal Forward+ display session. Headless execution is only useful
# with --check-only to parse this fixture.
const WATCHDOG_SECONDS := 30.0
const VIEW_SIZE := Vector2i(96, 96)
const CAMERA_WORLD := Vector3(100.0, 0.0, 0.0)
const TARGET_WORLD_X := 90.0
const SHADERS := [
	"res://materials/ring_far_cloud_clipmap.gdshader",
	"res://materials/ring_cloud_layer.gdshader",
	"res://materials/ring_mesh_cloud.gdshader"
]

var _watchdog: SceneTreeTimer
var _failed := false
var _white_weather: ImageTexture

func _init() -> void:
	_watchdog = create_timer(WATCHDOG_SECONDS)
	_watchdog.timeout.connect(_watchdog_expired)
	call_deferred("_run")

func _watchdog_expired() -> void:
	if not _failed:
		_fail("30-second watchdog expired")

func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice unavailable. This fixture requires a GPU-capable display session")
		return
	_white_weather = _make_white_weather()
	for shader_path in SHADERS:
		var result := await _verify_shader(shader_path)
		if not result:
			return
	print("CLOUD_SPATIAL_ATMOSPHERE_GPU=PASS shaders=", SHADERS.size())
	quit(0)

func _verify_shader(shader_path: String) -> bool:
	var shader := load(shader_path) as Shader
	if shader == null:
		_fail("could not load " + shader_path)
		return false
	var haze_off := await _capture(shader, "off", 0.0)
	var strong_haze := await _capture(shader, "strong", 1.0)
	var zero_density := await _capture(shader, "zero_density", 0.0)
	if haze_off.is_empty() or strong_haze.is_empty() or zero_density.is_empty():
		_fail("empty GPU capture for " + shader_path)
		return false
	var off_rgb := _center_rgb(haze_off)
	var strong_rgb := _center_rgb(strong_haze)
	var zero_rgb := _center_rgb(zero_density)
	var strong_delta := _rgb_delta(off_rgb, strong_rgb)
	var zero_delta := _rgb_delta(off_rgb, zero_rgb)
	print("SPATIAL_HAZE_VALUES ", shader_path, " off=", off_rgb, " on=", strong_rgb,
		" alpha=", haze_off.get_pixel(VIEW_SIZE.x / 2, VIEW_SIZE.y / 2).a)
	if strong_delta <= 0.002:
		_fail("strong haze did not change center RGB for " + shader_path)
		return false
	if zero_delta >= 0.002:
		_fail("zero-density haze changed center RGB for " + shader_path)
		return false
	if shader_path != "res://materials/ring_mesh_cloud.gdshader":
		var clear_alpha := maxf(haze_off.get_pixel(0, 0).a, strong_haze.get_pixel(0, 0).a)
		if clear_alpha > 0.02:
			_fail("discard/clear corner gained alpha for " + shader_path)
			return false
	print("CLOUD_SPATIAL_ATMOSPHERE_GPU shader=", shader_path,
		" strong_rgb_delta=", strong_delta, " zero_density_delta=", zero_delta)
	return true

func _capture(shader: Shader, mode: String, density: float) -> Image:
	var viewport := SubViewport.new()
	viewport.size = VIEW_SIZE
	viewport.transparent_bg = true
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	viewport.add_child(camera)
	camera.global_position = CAMERA_WORLD
	camera.look_at(Vector3(TARGET_WORLD_X, 0.0, 0.0), Vector3.UP)
	camera.current = true
	var material := ShaderMaterial.new()
	material.shader = shader
	_configure_common(material, mode, density)
	_configure_shader(material, shader.resource_path)
	var cloud := MeshInstance3D.new()
	cloud.mesh = _make_cloud_quad()
	cloud.material_override = material
	cloud.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	viewport.add_child(cloud)
	for frame in 3:
		await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	viewport.queue_free()
	await process_frame
	return image

func _configure_common(material: ShaderMaterial, mode: String, density: float) -> void:
	var haze_enabled := mode != "off"
	var haze_strength := 0.95 if mode == "strong" else density
	material.set_shader_parameter("ring_atmosphere_enabled", haze_enabled)
	material.set_shader_parameter("ring_radius_meters", 100.0)
	material.set_shader_parameter("atmosphere_floor_depth_meters", 2.5)
	material.set_shader_parameter("atmosphere_boundary_height_meters", 2.5)
	material.set_shader_parameter("atmosphere_upper_height_meters", 20.0)
	material.set_shader_parameter("atmosphere_boundary_extinction_meters", 3.0)
	material.set_shader_parameter("atmosphere_upper_extinction_meters", 8.0)
	material.set_shader_parameter("atmosphere_boundary_max_opacity", 0.30 * haze_strength)
	material.set_shader_parameter("atmosphere_upper_max_opacity", 0.25 * haze_strength)
	material.set_shader_parameter("atmosphere_total_max_opacity", 0.90 * haze_strength)
	material.set_shader_parameter("atmosphere_clear_distance_meters", 0.0)
	material.set_shader_parameter("atmosphere_full_distance_meters", 1.0)
	material.set_shader_parameter("atmosphere_high_altitude_clear_distance_meters", 0.0)
	material.set_shader_parameter("atmosphere_high_altitude_full_distance_meters", 1.0)
	material.set_shader_parameter("atmosphere_distance_haze_max_opacity", 0.80 * haze_strength)
	material.set_shader_parameter("atmosphere_opacity_multiplier", 1.0)
	material.set_shader_parameter("atmosphere_scattering_energy", 1.0)
	material.set_shader_parameter("atmosphere_day_color", Color(0.20, 0.55, 0.95))
	material.set_shader_parameter("atmosphere_night_color", Color(0.02, 0.03, 0.06))
	material.set_shader_parameter("ring_atmosphere_to_sun", Vector3(1.0, 0.0, 0.0))
	material.set_shader_parameter("ring_atmosphere_ray_frame_valid", true)
	material.set_shader_parameter("ring_atmosphere_camera_world_position", CAMERA_WORLD)
	material.set_shader_parameter("ring_atmosphere_camera_height_meters", 0.0)
	material.set_shader_parameter("ring_atmosphere_outward_world", Vector3.RIGHT)
	material.set_shader_parameter("ring_atmosphere_axis_world", Vector3.UP)
	material.set_shader_parameter("ring_atmosphere_sidewall_negative_meters", 1000.0)
	material.set_shader_parameter("ring_atmosphere_sidewall_positive_meters", 1000.0)
	material.set_shader_parameter("ring_atmosphere_coordinate_scale", 1.0)
	material.set_shader_parameter("camera_world_position", CAMERA_WORLD)
	material.set_shader_parameter("ring_center", Vector3.ZERO)
	material.set_shader_parameter("sun_direction", Vector3(1.0, 0.0, 0.0))
	material.set_shader_parameter("surface_up", Vector3(-1.0, 0.0, 0.0))

func _configure_shader(material: ShaderMaterial, shader_path: String) -> void:
	if shader_path == "res://materials/ring_far_cloud_clipmap.gdshader":
		material.set_shader_parameter("weather_atlas", _white_weather)
		material.set_shader_parameter("weather_atlas_enabled", true)
		material.set_shader_parameter("patch_radius_meters", 20.0)
		material.set_shader_parameter("patch_across_meters", 20.0)
		# The production handoff uses a large positive radius. This isolated quad
		# starts at the camera ray origin, so finish the same fade before it.
		material.set_shader_parameter("handoff_start_meters", -24000.0)
		material.set_shader_parameter("ring_width_km", 318.0)
		material.set_shader_parameter("canonical_origin_km", Vector2(0.0, 159.0))
		material.set_shader_parameter("ring_circumference_km", 10.0)
		material.set_shader_parameter("opacity", 0.8)
		material.set_shader_parameter("weather_time_seconds", 0.0)
	elif shader_path == "res://materials/ring_cloud_layer.gdshader":
		material.set_shader_parameter("weather_atlas", _white_weather)
		material.set_shader_parameter("weather_atlas_enabled", true)
		material.set_shader_parameter("ring_width_km", 1.0)
		material.set_shader_parameter("ring_circumference_km", 10.0)
		material.set_shader_parameter("coverage", 0.2)
		material.set_shader_parameter("opacity", 0.8)
		material.set_shader_parameter("softness", 0.1)
		material.set_shader_parameter("far_space_enabled", false)
		material.set_shader_parameter("local_volumetric_handoff_enabled", false)
	else:
		material.set_shader_parameter("use_vertex_cloud_data", true)
		material.set_shader_parameter("world_space_union_mesh", true)
		material.set_shader_parameter("handoff_start_meters", 1000.0)
		material.set_shader_parameter("handoff_end_meters", 1001.0)
		material.set_shader_parameter("edge_firmness", 0.7)

func _make_cloud_quad() -> ArrayMesh:
	var surface := SurfaceTool.new()
	surface.begin(Mesh.PRIMITIVE_TRIANGLES)
	_add_vertex(surface, Vector3(TARGET_WORLD_X, -4.0, -4.0), Vector2(0.0, 0.0))
	_add_vertex(surface, Vector3(TARGET_WORLD_X, 4.0, -4.0), Vector2(1.0, 0.0))
	_add_vertex(surface, Vector3(TARGET_WORLD_X, 4.0, 4.0), Vector2(1.0, 1.0))
	_add_vertex(surface, Vector3(TARGET_WORLD_X, -4.0, -4.0), Vector2(0.0, 0.0))
	_add_vertex(surface, Vector3(TARGET_WORLD_X, 4.0, 4.0), Vector2(1.0, 1.0))
	_add_vertex(surface, Vector3(TARGET_WORLD_X, -4.0, 4.0), Vector2(0.0, 1.0))
	return surface.commit()

func _add_vertex(surface: SurfaceTool, vertex: Vector3, uv: Vector2) -> void:
	surface.set_normal(Vector3.RIGHT)
	surface.set_color(Color(1.0, 0.0, 0.35, 0.55))
	surface.set_uv(uv)
	surface.set_uv2(Vector2(0.55, 1200.0))
	surface.add_vertex(vertex)

func _make_white_weather() -> ImageTexture:
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.fill(Color.WHITE)
	return ImageTexture.create_from_image(image)

func _center_rgb(image: Image) -> Vector3:
	var total := Vector3.ZERO
	var samples := 0
	var center := Vector2i(image.get_width() / 2, image.get_height() / 2)
	for y in range(center.y - 2, center.y + 3):
		for x in range(center.x - 2, center.x + 3):
			var pixel := image.get_pixel(x, y)
			total += Vector3(pixel.r, pixel.g, pixel.b)
			samples += 1
	return total / float(samples)

func _rgb_delta(first: Vector3, second: Vector3) -> float:
	return absf(first.x - second.x) + absf(first.y - second.y) + absf(first.z - second.z)

func _fail(message: String) -> void:
	if _failed:
		return
	_failed = true
	push_error("CLOUD_SPATIAL_ATMOSPHERE_GPU=FAIL " + message)
	quit(1)
