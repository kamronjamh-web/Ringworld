extends SceneTree
# Wet world GPU test (docs/storm-revamp-s2-wet-world-design.md section 6). Run (windowed Vulkan, not --headless; no other Godot process running):
#   synthetic (default): godot --path . --script res://tools/wet_world_gpu_test.gd [-- --out=<dir>]
#     Shader contract with hand-set ring_wet_* globals: shelter fixture (roof, facade, underside), puddles only on flat ground and growing from cavities, ripples scale with rain
#     and stop beyond 40 m, fat-drop spots only while FatDrop > 0, hail ripples only while HailIntensity > 0, every receiver class changes under forced wetness and compiles,
#     valid = 0 is the identity, a paused clock gives identical frames, the lens droplets draw nothing at L = 0 and something after a look-up in rain.
#   --world: the production map in the certified gameplay scene at a natural storm: ForceUniform texels, map vs the CPU WetnessModel at 64 points, load / rewind statelessness.
# Prints WET_WORLD_GPU=PASS (or =FAIL <reason> on stderr, exit 1).
const PROBE_SHADER := "res://tools/wet_world_probe.gdshader"
const SIZE := Vector2i(480, 270)
const WALL_X := 60.0
const SUN_DIR := Vector3(-0.6, 0.7, 0.2)
const EXPECT_CHANGE := 0.004
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const HELPER := preload("res://tools/WetWorldCaptureHelper.cs")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")

# name, shader, receiver (wall | sphere | grass), extra uniforms, expect a visible change under wetness
const MATERIALS := [
	{"name": "proxy", "shader": "res://materials/ring_terrain_proxy.gdshader", "receiver": "wall", "params": {"geomorph_enabled": false}, "change": true},
	{"name": "global_world", "shader": "res://materials/ring_terrain_global.gdshader", "receiver": "wall", "params": {"regional_band_distance_clip": false, "terrain_macro_shadow_use_uv": false}, "change": true},
	{"name": "voxel", "shader": "res://materials/voxel_ring_terrain.gdshader", "receiver": "wall", "params": {}, "change": true},
	{"name": "ocean_proxy", "shader": "res://materials/ring_ocean_proxy.gdshader", "receiver": "wall", "params": {"geomorph_enabled": false, "water_wind_clock_enabled": true}, "change": false},
	{"name": "ocean_global", "shader": "res://materials/ring_ocean_global.gdshader", "receiver": "wall", "params": {"water_wind_clock_enabled": true, "fade_start_meters": 0.0, "fade_end_meters": 0.01}, "change": false},
	{"name": "poi_architecture", "shader": "res://materials/ring_poi_architecture.gdshader", "receiver": "wall", "params": {}, "change": true},
	{"name": "poi_architecture_double", "shader": "res://materials/ring_poi_architecture_double_sided.gdshader", "receiver": "wall", "params": {}, "change": true},
	{"name": "construction", "shader": "res://materials/ring_construction_surface.gdshader", "receiver": "wall", "params": {}, "change": true},
	{"name": "ring_tree", "shader": "res://materials/ring_tree.gdshader", "receiver": "sphere", "params": {"vegetation_wind_enabled": false}, "change": true},
	{"name": "imported_tree", "shader": "res://materials/imported_tree.gdshader", "receiver": "sphere", "params": {"vegetation_wind_enabled": false}, "change": true},
	{"name": "imported_tree_impostor", "shader": "res://materials/imported_tree_impostor.gdshader", "receiver": "sphere", "params": {"vegetation_wind_enabled": false}, "change": false},
	{"name": "standing_grass", "shader": "res://materials/ring_standing_grass.gdshader", "receiver": "grass", "params": {}, "change": true},
	{"name": "grass_cards", "shader": "res://materials/ring_grass_cards.gdshader", "receiver": "wall", "params": {}, "change": false},
	{"name": "emitter_standin", "shader": "res://materials/ring_emitter_standin.gdshader", "receiver": "sphere", "params": {"use_instance_custom": false}, "change": true},
	{"name": "scale_anchor", "shader": "res://materials/ring_scale_anchor.gdshader", "receiver": "sphere", "params": {}, "change": true},
]

var failed := false
var failures: Array = []
var world_mode := false
var out_dir := "res://.godot/wet_world_gpu"
var _viewport: SubViewport
var _wall: MeshInstance3D
var _sphere: MeshInstance3D
var _grass: MultiMeshInstance3D
var _wet_world: Node
var _map_texture: ImageTexture
var _occlusion_texture: ImageTexture
var _log: Array = []


func _init() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg == "--world":
			world_mode = true
		elif arg.begins_with("--out="):
			out_dir = arg.substr(6)
	call_deferred("_run")
	create_timer(1500.0 if world_mode else 600.0).timeout.connect(func() -> void: _fail("timeout"))


func _check(ok: bool, message: String) -> void:
	_log.append(("ok   " if ok else "FAIL ") + message)
	if not ok:
		failures.append(message)


func _fail(message: String) -> void:
	push_error("WET_WORLD_GPU=FAIL " + message)
	quit(1)


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir))
	if world_mode:
		await _world()
	else:
		await _synthetic()
	for line in _log:
		print("WET_WORLD_GPU ", line)
	if failures.size() > 0:
		push_error("WET_WORLD_GPU=FAIL " + " | ".join(failures.slice(0, 12)))
		quit(1)
		return
	print("WET_WORLD_GPU=PASS ", "world" if world_mode else "synthetic")
	quit(0)


