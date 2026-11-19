extends SceneTree

## T6-10 (docs/work-orders/terrain-vegetation/T6.md, design B5): the far canopy of worlds with vegetation.far_canopy (world feature bit 8).
##   CPU half (tools/FarCanopyTintGpuTest.cs): generation 1 identity of the weights and the far colour; the fold; the forest weight's 0.5 contour against the contour where the expected stem density is half
##   its interior value, on transects across forest edges, within one proxy vertex spacing at 2, 5 and 20 km (8, 32 and 128 m); the published canopy albedo.
##   GPU half (this file): a flat proxy receiver with the ring_terrain_proxy_ground shader (the rig of terrain_rock_material_gpu_test.gd) and forest weight 1:
##     (b) feature off is byte identical to the same shader with the canopy block cut out (text between FAR_CANOPY_BEGIN and FAR_CANOPY_END), in five ways: bit 8 clear; bit 8 set with the global unpublished (w 0);
##         bit 8 set and published but the vertex weight 0 (grass); bit 8 set and published on a 40 degree face (the slope term) and on a mesh with zero normals (skirts). The pre-change far captures of the whole ground shaders are checked by
##         terrain_rock_material_gpu_test (byte identity against .godot/audit_baseline/rtx4070/t4/pre);
##     (c) bit 8 set and published, forest weight 1, flat: the colour is the feature-off colour below far start - 1000 m (identical), moves toward the canopy albedo along the ramp (monotone) and is full beyond far start.
## Run (windowed Vulkan, not --headless): tools/run_tests.ps1 -Tests far_canopy_tint_gpu_test -Label t6-10. Prints FAR_CANOPY_TINT_GPU=PASS.
const TEST := preload("res://tools/FarCanopyTintGpuTest.cs")
const SHADER_PATH := "res://materials/ring_terrain_proxy_ground.gdshader"
const FEATURE_BIT := 256 # 1 << 8
const VIEW := Vector2i(256, 160)
const RING_RADIUS := 5000000.0
const PROXY_BASE := 2400.0
const FAR_START := 5500.0
const TEST_ALBEDO := Vector3(0.02, 0.5, 0.02)
const SETTLE_FRAMES := 12

var failures: PackedStringArray = []
var _viewport: SubViewport
var _camera: Camera3D
var _sun: DirectionalLight3D
var _flat: MeshInstance3D
var _tilted: MeshInstance3D
var _skirt: MeshInstance3D
var _material: ShaderMaterial
var _stripped: ShaderMaterial
var _finished := false


func _init() -> void:
	call_deferred("_run")
	create_timer(300.0).timeout.connect(func() -> void: _fail("timeout"))


func _run() -> void:
	var runner = TEST.new()
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--scan="): # pose finder for the calibration stills: --scan=around,across
			var parts := arg.substr(7).split(",")
			runner.Scan(int(parts[0]), int(parts[1]))
			quit(0)
			return
	var code: int = runner.Run()
	if code != 0:
		failures.append("the CPU half failed (see FAR_CANOPY_TINT_GPU=FAIL above)") # the GPU half still runs, so one run shows every failing check
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	_build_rig()
	await _gpu_checks()
	if failures.is_empty():
		print("FAR_CANOPY_TINT_GPU=PASS")
		_finish(0)
	else:
		push_error("FAR_CANOPY_TINT_GPU=FAIL " + "; ".join(failures))
		_finish(1)


# ------------------------------------------------------------------ rig

func _grid(normal: Vector3) -> MeshInstance3D:
	var vertices := PackedVector3Array()
	var normals := PackedVector3Array()
	var colors := PackedColorArray()
	var custom := PackedFloat32Array()
	var indices := PackedInt32Array()
	var cells := 44
	for i in cells + 1:
		for j in cells + 1:
			vertices.append(Vector3(PROXY_BASE, i * 2.0, -44.0 + j * 2.0))
			normals.append(normal)
			colors.append(Color(0, 1, 0, 0)) # forest weight 1, the rest grass is 0
			custom.append_array([0.0, 0.0, 0.0, 0.0])
	for i in cells:
		for j in cells:
			var a := i * (cells + 1) + j
			var c := a + cells + 1
			indices.append_array([a, a + 1, c, a + 1, c + 1, c])
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = vertices
	arrays[ArrayMesh.ARRAY_NORMAL] = normals
	arrays[ArrayMesh.ARRAY_COLOR] = colors
	arrays[ArrayMesh.ARRAY_CUSTOM0] = custom
	arrays[ArrayMesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays, [], {}, Mesh.ARRAY_CUSTOM_RGBA_FLOAT << Mesh.ARRAY_FORMAT_CUSTOM0_SHIFT)
	mesh.custom_aabb = AABB(Vector3(PROXY_BASE - 10, -10, -100), Vector3(200, 120, 200))
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	instance.visible = false
	_viewport.add_child(instance)
	return instance


