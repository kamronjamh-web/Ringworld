extends SceneTree

## Shadow revamp S3 - behaviour-identity pixel test for the shared shadow composition (docs/shadow-revamp-plan.md S3).
## A synthetic scene (shadowed StarLight + a box casting onto a wall, a planet directional light, an omni) is rendered once per
## material x variant with per-material uniforms only (use_globals = false), synthetic near/far cloud textures, synthetic macro
## and planet stripe textures; the far weather cloud shadow is enabled but has no baked map. Variants: shadow_overlap_darkening 0 / 0.5 and
## ringworld_hub_light_weight 0 / 0.6.
##
##   capture : godot --path . --script res://tools/shadow_compose_identity_gpu_test.gd -- --capture --out=<dir>
##   compare : godot --path . --script res://tools/shadow_compose_identity_gpu_test.gd -- --compare --golden=<dir> --max-diff=N [--out=<dir>]
##   debug   : add --debug-modes to also render ringworld_shadow_debug_mode 1..5 per material and check they are finite / non-uniform
## Requires a real Vulkan/Forward+ renderer (do not pass --headless). Goldens are machine specific. Output stays under .godot/.
## Not driven here: imported_tree_impostor (needs the baked variant data textures) and the grass cards / Terrain3D (covered by
## tools/terrain_surface_include_regression.gd).

const SIZE := Vector2i(480, 270)
const WALL_X := 60.0
const SETTLE_FRAMES := 8
const SUN_DIR := Vector3(-0.6, 0.7, 0.2)
const PLANET_DIR := Vector3(-0.5, 0.6, -0.6)
const OVERLAPS := [0.0, 0.5]
const HUBS := [0.0, 0.6]

# name, shader, receiver (wall | sphere | grass), extra uniforms
const MATERIALS := [
	{"name": "proxy", "shader": "res://materials/ring_terrain_proxy.gdshader", "receiver": "wall", "params": {"geomorph_enabled": false}},
	{"name": "global_world", "shader": "res://materials/ring_terrain_global.gdshader", "receiver": "wall", "params": {"regional_band_distance_clip": false, "terrain_macro_shadow_use_uv": false}},
	{"name": "global_uv", "shader": "res://materials/ring_terrain_global.gdshader", "receiver": "wall", "params": {"regional_band_distance_clip": false, "terrain_macro_shadow_use_uv": true}},
	{"name": "voxel", "shader": "res://materials/voxel_ring_terrain.gdshader", "receiver": "wall", "params": {}},
	{"name": "ocean_proxy", "shader": "res://materials/ring_ocean_proxy.gdshader", "receiver": "wall", "params": {"geomorph_enabled": false, "water_wind_clock_enabled": true}},
	{"name": "ocean_global", "shader": "res://materials/ring_ocean_global.gdshader", "receiver": "wall", "params": {"water_wind_clock_enabled": true, "fade_start_meters": 0.0, "fade_end_meters": 0.01}},
	{"name": "ring_tree", "shader": "res://materials/ring_tree.gdshader", "receiver": "sphere", "params": {"vegetation_wind_enabled": false}},
	{"name": "imported_tree", "shader": "res://materials/imported_tree.gdshader", "receiver": "sphere", "params": {"vegetation_wind_enabled": false}},
	{"name": "standing_grass", "shader": "res://materials/ring_standing_grass.gdshader", "receiver": "grass", "params": {}},
	{"name": "emitter_standin", "shader": "res://materials/ring_emitter_standin.gdshader", "receiver": "sphere", "params": {"use_instance_custom": false}},
	{"name": "scale_anchor", "shader": "res://materials/ring_scale_anchor.gdshader", "receiver": "sphere", "params": {}},
]

var failed := false
var _capture := false
var _compare := false
var _debug_modes := false
var _out_dir := "res://.godot/shadow_compose_identity"
var _golden_dir := ""
var _max_diff := 0
var _viewport: SubViewport
var _sun: DirectionalLight3D
var _planet: DirectionalLight3D
var _omni: OmniLight3D
var _wall: MeshInstance3D
var _sphere: MeshInstance3D
var _grass: MultiMeshInstance3D
var _textures := {}
var _globals_present := {}


