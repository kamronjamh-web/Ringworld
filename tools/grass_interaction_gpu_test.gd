extends SceneTree

# GPU evidence for the production grass interaction shader.  This deliberately
# reads rendered pixels only; the CPU field contract is exposed by the paired
# callable driver for a lightweight headless launcher.
const FACTORY := preload("res://tools/VegetationWindTestDriver.cs")
const CPU_DRIVER := preload("res://tools/GrassInteractionTestDriver.cs")
const SIZE := 256
const GRASS_PIXEL_MINIMUM := 50
const UPPER_CHANGED_MINIMUM := 20
const ROOT_CHANGED_MAXIMUM := 8

var viewport: SubViewport
var rig: Node3D
var camera: Camera3D
var grass: MultiMeshInstance3D
var material: ShaderMaterial
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("RenderingDevice required; Dummy renderer is not GPU evidence")
		return
	call_deferred("run")

func run() -> void:
	var cpu := CPU_DRIVER.new()
	root.add_child(cpu)
	if not bool(cpu.call("VerifyCpuContract")):
		fail("CPU interaction field contract")
		return
	cpu.queue_free()
	setup()
	# The production wind include remains installed, but the fixture's globals
	# make all wind displacement deterministic zero.
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector", Vector2.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave", Vector4.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_center", Vector3(0, 1000000, 0))
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis", Vector3(0, 0, 1))
	var calm := await render_state(false, 0, _payload(Vector4.ZERO))
	if not require(grass_pixels(calm) > GRASS_PIXEL_MINIMUM, "production mesh/shader rendered too few grass pixels"): return
	var disabled := await render_state(false, 1, _payload(Vector4(.45, 0, 0, 1)))
	if not require(pixel_difference(calm, disabled) < .002, "disabled interaction changed the calm render"): return
	var recovery := await render_state(true, 1, _payload(Vector4(.45, 0, 0, 0)))
	if not require(pixel_difference(calm, recovery) < .002, "zero-weight recovery sample did not return to baseline"): return
	var vertical := await render_state(true, 1, _payload(Vector4(.45, 5, 0, 1)))
	if not require(pixel_difference(calm, vertical) < .002, "vertically separated sample bent grass"): return
	var bent := await render_state(true, 1, _payload(Vector4(.45, 0, 0, 1)))
	var upper_changed := changed_pixels(calm, bent, 0, 140)
	var root_changed := changed_pixels(calm, bent, 150, SIZE)
	if not require(upper_changed >= UPPER_CHANGED_MINIMUM, "interaction did not visibly move the upper blade mask"): return
	if not require(root_changed <= ROOT_CHANGED_MAXIMUM, "interaction moved root pixels"): return
	if not require(mask_difference(calm, bent) >= UPPER_CHANGED_MINIMUM, "interaction mask change was vacuous"): return
	# The payload is world-space. Rebase the complete local frame and transform
	# its sample with it; production shader output must remain invariant.
	rig.transform = Transform3D(Basis(Vector3(1, 2, -1).normalized(), .73), Vector3(20000, -12000, 8000))
	var rebased_point := rig.to_global(Vector3(.45, 0, 0))
	var rebased_payload := _payload(Vector4(rebased_point.x, rebased_point.y, rebased_point.z, 1))
	var rebased := await render_state(true, 1, rebased_payload)
	if not require(pixel_difference(bent, rebased) < .012, "interaction changed after rotating/translating rig and payload together"): return
	rig.transform = Transform3D.IDENTITY
	var debug_ok := await verify_debug_modes(calm, bent)
	if not debug_ok: return
	print("GRASS_INTERACTION_GPU=PASS grass_pixels=", grass_pixels(calm), " upper_changed=", upper_changed, " root_changed=", root_changed)
	quit(0)

