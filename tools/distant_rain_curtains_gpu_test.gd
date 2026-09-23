extends SceneTree

# Real RenderingDevice test. Uses the production field/mesh/material, with
# isolated output controls plus one actual terrain/cloud-composited main view.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const OUT := "res://.godot/rain_curtains_gpu"
var world: Node
var weather: Node
var rain: Node
var grid: Node
var bridge: Node
var camera: Camera3D
var sun: DirectionalLight3D
var vp: SubViewport
var isolated: MeshInstance3D
var probe_camera: Camera3D
var failed := false
var label := "normal"

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("requires RenderingDevice; headless is not GPU evidence")
		return
	call_deferred("run")

func run() -> void:
	Engine.max_fps = 60
	world = WORLD.instantiate()
	if "--rain-curtains-certified" in OS.get_cmdline_user_args():
		label = "certified"
		var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", world, 5000000.0)
		launcher.free()
	root.add_child(world)
	for i in range(20): await process_frame
	rain = world.get_node_or_null("DistantRainCurtains")
	if rain == null: fail("default field not instantiated"); return
	weather = world.get_node("RingVolumetricClouds")
	grid = world.get_node("ChunkGridManager")
	bridge = world.get_node("SunshineCloudBridge")
	camera = world.get_node("Explorer/Head/Camera3D")
	sun = world.get_node("StarLight")
	world.get_node("Explorer").set_physics_process(false)
	world.get_node("RingDayNightCycle").set_process(false)
	# Atmosphere publishes the eclipse shader global, independently of the clock.
	world.get_node("DaySideAtmosphere").set_process(false)
	world.get_node("LocalWeatherEffects").set_process(false)
	weather.set_process(false)
	bridge.set_process(false)
	rain.set_process(false)
	weather.set("CloudSimulationTimeSeconds", 300.0)
	weather.set("WeatherWindMetersPerSecond", Vector2.ZERO)
	weather.set("Coverage", -10.0)
	weather.set("WeatherClusteringStrength", 0.0)
	rain.set("SnowTemperatureThreshold", -100.0)
	bridge.call("SetBenchmarkViewerPosition", 12000.0, 1000.0, float(grid.get("RingAcrossWidthMeters")) * .5)
	step(160)
	var target := {}
	for c: Dictionary in rain.call("GetCurtainDiagnostics"):
		if bool(c.get("valid", true)) and float(c.surface_meters) < 1400.0:
			var da := absf(float(c.around_meters) - 12000.0)
			if da > 3000 and da < 7000: target = c; break
	if target.is_empty(): fail("no lowland field control"); return
	weather.call("RegisterManualWeatherAt", "curtain-gpu", float(target.around_meters), float(target.across_meters), 1200.0, -9492)
	var ca := float(target.around_meters)
	var cz := float(target.across_meters)
	var cy := (float(target.surface_meters) + 2000.0) * .5
	var center := canonical(ca, cy, cz)
	var up := (canonical(ca, cy + 100.0, cz) - center).normalized()
	var around := (canonical(ca + 100.0, cy, cz) - center).normalized()
	sun.global_basis = Basis.looking_at(-up, around)
	sun.light_energy = 1.0
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	setup_probe()
	for direction in ["along", "across"]:
		var oa := ca - 4500.0 if direction == "along" else ca
		var oz := cz if direction == "along" else cz - 4500.0
		bridge.call("SetBenchmarkViewerPosition", oa, cy, oz)
		center = canonical(ca, cy, cz)
		up = (canonical(ca, cy + 100.0, cz) - center).normalized()
		camera.look_at(center, up)
		step(160)
		var source := source_mesh(target)
		if source == null: fail(direction + " production curtain missing"); return
		copy_source(source)
		isolated.visible = false
		var off := await read_image(vp, direction + "_off")
		isolated.visible = true
		var day := await read_image(vp, direction + "_day")
		var day_delta := difference(day, off)
		if not require(day_delta.count > 30 and day_delta.mean > .0001, direction + " edge-on/invisible curtain " + str(day_delta)): return
		if direction == "along":
			RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 0.0)
			var total := await read_image(vp, "totality")
			var total_delta := difference(total, off)
			print("RAIN_CURTAIN_ECLIPSE day=", day_delta, " total=", total_delta)
			if not require(total_delta.mean < day_delta.mean * .65, "total eclipse kept daytime rain brightness"): return
			RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
			sun.global_basis = Basis.looking_at(up, around)
			step(1)
			copy_source(source)
			var night := await read_image(vp, "night")
			if not require(difference(night, off).mean < day_delta.mean * .65, "geometric night kept daytime rain brightness"): return
			sun.global_basis = Basis.looking_at(-up, around)
			step(1)
			copy_source(source)
			# An opaque foreground quad must block the transparent curtain.
			var blocker := MeshInstance3D.new()
			var quad := QuadMesh.new(); quad.size = Vector2(30000, 30000)
			blocker.mesh = quad
			var mat := StandardMaterial3D.new(); mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED; mat.albedo_color = Color(.07, .08, .09)
			blocker.material_override = mat
			vp.add_child(blocker)
			blocker.global_transform = probe_camera.global_transform
			blocker.global_position -= probe_camera.global_basis.z * 500.0
			isolated.visible = false
			var blocked_off := await read_image(vp, "blocked_off")
			isolated.visible = true
			var blocked_on := await read_image(vp, "blocked_on")
			if not require(difference(blocked_on, blocked_off).mean < .00001, "rain drew through opaque geometry"): return
			blocker.queue_free()
		print("RAIN_CURTAIN_GPU_DIRECTION ", direction, " delta=", day_delta)
	# Physical-width clipping is independent of billboard orientation.
	var edge_material := isolated.material_override as ShaderMaterial
	isolated.visible = false
	var edge_off := await read_image(vp, "edge_off")
	isolated.visible = true
	for extent in [Vector2(-10000, 1000), Vector2(10000, 1000)]:
		edge_material.set_shader_parameter("ring_width", 1000.0)
		edge_material.set_shader_parameter("across_extent", extent)
		var edge_on := await read_image(vp, "edge_" + str(int(extent.x)))
		if not require(difference(edge_on, edge_off).mean < .00001, "rain leaked beyond physical ring width"): return
	# A full-scene check with existing terrain, sky, atmosphere and Sunshine
	# compositor retained. Freeze clocks/history; use OFF/OFF and OFF/ON/OFF.
	var far := world.get_node("ScaledFarViewport")
	if bool(far.get("Enabled")):
		var settled := false
		for i in range(480):
			await process_frame
			if not bool(far.get("FarWeatherAtlasRefreshInProgress")) and absf(float(far.get("FarWeatherAtlasSnapshotTimeSeconds")) - 300.0) < .001:
				settled = true; break
		if not require(settled, "far atlas did not publish the controlled weather epoch"): return
	for name in ["SunshineCloudBridge", "SunshineCloudsDriver", "DaySideAtmosphere", "ScaledFarViewport"]:
		var n := world.get_node_or_null(name)
		if n: n.set_process(false)
	var attributes := world.get_node("WorldEnvironment").get("camera_attributes") as CameraAttributesPractical
	if attributes: attributes.auto_exposure_enabled = false
	for layer: CanvasLayer in world.find_children("*", "CanvasLayer", true, false):
		if layer.layer > 0: layer.hide()
	Engine.time_scale = 0.0
	rain.hide()
	for i in range(100): await process_frame
	var before: Image
	var control: Image
	var baseline := INF
	for attempt in range(35):
		before = await read_image(world.get_viewport(), "main_off_a")
		control = await read_image(world.get_viewport(), "main_off_b")
		baseline = difference(before, control).mean
		if baseline < .00001: break
	if not require(baseline < .00001, "OFF/OFF main view did not settle: " + str(baseline)): return
	rain.show()
	var on := await read_image(world.get_viewport(), "main_on")
	rain.hide()
	var after := await read_image(world.get_viewport(), "main_off_after")
	var drift := maxf(difference(before, control).mean, difference(before, after).mean)
	var effect := minf(difference(on, before).mean, difference(on, after).mean)
	print("RAIN_CURTAIN_MAIN_COMPOSITE effect=", effect, " drift=", drift)
	if not require(effect > maxf(.00005, drift * 2.0), "actual main compositor had no stable rain contribution"): return
	if failed: return
	print("DISTANT_RAIN_CURTAINS_GPU=PASS preset=", label)
	quit(0)

