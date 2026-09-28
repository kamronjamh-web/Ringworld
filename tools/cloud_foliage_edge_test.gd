extends SceneTree

## GPU regression for Sunshine's resolved-depth mask with production alpha-cutout foliage.
## Run in a Forward+ display session; headless check-only only validates this fixture's syntax.
const CLOUD_PROFILE := preload("res://addons/SunshineClouds2/SunshineCloudsGDTestResource.tres")
const CLOUD_DRIVER := preload("res://addons/SunshineClouds2/SunshineCloudsDriver.gd")
const TREE_SHADER := preload("res://materials/imported_tree.gdshader")
const FAR_DEPTH_CAPTURE := preload("res://scripts/render/RingFarDepthCapture.gd")
const OUTPUT := "res://.godot/cloud-foliage-edge"
const BASE_SIZE := Vector2i(512, 288)

var _failed := false
var _viewport: SubViewport
var _world_environment: WorldEnvironment
var _clouds: SunshineCloudsGD
var _driver: SunshineCloudsDriverGD
var _foliage: MeshInstance3D
var _solid: MeshInstance3D
var _depth_capture: CompositorEffect
var _consumer: TextureRect
var _constant_dither: ImageTexture3D

func _init() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required; Dummy rendering is not GPU evidence")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	_build_world()
	if _failed: return
	var report := {
		"result": "pass",
		"static_temporal_history": "capture-only disabled",
		"deterministic_cloud_controls": {
			"atmospheric_density": 0.0,
			"dither_noise": "constant 1x1x1 rgba(0.5)",
			"ray_start_jitter_strength": 1.0,
			"production_shape_noise": true
		},
		"captures": [],
		"cases": []
	}
	for resolution_scale: int in [0, 1]:
		for blur_power: float in [0.0, 0.7]:
			var result := await _capture_case(resolution_scale, blur_power)
			if _failed: return
			report.cases.append(result)
			for capture_name: String in result.captures: report.captures.append(capture_name)
	var temporal := await _temporal_moving_card_case()
	if _failed: return
	report.temporal_moving_card = temporal
	for capture_name: String in temporal.captures: report.captures.append(capture_name)
	var file := FileAccess.open(OUTPUT.path_join("report.json"), FileAccess.WRITE)
	if file == null:
		_fail("could not write report.json")
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("CLOUD_FOLIAGE_EDGE_TEST=PASS output=%s cases=%d" % [OUTPUT, report.cases.size()])
	quit(0)

