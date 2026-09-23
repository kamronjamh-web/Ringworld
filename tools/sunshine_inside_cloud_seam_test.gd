extends SceneTree

# Certified-world cloud capture. --cloud-seam-fixed also enforces a dense-cloud
# horizontal-ray regression at the two in-band observer heights. Canvas mask
# ON/OFF captures are diagnostic only (streaming can change terrain between them).
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
var OUTPUT := "res://.godot/cloud_seam_probe_fixed" if "--cloud-seam-fixed" in OS.get_cmdline_user_args() else "res://.godot/cloud_seam_probe_baseline"

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; this diagnostic cannot run headless")
		return
	call_deferred("_run")

func _run() -> void:
	if "--cloud-seam-1440p" in OS.get_cmdline_user_args():
		OUTPUT += "_1440p"
		# Windows can clamp a hidden window to the monitor's work area. Render
		# an explicit 1440p viewport and validate the actual captured texture.
		root.content_scale_mode = Window.CONTENT_SCALE_MODE_VIEWPORT
		root.content_scale_size = Vector2i(2560, 1440)
	var world := WORLD.instantiate() as Node3D
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	if not launcher.is_node_ready():
		await launcher.ready
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", world, 5000000.0)
	launcher.free()
	root.add_child(world)
	for frame in 30:
		await process_frame
	var bridge := world.get_node("SunshineCloudBridge")
	var grid := world.get_node("ChunkGridManager")
	var explorer := world.get_node("Explorer")
	var camera := world.get_node("Explorer/Head/Camera3D") as Camera3D
	var clock := world.get_node("RingDayNightCycle")
	var weather := world.get_node("RingVolumetricClouds")
	var far := world.get_node("ScaledFarViewport")
	var driver := world.get_node("SunshineCloudsDriver")
	var clouds = driver.get("clouds_resource")
	var monitor := world.get_node_or_null("CertificationMonitor")
	if monitor != null:
		monitor.set("EnableAdaptiveProtection", false)
		monitor.set_process(false)
	world.get_viewport().scaling_3d_scale = 1.0
	bridge.call("SetBenchmarkViewerPosition", 872300.0, 2700.0, 155000.0)
	explorer.set_meta("ring_camera_underwater", false)
	explorer.set_meta("ring_water_depth_meters", 0.0)
	clock.set("Paused", true)
	clock.call("set_simulation_seconds", 0.0)
	weather.set("CloudSimulationTimeSeconds", 100.0)
	for frame in 30:
		await process_frame
	explorer.set_process(false)
	explorer.set_physics_process(false)
	bridge.set_process(false)
	driver.set_process(false)
	var environment := world.get_node("WorldEnvironment") as WorldEnvironment
	if environment.camera_attributes is CameraAttributesPractical:
		(environment.camera_attributes as CameraAttributesPractical).auto_exposure_enabled = false
	var atmosphere := world.get_node_or_null("DaySideAtmosphere")
	if atmosphere != null:
		atmosphere.call("_process", 10.0)
		if float(atmosphere.get("current_underwater_factor")) > 0.0001:
			_fail("fixture water metadata override did not clear current_underwater_factor")
			return
		atmosphere.set_process(false)
	clouds.set("accumulation_decay", 0.95)
	weather.set_process(false)
	weather.set("CloudSimulationTimeSeconds", 100.0)
	Engine.time_scale = 0.0
	for layer in world.find_children("*", "CanvasLayer", true, false):
		if (layer as CanvasLayer).layer > 0:
			(layer as CanvasLayer).hide()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	for height in [2700.0, 4000.0, 6500.0]:
		bridge.call("SetBenchmarkViewerPosition", 872300.0, height, 155000.0)
		clouds.set("accumulation_decay", 0.95)
		var target := grid.call("CanonicalRingPositionToWorld", Vector3(873300.0, height, 155000.0)) as Vector3
		camera.look_at(target, camera.global_basis.y)
		for frame in 20:
			await process_frame
		far.call("_Process", 0.0)
		far.set_process(false)
		var material = far.get_node("FarRingBackdrop/FarRingTexture").material
		material.set_shader_parameter("cloud_mask_enabled", true)
		for frame in 20: await process_frame
		var mask_on := await _capture(world.get_viewport(), "main_%d_mask_on" % int(height))
		material.set_shader_parameter("cloud_mask_enabled", false)
		for frame in 20: await process_frame
		var mask_off := await _capture(world.get_viewport(), "main_%d_mask_off" % int(height))
		for frame in 20: await process_frame
		var mask_off_control := await _capture(world.get_viewport(), "main_%d_mask_off_control" % int(height))
		material.set_shader_parameter("cloud_mask_enabled", true)
		if clouds == null or int(clouds.get("resolution_scale")) != 0:
			_fail("missing native-resolution cloud resource")
			return
		clouds.set("diagnostic_raw_density_matte", true)
		for frame in 20:
			await process_frame
		var raw_image: Image = await _capture(world.get_viewport(), "raw_%d" % int(height))
		clouds.set("diagnostic_raw_density_matte", false)
		if raw_image == null:
			_fail("raw cloud image unreadable")
			return
		if "--cloud-seam-1440p" in OS.get_cmdline_user_args() \
				and (raw_image.get_size() != Vector2i(2560, 1440) \
				or clouds.get("last_size") != Vector2i(2560, 1440)):
			_fail("1440p fixture did not render native 2560x1440 clouds")
			return
		raw_image.save_png(OUTPUT + "/raw_%d.png" % int(height))
		var stats := _stats(raw_image)
		if not stats.finite or stats.positive <= 0.0:
			_fail("raw cloud alpha is invalid or empty")
			return
		if height < 6000.0:
			var horizon := _horizon_density(raw_image)
			print("CLOUD_HORIZON height=%.0f dense_flanks=%.6f weakest_row=%.6f deficit=%.6f" % [height, horizon.flank, horizon.minimum, horizon.deficit])
			if "--cloud-seam-fixed" in OS.get_cmdline_user_args() \
					and (horizon.flank < 0.8 or horizon.deficit > 0.015):
				_fail("dense inside-cloud horizon lost opacity or lacks a dense positive control")
				return
		if not _is_grayscale(raw_image):
			_fail("raw density matte is not grayscale within 2/255")
			return
		var mask_difference := _image_difference(mask_on, mask_off)
		var control_difference := _image_difference(mask_off, mask_off_control)
		var log := "height=%f render=%s raw_density_min=%f mean=%f max=%f positive_fraction=%f mask_on_off_mean=%f max=%f off_control_mean=%f max=%f water_metadata_override=true mask_ab=diagnostic_only\n" % [height, raw_image.get_size(), stats.minimum, stats.mean, stats.maximum, stats.positive, mask_difference.mean, mask_difference.maximum, control_difference.mean, control_difference.maximum]
		FileAccess.open(OUTPUT + "/pose_%d.log" % int(height), FileAccess.WRITE).store_string(log)
		print(log)
		print("CLOUD_SEAM_STATE camera=%s water_metadata_override=true actual_scale=%s mask_mode=%s" % [camera.global_position, world.get_viewport().scaling_3d_scale, far.get("ExactSunshineBackdropActive")])
	Engine.time_scale = 1.0
	if "--cloud-seam-fixed" in OS.get_cmdline_user_args():
		print("Sunshine inside-cloud horizontal-ray GPU regression PASS")
	quit()