func setup_probe() -> void:
	vp = SubViewport.new(); vp.size = Vector2i(320, 200); vp.own_world_3d = true
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(vp)
	var we := WorldEnvironment.new(); var env := Environment.new()
	env.background_mode = Environment.BG_COLOR; env.background_color = Color.BLACK
	env.tonemap_mode = Environment.TONE_MAPPER_LINEAR; we.environment = env; vp.add_child(we)
	probe_camera = Camera3D.new(); probe_camera.current = true; probe_camera.far = 30000
	vp.add_child(probe_camera)
	isolated = MeshInstance3D.new(); isolated.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	vp.add_child(isolated)

func source_mesh(target: Dictionary) -> MeshInstance3D:
	var nearest: MeshInstance3D
	var distance := INF
	var center := canonical(float(target.around_meters), (float(target.surface_meters) + 2000.0) * .5, float(target.across_meters))
	for node in rain.get_children():
		if node is MeshInstance3D and node.visible:
			var d: float = node.global_position.distance_to(center)
			if d < distance: distance = d; nearest = node
	return nearest if distance < 100.0 else null

func copy_source(source: MeshInstance3D) -> void:
	isolated.mesh = source.mesh
	isolated.material_override = source.material_override.duplicate()
	isolated.global_transform = source.global_transform
	probe_camera.global_transform = camera.global_transform
	probe_camera.fov = camera.fov

func canonical(a: float, y: float, z: float) -> Vector3:
	return grid.call("CanonicalRingPositionToWorld", Vector3(a, y, z))
func step(count: int) -> void:
	for i in range(count): rain.call("_Process", .1)
func read_image(view: Viewport, suffix: String) -> Image:
	for i in range(8): await RenderingServer.frame_post_draw
	var im := view.get_texture().get_image()
	if im == null or im.is_empty(): fail("empty image " + suffix); return Image.new()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	if im.save_png(OUT.path_join(label + "_" + suffix + ".png")) != OK: fail("capture save failed")
	return im
func difference(a: Image, b: Image) -> Dictionary:
	var count := 0; var total := 0.0; var n := 0
	for y in range(0, a.get_height(), 2):
		for x in range(0, a.get_width(), 2):
			var c := a.get_pixel(x,y).srgb_to_linear() - b.get_pixel(x,y).srgb_to_linear()
			var d := (absf(c.r) + absf(c.g) + absf(c.b)) / 3.0
			if not is_finite(d): fail("nonfinite capture"); return {"count": 0, "mean": 0.0}
			if d > .001: count += 1
			total += d; n += 1
	return {"count": count, "mean": total / max(1,n)}
func require(ok: bool, message: String) -> bool:
	if not ok: fail(message)
	return ok
func fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("DISTANT_RAIN_CURTAINS_GPU=FAIL " + message)
	quit(1)