func _build_world() -> void:
	_viewport = SubViewport.new()
	_viewport.size = BASE_SIZE
	_viewport.own_world_3d = true
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var canvas := CanvasLayer.new()
	root.add_child(canvas)
	_consumer = TextureRect.new()
	_consumer.texture = _viewport.get_texture()
	_consumer.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_consumer.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	canvas.add_child(_consumer)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.08, 0.16, 0.28, 1.0)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.WHITE
	environment.ambient_light_energy = 0.85
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	_world_environment = WorldEnvironment.new()
	_world_environment.environment = environment
	_world_environment.compositor = Compositor.new()
	_depth_capture = FAR_DEPTH_CAPTURE.new() as CompositorEffect
	_depth_capture.set("coordinate_scale", 1.0)
	_world_environment.compositor.compositor_effects = [_depth_capture]
	_viewport.add_child(_world_environment)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = 50.0
	camera.position = Vector3(0.0, 0.0, 10.0)
	camera.current = true
	_viewport.add_child(camera)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	var sun := DirectionalLight3D.new()
	sun.light_energy = 0.7
	sun.shadow_enabled = false
	sun.rotation_degrees = Vector3(-35.0, -25.0, 0.0)
	_viewport.add_child(sun)
	_clouds = CLOUD_PROFILE.duplicate(true) as SunshineCloudsGD
	if _clouds == null:
		_fail("production SunshineCloudsGD resource unavailable")
		return
	_clouds.clouds_coverage = 1.0
	_clouds.clouds_density = 4.0
	_clouds.effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	# Prevent the cloud-signal check from passing on atmospheric post fog alone.
	_clouds.atmospheric_density = 0.0
	_clouds.cloud_floor = 500.0
	_clouds.cloud_ceiling = 5000.0
	_clouds.max_step_count = 128.0
	_clouds.dither_speed = 0.0
	# Sunshine advances its dither phase per compositor callback even with zero
	# speed. A constant volume keeps ray-start jitter deterministic while leaving
	# the production cloud shape/noise textures and jitter algorithm unchanged.
	_constant_dither = _constant_dither_texture()
	_clouds.dither_noise = _constant_dither
	_clouds.current_time = 0.375
	_clouds.accumulation_decay = 0.0
	# Capture-only deterministic sampling; gameplay leaves temporal history enabled.
	_clouds.diagnostic_disable_temporal_history = true
	_clouds.menu_fast_reconstruction = false
	_clouds.cloud_frame_origin = Vector3(0.0, 0.0, 10.0)
	_clouds.cloud_frame_around = Vector4(1.0, 0.0, 0.0, 0.0)
	_clouds.cloud_frame_up = Vector4(0.0, 0.0, -1.0, 5000000.0)
	_clouds.cloud_frame_across = Vector4(0.0, 1.0, 0.0, 0.0)
	_driver = CLOUD_DRIVER.new() as SunshineCloudsDriverGD
	_driver.update_continuously = false
	_driver.clouds_resource = _clouds
	_driver.tracked_directional_lights = [sun]
	_viewport.add_child(_driver)
	_driver.retrieve_texture_data()
	# The resource setter ran before the driver entered the tree; attach the
	# effect explicitly to the exact world whose foliage depth is being tested.
	_world_environment.compositor.compositor_effects = [_depth_capture, _clouds]
	_foliage = _card("AlphaCutoutFoliage", _leaf_texture(), true)
	_solid = _card("SolidFoliageControl", _solid_texture(), false)
	_viewport.add_child(_foliage)
	_viewport.add_child(_solid)
	_foliage.position.x = -2.4
	_solid.position.x = 2.4
	_foliage.visible = false
	_solid.visible = false

func _card(card_name: String, albedo: ImageTexture, alpha_cutout: bool) -> MeshInstance3D:
	var mesh := QuadMesh.new()
	mesh.size = Vector2(4.4, 4.4)
	var material := ShaderMaterial.new()
	material.shader = TREE_SHADER
	material.set_shader_parameter("albedo_texture", albedo)
	material.set_shader_parameter("normal_texture", _flat_texture(Color(0.5, 0.5, 1.0, 1.0)))
	material.set_shader_parameter("metallic_texture", _flat_texture(Color.BLACK))
	material.set_shader_parameter("roughness_texture", _flat_texture(Color.WHITE))
	material.set_shader_parameter("has_normal_texture", true)
	material.set_shader_parameter("has_metallic_texture", false)
	material.set_shader_parameter("has_roughness_texture", false)
	material.set_shader_parameter("use_alpha_cutout", alpha_cutout)
	material.set_shader_parameter("alpha_cutoff", 0.5)
	material.set_shader_parameter("vegetation_wind_enabled", false)
	material.set_shader_parameter("ring_center", Vector3(0.0, 0.0, 5000.0))
	material.set_shader_parameter("sun_direction", Vector3(-1.0, 0.0, 0.0))
	material.set_shader_parameter("night_tint", Vector3.ONE)
	material.set_shader_parameter("day_direct_light_factor", 1.0)
	material.set_shader_parameter("local_cloud_shadow_enabled", false)
	material.set_shader_parameter("local_cloud_shadow_near_enabled", false)
	material.set_shader_parameter("local_cloud_shadow_far_enabled", false)
	mesh.material = material
	var card := MeshInstance3D.new()
	card.name = card_name
	card.mesh = mesh
	card.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	return card