func _capture(viewport: Viewport, name: String) -> Image:
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	image.save_png(OUTPUT + "/" + name + ".png")
	return image

func _stats(image: Image) -> Dictionary:
	var minimum := INF
	var maximum := -INF
	var total := 0.0
	var count := image.get_width() * image.get_height()
	var positive := 0
	for y in image.get_height():
		for x in image.get_width():
			var value := image.get_pixel(x, y).r
			minimum = minf(minimum, value)
			maximum = maxf(maximum, value)
			total += value
			if value > 0.001: positive += 1
	return {"finite": is_finite(total), "minimum": minimum, "mean": total / max(1, count), "maximum": maximum, "positive": float(positive) / max(1, count)}

func _image_difference(left: Image, right: Image) -> Dictionary:
	var total := 0.0
	var maximum := 0.0
	var count: int = mini(left.get_width(), right.get_width()) * mini(left.get_height(), right.get_height())
	for y in min(left.get_height(), right.get_height()):
		for x in min(left.get_width(), right.get_width()):
			var delta := absf(left.get_pixel(x, y).get_luminance() - right.get_pixel(x, y).get_luminance())
			total += delta
			maximum = maxf(maximum, delta)
	return {"mean": total / max(1, count), "maximum": maximum}

func _horizon_density(image: Image) -> Dictionary:
	# Fixed pose looks along the ring tangent. The right-hand cloud body is dense
	# above and below the geometric horizon; an isolated dark row here is invalid.
	var x_begin := int(image.get_width() * 0.625)
	var x_end := int(image.get_width() * 0.9375)
	var center := int(image.get_height() * 0.5)
	var half_band := maxi(8, int(image.get_height() / 90.0))
	var rows: Array[float] = []
	for y in range(center - half_band, center + half_band + 1):
		var total := 0.0
		for x in range(x_begin, x_end):
			total += image.get_pixel(x, y).r
		rows.append(total / (x_end - x_begin))
	var flank := (rows[0] + rows[1] + rows[-2] + rows[-1]) * 0.25
	var minimum := flank
	for value in rows:
		minimum = minf(minimum, value)
	return {"flank": flank, "minimum": minimum, "deficit": flank - minimum}

func _is_grayscale(image: Image) -> bool:
	for y in image.get_height():
		for x in image.get_width():
			var color := image.get_pixel(x, y)
			if absf(color.r - color.g) > 2.0 / 255.0 or absf(color.r - color.b) > 2.0 / 255.0:
				return false
	return true

func _fail(message: String) -> void:
	push_error(message)
	quit(1)
