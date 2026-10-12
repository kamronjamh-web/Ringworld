extends SceneTree
## Run after the dev7 editor import. CPU readback is separate from GPU feedback validation.
const SOURCES := [
	"res://resources/trees/lolipop/pine/textures/Bark_Mat_baseColor.png",
	"res://resources/trees/lolipop/pine/textures/Clusters_Mat_normal.png",
	"res://resources/trees/lolipop/maple/textures/Bark_Mat_normal.png",
	"res://resources/trees/lolipop/fir/textures/Bark_Mat_baseColor.png",
]

func _initialize() -> void:
	if not Engine.has_singleton("TextureStreaming"):
		fail("missing native singleton")
		return
	var streamer: Object = Engine.get_singleton("TextureStreaming")
	var old_budget: int = streamer.call("get_memory_budget_mb_override")
	var old_min: int = streamer.call("get_min_lod_override")
	var old_max: int = streamer.call("get_max_lod_override")
	streamer.call("set_memory_budget_mb_override", 256)
	streamer.call("set_min_lod_override", 0)
	streamer.call("set_max_lod_override", 3)
	if int(streamer.call("get_memory_budget_mb_override")) != 256 or int(streamer.call("get_min_lod_override")) != 0 or int(streamer.call("get_max_lod_override")) != 3:
		fail("runtime overrides did not bind")
		return
	for source_path: String in SOURCES:
		var texture := load(source_path) as Texture2D
		if texture == null or texture.get_class() != "StreamedTexture2D":
			fail("incorrect imported resource: " + source_path)
			return
		var image: Image = texture.get_image()
		var original := Image.new()
		var source_error: Error = original.load_png_from_buffer(FileAccess.get_file_as_bytes(source_path))
		if source_error != OK or original.is_empty() or image == null or image.is_empty() or image.get_width() != original.get_width() or image.get_height() != original.get_height() or not image.has_mipmaps():
			fail("CPU get_image must return full-resolution mip chain: " + source_path)
			return
		if image.is_compressed() and image.decompress() != OK:
			fail("CPU decompression failed: " + source_path)
			return
		image.convert(Image.FORMAT_RGBA8)
		image.resize(64, 64)
		image.generate_mipmaps()
		var array := Texture2DArray.new()
		if array.create_from_images([image]) != OK:
			fail("impostor CPU image to texture-array compatibility failed")
			return
	streamer.call("set_memory_budget_mb_override", old_budget)
	streamer.call("set_min_lod_override", old_min)
	streamer.call("set_max_lod_override", old_max)
	print("NATIVE_TEXTURE_STREAMING_CPU=PASS renderer=", RenderingServer.get_current_rendering_method(), " used_bytes=", streamer.call("get_memory_budget_bytes_used"))
	if "--rendered" in OS.get_cmdline_user_args():
		_run_rendered.call_deferred()
	else:
		quit(0)

func _run_rendered() -> void:
	if RenderingServer.get_rendering_device() == null or RenderingServer.get_current_rendering_method() != "forward_plus":
		fail("--rendered requires Forward+ and an actual RenderingDevice")
		return
	var streamer: Object = Engine.get_singleton("TextureStreaming")
	streamer.call("set_memory_budget_mb_override", 512)
	streamer.call("set_min_lod_override", 0)
	streamer.call("set_max_lod_override", 3)
	var texture: Texture2D = load(SOURCES[3])
	var viewport := SubViewport.new()
	viewport.size = Vector2i(512, 512)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	viewport.own_world_3d = true
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 4
	camera.position = Vector3(0, 0, 2)
	viewport.add_child(camera)
	camera.make_current()
	var quad := MeshInstance3D.new()
	var mesh := QuadMesh.new()
	mesh.size = Vector2(4, 4)
	quad.mesh = mesh
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_texture = texture
	# A small UV region is magnified to demand true mip 0 even at a 512px viewport.
	material.uv1_scale = Vector3(0.0625, 0.0625, 1)
	quad.material_override = material
	quad.visible = false
	viewport.add_child(quad)
	await create_timer(1).timeout
	var initial: Dictionary = await _resident(texture)
	quad.visible = true
	if not await _wait_for_resolution(texture, texture.get_width(), 10):
		fail("visible magnified texture did not recover full-resolution mip; initial=" + str(initial) + " actual=" + str(await _resident(texture)))
		return
	var fine: Dictionary = await _resident(texture)
	var fine_bytes: int = streamer.call("get_memory_budget_bytes_used")
	streamer.call("set_memory_budget_mb_override", 1)
	if not await _wait_for_coarser(texture, texture.get_width(), 10):
		fail("1 MiB budget did not reduce native residency: " + str(await _resident(texture)))
		return
	var budget_limited: Dictionary = await _resident(texture)
	var reduced_bytes: int = streamer.call("get_memory_budget_bytes_used")
	streamer.call("set_memory_budget_mb_override", 512)
	if not await _wait_for_resolution(texture, texture.get_width(), 10):
		fail("native residency did not recover after restoring memory budget")
		return
	quad.visible = false
	if not await _wait_for_coarser(texture, texture.get_width(), 18):
		fail("invisible texture failed inactivity residency decay")
		return
	var unseen: Dictionary = await _resident(texture)
	quad.visible = true
	if not await _wait_for_resolution(texture, texture.get_width(), 10):
		fail("native texture failed near-camera recovery after becoming visible")
		return
	print("NATIVE_TEXTURE_STREAMING_RENDERED=PASS initial=", initial, " fine=", fine, " budget_limited=", budget_limited, " unseen=", unseen, " fine_bytes=", fine_bytes, " reduced_bytes=", reduced_bytes)
	viewport.queue_free()
	quit(0)

func _wait_for_resolution(texture: Texture2D, expected_width: int, seconds: float) -> bool:
	var deadline: int = Time.get_ticks_msec() + int(seconds * 1000)
	while Time.get_ticks_msec() < deadline:
		var value: Dictionary = await _resident(texture)
		if int(value.get("width", 0)) == expected_width:
			return true
		await create_timer(0.1).timeout
	return false

func _wait_for_coarser(texture: Texture2D, full_width: int, seconds: float) -> bool:
	var deadline: int = Time.get_ticks_msec() + int(seconds * 1000)
	while Time.get_ticks_msec() < deadline:
		var value: Dictionary = await _resident(texture)
		if int(value.get("width", 0)) > 0 and int(value.width) < full_width:
			return true
		await create_timer(0.1).timeout
	return false

func _resident(texture: Texture2D) -> Dictionary:
	var result: Dictionary = {}
	var mutex := Mutex.new()
	RenderingServer.call_on_render_thread(func() -> void:
		mutex.lock()
		var device: RenderingDevice = RenderingServer.get_rendering_device()
		var rid: RID = RenderingServer.texture_get_rd_texture(texture.get_rid())
		if rid.is_valid():
			var format: RDTextureFormat = device.texture_get_format(rid)
			result["width"] = format.width
			result["height"] = format.height
			result["mipmaps"] = format.mipmaps
		result["done"] = true
		mutex.unlock()
	)
	while true:
		mutex.lock()
		var done: bool = result.has("done")
		mutex.unlock()
		if done:
			break
		await process_frame
	return result

func fail(message: String) -> void:
	push_error("NATIVE_TEXTURE_STREAMING_CPU=FAIL " + message)
	quit(1)