func _capture_case(resolution_scale: int, blur_power: float) -> Dictionary:
	_clouds.resolution_scale = resolution_scale
	_clouds.blur_power = blur_power
	_clouds.current_time = 0.375
	_clouds.refresh_compute()
	var label := "full" if resolution_scale == 0 else "half"
	label += "_blur0" if is_zero_approx(blur_power) else "_blur_profile07"
	_foliage.visible = false
	_solid.visible = false
	_clouds.enabled = false
	var empty_no_cloud := await _capture(label + "_empty_no_cloud")
	_clouds.enabled = true
	var cloud_only := await _capture(label + "_cloud_only")
	if _failed: return {}
	var cloud_signal := _mean_delta(cloud_only, empty_no_cloud)
	if cloud_signal < 0.01:
		_fail("cloud signal absent for " + label)
		return {}
	_clouds.enabled = false
	_foliage.visible = true
	_solid.visible = true
	var no_cloud_cards := await _capture(label + "_no_cloud_cards")
	_clouds.enabled = true
	var with_cards := await _capture(label + "_cloud_cards")
	if _failed: return {}
	var depth := _depth_image()
	if _failed: return {}
	var metrics := _depth_mask_metrics(depth, no_cloud_cards, with_cards, cloud_only, empty_no_cloud)
	if int(metrics.foliage_interior_count) == 0 or int(metrics.solid_interior_count) == 0 or int(metrics.alpha_hole_count) == 0 or int(metrics.edge_count) == 0:
		_fail("depth masks empty for " + label + " " + JSON.stringify(metrics))
		return {}
	if float(metrics.alpha_hole_cloud_signal) < 0.01 or float(metrics.foliage_edge_cloud_signal) < 0.01:
		_fail("cloud signal absent at foliage masks %s hole=%.5f edge=%.5f" % [label, metrics.alpha_hole_cloud_signal, metrics.foliage_edge_cloud_signal])
		return {}
	if float(metrics.opaque_foliage_delta) > 0.035 or float(metrics.opaque_solid_delta) > 0.035:
		_fail("cloud altered opaque near card pixels %s foliage=%.5f solid=%.5f" % [label, metrics.opaque_foliage_delta, metrics.opaque_solid_delta])
		return {}
	if float(metrics.alpha_hole_cloud_delta) > 0.045:
		_fail("alpha holes did not preserve cloud background %s delta=%.5f" % [label, metrics.alpha_hole_cloud_delta])
		return {}
	metrics.resolution = label
	metrics.blur_power = blur_power
	metrics.cloud_signal = cloud_signal
	metrics.captures = [label + "_empty_no_cloud.png", label + "_cloud_only.png", label + "_no_cloud_cards.png", label + "_cloud_cards.png"]
	return metrics

func _temporal_moving_card_case() -> Dictionary:
	# Each movement sequence uses one post-draw wait per source frame. The
	# history-disabled control distinguishes temporal accumulation from ordinary
	# compositor/readback latency without changing geometry or cloud inputs.
	_clouds.enabled = false
	_clouds.resolution_scale = 0
	_clouds.blur_power = 0.7
	_clouds.current_time = 0.375
	_clouds.dither_speed = 0.0
	_clouds.diagnostic_disable_temporal_history = true
	_clouds.refresh_compute()
	_foliage.visible = false
	_solid.visible = false
	# A six-draw stabilized, history-disabled cloud reference avoids sampling
	# the resource immediately after its compute rebuild.
	var empty_reference := await _capture("temporal_empty_no_cloud_reference")
	_clouds.enabled = true
	var reference := await _capture("temporal_cloud_only_reference")
	if _failed: return {}
	var captures: Array[String] = ["temporal_empty_no_cloud_reference.png", "temporal_cloud_only_reference.png"]
	var controls: Array[Dictionary] = []
	for control: Dictionary in [
		{"name": "history95", "history_disabled": false},
		{"name": "history_disabled", "history_disabled": true}
	]:
		var result := await _temporal_movement_control(str(control.name), bool(control.history_disabled), reference, empty_reference)
		if _failed: return {}
		controls.append(result)
		for capture_name: String in result.captures:
			captures.append(capture_name)
	return {
		"description": "matched production temporal-history and history-disabled moving alpha-cutout card controls",
		"history_weight": 0.95,
		"warm_card_source_frames": 36,
		"translation_world_units": 0.12,
		"reference": "six-draw history-disabled cloud-only capture with fixed dither/time/drift",
		"controls": controls,
		"captures": captures
	}

