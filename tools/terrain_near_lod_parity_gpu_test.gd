extends SceneTree
# GPU receiver parity for the native Terrain3D material and the next proxy LOD.
# It intentionally uses a low, flat prepared window so snow cannot mask a
# colour/lighting difference. Both receivers use world normal -X.

const SURFACE := preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const GEOMETRY := preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
const SIZE := 128
const SIDE := 384
const TILE := 128
const RING_RADIUS := 5000000.0
const TOLERANCE := 0.02

var viewport: SubViewport
var surface: Node3D
var proxy: MeshInstance3D
var native_material
var proxy_material: ShaderMaterial
var light: DirectionalLight3D
var clear_map: Texture2D
var storm_map: Texture2D
var failures: PackedStringArray = []

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		return fail("Forward+ RenderingDevice required")
	make_viewport()
	clear_map = shadow_texture(0.0)
	storm_map = shadow_texture(1.0)
	surface = SURFACE.new()
	viewport.add_child(surface)
	var fixture := flat_fixture(Color(0.0, 0.0, 0.0, 0.0)) # implicit grass
	var geometry := GEOMETRY.new()
	if not geometry.call("PrepareGameplay", fixture.heights, 0, 192, 0.0, -1, -8192.0, 8000.0, 32):
		return fail("could not prepare flat native geometry")
	if not bool(surface.call("BeginGameplayPreparedWindow", fixture.heights, fixture.height_images, fixture.biome_images, geometry, 0, 192, 0.0, -1, 1, -8192.0, 8000.0)):
		return fail("could not begin native window")
	# local (around, up, across) -> (-Z, -X, +Y); y/up becomes -X.
	var frame := Transform3D(Basis(Vector3(0, 0, -1), Vector3.LEFT, Vector3.UP), Vector3.ZERO)
	surface.call("SetGameplayWorldFrames", frame, frame)
	for ignored in 240:
		var state: Dictionary = surface.call("AdvanceBudgetedPreparation", .5, .5)
		if not bool(state.get("ok", false)):
			return fail("native preparation failed: " + JSON.stringify(state))
		if bool(state.get("ready", false)):
			break
		await process_frame
	if not bool(surface.call("ActivateStagedWindow", 1)):
		return fail("native window did not activate")
	var terrain = surface.get("TerrainNode")
	native_material = terrain.call("get_material") if terrain != null else null
	if native_material == null:
		return fail("native material unavailable")
	proxy = MeshInstance3D.new()
	proxy.mesh = inward_quad()
	proxy_material = ShaderMaterial.new()
	proxy_material.shader = load("res://materials/ring_terrain_proxy.gdshader") as Shader
	proxy.material_override = proxy_material
	viewport.add_child(proxy)
	configure_common(native_material)
	configure_common(proxy_material)
	copy_biome_textures()
	# The proxy uses inward -X at the origin when its ring center is -R on X.
	proxy_material.set_shader_parameter("ring_center", Vector3(-RING_RADIUS, 0, 0))
	proxy_material.set_shader_parameter("camera_world_position", Vector3(-24, 0, 8))
	proxy_material.set_shader_parameter("near_fade_enabled", false)
	proxy_material.set_shader_parameter("far_fade_enabled", false)
	proxy_material.set_shader_parameter("geomorph_enabled", false)
	proxy_material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	proxy_material.set_shader_parameter("terrain_macro_shadow_enabled", false)
	var clear := await measure_case(clear_map, true)
	var storm := await measure_case(storm_map, true)
	var night := await measure_case(clear_map, false)
	var macro_day_clear := await measure_macro_case(true, false, false, Vector3.LEFT)
	var macro_day_occluded := await measure_macro_case(true, true, false, Vector3.LEFT)
	var macro_night_clear := await measure_macro_case(false, false, false, Vector3.LEFT)
	var macro_night_occluded := await measure_macro_case(false, false, true, Vector3.LEFT)
	var macro_night_wrong_direction := await measure_macro_case(false, false, true, Vector3.RIGHT)
	disable_macro_shadows()
	# Globals belong to this isolated test process, which exits after the capture.
	# Avoid editor-only global_shader_parameter_get() at runtime.
	var original_light_specular := light.light_specular
	var original_wetness: float = float(material_get(native_material, "surface_wetness"))
	var original_wetness_strength: float = float(material_get(native_material, "surface_wetness_strength"))
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 0.0)
	var grass_off := await measure_case(clear_map, true)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_start_meters", 1.0)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_end_meters", 2.0)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_strength", .65)
	var grass_on := await measure_case(clear_map, true)
	# Keep diffuse receiver parity independent of native's explicit GGX branch.
	light.light_specular = 1.0
	material_set(native_material, "surface_wetness", 0.0)
	material_set(native_material, "surface_wetness_strength", 1.0)
	var dry_specular := await measure_case(clear_map, true)
	material_set(native_material, "surface_wetness", 1.0)
	var wet_specular := await measure_case(clear_map, true)
	light.light_specular = 0.0
	var wet_without_light_specular := await measure_case(clear_map, true)
	light.light_specular = 1.0
	material_set(native_material, "surface_wetness_strength", 0.0)
	var disabled_wetness_specular := await measure_case(clear_map, true)
	material_set(native_material, "surface_wetness", original_wetness)
	material_set(native_material, "surface_wetness_strength", original_wetness_strength)
	light.light_specular = original_light_specular
	check(clear.native > .001 and clear.proxy > .001, "day baseline is visible")
	check(storm.native < clear.native * .90 and storm.proxy < clear.proxy * .90, "storm materially darkens both receivers")
	check_ratio(clear.native / max(clear.proxy, .0001), 1.0, "day native/proxy base")
	check_ratio(storm.native / max(clear.native, .0001), storm.proxy / max(clear.proxy, .0001), "local cloud shadow response")
	check_ratio(night.native / max(clear.native, .0001), night.proxy / max(clear.proxy, .0001), "night tint response")
	check(macro_day_occluded.native < macro_day_clear.native * .45 and macro_day_occluded.proxy < macro_day_clear.proxy * .45, "day sun macro mask darkens native and proxy")
	# The mask attenuates direct light; the unlit night ambient remains intact.
	check_ratio((macro_night_occluded.native - night.native) / max(macro_night_clear.native - night.native, .0001), .2, "native planet direct-light transmittance")
	check_ratio((macro_night_occluded.proxy - night.proxy) / max(macro_night_clear.proxy - night.proxy, .0001), .2, "proxy planet direct-light transmittance")
	check(absf(macro_night_wrong_direction.native - macro_night_clear.native) <= TOLERANCE and absf(macro_night_wrong_direction.proxy - macro_night_clear.proxy) <= TOLERANCE, "wrong-direction planet mask leaves native and proxy lit")
	check_ratio(grass_on.native / max(grass_off.native, .0001), grass_on.proxy / max(grass_off.proxy, .0001), "distant grass coverage response")
	check(absf(dry_specular.native - grass_on.native) <= .0001, "dry native direct specular matches proxy-style baseline")
	check(wet_specular.native > wet_without_light_specular.native + .01, "wet native direct specular highlight response retained")
	check(absf(disabled_wetness_specular.native - dry_specular.native) <= .0001, "zero wetness strength removes direct wet highlight")
	# Snow was formerly a native-only final ALBEDO override. The test's low
	# height proves it is absent here; the material value is also checked so a
	# future high-elevation control can use the same receiver.
	check(absf(float(material_get(native_material, "surface_wetness"))) < .001, "dry baseline retained")
	var shader_source := FileAccess.get_file_as_string("res://materials/terrain3d_ring_surface.gdshader")
	check(not shader_source.contains("float snow") and not shader_source.contains("), snow)"), "native shader contains no near-only snow blend")
	print("TERRAIN_NEAR_LOD_PARITY_GPU_TEST=%s clear=%s storm=%s night=%s macro_day_clear=%s macro_day_occluded=%s macro_night_clear=%s macro_night_occluded=%s macro_night_wrong_direction=%s grass_off=%s grass_on=%s dry_specular=%s wet_specular=%s wet_without_light_specular=%s disabled_wetness_specular=%s" % ["PASS" if failures.is_empty() else "FAIL", clear, storm, night, macro_day_clear, macro_day_occluded, macro_night_clear, macro_night_occluded, macro_night_wrong_direction, grass_off, grass_on, dry_specular, wet_specular, wet_without_light_specular, disabled_wetness_specular])
	quit(0 if failures.is_empty() else 1)

