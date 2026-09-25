extends SceneTree

## Forward+ GPU contract for the camera-relative regional-terrain macro-shadow
## path.  The mesh supplies canonical UVs directly, so the test exercises the
## shader's UV sampling rather than reconstructing a global ring coordinate.
const SIZE := 96
const LUMA_TOLERANCE := 0.025
const SURFACE_X := 1000.0

var _viewport: SubViewport
var _surface: MeshInstance3D
var _light: DirectionalLight3D
var _material: ShaderMaterial
var _mask: Texture2D


func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy/headless rendering is not GPU validation")
		return
	call_deferred("_run")


func _run() -> void:
	_make_viewport()
	_mask = _make_mask()
	_material = _make_material()

	# Row zero is opaque while the final row is clear.  A V of exactly 1.0
	# must clamp to the final row; repeat wrapping would incorrectly darken it.
	var disabled := await _measure(Vector2(0.01, 0.25), false, "disabled_control")
	var occluded := await _measure(Vector2(0.01, 0.25), true, "first_row_occluded")
	var final_row := await _measure(Vector2(0.01, 1.0), true, "final_row_v_1")
	# The U endpoints have identical opaque texels.  Samples just before and
	# after the seam must remain equivalent when U is normalized with fract().
	var seam_before := await _measure(Vector2(0.99, 0.25), true, "seam_before_099")
	var seam_after := await _measure(Vector2(1.01, 0.25), true, "seam_after_101")

	if disabled <= 0.0001 or occluded <= 0.0001 or final_row <= 0.0001:
		_fail("empty GPU terrain sample disabled=%.6f occluded=%.6f final_row=%.6f" % [disabled, occluded, final_row])
		return
	if occluded >= disabled * 0.45:
		_fail("enabled UV mask did not materially darken direct primary sunlight: disabled=%.6f occluded=%.6f" % [disabled, occluded])
		return
	if absf(final_row - disabled) > LUMA_TOLERANCE:
		_fail("UV V=1.0 did not sample the clear final mask row: disabled=%.6f final_row=%.6f" % [disabled, final_row])
		return
	if absf(seam_before - seam_after) > LUMA_TOLERANCE:
		_fail("UV seam was discontinuous after fract: u=0.99 %.6f u=1.01 %.6f" % [seam_before, seam_after])
		return
	if seam_before >= disabled * 0.45 or seam_after >= disabled * 0.45:
		_fail("UV seam samples did not retain macro-shadow occlusion: disabled=%.6f before=%.6f after=%.6f" % [disabled, seam_before, seam_after])
		return
	print("TERRAIN_MACRO_SHADOW_UV_GPU=PASS disabled=%.6f occluded=%.6f final_row=%.6f seam_099=%.6f seam_101=%.6f" % [disabled, occluded, final_row, seam_before, seam_after])
	quit(0)


func _make_viewport() -> void:
	_viewport = SubViewport.new()
	_viewport.size = Vector2i(SIZE, SIZE)
	_viewport.own_world_3d = true
	_viewport.transparent_bg = false
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var environment := WorldEnvironment.new()
	var settings := Environment.new()
	settings.background_mode = Environment.BG_COLOR
	settings.background_color = Color.BLACK
	settings.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	settings.ambient_light_energy = 0.0
	settings.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.environment = settings
	_viewport.add_child(environment)
	var camera := Camera3D.new()
	camera.position = Vector3(SURFACE_X - 20.0, 0.0, 0.0)
	camera.fov = 32.0
	camera.current = true
	_viewport.add_child(camera)
	camera.look_at(Vector3(SURFACE_X, 0.0, 0.0), Vector3.UP)
	_light = DirectionalLight3D.new()
	_light.light_energy = 1.0
	_viewport.add_child(_light)
	# The incident ray is +X, making the shader LIGHT vector -X, aligned with
	# the inward-facing surface normal.
	_light.look_at(Vector3(1.0, 0.0, 0.0), Vector3.UP)
	_surface = MeshInstance3D.new()
	_viewport.add_child(_surface)


