extends SceneTree

# Headless launcher for the scaled far regional-band boundary contract.
# Usage: godot --headless --path . -s res://scripts/Tools/far_regional_transition_contract.gd

func _initialize() -> void:
	# The contract constructs only its deterministic sampler and edit snapshot.
	# Do not instantiate the gameplay scene: unrelated renderers retain GPU RIDs
	# during shutdown and make an otherwise pure mesh-data test look leaky.
	var fixture: PackedScene = load("res://scenes/FarRegionalTransitionContract.tscn")
	var scaled_far: Node = fixture.instantiate()
	get_root().add_child(scaled_far)
	var result: String = scaled_far.call("RunStandaloneFarRegionalTransitionContract")
	scaled_far.queue_free()
	await process_frame
	if "--rendered" in OS.get_cmdline_user_args():
		var rendered_result: String = await _run_rendered_shader_smoke()
		print("FAR_REGIONAL_RENDERED_SMOKE=" + rendered_result)
		if rendered_result.begins_with("FAIL:"):
			result = rendered_result
	print("FAR_REGIONAL_TRANSITION_CONTRACT=" + result)
	quit(0 if result == "PASS" else 1)


# Optional smoke coverage for the changed global terrain shader. This is not a
# gameplay-transition visual test: it proves a regional material with the new
# clip switch compiles and produces pixels in an isolated render target.
func _run_rendered_shader_smoke() -> String:
	if RenderingServer.get_rendering_device() == null:
		return "SKIP: RenderingDevice unavailable"
	var viewport := SubViewport.new()
	viewport.size = Vector2i(64, 64)
	viewport.transparent_bg = false
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var world := World3D.new()
	viewport.world_3d = world
	get_root().add_child(viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	viewport.add_child(world_environment)

	var camera := Camera3D.new()
	viewport.add_child(camera)
	camera.position = Vector3(0.0, 1.0, 2.0)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	camera.current = true
	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-45.0, 30.0, 0.0)
	viewport.add_child(light)
	for _frame: int in 2:
		await process_frame
	var blank_image := viewport.get_texture().get_image()
	var terrain := MeshInstance3D.new()
	var quad := QuadMesh.new()
	quad.size = Vector2(2.0, 2.0)
	terrain.mesh = quad
	var material := ShaderMaterial.new()
	material.shader = load("res://materials/ring_terrain_global.gdshader")
	material.set_shader_parameter("regional_band_distance_clip", false)
	material.set_shader_parameter("camera_relative_region", true)
	material.set_shader_parameter("regional_surface_up", Vector3.UP)
	material.set_shader_parameter("camera_world_position", camera.position)
	material.set_shader_parameter("ring_center", Vector3.ZERO)
	material.set_shader_parameter("sun_direction", Vector3(0.0, 1.0, 0.0))
	terrain.material_override = material
	viewport.add_child(terrain)
	for _frame: int in 3:
		await process_frame
	var image := viewport.get_texture().get_image()
	var changed_pixels := false
	for y: int in image.get_height():
		for x: int in image.get_width():
			var rendered_color := image.get_pixel(x, y)
			var blank_color := blank_image.get_pixel(x, y)
			if absf(rendered_color.r - blank_color.r) + absf(rendered_color.g - blank_color.g) + absf(rendered_color.b - blank_color.b) > 0.001:
				changed_pixels = true
				break
		if changed_pixels:
			break
	viewport.queue_free()
	await process_frame
	return "PASS" if changed_pixels else "FAIL: Regional shader smoke did not change the black render target."
