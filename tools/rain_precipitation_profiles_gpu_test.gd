extends SceneTree

# Exercise the actual production emitter on Vulkan, first against a black
# control world, then reattach it to the real terrain/cloud-composited scene.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
const OUT := "res://.godot/rain_profiles_gpu"
var world: Node
var local: Node
var particles: GPUParticles3D
var driver: RefCounted
var camera: Camera3D
var probe: Camera3D
var vp: SubViewport
var failed := false
var label := "normal"
var restart_count := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("requires RenderingDevice; headless cannot prove particle rendering")
		return
	call_deferred("run")

func run() -> void:
	Engine.max_fps = 60
	world = WORLD.instantiate()
	if "--rain-profiles-certified" in OS.get_cmdline_user_args():
		label = "certified"
		var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", world, 5000000.0)
		launcher.free()
	root.add_child(world)
	for i in range(20): await process_frame
	world.get_node("Explorer").set_physics_process(false)
	world.get_node("RingDayNightCycle").set("Paused", true)
	world.get_node("RingVolumetricClouds").set_process(false)
	world.get_node("SunshineCloudBridge").set_process(false)
	world.get_node("DistantRainCurtains").set_process(false)
	local = world.get_node("LocalWeatherEffects")
	local.set_process(false)
	local.set("EnableLightning", false)
	particles = local.get_node("CameraLocalPrecipitation")
	camera = world.get_node("Explorer/Head/Camera3D")
	driver = DRIVER.new()
	driver.call("SetIntensity", local, 0.0, false, Vector2.ZERO)
	for i in range(200): local.call("_Process", 1.0 / 60.0)
	restart_count = int(local.get("ParticleRestartCount"))
	setup_probe()
	particles.speed_scale = 0.0
	particles.use_fixed_seed = true
	particles.seed = 371
	particles.reparent(vp, true)
	# Keep engine time advancing while manually seeking the GPU simulation.
	# Only freeze the main scene's clock later, when no particle steps remain.
	particles.hide()
	var black := await capture(vp, "black")
	particles.show()
	var summaries := []
	for intensity in [.15, .5, 1.0]:
		driver.call("SetIntensity", local, intensity, false, Vector2(5, -2))
		await advance(180)
		print("RAIN_PROFILE_GPU_EMITTER state=", local.call("GetPrecipitationDiagnostics"), " position=", particles.global_transform, " camera=", probe.global_transform, " bounds=", particles.capture_aabb(), " visible=", particles.is_visible_in_tree(), " emitting=", particles.emitting)
		var total := 0.0
		var count := 0
		for sample in range(4):
			await advance(8)
			var im := await capture(vp, str(int(intensity * 100)) + "_" + str(sample))
			var change := difference(im, black)
			if not require(change.count > 10 and change.mean > .000001, "invisible rain stage " + str(intensity)): return
			total += float(change.mean)
			count += int(change.count)
		var result := {"intensity": intensity, "mean": total / 4, "pixels": count / 4}
		summaries.append(result)
		print("RAIN_PROFILE_GPU_STAGE ", result)
	for i in range(1, summaries.size()):
		if not require(float(summaries[i].mean) > float(summaries[i - 1].mean) * 1.2 and int(summaries[i].pixels) > int(summaries[i - 1].pixels), "stages did not visibly increase " + str(summaries)): return
	var before := await capture(vp, "motion_before")
	await advance(6)
	var after := await capture(vp, "motion_after")
	if not require(difference(before, after).count > 10, "particles do not move"): return
	# Wet-to-wet transition must never blank the visible emitter or restart it.
	for intensity in [.15, 1.0]:
		driver.call("SetIntensity", local, intensity, false, Vector2.ZERO)
		for sample in range(16):
			await advance(6)
			var live := await capture(vp)
			if not require(difference(live, black).count > 10, "wet transition emptied the screen"): return
	if not require(int(local.get("ParticleRestartCount")) == restart_count and particles.amount == 4096, "rain stage changed emitter lifecycle or allocation"): return
	driver.call("SetIntensity", local, 0.0, false, Vector2.ZERO)
	await advance(180)
	var dry := await capture(vp, "dry")
	if not require(not particles.emitting and difference(dry, black).count == 0, "dry weather left rain particles"): return
	# Back to the actual scene, retaining its existing clouds/terrain/depth path.
	particles.reparent(local, true)
	driver.call("SetIntensity", local, 1.0, false, Vector2.ZERO)
	await advance(180)
	for layer: CanvasLayer in world.find_children("*", "CanvasLayer", true, false):
		if layer.layer > 0: layer.hide()
	var attributes := world.get_node("WorldEnvironment").get("camera_attributes") as CameraAttributesPractical
	if attributes: attributes.auto_exposure_enabled = false
	for name in ["SunshineCloudBridge", "SunshineCloudsDriver", "DaySideAtmosphere", "ScaledFarViewport"]:
		var node := world.get_node_or_null(name)
		if node: node.set_process(false)
	Engine.time_scale = 0.0
	particles.hide()
	for i in range(100): await process_frame
	var off_a: Image
	var off_b: Image
	var drift := INF
	for attempt in range(30):
		off_a = await capture(root)
		off_b = await capture(root)
		drift = float(difference(off_a, off_b).mean)
		if drift < .00001: break
	if not require(drift < .00001, "main OFF/OFF view did not settle: " + str(drift)): return
	particles.show()
	var on := await capture(root, "main_on")
	particles.hide()
	var off_after := await capture(root, "main_off")
	drift = maxf(drift, float(difference(off_b, off_after).mean))
	var effect := minf(float(difference(on, off_b).mean), float(difference(on, off_after).mean))
	print("RAIN_PROFILE_MAIN_COMPOSITE effect=", effect, " drift=", drift)
	if not require(effect > maxf(.000001, drift * 2.0), "no stable main-view particle contribution"): return
	print("RAIN_PRECIPITATION_PROFILES_GPU=PASS preset=", label, " stages=", summaries)
	quit(0)