# ------------------------------------------------------------------ globals

func _set_frame(up_minus_x: bool) -> void:
	# Identity frame: world (x, y, z) -> offset (around, across) from the camera. Probes use up = +Y (around = x, across = z); the receiver wall uses up = -X (around = z, across = y).
	if up_minus_x:
		RenderingServer.global_shader_parameter_set("ring_wet_frame", Projection(Vector4(0, 0, 1, 0), Vector4(0, 1, 0, 0), Vector4(-1, 0, 0, 0), Vector4(0, 0, 0, 0)))
	else:
		RenderingServer.global_shader_parameter_set("ring_wet_frame", Projection(Vector4(1, 0, 0, 0), Vector4(0, 0, 1, 0), Vector4(0, 1, 0, 0), Vector4(0, 0, 0, 0)))


func _set_wet(film: float, soak: float, standing: float, rain: float, valid := true, fat := 0.0, hail := 0.0, layers := 4.0) -> void:
	var image := Image.create(2, 2, false, Image.FORMAT_RGBAH)
	image.fill(Color(film, soak, standing, rain))
	_map_texture = ImageTexture.create_from_image(image)
	RenderingServer.global_shader_parameter_set("ring_wet_map", _map_texture)
	RenderingServer.global_shader_parameter_set("ring_wet_map_frame", Vector4(8192, 8192, 64, 256))
	RenderingServer.global_shader_parameter_set("ring_wet_params", Vector4(1.0, 0.18, 1.0, 1.0 if valid else 0.0))
	RenderingServer.global_shader_parameter_set("ring_wet_clock", Vector4(3.7, layers, 8192, 1000))
	RenderingServer.global_shader_parameter_set("ring_wet_events", Vector4(fat, hail, 0.5, 0.0))
	RenderingServer.global_shader_parameter_set("ring_wet_wind", Vector4(0, 0, 0, 0))


func _set_occlusion(active: bool) -> void:
	# Roof fixture: a 16 m x 16 m map (64 px, 0.25 m texels) from a plane at y = 10, image right = +X, image up = -Z (so up = cross(across, around) = +Y). The left half (x < 0) holds a roof
	# whose top is at y = 4 (6 m below the plane); the right half is open.
	var image := Image.create(64, 64, false, Image.FORMAT_RF)
	image.fill(Color(0, 0, 0, 1))
	for y in 64:
		for x in 32:
			image.set_pixel(x, y, Color(6.0, 0, 0, 1))
	_occlusion_texture = ImageTexture.create_from_image(image)
	RenderingServer.global_shader_parameter_set("ring_precip_occlusion", _occlusion_texture)
	RenderingServer.global_shader_parameter_set("ring_precip_occlusion_origin", Vector4(0, 10, 0, 64))
	RenderingServer.global_shader_parameter_set("ring_precip_occlusion_around", Vector4(0, 0, -1, 8))
	RenderingServer.global_shader_parameter_set("ring_precip_occlusion_across", Vector4(1, 0, 0, 0.25))
	RenderingServer.global_shader_parameter_set("ring_precip_occlusion_state", Vector4(1.0 if active else 0.0, 64, 1, 0))


# ------------------------------------------------------------------ probes

func _probe(mode: int, worlds: PackedVector3Array, normals: PackedVector3Array, up: Vector3, params: Vector4, alpha_pass := false) -> Image:
	var count := worlds.size()
	var padded_worlds := PackedVector3Array(worlds)
	var padded_normals := PackedVector3Array(normals)
	padded_worlds.resize(512)
	padded_normals.resize(512)
	var viewport := SubViewport.new()
	viewport.size = Vector2i(64, 8)
	viewport.disable_3d = true
	viewport.transparent_bg = false
	viewport.use_hdr_2d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var rect := ColorRect.new()
	rect.size = Vector2(64, 8)
	var material := ShaderMaterial.new()
	material.shader = load(PROBE_SHADER)
	material.set_shader_parameter("probe_mode", mode)
	material.set_shader_parameter("probe_world", padded_worlds)
	material.set_shader_parameter("probe_normal", padded_normals)
	material.set_shader_parameter("probe_up", up)
	material.set_shader_parameter("probe_params", params)
	material.set_shader_parameter("probe_alpha_pass", alpha_pass)
	rect.material = material
	viewport.add_child(rect)
	root.add_child(viewport)
	for ignored in 4:
		await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	viewport.queue_free()
	return image


func _probe_pixel(image: Image, index: int) -> Color:
	return image.get_pixel(index % 64, index / 64)


func _plane_points(count: int, extent: float, seed_value := 77) -> PackedVector3Array:
	# Deterministic scatter over an extent x extent metre patch of the plane (x, z).
	var rng := RandomNumberGenerator.new()
	rng.seed = seed_value
	var points := PackedVector3Array()
	for i in count:
		points.append(Vector3(rng.randf() * extent + 3.0, 0.0, rng.randf() * extent + 5.0))
	return points


