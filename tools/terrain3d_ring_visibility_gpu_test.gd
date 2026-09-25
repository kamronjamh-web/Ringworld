extends SceneTree

func _init() -> void:
	if OS.has_feature("headless"):
		print("Terrain3D ring visibility GPU test: SKIP (headless renderer unsupported)")
		quit(0)
		return
	var fixture := "res://.godot/terrain3d_ring_visibility_%d" % Time.get_ticks_usec()
	var harness: Node = load("res://tools/Terrain3DRingVisibilityTest.cs").new()
	root.add_child(harness)
	for wait_frame in 180:
		await process_frame
		if harness.IsSurfaceReady:
			break
	if not harness.IsSurfaceReady:
		_fail("surface failed to initialize: %s" % harness.LastError)
		return
	for quarter in 4:
		harness.ConfigureAngle(quarter)
		await process_frame
		await RenderingServer.frame_post_draw
		var image := root.get_viewport().get_texture().get_image()
		var path := ProjectSettings.globalize_path(fixture.path_join("quarter_%d.png" % quarter))
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
		if image.save_png(path) != OK:
			_fail("capture failed at quarter %d" % quarter)
			return
		print("Terrain3D ring visibility capture: ", path)
		if not _has_ground_pixels(image):
			_fail("terrain missing at quarter %d; likely all-axis culling regression" % quarter)
			return
	print("Terrain3D ring visibility GPU test: PASS")
	quit(0)

func _has_ground_pixels(image: Image) -> bool:
	for x_fraction in [0.25, 0.5, 0.75]:
		for y_fraction in [0.5, 0.7]:
			var pixel := image.get_pixel(int(image.get_width() * x_fraction), int(image.get_height() * y_fraction))
			if pixel.r > 0.5 and pixel.b > 0.5 and pixel.g < 0.1:
				return false
	return true

func _fail(message: String) -> void:
	push_error(message)
	quit(1)