func _make_material() -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = load("res://materials/ring_terrain_global.gdshader") as Shader
	material.set_shader_parameter("ring_center", Vector3.ZERO)
	material.set_shader_parameter("camera_world_position", Vector3(SURFACE_X - 20.0, 0.0, 0.0))
	material.set_shader_parameter("camera_relative_region", true)
	material.set_shader_parameter("regional_surface_up", Vector3.LEFT)
	material.set_shader_parameter("sun_direction", Vector3.LEFT)
	material.set_shader_parameter("day_start_dot", -0.2)
	material.set_shader_parameter("day_full_dot", 0.35)
	material.set_shader_parameter("night_brightness", 1.0)
	material.set_shader_parameter("day_direct_light_factor", 1.0)
	material.set_shader_parameter("night_direct_light_factor", 0.0)
	material.set_shader_parameter("shadow_floor", 0.0)
	material.set_shader_parameter("fade_start_meters", -1.0)
	material.set_shader_parameter("fade_end_meters", 0.0)
	material.set_shader_parameter("far_fade_enabled", false)
	material.set_shader_parameter("far_space_enabled", false)
	material.set_shader_parameter("ring_atmosphere_enabled", false)
	material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	material.set_shader_parameter("terrain_macro_shadow_texture", _mask)
	material.set_shader_parameter("terrain_macro_shadow_use_uv", true)
	material.set_shader_parameter("terrain_macro_shadow_strength", 0.8)
	material.set_shader_parameter("terrain_macro_shadow_fade_start_meters", 0.0)
	material.set_shader_parameter("terrain_macro_shadow_fade_end_meters", 1.0)
	material.set_shader_parameter("debug_color_enabled", false)
	return material


func _measure(canonical_uv: Vector2, enabled: bool, label: String) -> float:
	_material.set_shader_parameter("terrain_macro_shadow_enabled", enabled)
	_surface.mesh = _inward_surface_mesh(canonical_uv)
	_surface.material_override = _material
	for settle in range(3):
		await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty GPU capture: " + label)
		return -1.0
	var output_dir := "res://.godot/terrain_macro_shadow_uv_gpu"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_dir))
	if image.save_png("%s/%s.png" % [output_dir, label]) != OK:
		_fail("could not save GPU capture: " + label)
		return -1.0
	var total := 0.0
	var samples := 0
	for y in range(SIZE / 3, SIZE * 2 / 3):
		for x in range(SIZE / 3, SIZE * 2 / 3):
			var color := image.get_pixel(x, y).srgb_to_linear()
			if not is_finite(color.r) or not is_finite(color.g) or not is_finite(color.b):
				_fail("non-finite GPU capture: " + label)
				return -1.0
			total += color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722
			samples += 1
	var luma: float = total / maxf(float(samples), 1.0)
	print("TERRAIN_MACRO_SHADOW_UV_CASE name=%s enabled=%s uv=(%.3f, %.3f) luma=%.6f" % [label, enabled, canonical_uv.x, canonical_uv.y, luma])
	return luma


func _make_mask() -> Texture2D:
	# R8 is intentional: the shader reads only .r.  The first and final U
	# texels match, producing a periodic source texture for the seam assertion.
	var image := Image.create(4, 2, false, Image.FORMAT_R8)
	for x in range(4):
		image.set_pixel(x, 0, Color(1.0 if x == 0 or x == 3 else 0.0, 0.0, 0.0, 1.0))
		image.set_pixel(x, 1, Color(0.0, 0.0, 0.0, 1.0))
	return ImageTexture.create_from_image(image)


func _inward_surface_mesh(canonical_uv: Vector2) -> ArrayMesh:
	var half_extent := 8.0
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = PackedVector3Array([
		Vector3(SURFACE_X, -half_extent, -half_extent), Vector3(SURFACE_X, half_extent, -half_extent),
		Vector3(SURFACE_X, half_extent, half_extent), Vector3(SURFACE_X, -half_extent, half_extent)
	])
	arrays[ArrayMesh.ARRAY_NORMAL] = PackedVector3Array([Vector3.LEFT, Vector3.LEFT, Vector3.LEFT, Vector3.LEFT])
	arrays[ArrayMesh.ARRAY_COLOR] = PackedColorArray([Color(0.62, 0.42, 0.20), Color(0.62, 0.42, 0.20), Color(0.62, 0.42, 0.20), Color(0.62, 0.42, 0.20)])
	arrays[ArrayMesh.ARRAY_TEX_UV] = PackedVector2Array([canonical_uv, canonical_uv, canonical_uv, canonical_uv])
	arrays[ArrayMesh.ARRAY_INDEX] = PackedInt32Array([0, 1, 2, 0, 2, 3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _fail(message: String) -> void:
	push_error("TERRAIN_MACRO_SHADOW_UV_GPU=FAIL " + message)
	quit(1)
