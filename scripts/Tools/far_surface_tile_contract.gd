extends SceneTree

func _initialize() -> void:
	var fixture: PackedScene = load("res://scenes/FarRegionalTransitionContract.tscn")
	var far: Node = fixture.instantiate()
	get_root().add_child(far)
	var result: String = far.call("RunStandaloneFarSurfaceTileContract")
	far.queue_free()
	await process_frame
	if "--rendered" in OS.get_cmdline_user_args():
		var rendered: String = await _rendered_contract()
		print("FAR_SURFACE_TILE_RENDERED=" + rendered)
		if rendered.begins_with("FAIL:"):
			result = rendered
	print("FAR_SURFACE_TILE_CONTRACT=" + result)
	quit(0 if result == "PASS" else 1)

func _difference(a: Image, b: Image) -> float:
	var total := 0.0
	for y: int in a.get_height():
		for x: int in a.get_width():
			var ca := a.get_pixel(x,y)
			var cb := b.get_pixel(x,y)
			total += absf(ca.r-cb.r) + absf(ca.g-cb.g) + absf(ca.b-cb.b)
	return total / float(a.get_width()*a.get_height())

func _capture(viewport: SubViewport) -> Image:
	for _frame: int in 3:
		await process_frame
	return viewport.get_texture().get_image()

func _rendered_contract() -> String:
	if RenderingServer.get_rendering_device() == null:
		return "SKIP: RenderingDevice unavailable"
	var viewport := SubViewport.new()
	viewport.size = Vector2i(128,128)
	viewport.world_3d = World3D.new()
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	get_root().add_child(viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	viewport.add_child(world_environment)
	var camera := Camera3D.new()
	viewport.add_child(camera)
	camera.position = Vector3(3,0,0)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	camera.current = true
	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(0,90,0)
	viewport.add_child(light)
	var arrays: Array = []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = PackedVector3Array([Vector3(0,-1,-1),Vector3(0,1,-1),Vector3(0,1,1),Vector3(0,-1,1)])
	arrays[Mesh.ARRAY_NORMAL] = PackedVector3Array([Vector3.RIGHT,Vector3.RIGHT,Vector3.RIGHT,Vector3.RIGHT])
	arrays[Mesh.ARRAY_COLOR] = PackedColorArray([Color(0.2,0.2,0.2),Color(0.2,0.2,0.2),Color(0.2,0.2,0.2),Color(0.2,0.2,0.2)])
	arrays[Mesh.ARRAY_TEX_UV2] = PackedVector2Array([Vector2(0,0),Vector2(0,1),Vector2(1,1),Vector2(1,0)])
	arrays[Mesh.ARRAY_INDEX] = PackedInt32Array([0,1,2,0,2,3])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	var terrain := MeshInstance3D.new()
	terrain.mesh = mesh
	var material := ShaderMaterial.new()
	material.shader = load("res://materials/ring_terrain_global.gdshader")
	material.set_shader_parameter("regional_band_distance_clip", false)
	material.set_shader_parameter("camera_relative_region", true)
	material.set_shader_parameter("regional_surface_up", Vector3.RIGHT)
	material.set_shader_parameter("camera_world_position", camera.position)
	material.set_shader_parameter("sun_direction", Vector3.RIGHT)
	material.set_shader_parameter("ring_atmosphere_enabled", false)
	material.set_shader_parameter("analytic_cloud_shadow_enabled", false)
	material.set_shader_parameter("surface_tile_angle_start", PI)
	material.set_shader_parameter("surface_tile_angle_span", 0.0)
	material.set_shader_parameter("surface_tile_resolution", 32.0)
	terrain.material_override = material
	viewport.add_child(terrain)
	var fallback: Image = await _capture(viewport)
	var color := Image.create(34,34,false,Image.FORMAT_RGBA8)
	for y: int in 34:
		for x: int in 34:
			color.set_pixel(x,y,Color(0.8,0.1,0.05) if x < 17 else Color(0.05,0.7,0.15))
	color.generate_mipmaps()
	var normal := Image.create(34,34,false,Image.FORMAT_RGBA8)
	normal.fill(Color(0.5,1.0,0.5))
	normal.generate_mipmaps()
	material.set_shader_parameter("surface_tile_color", ImageTexture.create_from_image(color))
	material.set_shader_parameter("surface_tile_normal", ImageTexture.create_from_image(normal))
	material.set_shader_parameter("surface_tile_enabled", true)
	var colored: Image = await _capture(viewport)
	normal.clear_mipmaps()
	normal.fill(Color(0.95,0.7,0.5))
	normal.generate_mipmaps()
	material.set_shader_parameter("surface_tile_normal", ImageTexture.create_from_image(normal))
	var tilted: Image = await _capture(viewport)
	material.set_shader_parameter("surface_tile_enabled", false)
	material.set_shader_parameter("surface_tile_color", null)
	material.set_shader_parameter("surface_tile_normal", null)
	var evicted: Image = await _capture(viewport)
	material.set_shader_parameter("surface_tile_color", ImageTexture.create_from_image(color))
	normal.clear_mipmaps()
	normal.fill(Color(0.5,1,0.5))
	normal.generate_mipmaps()
	material.set_shader_parameter("surface_tile_normal", ImageTexture.create_from_image(normal))
	material.set_shader_parameter("surface_tile_enabled", true)
	var rebound: Image = await _capture(viewport)
	colored.save_png("user://far_surface_tile_color.png")
	tilted.save_png("user://far_surface_tile_normal.png")
	var failure := ""
	if _difference(fallback, colored) < 0.025:
		failure = "FAIL: Tile albedo did not change fallback rendering."
	elif _difference(colored, tilted) < 0.015:
		failure = "FAIL: Curved-frame normal map did not change directional lighting."
	elif _difference(fallback, evicted) > 0.001 or _difference(colored, rebound) > 0.001:
		failure = "FAIL: Tile eviction/rebinding failed to restore valid fallback/resident output."
	viewport.queue_free()
	await process_frame
	return "PASS" if failure.is_empty() else failure