func _temporal_movement_control(mode: String, history_disabled: bool, reference: Image, empty_reference: Image) -> Dictionary:
	_clouds.accumulation_decay = 0.95
	_clouds.diagnostic_disable_temporal_history = history_disabled
	# Rebuild/reset history before every equal 36-frame real-card warmup.
	_clouds.refresh_compute()
	_foliage.visible = true
	_solid.visible = false
	_foliage.position.x = -2.4
	for frame: int in range(36):
		await RenderingServer.frame_post_draw
	var depth_before := _depth_image()
	if _failed: return {}
	_foliage.position.x += 0.12
	var captures: Array[String] = []
	var observations: Array[Dictionary] = []
	var previous_source_frame := 0
	for source_frame: int in [1, 2, 8, 32]:
		for frame: int in range(source_frame - previous_source_frame):
			await RenderingServer.frame_post_draw
		previous_source_frame = source_frame
		var label := "temporal_%s_moving_card_frame%02d" % [mode, source_frame]
		var image := _save_current_capture(label)
		if _failed: return {}
		var depth_after := _depth_image()
		if _failed: return {}
		var uncovered := _newly_uncovered_mask(depth_before, depth_after)
		if int(uncovered.count) == 0:
			_fail("newly uncovered depth mask empty for temporal " + mode)
			return {}
		var delta := _masked_delta(image, reference, uncovered.mask)
		var cloud_signal := _masked_delta(reference, empty_reference, uncovered.mask)
		if cloud_signal < 0.01:
			_fail("cloud signal absent at newly uncovered pixels for temporal " + mode)
			return {}
		observations.append({"source_frame_count": source_frame, "newly_uncovered_sky_hole_pixels": uncovered.count, "cloud_reference_signal": cloud_signal, "cloud_reference_rgb_delta": delta})
		captures.append(label + ".png")
	_foliage.position.x = -2.4
	_foliage.visible = false
	return {"mode": mode, "history_disabled": history_disabled, "observations": observations, "captures": captures}

func _capture(label: String) -> Image:
	for frame: int in range(6): await RenderingServer.frame_post_draw
	return _save_current_capture(label)

func _capture_raw(label: String) -> Image:
	await RenderingServer.frame_post_draw
	return _save_current_capture(label)

func _save_current_capture(label: String) -> Image:
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty capture " + label)
		return Image.new()
	if image.save_png(OUTPUT.path_join(label + ".png")) != OK:
		_fail("could not save capture " + label)
	return image

func _newly_uncovered_mask(before: Image, after: Image) -> Dictionary:
	var mask := PackedByteArray()
	mask.resize(before.get_width() * before.get_height())
	var count := 0
	for y: int in range(before.get_height()):
		for x: int in range(before.get_width()):
			if _opaque_depth(before, x, y) and not _opaque_depth(after, x, y):
				mask[y * before.get_width() + x] = 1
				count += 1
	return {"mask": mask, "count": count}

func _masked_delta(image: Image, reference: Image, mask: PackedByteArray) -> float:
	var total := 0.0
	var count := 0
	for y: int in range(image.get_height()):
		for x: int in range(image.get_width()):
			if mask[y * image.get_width() + x] != 0:
				total += _color_delta(image.get_pixel(x, y), reference.get_pixel(x, y))
				count += 1
	return total / maxf(1.0, float(count))

func _depth_image() -> Image:
	var texture := _depth_capture.call("get_depth_texture") as Texture2D
	if texture == null:
		_fail("main opaque depth capture was not published")
		return Image.new()
	var image := texture.get_image()
	if image == null or image.is_empty():
		_fail("main opaque depth capture was empty")
	return image