func _init() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg == "--capture":
			_capture = true
		elif arg == "--compare":
			_compare = true
		elif arg == "--debug-modes":
			_debug_modes = true
		elif arg.begins_with("--out="):
			_out_dir = arg.substr(6)
		elif arg.begins_with("--golden="):
			_golden_dir = arg.substr(9)
		elif arg.begins_with("--max-diff="):
			_max_diff = int(arg.substr(11))
	call_deferred("_run")
	create_timer(600.0).timeout.connect(func() -> void: _fail("timeout"))


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	if _capture == _compare and not _debug_modes:
		_fail("pass --capture or --compare (and optionally --debug-modes)")
		return
	if _compare and _golden_dir == "":
		_fail("--compare needs --golden=<dir>")
		return
	for name in ["ringworld_hub_light_weight", "ringworld_shadow_debug_mode"]:
		_globals_present[name] = ProjectSettings.has_setting("shader_globals/" + name)
	_build_scene()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out_dir))
	var summary: Array = []
	var any_diff := false
	var total_cases := 0
	if _capture or _compare:
		for entry in MATERIALS:
			var material := _make_material(entry)
			_show_receiver(entry, material)
			for hub in HUBS:
				for overlap in OVERLAPS:
					_apply_variant(material, overlap, hub)
					var case_name := "%s_od%s_hub%s" % [entry.name, str(overlap), str(hub)]
					var image := await _grab()
					if failed:
						return
					total_cases += 1
					var out_path := ProjectSettings.globalize_path(_out_dir.path_join(case_name + ".png"))
					image.save_png(out_path)
					if _capture:
						summary.append("%s captured nonblack=%d" % [case_name, _nonblack(image)])
						continue
					var golden := Image.load_from_file(ProjectSettings.globalize_path(_golden_dir.path_join(case_name + ".png")))
					if golden == null or golden.is_empty():
						_fail("missing golden for " + case_name)
						return
					var result := _diff(image, golden)
					summary.append("%s differing_px=%d max_channel_diff=%d" % [case_name, result.pixels, result.max_diff])
					if int(result.max_diff) > _max_diff:
						any_diff = true
	if _debug_modes:
		if not _globals_present["ringworld_shadow_debug_mode"]:
			summary.append("DEBUG_MODES skipped: global ringworld_shadow_debug_mode not defined")
		else:
			for entry in MATERIALS:
				var material := _make_material(entry)
				if entry.name == "standing_grass":
					# Production configuration: the grass identifies the primary sun itself (off in the identity cases to match the baseline).
					material.set_shader_parameter("grass_primary_sun_gate_enabled", true)
					material.set_shader_parameter("grass_primary_sun_direction", SUN_DIR.normalized())
				_show_receiver(entry, material)
				_apply_variant(material, 0.0, 0.0)
				RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
				var base := await _grab()
				for mode in range(1, 6):
					RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", mode)
					var image := await _grab()
					if failed:
						return
					image.save_png(ProjectSettings.globalize_path(_out_dir.path_join("debug_%s_mode%d.png" % [entry.name, mode])))
					var stats := _stats(image)
					var changed: int = _diff(image, base).pixels
					summary.append("DEBUG %s mode=%d min=%d max=%d distinct=%d changed_px=%d" % [entry.name, mode, stats.min, stats.max, stats.distinct, changed])
				RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
	for line in summary:
		print("SHADOW_COMPOSE_IDENTITY ", line)
	if any_diff:
		_fail("pixels differ from golden (actual images in %s)" % _out_dir)
		return
	print("SHADOW_COMPOSE_IDENTITY=%s cases=%d" % ["CAPTURED" if _capture else ("PASS" if _compare else "DEBUG_ONLY"), total_cases])
	_finish(0)


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
	environment.ssao_enabled = false
	environment.glow_enabled = false
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
	_sun = DirectionalLight3D.new()
	_sun.light_energy = 1.0
	_sun.shadow_enabled = true
	_sun.directional_shadow_max_distance = 200.0
	_sun.shadow_bias = 0.02
	_sun.shadow_normal_bias = 0.5
	_viewport.add_child(_sun)
	_orient(_sun, SUN_DIR)
	_planet = DirectionalLight3D.new()
	_planet.light_energy = 0.4
	_planet.light_color = Color(0.55, 0.65, 1.0)
	_viewport.add_child(_planet)
	_orient(_planet, PLANET_DIR)
	_omni = OmniLight3D.new()
	_omni.light_energy = 2.0
	_omni.omni_range = 40.0
	_omni.position = Vector3(WALL_X - 8.0, 6.0, -8.0)
	_viewport.add_child(_omni)
	# Shadow caster between the sun and every receiver.
	var box := MeshInstance3D.new()
	var box_mesh := BoxMesh.new()
	box_mesh.size = Vector3(4.0, 8.0, 4.0)
	box.mesh = box_mesh
	box.position = Vector3(50.0, -2.0, 2.0)
	box.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_ON
	_viewport.add_child(box)
	_wall = MeshInstance3D.new()
	_wall.mesh = _wall_mesh()
	_wall.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
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
	# side = +Z (8 m wide), up = +Y (6 m tall), face = +X toward the wall.
	multimesh.set_instance_transform(0, Transform3D(Basis(Vector3(0, 0, 8), Vector3(0, 6, 0), Vector3(1, 0, 0)), Vector3(56.0, -8.0, 0.0)))
	multimesh.set_instance_custom_data(0, Color(0.7, 0.3, 0.0, 0.0))
	_grass.multimesh = multimesh
	_viewport.add_child(_grass)
	_textures = {
		"near": _cloud_texture(64, 0.0),
		"far": _cloud_texture(32, 1.7),
		"macro": _stripe_texture(32, 64, 1.0),
		"planet": _stripe_texture(32, 64, 2.3),
	}
	if _globals_present["ringworld_hub_light_weight"]:
		RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", 0.0)


