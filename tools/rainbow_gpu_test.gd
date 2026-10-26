extends SceneTree
## S3 WO-F1 rainbow GPU test (docs/storm-revamp-s3-storm-arrival-design.md sections 6 and 12). A synthetic forced rain mask (all five azimuth strengths 1) is
## published straight to RingRainbowComposite.gd in a small sky scene with a near wall (10 m, must stay clean) and a far wall (300 m, must show the bow).
## Checks: (1) the bow's luminance-weighted angular radius on screen is 42 +/- 1 deg with red outside violet; (2) the secondary ring exists at ~0.43x;
## (3) Rainbows 0 / an inactive payload renders pixel-identical to no effect (SSIM 1.000); (4) no bow on opaque pixels nearer than 30 m; (5) the rain streak and rain
## haze compositors stay after the rainbow in the effect list, including after the rain owner removes and re-adds them (the EnsureRainHazeCompositorLast move).
## Captures A (no effect), B (inactive), C (active) go to .godot/captures/rainbow/ for ffmpeg_tools compare / sheet.
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   godot --path . --script res://tools/rainbow_gpu_test.gd [-- --sun=20 --azimuth=30]
## Prints RAINBOW_GPU=PASS.
const HELPER := preload("res://tools/RingRainbowGpuHelper.cs")
const COMPOSITE := "res://scripts/render/RingRainbowComposite.gd"
const OUT_DIR := "res://.godot/captures/rainbow/"
var _env: WorldEnvironment
var _camera: Camera3D
var _effect: CompositorEffect
var _bow: Dictionary
var _failures: Array[String] = []
var _images := {}

func _initialize() -> void:
	call_deferred("_run")

func _arg(name: String, fallback: float) -> float:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--" + name + "="): return float(a.get_slice("=", 1))
	return fallback

func _check(ok: bool, message: String) -> void:
	if not ok: _failures.append(message)

func _finish() -> void:
	if _failures.is_empty():
		print("RAINBOW_GPU=PASS")
		quit(0)
	else:
		push_error("RAINBOW_GPU=FAIL " + " | ".join(_failures))
		quit(1)

func _run() -> void:
	create_timer(300.0).timeout.connect(func(): push_error("RAINBOW_GPU=FAIL timeout"); quit(1))
	if RenderingServer.get_rendering_device() == null:
		push_error("RAINBOW_GPU=FAIL requires Vulkan/Forward+")
		quit(1)
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	var sun_elevation := _arg("sun", 20.0)
	var sun_azimuth := _arg("azimuth", 30.0)
	root.size = Vector2i(1280, 720)
	get_root().content_scale_size = Vector2i(0, 0)
	# Sky scene.
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.22, 0.30, 0.42)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(1, 1, 1)
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	_env = WorldEnvironment.new(); _env.environment = environment
	root.add_child(_env)
	_env.compositor = Compositor.new()
	_bow = HELPER.new().Make(sun_elevation, sun_azimuth, 1.0, 1.0, 0.8)
	_check(bool(_bow["active"]), "bow inactive for sun elevation %.1f" % sun_elevation)
	var anti: Vector3 = _bow["anti"]
	_camera = Camera3D.new(); _camera.fov = 100.0; _camera.near = 0.1; _camera.far = 5000.0
	root.add_child(_camera)
	var forward := Vector3(anti.x, 0.0, anti.z).normalized()
	_camera.look_at_from_position(Vector3.ZERO, forward, Vector3.UP)
	var right := forward.cross(Vector3.UP).normalized()
	# Near wall (10 m) covering the right part of the view; far wall (300 m) covering the left part.
	_add_wall(forward * 10.0 + right * 8.0, forward, Vector2(14, 40), Color(0.15, 0.15, 0.15))
	_add_wall(forward * 300.0 - right * 330.0, forward, Vector2(600, 600), Color(0.10, 0.10, 0.12))
	_effect = load(COMPOSITE).new() as CompositorEffect
	_check(_effect != null, "composite effect did not load")
	await _settle()
	_env.compositor.compositor_effects = []
	await _settle()
	_images["A"] = _capture("A_no_effect")
	# B: effect present, inactive (Rainbows 0 publishes this).
	_env.compositor.compositor_effects = [_effect]
	_effect.call("publish_frame", HELPER.new().InactivePayload(), false)
	await _settle()
	_images["B"] = _capture("B_inactive")
	# C: active with the forced rain mask.
	_effect.call("publish_frame", _bow["payload"], true)
	await _settle()
	_images["C"] = _capture("C_active")
	_analyse()
	await _check_ordering()
	_finish()

