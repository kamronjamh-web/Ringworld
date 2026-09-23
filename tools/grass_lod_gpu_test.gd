extends SceneTree

# Isolated Forward+ evidence for the production grass shader's paired LOD
# passes. Red is the four-section near mesh, green the two-section mesh.
const FACTORY := preload("res://tools/VegetationWindTestDriver.cs")
const SIZE := 256
var viewport: SubViewport
var rig: Node3D
var camera: Camera3D
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("RenderingDevice required; Dummy rendering is not GPU evidence")
		return
	call_deferred("run")

func run() -> void:
	setup()
	var near := make_pass(FACTORY.new().call("LoadMeadowMesh"), 0, Vector3(1, .02, .02))
	var middle := make_pass(FACTORY.new().call("LoadIntermediateGrassMesh"), 1, Vector3(.02, 1, .02))
	rig.add_child(near)
	rig.add_child(middle)
	set_camera_distance(12.0)
	var close := await capture()
	var close_counts := colors(close)
	if not require(close_counts.red > 20 and close_counts.green == 0, "near pass did not exclusively cover roots"): return
	(near.material_override as ShaderMaterial).set_shader_parameter("grass_debug_mode", 5)
	(middle.material_override as ShaderMaterial).set_shader_parameter("grass_debug_mode", 5)
	near.set_instance_shader_parameter("grass_debug_tile_color", Vector3(1, .02, .02))
	middle.set_instance_shader_parameter("grass_debug_tile_color", Vector3(1, .02, .02))
	var debug_near := await capture()
	if not require(bright_pixels(debug_near) > 20, "debug tile mode was vacuous before far cutoff"): return
	(near.material_override as ShaderMaterial).set_shader_parameter("grass_debug_mode", 0)
	(middle.material_override as ShaderMaterial).set_shader_parameter("grass_debug_mode", 0)
	set_camera_distance(30.0)
	# Render each pass independently, then together. With the fixture roots
	# deliberately separated, overlapping masks expose a root selected by both
	# passes and a joint/union disagreement exposes a transition hole.
	middle.visible = false
	var near_only := await capture()
	near.visible = false
	middle.visible = true
	var middle_only := await capture()
	near.visible = true
	var handoff := await capture()
	var handoff_counts := colors(handoff)
	if not require(handoff_counts.red > 5 and handoff_counts.green > 5, "paired root-dither handoff lacks both passes"): return
	if not require(mask_overlap(near_only, middle_only) == 0, "a root was selected by both LOD passes"): return
	if not require(mask_disagreement(near_only, middle_only, handoff) == 0, "paired LOD passes left a transition hole"): return
	set_camera_distance(60.0)
	var far := await capture()
	var far_counts := colors(far)
	if not require(far_counts.red == 0 and far_counts.green > 20, "intermediate pass did not replace near geometry"): return
	# Rebase and rotate the complete local frame. Canonical INSTANCE_CUSTOM
	# seeds must choose the same roots, independent of MODEL_MATRIX position.
	rig.transform = Transform3D(Basis(Vector3(1, 2, -1).normalized(), .71), Vector3(20000, -12000, 8000))
	var rebased := await capture()
	if not require(pixel_difference(far, rebased) < .012, "LOD root selection changed after frame rebase/rotation"): return
	rig.transform = Transform3D.IDENTITY
	set_camera_distance(96.0)
	# Fragment discard must precede every debug visualization. If a debug tint
	# leaked past the final cutoff, this red tile-mode capture would expose it.
	(near.material_override as ShaderMaterial).set_shader_parameter("grass_debug_mode", 5)
	(middle.material_override as ShaderMaterial).set_shader_parameter("grass_debug_mode", 5)
	near.set_instance_shader_parameter("grass_debug_tile_color", Vector3(1, .02, .02))
	middle.set_instance_shader_parameter("grass_debug_tile_color", Vector3(1, .02, .02))
	var cutoff := await capture()
	if not require(bright_pixels(cutoff) == 0, "debug visualization bypassed far terrain cutoff"): return
	print("GRASS_LOD_GPU=PASS paired complement, near/intermediate meshes, far cutoff, rebase")
	quit(0)

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
	camera.size = 7.0
	camera.far = 160.0
	rig.add_child(camera)