func make_viewport() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(SIZE, SIZE)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var environment := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color.BLACK
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color.WHITE
	env.ambient_light_energy = .15
	env.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.environment = env
	viewport.add_child(environment)
	var camera := Camera3D.new()
	camera.position = Vector3(-24, 0, 8)
	camera.current = true
	viewport.add_child(camera)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	light = DirectionalLight3D.new()
	light.light_energy = 1.0
	light.light_specular = 0.0
	viewport.add_child(light)
	light.look_at(Vector3(1, 0, 0), Vector3.UP)

func configure_common(material) -> void:
	material_set(material, "ring_atmosphere_enabled", false)
	material_set(material, "biome_texture_weights_enabled", true)
	material_set(material, "biome_texture_scale", .20)
	material_set(material, "sun_direction", Vector3(-1, 0, 0))
	material_set(material, "day_start_dot", -.2)
	material_set(material, "day_full_dot", .35)
	material_set(material, "night_tint", Vector3(.30, .45, .70))
	material_set(material, "night_direct_light_factor", 0.0)
	material_set(material, "day_direct_light_factor", 1.0)
	material_set(material, "shadow_floor", 0.0)
	material_set(material, "ring_viewer_world_position", Vector3(-24, 0, 8))

func copy_biome_textures() -> void:
	for name in ["biome_grass_albedo", "biome_dirt_albedo", "biome_desert_albedo", "biome_forest_albedo", "biome_tundra_albedo", "biome_alpine_albedo"]:
		proxy_material.set_shader_parameter(name, material_get(native_material, name))

