extends SceneTree

# Production shader proof for the CPU-provided low-frequency terrain handoff.
const DRIVER := preload("res://tools/VegetationWindTestDriver.cs")
const SIZE := 256
var viewport: SubViewport
var camera: Camera3D
var grass: MultiMeshInstance3D
var material: ShaderMaterial
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("RenderingDevice required")
		return
	call_deferred("run")

func run() -> void:
	setup()
	# Implicit grassland is all-zero COLOR. With strength zero, moving beyond
	# the handoff must preserve blade color; strength one targets terrain blue.
	material.set_shader_parameter("grass_terrain_handoff_strength", 0.0)
	set_distance(10.0)
	var near := await capture()
	material.set_shader_parameter("grass_appearance_enabled", false)
	var legacy_near := await capture()
	if not require(difference(near, legacy_near) < .002, "default grassland palette changed existing near color"): return
	material.set_shader_parameter("grass_appearance_enabled", true)
	material.set_shader_parameter("grass_palette_grassland", Color(1, .02, .02, 1))
	var palette_near := await capture()
	if not require(colored(palette_near) > 50 and average(palette_near).x > average(near).x + .12, "grassland palette edit did not affect near blades: before=%s after=%s pixels=%s/%s" % [average(near), average(palette_near), colored(near), colored(palette_near)]): return
	material.set_shader_parameter("grass_palette_grassland", Color(.22, .36, .12, 1))
	set_distance(140.0)
	var zero_far := await capture()
	if not require(colored(near) > 50 and colored(zero_far) > 50 and difference(near, zero_far) < .015, "zero-strength terrain handoff changed blade color"): return
	material.set_shader_parameter("grass_terrain_handoff_strength", 1.0)
	var blue_far := await capture()
	if not require(colored(blue_far) > 50 and average(blue_far).z > average(zero_far).z + .12, "implicit grassland did not reach blue terrain average"): return
	# Explicit forest COLOR uses its own terrain average, proving the payload is
	# interpreted as raw biome data rather than sRGB display color.
	grass.multimesh.set_instance_color(0, Color(0, 1, 0, 0))
	material.set_shader_parameter("grass_terrain_average_forest", Vector3(1, .03, .03))
	var forest_far := await capture()
	if not require(colored(forest_far) > 50 and average(forest_far).x > average(blue_far).x + .12, "forest COLOR did not select forest terrain average"): return
	print("GRASS_APPEARANCE_GPU=PASS zero-strength, implicit-grassland, forest-color handoff")
	quit(0)

func setup() -> void:
	viewport = SubViewport.new(); viewport.size = Vector2i(SIZE, SIZE); viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var environment := Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color.BLACK; environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color.WHITE; environment.ambient_light_energy = 1.0
	var world := WorldEnvironment.new(); world.environment = environment; viewport.add_child(world)
	camera = Camera3D.new(); camera.current = true; camera.projection = Camera3D.PROJECTION_ORTHOGONAL; camera.size = 2.0; camera.far = 200.0; viewport.add_child(camera)
	grass = MultiMeshInstance3D.new(); grass.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF; viewport.add_child(grass)
	var multi := MultiMesh.new(); multi.transform_format = MultiMesh.TRANSFORM_3D; multi.use_colors = true; multi.use_custom_data = true; multi.mesh = DRIVER.new().call("LoadMeadowMesh"); multi.instance_count = 1; multi.set_instance_transform(0, Transform3D(Basis(Vector3.UP, .3).scaled(Vector3(1.0, 1.0, 1.0)), Vector3.ZERO)); multi.set_instance_color(0, Color(0, 0, 0, 0)); multi.set_instance_custom_data(0, Color(.5, .5, .1, .2)); grass.multimesh = multi
	material = ShaderMaterial.new(); material.shader = load("res://materials/ring_standing_grass.gdshader"); material.set_shader_parameter("local_cloud_shadow_enabled", false); material.set_shader_parameter("grass_base_color", Vector3(.2, .2, .2)); material.set_shader_parameter("grass_tip_color", Vector3(.2, .2, .2)); material.set_shader_parameter("grass_lod_enabled", false); material.set_shader_parameter("grass_lod_near_distance", 30.0); material.set_shader_parameter("grass_lod_far_distance", 90.0); material.set_shader_parameter("grass_lod_transition_width", 10.0); material.set_shader_parameter("grass_appearance_enabled", true); material.set_shader_parameter("grass_palette_grassland", Color(.22, .36, .12, 1)); material.set_shader_parameter("grass_palette_forest", Color(.14, .28, .10, 1)); material.set_shader_parameter("grass_palette_sparse", Color(.42, .38, .18, 1)); material.set_shader_parameter("grass_terrain_average_grass", Vector3(.02, .05, .92)); material.set_shader_parameter("grass_terrain_average_dirt", Vector3(.02, .05, .92)); material.set_shader_parameter("grass_terrain_average_desert", Vector3(.02, .05, .92)); material.set_shader_parameter("grass_terrain_average_forest", Vector3(.02, .05, .92)); material.set_shader_parameter("grass_terrain_average_tundra", Vector3(.02, .05, .92)); material.set_shader_parameter("grass_terrain_average_alpine", Vector3(.02, .05, .92)); grass.material_override = material

func set_distance(distance: float) -> void:
	camera.position = Vector3(0, .5, distance); camera.look_at(Vector3(0, .5, 0), Vector3.UP)

func capture() -> Image:
	for i in range(4): await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()

func colored(image: Image) -> int:
	var count := 0
	for y in range(SIZE):
		for x in range(SIZE):
			if image.get_pixel(x, y).get_luminance() > .02: count += 1
	return count

func average(image: Image) -> Vector3:
	var sum := Vector3.ZERO
	var count := 0
	for y in range(SIZE):
		for x in range(SIZE):
			var color := image.get_pixel(x, y)
			if color.get_luminance() > .02: sum += Vector3(color.r, color.g, color.b); count += 1
	return sum / max(1, count)

func difference(a: Image, b: Image) -> float:
	var sum := 0.0
	for y in range(SIZE):
		for x in range(SIZE):
			var left := a.get_pixel(x, y); var right := b.get_pixel(x, y); sum += absf(left.r-right.r) + absf(left.g-right.g) + absf(left.b-right.b)
	return sum / float(SIZE * SIZE)

func require(condition: bool, message: String) -> bool:
	if condition: return true
	fail(message); return false
func fail(message: String) -> void:
	if failed: return
	failed = true; push_error("GRASS_APPEARANCE_GPU=FAIL " + message); quit(1)

