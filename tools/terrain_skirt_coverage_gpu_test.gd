extends SceneTree

## Isolated rendered regression for the production terrain and terrain-shadow
## vertex/discard paths. It never changes project shaders or scene resources.
const TERRAIN: Shader = preload("res://materials/ring_terrain_proxy.gdshader")
const SHADOW: Shader = preload("res://materials/ring_terrain_proxy_shadow.gdshader")
const RADIUS: float = 31415936.0 / TAU
const OUTPUT: String = "res://.godot/terrain_skirt_coverage"
var _world: Node3D
var _camera: Camera3D
var _terrain: MeshInstance3D
var _shadow: MeshInstance3D
var _covered: ImageTexture
var _uncovered: ImageTexture
var _phase: String = "boot"
var _captures: Dictionary = {}

func _initialize() -> void:
	_boot.call_deferred()

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	_covered = _mask(Color(1.0, 1.0, 1.0, 1.0))
	_uncovered = _mask(Color(0.0, 0.0, 0.0, 1.0))
	_world = Node3D.new(); root.add_child(_world)
	_camera = Camera3D.new(); _camera.projection = Camera3D.PROJECTION_ORTHOGONAL; _camera.size = 32.0; _camera.position = Vector3(0.0, 0.0, 18.0); _world.add_child(_camera); _camera.make_current()
	var light: DirectionalLight3D = DirectionalLight3D.new(); light.rotation_degrees = Vector3(-35.0, 20.0, 0.0); _world.add_child(light)
	_terrain = _fixture(_variant(TERRAIN, false)); _terrain.name = "TerrainFixture"; _world.add_child(_terrain)
	_shadow = _fixture(_variant(SHADOW, true)); _shadow.name = "ShadowFixture"; _shadow.position.x = 10.0; _world.add_child(_shadow)
	_phase = "capture"

func _process(_delta: float) -> bool:
	if _phase == "capture":
		_phase = "running"; _sequence.call_deferred()
	return false

func _sequence() -> void:
	# Covered must discard both top and UV2.y=1 skirt through the real prefix.
	_set_coverage(_covered)
	await RenderingServer.frame_post_draw
	if _red_pixels() != 0 or _green_pixels() != 0: _fail("covered terrain/skirt fixture rendered colored pixels"); return
	# Uncovered must render both production terrain and production-shadow variants.
	_set_coverage(_uncovered)
	await RenderingServer.frame_post_draw
	var uncovered: int = _red_pixels()
	if uncovered <= 0 or _green_pixels() <= 0: _fail("uncovered terrain or shadow skirt fixture had no visible pixels"); return
	_set_part(1); await RenderingServer.frame_post_draw
	if _red_pixels() <= 0 or _green_pixels() <= 0: _fail("uncovered top quad was not visible for terrain and shadow"); return
	_set_part(2); await RenderingServer.frame_post_draw
	if _red_pixels() <= 0 or _green_pixels() <= 0: _fail("uncovered UV2.y skirt quad was not visible for terrain and shadow"); return
	_set_part(0)
	var near_image: Image
	var complete_image: Image
	for distance: float in [0.0, 40.0, 80.0]:
		_set_morph_camera_distance(distance)
		await RenderingServer.frame_post_draw
		var top_only: Image = _image()
		if top_only.is_empty() or not _finite_image(top_only): _fail("non-finite or empty morph capture"); return
		# Marker-only toggle: top geometry captures must remain exactly identical.
		_set_skirt_marker(0.0)
		await RenderingServer.frame_post_draw
		var marker_zero: Image = _image()
		_set_skirt_marker(1.0)
		await RenderingServer.frame_post_draw
		var marker_one: Image = _image()
		if _red_pixels() <= 0 or _green_pixels() <= 0: _fail("marker capture was empty"); return
		if _image_difference(marker_zero, marker_one) != 0: _fail("UV2.y skirt marker altered morph geometry"); return
		if distance == 0.0: near_image = marker_one
		if distance == 80.0: complete_image = marker_one
		_captures[str(distance)] = {"red_pixels": _red_pixels(), "marker_delta": 0}
	if _image_difference(near_image, complete_image) < 3: _fail("morph endpoint did not move fixture pixels"); return
	_set_skirt_marker(1.0)
	var report: Dictionary = {"status":"pass", "certified_radius_m":RADIUS, "coverage":"covered=zero red; uncovered=nonempty red", "morph":_captures, "terrain_shader":TERRAIN.resource_path, "shadow_shader":SHADOW.resource_path}
	var file: FileAccess = FileAccess.open(ProjectSettings.globalize_path(OUTPUT + "/report.json"), FileAccess.WRITE)
	if file == null: _fail("report open failed"); return
	file.store_string(JSON.stringify(report, "\t")); print("TERRAIN_SKIRT_COVERAGE_GPU_TEST=PASS ", report); _world.queue_free(); quit(0)

