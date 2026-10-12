extends SceneTree

# Production soil and grass shaders; no world or user preferences are loaded.
const DRIVER := preload("res://tools/GrassTerrainBlendTestDriver.cs")
const SIZE := 384
const OUTPUT := "res://artifacts/grass-terrain-blend/"
var viewport: SubViewport
var camera: Camera3D
var ground: MeshInstance3D
var grass: MultiMeshInstance3D
var terrain_material: ShaderMaterial
var grass_material: ShaderMaterial
var production_shader: Shader
var authored_band_shader: Shader
var lighting: DirectionalLight3D
var environment: Environment
var driver: RefCounted
var report := {"purpose": "production grass/voxel soil cutoff blending; not gameplay performance", "checks": []}
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("Forward+ RenderingDevice required")
	call_deferred("run")

func run() -> void:
	driver = DRIVER.new()
	if not bool(driver.call("VerifyRuntimeContract")): return fail("runtime appearance contract failed")
	setup()
	set_distance(10.0)
	set_old_blend()
	var close_old := await capture("near_old")
	set_new_blend()
	var close_new := await capture("near_new")
	grass.visible = false
	var close_floor := await capture("near_ground")
	grass.visible = true
	var close_comparison := contrast(close_old, close_new, close_floor)
	report.checks.append({"name": "near_ground_convergence", "metrics": close_comparison})
	if not require(close_comparison.pixels > 100 and close_comparison.new_error < close_comparison.old_error * .8, "near grass did not converge toward rendered ground: " + str(close_comparison)): return
	grass_material.set_shader_parameter("grass_terrain_handoff_strength", .53)
	var close_partial := await capture("near_saved_partial_strength")
	var partial_comparison := contrast(close_old, close_partial, close_floor)
	report.checks.append({"name": "near_saved_partial_strength", "metrics": partial_comparison})
	if not require(partial_comparison.new_error < partial_comparison.old_error and partial_comparison.new_error > close_comparison.new_error, "saved partial strength did not improve near color proportionally"): return
	set_new_blend()
	# Fixed camera and geometry; only the terrain blend settings change.
	set_distance(82.0)
	grass.visible = false
	var floor_only := await capture("constant_ground")
	grass.visible = true
	set_old_blend()
	var outer_old := await capture("constant_outer_old")
	set_new_blend()
	var outer_new := await capture("constant_outer_new")
	var comparison := contrast(outer_old, outer_new, floor_only)
	report.checks.append({"name": "outer_ground_convergence", "metrics": comparison})
	if not require(comparison.pixels > 100 and comparison.new_error < comparison.old_error * .7, "outer grass did not converge toward rendered ground: " + str(comparison)): return
	set_distance(72.0)
	grass.visible = false
	var middle_floor := await capture("constant_middle_ground")
	grass.visible = true
	set_old_blend(); var middle_old := await capture("constant_middle_old")
	set_new_blend(); var middle_new := await capture("constant_middle_new")
	var middle_comparison := contrast(middle_old, middle_new, middle_floor)
	report.checks.append({"name": "gradual_middle_blend", "metrics": middle_comparison})
	if not require(middle_comparison.pixels > 100 and middle_comparison.new_error < middle_comparison.old_error and middle_comparison.new_error / middle_comparison.old_error > comparison.new_error / comparison.old_error, "color convergence was not gradual before final fade"): return
	# Crossed slider endpoints still complete before the 85 m disappearance band.
	set_distance(84.95)
	grass_material.set_shader_parameter("grass_terrain_blend_start_fraction", .99)
	grass_material.set_shader_parameter("grass_terrain_blend_end_fraction", 1.0)
	var endpoint_capture := await capture("ordered_99_100_endpoints")
	if not require(is_finite(difference(endpoint_capture, endpoint_capture)), "endpoint capture contains nonfinite RGB"): return
	set_distance(82.0); set_new_blend()
	# Production directional light verifies actual brightness, beyond ambient albedo.
	environment.ambient_light_energy = .1
	lighting = DirectionalLight3D.new(); lighting.rotation.y = -PI / 2.0; lighting.light_energy = 1.0; viewport.add_child(lighting)
	grass.visible = false
	var day_floor := await capture("daylight_constant_ground")
	grass.visible = true
	set_old_blend()
	var day_old := await capture("daylight_constant_outer_old")
	set_new_blend()
	var day_new := await capture("daylight_constant_outer_new")
	var daylight_comparison := contrast(day_old, day_new, day_floor)
	report.checks.append({"name": "daylight_outer_ground_convergence", "metrics": daylight_comparison})
	if not require(daylight_comparison.pixels > 100 and daylight_comparison.new_error < daylight_comparison.old_error * .7, "directional grass brightness did not converge toward terrain: " + str(daylight_comparison)): return
	lighting.queue_free(); await process_frame; environment.ambient_light_energy = 1.0
	grass_material.set_shader_parameter("grass_terrain_handoff_strength", 0.0)
	var disabled := await capture("strength_zero")
	set_old_blend()
	grass_material.set_shader_parameter("grass_terrain_handoff_strength", 0.0)
	grass_material.set_shader_parameter("grass_blade_shading_enabled", false)
	var disabled_old := await capture("strength_zero_old")
	if not require(difference(disabled, disabled_old) < .0001, "strength zero retained early lighting/color changes"): return
	grass_material.set_shader_parameter("grass_appearance_enabled", false)
	set_new_blend()
	var appearance_off := await capture("appearance_off")
	set_old_blend()
	grass_material.set_shader_parameter("grass_blade_shading_enabled", false)
	var appearance_off_old := await capture("appearance_off_old")
	if not require(difference(appearance_off, appearance_off_old) < .0001, "appearance disable retained blend effects"): return
	grass_material.set_shader_parameter("grass_appearance_enabled", true)
	set_new_blend()
	# At 82 m, near handoff completed and final fade has not started (85 m).
	grass_material.set_shader_parameter("grass_lod_enabled", true)
	grass.set_instance_shader_parameter("grass_lod_pass", 1)
	grass.set_instance_shader_parameter("grass_gpu_culled", false)
	var cpu := await capture("cpu_selected_roots")
	grass.set_instance_shader_parameter("grass_gpu_culled", true)
	var gpu_flag := await capture("gpu_selected_roots")
	if not require(difference(cpu, gpu_flag) < .0001, "CPU/GPU-selected roots disagree before fade"): return
	grass.set_instance_shader_parameter("grass_gpu_culled", false)
	grass_material.set_shader_parameter("grass_lod_enabled", false)
	# Explicit forest biome data must match the production terrain's forest target.
	set_biome(Color(0, 1, 0, 0))
	grass.visible = false
	var forest_floor := await capture("forest_ground")
	grass.visible = true
	set_old_blend()
	var forest_old := await capture("forest_old")
	set_new_blend()
	var forest_new := await capture("forest_new")
	var forest_comparison := contrast(forest_old, forest_new, forest_floor)
	report.checks.append({"name": "forest_ground_convergence", "metrics": forest_comparison})
	if not require(forest_comparison.pixels > 100 and forest_comparison.new_error < forest_comparison.old_error * .7, "forest target did not match terrain: " + str(forest_comparison)): return
	set_distance(10.0)
	grass.visible = false
	var near_forest_floor := await capture("near_forest_ground")
	grass.visible = true
	set_old_blend(); var near_forest_old := await capture("near_forest_old")
	set_new_blend(); var near_forest_new := await capture("near_forest_new")
	var near_forest_comparison := contrast(near_forest_old, near_forest_new, near_forest_floor)
	report.checks.append({"name": "near_forest_ground_convergence", "metrics": near_forest_comparison})
	if not require(near_forest_comparison.pixels > 100 and near_forest_comparison.new_error < near_forest_comparison.old_error, "near forest target did not improve terrain match: " + str(near_forest_comparison)): return
	set_distance(82.0)
	# Existing soil mask must prevent terrain coverage from coloring rock.
	set_biome(Color(0, 0, 0, 0), 1.0)
	grass.visible = false
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 0.0)
	var rock_off := await capture("rock_coverage_off")
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_start_meters", 1.0)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_end_meters", 2.0)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_strength", 1.0)
	var rock_on := await capture("rock_coverage_on")
	if not require(difference(rock_off, rock_on) < .0001, "rock received grass terrain coverage"): return
	# A positive soil control proves the forced coverage range is actually active.
	set_biome(Color(0, 0, 0, 0), 2.0)
	var soil_on := await capture("soil_coverage_on")
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 0.0)
	var soil_off := await capture("soil_coverage_off")
	var soil_difference := difference(soil_on, soil_off)
	report.checks.append({"name": "soil_positive_coverage_control", "rgb_difference": soil_difference})
	if not require(is_finite(soil_difference) and soil_difference > .001, "soil positive control did not receive visible terrain coverage"): return
	# Force coverage before the old 63 m distance band. Compare opaque grass
	# interiors against the grass-off reference so background-only tint cannot
	# satisfy the foreground response assertion.
	set_distance(10.0)
	set_new_blend()
	grass.visible = false
	var near_coverage_floor := await capture("near_coverage_ground_off")
	grass.visible = true
	var near_coverage_off := await capture("near_coverage_grass_off")
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 1.0)
	var near_coverage_on := await capture("near_coverage_grass_on")
	var foreground_response := coverage_foreground_response(near_coverage_off, near_coverage_on, near_coverage_floor)
	report.checks.append({"name": "near_grass_foreground_coverage", "metrics": foreground_response})
	if not require(foreground_response.pixels > 100 and foreground_response.rgb_response > .005, "near grass foreground did not respond to terrain coverage: " + str(foreground_response)): return
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 0.0)
	await wide_review()
	if failed: return
	report.status = "PASS"
	write_report()
	print("GRASS_TERRAIN_BLEND_GPU=PASS ", JSON.stringify(report))
	viewport.queue_free()
	await process_frame
	quit(0)