func measure_case(map: Texture2D, day: bool) -> Dictionary:
	var sun := Vector3(-1, 0, 0) if day else Vector3(1, 0, 0)
	light.visible = day
	light.look_at(-sun, Vector3.UP)
	for material in [native_material, proxy_material]:
		material_set(material, "sun_direction", sun)
		configure_shadow(material, map, day)
	proxy.visible = false
	var native := await capture_luma()
	proxy.visible = true
	surface.visible = false
	var proxy_luma := await capture_luma()
	surface.visible = true
	return {"native":native, "proxy":proxy_luma}

func measure_macro_case(day: bool, sun_mask_enabled: bool, planet_mask_enabled: bool, planet_direction: Vector3) -> Dictionary:
	# Keep the actual directional light on the inward-facing surface. During the
	# night case it represents PlanetLight while the configured sun faces away.
	var sun := Vector3.LEFT if day else Vector3.RIGHT
	light.visible = true
	light.look_at(Vector3.RIGHT, Vector3.UP)
	for material in [native_material, proxy_material]:
		material_set(material, "sun_direction", sun)
		configure_shadow(material, clear_map, false)
		material_set(material, "terrain_macro_shadow_texture", storm_map)
		material_set(material, "terrain_macro_shadow_enabled", sun_mask_enabled)
		material_set(material, "terrain_macro_shadow_strength", .8)
		material_set(material, "terrain_macro_shadow_fade_start_meters", -1.0)
		material_set(material, "terrain_macro_shadow_fade_end_meters", 0.0)
		material_set(material, "terrain_planet_shadow_texture", storm_map)
		material_set(material, "terrain_planet_shadow_enabled", planet_mask_enabled)
		material_set(material, "terrain_planet_shadow_direction", planet_direction)
		material_set(material, "terrain_planet_shadow_strength", .8)
		material_set(material, "terrain_planet_shadow_fade_start_meters", -1.0)
		material_set(material, "terrain_planet_shadow_fade_end_meters", 0.0)
	proxy.visible = false
	var native := await capture_luma()
	proxy.visible = true
	surface.visible = false
	var proxy_luma := await capture_luma()
	surface.visible = true
	return {"native":native, "proxy":proxy_luma}

func disable_macro_shadows() -> void:
	for material in [native_material, proxy_material]:
		material_set(material, "terrain_macro_shadow_enabled", false)
		material_set(material, "terrain_planet_shadow_enabled", false)