func _add_wall(position: Vector3, forward: Vector3, size: Vector2, color: Color) -> void:
	var mesh := MeshInstance3D.new()
	var quad := QuadMesh.new(); quad.size = size
	mesh.mesh = quad
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = color
	material.cull_mode = BaseMaterial3D.CULL_DISABLED
	mesh.material_override = material
	root.add_child(mesh)
	mesh.look_at_from_position(position, position - forward, Vector3.UP)

func _settle() -> void:
	for i in 6: await process_frame

func _capture(label: String) -> Image:
	var image := root.get_texture().get_image()
	image.convert(Image.FORMAT_RGBF)
	image.save_png(ProjectSettings.globalize_path(OUT_DIR + label + ".png"))
	return image

func _pixel(image: Image, position: Vector2) -> Color:
	var x := int(round(position.x)); var y := int(round(position.y))
	if x < 0 or y < 0 or x >= image.get_width() or y >= image.get_height(): return Color(-1, -1, -1)
	return image.get_pixel(x, y)

func _analyse() -> void:
	var a: Image = _images["A"]; var b: Image = _images["B"]; var c: Image = _images["C"]
	# (3) an inactive effect renders identically (SSIM 1.000 means no pixel differs).
	var max_ab := 0.0
	for y in a.get_height():
		for x in a.get_width():
			var d := a.get_pixel(x, y) - b.get_pixel(x, y)
			max_ab = maxf(max_ab, maxf(absf(d.r), maxf(absf(d.g), absf(d.b))))
	print("inactive vs none: max channel difference = %.6f" % max_ab)
	_check(max_ab == 0.0, "inactive effect changed pixels (max diff %.6f)" % max_ab)
	# (1) ring radius from the additive difference C - A.
	var anti: Vector3 = _bow["anti"]; var top: Vector3 = _bow["top"]; var side: Vector3 = _bow["side"]
	var centroid_sum := 0.0; var centroid_n := 0
	var red_theta := []; var violet_theta := []
	var secondary_peak := 0.0; var primary_peak := 0.0
	for psi_deg in range(-30, 31, 5):
		var psi := deg_to_rad(float(psi_deg))
		var weight_sum := 0.0; var theta_sum := 0.0
		var best_red := -1.0; var best_red_theta := 0.0; var best_violet := -1.0; var best_violet_theta := 0.0
		var any := false
		var theta := 36.0
		while theta <= 56.0:
			var r := deg_to_rad(theta)
			var direction := (anti * cos(r) + (top * cos(psi) + side * sin(psi)) * sin(r)).normalized()
			if _camera.is_position_behind(direction * 100.0):
				theta += 0.1; continue
			var screen := _camera.unproject_position(direction * 100.0)
			var pc := _pixel(c, screen); var pa := _pixel(a, screen)
			if pc.r < 0.0:
				theta += 0.1; continue
			var diff := pc - pa
			var luma := maxf(0.0, diff.r * 0.2126 + diff.g * 0.7152 + diff.b * 0.0722)
			if theta <= 44.0:
				var in_band := theta >= 40.0 and theta <= 43.2
				any = true
				if in_band:
					weight_sum += luma; theta_sum += luma * theta
				primary_peak = maxf(primary_peak, luma)
				var redness := diff.r - maxf(diff.g, diff.b)
				var blueness := diff.b - maxf(diff.r, diff.g)
				if redness > best_red: best_red = redness; best_red_theta = theta
				if blueness > best_violet: best_violet = blueness; best_violet_theta = theta
			elif theta >= 49.0:
				secondary_peak = maxf(secondary_peak, luma)
			theta += 0.1
		if any and weight_sum > 0.002:
			centroid_sum += theta_sum / weight_sum; centroid_n += 1
			red_theta.append(best_red_theta); violet_theta.append(best_violet_theta)
	_check(centroid_n >= 3, "bow visible at only %d azimuths" % centroid_n)
	if centroid_n > 0:
		var mean_radius := centroid_sum / centroid_n
		var mean_red := 0.0; var mean_violet := 0.0
		for t in red_theta: mean_red += t
		for t in violet_theta: mean_violet += t
		mean_red /= red_theta.size(); mean_violet /= violet_theta.size()
		print("bow radius (luminance centroid) = %.2f deg over %d azimuths; red peak %.2f deg, violet peak %.2f deg; primary peak %.3f, secondary peak %.3f" % [mean_radius, centroid_n, mean_red, mean_violet, primary_peak, secondary_peak])
		_check(absf(mean_radius - 42.0) <= 1.0, "bow radius %.2f deg is not 42 +/- 1" % mean_radius)
		_check(mean_red > mean_violet + 0.5, "red must be outside violet (red %.2f violet %.2f)" % [mean_red, mean_violet])
		_check(secondary_peak > 0.05 * primary_peak and secondary_peak < 0.9 * primary_peak, "secondary ring strength off (%.3f vs %.3f)" % [secondary_peak, primary_peak])
	# (4) no bow on the near wall: every pixel of the wall region that shows the bow elsewhere must be unchanged.
	var wall_changed := 0; var wall_pixels := 0; var far_changed := 0
	var viewport_size := Vector2(c.get_width(), c.get_height())
	for y in range(0, c.get_height(), 2):
		for x in range(0, c.get_width(), 2):
			var d := c.get_pixel(x, y) - a.get_pixel(x, y)
			var changed := maxf(absf(d.r), maxf(absf(d.g), absf(d.b))) > 1.0 / 255.0
			var near_wall := x > c.get_width() * 0.62
			if near_wall:
				wall_pixels += 1
				if changed: wall_changed += 1
			elif x < c.get_width() * 0.38 and changed:
				far_changed += 1
	print("near-wall pixels changed: %d of %d; far-wall/sky pixels changed on the left: %d" % [wall_changed, wall_pixels, far_changed])
	_check(wall_changed == 0, "%d pixels of the 10 m wall got rainbow" % wall_changed)
	_check(far_changed > 200, "bow missing on the far wall (%d changed pixels)" % far_changed)

