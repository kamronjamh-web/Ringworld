extends SceneTree
# Run windowed Vulkan/Forward+ through tools/run_tests.ps1; no other Godot process running.
# A prepared artifact must have been baked and checked before this acceptance test.
const PROBE := preload("res://tools/PreparedTreeTextureProbe.cs")
var _streamer: Object
var _old_overrides: Array[int] = []

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	if RenderingServer.get_rendering_device() == null or RenderingServer.get_current_rendering_method() != "forward_plus" or not Engine.has_singleton("TextureStreaming"):
		_finish(1, "requires Forward+ and native TextureStreaming")
		return
	create_timer(60.0).timeout.connect(func(): _finish(1, "timeout"))
	var probe = PROBE.new()
	var texture: Texture2D = probe.LoadTexture()
	probe = null
	if texture == null:
		_finish(1, "no native texture from reloaded prepared material")
		return
	_streamer = Engine.get_singleton("TextureStreaming")
	_old_overrides = [int(_streamer.call("get_memory_budget_mb_override")), int(_streamer.call("get_min_lod_override")), int(_streamer.call("get_max_lod_override"))]
	_streamer.call("set_memory_budget_mb_override", 512)
	_streamer.call("set_min_lod_override", 0)
	_streamer.call("set_max_lod_override", 3)
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
	material.uv1_scale = Vector3(0.0625, 0.0625, 1)
	quad.material_override = material
	viewport.add_child(quad)
	if not await _wait_width(texture, false):
		_finish(1, "magnified prepared texture did not reach full resolution")
		return
	var fine := await _resident_width(texture)
	_streamer.call("set_memory_budget_mb_override", 1)
	if not await _wait_width(texture, true):
		_finish(1, "prepared texture did not reduce residency under 1 MiB budget")
		return
	var coarse := await _resident_width(texture)
	_streamer.call("set_memory_budget_mb_override", 512)
	if not await _wait_width(texture, false):
		_finish(1, "prepared texture did not recover residency")
		return
	print("PREPARED_TREE_TEXTURE_DETAIL path=", texture.resource_path, " class=", texture.get_class(), " fine_width=", fine, " coarse_width=", coarse)
	viewport.queue_free()
	_finish(0, "")

func _wait_width(texture: Texture2D, coarser: bool) -> bool:
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline:
		var width := await _resident_width(texture)
		if (coarser and width > 0 and width < texture.get_width()) or (not coarser and width == texture.get_width()):
			return true
		await create_timer(0.1).timeout
	return false

func _resident_width(texture: Texture2D) -> int:
	var result := {"done": false, "width": 0}
	var mutex := Mutex.new()
	RenderingServer.call_on_render_thread(func() -> void:
		mutex.lock()
		var device := RenderingServer.get_rendering_device()
		var rid := RenderingServer.texture_get_rd_texture(texture.get_rid())
		if rid.is_valid():
			result.width = device.texture_get_format(rid).width
		result.done = true
		mutex.unlock())
	while true:
		mutex.lock()
		var done: bool = result.done
		mutex.unlock()
		if done:
			break
		await process_frame
	return int(result.width)

func _finish(code: int, message: String) -> void:
	if _streamer != null and _old_overrides.size() == 3:
		_streamer.call("set_memory_budget_mb_override", _old_overrides[0])
		_streamer.call("set_min_lod_override", _old_overrides[1])
		_streamer.call("set_max_lod_override", _old_overrides[2])
	if code == 0:
		print("PREPARED_TREE_TEXTURE_STREAMING=PASS")
	else:
		push_error("PREPARED_TREE_TEXTURE_STREAMING=FAIL " + message)
	quit(code)