func _make_material(shader: Shader) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("biome_texture_weights_enabled", true)
	material.set_shader_parameter("biome_texture_scale", 0.20)
	material.set_shader_parameter("ring_center", Vector3(RING_RADIUS, 0, 0))
	material.set_shader_parameter("near_fade_enabled", false)
	material.set_shader_parameter("far_fade_enabled", false)
	material.set_shader_parameter("geomorph_enabled", false)
	material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	material.set_shader_parameter("terrain_macro_shadow_enabled", false)
	material.set_shader_parameter("sun_direction", Vector3(0.45, 0.2, 0.3).normalized())
	material.set_shader_parameter("roughness_value", 0.92)
	return material


func _build_rig() -> void:
	_viewport = SubViewport.new()
	_viewport.own_world_3d = true
	_viewport.size = VIEW
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.10, 0.12, 0.17)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.30, 0.33, 0.38)
	environment.ambient_light_energy = 1.0
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	_viewport.add_child(world_environment)
	_sun = DirectionalLight3D.new()
	_sun.shadow_enabled = false
	_sun.transform = Transform3D(Basis.looking_at(-Vector3(0.30, 0.85, 0.20).normalized(), Vector3.UP), Vector3.ZERO)
	_viewport.add_child(_sun)
	_camera = Camera3D.new()
	_camera.current = true
	_camera.fov = 70.0
	_camera.far = 2000.0
	_viewport.add_child(_camera)
	RenderingServer.global_shader_parameter_set("ring_wet_params", Vector4(1, 0.18, 1, 0))
	_flat = _grid(Vector3.RIGHT)
	var tilt := deg_to_rad(40.0)
	_tilted = _grid(Vector3(cos(tilt), sin(tilt), 0.0)) # the geometric normal 40 degrees off the ring up (+X)
	_skirt = _grid(Vector3.ZERO) # skirt vertices carry no normal
	var shader := load(SHADER_PATH) as Shader
	_material = _make_material(shader)
	var lines := FileAccess.get_file_as_string(SHADER_PATH).split("\n")
	var kept := PackedStringArray()
	var skipping := false
	var cut := 0
	for line in lines:
		if line.contains("// FAR_CANOPY_BEGIN"):
			skipping = true
		if not skipping:
			kept.append(line)
		else:
			cut += 1
		if line.contains("// FAR_CANOPY_END"):
			skipping = false
	_check(cut > 4, "the canopy block markers were not found in %s" % SHADER_PATH)
	var stripped_shader := Shader.new()
	stripped_shader.code = "\n".join(kept)
	_stripped = _make_material(stripped_shader)


# ------------------------------------------------------------------ views

func _set_globals(mask: int, published: bool) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_world_features", mask)
	RenderingServer.global_shader_parameter_set("ringworld_far_canopy", Vector4(TEST_ALBEDO.x, TEST_ALBEDO.y, TEST_ALBEDO.z, FAR_START if published else 0.0))


func _view(material: ShaderMaterial, tilted: bool, weight: float, distance: float, skirt := false) -> Image:
	_flat.visible = not tilted and not skirt
	_tilted.visible = tilted
	_skirt.visible = skirt
	var instance := _skirt if skirt else (_tilted if tilted else _flat)
	instance.material_override = material
	# Forest weight 0 or 1 comes from the vertex colour; rebuild the colour through a shader-independent route: the instance modulates nothing, so swap the mesh colours.
	_set_mesh_weight(instance, weight)
	_camera.position = Vector3(PROXY_BASE + 22, -30, 0)
	_camera.look_at(Vector3(PROXY_BASE + 30, 40, 0), Vector3.RIGHT)
	# The shader distance is set through the material's camera uniform: the receiver stays where it is.
	material.set_shader_parameter("camera_world_position", Vector3(PROXY_BASE, -distance + 40.0, 0))
	for frame in SETTLE_FRAMES:
		await process_frame
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	image.convert(Image.FORMAT_RGBA8)
	return image


func _set_mesh_weight(instance: MeshInstance3D, weight: float) -> void:
	var mesh := instance.mesh as ArrayMesh
	var arrays := mesh.surface_get_arrays(0)
	var colors := PackedColorArray()
	colors.resize((arrays[ArrayMesh.ARRAY_VERTEX] as PackedVector3Array).size())
	colors.fill(Color(0, weight, 0, 0))
	arrays[ArrayMesh.ARRAY_COLOR] = colors
	var custom := PackedFloat32Array()
	custom.resize((arrays[ArrayMesh.ARRAY_VERTEX] as PackedVector3Array).size() * 4)
	arrays[ArrayMesh.ARRAY_CUSTOM0] = custom
	mesh.clear_surfaces()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays, [], {}, Mesh.ARRAY_CUSTOM_RGBA_FLOAT << Mesh.ARRAY_FORMAT_CUSTOM0_SHIFT)


