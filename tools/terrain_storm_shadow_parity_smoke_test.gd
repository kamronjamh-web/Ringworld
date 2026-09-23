extends SceneTree

## GPU contract for the production voxel, proxy, and global terrain receivers. It renders
## each shader on the exact same inward-facing ring-surface quad, using an
## intentionally saturated synthetic local-cloud map.  The latter is important:
## it catches clamps or alternate shadow paths that a mild storm map hides.
const SIZE := 96
const SURFACE_X := 1000.0
const MAP_STRENGTH := 0.98
const PARITY_TOLERANCE := 0.005

var _viewport: SubViewport
var _surface: MeshInstance3D
var _light: DirectionalLight3D
var _environment: Environment
var _voxel_material: ShaderMaterial
var _proxy_material: ShaderMaterial
var _global_material: ShaderMaterial
var _clear_map: Texture2D
var _storm_map: Texture2D


func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy/headless rendering is not GPU validation")
		return
	call_deferred("_run")


func _run() -> void:
	_make_viewport()
	_clear_map = _constant_shadow_texture(0.0)
	_storm_map = _constant_shadow_texture(1.0)
	_voxel_material = _make_material("res://materials/voxel_ring_terrain.gdshader")
	_proxy_material = _make_material("res://materials/ring_terrain_proxy.gdshader")
	_global_material = _make_material("res://materials/ring_terrain_global.gdshader")

	# Clear daylight is the unshadowed control. Heavy daylight exercises direct
	# sun response; ambient is captured independently because AO_LIGHT_AFFECT=0.
	var clear_direct := await _measure_pair(_clear_map, true, true)
	var moderate_direct := await _measure_pair(_constant_shadow_texture(0.5), true, true)
	var storm_direct := await _measure_pair(_storm_map, true, true)
	var clear_ambient := await _measure_pair(_clear_map, false, true)
	var storm_ambient := await _measure_pair(_storm_map, false, true)
	var disabled_day_storm := await _measure_pair(_storm_map, true, true, false)
	# Night keeps cloud sampling and ambient enabled. It verifies that day gating
	# reaches both the direct and ambient paths without permitting blank captures.
	var night_clear := await _measure_pair(_clear_map, true, false, true)
	var night_storm := await _measure_pair(_storm_map, true, false, true)
	# Totality must retain ambient light without darkening it a second time
	# through the cloud map. This global is local to this isolated test process.
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 0.0)
	var eclipse_clear := await _measure_pair(_clear_map, false, true, true, "eclipse")
	var eclipse_storm := await _measure_pair(_storm_map, false, true, true, "eclipse")
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)

	if not _pairs_match(clear_direct, "clear direct") \
		or not _pairs_match(moderate_direct, "moderate direct") \
		or not _pairs_match(storm_direct, "heavy direct") \
		or not _pairs_match(clear_ambient, "clear ambient") \
		or not _pairs_match(storm_ambient, "heavy ambient") \
		or not _pairs_match(disabled_day_storm, "day cloud-disabled storm-map") \
		or not _pairs_match(night_clear, "night clear") \
		or not _pairs_match(night_storm, "night heavy") \
		or not _pairs_match(eclipse_clear, "eclipse clear") \
		or not _pairs_match(eclipse_storm, "eclipse heavy"):
		return
	if storm_direct.voxel >= clear_direct.voxel * 0.30 \
		or storm_direct.proxy >= clear_direct.proxy * 0.30 \
		or storm_direct.global >= clear_direct.global * 0.30:
		_fail("saturated local cloud map did not materially darken direct terrain: clear=%s storm=%s" % [clear_direct, storm_direct])
		return
	if moderate_direct.voxel >= clear_direct.voxel * 0.80 \
		or moderate_direct.proxy >= clear_direct.proxy * 0.80 \
		or moderate_direct.global >= clear_direct.global * 0.80:
		_fail("moderate local cloud map did not darken direct terrain: clear=%s moderate=%s" % [clear_direct, moderate_direct])
		return
	if storm_ambient.voxel >= clear_ambient.voxel * 0.32 \
		or storm_ambient.proxy >= clear_ambient.proxy * 0.32 \
		or storm_ambient.global >= clear_ambient.global * 0.32:
		_fail("saturated local cloud map did not materially darken ambient terrain: clear=%s storm=%s" % [clear_ambient, storm_ambient])
		return
	if absf(disabled_day_storm.voxel - clear_direct.voxel) > PARITY_TOLERANCE \
		or absf(disabled_day_storm.proxy - clear_direct.proxy) > PARITY_TOLERANCE \
		or absf(disabled_day_storm.global - clear_direct.global) > PARITY_TOLERANCE:
		_fail("day cloud-disabled storm map differed from clear control: clear=%s disabled=%s" % [clear_direct, disabled_day_storm])
		return
	for receiver in ["voxel", "proxy", "global"]:
		if absf(night_clear[receiver] - night_storm[receiver]) > PARITY_TOLERANCE \
			or absf(eclipse_clear[receiver] - eclipse_storm[receiver]) > PARITY_TOLERANCE:
			_fail("cloud map changed nighttime/totality ambient for " + receiver)
			return
	print("TERRAIN_STORM_SHADOW_PARITY=PASS clear_direct=%s moderate_direct=%s storm_direct=%s clear_ambient=%s storm_ambient=%s night_clear=%s night_storm=%s" % [clear_direct, moderate_direct, storm_direct, clear_ambient, storm_ambient, night_clear, night_storm])
	quit(0)


