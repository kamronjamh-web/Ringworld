extends SceneTree

## Pixel-level regression for the tree LOD transition ABI.  It deliberately
## renders the production imported-tree shader through a MultiMesh, because
## INSTANCE_CUSTOM is the data channel used by GPU tree output rows.
const SIZE := 256
const OUT := "res://artifacts/tree-render-optimization/visual"
const CROP := Rect2i(48, 48, 160, 160)

var _failed := false
var _viewport: SubViewport
var _shader: Shader
var _white: ImageTexture
var _normal: ImageTexture

func _init() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT)) != OK:
		_fail("could not create artifact directory")
		return
	_shader = load("res://materials/imported_tree.gdshader") as Shader
	if _shader == null:
		_fail("production imported-tree shader unavailable")
		return
	_white = _texture(Color(0.34, 0.72, 0.25, 1.0))
	_normal = _texture(Color(0.5, 0.5, 1.0, 1.0))
	_make_viewport()
	var legacy := await _render("legacy_full", [Vector4(0.0, 0.0, 0.0, 0.0)], false)
	var incoming_zero := await _render("incoming_0", [Vector4(0.0, 0.0, 0.271, 2.0)], false)
	var incoming_full := await _render("incoming_1", [Vector4(0.0, 1.0, 0.271, 2.0)], false)
	var outgoing_full := await _render("outgoing_minus_1", [Vector4(0.0, -1.0, 0.271, 2.0)], false)
	if _failed: return
	_assert_visible("legacy full", legacy)
	_assert_hidden("incoming zero", incoming_zero)
	_assert_same_coverage("incoming full", legacy, incoming_full)
	_assert_same_coverage("outgoing minus one", legacy, outgoing_full)
	for coverage: float in [0.25, 0.50, 0.75]:
		var overlap := await _render("overlap_%02d" % int(coverage * 100.0), [Vector4(0.0, coverage, 0.271, 2.0), Vector4(0.0, -(1.0 - coverage), 0.271, 2.0)], false)
		_assert_same_coverage("complement %.2f" % coverage, legacy, overlap)
	if _failed: return
	var detailed := await _render("detailed_shading", [Vector4(0.0, 1.0, 0.271, 2.0)], false)
	var simple := await _render("simplified_shading", [Vector4(0.0, 1.0, 0.271, 2.0)], true)
	_assert_visible("detailed shading", detailed)
	_assert_visible("simplified shading", simple)
	var detailed_luma := _mean_luma(detailed)
	var simple_luma := _mean_luma(simple)
	if simple_luma < detailed_luma * 0.55:
		_fail("simplified shading lost albedo detailed=%.5f simple=%.5f" % [detailed_luma, simple_luma])
	if _failed: return
	var report := {
		"result": "pass",
		"shader": "materials/imported_tree.gdshader",
		"abi": {"marker": 2, "incoming": "+coverage", "outgoing": "-(1-coverage)", "seed": 0.271},
		"mean_luma": {"legacy": _mean_luma(legacy), "incoming_zero": _mean_luma(incoming_zero), "incoming_full": _mean_luma(incoming_full), "outgoing_minus_one": _mean_luma(outgoing_full), "detailed": detailed_luma, "simplified": simple_luma},
		"captures": ["legacy_full.png", "incoming_0.png", "incoming_1.png", "outgoing_minus_1.png", "overlap_25.png", "overlap_50.png", "overlap_75.png", "detailed_shading.png", "simplified_shading.png"]
	}
	var file := FileAccess.open(OUT.path_join("tree_lod_dither_gpu_test.json"), FileAccess.WRITE)
	if file == null:
		_fail("could not write JSON report")
		return
	file.store_string(JSON.stringify(report, "\t"))
	print("TREE_LOD_DITHER_GPU_TEST=PASS output=%s detailed=%.5f simplified=%.5f" % [OUT, detailed_luma, simple_luma])
	quit(0)

func _make_viewport() -> void:
	_viewport = SubViewport.new()
	_viewport.size = Vector2i(SIZE, SIZE)
	_viewport.own_world_3d = true
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.WHITE
	environment.ambient_light_energy = 0.75
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world := WorldEnvironment.new()
	world.environment = environment
	_viewport.add_child(world)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 12.0
	camera.position = Vector3(0.0, 0.0, 10.0)
	camera.current = true
	_viewport.add_child(camera)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	var sun := DirectionalLight3D.new()
	sun.light_energy = 0.45
	sun.shadow_enabled = false
	_viewport.add_child(sun)
	sun.look_at(Vector3(-0.4, -0.5, -1.0), Vector3.UP)
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)