func setup() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave", Vector4.ZERO)
	RenderingServer.global_shader_parameter_set("ring_grass_terrain_coverage_enabled", 0.0)
	viewport = SubViewport.new(); viewport.size = Vector2i(SIZE, SIZE); viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	environment = Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color(.1, .12, .15); environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color.WHITE; environment.ambient_light_energy = 1.0
	var world := WorldEnvironment.new(); world.environment = environment; viewport.add_child(world)
	camera = Camera3D.new(); camera.current = true; camera.projection = Camera3D.PROJECTION_ORTHOGONAL; camera.size = 5.0; camera.far = 600.0; viewport.add_child(camera)
	terrain_material = ShaderMaterial.new(); terrain_material.shader = load("res://materials/voxel_ring_terrain.gdshader")
	terrain_material.set_shader_parameter("pilot_triplanar_enabled", true); terrain_material.set_shader_parameter("biome_texture_weights_enabled", true); terrain_material.set_shader_parameter("ring_center", Vector3(-10000, 0, 0)); terrain_material.set_shader_parameter("sun_direction", Vector3.LEFT); terrain_material.set_shader_parameter("ring_atmosphere_enabled", false); terrain_material.set_shader_parameter("local_cloud_shadow_enabled", false)
	var grass_color := Color(.65, .42, .18)
	var forest_color := Color(.16, .35, .12)
	for key in ["pilot_grass_albedo", "pilot_dirt_albedo", "biome_desert_albedo", "biome_tundra_albedo", "biome_alpine_albedo"]: terrain_material.set_shader_parameter(key, constant_texture(grass_color))
	terrain_material.set_shader_parameter("biome_forest_albedo", constant_texture(forest_color))
	ground = MeshInstance3D.new(); ground.material_override = terrain_material; ground.rotation.z = PI / 2.0; viewport.add_child(ground)
	grass = MultiMeshInstance3D.new(); grass.rotation.z = PI / 2.0; grass.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF; viewport.add_child(grass)
	var multi := MultiMesh.new(); multi.transform_format = MultiMesh.TRANSFORM_3D; multi.use_colors = true; multi.use_custom_data = true; multi.mesh = driver.call("LoadMesh"); multi.instance_count = 64
	for i in range(64):
		multi.set_instance_transform(i, Transform3D(Basis(Vector3.UP, float(i) * .61).scaled(Vector3(.18, .8, .18)), Vector3(float(i % 8) * .48 - 1.7, 0, float(i / 8) * .4 - 1.4)))
		multi.set_instance_custom_data(i, Color(.5, .5, .17, .31))
	grass.multimesh = multi
	grass.set_instance_shader_parameter("grass_instance_count", 64.0)
	production_shader = load("res://materials/ring_standing_grass.gdshader")
	var original_call := "grass_appearance_albedo_with_coverage(albedo, COLOR, grass_terrain_color_blend, grass_terrain_root_coverage)"
	var source := FileAccess.get_file_as_string("res://materials/ring_standing_grass.gdshader")
	if not source.contains(original_call): fail("production appearance call changed; historical band fixture needs review"); return
	authored_band_shader = Shader.new()
	# Freeze the historical distance-gated albedo explicitly; changing the
	# shared include must not silently update the before-image algorithm.
	var appearance_source := FileAccess.get_file_as_string("res://materials/ring_grass_appearance.gdshaderinc")
	var current_return := "return mix(graded_blade, terrain * relief, strength);"
	if not appearance_source.contains(current_return): fail("historical appearance fixture needs review"); return
	appearance_source = appearance_source.replace(current_return, "return mix(graded_blade, terrain, strength * handoff);")
	authored_band_shader.code = source.replace('#include "res://materials/ring_grass_appearance.gdshaderinc"', appearance_source).replace(original_call, "grass_appearance_albedo_with_coverage(albedo, COLOR, grass_lod_final_fade, 0.0)")
	grass_material = ShaderMaterial.new(); grass_material.shader = production_shader; grass.material_override = grass_material
	for pair in [["grass_appearance_enabled", true], ["grass_lod_enabled", false], ["grass_lod_near_distance", 20.0], ["grass_lod_far_distance", 90.0], ["grass_lod_transition_width", 10.0], ["grass_lod_near_density", 1.0], ["grass_lod_far_density", 1.0], ["grass_terrain_draw_distance", 90.0], ["local_cloud_shadow_enabled", false], ["grass_gust_enabled", false]]: grass_material.set_shader_parameter(pair[0], pair[1])
	var linear := grass_color.srgb_to_linear()
	grass_material.set_shader_parameter("grass_terrain_average_grass", Vector3(linear.r, linear.g, linear.b) * Vector3(.8376, 1.174, .7796))
	linear = forest_color.srgb_to_linear()
	grass_material.set_shader_parameter("grass_terrain_average_forest", Vector3(linear.r, linear.g, linear.b))
	grass_material.set_shader_parameter("grass_terrain_average_dirt", Vector3(.3, .2, .1))
	set_biome(Color(0, 0, 0, 0))