func _uniform_normals(count: int, normal: Vector3) -> PackedVector3Array:
	var normals := PackedVector3Array()
	for i in count:
		normals.append(normal)
	return normals


func _mean_channel(image: Image, count: int, channel: int) -> float:
	var sum := 0.0
	for i in count:
		sum += _probe_pixel(image, i)[channel]
	return sum / float(maxi(count, 1))


func _count_above(image: Image, count: int, channel: int, threshold: float) -> int:
	var n := 0
	for i in count:
		if _probe_pixel(image, i)[channel] > threshold:
			n += 1
	return n


# ------------------------------------------------------------------ synthetic

func _synthetic() -> void:
	_wet_world = load("res://scripts/Weather/RingWetWorld.cs").new()
	_wet_world.set("Enabled", false)
	root.add_child(_wet_world)
	await process_frame
	await process_frame
	var up := Vector3.UP
	_set_frame(false)

	# --- shelter fixture (roof, facade, underside) with the real S1 exposure function
	_set_wet(0.8, 0.6, 0.9, 0.7)
	_set_occlusion(true)
	var worlds := PackedVector3Array([
		Vector3(4, 0, 0),      # 0 ground in the open
		Vector3(-4, 0, 0),     # 1 ground under the roof
		Vector3(0, 2, 0),      # 2 facade (east wall of the roofed block, faces +X) under open sky
		Vector3(-2, 2, 0),     # 3 interior wall under the roof (faces -X)
		Vector3(-4, 4, 0),     # 4 roof top
		Vector3(-4, 4, 0),     # 5 roof underside
		Vector3(-4, 0, 6),     # 6 ground under the roof near its edge
		Vector3(20, 0, 0),     # 7 outside the occlusion map: exposed
	])
	var normals := PackedVector3Array([Vector3.UP, Vector3.UP, Vector3(1, 0, 0), Vector3(-1, 0, 0), Vector3.UP, Vector3.DOWN, Vector3.UP, Vector3.UP])
	var image := await _probe(0, worlds, normals, up, Vector4())
	var rain_image := await _probe(0, worlds, normals, up, Vector4(), true)
	var open_ground := _probe_pixel(image, 0)
	open_ground.a = _probe_pixel(rain_image, 0).r
	_check(absf(open_ground.r - 0.8) < 0.01 and absf(open_ground.g - 0.6) < 0.01 and absf(open_ground.b - 0.9) < 0.01 and absf(open_ground.a - 0.7) < 0.01, "open ground carries the map values %s" % str(open_ground))
	var roofed := _probe_pixel(image, 1)
	roofed.a = _probe_pixel(rain_image, 1).r
	_check(roofed.r + roofed.g + roofed.b + roofed.a < 0.01, "ground under the roof is dry %s" % str(roofed))
	var facade := _probe_pixel(image, 2)
	_check(facade.r > 0.7 and facade.g > 0.5 and facade.b < 0.001, "facade under open sky is wet, never standing water %s" % str(facade))
	var interior := _probe_pixel(image, 3)
	_check(interior.r + interior.g + interior.b + _probe_pixel(rain_image, 3).r < 0.01, "wall under the roof stays dry %s" % str(interior))
	var roof_top := _probe_pixel(image, 4)
	_check(roof_top.r > 0.7 and roof_top.b > 0.8, "roof top is wet %s" % str(roof_top))
	var underside := _probe_pixel(image, 5)
	_check(underside.r + underside.g + underside.b + _probe_pixel(rain_image, 5).r < 0.01, "underside is dry %s" % str(underside))
	_check(_probe_pixel(image, 6).r < 0.001 and _probe_pixel(image, 7).r > 0.7, "roof edge shelter and outside-map exposure")
	# Inactive map (in cloud or above): exposed everywhere.
	_set_occlusion(false)
	image = await _probe(0, worlds, normals, up, Vector4())
	_check(_probe_pixel(image, 1).r > 0.7, "inactive occlusion map exposes everything")
	_set_occlusion(true)
	# valid = 0 is the identity.
	_set_wet(0.8, 0.6, 0.9, 0.7, false)
	image = await _probe(0, worlds, normals, up, Vector4())
	var zero := 0.0
	for i in 8:
		var pixel := _probe_pixel(image, i)
		zero += pixel.r + pixel.g + pixel.b
	_check(zero < 0.01, "valid = 0 returns zero wetness at every point")
	_set_occlusion(false)
	_set_wet(0.8, 0.6, 0.9, 0.7)

	# --- puddles only on flat ground, growing from cavities, standing-water driven
	var points := _plane_points(256, 48.0)
	var flat := _uniform_normals(256, Vector3.UP)
	var tilted := _uniform_normals(256, Vector3(sin(deg_to_rad(35.0)), cos(deg_to_rad(35.0)), 0.0))
	# Baseline: standing water 0.6 on shallow hollows (cavity 0.8): puddles in the noise lows only.
	var flat_image := await _probe(1, points, flat, up, Vector4(0.6, 0.8, 5.0, 0.0))
	var tilted_image := await _probe(1, points, tilted, up, Vector4(0.6, 0.8, 5.0, 0.0))
	var dry_image := await _probe(1, points, flat, up, Vector4(0.0, 0.8, 5.0, 0.0))
	var hollow_image := await _probe(1, points, flat, up, Vector4(0.6, 1.0, 5.0, 0.0))
	var bump_image := await _probe(1, points, flat, up, Vector4(0.6, 0.0, 5.0, 0.0))
	var low_image := await _probe(1, points, flat, up, Vector4(0.45, 0.8, 5.0, 0.0))
	var high_image := await _probe(1, points, flat, up, Vector4(0.75, 0.8, 5.0, 0.0))
	var far_image := await _probe(1, points, flat, up, Vector4(1.0, 1.0, 200.0, 0.0))
	var flat_count := _count_above(flat_image, 256, 0, 0.5)
	_log.append("info puddle counts: base %d low %d high %d hollow %d bump %d" % [flat_count, _count_above(low_image, 256, 0, 0.5), _count_above(high_image, 256, 0, 0.5), _count_above(hollow_image, 256, 0, 0.5), _count_above(bump_image, 256, 0, 0.5)])
	_check(flat_count > 10 and flat_count < 250, "puddles cover part of flat ground (%d of 256)" % flat_count)
	_check(_count_above(tilted_image, 256, 0, 0.001) == 0 and _count_above(tilted_image, 256, 1, 0.001) == 0, "no puddle on a 35 degree slope")
	_check(_count_above(dry_image, 256, 0, 0.001) == 0, "no standing water, no puddle")
	_check(_count_above(hollow_image, 256, 0, 0.5) > _count_above(bump_image, 256, 0, 0.5), "hollows fill before bumps (%d vs %d)" % [_count_above(hollow_image, 256, 0, 0.5), _count_above(bump_image, 256, 0, 0.5)])
	_check(_count_above(low_image, 256, 0, 0.5) <= flat_count and flat_count < _count_above(high_image, 256, 0, 0.5) and _count_above(low_image, 256, 0, 0.5) < _count_above(high_image, 256, 0, 0.5), "puddles grow with the standing water (%d <= %d < %d)" % [_count_above(low_image, 256, 0, 0.5), flat_count, _count_above(high_image, 256, 0, 0.5)])
	_check(_count_above(far_image, 256, 0, 0.001) == 0, "the puddle mask is gone beyond 150 m")
	var rim_count := _count_above(flat_image, 256, 1, 0.05)
	_check(rim_count > 0, "puddles have a darkened rim band (%d points)" % rim_count)

	# --- ripples: scale with rain, stop beyond 40 m, fat drops and hail only with their signals
	var ripple_points := _plane_points(400, 3.0, 91)
	var ripple_normals := _uniform_normals(400, Vector3.UP)
	var r0 := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(0.0, 0.0, 5.0, 0.0)), 400, 2)
	var r1 := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(0.1, 0.0, 5.0, 0.0)), 400, 2)
	var r5 := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(0.5, 0.0, 5.0, 0.0)), 400, 2)
	var r10 := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(1.0, 0.0, 5.0, 0.0)), 400, 2)
	_check(r0 == 0.0 and r1 > 0.0 and r5 > r1 and r10 > r5, "ripples scale with rain (%.4f %.4f %.4f %.4f)" % [r0, r1, r5, r10])
	_check(_mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(1.0, 0.0, 50.0, 1.0)), 400, 2) == 0.0, "no ripples beyond 40 m")
	var fat_off := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(0.0, 0.0, 5.0, 0.0)), 400, 2)
	var fat_on := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(0.0, 1.0, 5.0, 0.0)), 400, 2)
	_check(fat_off == 0.0 and fat_on > 0.0, "fat-drop ripples appear only with FatDrop where it is not raining (%.4f -> %.4f)" % [fat_off, fat_on])
	var hail_on := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(0.0, 0.0, 5.0, 1.0)), 400, 2)
	_check(hail_on > 0.0, "hail crowns appear with HailIntensity (%.4f)" % hail_on)
	# preset cap: one layer carries less ripple energy than four at a downpour
	_set_wet(0.8, 0.6, 0.9, 0.7, true, 0.0, 0.0, 1.0)
	var one_layer := _mean_channel(await _probe(3, ripple_points, ripple_normals, up, Vector4(1.0, 0.0, 5.0, 0.0)), 400, 2)
	_check(one_layer > 0.0 and one_layer < r10, "ripple layers follow the preset cap (%.4f < %.4f)" % [one_layer, r10])
	_set_wet(0.8, 0.6, 0.9, 0.7)

	# --- fat-drop spots only while FatDrop > 0
	var spot_points := _plane_points(512, 8.0, 53)
	_set_wet(0.8, 0.6, 0.9, 0.0, true, 0.0)
	var spot_normals := _uniform_normals(512, Vector3.UP)
	var spots_off := _count_above(await _probe(2, spot_points, spot_normals, up, Vector4(0, 0, 5.0, 0)), 512, 0, 0.001)
	_set_wet(0.8, 0.6, 0.9, 0.0, true, 0.3)
	var spots_low := _count_above(await _probe(2, spot_points, spot_normals, up, Vector4(0, 0, 5.0, 0)), 512, 0, 0.001)
	_set_wet(0.8, 0.6, 0.9, 0.0, true, 0.9)
	var spots_high := _count_above(await _probe(2, spot_points, spot_normals, up, Vector4(0, 0, 5.0, 0)), 512, 0, 0.001)
	var spots_far := _count_above(await _probe(2, spot_points, spot_normals, up, Vector4(0, 0, 80.0, 0)), 512, 0, 0.001)
	_check(spots_off == 0 and spots_low > 0 and spots_high > spots_low and spots_far == 0, "fat-drop spots follow FatDrop (%d, %d, %d, far %d)" % [spots_off, spots_low, spots_high, spots_far])
	_set_wet(0.8, 0.6, 0.9, 0.7)

	await _receivers()
	await _lens()
	_set_wet(0.0, 0.0, 0.0, 0.0, false)


