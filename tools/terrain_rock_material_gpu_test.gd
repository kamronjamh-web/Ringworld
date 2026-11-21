extends SceneTree
# T4-3 ground surface (rock, strata, scree) in the Terrain3D surface and the proxy sector material, on synthetic receivers.
#
#   check       : godot --path . --script tools/terrain_rock_material_gpu_test.gd            (windowed Vulkan, not --headless)
#   capture-pre : godot --path . --script tools/terrain_rock_material_gpu_test.gd -- --capture-pre
#                 Feature-off reference images of the Terrain3D receiver, a mesh-installed proxy receiver and a ring_terrain_global receiver, taken BEFORE any shader edit
#                 (T4 design D7), under .godot/audit_baseline/rtx4070/t4/pre/ (the pruner keeps audit_baseline). The check mode compares against them byte for byte
#                 with the feature bit cleared, when they exist.
#   capture-ground : godot --path . --script tools/terrain_rock_material_gpu_test.gd -- --capture-ground
#                 T5-9: the same three receivers in a ground.surface world without scatter.rocks (mask 4), saved as rock_ground_*.png beside the pre-edit references. Taken BEFORE the far speckle edit.
#   options     : --pre-dir=<dir>   override the reference directory
#                 --break=rock      raise the rock start by 10 degrees, to 48 (the weight checks must then FAIL)
#                 --break=strata    offset the proxy receiver by half a bed (the strata checks must then FAIL)
#                 --break=relief    flip the expected sign of the colour variation (the relief colour checks must then FAIL)
#                 --strict-reach    fail on a Terrain3D relief tap outside the window (a report line otherwise)
#
# Checks:
#  1. GLSL against the C# mirror: rock exposure, scree and the strata factor at 64 (slope, baseline) and 64 (height, footprint) pairs, 16-bit readback through a canvas shader that
#     includes materials/ring_ground_surface.gdshaderinc (tolerance 1e-4).
#  2. Both near tiers, through the ground debug view (ringworld_ground_look.w = 1: EMISSION = rock, scree, strata factor / 2), on a 10 degree, a 33 degree and a 50 degree face
#     viewed with an orthographic camera, so a screen row is an absolute height: rock above 0.9 at 50 and 0 at 10, scree 0.4 at 33 on grass ground, and the strata factor per row
#     equal to the C# mirror at that height (Terrain3D and the proxy sector derive the height by different routes, S2/S3).
#  3. Feature off: the receivers render byte-identically to the pre-edit references. T5-9: a world with ground.surface and WITHOUT scatter.rocks (the far speckle's bit 4) also renders
#     byte-identically to the reference taken before the speckle edit (rock_ground_*.png, --capture-ground); with the bit on but density 0 the proxy is unchanged too, and with the
#     bit and a density the proxy (camera moved past 540 m through its uniform) changes (_check_speckle_gate).
#  4. T4-4b colour variation (design D5): C64 on both tiers against the C# model at synthetic mounds (debug view w = 2: relief and the relief colour of mid grey), the sign of the
#     shift (crest paler, hollow darker and greener), Terrain3D against the proxy within 0.02, and the Terrain3D tap reach at the window's far edge (--strict-reach makes a gap fail).
#     The rock and scree checks of step 2 use each row's measured relief, since relief now enters D2 and D4.
#  5. T4-4b detail normal (design D6): the global sampler binds (texels against the PNG), the weight follows rock and scree and is gone by 60 m, and the normal changes where it applies.
#  6. T4-5 far rock tone: a ring_terrain_global_ground receiver with a surface tile of 10, 36 and 50 degree zones; rock exposure (debug view) against the C# model at the 128 m baseline, and the lit colour unchanged at 10 degrees and moved at 50.
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
const ACROSS_ANCHOR := 159000 # the prepared window's across anchor (159000 mod 32 = 24) and direction
const ACROSS_DIR := -1
const MOUNDS := [[-120.0, 30.0, 6.0], [-120.0, -30.0, -6.0], [-232.0, 30.0, 6.0], [-232.0, -30.0, -6.0]] # d, z, height; sigma 14 m (GroundSurfaceModelBridge.FaceHeight mirrors this)
const MOUND_SIGMA := 14.0
const MOUND_VIEW_D := -120.0
const REACH_VIEW_D := -232.0
const DETAIL_NORMAL_PNG := "res://materials/ground/rock_face/rock_face_normal.png"
const FAR_ZONE_SLOPES := [10.0, 36.0, 50.0]