func _check_ordering() -> void:
	# The rainbow driver node slots the effect before the rain streak / rain haze compositors; the rain owner's EnsureRainHazeCompositorLast then
	# removes and re-adds exactly those two at the end, which must leave the rainbow in front of them.
	var streaks := load("res://scripts/render/RingRainStreaksComposite.gd").new() as CompositorEffect
	var haze := load("res://scripts/render/RingRainHazeComposite.gd").new() as CompositorEffect
	var other := load(COMPOSITE).new() as CompositorEffect     # stands in for Sunshine (any other effect)
	var driver := preload("res://scripts/Weather/RingRainbow.cs").new()
	driver.set("AutoBind", false)
	root.add_child(driver)
	await process_frame
	driver.call("Bind", null, null, null, _env)
	var effects: Array[CompositorEffect] = [other, streaks, haze]
	_env.compositor.compositor_effects = effects
	driver.call("EnsureCompositorSlot")
	var list := _env.compositor.compositor_effects
	var rainbow_index := -1
	var scripts_before := []
	for i in list.size():
		var path := ""
		if list[i].get_script() != null: path = (list[i].get_script() as Script).resource_path
		scripts_before.append(path.get_file())
	# The driver's own effect is the one that is not `other`, `streaks` or `haze`.
	for i in list.size():
		if list[i] != other and list[i] != streaks and list[i] != haze: rainbow_index = i
	_check(rainbow_index == 1, "rainbow slot %d, expected directly after the first effect (list %s)" % [rainbow_index, str(scripts_before)])
	_check(list.find(streaks) == rainbow_index + 1 and list.find(haze) == rainbow_index + 2, "streaks and haze must follow the rainbow")
	# The rain owner's move: remove streaks and haze, add them back at the end.
	var moved := _env.compositor.compositor_effects
	moved.erase(streaks); moved.erase(haze); moved.append(streaks); moved.append(haze)
	_env.compositor.compositor_effects = moved
	driver.call("EnsureCompositorSlot")
	var after := _env.compositor.compositor_effects
	_check(after.find(other) < rainbow_index + 1 and after.find(streaks) == after.size() - 2 and after.find(haze) == after.size() - 1, "tail order after the rain owner's move")
	print("effect order: " + str(after.map(func(e): return (e.get_script() as Script).resource_path.get_file() if e.get_script() != null else "?")))
	driver.queue_free()
