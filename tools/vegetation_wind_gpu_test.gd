extends SceneTree

# Real production MultiMesh, shader, grass prototype, normalized KayKit mesh,
# and procedural trunk/canopy controls. No new game assets or saved settings.
const DRIVER := preload("res://tools/VegetationWindTestDriver.cs")
const OUT := "user://generated/grass/vegetation_wind_gpu"
const SIZE := 512
var vp: SubViewport
var camera: Camera3D
var geometry: MultiMeshInstance3D
var material: ShaderMaterial
var driver: RefCounted
var base_camera := Transform3D.IDENTITY
var instance_basis := Basis.IDENTITY
var pose := Transform3D.IDENTITY
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("RenderingDevice required; Dummy rendering is not GPU evidence")
		return
	call_deferred("run")

func run() -> void:
	Engine.max_fps = 60
	driver = DRIVER.new()
	setup_view()
	var procedural: Dictionary = driver.call("LoadProceduralTreeMeshes")
	var meshes := {
		"grass": driver.call("LoadGrassMesh"),
		"meadow": driver.call("LoadMeadowMesh"),
		"authored_tree": driver.call("LoadAuthoredTreeMesh"),
		"trunk": procedural.trunk,
		"canopy": procedural.canopy
	}
	for kind: String in meshes:
		var is_grass := kind == "grass" or kind == "meadow"
		configure_mesh(meshes[kind], is_grass)
		set_field(Vector2.ZERO, Vector4(0, 1, 0, 1))
		var calm := await capture(kind + "_calm")
		if not require(metrics(calm).count > 10, "invisible production mesh: " + kind): return
		if is_grass:
			# Same blade and clock, distinct canonical phase anchors. This exercises
			# the production grass gust field rather than merely shared tree wind.
			geometry.multimesh.set_instance_custom_data(0, Color(.70, .50, .11, .69))
			set_field(Vector2(-16, 0), Vector4(.7, .714, .3, .954))
			var phase_a := await capture(kind + "_spatial_phase_a")
			geometry.multimesh.set_instance_custom_data(0, Color(.70, .50, .61, .17))
			var phase_b := await capture(kind + "_spatial_phase_b")
			if not require(difference(phase_a, phase_b).count > 10, "grass canonical spatial gust phase is invisible"): return
			var phase_b_frozen := await capture(kind + "_spatial_phase_b_frozen")
			if not require(difference(phase_b, phase_b_frozen).count == 0, "grass spatial gust advances without publisher clock"): return
		set_field(Vector2(-16, 0), Vector4(.7, .714, .3, .954))
		var right := await capture(kind + "_right")
		set_field(Vector2(16, 0), Vector4(.7, .714, .3, .954))
		var left := await capture(kind + "_left")
		var motion := difference(right, left)
		var center := float(metrics(calm).x)
		if not require(float(metrics(right).x) > center + .25 and float(metrics(left).x) < center - .25 and motion.count > 10, "wind direction ignored instance yaw/scale: " + kind + " " + str(motion)): return
		# Low and strong wind should differ in the same downwind direction.
		set_field(Vector2(-4, 0), Vector4(.7, .714, .3, .954))
		var breeze := await capture(kind + "_breeze")
		if not require(float(metrics(right).x) > float(metrics(breeze).x) + .2, "strong wind not stronger: " + kind): return
		set_field(Vector2(-16, 0), Vector4(-.8, .6, -.5, .866))
		var sway := await capture(kind + "_sway")
		if not require(difference(right, sway).count > 10, "time-based sway invisible: " + kind): return
		var still := await capture(kind + "_still")
		if not require(difference(sway, still).count == 0, "frozen publisher still animates via shader TIME: " + kind): return
		set_field(Vector2(-16, 0), Vector4(.7, .714, .3, .954))
		# Translate/rotate camera, ring frame, and instance together. The output
		# should agree despite non-uniform prototype scale and a new world frame.
		var rotation := Basis(Vector3(1, 2, -1).normalized(), .71)
		pose = Transform3D(rotation, Vector3(20000, -12000, 8000))
		apply_pose()
		set_field(Vector2(-16, 0), Vector4(.7, .714, .3, .954))
		var rebased := await capture(kind + "_rebased_rotated")
		var rebase_delta := difference(right, rebased)
		set_field(Vector2.ZERO, Vector4(.7, .714, .3, .954))
		var rebase_calm := await capture(kind + "_rebased_calm")
		var baseline_delta := difference(calm, rebase_calm)
		var shift := float(metrics(right).x) - center
		var rebased_shift := float(metrics(rebased).x) - float(metrics(rebase_calm).x)
		print("WIND_REBASE_CONTROL ", kind, " wind=", rebase_delta, " calm=", baseline_delta, " shifts=", shift, "/", rebased_shift)
		# Large absolute translations also quantize the undeformed raster by
		# ~1px. Compare deformation against that independent calm control.
		if not require(rebase_delta.mean < maxf(.001, float(baseline_delta.mean) * 2.0) and absf(shift - rebased_shift) < .5, "bend changed beyond calm rebase drift: " + kind + " " + str(rebase_delta)): return
		pose = Transform3D.IDENTITY
		apply_pose()
		# A wind along canonical across points at/away from the camera in this
		# frame: rotating the camera/whole frame shouldn't turn it into +X sway.
		set_field(Vector2(0, 16), Vector4(.7, .714, .3, .954))
		var across := await capture(kind + "_across")
		if is_grass:
			# Across wind moves toward/away from the camera. A curved ribbon can
			# shorten vertically, shifting its pixel centroid, but it must not gain
			# any horizontal (around) extent.
			var calm_bounds := metrics(calm)
			var across_bounds := metrics(across)
			var bound_delta := maxf(absf(float(across_bounds.min_x) - float(calm_bounds.min_x)), absf(float(across_bounds.max_x) - float(calm_bounds.max_x)))
			print("WIND_ACROSS_BOUNDS ", kind, " calm=", calm_bounds.min_x, "/", calm_bounds.max_x, " across=", across_bounds.min_x, "/", across_bounds.max_x, " delta=", bound_delta)
			if not require(bound_delta <= 1.0, "across wind changed grass horizontal bounds: " + kind + " delta=" + str(bound_delta)): return
		elif not require(absf(float(metrics(across).x) - center) < .35, "across wind bent along-around: " + kind): return
		# Explicitly disabled, zero-vector, and degenerate-axis/root guards.
		for guard in ["disabled", "zero", "axis", "radial"]:
			set_field(Vector2(-16, 0), Vector4(.7, .714, .3, .954))
			if guard == "disabled": RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
			if guard == "zero": RenderingServer.global_shader_parameter_set("ring_local_wind_vector", Vector2.ZERO)
			if guard == "axis": RenderingServer.global_shader_parameter_set("ring_local_wind_axis", Vector3.ZERO)
			if guard == "radial": RenderingServer.global_shader_parameter_set("ring_local_wind_center", Vector3.ZERO)
			var guarded := await capture()
			if not require(difference(calm, guarded).count == 0, "invalid/calm guard changed geometry: " + kind + "/" + guard): return
		print("VEGETATION_WIND_GPU_MESH ", kind, " positive_shift=", float(metrics(right).x) - center, " reverse_shift=", float(metrics(left).x) - center, " rebase=", rebase_delta)
	# The real shader's flat-LOD material flag must suppress all bending.
	var quad := QuadMesh.new()
	quad.size = Vector2(1, 1)
	configure_mesh(quad, false)
	material.set_shader_parameter("vegetation_wind_enabled", false)
	set_field(Vector2.ZERO, Vector4.ZERO)
	var flat_calm := await capture("flat_calm")
	set_field(Vector2(-80, 0), Vector4(1, 0, 1, 0))
	var flat_wind := await capture("flat_wind")
	if not require(metrics(flat_calm).count > 10 and difference(flat_calm, flat_wind).count == 0, "flat tree billboard bent"): return
	# A tiny texture pass calls the production helper directly to verify exact
	# root pinning and direction at the ring seam without raster-edge ambiguity.
	if not await helper_contract(): return
	print("VEGETATION_WIND_GPU=PASS grass/authored/procedural, direction/scale/rebase, motion/freeze, calm guards, flat LOD, roots/seam")
	quit(0)