func _make_viewport() -> void:
	_viewport = SubViewport.new()
	_viewport.size = Vector2i(SIZE, SIZE)
	_viewport.own_world_3d = true
	_viewport.transparent_bg = false
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var environment := WorldEnvironment.new()
	_environment = Environment.new()
	_environment.background_mode = Environment.BG_COLOR
	_environment.background_color = Color.BLACK
	_environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	_environment.ambient_light_color = Color.WHITE
	_environment.ambient_light_energy = 0.35
	_environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	_environment.glow_enabled = false
	environment.environment = _environment
	_viewport.add_child(environment)
	var camera := Camera3D.new()
	camera.position = Vector3(SURFACE_X - 20.0, 0.0, 0.0)
	camera.fov = 32.0
	camera.current = true
	_viewport.add_child(camera)
	camera.look_at(Vector3(SURFACE_X, 0.0, 0.0), Vector3.UP)
	_light = DirectionalLight3D.new()
	# A light travelling +X has a shader LIGHT direction of -X, aligned with
	# this ring surface's inward normal.
	_light.light_energy = 0.25
	_viewport.add_child(_light)
	_light.look_at(Vector3(1.0, 0.0, 0.0), Vector3.UP)
	_surface = MeshInstance3D.new()
	_surface.mesh = _inward_surface_mesh()
	_viewport.add_child(_surface)


func _make_material(shader_path: String) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = load(shader_path) as Shader
	material.set_shader_parameter("ring_center", Vector3.ZERO)
	material.set_shader_parameter("camera_world_position", Vector3(SURFACE_X - 20.0, 0.0, 0.0))
	material.set_shader_parameter("sun_direction", Vector3(-1.0, 0.0, 0.0))
	material.set_shader_parameter("day_start_dot", -0.2)
	material.set_shader_parameter("day_full_dot", 0.35)
	material.set_shader_parameter("night_direct_light_factor", 0.0)
	material.set_shader_parameter("day_direct_light_factor", 1.0)
	material.set_shader_parameter("shadow_floor", 0.0)
	material.set_shader_parameter("ring_atmosphere_enabled", false)
	# Hold proxy-only LOD, analytic-field, and macro-shadow branches out of this
	# receiver parity contract; both materials receive the same local map below.
	if shader_path.ends_with("ring_terrain_proxy.gdshader"):
		material.set_shader_parameter("near_fade_enabled", false)
		material.set_shader_parameter("far_fade_enabled", false)
		material.set_shader_parameter("geomorph_enabled", false)
		material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
		material.set_shader_parameter("terrain_macro_shadow_enabled", false)
		material.set_shader_parameter("debug_color_enabled", false)
	if shader_path.ends_with("ring_terrain_global.gdshader"):
		material.set_shader_parameter("fade_start_meters", -1.0)
		material.set_shader_parameter("fade_end_meters", 0.0)
		material.set_shader_parameter("far_fade_enabled", false)
		material.set_shader_parameter("far_space_enabled", false)
		material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
		material.set_shader_parameter("terrain_macro_shadow_enabled", false)
		material.set_shader_parameter("night_brightness", 1.0)
		material.set_shader_parameter("debug_color_enabled", false)
	return material


func _configure_local_shadow(material: ShaderMaterial, map: Texture2D, enabled: bool) -> void:
	material.set_shader_parameter("local_cloud_shadow_texture", map)
	material.set_shader_parameter("local_cloud_shadow_far_texture", _clear_map)
	material.set_shader_parameter("local_cloud_shadow_enabled", enabled)
	material.set_shader_parameter("local_cloud_shadow_near_enabled", enabled)
	material.set_shader_parameter("local_cloud_shadow_far_enabled", false)
	material.set_shader_parameter("local_cloud_shadow_center_world", Vector3(SURFACE_X, 20.0, 0.0))
	material.set_shader_parameter("local_cloud_shadow_axis_around", Vector3(0.0, 0.0, 1.0))
	material.set_shader_parameter("local_cloud_shadow_axis_across", Vector3(0.0, 1.0, 0.0))
	material.set_shader_parameter("local_cloud_shadow_axis_up", Vector3(-1.0, 0.0, 0.0))
	material.set_shader_parameter("local_cloud_shadow_radius_meters", 2000.0)
	material.set_shader_parameter("local_cloud_shadow_strength", MAP_STRENGTH)
	material.set_shader_parameter("local_cloud_shadow_near_start_meters", 0.0)
	material.set_shader_parameter("local_cloud_shadow_near_end_meters", 1.0)
	material.set_shader_parameter("local_cloud_shadow_far_strength_start_meters", 4000.0)
	material.set_shader_parameter("local_cloud_shadow_far_strength_end_meters", 5000.0)