func constant_texture(color: Color) -> ImageTexture:
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8); image.fill(color)
	return ImageTexture.create_from_image(image)

func set_biome(weights: Color, soil: float = 2.0) -> void:
	var plane := PlaneMesh.new(); plane.size = Vector2(500, 500)
	var arrays := plane.surface_get_arrays(0)
	var colors := PackedColorArray(); var uv2 := PackedVector2Array()
	for i in range((arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()): colors.append(weights); uv2.append(Vector2(soil, 0))
	arrays[Mesh.ARRAY_COLOR] = colors; arrays[Mesh.ARRAY_TEX_UV2] = uv2
	var mesh := ArrayMesh.new(); mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays); ground.mesh = mesh
	for i in range(grass.multimesh.instance_count): grass.multimesh.set_instance_color(i, weights)

func set_distance(distance: float) -> void:
	camera.position = Vector3(-3, 0, distance); camera.look_at(Vector3(-.4, 0, 0), Vector3.LEFT)
	terrain_material.set_shader_parameter("camera_world_position", camera.position)

func set_old_blend() -> void:
	# Retain original 85..95 m fade-driven color behavior despite new endpoint cap.
	grass_material.shader = authored_band_shader
	grass_material.set_shader_parameter("grass_blade_shading_enabled", true)
	grass_material.set_shader_parameter("grass_terrain_blend_start_fraction", 85.0 / 90.0)
	grass_material.set_shader_parameter("grass_terrain_blend_end_fraction", 95.0 / 90.0)
	grass_material.set_shader_parameter("grass_terrain_handoff_strength", .55)
	grass_material.set_shader_parameter("grass_terrain_lighting_match", 0.0)

