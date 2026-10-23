extends SceneTree

## Production tree haze contract: procedural, imported mesh, and the shared
## impostor atlas must all use the same camera-relative atmosphere response.
## Run:  godot --path . --script res://tools/tree_atmosphere_gpu_test.gd [-- --model=legacy]
## --model=legacy selects the Legacy haze (atmosphere_haze_model = 0); the default is Physical.
const SIZE := 192
const OUT := "res://.godot/tree_atmosphere_gpu"
const BACKGROUND := Color(0.015, 0.02, 0.03, 1.0)
var physical := false
var failed := false
var done := false
var viewport: SubViewport
var environment: Environment
var nodes := {}
var materials: Array[ShaderMaterial] = []

func _initialize() -> void:
	physical = not ("--model=legacy" in OS.get_cmdline_user_args())
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(60.0).timeout.connect(_on_watchdog)
	_make_scene()
	if failed: return
	_show("")
	var background := await _capture("background")
	var results := {}
	for label in nodes:
		_show(label)
		var disabled := await _capture(label + "_disabled")
		_set_atmosphere(true, false)
		var enabled := await _capture(label + "_enabled")
		_set_atmosphere(true, true)
		var zero := await _capture(label + "_zero_density")
		_set_atmosphere(false, false)
		var disabled_luma := _foreground_luma(disabled, background)
		var enabled_luma := _foreground_luma(enabled, background)
		results[label] = enabled_luma - disabled_luma
		if absf(results[label]) < .0005:
			_fail("%s haze did not change production RGB" % label); return
		if _image_delta(disabled, zero) > .001:
			_fail("%s zero density differs from disabled atmosphere" % label); return
		if _discarded_fill(disabled, enabled, background):
			_fail("%s atmosphere filled alpha-cutout texels" % label); return
	if results.mesh * results.impostor <= 0.0 or absf(results.mesh / results.impostor) > 3.0:
		_fail("mesh/impostor haze profile response diverged: %s" % results); return
	# In-scatter must grow (never shrink) with distance: a black tree shows only haze radiance, re-captured at increasing range.
	_show("procedural")
	var original_tint: Variant = (nodes["procedural"].material_override as ShaderMaterial).get_shader_parameter("tree_tint")
	(nodes["procedural"].material_override as ShaderMaterial).set_shader_parameter("tree_tint", Vector3.ZERO)
	var previous_effect := -1.0
	var effects := []
	# Ranges stay above the floor (a 300 m camera sees ground at ~55 km); beyond that a physical ray dips into the dense floor shell
	# where side-scatter Mie weighs more than Rayleigh, so the in-scatter radiance legitimately stops being monotone.
	for distance in [2000.0, 10000.0, 30000.0, 50000.0]:
		nodes["procedural"].position = Vector3(0, 0, -distance)
		_set_atmosphere(false, false)
		var clear_image := await _capture("monotone_clear_%d" % int(distance))
		_set_atmosphere(true, false)
		var hazed_image := await _capture("monotone_hazed_%d" % int(distance))
		var effect := _footprint_effect(clear_image, hazed_image, background)
		effects.append(effect)
		if effect < previous_effect - .002:
			_fail("haze in-scatter shrank with distance: %s" % [effects]); return
		previous_effect = effect
	# The Legacy fixture saturates its ramp at 2 km, so only the physical model has a 2-50 km gradient to assert.
	if physical and effects[effects.size() - 1] < effects[0] + .01:
		_fail("haze in-scatter did not grow over 2-50 km: %s" % [effects]); return
	(nodes["procedural"].material_override as ShaderMaterial).set_shader_parameter("tree_tint", original_tint)
	nodes["procedural"].position = Vector3(0, 0, -100000)
	print("TREE_ATMOSPHERE_GPU=PASS model=%s procedural=%.6f mesh=%.6f impostor=%.6f monotone=%s" % ["physical" if physical else "legacy", results.procedural, results.mesh, results.impostor, effects])
	done = true
	quit(0)

func _make_scene() -> void:
	viewport = SubViewport.new(); viewport.size = Vector2i(SIZE, SIZE); viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var world := WorldEnvironment.new(); environment = Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = BACKGROUND; environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color.WHITE; environment.ambient_light_energy = 1.0; environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR; world.environment = environment; viewport.add_child(world)
	var camera := Camera3D.new(); camera.projection = Camera3D.PROJECTION_ORTHOGONAL; camera.size = 18.0; camera.far = 200000.0; camera.current = true; viewport.add_child(camera); camera.look_at(Vector3(0, 0, -100000), Vector3.UP)
	var driver: Node = load("res://scripts/Vegetation/ImportedTreeCatalogDriver.cs").new(); root.add_child(driver)
	_add("procedural", QuadMesh.new(), _material("res://materials/ring_tree.gdshader"))
	_add("mesh", driver.call("LoadVariantMesh", 0, 3) as Mesh, _copy(driver.call("LoadVariantLodMaterial", 0, 3) as ShaderMaterial))
	_add("impostor", driver.call("LoadSharedImpostorMesh") as Mesh, _copy(driver.call("LoadSharedImpostorMaterial")))
	if nodes.size() != 3: _fail("production tree assets unavailable"); return
	for material in materials: _configure(material)
	_set_atmosphere(false, false)

func _add(label: String, mesh: Mesh, material: ShaderMaterial) -> void:
	if mesh == null or material == null: return
	var instance := MeshInstance3D.new(); instance.mesh = mesh; instance.material_override = material; instance.position = Vector3(0, 0, -100000); instance.scale = Vector3(10, 10, 10); viewport.add_child(instance); nodes[label] = instance; materials.append(material)