# ------------------------------------------------------------------ receivers

func _build_scene() -> void:
	_viewport = SubViewport.new()
	_viewport.own_world_3d = true
	_viewport.size = SIZE
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.45, 0.5, 0.6)
	environment.ambient_light_energy = 0.35
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	_viewport.add_child(world_environment)
	var camera := Camera3D.new()
	camera.position = Vector3(WALL_X - 30.0, 0.0, 0.0)
	camera.current = true
	camera.fov = 70.0
	camera.far = 400.0
	_viewport.add_child(camera)
	camera.look_at(Vector3(WALL_X, 0.0, 0.0), Vector3.UP)
	var sun := DirectionalLight3D.new()
	sun.light_energy = 1.0
	sun.shadow_enabled = false
	_viewport.add_child(sun)
	var direction := SUN_DIR.normalized()
	sun.transform = Transform3D(Basis.looking_at(-direction, Vector3.UP), Vector3.ZERO)
	_wall = MeshInstance3D.new()
	_wall.mesh = _wall_mesh()
	_viewport.add_child(_wall)
	_sphere = MeshInstance3D.new()
	var sphere_mesh := SphereMesh.new()
	sphere_mesh.radius = 5.0
	sphere_mesh.height = 10.0
	sphere_mesh.radial_segments = 48
	sphere_mesh.rings = 24
	_sphere.mesh = sphere_mesh
	_sphere.position = Vector3(58.0, -9.6, -1.3)
	_viewport.add_child(_sphere)
	_grass = MultiMeshInstance3D.new()
	var multimesh := MultiMesh.new()
	multimesh.transform_format = MultiMesh.TRANSFORM_3D
	multimesh.use_custom_data = true
	multimesh.mesh = _grass_blade_mesh()
	multimesh.instance_count = 1
	multimesh.set_instance_transform(0, Transform3D(Basis(Vector3(0, 0, 8), Vector3(0, 6, 0), Vector3(1, 0, 0)), Vector3(56.0, -8.0, 0.0)))
	multimesh.set_instance_custom_data(0, Color(0.7, 0.3, 0.0, 0.0))
	_grass.multimesh = multimesh
	_viewport.add_child(_grass)