var failures: PackedStringArray = []
var _viewport: SubViewport
var _camera: Camera3D
var _sun: DirectionalLight3D
var _surface: Node3D
var _proxy: MeshInstance3D
var _mound_proxy: MeshInstance3D
var _strict_reach := false
var _proxy_material: ShaderMaterial
var _far: MeshInstance3D
var _far_material: ShaderMaterial
var _pre_dir := DEFAULT_PRE_DIR
var _capture_pre := false
var _capture_ground := false
var _uniform_camera_offset := Vector3.ZERO # moves the proxy's camera_world_position uniform (the shader's distance) without moving the camera
var _break := ""
var _finished := false
var MODEL: RefCounted


func _init() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg == "--capture-pre":
			_capture_pre = true
		elif arg == "--capture-ground":
			_capture_ground = true
		elif arg.begins_with("--pre-dir="):
			_pre_dir = arg.substr(10)
		elif arg.begins_with("--break="):
			_break = arg.substr(8)
		elif arg == "--strict-reach":
			_strict_reach = true
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
	var h := BASE_HEIGHT + _tan_slope(z) * clampf(d - FACE_START, 0.0, FACE_SPAN)
	for mound in MOUNDS:
		var dd: float = d - mound[0]
		var dz: float = z - mound[1]
		h += float(mound[2]) * exp(-(dd * dd + dz * dz) / (2.0 * MOUND_SIGMA * MOUND_SIGMA))
	return h


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
	if _capture_ground:
		await _capture_or_compare_identity(true, "", FEATURE_BIT, "rock_ground_%s.png")
		print("TERRAIN_ROCK_MATERIAL=CAPTURED ground dir=%s" % _pre_dir)
		await _finish(0)
		return
	await _capture_or_compare_identity(false, "ground variants, mask cleared")
	await _capture_or_compare_identity(false, "ground.surface without scatter.rocks", FEATURE_BIT, "rock_ground_%s.png")
	await _check_speckle_gate()
	await _check_probe()
	await _check_tiers()
	await _check_sampler()
	await _check_relief()
	await _check_detail()
	await _check_far()
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
	for node in [_surface, _proxy, _mound_proxy, _far]:
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
# A mesh-installed proxy grid over d in [d_min, d_max], z in [-half_lat, half_lat] (2 m cells). CUSTOM0.w carries C64 from the C# model, as the sector builders store it (T4-4a).
func _grid_instance(d_min: float, d_max: float, half_lat: float) -> MeshInstance3D:
	var cells_lat := int(half_lat)
	var cells_d := int((d_max - d_min) / 2.0)
	var vertices := PackedVector3Array()
	var normals := PackedVector3Array()
	var colors := PackedColorArray()
	var custom := PackedFloat32Array()
	var indices := PackedInt32Array()
	for i in cells_d + 1:
		var d := d_min + i * 2.0
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
			custom.append_array([0.0, 0.0, 0.0, float(MODEL.call("FaceC64", d, z, ACROSS_ANCHOR, ACROSS_DIR))])
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
	arrays[ArrayMesh.ARRAY_CUSTOM0] = custom
	arrays[ArrayMesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays, [], {}, Mesh.ARRAY_CUSTOM_RGBA_FLOAT << Mesh.ARRAY_FORMAT_CUSTOM0_SHIFT)
	mesh.custom_aabb = AABB(Vector3(PROXY_BASE - 10, d_min - 10, -100), Vector3(200, d_max - d_min + 20, 200))
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	instance.visible = false
	_viewport.add_child(instance)
	return instance


