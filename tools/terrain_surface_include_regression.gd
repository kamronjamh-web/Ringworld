extends SceneTree
# Pixel regression for materials/terrain3d_ring_surface.gdshader and the shared
# ring_terrain_*.gdshaderinc includes it composes (grass-cards plan step S1+).
#
# A synthetic, deterministic Terrain3D window (rolling heights, a steep slope, a
# grass->forest->desert->tundra->alpine biome layout) is rendered at fixed poses
# and sun/haze/shadow settings, then compared byte for byte with the golden PNGs
# in tools/fixtures/terrain_surface_include_regression/.
#
#   compare : godot --path . --script tools/terrain_surface_include_regression.gd
#   capture : godot --path . --script tools/terrain_surface_include_regression.gd -- --capture
#   options : --out=<res:// or abs dir>   write the actual images there (default res://.godot/terrain_surface_include_regression)
#             --golden-dir=<dir>          override the golden directory
#             --max-diff=<0..255>         per-channel tolerance (default 0 = pixel identical)
# Requires a real Vulkan/Forward+ renderer (do not pass --headless).
const SURFACE = preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const GEOMETRY = preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")

const WINDOW := 384
const REGION := 128
const VIEWPORT_SIZE := Vector2i(480, 270)
const MAX_HEIGHT := 64.0
const SETTLE_FRAMES := 24
const DEFAULT_GOLDEN_DIR := "res://tools/fixtures/terrain_surface_include_regression"
const DEFAULT_OUT_DIR := "res://.godot/terrain_surface_include_regression"

# name, cam = (local x, eye height above ground, local z), look = (local x, height above ground, local z), sun = direction to the sun
var POSES := [
	{"name": "day_mixed", "cam": Vector3(-24, 2.0, -12), "look": Vector3(90, 3, 14), "sun": Vector3(0.30, 0.85, 0.20)},
	{"name": "day_slope_triplanar", "cam": Vector3(70, 2.0, -4), "look": Vector3(104, 6, 6), "sun": Vector3(-0.55, 0.55, 0.25)},
	{"name": "dusk_macro_wet", "cam": Vector3(-10, 2.0, 0), "look": Vector3(60, 2, 40), "sun": Vector3(0.92, 0.14, 0.20), "macro": true, "wet": 0.7},
	{"name": "night_planet", "cam": Vector3(0, 2.0, 0), "look": Vector3(60, 0, 60), "sun": Vector3(0.20, -0.95, 0.10), "planet": true},
	{"name": "border_close_wet", "cam": Vector3(28, 1.2, -5), "look": Vector3(46, 0, 1), "sun": Vector3(0.45, 0.70, -0.30), "wet": 0.6},
	{"name": "day_haze", "cam": Vector3(0, 3.0, 0), "look": Vector3(220, 6, 70), "sun": Vector3(0.40, 0.75, 0.10), "haze": true},
]

var failed := false
var _viewport: SubViewport
var _camera: Camera3D
var _sun: DirectionalLight3D
var _planet: DirectionalLight3D
var _surface: Node3D
var _golden_dir := DEFAULT_GOLDEN_DIR
var _out_dir := DEFAULT_OUT_DIR
var _capture := false
var _max_diff := 0
var _ground_height := 0.0


func _init() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg == "--capture":
			_capture = true
		elif arg.begins_with("--out="):
			_out_dir = arg.substr(6)
		elif arg.begins_with("--golden-dir="):
			_golden_dir = arg.substr(13)
		elif arg.begins_with("--max-diff="):
			_max_diff = int(arg.substr(11))
	call_deferred("_run")
	create_timer(240.0).timeout.connect(func() -> void: _fail("timeout"))


func _local_height(x: float, z: float) -> float:
	# Local meters about the window origin (sample index 128 is local 0).
	var h := 8.0 + 2.5 * sin(x * 0.045) + 1.8 * cos(z * 0.071 + x * 0.013)
	h += 24.0 * smoothstep(92.0, 124.0, x) # steep ramp, roughly 40 degrees at its midpoint
	h += 6.0 * smoothstep(120.0, 220.0, z)
	return h