func _fixture(material: ShaderMaterial) -> MeshInstance3D:
	var arrays: Array = []; arrays.resize(Mesh.ARRAY_MAX)
	# Clockwise as viewed from +Z yields the front-facing +Z normal under Godot's
	# terrain winding convention. Top and skirt share valid coverage UVs.
	arrays[Mesh.ARRAY_VERTEX] = PackedVector3Array([Vector3(-4,-4,0),Vector3(4,-4,0),Vector3(4,4,0),Vector3(-4,4,0),Vector3(-7,-4,0),Vector3(-7,4,0)])
	arrays[Mesh.ARRAY_NORMAL] = PackedVector3Array([Vector3(0,0,1),Vector3(0,0,1),Vector3(0,0,1),Vector3(0,0,1),Vector3(0,0,1),Vector3(0,0,1)])
	arrays[Mesh.ARRAY_TEX_UV] = PackedVector2Array([Vector2(0,0),Vector2(1,0),Vector2(1,1),Vector2(0,1),Vector2(0,0),Vector2(0,1)])
	arrays[Mesh.ARRAY_TEX_UV2] = PackedVector2Array([Vector2(1,0),Vector2(1,0),Vector2(1,0),Vector2(1,0),Vector2(1,1),Vector2(1,1)])
	arrays[Mesh.ARRAY_INDEX] = PackedInt32Array([0,2,1,0,3,2,0,4,5,0,5,3])
	var mesh: ArrayMesh = ArrayMesh.new(); mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays); mesh.surface_set_material(0, material)
	var node: MeshInstance3D = MeshInstance3D.new(); node.mesh = mesh; return node

func _variant(source: Shader, shadow: bool) -> ShaderMaterial:
	var code: String = source.code.replace("render_mode cull_back, depth_draw_opaque;", "render_mode unshaded, cull_back, depth_draw_opaque;")
	if shadow:
		var anchor: String = "\t\t&& texture(coverage_atlas, coverage_atlas_rect.xy + min(proxy_coverage_uv, vec2(0.99999)) * coverage_atlas_rect.zw).r > 0.5) discard;\n}"
		if not code.contains(anchor):
			push_error("terrain shadow discard anchor changed")
		code = code.replace(anchor, "\t\t&& texture(coverage_atlas, coverage_atlas_rect.xy + min(proxy_coverage_uv, vec2(0.99999)) * coverage_atlas_rect.zw).r > 0.5) discard;\n\tALBEDO = vec3(0.0, 1.0, 0.0);\n}")
	else:
		var fragment_start: int = code.find("void fragment() {")
		var terrain_color: int = code.find("\tvec3 terrain_color", fragment_start)
		if fragment_start < 0 or terrain_color < 0:
			push_error("terrain fragment discard prefix anchor changed")
		else:
			code = code.substr(0, terrain_color) + "\tALBEDO = vec3(1.0, 0.0, 0.0);\n}\n"
	var shader: Shader = Shader.new(); shader.code = code
	var material: ShaderMaterial = ShaderMaterial.new(); material.shader = shader
	material.set_shader_parameter("coverage_atlas", _uncovered); material.set_shader_parameter("camera_world_position", Vector3(0,0,0)); material.set_shader_parameter("ring_center", Vector3(RADIUS,0,0)); material.set_shader_parameter("geomorph_enabled", true); material.set_shader_parameter("morph_start_meters", 20.0); material.set_shader_parameter("morph_end_meters", 100.0); material.set_shader_parameter("near_fade_enabled", false); material.set_shader_parameter("far_fade_enabled", false)
	if not shadow: material.set_shader_parameter("debug_color_enabled", true); material.set_shader_parameter("debug_color", Vector3(1,0,0)); material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	return material

