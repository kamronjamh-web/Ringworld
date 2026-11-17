extends SceneTree
# T4-3 ground surface (rock, strata, scree) in the Terrain3D surface and the proxy sector material, on synthetic receivers.
#
#   check       : godot --path . --script tools/terrain_rock_material_gpu_test.gd            (windowed Vulkan, not --headless)
#   capture-pre : godot --path . --script tools/terrain_rock_material_gpu_test.gd -- --capture-pre
#                 Feature-off reference images of the Terrain3D receiver, a mesh-installed proxy receiver and a ring_terrain_global receiver, taken BEFORE any shader edit
#                 (T4 design D7), under .godot/audit_baseline/rtx4070/t4/pre/ (the pruner keeps audit_baseline). The check mode compares against them byte for byte
#                 with the feature bit cleared, when they exist.
#   options     : --pre-dir=<dir>   override the reference directory
#                 --break=rock      raise the rock start by 10 degrees, to 48 (the weight checks must then FAIL)
#                 --break=strata    offset the proxy receiver by half a bed (the strata checks must then FAIL)
#
# Checks:
#  1. GLSL against the C# mirror: rock exposure, scree and the strata factor at 64 (slope, baseline) and 64 (height, footprint) pairs, 16-bit readback through a canvas shader that
#     includes materials/ring_ground_surface.gdshaderinc (tolerance 1e-4).
#  2. Both near tiers, through the ground debug view (ringworld_ground_look.w = 1: EMISSION = rock, scree, strata factor / 2), on a 10 degree, a 33 degree and a 50 degree face
#     viewed with an orthographic camera, so a screen row is an absolute height: rock above 0.9 at 50 and 0 at 10, scree 0.4 at 33 on grass ground, and the strata factor per row
#     equal to the C# mirror at that height (Terrain3D and the proxy sector derive the height by different routes, S2/S3).
#  3. Feature off: the receivers render byte-identically to the pre-edit references.
# Prints TERRAIN_ROCK_MATERIAL=PASS.
const SURFACE = preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const GEOMETRY = preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
const MODEL_BRIDGE = preload("res://tools/GroundSurfaceModelBridge.cs")

const WINDOW := 384
const REGION := 128
const VIEW := Vector2i(480, 270)
const MAX_HEIGHT := 64.0
const SETTLE_FRAMES := 20
const DEFAULT_PRE_DIR := "res://.godot/audit_baseline/rtx4070/t4/pre"
const RING_RADIUS := 5000000.0
const BASE_HEIGHT := 8.0
const PROXY_BASE := 2400.0
const FACE_START := 20.0
const FACE_SPAN := 40.0
const ORTHO_SIZE := 70.0
const STRATA_STEP := 40.0
const STRATA_SEED := 1234
const FEATURE_BIT := 4 # 1 << 2

var failures: PackedStringArray = []
var _viewport: SubViewport
var _camera: Camera3D
var _sun: DirectionalLight3D
var _surface: Node3D
var _proxy: MeshInstance3D
var _proxy_material: ShaderMaterial
var _far: MeshInstance3D
var _far_material: ShaderMaterial
var _pre_dir := DEFAULT_PRE_DIR
var _capture_pre := false
var _break := ""
var _finished := false
var MODEL: RefCounted


func _init() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg == "--capture-pre":
			_capture_pre = true
		elif arg.begins_with("--pre-dir="):
			_pre_dir = arg.substr(10)
		elif arg.begins_with("--break="):
			_break = arg.substr(8)
	call_deferred("_run")
	create_timer(300.0).timeout.connect(func() -> void: _fail("timeout"))


# ------------------------------------------------------------------ face profile

# Slope of the face along its ramp at lateral position z (m): 10 degrees, then 33, then 50, blended over 20 m.
func _tan_slope(z: float) -> float:
	var t10 := tan(deg_to_rad(10.0))
	var t33 := tan(deg_to_rad(33.0))
	var t50 := tan(deg_to_rad(50.0))
	return lerpf(lerpf(t10, t33, smoothstep(-30.0, -10.0, z)), t50, smoothstep(10.0, 30.0, z))