func _wall_mesh() -> ArrayMesh:
	var half_y := 40.0
	var half_z := 70.0
	var vertices := PackedVector3Array([Vector3(WALL_X, -half_y, -half_z), Vector3(WALL_X, half_y, -half_z), Vector3(WALL_X, half_y, half_z), Vector3(WALL_X, -half_y, half_z)])
	var normals := PackedVector3Array([Vector3(-1, 0, 0), Vector3(-1, 0, 0), Vector3(-1, 0, 0), Vector3(-1, 0, 0)])
	var colors := PackedColorArray([Color(0.35, 0.55, 0.25), Color(0.45, 0.4, 0.3), Color(0.3, 0.5, 0.35), Color(0.5, 0.45, 0.25)])
	var uvs := PackedVector2Array([Vector2(0, 0), Vector2(0, 1), Vector2(1, 1), Vector2(1, 0)])
	var indices := PackedInt32Array([0, 1, 2, 0, 2, 3])
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_COLOR] = colors
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _grass_blade_mesh() -> ArrayMesh:
	var vertices := PackedVector3Array([Vector3(-0.5, 0, 0), Vector3(0.5, 0, 0), Vector3(0.5, 1, 0), Vector3(-0.5, 1, 0)])
	var normals := PackedVector3Array([Vector3(0, 0, 1), Vector3(0, 0, 1), Vector3(0, 0, 1), Vector3(0, 0, 1)])
	var colors := PackedColorArray([Color(0, 0, 0, 0), Color(0, 0, 0, 0), Color(0, 0, 0, 0), Color(0, 0, 0, 0)])
	var uvs := PackedVector2Array([Vector2(0, 0), Vector2(1, 0), Vector2(1, 1), Vector2(0, 1)])
	var indices := PackedInt32Array([0, 1, 2, 0, 2, 3])
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_COLOR] = colors
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _make_material(entry: Dictionary) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = load(entry.shader)
	var names := {}
	for uniform in material.shader.get_shader_uniform_list():
		names[uniform.name] = true
	var values := {
		"ring_center": Vector3.ZERO,
		"camera_world_position": Vector3(WALL_X - 30.0, 0.0, 0.0),
		"sun_direction": SUN_DIR.normalized(),
		"day_start_dot": -0.2,
		"day_full_dot": 0.35,
		"night_tint": Vector3(0.30, 0.45, 0.70),
		"night_direct_light_factor": 0.2,
		"day_direct_light_factor": 1.1,
		"shadow_floor": 0.12,
		"ring_width_meters": 40.0,
	}
	for key in values:
		if names.has(key):
			material.set_shader_parameter(key, values[key])
	for key in entry.params:
		material.set_shader_parameter(key, entry.params[key])
	return material