func _orient(light: DirectionalLight3D, to_light: Vector3) -> void:
	var direction := to_light.normalized()
	var up_hint := Vector3.RIGHT if absf(direction.dot(Vector3.UP)) > 0.95 else Vector3.UP
	light.transform = Transform3D(Basis.looking_at(-direction, up_hint), Vector3.ZERO)


func _wall_mesh() -> ArrayMesh:
	var half_y := 40.0
	var half_z := 70.0
	var vertices := PackedVector3Array([
		Vector3(WALL_X, -half_y, -half_z), Vector3(WALL_X, half_y, -half_z),
		Vector3(WALL_X, half_y, half_z), Vector3(WALL_X, -half_y, half_z)])
	var normals := PackedVector3Array([Vector3(-1, 0, 0), Vector3(-1, 0, 0), Vector3(-1, 0, 0), Vector3(-1, 0, 0)])
	var colors := PackedColorArray([Color(0.35, 0.55, 0.25), Color(0.45, 0.4, 0.3), Color(0.3, 0.5, 0.35), Color(0.5, 0.45, 0.25)])
	var uvs := PackedVector2Array([Vector2(0, 0), Vector2(0, 1), Vector2(1, 1), Vector2(1, 0)])
	# Clockwise as seen from the camera (-X side) so the front face is visible to cull_back receivers.
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


func _cloud_texture(size: int, phase: float) -> ImageTexture:
	# RG phase map (combine_ring_cloud_shadow_phases reads both channels): diagonal soft bands.
	var image := Image.create_empty(size, size, false, Image.FORMAT_RG8)
	for y in size:
		for x in size:
			var u := float(x) / float(size)
			var v := float(y) / float(size)
			var r := clampf(0.5 + 0.55 * sin((u * 3.0 + v * 2.0) * TAU + phase), 0.0, 1.0)
			var g := clampf(0.5 + 0.55 * sin((u * 2.0 - v * 4.0) * TAU + phase * 0.5), 0.0, 1.0)
			image.set_pixel(x, y, Color(r, g, 0.0, 1.0))
	return ImageTexture.create_from_image(image)