func setup_probe() -> void:
	vp = SubViewport.new()
	vp.size = Vector2i(1280, 720)
	vp.own_world_3d = true
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)
	var we := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color.BLACK
	env.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	we.environment = env
	vp.add_child(we)
	probe = Camera3D.new()
	probe.current = true
	probe.far = 1000
	probe.fov = camera.fov
	vp.add_child(probe)
	probe.global_transform = camera.global_transform

func advance(frames: int) -> void:
	for i in range(frames):
		local.call("_Process", 1.0 / 30.0)
		if particles.emitting: particles.request_particles_process(1.0 / 30.0)
		else: particles.request_particles_process(0.0, 1.0 / 30.0)
		await RenderingServer.frame_post_draw

func capture(view: Viewport, suffix := "") -> Image:
	for i in range(4): await RenderingServer.frame_post_draw
	var im := view.get_texture().get_image()
	if im == null or im.is_empty(): fail("empty GPU image"); return Image.new()
	if not suffix.is_empty():
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
		if im.save_png(OUT.path_join(label + "_" + suffix + ".png")) != OK: fail("capture save failed")
	return im

func difference(a: Image, b: Image) -> Dictionary:
	var count := 0
	var total := 0.0
	var n := 0
	for y in range(0, a.get_height(), 2):
		for x in range(0, a.get_width(), 2):
			var c := a.get_pixel(x, y).srgb_to_linear() - b.get_pixel(x, y).srgb_to_linear()
			var d := (absf(c.r) + absf(c.g) + absf(c.b)) / 3.0
			if not is_finite(d): fail("nonfinite capture"); return {"count": 0, "mean": 0.0}
			if d > .001: count += 1
			total += d
			n += 1
	return {"count": count, "mean": total / max(1, n)}

func require(ok: bool, message: String) -> bool:
	if not ok: fail(message)
	return ok

func fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("RAIN_PRECIPITATION_PROFILES_GPU=FAIL " + message)
	quit(1)