func make_pass(mesh: Mesh, lod_pass: int, tint: Vector3) -> MultiMeshInstance3D:
	var node := MultiMeshInstance3D.new()
	node.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var multi := MultiMesh.new()
	multi.transform_format = MultiMesh.TRANSFORM_3D
	multi.use_custom_data = true
	multi.mesh = mesh
	multi.instance_count = 25
	for z in range(5):
		for x in range(5):
			var index := z * 5 + x
			# The production dense ribbon is already normalized to a .065 m root
			# half-width. Keep its local width near one metre of prototype scale;
			# the old .07 scale made this GPU fixture sub-pixel at 7 m ortho size.
			# Separate root silhouettes so unrelated occluding blades cannot be
			# mistaken for duplicate ownership of the same root.
			multi.set_instance_transform(index, Transform3D(Basis(Vector3.UP, float(index) * .37).scaled(Vector3(1.0, .8, 1.0)), Vector3((x - 2) * 1.2, (z - 2) * 1.2, 0)))
			multi.set_instance_custom_data(index, Color(.15 + float(index) * .031, .2 + float(index) * .023, .1, .7))
	node.multimesh = multi
	var material := ShaderMaterial.new()
	material.shader = load("res://materials/ring_standing_grass.gdshader")
	material.set_shader_parameter("grass_base_color", tint)
	material.set_shader_parameter("grass_tip_color", tint)
	material.set_shader_parameter("local_cloud_shadow_enabled", false)
	material.set_shader_parameter("grass_lod_enabled", true)
	material.set_shader_parameter("grass_lod_near_distance", 30.0)
	material.set_shader_parameter("grass_lod_far_distance", 90.0)
	material.set_shader_parameter("grass_lod_transition_width", 10.0)
	material.set_shader_parameter("grass_lod_near_density", 1.0)
	material.set_shader_parameter("grass_lod_far_density", 1.0)
	node.material_override = material
	node.set_instance_shader_parameter("grass_lod_pass", lod_pass)
	node.set_instance_shader_parameter("grass_instance_count", 25.0)
	return node

func set_camera_distance(distance: float) -> void:
	camera.position = Vector3(0, .5, distance)
	camera.look_at(Vector3(0, .5, 0), Vector3.UP)

func capture() -> Image:
	for i in range(4): await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()

func colors(image: Image) -> Dictionary:
	var red := 0
	var green := 0
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var color := image.get_pixel(x, y)
			if color.r > color.g * 1.5 and color.r > .08: red += 1
			if color.g > color.r * 1.5 and color.g > .08: green += 1
	return {"red": red, "green": green}

func pixel_difference(a: Image, b: Image) -> float:
	var total := 0.0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			var first := a.get_pixel(x, y)
			var second := b.get_pixel(x, y)
			total += absf(first.r - second.r) + absf(first.g - second.g) + absf(first.b - second.b)
	return total / float(a.get_width() * a.get_height())

func mask_overlap(a: Image, b: Image) -> int:
	var overlap := 0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			if visible_pixel(a.get_pixel(x, y)) and visible_pixel(b.get_pixel(x, y)): overlap += 1
	return overlap

func mask_disagreement(a: Image, b: Image, joint: Image) -> int:
	var mismatch := 0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			var expected := visible_pixel(a.get_pixel(x, y)) or visible_pixel(b.get_pixel(x, y))
			if expected != visible_pixel(joint.get_pixel(x, y)): mismatch += 1
	return mismatch

func visible_pixel(color: Color) -> bool:
	return color.r > .05 or color.g > .05 or color.b > .05

func bright_pixels(image: Image) -> int:
	var pixels := 0
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var color := image.get_pixel(x, y)
			if maxf(color.r, maxf(color.g, color.b)) > .08: pixels += 1
	return pixels

func require(condition: bool, message: String) -> bool:
	if condition: return true
	fail(message)
	return false

func fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("GRASS_LOD_GPU=FAIL " + message)
	quit(1)