func _set_coverage(texture: ImageTexture) -> void:
	for node: MeshInstance3D in [_terrain, _shadow]:
		var material: ShaderMaterial = node.mesh.surface_get_material(0) as ShaderMaterial; material.set_shader_parameter("coverage_atlas", texture); node.set_instance_shader_parameter("coverage_atlas_rect", Vector4(0,0,1,1))

func _set_morph_camera_distance(distance: float) -> void:
	for node: MeshInstance3D in [_terrain, _shadow]:
		var material: ShaderMaterial = node.mesh.surface_get_material(0) as ShaderMaterial
		material.set_shader_parameter("camera_world_position", Vector3(0.0, 0.0, distance))

func _set_skirt_marker(value: float) -> void:
	for node: MeshInstance3D in [_terrain, _shadow]:
		var arrays: Array = node.mesh.surface_get_arrays(0); var uv2: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV2]; uv2[4].y = value; uv2[5].y = value; arrays[Mesh.ARRAY_TEX_UV2] = uv2; var replacement: ArrayMesh = ArrayMesh.new(); replacement.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays); replacement.surface_set_material(0, node.mesh.surface_get_material(0)); node.mesh = replacement

func _set_part(part: int) -> void:
	var indices: PackedInt32Array = PackedInt32Array([0,2,1,0,3,2]) if part == 1 else PackedInt32Array([0,4,5,0,5,3]) if part == 2 else PackedInt32Array([0,2,1,0,3,2,0,4,5,0,5,3])
	for node: MeshInstance3D in [_terrain, _shadow]:
		var arrays: Array = node.mesh.surface_get_arrays(0); arrays[Mesh.ARRAY_INDEX] = indices; var replacement: ArrayMesh = ArrayMesh.new(); replacement.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays); replacement.surface_set_material(0, node.mesh.surface_get_material(0)); node.mesh = replacement

func _mask(color: Color) -> ImageTexture:
	var image: Image = Image.create(1,1,false,Image.FORMAT_RGBA8); image.fill(color); return ImageTexture.create_from_image(image)

func _image() -> Image:
	return root.get_texture().get_image()

func _red_pixels() -> int:
	var image: Image = _image(); var count: int = 0
	for y: int in range(0,image.get_height(),2):
		for x: int in range(0,image.get_width(),2):
			var pixel: Color = image.get_pixel(x,y)
			if pixel.r > .75 and pixel.g < .2 and pixel.b < .2: count += 1
	return count

func _green_pixels() -> int:
	var image: Image = _image(); var count: int = 0
	for y: int in range(0,image.get_height(),2):
		for x: int in range(0,image.get_width(),2):
			var pixel: Color = image.get_pixel(x,y)
			if pixel.g > .75 and pixel.r < .2 and pixel.b < .2: count += 1
	return count

func _finite_image(image: Image) -> bool:
	if image.get_width() <= 0 or image.get_height() <= 0: return false
	for y: int in range(0, image.get_height(), 8):
		for x: int in range(0, image.get_width(), 8):
			var pixel: Color = image.get_pixel(x, y)
			if is_nan(pixel.r) or is_nan(pixel.g) or is_nan(pixel.b) or is_inf(pixel.r) or is_inf(pixel.g) or is_inf(pixel.b): return false
	return true

func _image_difference(a: Image, b: Image) -> int:
	if a.get_size() != b.get_size(): return 1
	var different: int = 0
	for y: int in range(0,a.get_height(),4):
		for x: int in range(0,a.get_width(),4):
			if a.get_pixel(x,y) != b.get_pixel(x,y): different += 1
	return different

func _fail(reason: String) -> void:
	push_error("TERRAIN_SKIRT_COVERAGE_GPU_TEST=FAIL " + reason)
	if is_instance_valid(_world): _world.queue_free()
	quit(1)