func set_new_blend() -> void:
	grass_material.shader = production_shader
	grass_material.set_shader_parameter("grass_blade_shading_enabled", false)
	grass_material.set_shader_parameter("grass_terrain_blend_start_fraction", .7)
	grass_material.set_shader_parameter("grass_terrain_blend_end_fraction", .9)
	grass_material.set_shader_parameter("grass_terrain_handoff_strength", 1.0)
	grass_material.set_shader_parameter("grass_terrain_lighting_match", 1.0)

func capture(name: String) -> Image:
	for i in range(6): await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty(): fail("empty capture: " + name); return Image.new()
	if image.save_png(OUTPUT + name + ".png") != OK: fail("capture write failed: " + name)
	return image

func difference(a: Image, b: Image) -> float:
	var sum := 0.0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			var left := a.get_pixel(x, y); var right := b.get_pixel(x, y)
			if not is_finite(left.r + left.g + left.b + right.r + right.g + right.b): fail("nonfinite rendered RGB"); return INF
			sum += absf(left.r - right.r) + absf(left.g - right.g) + absf(left.b - right.b)
	return sum / float(a.get_width() * a.get_height())

func contrast(old: Image, improved: Image, floor_image: Image, region: Rect2i = Rect2i()) -> Dictionary:
	var old_error := 0.0; var new_error := 0.0; var pixels := 0
	if not region.has_area(): region = Rect2i(0, 0, old.get_width(), old.get_height())
	for y in range(region.position.y, region.end.y):
		for x in range(region.position.x, region.end.x):
			var a := old.get_pixel(x, y); var b := improved.get_pixel(x, y); var floor_color := floor_image.get_pixel(x, y)
			if not is_finite(a.r + a.g + a.b + b.r + b.g + b.b + floor_color.r + floor_color.g + floor_color.b):
				fail("nonfinite RGB in contrast captures")
				return {"pixels": 0, "old_error": INF, "new_error": INF}
			var delta := absf(a.r-floor_color.r) + absf(a.g-floor_color.g) + absf(a.b-floor_color.b)
			if delta > .025:
				pixels += 1; old_error += delta; new_error += absf(b.r-floor_color.r) + absf(b.g-floor_color.g) + absf(b.b-floor_color.b)
	return {"pixels": pixels, "old_error": old_error/maxi(1, pixels), "new_error": new_error/maxi(1, pixels)}

