extends SceneTree

const SURFACE = preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const GEOMETRY = preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
const FAR_DEPTH_CAPTURE = preload("res://scripts/render/RingFarDepthCapture.gd")

const CACHE_SIDE := 384
const CACHE_TILE_SIDE := 128
const VIEWPORT_SIZE := Vector2i(257, 145)
const CAMERA_POSITION := Vector3(0.8, 20.0, 0.8)
const TERRAIN_POINT := Vector3(0.8, 0.0, 0.8)
const SPIKE_HEIGHT := 8.0
const EXPECTED_DIAGONAL_HEIGHT := 1.6
const DEPTH_TOLERANCE_METERS := 0.05

# The odd target size gives an unambiguous center pixel. Nearby samples cover
# both halves of the spike cell and its adjacent terrain faces.
const DEPTH_SAMPLE_OFFSETS := [
	Vector2i(-2, -1), Vector2i(-1, -1), Vector2i(0, -1), Vector2i(1, -1), Vector2i(2, -1),
	Vector2i(-2, 0), Vector2i(-1, 0), Vector2i(0, 0), Vector2i(1, 0), Vector2i(2, 0),
	Vector2i(-2, 1), Vector2i(-1, 1), Vector2i(0, 1), Vector2i(1, 1), Vector2i(2, 1),
]

var failed := false
var _viewport: SubViewport


func _init() -> void:
	call_deferred("_run")
	create_timer(45.0).timeout.connect(func() -> void: _fail("timeout"))


func _run() -> void:
	_viewport = SubViewport.new()
	_viewport.own_world_3d = true
	_viewport.size = VIEWPORT_SIZE
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)

	var capture: CompositorEffect = FAR_DEPTH_CAPTURE.new()
	capture.set("coordinate_scale", 1.0)
	var environment := Environment.new()
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	world_environment.compositor = Compositor.new()
	world_environment.compositor.compositor_effects = [capture]
	_viewport.add_child(world_environment)

	var camera := Camera3D.new()
	camera.current = true
	camera.position = CAMERA_POSITION
	_viewport.add_child(camera)
	camera.look_at(TERRAIN_POINT, Vector3.FORWARD)

	var surface: Node3D = SURFACE.new()
	_viewport.add_child(surface)
	if not await _prepare_surface(surface):
		return

	# A positive diagonal gives 4.8m here. The production flipped diagonal is 1.6m.
	var vertical_hit := _raycast(Vector3(0.8, 20.0, 0.8), Vector3(0.8, -20.0, 0.8))
	_expect(not vertical_hit.is_empty(), "custom diagonal collider missing")
	if failed:
		return
	var collider_height: float = (vertical_hit.position as Vector3).y
	_expect(absf(collider_height - EXPECTED_DIAGONAL_HEIGHT) < DEPTH_TOLERANCE_METERS,
		"custom collider diagonal height %.3fm differs from flipped expected %.3fm" % [collider_height, EXPECTED_DIAGONAL_HEIGHT])
	if failed:
		return

	for frame in 16:
		await RenderingServer.frame_post_draw
		if failed:
			return

	var depth_texture: Texture2D = capture.call("get_depth_texture") as Texture2D
	_expect(depth_texture != null, "far depth capture did not publish")
	if failed:
		return
	var depth_image := depth_texture.get_image()
	_expect(depth_image != null and not depth_image.is_empty() and depth_image.get_format() == Image.FORMAT_RF,
		"far depth readback did not return an RF image")
	if failed:
		return

	var metrics := _compare_depth_samples(camera, depth_image)
	if failed:
		return
	print("TERRAIN3D_DIAGONAL_GPU_METRICS ", JSON.stringify(metrics))
	print("TERRAIN3D_DIAGONAL_GPU_TEST=PASS collider_height=%.4f max_depth_error_m=%.5f samples=%d" % [
		collider_height, metrics.max_depth_error_m, metrics.sample_count,
	])
	await _finish(0)