func _stripe_texture(width: int, height: int, phase: float) -> ImageTexture:
	var image := Image.create_empty(width, height, false, Image.FORMAT_R8)
	for y in height:
		for x in width:
			var u := float(x) / float(width)
			var v := float(y) / float(height)
			var value := clampf(0.5 + 0.5 * sin(u * TAU * 3.0 + phase) * sin(v * TAU * 5.0 + phase * 0.7), 0.0, 1.0)
			image.set_pixel(x, y, Color(value, 0.0, 0.0, 1.0))
	return ImageTexture.create_from_image(image)


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
		# local projected cloud shadow, per-material uniforms only
		"local_cloud_shadow_enabled": true,
		"local_cloud_shadow_near_enabled": true,
		"local_cloud_shadow_far_enabled": true,
		"local_cloud_shadow_use_globals": false,
		"local_cloud_shadow_texture": _textures.near,
		"local_cloud_shadow_far_texture": _textures.far,
		"local_cloud_shadow_center_world": Vector3(WALL_X, 0.0, 0.0),
		"local_cloud_shadow_far_center_world": Vector3(WALL_X, 0.0, 0.0),
		"local_cloud_shadow_axis_around": Vector3(0, 0, 1),
		"local_cloud_shadow_axis_across": Vector3(0, 1, 0),
		"local_cloud_shadow_axis_up": Vector3(-1, 0, 0),
		"local_cloud_shadow_far_axis_around": Vector3(0, 0, 1),
		"local_cloud_shadow_far_axis_across": Vector3(0, 1, 0),
		"local_cloud_shadow_far_axis_up": Vector3(-1, 0, 0),
		"local_cloud_shadow_radius_meters": 40.0,
		"local_cloud_shadow_far_radius_meters": 90.0,
		"local_cloud_shadow_near_start_meters": 0.0,
		"local_cloud_shadow_near_end_meters": 1.0,
		"local_cloud_shadow_strength": 0.4,
		"local_cloud_shadow_far_strength": 0.5,
		# far weather cloud shadow enabled per material; its globals (ring_far_weather_*) stay at their
		# project defaults (no baked map), so it contributes nothing and the cascade composition is isolated
		"analytic_cloud_shadow_enabled": true,
		# terrain macro + planet stripe masks
		"terrain_macro_shadow_enabled": true,
		"terrain_macro_shadow_texture": _textures.macro,
		"terrain_macro_shadow_strength": 0.45,
		"terrain_macro_shadow_fade_start_meters": 0.0,
		"terrain_macro_shadow_fade_end_meters": 1.0,
		"terrain_planet_shadow_enabled": true,
		"terrain_planet_shadow_texture": _textures.planet,
		"terrain_planet_shadow_direction": PLANET_DIR.normalized(),
		"terrain_planet_shadow_strength": 0.4,
		"terrain_planet_shadow_fade_start_meters": 0.0,
		"terrain_planet_shadow_fade_end_meters": 1.0,
		# tree / grass receivers
		"tree_tint": Vector3(0.3, 0.7, 0.25),
	}
	for key in values:
		if names.has(key):
			material.set_shader_parameter(key, values[key])
	var params: Dictionary = entry.params
	for key in params:
		if not names.has(key):
			_fail("%s has no uniform %s" % [entry.name, key])
			return material
		material.set_shader_parameter(key, params[key])
	return material


func _show_receiver(entry: Dictionary, material: ShaderMaterial) -> void:
	var kind: String = entry.receiver
	_wall.visible = kind == "wall"
	_sphere.visible = kind == "sphere"
	_grass.visible = kind == "grass"
	_wall.material_override = material if kind == "wall" else null
	_sphere.material_override = material if kind == "sphere" else null
	_grass.material_override = material if kind == "grass" else null


func _apply_variant(material: ShaderMaterial, overlap: float, hub: float) -> void:
	for uniform in material.shader.get_shader_uniform_list():
		if uniform.name == "shadow_overlap_darkening":
			material.set_shader_parameter("shadow_overlap_darkening", overlap)
	if _globals_present["ringworld_hub_light_weight"]:
		RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", hub)


func _grab() -> Image:
	for frame in SETTLE_FRAMES:
		await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("viewport readback failed")
		return Image.new()
	image.convert(Image.FORMAT_RGBA8)
	return image


func _nonblack(image: Image) -> int:
	var data := image.get_data()
	var count := 0
	for i in range(0, data.size(), 4):
		if data[i] + data[i + 1] + data[i + 2] > 0:
			count += 1
	return count


func _stats(image: Image) -> Dictionary:
	var data := image.get_data()
	var low := 255
	var high := 0
	var seen := {}
	for i in range(0, data.size(), 4):
		var value := int(data[i]) + int(data[i + 1]) * 256 + int(data[i + 2]) * 65536
		seen[value] = true
		var luma := maxi(data[i], maxi(data[i + 1], data[i + 2]))
		low = mini(low, luma)
		high = maxi(high, luma)
	return {"min": low, "max": high, "distinct": seen.size()}


func _diff(actual: Image, golden: Image) -> Dictionary:
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
	push_error("SHADOW_COMPOSE_IDENTITY=FAIL " + message)
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	call_deferred("quit", 1)
