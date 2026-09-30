extends SceneTree

## Diagnostic for the color-space boundary between the spatial atmosphere
## shaders (`source_color`) and Sunshine's raw compute profile. The bridge
## uploads Vector3 values, so this deliberately does the same rather than
## assigning a Color parameter.
const SIZE := Vector2i(64, 64)
const AUTHORED := Color(0.34, 0.46, 0.60, 1.0)
const TOLERANCE := 0.025

var _failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(30.0).timeout.connect(func() -> void: _fail("30-second watchdog"))
	var observed := await _capture_vector3_uniform()
	if _failed:
		return
	var raw := Vector3(AUTHORED.r, AUTHORED.g, AUTHORED.b)
	var source_linear_color := AUTHORED.srgb_to_linear()
	var source_linear := Vector3(source_linear_color.r, source_linear_color.g, source_linear_color.b)
	var raw_error := observed.distance_to(raw)
	var source_error := observed.distance_to(source_linear)
	if minf(raw_error, source_error) > TOLERANCE:
		_fail("observed output matches neither raw nor source-color expectation: %s" % observed)
		return
	var interpretation := "raw" if raw_error < source_error else "source_color_linearized"
	print("CLOUD_HAZE_COLOR_SPACE=PASS observed=", observed,
		" raw=", raw, " source_linear=", source_linear,
		" raw_error=", raw_error, " source_error=", source_error,
		" interpretation=", interpretation)
	quit(0)

func _capture_vector3_uniform() -> Vector3:
	var viewport := SubViewport.new()
	viewport.size = SIZE
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.BLACK
	environment.ambient_light_energy = 0.0
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world := WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	var camera := Camera3D.new()
	camera.current = true
	camera.position = Vector3(0.0, 0.0, 2.0)
	viewport.add_child(camera)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	var shader := Shader.new()
	shader.code = """shader_type spatial;
render_mode unshaded, cull_disabled;
uniform vec3 haze_color : source_color = vec3(0.34, 0.46, 0.60);
void fragment() { ALBEDO = haze_color; }
"""
	var material := ShaderMaterial.new()
	material.shader = shader
	# Matches RingSunshineCloudBridge's Vector3 profile upload convention.
	material.set_shader_parameter("haze_color", Vector3(AUTHORED.r, AUTHORED.g, AUTHORED.b))
	var mesh := MeshInstance3D.new()
	var quad := QuadMesh.new()
	quad.size = Vector2(4.0, 4.0)
	mesh.mesh = quad
	mesh.material_override = material
	viewport.add_child(mesh)
	for frame in 4:
		await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	viewport.queue_free()
	if image == null or image.is_empty():
		_fail("empty viewport image")
		return Vector3.ZERO
	var encoded := image.get_pixel(SIZE.x / 2, SIZE.y / 2)
	var linear := encoded.srgb_to_linear()
	return Vector3(linear.r, linear.g, linear.b)

func _fail(message: String) -> void:
	if _failed:
		return
	_failed = true
	push_error("CLOUD_HAZE_COLOR_SPACE=FAIL " + message)
	quit(1)