func _build_proxy() -> void:
	_proxy = _grid_instance(0.0, 88.0, 64.0)
	_mound_proxy = _grid_instance(MOUND_VIEW_D - 80.0, MOUND_VIEW_D + 80.0, 80.0)
	_proxy_material = ShaderMaterial.new()
	_proxy_material.shader = load(str(MODEL.call("ProxyShaderPath"))) as Shader
	_proxy.material_override = _proxy_material
	_mound_proxy.material_override = _proxy_material
	var native := _terrain_material()
	for parameter in ["biome_grass_albedo", "biome_dirt_albedo", "biome_desert_albedo", "biome_forest_albedo", "biome_tundra_albedo", "biome_alpine_albedo"]:
		_proxy_material.set_shader_parameter(parameter, native.call("get_shader_param", parameter))
	_proxy_material.set_shader_parameter("ground_rock_albedo", load("res://materials/ground/rock_face/rock_face_albedo.png")) # T8-R2: read by the ground variant only
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
	arrays[ArrayMesh.ARRAY_TEX_UV2] = PackedVector2Array([Vector2(0, 0), Vector2(1, 0), Vector2(1, 1), Vector2(0, 1)])
	arrays[ArrayMesh.ARRAY_INDEX] = PackedInt32Array([0, 1, 2, 0, 2, 3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	_far = MeshInstance3D.new()
	_far.mesh = mesh
	_far_material = ShaderMaterial.new()
	_far_material.shader = load(str(MODEL.call("FarShaderPath"))) as Shader
	_far_material.set_shader_parameter("ring_center", Vector3(RING_RADIUS, 0, 0))
	_far_material.set_shader_parameter("sun_direction", Vector3(0.45, 0.2, 0.3).normalized())
	_far_material.set_shader_parameter("wet_world_receiver", false)
	_far_material.set_shader_parameter("fade_start_meters", -1.0)
	_far_material.set_shader_parameter("fade_end_meters", 0.0)
	_far_material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	# T4-5 far tile: three zones along UV2.x with a constant normal each (10, 36 and 50 degrees about the around axis); the tile is only switched on by _check_far.
	var tile_n := 30
	var tile_colour := Image.create_empty(tile_n + 2, tile_n + 2, false, Image.FORMAT_RGBA8)
	tile_colour.fill(Color(0.45, 0.40, 0.30, 1.0))
	var tile_normal := Image.create_empty(tile_n + 2, tile_n + 2, false, Image.FORMAT_RGBA8)
	for x in tile_n + 2:
		var zone_slope: float = FAR_ZONE_SLOPES[0] if x <= 10 else (FAR_ZONE_SLOPES[1] if x <= 20 else FAR_ZONE_SLOPES[2])
		var s := deg_to_rad(zone_slope)
		for y in tile_n + 2:
			tile_normal.set_pixel(x, y, Color(sin(s) * 0.5 + 0.5, cos(s) * 0.5 + 0.5, 0.5, 1.0))
	_far_material.set_shader_parameter("surface_tile_color", ImageTexture.create_from_image(tile_colour))
	_far_material.set_shader_parameter("surface_tile_normal", ImageTexture.create_from_image(tile_normal))
	_far_material.set_shader_parameter("surface_tile_resolution", float(tile_n))
	_far_material.set_shader_parameter("surface_tile_angle_start", PI) # tile up = +X, the receiver's ring-up
	_far_material.set_shader_parameter("surface_tile_angle_span", 0.0)
	_far_material.set_shader_parameter("surface_tile_blend", 1.0)
	_far.material_override = _far_material
	_far.visible = false
	_viewport.add_child(_far)


# ------------------------------------------------------------------ views

func _show_only(which: String) -> void:
	_surface.visible = which == "t3d"
	_proxy.visible = which == "proxy"
	_mound_proxy.visible = which == "mound"
	_far.visible = which == "far"


func _orient_sun(to_light: Vector3) -> void:
	var direction := to_light.normalized()
	var up_hint := Vector3.RIGHT if absf(direction.dot(Vector3.UP)) > 0.95 else Vector3.UP
	_sun.transform = Transform3D(Basis.looking_at(-direction, up_hint), Vector3.ZERO)


func _set_mask(on: bool) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_world_features", FEATURE_BIT if on else 0)


func _set_mask_bits(bits: int) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_world_features", bits)


func _set_look(rock_start: float, contrast: float, debug: bool, view := 1.0) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_ground_look", Vector4(rock_start, contrast, 1.0, view if debug else 0.0))


# Terrain3D re-centres its clipmap on the camera (and sets _camera_pos) in its physics process, not per drawn frame. With VSync off, SETTLE_FRAMES process
# frames can all fall inside one 60 Hz physics tick, leaving the clipmap at the previous view (the far receiver, 2.4 km away) so the grab shows only the
# background. Two physics ticks after the last camera update guarantee one full Terrain3D physics process.
func _settle() -> void:
	for frame in SETTLE_FRAMES:
		_surface.call("UpdateCamera", _camera.global_position)
		await process_frame
	for tick in 2:
		_surface.call("UpdateCamera", _camera.global_position)
		await physics_frame
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
		_proxy_material.set_shader_parameter("camera_world_position", _camera.position + _uniform_camera_offset)
	else:
		_camera.position = Vector3(PROXY_BASE + 60, -60, 0)
		_camera.look_at(Vector3(PROXY_BASE, 0, 0), Vector3.RIGHT)
		_far_material.set_shader_parameter("camera_world_position", _camera.position)
	await _settle()
	return await _grab()


func _capture_or_compare_identity(capture: bool, label: String, mask := 0, file_pattern := "rock_pre_%s.png") -> void:
	_set_look(38.0, 0.12, false)
	_set_mask_bits(mask)
	var dir_abs := ProjectSettings.globalize_path(_pre_dir)
	if capture:
		DirAccess.make_dir_recursive_absolute(dir_abs)
	var compared := 0
	for which in ["t3d", "proxy", "far"]:
		var image := await _lit_view(which)
		if failed():
			return
		var path := dir_abs.path_join(file_pattern % which)
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


# T5-9 far speckle gate (design B8): the speckle needs bit 4 (scatter.rocks), a density (ringworld_ground_world.z) and a camera distance past 540 m (here set through the proxy's
# camera uniform, 700 m along the ring axis). Bit 4 clear, or density 0: the proxy is byte-identical; both set: it changes.
func _speckle_view(mask: int, density: float) -> Image:
	_set_mask_bits(mask)
	RenderingServer.global_shader_parameter_set("ringworld_ground_world", Vector4(STRATA_STEP, STRATA_SEED, density, 1.0))
	_uniform_camera_offset = Vector3(0, 700, 0)
	var image := await _lit_view("proxy")
	_uniform_camera_offset = Vector3.ZERO
	return image


func _check_speckle_gate() -> void:
	const SCATTER_BIT := 16 # 1 << 4
	var off_density := await _speckle_view(FEATURE_BIT | SCATTER_BIT, 0.0)
	var on := await _speckle_view(FEATURE_BIT | SCATTER_BIT, 1.0)
	var bit_clear := await _speckle_view(FEATURE_BIT, 1.0)
	RenderingServer.global_shader_parameter_set("ringworld_ground_world", Vector4(STRATA_STEP, STRATA_SEED, 0, 0))
	_set_mask_bits(0)
	if failed():
		return
	var clear_diff := _count_differences(bit_clear, off_density)
	var density_diff := _count_differences(on, off_density)
	print("TERRAIN_ROCK_MATERIAL speckle gate: bit clear vs density 0 differ in %d pixels; density 1 vs 0 differ in %d pixels" % [clear_diff, density_diff])
	_check(clear_diff == 0, "the far speckle changes the proxy with scatter.rocks clear (%d pixels)" % clear_diff)
	_check(density_diff > 200, "the far speckle does not show on the proxy with scatter.rocks, density 1 and the camera 700 m away (%d pixels differ)" % density_diff)


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

func _ortho_view(which: String, view := 1.0) -> Image:
	_show_only(which)
	_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_camera.size = ORTHO_SIZE
	_set_mask(true)
	_set_look(48.0 if _break == "rock" else 38.0, 0.12, true, view)
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
		var relief_image := await _ortho_view(which, 2.0) # the same view in the relief debug view: relief enters rock exposure and scree (D2, D4), so the expectation uses each row's measured relief
		if failed():
			return
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
			var expected_rock_sum := 0.0
			var expected_scree_sum := 0.0
			var relief_sum := 0.0
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
				var relief_mean := Vector3.ZERO
				for column in range(xs0, xs1):
					relief_mean += _decode(relief_image, column, row)
				var row_relief := 2.0 * relief_mean.x / float(xs1 - xs0) - 1.0
				relief_sum += row_relief
				expected_rock_sum += MODEL.call("RockExposure", slope, 4.0, row_relief, 0.0, 38.0)
				expected_scree_sum += MODEL.call("ScreeWeight", slope, 4.0, row_relief, 1.0)
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
			var expected_rock := expected_rock_sum / rows
			var expected_scree := expected_scree_sum / rows
			print("TERRAIN_ROCK_MATERIAL tier %s %s deg: rock %.3f (model %.3f) scree %.3f (model %.3f) mean relief %.3f strata rows %d max error %.4f" % [which, zone, rock, expected_rock, scree, expected_scree, relief_sum / rows, strata_rows, strata_error])
			_check(absf(rock - expected_rock) < 0.03, "%s %s deg rock weight %.3f, model %.3f" % [which, zone, rock, expected_rock])
			_check(absf(scree - expected_scree) < 0.03, "%s %s deg scree weight %.3f, model %.3f" % [which, zone, scree, expected_scree])
			if slope >= 45.0:
				_check(rock > 0.8, "%s %s deg rock weight %.3f is not above 0.8" % [which, zone, rock])
				_check(strata_rows > 20 and strata_error < 0.02, "%s strata factor differs from the C# mirror (max %.4f over %d rows)" % [which, strata_error, strata_rows])
			if slope <= 15.0:
				_check(rock < 0.001, "%s %s deg rock weight %.3f is not 0" % [which, zone, rock])
	_set_mask(false)
	_set_look(38.0, 0.12, false)



# ------------------------------------------------------------------ check 6: far rock tone (T4-5, design D2)

func _far_view(mask_on: bool, debug: bool) -> Image:
	_show_only("far")
	_far_material.set_shader_parameter("surface_tile_enabled", true)
	_set_mask(mask_on)
	_set_look(38.0, 0.12, debug)
	_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_camera.size = ORTHO_SIZE
	_camera.position = Vector3(PROXY_BASE + 40, -30, 0)
	_camera.look_at(Vector3(PROXY_BASE + 40, 40, 0), Vector3.RIGHT)
	_far_material.set_shader_parameter("camera_world_position", _camera.position)
	await _settle()
	return await _grab()


func _far_zone_mean(image: Image, zone: int) -> Vector3:
	var centre: int = [237, 135, 33][zone] # screen row of u = 0.17, 0.5, 0.83 (UV2.x runs along +X, the screen up direction of this view)
	var mean := Vector3.ZERO
	var count := 0
	for y in range(centre - 20, centre + 20):
		for x in range(150, 330):
			mean += _decode(image, x, y)
			count += 1
	return mean / float(count)


func _check_far() -> void:
	var debug_image := await _far_view(true, true)
	if failed():
		return
	var lit_on := await _far_view(true, false)
	var lit_off := await _far_view(false, false)
	_far_material.set_shader_parameter("surface_tile_enabled", false)
	_set_mask(false)
	_set_look(38.0, 0.12, false)
	if failed():
		return
	var worst_rock := 0.0
	for zone in 3:
		var slope: float = FAR_ZONE_SLOPES[zone]
		var rock := _far_zone_mean(debug_image, zone).x
		var expected: float = MODEL.call("RockExposure", slope, 128.0, 0.0, 0.0, 38.0)
		var on := _far_zone_mean(lit_on, zone)
		var off := _far_zone_mean(lit_off, zone)
		var delta := (on - off).abs()
		var change := maxf(delta.x, maxf(delta.y, delta.z))
		print("TERRAIN_ROCK_MATERIAL far zone %d deg: rock %.3f (model %.3f), colour change on against off %.4f" % [int(slope), rock, expected, change])
		worst_rock = maxf(worst_rock, absf(rock - expected))
		if expected == 0.0:
			_check(change < 0.002, "far zone %d deg changes colour with the feature on (%.4f) although rock is 0" % [int(slope), change])
		elif expected > 0.9:
			_check(change > 0.002, "far zone %d deg has full rock but the colour did not move (%.4f)" % [int(slope), change])
	_check(worst_rock < 0.05, "far rock exposure differs from the C# model by %.3f at the 128 m baseline" % worst_rock)

# ------------------------------------------------------------------ check 4: colour variation (relief), both tiers

# Top-down orthographic view over a mound pair. Terrain3D: screen up = +X (the window's around axis), screen right = +Z. Proxy (up is +X, the ring axis is +Y): screen up = +Y, screen right = -Z.
func _top_view(which: String, centre_d: float, view: float, size: float) -> Image:
	_show_only(which)
	_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_camera.size = size
	_set_mask(true)
	_set_look(38.0, 0.12, true, view)
	if which == "t3d":
		_camera.position = Vector3(centre_d, 300.0, 0.0)
		_camera.look_at(Vector3(centre_d, 0.0, 0.0), Vector3.RIGHT)
	else:
		_camera.position = Vector3(PROXY_BASE + 300.0, centre_d, 0.0)
		_camera.look_at(Vector3(PROXY_BASE, centre_d, 0.0), Vector3.UP)
		_proxy_material.set_shader_parameter("camera_world_position", _camera.position)
	await _settle()
	return await _grab()


# Mean decoded colour of a 3 x 3 block at the pixel of window position (d, z).
func _top_sample(image: Image, which: String, centre_d: float, size: float, d: float, z: float) -> Vector3:
	var px := size / VIEW.y
	var x := int(round(VIEW.x * 0.5 + (z if which == "t3d" else -z) / px - 0.5))
	var y := int(round(VIEW.y * 0.5 - (d - centre_d) / px - 0.5))
	var sum := Vector3.ZERO
	for dy in range(-1, 2):
		for dx in range(-1, 2):
			sum += _decode(image, clampi(x + dx, 0, VIEW.x - 1), clampi(y + dy, 0, VIEW.y - 1))
	return sum / 9.0


func _check_relief() -> void:
	var size := 120.0
	var sign_flip := -1.0 if _break == "relief" else 1.0
	# Away from steep relief gradients (a 1 px offset between the tiers' views is then below the 8-bit readback step).
	var points := [[-120.0, 30.0], [-120.0, -30.0], [-124.0, 30.0], [-100.0, 30.0], [-140.0, -30.0], [-120.0, 56.0], [-96.0, -12.0], [-132.0, 12.0]]
	var results := {}
	for which in ["t3d", "mound"]:
		var relief_image := await _top_view(which, MOUND_VIEW_D, 2.0, size)
		if failed():
			return
		relief_image.save_png(ProjectSettings.globalize_path("res://.godot/terrain_rock_relief_%s.png" % which))
		results[which] = relief_image
	var worst := {"t3d": 0.0, "mound": 0.0}
	var worst_colour := 0.0
	var worst_tiers := 0.0
	for point in points:
		var expected_c64: float = MODEL.call("FaceC64", point[0], point[1], ACROSS_ANCHOR, ACROSS_DIR)
		var expected_relief: float = MODEL.call("Relief", expected_c64)
		var expected_green: float = MODEL.call("ReliefGreyGreen", expected_relief)
		var measured := {}
		for which in ["t3d", "mound"]:
			var sample := _top_sample(results[which], which, MOUND_VIEW_D, size, point[0], point[1])
			var relief := 2.0 * sample.x - 1.0
			measured[which] = relief
			worst[which] = maxf(worst[which], absf(relief - expected_relief))
			worst_colour = maxf(worst_colour, absf(sample.y - expected_green))
			if absf(expected_relief) > 0.3:
				var shift := sample.y - 0.5
				_check(shift * expected_relief * sign_flip > 0.0, "%s relief colour at (%.0f, %.0f): grey became %.3f with relief %.2f (convex must be paler, hollow darker)" % [which, point[0], point[1], sample.y, expected_relief])
		worst_tiers = maxf(worst_tiers, absf(measured["t3d"] - measured["mound"]))
		print("TERRAIN_ROCK_MATERIAL relief at (%.0f, %.0f): model %.3f (C64 %.2f m), terrain3d %.3f, proxy %.3f" % [point[0], point[1], expected_relief, expected_c64, measured["t3d"], measured["mound"]])
	print("TERRAIN_ROCK_MATERIAL relief: worst against the model terrain3d %.4f proxy %.4f, colour %.4f, terrain3d against proxy %.4f" % [worst["t3d"], worst["mound"], worst_colour, worst_tiers])
	_check(worst["t3d"] <= 0.02, "Terrain3D relief differs from the C# model by %.4f (limit 0.02)" % worst["t3d"])
	_check(worst["mound"] <= 0.02, "proxy relief differs from the C# model by %.4f (limit 0.02)" % worst["mound"])
	_check(worst_colour <= 0.02, "relief colour differs from the C# model by %.4f (limit 0.02)" % worst_colour)
	_check(worst_tiers <= 0.02, "Terrain3D and proxy relief differ by %.4f at the mounds (limit 0.02)" % worst_tiers)
	# Reach: the pair 24 m from the window's far edge (-256 m). A crest there needs taps out to d - 32 - 64, which the window does not hold (the player may stand 128 m behind the anchor,
	# so the hand-off ring reaches this far). The shader then takes one axis only; the gap against the model is reported, and fails the run with --strict-reach.
	var reach_image := await _top_view("t3d", REACH_VIEW_D + 20.0, 2.0, size)
	if failed():
		return
	var reach_worst := 0.0
	for point in [[-232.0, 30.0], [-232.0, -30.0], [-210.0, 30.0], [-210.0, -30.0]]:
		var expected_relief: float = MODEL.call("Relief", MODEL.call("FaceC64", point[0], point[1], ACROSS_ANCHOR, ACROSS_DIR))
		var sample := _top_sample(reach_image, "t3d", REACH_VIEW_D + 20.0, size, point[0], point[1])
		var relief := 2.0 * sample.x - 1.0
		reach_worst = maxf(reach_worst, absf(relief - expected_relief))
		print("TERRAIN_ROCK_MATERIAL reach at (%.0f, %.0f): model %.3f terrain3d %.3f" % [point[0], point[1], expected_relief, relief])
	print("TERRAIN_ROCK_MATERIAL reach: worst Terrain3D relief difference at the far window edge %.4f%s" % [reach_worst, "" if reach_worst <= 0.02 else " (taps outside the window)"])
	if _strict_reach:
		_check(reach_worst <= 0.02, "Terrain3D relief at the window's far edge differs from the model by %.4f: some taps lie outside the window" % reach_worst)
	_set_mask(false)
	_set_look(38.0, 0.12, false)


# ------------------------------------------------------------------ check 5: detail normal (Terrain3D)

const SAMPLER_PROBE_SHADER := """
shader_type canvas_item;
render_mode blend_disabled;
global uniform sampler2D ringworld_ground_rock_detail_normal : filter_nearest, repeat_enable;
void fragment() {
	ivec2 size = textureSize(ringworld_ground_rock_detail_normal, 0);
	int i = int(FRAGCOORD.x);
	ivec2 texel = ivec2((i * 61 + 7) % size.x, (i * 113 + 3) % size.y);
	vec4 t = texelFetch(ringworld_ground_rock_detail_normal, texel, 0);
	COLOR = vec4(t.rg, float(size.x) / 4096.0, 1.0);
}
"""


# The global sampler's project default binds (D10's open item): 16 texels read back through a canvas shader against the PNG, and the size.
func _check_sampler() -> void:
	var shader := Shader.new()
	shader.code = SAMPLER_PROBE_SHADER
	var material := ShaderMaterial.new()
	material.shader = shader
	var rect := ColorRect.new()
	rect.size = Vector2(16, 1)
	rect.material = material
	var view := SubViewport.new()
	view.size = Vector2i(16, 1)
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	view.add_child(rect)
	root.add_child(view)
	for frame in 4:
		await RenderingServer.frame_post_draw
	var gpu := view.get_texture().get_image()
	gpu.convert(Image.FORMAT_RGBA8)
	view.queue_free()
	var source := Image.load_from_file(ProjectSettings.globalize_path(DETAIL_NORMAL_PNG))
	if source == null or source.is_empty():
		_check(false, "detail normal PNG missing (%s)" % DETAIL_NORMAL_PNG)
		return
	source.convert(Image.FORMAT_RGBA8)
	var size_ok := absi(int(round(gpu.get_pixel(0, 0).b * 4096.0)) - source.get_width()) <= 16 # the size travels in an 8-bit channel
	var total := 0.0
	var worst := 0.0
	for i in 16:
		var texel := Vector2i((i * 61 + 7) % source.get_width(), (i * 113 + 3) % source.get_height())
		var want := source.get_pixelv(texel)
		var got := gpu.get_pixel(i, 0)
		var error := maxf(absf(got.r - want.r), absf(got.g - want.g))
		total += error
		worst = maxf(worst, error)
	print("TERRAIN_ROCK_MATERIAL detail normal sampler: size match %s, mean texel error %.4f, worst %.4f" % [size_ok, total / 16.0, worst])
	_check(size_ok, "the global detail normal sampler is not bound to a %d px texture (project.godot default)" % source.get_width())
	_check(total / 16.0 < 0.03 and worst < 0.1, "the bound detail normal differs from the PNG (mean %.4f, worst %.4f)" % [total / 16.0, worst])


# The 50 degree face through a 10 m tall orthographic window (4 cm per pixel, about a 1080p pixel at 40 m, so the mip of the 5 m detail tile is about the real one), seen from camera_x along +X at the
# height centre_h, in the rock view and the detail view: the weight follows rock and scree and is gone by 60 m, and the normal changes where it applies, with variation across the face.
func _detail_view(camera_x: float, centre_h: float) -> Dictionary:
	_show_only("t3d")
	_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_camera.size = 10.0
	_set_mask(true)
	_camera.position = Vector3(camera_x, centre_h, 48)
	_camera.look_at(Vector3(camera_x + 50.0, centre_h, 48), Vector3.UP)
	_set_look(38.0, 0.12, true, 1.0)
	await _settle()
	var rock_image := await _grab()
	_set_look(38.0, 0.12, true, 3.0)
	await _settle()
	var detail_image := await _grab()
	var px := 10.0 / VIEW.y
	var tan_slope := tan(deg_to_rad(50.0))
	var error_sum := 0.0
	var error_max := 0.0
	var count := 0
	var delta_sum := 0.0
	var spread_sum := 0.0
	var rows := 0
	for row in range(0, VIEW.y, 3):
		var height := centre_h + (VIEW.y * 0.5 - (row + 0.5)) * px
		var face_d := FACE_START + (height - BASE_HEIGHT) / tan_slope
		var row_values := []
		var row_mean := 0.0
		for column in range(40, VIEW.x - 40, 8):
			var dz := (column + 0.5 - VIEW.x * 0.5) * px
			var distance := sqrt(pow(face_d - camera_x, 2.0) + pow(height - centre_h, 2.0) + dz * dz)
			var rock := _decode(rock_image, column, row)
			var expected: float = MODEL.call("DetailNormalWeight", rock.x, rock.y, distance)
			var detail := _decode(detail_image, column, row)
			error_sum += absf(detail.x - expected)
			error_max = maxf(error_max, absf(detail.x - expected))
			count += 1
			row_values.append(detail.y)
			row_mean += detail.y
		row_mean /= row_values.size()
		var spread := 0.0
		for value in row_values:
			spread += absf(value - row_mean)
		delta_sum += row_mean
		spread_sum += spread / row_values.size()
		rows += 1
	detail_image.save_png(ProjectSettings.globalize_path("res://.godot/terrain_rock_detail_%d.png" % int(camera_x)))
	return {"error_mean": error_sum / maxf(count, 1), "error_max": error_max, "count": count, "delta": delta_sum / maxf(rows, 1), "spread": spread_sum / maxf(rows, 1)}


func _check_detail() -> void:
	var near := await _detail_view(0.0, 20.0) # the face 26 to 34 m from the camera: weight = rock
	var far := await _detail_view(-10.0, 50.0) # 60 m and more away: no detail normal
	if failed():
		return
	print("TERRAIN_ROCK_MATERIAL detail normal near: weight error mean %.4f max %.4f over %d pixels; mean normal change %.4f, spread %.4f" % [near["error_mean"], near["error_max"], near["count"], near["delta"], near["spread"]])
	print("TERRAIN_ROCK_MATERIAL detail normal far: weight error mean %.4f max %.4f over %d pixels; mean normal change %.5f" % [far["error_mean"], far["error_max"], far["count"], far["delta"]])
	_check(near["error_mean"] < 0.04 and near["error_max"] < 0.2 and far["error_mean"] < 0.01, "detail normal weight differs from rock + 0.6 scree faded 40 to 60 m (near mean %.4f max %.4f, far mean %.4f)" % [near["error_mean"], near["error_max"], far["error_mean"]])
	_check(near["delta"] > 0.01, "the detail normal does not change the normal on near rock (mean change %.4f)" % near["delta"])
	_check(near["spread"] > 0.003, "the detail normal does not vary across near rock (spread %.4f)" % near["spread"])
	_check(far["delta"] < 0.002, "the detail normal changes the normal beyond 60 m (%.5f)" % far["delta"])
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
