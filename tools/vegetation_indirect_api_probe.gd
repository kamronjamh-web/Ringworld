extends SceneTree

# Small real-RD contract probe. No world scene, terrain streaming, or user settings.
var viewport: SubViewport
var multi: MultiMesh
var mesh: Mesh
var node: MultiMeshInstance3D
var initial_buffer := RID()
var commands := RID()
var checked := false
var failure := ""

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("VEGETATION_INDIRECT_API=FAIL RenderingDevice required")
		quit(1)
		return
	call_deferred("run")

func run() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(96, 96)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.position = Vector3(0, 0, 5)
	camera.current = true
	viewport.add_child(camera)
	mesh = BoxMesh.new()
	multi = MultiMesh.new()
	multi.transform_format = MultiMesh.TRANSFORM_3D
	multi.mesh = mesh
	multi.instance_count = 3
	multi.custom_aabb = AABB(Vector3(-3,-2,-2), Vector3(6,4,4))
	node = MultiMeshInstance3D.new()
	node.multimesh = multi
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = Color.RED
	node.material_override = material
	viewport.add_child(node)
	RenderingServer.call_on_render_thread(allocate_indirect)
	for i in range(8): await RenderingServer.frame_post_draw
	if not checked or not failure.is_empty():
		push_error("VEGETATION_INDIRECT_API=FAIL " + failure)
		quit(1)
		return
	var pixels := red_pixels(viewport.get_texture().get_image())
	if pixels < 50:
		push_error("VEGETATION_INDIRECT_API=FAIL indirect buffer not drawn, pixels=%d" % pixels)
		quit(1)
		return
	viewport.use_taa = true
	for i in range(8): await RenderingServer.frame_post_draw
	RenderingServer.call_on_render_thread(check_after_taa)
	for i in range(4): await RenderingServer.frame_post_draw
	if not failure.is_empty():
		push_error("VEGETATION_INDIRECT_API=FAIL " + failure)
		quit(1)
		return
	node.visible = false
	for i in range(2): await RenderingServer.frame_post_draw
	RenderingServer.call_on_render_thread(retire_indirect)
	for i in range(2): await RenderingServer.frame_post_draw
	print("VEGETATION_INDIRECT_API=PASS indirect draw pixels=%d, TAA buffer stable, explicit command retirement" % pixels)
	quit(0)

func allocate_indirect() -> void:
	var rd := RenderingServer.get_rendering_device()
	var rid := multi.get_rid()
	RenderingServer.multimesh_set_mesh(rid, RID())
	RenderingServer.multimesh_allocate_data(rid, 0, RenderingServer.MULTIMESH_TRANSFORM_3D, false, false, false)
	RenderingServer.multimesh_allocate_data(rid, 3, RenderingServer.MULTIMESH_TRANSFORM_3D, false, false, true)
	RenderingServer.multimesh_set_mesh(rid, mesh.get_rid())
	RenderingServer.multimesh_set_custom_aabb(rid, multi.custom_aabb)
	initial_buffer = RenderingServer.multimesh_get_buffer_rd_rid(rid)
	commands = RenderingServer.multimesh_get_command_buffer_rd_rid(rid)
	if not initial_buffer.is_valid() or not commands.is_valid():
		failure = "missing buffers"
		checked = true
		return
	var source := PackedFloat32Array([1,0,0,-1, 0,1,0,0, 0,0,1,0, 1,0,0,1, 0,1,0,0, 0,0,1,0, 1,0,0,0, 0,1,0,2, 0,0,1,0])
	rd.buffer_update(initial_buffer, 0, source.size() * 4, source.to_byte_array())
	var command := rd.buffer_get_data(commands)
	if command.size() != 20 or command.decode_u32(0) == 0:
		failure = "invalid geometry command"
	else:
		var count := PackedByteArray()
		count.resize(4)
		count.encode_u32(0, 2)
		rd.buffer_update(commands, 4, 4, count)
	checked = true

func check_after_taa() -> void:
	var rd := RenderingServer.get_rendering_device()
	var current_buffer := RenderingServer.multimesh_get_buffer_rd_rid(multi.get_rid())
	if current_buffer != initial_buffer:
		failure = "TAA reallocated instance buffer; driver must refresh bindings"
	var command := rd.buffer_get_data(commands)
	if command.size() < 8 or command.decode_u32(4) != 2:
		failure = "renderer overwrote GPU instance count"

func retire_indirect() -> void:
	# Godot 4.7.2 drops this command RID during reallocation without freeing it.
	# The project pins that version; the production driver must retire it too.
	RenderingServer.multimesh_allocate_data(multi.get_rid(), 0, RenderingServer.MULTIMESH_TRANSFORM_3D, false, false, false)
	RenderingServer.get_rendering_device().free_rid(commands)
	commands = RID()

func red_pixels(img: Image) -> int:
	var count := 0
	for y in range(img.get_height()):
		for x in range(img.get_width()):
			var color := img.get_pixel(x,y)
			if color.r > .1 and color.r > color.g * 2: count += 1
	return count