func _show_receiver(entry: Dictionary, material: ShaderMaterial) -> void:
	_wall.visible = entry.receiver == "wall"
	_sphere.visible = entry.receiver == "sphere"
	_grass.visible = entry.receiver == "grass"
	_wall.material_override = material if entry.receiver == "wall" else null
	_sphere.material_override = material if entry.receiver == "sphere" else null
	_grass.material_override = material if entry.receiver == "grass" else null


func _grab() -> Image:
	for ignored in 6:
		await process_frame
	await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	image.convert(Image.FORMAT_RGBA8)
	return image


func _changed_pixels(a: Image, b: Image, threshold := 3) -> int:
	if a.get_size() != b.get_size():
		return 1 << 30
	var da := a.get_data()
	var db := b.get_data()
	var count := 0
	for i in range(0, da.size(), 4):
		if absi(da[i] - db[i]) > threshold or absi(da[i + 1] - db[i + 1]) > threshold or absi(da[i + 2] - db[i + 2]) > threshold:
			count += 1
	return count


func _mean_abs_diff(a: Image, b: Image) -> float:
	if a.get_size() != b.get_size():
		return 1.0
	var da := a.get_data()
	var db := b.get_data()
	var sum := 0
	var count := 0
	for i in range(0, da.size(), 4):
		sum += absi(da[i] - db[i]) + absi(da[i + 1] - db[i + 1]) + absi(da[i + 2] - db[i + 2])
		count += 3
	return float(sum) / float(maxi(count, 1)) / 255.0


func _receivers() -> void:
	_build_scene()
	_set_frame(true)
	for entry in MATERIALS:
		var material := _make_material(entry)
		_show_receiver(entry, material)
		_set_wet(0.0, 0.0, 0.0, 0.0, false)
		var dry := await _grab()
		var dry_again := await _grab()
		_set_wet(1.0, 1.0, 1.0, 0.0, true)
		var wet := await _grab()
		var wet_again := await _grab()
		# valid = 0 with a loud map is still the dry image (the dry identity).
		_set_wet(1.0, 1.0, 1.0, 0.8, false)
		var dry_loud := await _grab()
		_set_wet(1.0, 1.0, 1.0, 0.8, true, 0.6)
		var rain := await _grab()
		var change := _mean_abs_diff(dry, wet)
		var rain_change := _mean_abs_diff(dry, rain)
		dry.save_png(ProjectSettings.globalize_path(out_dir.path_join(entry.name + "_dry.png")))
		wet.save_png(ProjectSettings.globalize_path(out_dir.path_join(entry.name + "_wet.png")))
		rain.save_png(ProjectSettings.globalize_path(out_dir.path_join(entry.name + "_rain.png")))
		_check(_mean_abs_diff(dry, dry_again) == 0.0, "%s: two dry frames are identical" % entry.name)
		_check(_mean_abs_diff(dry, dry_loud) == 0.0, "%s: valid = 0 ignores the map (identity)" % entry.name)
		_check(_mean_abs_diff(wet, wet_again) == 0.0, "%s: paused clock gives two identical wet frames" % entry.name)
		var changed := maxi(_changed_pixels(dry, wet), _changed_pixels(dry, rain))
		if entry.change:
			_check(changed >= 30 or change > EXPECT_CHANGE or rain_change > EXPECT_CHANGE, "%s changes under forced wetness (wet %.4f, rain %.4f, %d px)" % [entry.name, change, rain_change, changed])
		else:
			_log.append("info %s wet change %.4f rain change %.4f, %d px changed (no change required: far / distance gated)" % [entry.name, change, rain_change, changed])
	_set_wet(0.0, 0.0, 0.0, 0.0, false)


# ------------------------------------------------------------------ lens