func _face_height(d: float, z: float) -> float:
	return BASE_HEIGHT + _tan_slope(z) * clampf(d - FACE_START, 0.0, FACE_SPAN)


# ------------------------------------------------------------------ scene

func _run() -> void:
	MODEL = MODEL_BRIDGE.new()
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
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
	_sun.name = "Sun"
	_sun.shadow_enabled = false
	_viewport.add_child(_sun)
	_camera = Camera3D.new()
	_camera.current = true
	_camera.fov = 70.0
	_camera.far = 2000.0
	_viewport.add_child(_camera)

	RenderingServer.global_shader_parameter_set("ring_wet_params", Vector4(1, 0.18, 1, 0))
	RenderingServer.global_shader_parameter_set("ringworld_ground_world", Vector4(STRATA_STEP, STRATA_SEED, 0, 0))

	# The near shaders are picked when their materials are created (RingGroundSurfaceVariants): a world without ground.surface gets the shipped shaders (generation 1),
	# a world with it the *_ground variants.
	if not await _build_receivers(false):
		return
	if _capture_pre:
		await _capture_or_compare_identity(true, "")
		print("TERRAIN_ROCK_MATERIAL=CAPTURED dir=%s" % _pre_dir)
		await _finish(0)
		return
	await _capture_or_compare_identity(false, "shipped shaders, no feature")
	await _teardown_receivers()
	if not await _build_receivers(true):
		return
	await _capture_or_compare_identity(false, "ground variants, mask cleared")
	await _check_probe()
	await _check_tiers()
	if failures.is_empty():
		print("TERRAIN_ROCK_MATERIAL=PASS")
		await _finish(0)
	else:
		push_error("TERRAIN_ROCK_MATERIAL=FAIL " + "; ".join(failures))
		await _finish(1)


func _build_receivers(feature_world: bool) -> bool:
	MODEL.call("PublishMask", FEATURE_BIT if feature_world else 0)
	_surface = SURFACE.new()
	_viewport.add_child(_surface)
	if not await _prepare_surface():
		return false
	_build_proxy()
	_build_far()
	print("TERRAIN_ROCK_MATERIAL receivers built for a %s world (proxy shader %s)" % ["feature" if feature_world else "plain", _proxy_material.shader.resource_path])
	return true


func _teardown_receivers() -> void:
	for node in [_surface, _proxy, _far]:
		node.queue_free()
	for frame in 6:
		await process_frame


func _prepare_surface() -> bool:
	var heights := PackedFloat32Array()
	heights.resize(WINDOW * WINDOW)
	for z in WINDOW:
		for x in WINDOW:
			heights[z * WINDOW + x] = _face_height((x - REGION) * 2.0, (z - REGION) * 2.0)
	var height_images: Array[Image] = []
	var biome_images: Array[Image] = []
	for region in 9:
		var region_x := region % 3
		var region_z := region / 3
		var bytes := PackedByteArray()
		bytes.resize(REGION * REGION * 4)
		var biome := Image.create_empty(REGION, REGION, false, Image.FORMAT_RGBA8)
		biome.fill(Color(0, 0, 0, 0)) # grass everywhere: rock then replaces the grass surface, and the proxy has to sample alpine itself
		for row in REGION:
			for column in REGION:
				bytes.encode_float((row * REGION + column) * 4, heights[(region_z * REGION + row) * WINDOW + region_x * REGION + column])
		height_images.append(Image.create_from_data(REGION, REGION, false, Image.FORMAT_RF, bytes))
		biome_images.append(biome)
	var geometry := GEOMETRY.new()
	if not geometry.call("PrepareGameplay", heights, 0, 159000, 0.0, -1, 0.0, MAX_HEIGHT, 32):
		_fail("prepare collision geometry")
		return false
	if not _surface.call("BeginGameplayPreparedWindow", heights, height_images, biome_images, geometry, 0, 159000, 0.0, -1, 1, 0.0, MAX_HEIGHT):
		_fail("begin prepared window")
		return false
	var ready := false
	for frame in 480:
		var state: Dictionary = _surface.call("AdvanceBudgetedPreparation", 0.5, 0.5)
		if not bool(state.get("ok", false)):
			_fail("prepared state invalid: %s" % state)
			return false
		if bool(state.get("ready", false)):
			ready = true
			break
		await process_frame
	if not ready:
		_fail("prepared window did not become ready")
		return false
	if not _surface.call("ActivateStagedWindow", 1):
		_fail("activate prepared window")
		return false
	await physics_frame
	return true