func coverage_foreground_response(off: Image, on: Image, floor_image: Image) -> Dictionary:
	var pixels := 0
	var response := 0.0
	for y in range(1, off.get_height() - 1):
		for x in range(1, off.get_width() - 1):
			var interior := true
			# Erode the foreground mask by one pixel to exclude antialiased
			# silhouettes whose background changes could imitate blade response.
			for dy in range(-1, 2):
				for dx in range(-1, 2):
					var blade := off.get_pixel(x + dx, y + dy)
					var soil := floor_image.get_pixel(x + dx, y + dy)
					if absf(blade.r-soil.r) + absf(blade.g-soil.g) + absf(blade.b-soil.b) <= .025: interior = false
			if not interior: continue
			var before := off.get_pixel(x, y)
			var after := on.get_pixel(x, y)
			var delta := absf(before.r-after.r) + absf(before.g-after.g) + absf(before.b-after.b)
			if not is_finite(delta): fail("nonfinite near coverage foreground RGB"); return {"pixels": 0, "rgb_response": INF}
			pixels += 1
			response += delta
	return {"pixels": pixels, "rgb_response": response / maxi(1, pixels)}

func wide_review() -> void:
	set_biome(Color(0, 0, 0, 0))
	for pair in [["pilot_grass_albedo", "res://materials/third_party/voxelgame/grass_albedo.png"], ["pilot_dirt_albedo", "res://materials/third_party/voxelgame/dirt_albedo.png"], ["biome_desert_albedo", "res://materials/biomes/desert_sand_albedo.png"], ["biome_forest_albedo", "res://materials/biomes/forest_floor_albedo.png"], ["biome_tundra_albedo", "res://materials/biomes/tundra_ground_albedo.png"], ["biome_alpine_albedo", "res://materials/biomes/alpine_rock_albedo.png"]]: terrain_material.set_shader_parameter(pair[0], load(pair[1]))
	var palette: Dictionary = driver.call("CapturePalette")
	for key in palette: grass_material.set_shader_parameter("grass_terrain_average_" + str(key), palette[key])
	viewport.size = Vector2i(960, 540); camera.projection = Camera3D.PROJECTION_PERSPECTIVE; camera.fov = 68.0
	camera.position = Vector3(-1.8, 0, -2); camera.look_at(Vector3(-.6, 0, 20), Vector3.LEFT); terrain_material.set_shader_parameter("camera_world_position", camera.position)
	environment.ambient_light_energy = .1
	lighting = DirectionalLight3D.new(); lighting.rotation.y = -PI / 2.0; lighting.light_energy = 1.0; viewport.add_child(lighting)
	grass.multimesh.instance_count = 12000
	for i in range(12000):
		var x := float(i % 120) * .12 - 7.2; var z := float(i / 120) * 1.05
		grass.multimesh.set_instance_transform(i, Transform3D(Basis(Vector3.UP, float(i) * .73).scaled(Vector3(.12, .7, .12)), Vector3(x, 0, z)))
		grass.multimesh.set_instance_color(i, Color(0, 0, 0, 0)); grass.multimesh.set_instance_custom_data(i, Color(.5, .5, .17, .31))
	# Current compact gameplay range, paired near/intermediate source identities.
	grass.set_instance_shader_parameter("grass_instance_count", 12000.0); grass.set_instance_shader_parameter("grass_lod_pass", 0)
	var intermediate := MultiMeshInstance3D.new(); intermediate.rotation = grass.rotation; intermediate.material_override = grass_material; intermediate.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var middle_multi := MultiMesh.new(); middle_multi.transform_format = MultiMesh.TRANSFORM_3D; middle_multi.use_colors = true; middle_multi.use_custom_data = true; middle_multi.mesh = driver.call("LoadIntermediateMesh"); middle_multi.instance_count = 12000
	for i in range(12000):
		middle_multi.set_instance_transform(i, grass.multimesh.get_instance_transform(i)); middle_multi.set_instance_color(i, grass.multimesh.get_instance_color(i)); middle_multi.set_instance_custom_data(i, grass.multimesh.get_instance_custom_data(i))
	intermediate.multimesh = middle_multi; viewport.add_child(intermediate)
	intermediate.set_instance_shader_parameter("grass_instance_count", 12000.0); intermediate.set_instance_shader_parameter("grass_lod_pass", 1); intermediate.set_instance_shader_parameter("grass_gpu_culled", false)
	for pair in [["grass_lod_near_distance", 22.0], ["grass_lod_far_distance", 32.0], ["grass_lod_transition_width", 4.0], ["grass_lod_far_density", .25], ["grass_terrain_draw_distance", 32.0], ["grass_camera_facing_enabled", true], ["grass_camera_facing_strength", 1.0], ["grass_camera_facing_start", 0.0], ["grass_camera_facing_end", 1.0]]: grass_material.set_shader_parameter(pair[0], pair[1])
	grass_material.set_shader_parameter("grass_lod_enabled", true)
	grass.visible = false; intermediate.visible = false
	var hidden := await capture("daylight_cutoff_ground")
	grass.visible = true; intermediate.visible = true
	set_old_blend(); var before := await capture("daylight_cutoff_before")
	set_new_blend(); var after := await capture("daylight_cutoff_after")
	var edge_comparison := contrast(before, after, hidden)
	report.checks.append({"name": "actual_texture_daylight_contrast", "metrics": edge_comparison})
	if not require(edge_comparison.pixels > 100 and is_finite(edge_comparison.new_error), "actual texture daylight capture is empty/nonfinite"): return
	var outer_region := projected_band(27.0, 29.0)
	var outer_comparison := contrast(before, after, hidden, outer_region)
	# Ground-foot strip excludes distant blade tips projected into a near AABB.
	var near_region := projected_band(5.0, 12.0, 0.0)
	var near_comparison := contrast(before, after, hidden, near_region)
	report.checks.append({"name": "actual_texture_daylight_outer_band", "pixel_region": str(outer_region), "metrics": outer_comparison})
	report.checks.append({"name": "actual_texture_daylight_near_population", "pixel_region": str(near_region), "metrics": near_comparison})
	if not require(outer_comparison.pixels > 50 and near_comparison.pixels > 100 and outer_comparison.new_error < outer_comparison.old_error, "actual texture edge contrast did not improve: " + str(outer_comparison)): return
	if not require(near_comparison.new_error < near_comparison.old_error, "actual terrain texture near contrast did not improve: " + str(near_comparison)): return
	report["visual_review"] = {"purpose": "Actual terrain textures and production soil/grass shaders with current compact gameplay LOD, fixed daylight camera, before/after blend; not a gameplay scene or performance capture.", "source_count_per_pass": 12000, "paired_source_count": 24000, "represented_blade_roots": 48000, "near_distance": 22.0, "far_distance": 32.0, "transition_width": 4.0, "intermediate_density": .25, "geometry": "two-segment near clumps and camera-facing single-ribbon intermediate clumps", "populations_identical_before_after": true}
	var readme := FileAccess.open(OUTPUT + "README.md", FileAccess.WRITE)
	if readme != null: readme.store_string("# Grass terrain blend fixture\n\nThe constant-texture quantitative checks retain the original 90 m proof range. The actual-texture daylight comparison uses paired production near and intermediate clumps at a 22 m near / 32 m far distance with a 4 m transition, 25% intermediate density, and full camera-facing single ribbons. Both before and after retain 12,000 identical source identities per pass (24,000 paired sources / 48,000 represented blade roots); near and intermediate selection is complementary. Both passes are hidden together for the terrain reference. Terrain color matching applies at every distance while preserving restrained nearby blade brightness relief. Ground lighting and final brightness convergence blend over 70 to 90% of draw distance, completing before root disappearance. Improved outer-band and populated near-band contrast accompany the wide captures. These are controlled appearance checks, not gameplay performance measurements.\n")

func projected_band(start: float, end: float, maximum_height: float = 1.0) -> Rect2i:
	var minimum := Vector2(INF, INF); var maximum := Vector2(-INF, -INF)
	for x in [-7.2, 7.2]:
		for height in [0.0, maximum_height]:
			for z in [start, end]:
				var pixel := camera.unproject_position(grass.to_global(Vector3(x, height, z)))
				minimum = minimum.min(pixel); maximum = maximum.max(pixel)
	var region := Rect2i(Vector2i(floori(minimum.x), floori(minimum.y)), Vector2i(ceili(maximum.x)-floori(minimum.x), ceili(maximum.y)-floori(minimum.y)))
	return region.intersection(Rect2i(Vector2i.ZERO, viewport.size))

func require(condition: bool, message: String) -> bool:
	if condition: return true
	fail(message); return false

func write_report() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	var file := FileAccess.open(OUTPUT + "report.json", FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report, "\t"))

func fail(message: String) -> void:
	if failed: return
	failed = true; report.status = "FAIL"; report.error = message; write_report()
	push_error("GRASS_TERRAIN_BLEND_GPU=FAIL " + message)
	quit(1)