func configure_shadow(material, map: Texture2D, enabled: bool) -> void:
	material_set(material, "local_cloud_shadow_texture", map)
	material_set(material, "local_cloud_shadow_far_texture", clear_map)
	material_set(material, "local_cloud_shadow_enabled", enabled)
	material_set(material, "local_cloud_shadow_near_enabled", enabled)
	material_set(material, "local_cloud_shadow_far_enabled", false)
	material_set(material, "local_cloud_shadow_center_world", Vector3.ZERO)
	material_set(material, "local_cloud_shadow_axis_around", Vector3(0, 0, -1))
	material_set(material, "local_cloud_shadow_axis_across", Vector3.UP)
	material_set(material, "local_cloud_shadow_axis_up", Vector3.LEFT)
	material_set(material, "local_cloud_shadow_radius_meters", 2000.0)
	material_set(material, "local_cloud_shadow_strength", .8)
	material_set(material, "local_cloud_shadow_near_start_meters", -1.0)
	material_set(material, "local_cloud_shadow_near_end_meters", 0.0)

func capture_luma() -> float:
	for ignored in 3:
		await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var total := 0.0
	var count := 0
	for y in range(SIZE / 3, SIZE * 2 / 3):
		for x in range(SIZE / 3, SIZE * 2 / 3):
			var color := image.get_pixel(x, y).srgb_to_linear()
			total += color.r * .2126 + color.g * .7152 + color.b * .0722
			count += 1
	return total / max(1, count)

func flat_fixture(color: Color) -> Dictionary:
	var heights := PackedFloat32Array()
	heights.resize(SIDE * SIDE)
	heights.fill(0.0)
	var height_images: Array[Image] = []
	var biome_images: Array[Image] = []
	for ignored in 9:
		var bytes := PackedByteArray()
		bytes.resize(TILE * TILE * 4)
		height_images.append(Image.create_from_data(TILE, TILE, false, Image.FORMAT_RF, bytes))
		var biomes := Image.create_empty(TILE, TILE, false, Image.FORMAT_RGBA8)
		biomes.fill(color)
		biome_images.append(biomes)
	return {"heights":heights, "height_images":height_images, "biome_images":biome_images}

func inward_quad() -> ArrayMesh:
	var e := 12.0
	var arrays := []
	arrays.resize(ArrayMesh.ARRAY_MAX)
	arrays[ArrayMesh.ARRAY_VERTEX] = PackedVector3Array([Vector3(0, -e, -e), Vector3(0, e, -e), Vector3(0, e, e), Vector3(0, -e, e)])
	arrays[ArrayMesh.ARRAY_NORMAL] = PackedVector3Array([Vector3.LEFT, Vector3.LEFT, Vector3.LEFT, Vector3.LEFT])
	arrays[ArrayMesh.ARRAY_COLOR] = PackedColorArray([Color(0, 0, 0, 0), Color(0, 0, 0, 0), Color(0, 0, 0, 0), Color(0, 0, 0, 0)])
	arrays[ArrayMesh.ARRAY_INDEX] = PackedInt32Array([0, 1, 2, 0, 2, 3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh

func shadow_texture(value: float) -> Texture2D:
	var image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
	image.fill(Color(value, value, 0, 1))
	return ImageTexture.create_from_image(image)

func check_ratio(actual: float, expected: float, label: String) -> void:
	check(absf(actual - expected) <= TOLERANCE, "%s actual=%.3f expected=%.3f" % [label, actual, expected])

func material_set(material, parameter: String, value) -> void:
	if material is ShaderMaterial:
		(material as ShaderMaterial).set_shader_parameter(parameter, value)
	else:
		material.call("set_shader_param", parameter, value)

func material_get(material, parameter: String):
	if material is ShaderMaterial:
		return (material as ShaderMaterial).get_shader_parameter(parameter)
	return material.call("get_shader_param", parameter)

func check(value: bool, label: String) -> void:
	if not value:
		failures.append(label)
		push_error("TERRAIN_NEAR_LOD_PARITY_GPU_TEST: " + label)

func fail(message: String) -> void:
	push_error("TERRAIN_NEAR_LOD_PARITY_GPU_TEST=FAIL " + message)
	quit(1)