func _mean(image: Image) -> Vector3:
	var mean := Vector3.ZERO
	var count := 0
	for y in range(40, 120):
		for x in range(80, 176):
			var c := image.get_pixel(x, y)
			mean += Vector3(c.r, c.g, c.b)
			count += 1
	return mean / float(count)


func _differing(a: Image, b: Image) -> int:
	var da := a.get_data()
	var db := b.get_data()
	if da.size() != db.size():
		return -1
	var n := 0
	for i in range(0, da.size(), 4):
		if da[i] != db[i] or da[i + 1] != db[i + 1] or da[i + 2] != db[i + 2] or da[i + 3] != db[i + 3]:
			n += 1
	return n


# ------------------------------------------------------------------ checks

func _gpu_checks() -> void:
	var far := FAR_START + 1000.0
	# (b) feature off: identical to the shader without the block.
	_set_globals(0, true)
	var cut_ref := await _view(_stripped, false, 1.0, far)
	var bit_clear := await _view(_material, false, 1.0, far)
	_check_identical(bit_clear, cut_ref, "bit 8 clear, published, forest 1, far")
	_set_globals(FEATURE_BIT, false)
	var unpublished := await _view(_material, false, 1.0, far)
	_check_identical(unpublished, cut_ref, "bit 8 set, global unpublished (w 0)")
	_set_globals(FEATURE_BIT, true)
	var grass := await _view(_material, false, 0.0, far)
	var grass_ref := await _view(_stripped, false, 0.0, far)
	_check_identical(grass, grass_ref, "bit 8 set and published, forest weight 0")
	var steep := await _view(_material, true, 1.0, far)
	var steep_ref := await _view(_stripped, true, 1.0, far)
	_check_identical(steep, steep_ref, "bit 8 set and published, 40 degree face")
	var skirt := await _view(_material, false, 1.0, far, true)
	var skirt_ref := await _view(_stripped, false, 1.0, far, true)
	_check_identical(skirt, skirt_ref, "bit 8 set and published, zero normal (skirt vertices)")
	# (c) the tint ramp on flat forest.
	var near_on := await _view(_material, false, 1.0, FAR_START - 1200.0)
	var near_ref := await _view(_stripped, false, 1.0, FAR_START - 1200.0)
	_check_identical(near_on, near_ref, "below far start - 1000 m the tint is not applied")
	var means: Array[Vector3] = []
	for d in [FAR_START - 800.0, FAR_START - 400.0, FAR_START, FAR_START + 1000.0]:
		var image := await _view(_material, false, 1.0, d)
		var ref := await _view(_stripped, false, 1.0, d)
		var m := _mean(image)
		var r := _mean(ref)
		means.append(m)
		print("FAR_CANOPY_TINT_GPU ramp %5.0f m: tinted mean (%.4f, %.4f, %.4f), untinted (%.4f, %.4f, %.4f)" % [d, m.x, m.y, m.z, r.x, r.y, r.z])
	var full := _mean(await _view(_material, false, 1.0, far))
	var full_ref := _mean(cut_ref)
	var greener := (full.y / maxf(full.x, 0.0001)) / (full_ref.y / maxf(full_ref.x, 0.0001))
	print("FAR_CANOPY_TINT_GPU full tint at %.0f m: mean (%.4f, %.4f, %.4f) against feature off (%.4f, %.4f, %.4f), green/red ratio x%.2f" % [far, full.x, full.y, full.z, full_ref.x, full_ref.y, full_ref.z, greener])
	# The scene is lit by blue ambient light and haze, so the canopy (green) shows as a drop of red and blue against the green.
	_check(greener > 1.05 and full.x < full_ref.x * 0.95, "the tint does not move the colour toward the (green) canopy albedo: green/red ratio x%.2f, red %.4f against %.4f" % [greener, full.x, full_ref.x])
	var ratio_prev := 1.0
	var ramp_ok := true
	for i in means.size():
		var ratio := means[i].y / maxf(means[i].x, 0.0001)
		if ratio < ratio_prev - 0.02:
			ramp_ok = false
		ratio_prev = ratio
	_check(ramp_ok, "the tint is not monotone over the ramp")


func _check_identical(a: Image, b: Image, label: String) -> void:
	var n := _differing(a, b)
	print("FAR_CANOPY_TINT_GPU identity (%s): differing pixels %d" % [label, n])
	_check(n == 0, "%s: %d pixels differ from the shader without the canopy block" % [label, n])


# ------------------------------------------------------------------ plumbing

func _check(ok: bool, message: String) -> void:
	if not ok:
		failures.append(message)
		push_error("FAR_CANOPY_TINT_GPU: " + message)


func _finish(code: int) -> void:
	_finished = true
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	for frame in 4:
		await RenderingServer.frame_post_draw
	quit(code)


func _fail(message: String) -> void:
	if _finished:
		return
	_finished = true
	push_error("FAR_CANOPY_TINT_GPU=FAIL " + message)
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	call_deferred("quit", 1)