func setup_view() -> void:
	vp = SubViewport.new()
	vp.size = Vector2i(SIZE, SIZE)
	vp.own_world_3d = true
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)
	var we := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color.BLACK
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color.WHITE
	env.ambient_light_energy = 1.0
	env.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	we.environment = env
	vp.add_child(we)
	camera = Camera3D.new()
	camera.current = true
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.far = 100.0
	vp.add_child(camera)
	geometry = MultiMeshInstance3D.new()
	geometry.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	geometry.extra_cull_margin = 6.0
	vp.add_child(geometry)

func configure_mesh(mesh: Mesh, grass: bool) -> void:
	var mm := MultiMesh.new()
	mm.transform_format = MultiMesh.TRANSFORM_3D
	mm.use_custom_data = true
	mm.mesh = mesh
	mm.instance_count = 1
	geometry.multimesh = mm
	material = ShaderMaterial.new()
	material.shader = load("res://materials/ring_standing_grass.gdshader" if grass else "res://materials/ring_tree.gdshader")
	material.set_shader_parameter("local_cloud_shadow_enabled", false)
	if grass:
		material.set_shader_parameter("grass_base_color", Vector3.ONE)
		material.set_shader_parameter("grass_tip_color", Vector3.ONE)
		# Production contract: curve, tint, canonical-around phase, canonical-across phase.
		mm.set_instance_custom_data(0, Color(.70, .50, .11, .69))
	else:
		material.set_shader_parameter("tree_tint", Vector3.ONE)
		material.set_shader_parameter("night_tint", Vector3.ONE)
	geometry.material_override = material
	var height := .75 if grass else 6.0
	var yaw := Basis(Vector3.UP, .63)
	instance_basis = Basis(yaw.x * (.065 if grass else 3.4), yaw.y * height, yaw.z * (.04 if grass else 2.3))
	pose = Transform3D.IDENTITY
	camera.size = height * 1.5
	camera.position = Vector3(0, height * .5, height * 3)
	camera.look_at(Vector3(0, height * .5, 0), Vector3.UP)
	base_camera = camera.transform
	apply_pose()

