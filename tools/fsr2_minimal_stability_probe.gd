extends SceneTree

## Minimal FSR2 temporal-stability repro (no gameplay scene). A static camera looks at a ground plane and a few boxes carrying a
## high-frequency procedural texture; for each configuration the root viewport is captured for 32 frames after a 96-frame warmup and
## the per-pixel temporal luminance standard deviation is reported (same statistics as tools/compositor_temporal_stability_test.gd).
## Separates engine FSR2 behaviour from project content. Optional: -- --shader (use a ShaderMaterial instead of StandardMaterial3D),
## --exposure (enable auto exposure), --vertex-position (ShaderMaterial that writes POSITION in vertex()).
## Run (windowed Vulkan, not --headless):  godot --path . --script res://tools/fsr2_minimal_stability_probe.gd
const STABILITY_HELPER := preload("res://tools/CompositorStabilityHelper.cs")
const CONFIGS := [
	{"name": "native", "mode": Viewport.SCALING_3D_MODE_BILINEAR, "scale": 1.0, "taa": false},
	{"name": "taa", "mode": Viewport.SCALING_3D_MODE_BILINEAR, "scale": 1.0, "taa": true},
	{"name": "fsr2_100", "mode": Viewport.SCALING_3D_MODE_FSR2, "scale": 1.0, "taa": false},
	{"name": "fsr2_67", "mode": Viewport.SCALING_3D_MODE_FSR2, "scale": 0.67, "taa": false},
]

func _init() -> void: call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("FSR2_MINIMAL=FAIL requires Vulkan/Forward+"); quit(1); return
	var args := OS.get_cmdline_user_args()
	var world := Node3D.new(); root.add_child(world)
	for argument in args:
		if argument.begins_with("--offset="):
			var parts := argument.get_slice("=", 1).split(",")
			world.position = Vector3(float(parts[0]), float(parts[1]), float(parts[2]))
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.4, 0.55, 0.8)
	environment.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	var world_environment := WorldEnvironment.new(); world_environment.environment = environment
	if "--exposure" in args:
		var attributes := CameraAttributesPractical.new(); attributes.auto_exposure_enabled = true
		world_environment.camera_attributes = attributes
	world.add_child(world_environment)
	var sun := DirectionalLight3D.new(); sun.rotation_degrees = Vector3(-50, 30, 0); world.add_child(sun)
	for argument in args:
		if argument.begins_with("--sun-energy="): sun.light_energy = float(argument.get_slice("=", 1))
		elif argument.begins_with("--tonemap-exposure="): environment.tonemap_exposure = float(argument.get_slice("=", 1))
		elif argument.begins_with("--background-energy="): environment.background_energy_multiplier = float(argument.get_slice("=", 1))
	var material: Material = make_material(args)
	var ground := MeshInstance3D.new(); var plane := PlaneMesh.new(); plane.size = Vector2(200, 200); ground.mesh = plane
	ground.material_override = material; world.add_child(ground)
	var multimesh: MultiMesh = null
	if "--multimesh" in args:
		ground.visible = false
		multimesh = MultiMesh.new(); multimesh.transform_format = MultiMesh.TRANSFORM_3D; multimesh.mesh = plane; multimesh.instance_count = 1
		multimesh.set_instance_transform(0, Transform3D.IDENTITY)
		var multimesh_instance := MultiMeshInstance3D.new(); multimesh_instance.multimesh = multimesh; multimesh_instance.material_override = material
		world.add_child(multimesh_instance)
	for index in 6:
		var box := MeshInstance3D.new(); var mesh := BoxMesh.new(); mesh.size = Vector3(1.5, 3.0, 1.5); box.mesh = mesh
		box.material_override = material; box.position = Vector3(-6.0 + index * 2.5, 1.5, -8.0 - index * 1.5); world.add_child(box)
	var camera := Camera3D.new(); camera.position = Vector3(0, 1.7, 4); camera.rotation_degrees = Vector3(-10, 0, 0)
	for argument in args:
		if argument.begins_with("--near="): camera.near = float(argument.get_slice("=", 1))
		elif argument.begins_with("--far="): camera.far = float(argument.get_slice("=", 1))
		elif argument.begins_with("--fov="): camera.fov = float(argument.get_slice("=", 1))
	world.add_child(camera); camera.make_current()
	for argument in args:
		# A nearly invisible camera-attached transparent quad covering the whole view (e.g. a haze / fog sheet).
		if argument.begins_with("--overlay="):
			var overlay := MeshInstance3D.new(); var quad := QuadMesh.new(); quad.size = Vector2(4, 4); overlay.mesh = quad
			var overlay_material := StandardMaterial3D.new()
			overlay_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
			overlay_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
			overlay_material.albedo_color = Color(0.8, 0.85, 0.9, float(argument.get_slice("=", 1)))
			overlay.material_override = overlay_material; overlay.position = Vector3(0, 0, -0.5); camera.add_child(overlay)
	if "--subviewport" in args or "--subviewport-own-world" in args:
		# A second viewport rendering every frame (like the gameplay minimap / far viewports): shared World3D unless -own-world.
		var sub := SubViewport.new(); sub.size = Vector2i(512, 512); sub.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		sub.own_world_3d = "--subviewport-own-world" in args
		var sub_camera := Camera3D.new(); sub_camera.position = Vector3(0, 60, 0); sub_camera.rotation_degrees = Vector3(-90, 0, 0)
		sub.add_child(sub_camera); root.add_child(sub); sub_camera.current = true
		if sub.own_world_3d:
			var other := MeshInstance3D.new(); other.mesh = BoxMesh.new(); sub.add_child(other)
	var velocity_probe: CompositorEffect = load("res://tools/velocity_stats_effect.gd").new()
	var input_probe_script: Script = load("res://tools/fsr2_input_capture_effect.gd")
	var input_probe: CompositorEffect = input_probe_script.new()
	var probe_effects: Array[CompositorEffect] = []
	if "--velocity" in args: probe_effects.append(velocity_probe)
	if "--input-noise" in args: probe_effects.append(input_probe)
	for argument in args:
		if argument.begins_with("--alpha-write="):
			var alpha_effect: CompositorEffect = load("res://tools/fsr2_alpha_write_effect.gd").new()
			alpha_effect.set("alpha_value", float(argument.get_slice("=", 1))); probe_effects.insert(0, alpha_effect)
	if not probe_effects.is_empty():
		var compositor := Compositor.new(); compositor.compositor_effects = probe_effects; world_environment.compositor = compositor
	var helper: RefCounted = STABILITY_HELPER.new()
	for argument in args:
		if argument.begins_with("--max-fps="): Engine.max_fps = int(argument.get_slice("=", 1))
	for config in CONFIGS:
		root.scaling_3d_mode = config["mode"]
		root.scaling_3d_scale = config["scale"]
		root.use_taa = config["taa"]
		root.fsr_sharpness = 2.0 if "--no-sharpen" in args else 0.8
		root.msaa_3d = Viewport.MSAA_DISABLED
		root.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED
		for ignored in 96:
			if "--touch" in args: ground.transform = ground.transform
			if multimesh != null and "--touch" in args: multimesh.set_instance_transform(0, Transform3D.IDENTITY)
			await process_frame
		var started := false
		var output_series: Array = []
		if "--input-noise" in args: input_probe.call("capture", 32)
		for frame in 32:
			if "--touch" in args: ground.transform = ground.transform
			if multimesh != null and "--touch" in args: multimesh.set_instance_transform(0, Transform3D.IDENTITY)
			await RenderingServer.frame_post_draw
			var image := root.get_texture().get_image()
			if image.get_format() != Image.FORMAT_RGBA8: image.convert(Image.FORMAT_RGBA8)
			if not started: helper.call("Begin", image.get_width(), image.get_height()); started = true
			helper.call("Accumulate", image.get_data())
			if "--input-noise" in args: output_series.append(input_probe_script.sample_output(image))
		if "--input-noise" in args:
			var input_noise: Dictionary = input_probe_script.relative_noise(input_probe.call("take"), 1.0e-4)
			var output_noise: Dictionary = input_probe_script.relative_noise(output_series, 1.0e-3)
			print("FSR2_MINIMAL cfg=%s input_noise=%s output_noise=%s" % [config["name"], str(input_noise), str(output_noise)])
		if "--velocity" in args:
			velocity_probe.call("capture", 6)
			for ignored in 8: await RenderingServer.frame_post_draw
			for record in velocity_probe.call("take"): print("FSR2_MINIMAL cfg=%s velocity %s" % [config["name"], str(record)])
		var stats: Dictionary = helper.call("Finish", ProjectSettings.globalize_path("res://.godot/fsr2_minimal_%s.png" % config["name"]))
		for roi in ["edge", "flat", "whole"]:
			var entry: Dictionary = stats[roi]
			print("FSR2_MINIMAL cfg=%s roi=%s mean=%.6f p99=%.6f" % [config["name"], roi, float(entry["mean"]), float(entry["p99"])])
	print("FSR2_MINIMAL=DONE")
	quit(0)