func _biome_weights(x: float, z: float) -> Color:
	# r desert, g forest, b tundra, a alpine; grass is the remainder.
	var forest := smoothstep(26.0, 58.0, x)
	var desert := smoothstep(150.0, 210.0, x)
	var tundra := smoothstep(60.0, 110.0, z) * 0.85
	var alpine := smoothstep(150.0, 200.0, z) * 0.6
	var f := forest * (1.0 - desert)
	return Color(clampf(desert, 0.0, 1.0), clampf(f, 0.0, 1.0), clampf(tundra * (1.0 - desert) * 0.5, 0.0, 1.0), clampf(alpine * 0.4, 0.0, 1.0))


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	_viewport = SubViewport.new()
	_viewport.own_world_3d = true
	_viewport.size = VIEWPORT_SIZE
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)

	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.10, 0.12, 0.17)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.30, 0.33, 0.38)
	environment.ambient_light_energy = 1.0
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	_viewport.add_child(world_environment)

	# The surface resolves a directional light named "Sun" once, so it must exist first.
	_sun = DirectionalLight3D.new()
	_sun.name = "Sun"
	_sun.shadow_enabled = true
	_sun.directional_shadow_max_distance = 500.0
	_viewport.add_child(_sun)
	_planet = DirectionalLight3D.new()
	_planet.name = "PlanetLight"
	_planet.light_color = Color(0.55, 0.65, 1.0)
	_planet.light_energy = 0.45
	_planet.visible = false
	_viewport.add_child(_planet)
	_orient_light(_planet, Vector3.UP)

	_camera = Camera3D.new()
	_camera.current = true
	_camera.fov = 70.0
	_camera.far = 2000.0
	_viewport.add_child(_camera)

	_surface = SURFACE.new()
	_viewport.add_child(_surface)
	if not await _prepare_surface():
		return

	var summary: Array = []
	var any_diff := false
	for pose in POSES:
		await _apply_pose(pose)
		var image := await _grab()
		if failed:
			return
		var out_path := _out_dir.path_join(pose.name + ".png")
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out_dir))
		image.save_png(ProjectSettings.globalize_path(out_path))
		if _capture:
			DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_golden_dir))
			if image.save_png(ProjectSettings.globalize_path(_golden_dir.path_join(pose.name + ".png"))) != OK:
				_fail("could not write golden " + pose.name)
				return
			summary.append("%s captured" % pose.name)
			continue
		var golden := Image.load_from_file(ProjectSettings.globalize_path(_golden_dir.path_join(pose.name + ".png")))
		if golden == null or golden.is_empty():
			_fail("missing golden for " + pose.name)
			return
		var result := _compare(image, golden)
		summary.append("%s differing_pixels=%d max_channel_diff=%d" % [pose.name, result.pixels, result.max_diff])
		if int(result.max_diff) > _max_diff:
			any_diff = true
	for line in summary:
		print("TERRAIN_SURFACE_REGRESSION ", line)
	if any_diff:
		_fail("pixels differ from golden (actual images in %s)" % _out_dir)
		return
	print("TERRAIN_SURFACE_INCLUDE_REGRESSION=%s poses=%d" % ["CAPTURED" if _capture else "PASS", POSES.size()])
	await _finish(0)


func _prepare_surface() -> bool:
	var heights := PackedFloat32Array()
	heights.resize(WINDOW * WINDOW)
	for z in WINDOW:
		for x in WINDOW:
			heights[z * WINDOW + x] = _local_height((x - REGION) * 2.0, (z - REGION) * 2.0)
	_ground_height = _local_height(0.0, 0.0)
	var height_images: Array[Image] = []
	var biome_images: Array[Image] = []
	for region in 9:
		var region_x := region % 3
		var region_z := region / 3
		var bytes := PackedByteArray()
		bytes.resize(REGION * REGION * 4)
		var biome := Image.create_empty(REGION, REGION, false, Image.FORMAT_RGBA8)
		for row in REGION:
			for column in REGION:
				var window_x := region_x * REGION + column
				var window_z := region_z * REGION + row
				bytes.encode_float((row * REGION + column) * 4, heights[window_z * WINDOW + window_x])
				biome.set_pixel(column, row, _biome_weights((window_x - REGION) * 2.0, (window_z - REGION) * 2.0))
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