func _depth_mask_metrics(depth: Image, no_cloud_cards: Image, cards: Image, cloud_only: Image, empty_no_cloud: Image) -> Dictionary:
	var foliage_total := 0.0
	var solid_total := 0.0
	var hole_total := 0.0
	var edge_total := 0.0
	var hole_signal_total := 0.0
	var edge_signal_total := 0.0
	var foliage_count := 0
	var solid_count := 0
	var hole_count := 0
	var edge_count := 0
	var minimum_depth := INF
	var maximum_depth := 0.0
	var enclosed_clear := _enclosed_clear_mask(depth)
	for y: int in range(1, depth.get_height() - 1):
		for x: int in range(1, depth.get_width() - 1):
			var value := depth.get_pixel(x, y).r
			var opaque := value >= 8.0 and value <= 12.0
			var neighbors_opaque := _opaque_depth(depth, x - 1, y) and _opaque_depth(depth, x + 1, y) and _opaque_depth(depth, x, y - 1) and _opaque_depth(depth, x, y + 1)
			if opaque:
				minimum_depth = minf(minimum_depth, value)
				maximum_depth = maxf(maximum_depth, value)
				if neighbors_opaque:
					var albedo := no_cloud_cards.get_pixel(x, y)
					if albedo.g > albedo.r:
						foliage_total += _color_delta(cards.get_pixel(x, y), no_cloud_cards.get_pixel(x, y))
						foliage_count += 1
					elif albedo.r > albedo.g:
						solid_total += _color_delta(cards.get_pixel(x, y), no_cloud_cards.get_pixel(x, y))
						solid_count += 1
			else:
				var foliage_neighbor := _foliage_neighbor(depth, no_cloud_cards, x, y)
				var index := y * depth.get_width() + x
				if enclosed_clear[index] != 0:
					hole_total += _color_delta(cards.get_pixel(x, y), cloud_only.get_pixel(x, y))
					hole_signal_total += _color_delta(cloud_only.get_pixel(x, y), empty_no_cloud.get_pixel(x, y))
					hole_count += 1
				if foliage_neighbor:
					edge_total += _color_delta(cards.get_pixel(x, y), cloud_only.get_pixel(x, y))
					edge_signal_total += _color_delta(cloud_only.get_pixel(x, y), empty_no_cloud.get_pixel(x, y))
					edge_count += 1
	if minimum_depth == INF:
		minimum_depth = 0.0
	return {
		"foliage_interior_count": foliage_count,
		"solid_interior_count": solid_count,
		"alpha_hole_count": hole_count,
		"edge_count": edge_count,
		"opaque_depth_min_meters": minimum_depth,
		"opaque_depth_max_meters": maximum_depth,
		"opaque_foliage_delta": foliage_total / maxf(1.0, float(foliage_count)),
		"opaque_solid_delta": solid_total / maxf(1.0, float(solid_count)),
		"alpha_hole_cloud_delta": hole_total / maxf(1.0, float(hole_count)),
		"one_pixel_outside_edge_delta": edge_total / maxf(1.0, float(edge_count)),
		"alpha_hole_cloud_signal": hole_signal_total / maxf(1.0, float(hole_count)),
		"foliage_edge_cloud_signal": edge_signal_total / maxf(1.0, float(edge_count))
	}

func _opaque_depth(depth: Image, x: int, y: int) -> bool:
	var value := depth.get_pixel(x, y).r
	return value >= 8.0 and value <= 12.0