func _lens() -> void:
	var layer := CanvasLayer.new()
	layer.layer = -10
	var backdrop := TextureRect.new()
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	var image := Image.create(256, 256, false, Image.FORMAT_RGB8)
	for y in 256:
		for x in 256:
			var stripe := 0.15 + 0.7 * float(((x / 16) + (y / 16)) % 2)
			image.set_pixel(x, y, Color(stripe, 0.5 * stripe + 0.1, 1.0 - stripe))
	backdrop.texture = ImageTexture.create_from_image(image)
	backdrop.stretch_mode = TextureRect.STRETCH_SCALE
	layer.add_child(backdrop)
	root.add_child(layer)
	var lens: CanvasLayer = load("res://scripts/Weather/RingLensDroplets.cs").new()
	root.add_child(lens)
	lens.set("LensWetnessOverride", 0.0)
	lens.set("CameraSource", null)
	for ignored in 6:
		await process_frame
	await RenderingServer.frame_post_draw
	var hidden_image := root.get_viewport().get_texture().get_image()
	_check(not bool(lens.get("Drawing")), "lens droplets draw nothing at L = 0")
	lens.set("LensWetnessOverride", 0.9)
	for ignored in 6:
		await process_frame
	await RenderingServer.frame_post_draw
	var visible_image := root.get_viewport().get_texture().get_image()
	_check(bool(lens.get("Drawing")), "lens droplets draw at L = 0.9")
	hidden_image.convert(Image.FORMAT_RGBA8)
	visible_image.convert(Image.FORMAT_RGBA8)
	var diff := _mean_abs_diff(hidden_image, visible_image)
	_check(diff > 0.01, "lens droplets visibly change the frame (%.4f)" % diff)
	hidden_image.save_png(ProjectSettings.globalize_path(out_dir.path_join("lens_l0.png")))
	visible_image.save_png(ProjectSettings.globalize_path(out_dir.path_join("lens_l09.png")))
	lens.set("LensWetnessOverride", 0.9)
	for ignored in 4:
		await process_frame
	await RenderingServer.frame_post_draw
	var again := root.get_viewport().get_texture().get_image()
	again.convert(Image.FORMAT_RGBA8)
	_check(_mean_abs_diff(visible_image, again) == 0.0, "paused clock gives two identical lens frames")
	lens.set("LensWetnessOverride", 0.0)
	await process_frame
	await process_frame
	_check(not bool(lens.get("Drawing")), "lens rect hides again at L = 0")
	lens.queue_free()
	layer.queue_free()


# ------------------------------------------------------------------ world (production map)

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false


var _game: Node
var _explorer: Node
var _surface: Node
var _weather: Node
var _fixture := ""
var _helper: RefCounted
var _around := 8150667.0
var _across := 119474.0


func _frames(count: int) -> void:
	for ignored in count:
		await process_frame