func _orient_light(light: DirectionalLight3D, to_light: Vector3) -> void:
	var direction := to_light.normalized()
	var up_hint := Vector3.RIGHT if absf(direction.dot(Vector3.UP)) > 0.95 else Vector3.UP
	# A directional light shines along its -Z axis, so +Z points at the light.
	light.transform = Transform3D(Basis.looking_at(-direction, up_hint), Vector3.ZERO)


func _terrain_material() -> Object:
	var terrain: Node = _surface.get("TerrainNode")
	return terrain.call("get_material")


func _apply_pose(pose: Dictionary) -> void:
	var cam: Vector3 = pose.cam
	var look: Vector3 = pose.look
	var eye := Vector3(cam.x, _local_height(cam.x, cam.z) + cam.y, cam.z)
	# look = (x, height above the ground there, z)
	var target := Vector3(look.x, _local_height(look.x, look.z) + look.y, look.z)
	_camera.position = eye
	_camera.look_at(target, Vector3.UP)
	_orient_light(_sun, pose.sun)
	_planet.visible = bool(pose.get("planet", false))
	# Non-default lighting contract values so every forwarded lighting uniform is exercised.
	_surface.call("SetSurfaceLighting", -0.2, 0.35, Color(0.30, 0.45, 0.70), 0.12, 1.15, 0.10)
	_surface.call("SetSurfaceWetness", float(pose.get("wet", 0.0)))
	var material := _terrain_material()
	var macro := bool(pose.get("macro", false))
	material.call("set_shader_param", "terrain_macro_shadow_enabled", macro)
	if macro:
		material.call("set_shader_param", "terrain_macro_shadow_texture", _macro_texture())
		material.call("set_shader_param", "terrain_macro_shadow_strength", 0.8)
		material.call("set_shader_param", "terrain_macro_shadow_fade_start_meters", 0.0)
		material.call("set_shader_param", "terrain_macro_shadow_fade_end_meters", 1.0)
	if bool(pose.get("haze", false)):
		material.call("set_shader_param", "atmosphere_clear_distance_meters", 8.0)
		material.call("set_shader_param", "atmosphere_full_distance_meters", 420.0)
		material.call("set_shader_param", "atmosphere_high_altitude_clear_distance_meters", 8.0)
		material.call("set_shader_param", "atmosphere_high_altitude_full_distance_meters", 420.0)
		material.call("set_shader_param", "atmosphere_distance_haze_max_opacity", 0.7)
		material.call("set_shader_param", "atmosphere_total_max_opacity", 0.8)
		material.call("set_shader_param", "atmosphere_scattering_energy", 1.0)
	# The camera updates the viewer, atmosphere ray frame and sun-direction uniforms.
	for frame in SETTLE_FRAMES:
		_surface.call("UpdateCamera", _camera.global_position)
		await process_frame


func _macro_texture() -> ImageTexture:
	var image := Image.create_empty(8, 64, false, Image.FORMAT_R8)
	for y in 64:
		var value := 0.15 + 0.85 * (float(y) / 63.0)
		for x in 8:
			image.set_pixel(x, y, Color(value, 0, 0, 1))
	return ImageTexture.create_from_image(image)


func _grab() -> Image:
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("viewport readback failed")
		return Image.new()
	image.convert(Image.FORMAT_RGBA8)
	return image


func _compare(actual: Image, golden: Image) -> Dictionary:
	golden.convert(Image.FORMAT_RGBA8)
	if actual.get_size() != golden.get_size():
		return {"pixels": actual.get_width() * actual.get_height(), "max_diff": 255}
	var a := actual.get_data()
	var g := golden.get_data()
	var pixels := 0
	var max_diff := 0
	for i in range(0, a.size(), 4):
		var pixel_diff := 0
		for c in 4:
			pixel_diff = maxi(pixel_diff, absi(int(a[i + c]) - int(g[i + c])))
		if pixel_diff > 0:
			pixels += 1
			max_diff = maxi(max_diff, pixel_diff)
	return {"pixels": pixels, "max_diff": max_diff}


func _finish(exit_code: int) -> void:
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	for frame in 4:
		await RenderingServer.frame_post_draw
	quit(exit_code)


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	push_error("TERRAIN_SURFACE_INCLUDE_REGRESSION=FAIL " + message)
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	call_deferred("quit", 1)