func verify_debug_modes(calm: Image, bent: Image) -> bool:
	# Debug Off is part of the regression contract: it must not perturb the
	# established interaction appearance or the calm/bent/rebase checks above.
	material.set_shader_parameter("grass_debug_mode", 0)
	var off_calm := await render_state(false, 0, _payload(Vector4.ZERO))
	if not require(pixel_difference(calm, off_calm) < .002, "debug Off did not restore the production calm baseline"): return false
	var off := await render_state(true, 1, _payload(Vector4(.45, 0, 0, 1)))
	if not require(pixel_difference(bent, off) < .002, "debug Off did not restore the production bent baseline"): return false
	material.set_shader_parameter("grass_debug_mode", 1)
	var lod := await render_state(false, 0, _payload(Vector4.ZERO))
	if not require(pixel_difference(off, lod) > .04 and color_pixels(lod, "red") > GRASS_PIXEL_MINIMUM, "LOD debug mode did not produce the near-pass color"): return false
	material.set_shader_parameter("grass_debug_mode", 2)
	var density := await render_state(false, 0, _payload(Vector4.ZERO))
	if not require(pixel_difference(lod, density) > .04 and color_pixels(density, "green") > GRASS_PIXEL_MINIMUM, "Density debug mode did not expose instance ranks"): return false
	material.set_shader_parameter("grass_debug_mode", 4)
	var interaction_zero := await render_state(true, 1, _payload(Vector4(.45, 0, 0, 0)))
	var interaction_full := await render_state(true, 1, _payload(Vector4(.45, 0, 0, 1)))
	if not require(pixel_difference(interaction_zero, interaction_full) > .012 and mean_channel(interaction_full, 0) > mean_channel(interaction_zero, 0) + .025, "Interaction debug mode did not respond to field magnitude"): return false
	material.set_shader_parameter("grass_debug_mode", 5)
	grass.set_instance_shader_parameter("grass_debug_tile_color", Vector3(1, .05, .05))
	var tiles_red := await render_state(false, 0, _payload(Vector4.ZERO))
	grass.set_instance_shader_parameter("grass_debug_tile_color", Vector3(.05, .05, 1))
	var tiles_blue := await render_state(false, 0, _payload(Vector4.ZERO))
	if not require(color_pixels(tiles_red, "red") > GRASS_PIXEL_MINIMUM and color_pixels(tiles_blue, "blue") > GRASS_PIXEL_MINIMUM and pixel_difference(tiles_red, tiles_blue) > .08, "Tiles debug mode ignored its per-instance tint"): return false
	material.set_shader_parameter("grass_debug_mode", 3)
	material.set_shader_parameter("grass_wind_strength_multiplier", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	var wind_calm := await render_state(false, 0, _payload(Vector4.ZERO))
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector", Vector2(12, 0))
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave", Vector4(1, 0, .4, .9))
	var wind_fixed := await render_state(false, 0, _payload(Vector4.ZERO))
	if not require(pixel_difference(wind_calm, wind_fixed) > .012 and silhouette_difference(wind_calm, wind_fixed) >= UPPER_CHANGED_MINIMUM, "Wind debug mode did not respond to fixed actual displacement"): return false
	# Leave global render state in the same calm, production-material condition
	# that the fixture started with, in case a test host runs another GPU scene.
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector", Vector2.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave", Vector4.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_center", Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis", Vector3.UP)
	material.set_shader_parameter("grass_wind_strength_multiplier", 0.0)
	material.set_shader_parameter("grass_debug_mode", 0)
	return true

func setup() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(SIZE, SIZE)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.WHITE
	environment.ambient_light_energy = 1.0
	var world := WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	rig = Node3D.new()
	viewport.add_child(rig)
	camera = Camera3D.new()
	camera.current = true
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 4.0
	camera.far = 128.0
	camera.position = Vector3(0, .5, 10)
	rig.add_child(camera)
	camera.look_at(Vector3(0, .5, 0), Vector3.UP)
	grass = MultiMeshInstance3D.new()
	grass.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var multi := MultiMesh.new()
	multi.transform_format = MultiMesh.TRANSFORM_3D
	multi.use_custom_data = true
	multi.mesh = FACTORY.new().call("LoadMeadowMesh") as Mesh
	multi.instance_count = 7
	for i in range(7):
		multi.set_instance_transform(i, Transform3D.IDENTITY.translated(Vector3((i - 3) * .55, 0, 0)))
		multi.set_instance_custom_data(i, Color(.2 + i * .07, .3, .1, 1))
	grass.multimesh = multi
	grass.set_instance_shader_parameter("grass_instance_count", 7.0)
	material = ShaderMaterial.new()
	material.shader = load("res://materials/ring_standing_grass.gdshader")
	material.set_shader_parameter("grass_base_color", Vector3(.12, .9, .18))
	material.set_shader_parameter("grass_tip_color", Vector3(.12, .9, .18))
	material.set_shader_parameter("local_cloud_shadow_enabled", false)
	material.set_shader_parameter("grass_lod_enabled", false)
	material.set_shader_parameter("grass_interaction_radius", 1.1)
	material.set_shader_parameter("grass_interaction_strength", 1.0)
	material.set_shader_parameter("grass_wind_strength_multiplier", 0.0)
	material.set_shader_parameter("grass_wind_gust_multiplier", 0.0)
	grass.material_override = material
	rig.add_child(grass)