func _enclosed_clear_mask(depth: Image) -> PackedByteArray:
	var width := depth.get_width()
	var height := depth.get_height()
	var exterior := PackedByteArray()
	exterior.resize(width * height)
	var pending: Array[Vector2i] = []
	for x: int in range(width):
		for y: int in [0, height - 1]:
			if not _opaque_depth(depth, x, y):
				pending.append(Vector2i(x, y))
	for y: int in range(1, height - 1):
		for x: int in [0, width - 1]:
			if not _opaque_depth(depth, x, y):
				pending.append(Vector2i(x, y))
	while not pending.is_empty():
		var point: Vector2i = pending.pop_back() as Vector2i
		var index: int = point.y * width + point.x
		if exterior[index] != 0 or _opaque_depth(depth, point.x, point.y):
			continue
		exterior[index] = 1
		for offset: Vector2i in [Vector2i.LEFT, Vector2i.RIGHT, Vector2i.UP, Vector2i.DOWN]:
			var next: Vector2i = point + offset
			if next.x >= 0 and next.x < width and next.y >= 0 and next.y < height:
				pending.append(next)
	var enclosed := PackedByteArray()
	enclosed.resize(width * height)
	for y: int in range(height):
		for x: int in range(width):
			var index: int = y * width + x
			if not _opaque_depth(depth, x, y) and exterior[index] == 0:
				enclosed[index] = 1
	return enclosed

func _foliage_neighbor(depth: Image, no_cloud_cards: Image, x: int, y: int) -> bool:
	for offset: Vector2i in [Vector2i.LEFT, Vector2i.RIGHT, Vector2i.UP, Vector2i.DOWN]:
		var sample_x := x + offset.x
		var sample_y := y + offset.y
		if _opaque_depth(depth, sample_x, sample_y):
			var albedo := no_cloud_cards.get_pixel(sample_x, sample_y)
			if albedo.g > albedo.r:
				return true
	return false

func _leaf_texture() -> ImageTexture:
	var image := Image.create(64, 64, false, Image.FORMAT_RGBA8)
	image.fill(Color(0.13, 0.72, 0.20, 0.0))
	for y: int in range(64):
		for x: int in range(64):
			var dx := float(x - 31) / 29.0
			var dy := float(y - 31) / 29.0
			var center_hole := x >= 27 and x < 37 and y >= 27 and y < 37
			var hole_ring := x >= 20 and x < 44 and y >= 20 and y < 44
			var leaf := dx * dx + dy * dy < 0.88 and ((x + y) % 7 > 1 or hole_ring)
			if leaf and not center_hole:
				image.set_pixel(x, y, Color(0.12 + float(x) / 420.0, 0.62 + float(y) / 520.0, 0.18, 1.0))
	return ImageTexture.create_from_image(image)

func _constant_dither_texture() -> ImageTexture3D:
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.fill(Color(0.5, 0.5, 0.5, 0.5))
	var texture := ImageTexture3D.new()
	if texture.create(Image.FORMAT_RGBA8, 1, 1, 1, false, [image]) != OK:
		_fail("could not create constant dither volume")
	return texture

func _solid_texture() -> ImageTexture: return _flat_texture(Color(0.76, 0.26, 0.10, 1.0))
func _flat_texture(color: Color) -> ImageTexture:
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, color)
	return ImageTexture.create_from_image(image)
func _mean_delta(a: Image, b: Image) -> float: return _mean_delta_rect(a, b, Rect2i(Vector2i.ZERO, a.get_size()))
func _mean_delta_rect(a: Image, b: Image, rect: Rect2i) -> float:
	var total := 0.0
	var count := 0
	for y: int in range(rect.position.y, rect.end.y):
		for x: int in range(rect.position.x, rect.end.x):
			total += _color_delta(a.get_pixel(x, y), b.get_pixel(x, y))
			count += 1
	return total / maxf(1.0, float(count))
func _color_delta(a: Color, b: Color) -> float:
	return Vector3(a.r, a.g, a.b).distance_to(Vector3(b.r, b.g, b.b))
func _fail(message: String) -> void:
	if _failed: return
	_failed = true
	var file := FileAccess.open(OUTPUT.path_join("report.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"result": "fail", "message": message}, "\t"))
		file.close()
	push_error("CLOUD_FOLIAGE_EDGE_TEST=FAIL " + message)
	quit(1)