func apply_pose() -> void:
	geometry.multimesh.set_instance_transform(0, pose * Transform3D(instance_basis, Vector3.ZERO))
	camera.transform = pose * base_camera

func set_field(wind: Vector2, wave: Vector4) -> void:
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector", wind)
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave", wave)
	RenderingServer.global_shader_parameter_set("ring_local_wind_center", pose * Vector3(0, 5000000, 0))
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis", pose.basis * Vector3.BACK)

func helper_contract() -> bool:
	var sample := SubViewport.new()
	sample.size = Vector2i(8, 8)
	sample.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(sample)
	var rect := ColorRect.new()
	rect.size = Vector2(8, 8)
	var shader := Shader.new()
	shader.code = "shader_type canvas_item;\n#include \"res://materials/ring_local_wind.gdshaderinc\"\nuniform vec3 root_point; uniform float weight; void fragment(){COLOR=vec4(vec3(.5)+ring_local_wind_displacement(root_point,weight,6.0)*.25,1.0);}"
	var mat := ShaderMaterial.new()
	mat.shader = shader
	rect.material = mat
	sample.add_child(rect)
	RenderingServer.global_shader_parameter_set("ring_local_wind_center", Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis", Vector3.UP)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector", Vector2(16, 0))
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 1.0)
	mat.set_shader_parameter("root_point", Vector3(5000000, 0, 0))
	mat.set_shader_parameter("weight", 0.0)
	var pinned := await helper_pixel(sample)
	if not require(pinned.distance_to(Vector3(.5, .5, .5)) < .007, "root displacement not zero"): return false
	mat.set_shader_parameter("weight", 1.0)
	mat.set_shader_parameter("root_point", Vector3(5000000, 0, -.5))
	var seam_a := await helper_pixel(sample)
	mat.set_shader_parameter("root_point", Vector3(5000000, 0, .5))
	var seam_b := await helper_pixel(sample)
	return require(seam_a.z > .501 and seam_a.distance_to(seam_b) < .007, "wind direction/phase discontinuity at canonical seam")

func helper_pixel(view: Viewport) -> Vector3:
	for i in range(4): await RenderingServer.frame_post_draw
	var c := view.get_texture().get_image().get_pixel(4, 4)
	return Vector3(c.r, c.g, c.b)

func capture(label := "") -> Image:
	for i in range(4): await RenderingServer.frame_post_draw
	var im := vp.get_texture().get_image()
	if im == null or im.is_empty(): fail("empty GPU capture"); return Image.new()
	if not label.is_empty():
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
		if im.save_png(OUT.path_join(label + ".png")) != OK: fail("capture save failed")
	return im

func metrics(im: Image) -> Dictionary:
	var count := 0
	var sum_x := 0.0
	var min_x := im.get_width()
	var max_x := -1
	for y in range(im.get_height()):
		for x in range(im.get_width()):
			var value := im.get_pixel(x, y).get_luminance()
			if not is_finite(value): fail("nonfinite image")
			if value > .05:
				count += 1
				sum_x += x
				min_x = mini(min_x, x)
				max_x = maxi(max_x, x)
	return {"count": count, "x": sum_x / max(1, count), "min_x": min_x, "max_x": max_x}

func difference(a: Image, b: Image) -> Dictionary:
	var total := 0.0
	var count := 0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			var delta := absf(a.get_pixel(x, y).get_luminance() - b.get_pixel(x, y).get_luminance())
			if delta > .03: count += 1
			total += delta
	return {"mean": total / (a.get_width() * a.get_height()), "count": count}

func require(ok: bool, message: String) -> bool:
	if not ok: fail(message)
	return ok
func fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("VEGETATION_WIND_GPU=FAIL " + message)
	quit(1)
