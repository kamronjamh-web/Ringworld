extends CompositorEffect
## Converts the scaled far viewport's opaque reversed-Z buffer to physical
## metres.  The published Texture2DRD remains GPU-resident for Sunshine.

@export var coordinate_scale := 0.001
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var output: RID
var published: Texture2DRD
var size := Vector2i.ZERO
var sampler: RID
var camera_buffer: RID
var uniform_set: RID
var depth_source: RID
var retired: Array[Dictionary] = []

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_OPAQUE
	access_resolved_depth = true

func get_depth_texture() -> Texture2DRD:
	return published

func _retire_output() -> void:
	if !output.is_valid() and !uniform_set.is_valid(): return
	# Sunshine can still have this wrapper recorded in an in-flight descriptor
	# set. Keep both the RID and wrapper alive across Godot's render delay plus
	# two callback boundaries before freeing on this render callback.
	retired.append({"frames": max(2, rd.get_frame_delay() + 2), "output": output, "set": uniform_set, "wrapper": published})
	output = RID(); uniform_set = RID(); published = null

func _retire_uniform_set() -> void:
	if !uniform_set.is_valid(): return
	retired.append({"frames": max(2, rd.get_frame_delay() + 2), "output": RID(), "set": uniform_set})
	uniform_set = RID()

func _retire_completed() -> void:
	var pending: Array[Dictionary] = []
	for item in retired:
		item["frames"] = int(item["frames"]) - 1
		if int(item["frames"]) > 0: pending.append(item)
		else:
			var set: RID = item.get("set", RID())
			if set.is_valid() and rd.uniform_set_is_valid(set): rd.free_rid(set)
			var texture: RID = item.get("output", RID())
			if texture.is_valid() and rd.texture_is_valid(texture): rd.free_rid(texture)
	retired = pending

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var set_rids: Array[RID] = [uniform_set]
	var rids: Array[RID] = [camera_buffer, sampler, output, pipeline, shader]
	for item in retired:
		set_rids.append(item.get("set", RID()))
		rids.append(item.get("output", RID()))
	RenderingServer.call_on_render_thread(func():
		for set in set_rids:
			if set.is_valid() and device.uniform_set_is_valid(set): device.free_rid(set)
		for rid in rids:
			if rid.is_valid(): device.free_rid(rid))
	retired.clear()
	output = RID(); pipeline = RID(); shader = RID(); sampler = RID(); camera_buffer = RID(); uniform_set = RID(); depth_source = RID(); published = null; size = Vector2i.ZERO

func _render_callback(_callback_type: int, render_data: RenderData) -> void:
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	_retire_completed()
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null: return
	var next_size := buffers.get_internal_size()
	if next_size.x <= 0 or next_size.y <= 0: return
	if !shader.is_valid():
		var source: RDShaderFile = load("res://scripts/render/ring_far_depth_capture.glsl")
		if source == null: return
		shader = rd.shader_create_from_spirv(source.get_spirv())
		pipeline = rd.compute_pipeline_create(shader)
	if !output.is_valid() or next_size != size:
		_retire_output()
		var format := RDTextureFormat.new()
		format.format = RenderingDevice.DATA_FORMAT_R32_SFLOAT
		format.width = next_size.x; format.height = next_size.y
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
		output = rd.texture_create(format, RDTextureView.new())
		published = Texture2DRD.new(); published.texture_rd_rid = output; size = next_size
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if scene == null: return
	var projection: Projection = scene.get_cam_projection()
	var inv := projection.inverse()
	var data := PackedByteArray(); data.resize(80)
	var offset := 0
	for column in [inv.x, inv.y, inv.z, inv.w]:
		for value in [column.x, column.y, column.z, column.w]: data.encode_float(offset, value); offset += 4
	data.encode_float(offset, maxf(coordinate_scale, 0.0000001))
	if !camera_buffer.is_valid(): camera_buffer = rd.uniform_buffer_create(data.size(), data)
	else: rd.buffer_update(camera_buffer, 0, data.size(), data)
	var resolved_depth := buffers.get_depth_layer(0, false)
	if !sampler.is_valid(): sampler = rd.sampler_create(RDSamplerState.new())
	if !uniform_set.is_valid() or depth_source != resolved_depth:
		# The existing descriptor may be referenced by an in-flight dispatch.
		# Retain it with the old output rather than freeing it immediately.
		if uniform_set.is_valid(): _retire_uniform_set()
		var uniforms: Array[RDUniform] = []
		var depth := RDUniform.new(); depth.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; depth.binding = 0; depth.add_id(sampler); depth.add_id(resolved_depth); uniforms.append(depth)
		var target := RDUniform.new(); target.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; target.binding = 1; target.add_id(output); uniforms.append(target)
		var camera := RDUniform.new(); camera.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; camera.binding = 2; camera.add_id(camera_buffer); uniforms.append(camera)
		uniform_set = rd.uniform_set_create(uniforms, shader, 0); depth_source = resolved_depth
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, uniform_set, 0); rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