func _prepare_surface(surface: Node3D) -> bool:
	var heights := PackedFloat32Array()
	heights.resize(CACHE_SIDE * CACHE_SIDE)
	heights.fill(0.0)
	heights[128 * CACHE_SIDE + 128] = SPIKE_HEIGHT

	var height_images: Array[Image] = []
	var biome_images: Array[Image] = []
	for tile_index in 9:
		var bytes := PackedByteArray()
		bytes.resize(CACHE_TILE_SIDE * CACHE_TILE_SIDE * 4)
		for pixel_index in CACHE_TILE_SIDE * CACHE_TILE_SIDE:
			bytes.encode_float(pixel_index * 4, 0.0)
		height_images.append(Image.create_from_data(CACHE_TILE_SIDE, CACHE_TILE_SIDE, false, Image.FORMAT_RF, bytes))
		var biome := Image.create_empty(CACHE_TILE_SIDE, CACHE_TILE_SIDE, false, Image.FORMAT_RGBA8)
		biome.fill(Color.WHITE)
		biome_images.append(biome)

	# This is the production cache sample corresponding to heights[128, 128].
	height_images[4].set_pixel(0, 0, Color(SPIKE_HEIGHT, 0.0, 0.0, 1.0))
	var geometry := GEOMETRY.new()
	_expect(geometry.call("PrepareGameplay", heights, 0, 159000, 0.0, -1, 0.0, SPIKE_HEIGHT, 32), "prepare collision geometry")
	if failed:
		return false
	_expect(surface.call("BeginGameplayPreparedWindow", heights, height_images, biome_images, geometry, 0, 159000, 0.0, -1, 1, 0.0, SPIKE_HEIGHT), "begin prepared window")
	if failed:
		return false

	var ready := false
	for frame in 240:
		var state: Dictionary = surface.call("AdvanceBudgetedPreparation", 0.5, 0.5)
		_expect(bool(state.get("ok", false)), "prepared state invalid")
		if failed:
			return false
		if bool(state.get("ready", false)):
			ready = true
			break
		await process_frame
	_expect(ready, "prepared window did not become ready")
	if failed:
		return false
	_expect(surface.call("ActivateStagedWindow", 1), "activate prepared window")
	if failed:
		return false
	await physics_frame
	return not failed


func _compare_depth_samples(camera: Camera3D, depth_image: Image) -> Dictionary:
	var image_size := depth_image.get_size()
	var center_pixel := Vector2i(image_size.x / 2, image_size.y / 2)
	var max_depth_error := 0.0
	var samples: Array[Dictionary] = []
	for offset: Vector2i in DEPTH_SAMPLE_OFFSETS:
		var pixel: Vector2i = center_pixel + offset
		_expect(Rect2i(Vector2i.ZERO, image_size).has_point(pixel), "depth sample %s is outside the image" % pixel)
		if failed:
			return {}
		var screen_position := (Vector2(pixel) + Vector2(0.5, 0.5)) / Vector2(image_size) * Vector2(VIEWPORT_SIZE)
		var ray_origin := camera.global_position
		var ray_direction := camera.project_ray_normal(screen_position)
		var collision_hit := _raycast(ray_origin, ray_origin + ray_direction * 100.0)
		var visual_distance := depth_image.get_pixelv(pixel).r
		_expect(not collision_hit.is_empty() and is_finite(visual_distance) and visual_distance > 0.0,
			"sample %s has no finite visual depth or collision" % pixel)
		if failed:
			return {}
		var collision_distance := ray_origin.distance_to(collision_hit.position)
		var error := absf(visual_distance - collision_distance)
		max_depth_error = maxf(max_depth_error, error)
		samples.append({"pixel": pixel, "visual_m": visual_distance, "collision_m": collision_distance, "error_m": error})
		_expect(error < DEPTH_TOLERANCE_METERS,
			"sample %s visual depth %.5fm differs from collision %.5fm by %.5fm" % [pixel, visual_distance, collision_distance, error])
		if failed:
			return {}
	return {"image_size": image_size, "sample_count": samples.size(), "max_depth_error_m": max_depth_error, "samples": samples}


func _raycast(from: Vector3, to: Vector3) -> Dictionary:
	return _viewport.find_world_3d().direct_space_state.intersect_ray(PhysicsRayQueryParameters3D.create(from, to, 1))


func _finish(exit_code: int) -> void:
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	for frame in 4:
		await RenderingServer.frame_post_draw
	quit(exit_code)


func _expect(condition: bool, message: String) -> void:
	if not condition:
		_fail(message)


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	push_error("TERRAIN3D_DIAGONAL_GPU_TEST " + message)
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	call_deferred("quit", 1)