func render_state(enabled: bool, count: int, points: PackedVector4Array) -> Image:
	material.set_shader_parameter("grass_interaction_enabled", enabled)
	material.set_shader_parameter("grass_interaction_count", count)
	material.set_shader_parameter("grass_interaction_points", points)
	for i in range(5): await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()

func _payload(first: Vector4) -> PackedVector4Array:
	var points := PackedVector4Array()
	points.resize(8)
	points[0] = first
	for i in range(1, 8): points[i] = Vector4.ZERO
	return points

func grass_pixels(image: Image) -> int:
	var pixels := 0
	for y in range(SIZE):
		for x in range(SIZE):
			if visible(image.get_pixel(x, y)): pixels += 1
	return pixels

func color_pixels(image: Image, channel: String) -> int:
	var pixels := 0
	for y in range(SIZE):
		for x in range(SIZE):
			var color := image.get_pixel(x, y)
			if channel == "red" and color.r > color.g * 1.5 and color.r > color.b * 1.5 and color.r > .1: pixels += 1
			if channel == "green" and color.g > color.r * 1.25 and color.g > color.b * 1.25 and color.g > .1: pixels += 1
			if channel == "blue" and color.b > color.r * 1.5 and color.b > color.g * 1.5 and color.b > .1: pixels += 1
	return pixels

func mean_channel(image: Image, channel: int) -> float:
	var total := 0.0
	var count := 0
	for y in range(SIZE):
		for x in range(SIZE):
			var color := image.get_pixel(x, y)
			if color.r > .02 or color.g > .02 or color.b > .02:
				total += [color.r, color.g, color.b][channel]
				count += 1
	return total / max(1, count)

func changed_pixels(a: Image, b: Image, from_y: int, to_y: int) -> int:
	var changed := 0
	for y in range(from_y, to_y):
		for x in range(SIZE):
			if pixel_delta(a.get_pixel(x, y), b.get_pixel(x, y)) > .08: changed += 1
	return changed

func mask_difference(a: Image, b: Image) -> int:
	var changed := 0
	for y in range(SIZE):
		for x in range(SIZE):
			if visible(a.get_pixel(x, y)) != visible(b.get_pixel(x, y)): changed += 1
	return changed

func silhouette_difference(a: Image, b: Image) -> int:
	var changed := 0
	for y in range(SIZE):
		for x in range(SIZE):
			if bright(a.get_pixel(x, y)) != bright(b.get_pixel(x, y)): changed += 1
	return changed

func pixel_difference(a: Image, b: Image) -> float:
	var total := 0.0
	for y in range(SIZE):
		for x in range(SIZE): total += pixel_delta(a.get_pixel(x, y), b.get_pixel(x, y))
	return total / float(SIZE * SIZE)

func pixel_delta(a: Color, b: Color) -> float:
	return absf(a.r - b.r) + absf(a.g - b.g) + absf(a.b - b.b)

func visible(color: Color) -> bool:
	return color.g > .08 and color.g > color.r * 1.35

func bright(color: Color) -> bool:
	return maxf(color.r, maxf(color.g, color.b)) > .08

func require(condition: bool, message: String) -> bool:
	if condition: return true
	fail(message)
	return false

func fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("GRASS_INTERACTION_GPU=FAIL " + message)
	quit(1)