func make_material(args: PackedStringArray) -> Material:
	var image := Image.create(256, 256, true, Image.FORMAT_RGBA8)
	var rng := RandomNumberGenerator.new(); rng.seed = 7
	for y in 256:
		for x in 256:
			var checker := 0.75 if ((x >> 3) + (y >> 3)) % 2 == 0 else 0.35
			var value := clampf(checker + rng.randf_range(-0.25, 0.25), 0.0, 1.0)
			var alpha := 1.0 if (not "--scissor" in args or rng.randf() > 0.5) else 0.0
			image.set_pixel(x, y, Color(value * 0.6, value, value * 0.4, alpha))
	image.generate_mipmaps()
	var texture := ImageTexture.create_from_image(image)
	if "--shader" in args or "--vertex-position" in args:
		var shader := Shader.new()
		var vertex := "void vertex() { POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)); }\n" if "--vertex-position" in args else ""
		shader.code = "shader_type spatial;\nuniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;\n" + vertex \
			+ "void fragment() { ALBEDO = texture(albedo_tex, UV * 20.0).rgb; ROUGHNESS = 0.8; }\n"
		var shader_material := ShaderMaterial.new(); shader_material.shader = shader
		shader_material.set_shader_parameter("albedo_tex", texture)
		return shader_material
	var standard := StandardMaterial3D.new()
	standard.albedo_texture = texture
	standard.uv1_scale = Vector3(20, 20, 20)
	standard.roughness = 0.8
	if "--nearest" in args: standard.texture_filter = BaseMaterial3D.TEXTURE_FILTER_NEAREST
	if "--scissor" in args: standard.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR; standard.cull_mode = BaseMaterial3D.CULL_DISABLED
	return standard
