extends SceneTree

# Production shader proof for terrain palette matching at every range.
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
	# Existing saved partial strength must affect close blades immediately.
	set_distance(10.0)
	material.set_shader_parameter("grass_terrain_handoff_strength", .53)
	var partial_near := await capture()
	material.set_shader_parameter("grass_terrain_handoff_strength", 1.0)
	var matched_near := await capture()
	if not require(colored(partial_near) > 50 and colored(matched_near) > 50 and average(partial_near).z > average(near).z + .08 and average(matched_near).z > average(partial_near).z + .04, "partial/full terrain color match did not affect near blades: zero=%s partial=%s full=%s pixels=%s/%s/%s" % [average(near), average(partial_near), average(matched_near), colored(near), colored(partial_near), colored(matched_near)]): return
	# Lighting convergence remains separate: nearby normals and shading must
	# be identical regardless of the optional ground lighting slider.
	material.set_shader_parameter("grass_terrain_lighting_match", 0.0)
	var near_lighting_off := await capture()
	if not require(difference(matched_near, near_lighting_off) < .0001, "ground lighting match changed near blade shading"): return
	set_distance(60.0)
	var before_blend := await capture()
	set_distance(70.0)
	var during_blend := await capture()
	set_distance(82.0)
	var completed_blend := await capture()
	if not require(colored(before_blend) > 50 and colored(during_blend) > 50 and colored(completed_blend) > 50, "ground convergence captures are empty"): return
	set_distance(140.0)
	var far_lighting_off := await capture()
	var before_average := average(before_blend)
	var during_average := average(during_blend)
	var completed_average := average(completed_blend)
	var far_average := average(far_lighting_off)
	var before_error := before_average.distance_to(far_average)
	var during_error := during_average.distance_to(far_average)
	var completed_error := completed_average.distance_to(far_average)
	for color_average in [average(matched_near), before_average, during_average, completed_average, far_average]:
		if not require(color_average.z > color_average.x + .2 and color_average.z > color_average.y + .2, "all-distance terrain blue hue was not retained: " + str(color_average)): return
	if not require(before_average.z > average(zero_far).z + .12 and before_error > .005 and during_error < before_error and completed_error < during_error and completed_error < .005, "brightness relief did not converge toward exact far terrain reference: before=%s during=%s completed=%s far=%s errors=%s/%s/%s" % [before_average, during_average, completed_average, far_average, before_error, during_error, completed_error]): return
	material.set_shader_parameter("grass_terrain_lighting_match", 1.0)
	var blue_far := await capture()
	# P3b: terrain and grass share the built-in (LIGHT_COLOR / PI) * NdotL diffuse expression, so the control no longer changes diffuse shading
	# (the default blade-shading-off path uses ground normals everywhere and has no specular); it must be inert here.
	if not require(difference(blue_far, far_lighting_off) < .0001, "ground lighting control changed distant blade diffuse shading after P3b"): return
	if not require(colored(blue_far) > 50 and average(blue_far).z > average(zero_far).z + .12, "implicit grassland did not reach blue terrain average"): return
	# Full matching still retains close blade brightness relief; final ground
	# convergence removes that relief without changing geometry.
	material.set_shader_parameter("grass_base_color", Vector3(.02, .02, .02))
	material.set_shader_parameter("grass_tip_color", Vector3(.8, .8, .8))
	var varied_far := await capture()
	if not require(difference(blue_far, varied_far) < .0001, "final convergence retained authored tip brightness"): return
	set_distance(10.0)
	var varied_near := await capture()
	if not require(difference(matched_near, varied_near) > .001, "full terrain match flattened near root/tip brightness"): return
	material.set_shader_parameter("grass_base_color", Vector3(.2, .2, .2))
	material.set_shader_parameter("grass_tip_color", Vector3(.2, .2, .2))
	set_distance(140.0)
	# Explicit forest COLOR uses its own terrain average, proving the payload is
	# interpreted as raw biome data rather than sRGB display color.
	grass.multimesh.set_instance_color(0, Color(0, 1, 0, 0))
	material.set_shader_parameter("grass_terrain_average_forest", Vector3(1, .03, .03))
	var forest_far := await capture()
	if not require(colored(forest_far) > 50 and average(forest_far).x > average(blue_far).x + .12, "forest COLOR did not select forest terrain average"): return
	set_distance(10.0)
	var forest_near := await capture()
	if not require(average(forest_near).x > average(matched_near).x + .12, "near forest did not select its terrain palette"): return
	# Sparse biomes use each texture average, including mixed terrain weights.
	for biome in [Color(1, 0, 0, 0), Color(0, 0, 1, 0), Color(0, 0, 0, 1), Color(.25, .25, .25, .25)]:
		grass.multimesh.set_instance_color(0, biome)
		material.set_shader_parameter("grass_terrain_average_desert", Vector3(.03, .9, .02))
		material.set_shader_parameter("grass_terrain_average_tundra", Vector3(.03, .9, .02))
		material.set_shader_parameter("grass_terrain_average_alpine", Vector3(.03, .9, .02))
		var sparse_near := await capture()
		if not require(colored(sparse_near) > 50 and average(sparse_near).y > average(matched_near).y + .12, "near biome mixture did not select terrain palette: " + str(biome)): return
	print("GRASS_APPEARANCE_GPU=PASS all-distance palette, partial/zero strength, biomes, separate lighting")
	quit(0)

func setup() -> void:
	# The production terrain albedo scale (default 0.15) darkens the blue terrain target; this fixture judges colour matching on absolute
	# brightness, so run it at the neutral scale.
	RenderingServer.global_shader_parameter_set("ringworld_terrain_albedo_scale", 1.0)
	RenderingServer.global_shader_parameter_set("ringworld_grass_albedo_scale", 1.0)
	viewport = SubViewport.new(); viewport.size = Vector2i(SIZE, SIZE); viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	# Keep combined illumination below display saturation: full-match blue
	# includes up to 1.2 brightness relief, so ambient 1 plus sun clips it.
	var environment := Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color.BLACK; environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color.WHITE; environment.ambient_light_energy = .35
	var world := WorldEnvironment.new(); world.environment = environment; viewport.add_child(world)
	var sun := DirectionalLight3D.new(); sun.light_energy = .2; sun.rotation_degrees = Vector3(-55, 25, 0); sun.shadow_enabled = false; viewport.add_child(sun)
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

