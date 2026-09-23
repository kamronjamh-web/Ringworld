extends SceneTree
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const MAX_WAIT := 1500
var world: Node
var weather: Node
var far: Node
var shell: ShaderMaterial

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: _fail("requires RenderingDevice"); return
	call_deferred("_run")

func _run() -> void:
	world = WORLD.instantiate()
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher)
	if not launcher.is_node_ready(): await launcher.ready
	launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", world, 5000000.0); launcher.free()
	root.add_child(world); weather = world.get_node("RingVolumetricClouds"); far = world.get_node("ScaledFarViewport")
	if not bool(far.get("Enabled")) or not bool(weather.get("SunshineCloudsPrimaryRenderer")): _fail("Certified far/Sunshine setup missing"); return
	for _i in range(8): await process_frame
	var far_world := far.get_node_or_null("FarRingViewport")
	var mesh := far_world.get_node_or_null("ScaledCloudShell") as MeshInstance3D if far_world else null
	if mesh == null or not (mesh.material_override is ShaderMaterial): _fail("far shell unavailable"); return
	shell = mesh.material_override as ShaderMaterial
	weather.set_process(false)
	var explorer := world.get_node_or_null("Explorer")
	if explorer: explorer.set_physics_process(false)
	var clock := world.get_node_or_null("RingDayNightCycle")
	if clock: clock.set("Paused", true)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/weather_phase1_visual"))
	var first_atlas: Image = await _capture_epoch(300.0)
	var second_atlas: Image = await _capture_epoch(1500.0)
	if first_atlas == null or second_atlas == null or not _images_differ(first_atlas, second_atlas): _fail("weather atlas did not change between epochs"); return
	print("WEATHER_LIFECYCLE_VISUAL=PASS epochs=300,1500")
	quit(0)

func _capture_epoch(epoch: float) -> Image:
	weather.set("CloudSimulationTimeSeconds", epoch)
	for frame in range(MAX_WAIT):
		await process_frame
		if absf(float(far.get("FarWeatherAtlasSnapshotTimeSeconds")) - epoch) < 0.000001 and not bool(far.get("FarWeatherAtlasRefreshInProgress")):
			await RenderingServer.frame_post_draw
			var atlas := shell.get_shader_parameter("weather_atlas") as Texture2D
			var far_view := far.get_node_or_null("FarRingViewport") as SubViewport
			var far_image := far_view.get_texture().get_image() if far_view else null
			var main_image := world.get_viewport().get_texture().get_image()
			if atlas == null or far_image == null or far_image.is_empty() or main_image == null or main_image.is_empty():
				_fail("empty capture epoch %.0f" % epoch)
				return null
			if _mean_luma(far_image) <= 0.00001 or not _has_variation(far_image):
				_fail("black/flat far capture epoch %.0f" % epoch)
				return null
			var atlas_image := atlas.get_image()
			if atlas_image == null or atlas_image.is_empty():
				_fail("empty atlas epoch %.0f" % epoch)
				return null
			if main_image.save_png("res://.godot/weather_phase1_visual/main_%d.png" % int(epoch)) != OK \
					or far_image.save_png("res://.godot/weather_phase1_visual/far_%d.png" % int(epoch)) != OK \
					or atlas_image.save_png("res://.godot/weather_phase1_visual/atlas_%d.png" % int(epoch)) != OK:
				_fail("failed to save epoch %.0f capture" % epoch)
				return null
			return atlas_image.duplicate()
	_fail("atlas timeout epoch=%.0f snapshot=%.6f pending=%s" % [epoch, float(far.get("FarWeatherAtlasSnapshotTimeSeconds")), bool(far.get("FarWeatherAtlasRefreshInProgress"))]); return null

func _mean_luma(image: Image) -> float:
	var total := 0.0; var count := 0
	for y in range(0, image.get_height(), max(1, image.get_height()/16)):
		for x in range(0, image.get_width(), max(1, image.get_width()/16)): total += image.get_pixel(x,y).get_luminance(); count += 1
	return total / max(1,count)
func _has_variation(image: Image) -> bool:
	var minimum := INF
	var maximum := -INF
	for y in range(0, image.get_height(), max(1, image.get_height() / 16)):
		for x in range(0, image.get_width(), max(1, image.get_width() / 16)):
			var luma := image.get_pixel(x, y).get_luminance()
			if not is_finite(luma):
				return false
			minimum = minf(minimum, luma)
			maximum = maxf(maximum, luma)
	return maximum - minimum > 0.00001
func _images_differ(a: Image,b: Image) -> bool:
	if a.get_size()!=b.get_size(): return false
	for y in range(0,a.get_height(),max(1,a.get_height()/8)):
		for x in range(0,a.get_width(),max(1,a.get_width()/16)):
			var left := a.get_pixel(x, y)
			var right := b.get_pixel(x, y)
			if absf(left.r - right.r) + absf(left.g - right.g) > 0.002:
				return true
	return false
func _fail(message:String)->void: push_error("WEATHER_LIFECYCLE_VISUAL=FAIL %s"%message);quit(1)