func _material(path: String) -> ShaderMaterial:
	var material := ShaderMaterial.new(); material.shader = load(path); material.set_shader_parameter("tree_tint", Vector3(.25, .72, .30)); return material

func _copy(source: ShaderMaterial) -> ShaderMaterial:
	return source.duplicate() as ShaderMaterial if source != null else null

func _configure(material: ShaderMaterial) -> void:
	material.set_shader_parameter("ring_center", Vector3.ZERO); material.set_shader_parameter("sun_direction", Vector3.LEFT); material.set_shader_parameter("night_tint", Vector3.ONE); material.set_shader_parameter("vegetation_wind_enabled", false)
	material.set_shader_parameter("ring_atmosphere_ray_frame_valid", true); material.set_shader_parameter("ring_atmosphere_camera_world_position", Vector3.ZERO); material.set_shader_parameter("ring_atmosphere_camera_height_meters", 300.0 if physical else 0.0); material.set_shader_parameter("ring_atmosphere_outward_world", Vector3.RIGHT); material.set_shader_parameter("ring_atmosphere_axis_world", Vector3.UP); material.set_shader_parameter("ring_atmosphere_sidewall_negative_meters", 1000000.0); material.set_shader_parameter("ring_atmosphere_sidewall_positive_meters", 1000000.0); material.set_shader_parameter("ring_atmosphere_coordinate_scale", 1.0); material.set_shader_parameter("ring_atmosphere_to_sun", Vector3.LEFT)
	material.set_shader_parameter("ring_radius_meters", 5000000.0); material.set_shader_parameter("atmosphere_clear_distance_meters", 1000.0); material.set_shader_parameter("atmosphere_full_distance_meters", 2000.0); material.set_shader_parameter("atmosphere_boundary_max_opacity", .35); material.set_shader_parameter("atmosphere_upper_max_opacity", .1); material.set_shader_parameter("atmosphere_distance_haze_max_opacity", .65); material.set_shader_parameter("atmosphere_total_max_opacity", .8); material.set_shader_parameter("atmosphere_scattering_energy", .26)
	material.set_shader_parameter("atmosphere_haze_model", 1 if physical else 0)

func _set_atmosphere(enabled: bool, zero_density: bool) -> void:
	for material in materials:
		material.set_shader_parameter("ring_atmosphere_enabled", enabled)
		material.set_shader_parameter("atmosphere_boundary_max_opacity", 0.0 if zero_density else .35)
		material.set_shader_parameter("atmosphere_upper_max_opacity", 0.0 if zero_density else .1)
		material.set_shader_parameter("atmosphere_distance_haze_max_opacity", 0.0 if zero_density else .65)
		# Physical model: zero density = zero extinction in every layer.
		material.set_shader_parameter("ring_haze_sigma_R", Vector3.ZERO if zero_density else Vector3(5.802e-6, 13.558e-6, 33.1e-6))
		material.set_shader_parameter("ring_haze_sigma_A", 0.0 if zero_density else 4.175e-6)
		material.set_shader_parameter("ring_haze_sigma_M0", 0.0 if zero_density else 4.0e-6)

func _show(label: String) -> void:
	for key in nodes: nodes[key].visible = key == label

func _capture(label: String) -> Image:
	for i in range(4): await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty(): _fail("empty capture " + label); return Image.new()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT)); image.save_png(OUT.path_join(label + ".png"))
	return image

func _foreground_luma(image: Image, background: Image) -> float:
	var total := 0.0; var count := 0
	for y in range(SIZE):
		for x in range(SIZE):
			var color := image.get_pixel(x, y)
			if _color_delta(color, background.get_pixel(x, y)) > .02: total += color.srgb_to_linear().get_luminance(); count += 1
	return total / max(1, count)

## Mean |hazed - clear| luminance over the tree's clear-air footprint (pixels that differ from the background without haze).
func _footprint_effect(clear: Image, hazed: Image, background: Image) -> float:
	var total := 0.0; var count := 0
	for y in range(SIZE):
		for x in range(SIZE):
			var color := clear.get_pixel(x, y)
			if _color_delta(color, background.get_pixel(x, y)) > .02:
				total += absf(hazed.get_pixel(x, y).srgb_to_linear().get_luminance() - color.srgb_to_linear().get_luminance()); count += 1
	return total / max(1, count)

func _image_delta(a: Image, b: Image) -> float:
	var total := 0.0
	for y in range(SIZE):
		for x in range(SIZE):
			total += _color_delta(a.get_pixel(x, y), b.get_pixel(x, y))
	return total / float(SIZE * SIZE)

func _discarded_fill(disabled: Image, enabled: Image, background: Image) -> bool:
	for y in range(SIZE):
		for x in range(SIZE):
			if _color_delta(disabled.get_pixel(x, y), background.get_pixel(x, y)) < .002 and _color_delta(enabled.get_pixel(x, y), background.get_pixel(x, y)) > .01: return true
	return false

func _color_delta(a: Color, b: Color) -> float:
	return absf(a.r - b.r) + absf(a.g - b.g) + absf(a.b - b.b)

func _fail(message: String) -> void:
	failed = true; done = true; push_error("TREE_ATMOSPHERE_GPU=FAIL " + message); quit(1)

func _on_watchdog() -> void:
	if not done: _fail("watchdog timeout")