func _render(label: String, rows: Array[Vector4], simplified: bool) -> Image:
	var holder := Node3D.new()
	_viewport.add_child(holder)
	for row: Vector4 in rows:
		var multi := MultiMesh.new()
		multi.transform_format = MultiMesh.TRANSFORM_3D
		multi.use_custom_data = true
		multi.mesh = _quad()
		multi.instance_count = 1
		multi.set_instance_transform(0, Transform3D.IDENTITY)
		multi.set_instance_custom_data(0, Color(row.x, row.y, row.z, row.w))
		var instance := MultiMeshInstance3D.new()
		instance.multimesh = multi
		instance.material_override = _material(simplified)
		instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		holder.add_child(instance)
	for frame: int in range(5):
		await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty capture " + label)
		return Image.new()
	if image.save_png(OUT.path_join(label + ".png")) != OK:
		_fail("could not save capture " + label)
	holder.queue_free()
	await process_frame
	return image

func _material(simplified: bool) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = _shader
	material.set_shader_parameter("albedo_texture", _white)
	material.set_shader_parameter("normal_texture", _normal)
	material.set_shader_parameter("metallic_texture", _white)
	material.set_shader_parameter("roughness_texture", _white)
	material.set_shader_parameter("has_normal_texture", true)
	material.set_shader_parameter("has_metallic_texture", true)
	material.set_shader_parameter("has_roughness_texture", true)
	material.set_shader_parameter("metallic_factor", 0.35)
	material.set_shader_parameter("roughness_factor", 0.25)
	material.set_shader_parameter("use_alpha_cutout", false)
	material.set_shader_parameter("vegetation_wind_enabled", false)
	material.set_shader_parameter("simplified_tree_shading", simplified)
	material.set_shader_parameter("ring_center", Vector3(0.0, 0.0, 5000.0))
	material.set_shader_parameter("sun_direction", Vector3(-1.0, 0.0, 0.0))
	material.set_shader_parameter("night_tint", Vector3.ONE)
	material.set_shader_parameter("night_direct_light_factor", 0.0)
	material.set_shader_parameter("day_direct_light_factor", 1.0)
	material.set_shader_parameter("local_cloud_shadow_enabled", false)
	material.set_shader_parameter("local_cloud_shadow_near_enabled", false)
	material.set_shader_parameter("local_cloud_shadow_far_enabled", false)
	return material

func _quad() -> ArrayMesh:
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = PackedVector3Array([Vector3(-4.0, -4.0, 0.0), Vector3(-4.0, 4.0, 0.0), Vector3(4.0, 4.0, 0.0), Vector3(4.0, -4.0, 0.0)])
	arrays[ArrayMesh.ARRAY_NORMAL] = PackedVector3Array([Vector3(0.0, 0.0, 1.0), Vector3(0.0, 0.0, 1.0), Vector3(0.0, 0.0, 1.0), Vector3(0.0, 0.0, 1.0)])
	arrays[ArrayMesh.ARRAY_TEX_UV] = PackedVector2Array([Vector2(0.0, 0.0), Vector2(0.0, 1.0), Vector2(1.0, 1.0), Vector2(1.0, 0.0)])
	arrays[ArrayMesh.ARRAY_INDEX] = PackedInt32Array([0, 1, 2, 0, 2, 3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh

func _texture(color: Color) -> ImageTexture:
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, color)
	return ImageTexture.create_from_image(image)

func _assert_visible(label: String, image: Image) -> void:
	if _mean_luma(image) < 0.02:
		_fail(label + " did not render visible pixels")

func _assert_hidden(label: String, image: Image) -> void:
	if _mean_luma(image) > 0.001:
		_fail(label + " retained visible pixels")

func _assert_same_coverage(label: String, reference: Image, candidate: Image) -> void:
	var holes := 0
	var reference_pixels := 0
	for y: int in range(CROP.position.y, CROP.end.y):
		for x: int in range(CROP.position.x, CROP.end.x):
			var base := reference.get_pixel(x, y).srgb_to_linear().get_luminance()
			var observed := candidate.get_pixel(x, y).srgb_to_linear().get_luminance()
			if base > 0.02:
				reference_pixels += 1
				if observed < base * 0.70: holes += 1
	if reference_pixels < 100 or holes > 0:
		_fail("%s coverage mismatch reference_pixels=%d holes=%d" % [label, reference_pixels, holes])

func _mean_luma(image: Image) -> float:
	var total := 0.0
	var samples := 0
	for y: int in range(CROP.position.y, CROP.end.y):
		for x: int in range(CROP.position.x, CROP.end.x):
			total += image.get_pixel(x, y).srgb_to_linear().get_luminance()
			samples += 1
	return total / maxf(1.0, float(samples))

func _fail(message: String) -> void:
	_failed = true
	push_error("TREE_LOD_DITHER_GPU_TEST=FAIL " + message)
	quit(1)