func _measure_pair(map: Texture2D, direct_enabled: bool, is_day: bool, cloud_enabled := true, context := "") -> Dictionary:
	var sun := Vector3(-1.0, 0.0, 0.0) if is_day else Vector3(1.0, 0.0, 0.0)
	# DirectionalLight3D's -Z is the incident ray, opposite the shader's LIGHT
	# vector / sun_direction convention used by these receiver materials.
	_light.look_at(-sun, Vector3.UP)
	_environment.ambient_light_energy = 0.35 if not is_day or not direct_enabled else 0.0
	_voxel_material.set_shader_parameter("sun_direction", sun)
	_proxy_material.set_shader_parameter("sun_direction", sun)
	_global_material.set_shader_parameter("sun_direction", sun)
	_light.visible = direct_enabled
	_configure_local_shadow(_voxel_material, map, cloud_enabled)
	_configure_local_shadow(_proxy_material, map, cloud_enabled)
	_configure_local_shadow(_global_material, map, cloud_enabled)
	_surface.material_override = _voxel_material
	var map_label := "clear" if map == _clear_map else ("storm" if map == _storm_map else "moderate")
	var label := "%s_%s_%s_%s" % ["day" if is_day else "night", "direct" if direct_enabled else "ambient", "cloud" if cloud_enabled else "disabled", map_label]
	if not context.is_empty():
		label = context + "_" + label
	var voxel := await _capture_luma(label + "_voxel")
	_surface.material_override = _proxy_material
	var proxy := await _capture_luma(label + "_proxy")
	_surface.material_override = _global_material
	var global := await _capture_luma(label + "_global")
	print("TERRAIN_SHADOW_CASE name=%s voxel=%.6f proxy=%.6f global=%.6f" % [label, voxel, proxy, global])
	return {"voxel": voxel, "proxy": proxy, "global": global}


func _capture_luma(label: String) -> float:
	for settle in range(3):
		await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty GPU capture: " + label)
		return -1.0
	var output_dir := "res://.godot/terrain_storm_shadow_parity/%s" % ("after" if "--terrain-shadow-after" in OS.get_cmdline_user_args() else "before")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_dir))
	if image.save_png("%s/%s.png" % [output_dir, label]) != OK:
		_fail("could not save GPU capture: " + label)
		return -1.0
	var total := 0.0
	var samples := 0
	for y in range(SIZE / 3, SIZE * 2 / 3):
		for x in range(SIZE / 3, SIZE * 2 / 3):
			var color := image.get_pixel(x, y).srgb_to_linear()
			if not is_finite(color.r) or not is_finite(color.g) or not is_finite(color.b):
				_fail("non-finite GPU capture: " + label)
				return -1.0
			total += color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722
			samples += 1
	return total / max(samples, 1)


func _pairs_match(values: Dictionary, label: String) -> bool:
	var voxel := float(values.voxel)
	var proxy := float(values.proxy)
	var global := float(values.global)
	if voxel < 0.0001 or proxy < 0.0001 or global < 0.0001:
		_fail("empty GPU terrain sample for %s: %s" % [label, values])
		return false
	if absf(voxel - proxy) > PARITY_TOLERANCE or absf(voxel - global) > PARITY_TOLERANCE:
		_fail("terrain storm-shadow parity mismatch for %s: %s (tolerance=%.3f)" % [label, values, PARITY_TOLERANCE])
		return false
	return true


func _constant_shadow_texture(value: float) -> Texture2D:
	var image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
	# R/G are the two GPU integration phases. Saturating both produces exactly
	# one before local_cloud_shadow_strength is applied.
	image.fill(Color(value, value, 0.0, 1.0))
	return ImageTexture.create_from_image(image)


func _inward_surface_mesh() -> ArrayMesh:
	var half_extent := 8.0
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = PackedVector3Array([
		Vector3(SURFACE_X, -half_extent, -half_extent), Vector3(SURFACE_X, half_extent, -half_extent),
		Vector3(SURFACE_X, half_extent, half_extent), Vector3(SURFACE_X, -half_extent, half_extent)
	])
	arrays[ArrayMesh.ARRAY_NORMAL] = PackedVector3Array([Vector3.LEFT, Vector3.LEFT, Vector3.LEFT, Vector3.LEFT])
	arrays[ArrayMesh.ARRAY_COLOR] = PackedColorArray([Color(0.56, 0.48, 0.32), Color(0.56, 0.48, 0.32), Color(0.56, 0.48, 0.32), Color(0.56, 0.48, 0.32)])
	# Godot's clockwise front faces make this -X-facing winding visible to camera.
	arrays[ArrayMesh.ARRAY_INDEX] = PackedInt32Array([0, 1, 2, 0, 2, 3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _fail(message: String) -> void:
	push_error("TERRAIN_STORM_SHADOW_PARITY=FAIL " + message)
	quit(1)