func _settle(seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame


func _world() -> void:
	_fixture = "res://.godot/atmosphere_visual/wet_world_%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var prior := root.get_node_or_null("WorldSession")
	if prior != null:
		prior.name = "WorldSessionOriginalWet"
	var session := SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	_game = await _create_game()
	if _game == null:
		return
	_explorer = _game.get_node_or_null("Explorer")
	_surface = _game.get_node_or_null("Terrain3DGameplay")
	_weather = _game.get_node_or_null("RingVolumetricClouds")
	var wet: Node = _game.get_node_or_null("WetWorld")
	var drawer := _game.get_node_or_null("LiveTuningDrawer")
	if _explorer == null or _surface == null or _weather == null or wet == null:
		_check(false, "missing nodes (explorer, surface, weather, WetWorld)")
		return
	_helper = HELPER.new()
	_weather.set("CloudSimulationPaused", true)
	var storm := _helper.call("FindStorm", _weather, 1800.0, 100000.0) as Dictionary
	_check(bool(storm.get("ok", false)), "natural storm found")
	if not bool(storm.get("ok", false)):
		return
	print("STORM ", JSON.stringify(storm))
	await _place(float(storm["around"]), float(storm["across"]))
	var clock: Node = _game.get_node_or_null("RingDayNightCycle")
	if clock != null:
		clock.set("Paused", true)
		clock.call("SetCycleFraction", 0.5)
	var last_rain := float(storm["last_rain"])
	var first_rain := float(storm["first_rain"])

	# ForceUniform: every texel gets exactly the forced values; the map stays valid.
	wet.call("ForceUniform", 0.7, 0.5, 0.3, 0.2)
	wet.call("RequestReadback")
	await _settle(2.5)
	var floats: PackedFloat32Array = wet.call("ReadbackFloats")
	_check(bool(wet.get("MapPublished")) and bool(wet.get("Valid")), "forced map is published and valid")
	if floats.size() >= 256 * 256 * 4:
		var worst := 0.0
		for i in range(0, floats.size(), 997 * 4):
			worst = maxf(worst, maxf(absf(floats[i] - 0.7), maxf(absf(floats[i + 1] - 0.5), maxf(absf(floats[i + 2] - 0.3), absf(floats[i + 3] - 0.2)))))
		_check(worst < 0.01, "forced texels equal the forced values (worst %.4f)" % worst)
	else:
		_check(false, "forced readback unavailable")
	wet.call("ClearForce")

	# Map vs the CPU model at 64 points, pinned sun, rain stopped 300 s ago (so the history carries the value; rain now is zero).
	var results := await _map_vs_cpu(wet, last_rain + 300.0, "post300")
	_check(results.worst < 0.02, "map matches the CPU WetnessModel 300 s after the rain (worst %.4f over %d points, %d wet)" % [results.worst, results.count, results.wet_points])
	var during := await _map_vs_cpu(wet, 0.5 * (first_rain + last_rain), "core")
	_check(during.wet_points > 20, "map is wet over the core (%d of %d points)" % [during.wet_points, during.count])
	var late := await _map_vs_cpu(wet, last_rain + 90.0, "post90")
	_check(late.worst < 0.02, "map matches the CPU WetnessModel 90 s after the rain (worst %.4f over %d points)" % [late.worst, late.count])

	# Stateless: load and rewind give the same map as a continuous run.
	var target := last_rain + 200.0
	# Continuous run: the clock advances at real time (1/60 s per frame) over the last 4 s; S3 refreshes one history lag per 0.125 s of wall time, so every lag is at most about a second old.
	await _jump_time(target - 4.0)
	for step in 240:
		_weather.set("CloudSimulationTimeSeconds", target - 4.0 + float(step + 1) / 60.0)
		await process_frame
	await _settle(0.3)
	var continuous := await _map_read(wet)
	var jumped_there := await _map_at(wet, first_rain - 400.0, true)
	var rewound := await _map_at(wet, target, true)
	var diff := 0.0
	var diff_at := -1
	var big := 0
	for i in range(0, mini(continuous.size(), rewound.size()), 13 * 4):
		for c in 3:
			var d := absf(continuous[i + c] - rewound[i + c])
			if d > diff:
				diff = d
				diff_at = i + c
			if d > 0.02:
				big += 1
	if diff_at >= 0:
		var texel := diff_at / 4
		print("REWIND_DIFF texel=(", texel % 256, ",", texel / 256, ") channel=", diff_at % 4, " continuous=", continuous[diff_at], " rewound=", rewound[diff_at], " samples_over_0.02=", big)
	_check(continuous.size() == rewound.size() and rewound.size() > 0 and diff < 0.02, "load and rewind give the same map as a continuous run (worst %.4f)" % diff)
	_check(jumped_there.size() > 0, "far jump read")
	# Dry fast path: far from any storm in clear weather the receivers are switched off.
	_weather.set("CloudSimulationTimeSeconds", 0.0)
	print("WET_WORLD_STATUS valid=", wet.get("Valid"), " dispatches=", wet.get("DispatchCount"))


# Sets the cloud clock and waits ten frames. (S3's observer used to refresh its stale history lags only when the clock lattice moved; it now keeps evaluating while any lag is stale, so a held clock is fine.)
func _jump_time(t: float) -> void:
	_weather.set("CloudSimulationTimeSeconds", t)
	for i in 12:
		await process_frame


func _map_read(wet: Node) -> PackedFloat32Array:
	wet.call("RequestReadback")
	await _settle(1.5)
	var floats: PackedFloat32Array = wet.call("ReadbackFloats")
	return floats


func _map_at(wet: Node, time: float, force_refresh: bool) -> PackedFloat32Array:
	await _jump_time(time)
	await _settle(4.0)
	return await _map_read(wet)


func _map_vs_cpu(wet: Node, time: float, tag: String) -> Dictionary:
	await _jump_time(time)
	wet.set("SunOverride", 0.6)
	await _settle(5.0)
	var floats: PackedFloat32Array = await _map_read(wet)
	var comparison := _helper.call("CompareMapToModel", wet, _weather, floats, 64, 0.6) as Dictionary
	print("MAP_VS_CPU ", tag, " ", JSON.stringify(comparison))
	return {"worst": float(comparison.get("worst", 1.0)), "count": int(comparison.get("count", 0)), "wet_points": int(comparison.get("wet_points", 0))}


func _place(a: float, z: float) -> void:
	_around = a
	_across = z
	var ground := float(_surface.call("SampleHeight", a, z)) + 1.1
	_explorer.process_mode = Node.PROCESS_MODE_INHERIT
	_explorer.call("SetCanonicalSpawnMeters", a, ground, z)
	_explorer.call("SetPresentationView", 0.0, 0.0)
	await _wait_ready()
	_explorer.process_mode = Node.PROCESS_MODE_DISABLED


func _wait_ready() -> void:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var diagnostics: Dictionary = _surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = diagnostics.get("streaming", {})
		var native: Dictionary = diagnostics.get("native", {})
		var preparation: Dictionary = native.get("preparation", {})
		var placed: bool = absf(float(diagnostics.get("player_around", NAN)) - _around) < 1.0 and absf(float(diagnostics.get("player_across", NAN)) - _across) < 1.0
		var ready := bool(diagnostics.get("ready", false)) and bool(diagnostics.get("visual_coverage", false)) and int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false)) and bool(preparation.get("ready", false))
		stable = stable + 1 if placed and ready else 0
		if stable >= 12:
			return
	_check(false, "Terrain3D workload did not settle")


func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null:
		_fail("missing terrain backend selector")
		return null
	selector.select(2)
	selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("runtime generation preparation failed")
		return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate()
	var target := next.get_node_or_null("LiveTuningDrawer")
	if target != null:
		for item in [["InstallGrassDefaults", "grass.tres"], ["InstallVegetationDefaults", "vegetation.tres"], ["InstallWaterDefaults", "water.tres"], ["InstallAntiAliasingDefaults", "anti_aliasing.tres"], ["InstallShadowDefaults", "shadows.tres"], ["InstallSurfaceTerrainDefaults", "surface.tres"], ["InstallAtmosphereDefaults", "atmosphere.tres"]]:
			target.call(str(item[0]), _fixture.path_join(str(item[1])))
		target.call("InstallWetWorldDefaults", _fixture.path_join("wet_world.tres"), _fixture.path_join("surface.tres"))
	var wet_node := next.get_node_or_null("WetWorld")
	if wet_node != null:
		wet_node.set("PreferencesPath", _fixture.path_join("wet_world.tres"))
	launcher.call("_configure_game", next, 5000000.0)
	root.add_child(next)
	current_scene = next
	launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			return next
	_fail("runtime Terrain3D startup timed out")
	return null