func _terrain_material() -> Object:
	var terrain: Node = _surface.get("TerrainNode")
	return terrain.call("get_material")


# Proxy receiver: a mesh-installed sector-style grid. The ring centre is at +X, so the ring up is +X and a vertex's absolute height is its X above the model origin
# (canonical height 0). The face rises along +Y (the ring axis) and varies along Z.
func _build_proxy() -> void:
	var half_lat := 64.0
	var cells_lat := int(half_lat * 2.0 / 2.0)
	var cells_d := 40 + 4
	var vertices := PackedVector3Array()
	var normals := PackedVector3Array()
	var colors := PackedColorArray()
	var indices := PackedInt32Array()
	var d0 := 0.0
	for i in cells_d + 1:
		var d := d0 + i * 2.0
		for j in cells_lat + 1:
			var z := -half_lat + j * 2.0
			var h := _face_height(d, z)
			vertices.append(Vector3(PROXY_BASE + h, d, z))
			var eps := 0.5
			var hd := (_face_height(d + eps, z) - _face_height(d - eps, z)) / (2.0 * eps)
			var hz := (_face_height(d, z + eps) - _face_height(d, z - eps)) / (2.0 * eps)
			# up = +X, depth = +Y, lateral = +Z: the gradient of the height surface.
			normals.append(Vector3(1.0, -hd, -hz).normalized())
			colors.append(Color(0, 0, 0, 0))
	for i in cells_d:
		for j in cells_lat:
			var a := i * (cells_lat + 1) + j
			var b := a + 1
			var c := a + cells_lat + 1
			var e := c + 1
			indices.append_array([a, b, c, b, e, c])
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = vertices
	arrays[ArrayMesh.ARRAY_NORMAL] = normals
	arrays[ArrayMesh.ARRAY_COLOR] = colors
	arrays[ArrayMesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	mesh.custom_aabb = AABB(Vector3(PROXY_BASE - 10, -10, -100), Vector3(200, 200, 200))
	_proxy = MeshInstance3D.new()
	_proxy.mesh = mesh
	_proxy_material = ShaderMaterial.new()
	_proxy_material.shader = load(str(MODEL.call("ProxyShaderPath"))) as Shader
	_proxy.material_override = _proxy_material
	_proxy.visible = false
	_viewport.add_child(_proxy)
	var native := _terrain_material()
	for parameter in ["biome_grass_albedo", "biome_dirt_albedo", "biome_desert_albedo", "biome_forest_albedo", "biome_tundra_albedo", "biome_alpine_albedo"]:
		_proxy_material.set_shader_parameter(parameter, native.call("get_shader_param", parameter))
	_proxy_material.set_shader_parameter("biome_texture_weights_enabled", true)
	_proxy_material.set_shader_parameter("biome_texture_scale", 0.20)
	_proxy_material.set_shader_parameter("ring_center", Vector3(RING_RADIUS, 0, 0))
	_proxy_material.set_shader_parameter("near_fade_enabled", false)
	_proxy_material.set_shader_parameter("far_fade_enabled", false)
	_proxy_material.set_shader_parameter("geomorph_enabled", false)
	_proxy_material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	_proxy_material.set_shader_parameter("terrain_macro_shadow_enabled", false)
	_proxy_material.set_shader_parameter("sun_direction", Vector3(0.45, 0.2, 0.3).normalized())
	_proxy_material.set_shader_parameter("roughness_value", 0.92)


func _build_far() -> void:
	var e := 40.0
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = PackedVector3Array([Vector3(PROXY_BASE, 0, -e), Vector3(PROXY_BASE + 2.0 * e, 0, -e), Vector3(PROXY_BASE + 2.0 * e, 0, e), Vector3(PROXY_BASE, 0, e)])
	arrays[ArrayMesh.ARRAY_NORMAL] = PackedVector3Array([Vector3.RIGHT, Vector3.RIGHT, Vector3.RIGHT, Vector3.RIGHT])
	arrays[ArrayMesh.ARRAY_COLOR] = PackedColorArray([Color(0.30, 0.34, 0.22), Color(0.45, 0.40, 0.30), Color(0.55, 0.52, 0.50), Color(0.35, 0.30, 0.25)])
	arrays[ArrayMesh.ARRAY_INDEX] = PackedInt32Array([0, 1, 2, 0, 2, 3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	_far = MeshInstance3D.new()
	_far.mesh = mesh
	_far_material = ShaderMaterial.new()
	_far_material.shader = load("res://materials/ring_terrain_global.gdshader") as Shader
	_far_material.set_shader_parameter("ring_center", Vector3(RING_RADIUS, 0, 0))
	_far_material.set_shader_parameter("sun_direction", Vector3(0.45, 0.2, 0.3).normalized())
	_far_material.set_shader_parameter("wet_world_receiver", false)
	_far_material.set_shader_parameter("fade_start_meters", -1.0)
	_far_material.set_shader_parameter("fade_end_meters", 0.0)
	_far_material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	_far.material_override = _far_material
	_far.visible = false
	_viewport.add_child(_far)


# ------------------------------------------------------------------ views

func _show_only(which: String) -> void:
	_surface.visible = which == "t3d"
	_proxy.visible = which == "proxy"
	_far.visible = which == "far"


func _orient_sun(to_light: Vector3) -> void:
	var direction := to_light.normalized()
	var up_hint := Vector3.RIGHT if absf(direction.dot(Vector3.UP)) > 0.95 else Vector3.UP
	_sun.transform = Transform3D(Basis.looking_at(-direction, up_hint), Vector3.ZERO)


func _set_mask(on: bool) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_world_features", FEATURE_BIT if on else 0)


func _set_look(rock_start: float, contrast: float, debug: bool) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_ground_look", Vector4(rock_start, contrast, 1.0, 1.0 if debug else 0.0))


func _settle() -> void:
	for frame in SETTLE_FRAMES:
		_surface.call("UpdateCamera", _camera.global_position)
		await process_frame


func _grab() -> Image:
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("viewport readback failed")
		return Image.new()
	image.convert(Image.FORMAT_RGBA8)
	return image


# Perspective view of one receiver with ordinary lighting (the identity images).
func _lit_view(which: String) -> Image:
	_show_only(which)
	_camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	_camera.fov = 70.0
	_orient_sun(Vector3(0.30, 0.85, 0.20))
	if which == "t3d":
		_camera.position = Vector3(-30, 22, 0)
		_camera.look_at(Vector3(40, 30, 0), Vector3.UP)
		_surface.call("SetSurfaceLighting", -0.2, 0.35, Color(0.30, 0.45, 0.70), 0.12, 1.15, 0.10)
	elif which == "proxy":
		_camera.position = Vector3(PROXY_BASE + 22, -30, 0)
		_camera.look_at(Vector3(PROXY_BASE + 30, 40, 0), Vector3.RIGHT)
		_proxy_material.set_shader_parameter("camera_world_position", _camera.position)
	else:
		_camera.position = Vector3(PROXY_BASE + 60, -60, 0)
		_camera.look_at(Vector3(PROXY_BASE, 0, 0), Vector3.RIGHT)
		_far_material.set_shader_parameter("camera_world_position", _camera.position)
	await _settle()
	return await _grab()


func _capture_or_compare_identity(capture: bool, label: String) -> void:
	_set_look(38.0, 0.12, false)
	_set_mask(false)
	var dir_abs := ProjectSettings.globalize_path(_pre_dir)
	if capture:
		DirAccess.make_dir_recursive_absolute(dir_abs)
	var compared := 0
	for which in ["t3d", "proxy", "far"]:
		var image := await _lit_view(which)
		if failed():
			return
		var path := dir_abs.path_join("rock_pre_%s.png" % which)
		if capture:
			image.save_png(path)
			continue
		var reference := Image.load_from_file(path)
		if reference == null or reference.is_empty():
			print("TERRAIN_ROCK_MATERIAL identity %s: SKIP (no pre-edit reference at %s)" % [which, path])
			continue
		reference.convert(Image.FORMAT_RGBA8)
		var differing := _count_differences(image, reference)
		compared += 1
		print("TERRAIN_ROCK_MATERIAL identity %s (%s): differing pixels %d" % [which, label, differing])
		_check(differing == 0, "%s: %s receiver differs from the pre-edit capture in %d pixels" % [label, which, differing])
	_set_mask(false)


func _count_differences(a: Image, b: Image) -> int:
	if a.get_size() != b.get_size():
		return a.get_width() * a.get_height()
	var da := a.get_data()
	var db := b.get_data()
	var count := 0
	for i in range(0, da.size(), 4):
		if da[i] != db[i] or da[i + 1] != db[i + 1] or da[i + 2] != db[i + 2] or da[i + 3] != db[i + 3]:
			count += 1
	return count


# ------------------------------------------------------------------ check 1: GLSL against the C# mirror

const PROBE_SHADER := """
shader_type canvas_item;
render_mode blend_disabled;
#include "res://materials/ring_ground_surface.gdshaderinc"
uniform vec4 pairs[64]; // rows 0 and 1: slope deg, baseline m, relief, vegetated; row 2: height m, footprint m, 0, 0
vec2 pack16(float v) {
	float q = floor(clamp(v, 0.0, 0.99998) * 65535.0 + 0.5);
	return vec2(floor(q / 256.0), mod(q, 256.0)) / 255.0;
}
void fragment() {
	int i = int(FRAGCOORD.x);
	int row = int(FRAGCOORD.y);
	vec4 p = pairs[i];
	float v = 0.0;
	if (row == 0) v = ring_ground_rock_exposure(p.x, p.y, p.z, p.w * 0.5);
	else if (row == 1) v = ring_ground_scree(p.x, p.y, p.z, p.w);
	else v = ring_ground_strata_factor(p.x, p.y) * 0.5;
	COLOR = vec4(pack16(v), 0.0, 1.0);
}
"""


func _check_probe() -> void:
	var shader := Shader.new()
	shader.code = PROBE_SHADER
	var material := ShaderMaterial.new()
	material.shader = shader
	var rect := ColorRect.new()
	rect.size = Vector2(64, 3)
	rect.material = material
	var view := SubViewport.new()
	view.size = Vector2i(64, 3)
	view.transparent_bg = false
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	view.add_child(rect)
	root.add_child(view)
	_set_look(48.0 if _break == "rock" else 38.0, 0.12, false)
	var rock_start := 48.0 if _break == "rock" else 38.0
	var pairs := PackedVector4Array()
	var rng := RandomNumberGenerator.new()
	rng.seed = 4321
	var heights := PackedFloat64Array()
	var footprints := PackedFloat64Array()
	for i in 64:
		pairs.append(Vector4(rng.randf_range(0.0, 90.0), [2.0, 4.0, 8.0, 16.0, 32.0, 64.0, 128.0, 4096.0][i % 8], rng.randf_range(-1.0, 1.0), rng.randf_range(0.0, 1.0)))
	# Row 1 reuses the same uniform array: x = height, y = footprint (set after the rows 0 values are consumed by the CPU side).
	var strata_pairs := PackedVector4Array()
	for i in 64:
		var h := rng.randf_range(-3000.0, 3000.0)
		var fw: float = [0.0, 0.05, 0.3, 1.0, 5.0, 20.0, 40.0, 80.0][i % 8]
		heights.append(h)
		footprints.append(fw)
		strata_pairs.append(Vector4(h, fw, 0, 0))
	# One shader reads both rows from the same array, so run two passes.
	var rock_result := await _run_probe(view, material, pairs, 0)
	var scree_result := await _run_probe(view, material, pairs, 1)
	var strata_result := await _run_probe(view, material, strata_pairs, 2)
	view.queue_free()
	var worst := 0.0
	for i in 64:
		var p := pairs[i]
		var expected_rock: float = MODEL.call("RockExposure", p.x, p.y, p.z, p.w * 0.5, rock_start)
		var expected_scree: float = MODEL.call("ScreeWeight", p.x, p.y, p.z, p.w)
		worst = maxf(worst, maxf(absf(rock_result[i] - expected_rock), absf(scree_result[i] - expected_scree)))
		var expected_strata: float = MODEL.call("StrataFactor", heights[i], footprints[i], STRATA_STEP, STRATA_SEED, 0.12) * 0.5
		worst = maxf(worst, absf(strata_result[i] - expected_strata))
	for i in 4:
		print("TERRAIN_ROCK_MATERIAL probe pair %d: in %s rock %.5f scree %.5f strata %.5f; model rock %.5f" % [i, pairs[i], rock_result[i], scree_result[i], strata_result[i], MODEL.call("RockExposure", pairs[i].x, pairs[i].y, pairs[i].z, pairs[i].w * 0.5, rock_start)])
	print("TERRAIN_ROCK_MATERIAL probe: 64 pairs, worst GLSL vs C# difference %.6f" % worst)
	_check(worst <= 1e-4, "GLSL and C# differ by %.6f (limit 1e-4)" % worst)


func _run_probe(view: SubViewport, material: ShaderMaterial, pairs: PackedVector4Array, row: int) -> Array:
	material.set_shader_parameter("pairs", pairs)
	for frame in 4:
		await RenderingServer.frame_post_draw
	var image := view.get_texture().get_image()
	image.convert(Image.FORMAT_RGBA8)
	var out := []
	for i in 64:
		var c := image.get_pixel(i, row)
		out.append((int(round(c.r * 255.0)) * 256 + int(round(c.g * 255.0))) / 65535.0)
	return out


# ------------------------------------------------------------------ check 2: both tiers

func _ortho_view(which: String) -> Image:
	_show_only(which)
	_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_camera.size = ORTHO_SIZE
	_set_mask(true)
	_set_look(48.0 if _break == "rock" else 38.0, 0.12, true)
	if which == "t3d":
		# Looks along +X at the face; screen right is +Z; a screen row is an absolute height (y).
		_camera.position = Vector3(-30, 32, 0)
		_camera.look_at(Vector3(40, 32, 0), Vector3.UP)
	else:
		# The proxy's up is +X and its ramp runs along +Y: look along +Y with +X as screen up. Screen right is then -Z.
		_camera.position = Vector3(PROXY_BASE + 32, -30, 0)
		_camera.look_at(Vector3(PROXY_BASE + 32, 40, 0), Vector3.RIGHT)
		_proxy_material.set_shader_parameter("camera_world_position", _camera.position)
	await _settle()
	return await _grab()


func _decode(image: Image, x: int, y: int) -> Vector3:
	var c := image.get_pixel(x, y).srgb_to_linear()
	return Vector3(c.r, c.g, c.b)


func _check_tiers() -> void:
	for which in ["t3d", "proxy"]:
		var image := await _ortho_view(which)
		if failed():
			return
		image.save_png(ProjectSettings.globalize_path("res://.godot/terrain_rock_material_%s.png" % which))
		# Columns by lateral zone. Terrain3D: screen right = +Z, so the 50 degree zone (z > 30) is on the right. The proxy looks along +Y with +X up: screen right = -Z, so it is mirrored.
		var mirror: bool = which == "proxy"
		var zones := {"10": [-62.0, -35.0], "33": [-8.0, 8.0], "50": [35.0, 62.0]}
		var base := BASE_HEIGHT + (PROXY_BASE if mirror else 0.0)
		var cam_height := (PROXY_BASE + 32.0) if mirror else 32.0
		var px := ORTHO_SIZE / VIEW.y
		for zone in zones:
			var z0: float = zones[zone][0]
			var z1: float = zones[zone][1]
			var xs0 := int((z0 + 62.0) / 124.0 * VIEW.x)
			var xs1 := int((z1 + 62.0) / 124.0 * VIEW.x)
			if mirror:
				xs0 = int((62.0 - z1) / 124.0 * VIEW.x)
				xs1 = int((62.0 - z0) / 124.0 * VIEW.x)
			xs0 += 8
			xs1 -= 8
			var slope := float(zone)
			var expected_rock: float = MODEL.call("RockExposure", slope, 4.0, 0.0, 0.0, 38.0)
			var expected_scree: float = MODEL.call("ScreeWeight", slope, 4.0, 0.0, 1.0)
			var rock_sum := 0.0
			var scree_sum := 0.0
			var strata_error := 0.0
			var rows := 0
			var strata_rows := 0
			var rise := tan(deg_to_rad(slope)) * FACE_SPAN
			for row in VIEW.y:
				var height := cam_height + (VIEW.y * 0.5 - (row + 0.5)) * px
				var relative := height - base
				if relative < 2.0 or relative > rise - 2.0:
					continue
				var mean := Vector3.ZERO
				for column in range(xs0, xs1):
					mean += _decode(image, column, row)
				mean /= float(xs1 - xs0)
				rock_sum += mean.x
				scree_sum += mean.y
				rows += 1
				var model_height := height
				if _break == "strata" and mirror:
					model_height += STRATA_STEP * 0.5
				var factor: float = MODEL.call("StrataFactor", model_height, px, STRATA_STEP, STRATA_SEED, 0.12)
				var near_boundary := absf(fposmod(height / STRATA_STEP + 0.5, 1.0) - 0.5) * STRATA_STEP < 0.7 or absf(fposmod(height / (STRATA_STEP / 8.0) + 0.5, 1.0) - 0.5) * (STRATA_STEP / 8.0) < 0.7
				if not near_boundary:
					strata_error = maxf(strata_error, absf(mean.z * 2.0 - factor))
					strata_rows += 1
			if rows == 0:
				_check(false, "%s %s degree zone: no rows" % [which, zone])
				continue
			var rock := rock_sum / rows
			var scree := scree_sum / rows
			print("TERRAIN_ROCK_MATERIAL tier %s %s deg: rock %.3f (model %.3f) scree %.3f (model %.3f) strata rows %d max error %.4f" % [which, zone, rock, expected_rock, scree, expected_scree, strata_rows, strata_error])
			_check(absf(rock - expected_rock) < 0.03, "%s %s deg rock weight %.3f, model %.3f" % [which, zone, rock, expected_rock])
			_check(absf(scree - expected_scree) < 0.03, "%s %s deg scree weight %.3f, model %.3f" % [which, zone, scree, expected_scree])
			if slope >= 45.0:
				_check(rock > 0.9, "%s %s deg rock weight %.3f is not above 0.9" % [which, zone, rock])
				_check(strata_rows > 20 and strata_error < 0.02, "%s strata factor differs from the C# mirror (max %.4f over %d rows)" % [which, strata_error, strata_rows])
			if slope <= 15.0:
				_check(rock < 0.001, "%s %s deg rock weight %.3f is not 0" % [which, zone, rock])
	_set_mask(false)
	_set_look(38.0, 0.12, false)


# ------------------------------------------------------------------ plumbing

func failed() -> bool:
	return _finished


func _check(ok: bool, message: String) -> void:
	if not ok:
		failures.append(message)
		push_error("TERRAIN_ROCK_MATERIAL: " + message)


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
	push_error("TERRAIN_ROCK_MATERIAL=FAIL " + message)
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	call_deferred("quit", 1)
