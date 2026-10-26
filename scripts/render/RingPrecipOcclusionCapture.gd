extends CompositorEffect
## Precipitation occlusion map capture (docs/storm-revamp-s1-design.md section 1). Runs POST_OPAQUE in the map camera's own
## compositor: converts the resolved orthographic depth to metres below the map plane (R32F, 0 = nothing drawn) into one of two
## ping-pong Texture2DRD outputs. A capture only happens after request_capture(), so any other render of the map viewport is inert.
## C# publishes the slot's texture to the shaders only once completed_serial reached the request, so readers never see a half-written map.

const SLOT_COUNT := 2
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var sampler: RID
var camera_buffer: RID
var outputs: Array[RID] = [RID(), RID()]
var wrappers: Array[Texture2DRD] = [null, null]
var size := Vector2i.ZERO
var retired: Array[Dictionary] = []
var _mutex := Mutex.new()
var _armed_slot := -1
var _armed_serial := 0
## Serial and slot of the newest finished capture. Written on the render thread, read from C# once per frame.
var completed_serial := 0
var completed_slot := -1
var gpu_dispatches := 0

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_OPAQUE
	access_resolved_depth = true

func request_capture(slot: int, serial: int) -> void:
	_mutex.lock()
	_armed_slot = clampi(slot, 0, SLOT_COUNT - 1); _armed_serial = serial
	_mutex.unlock()

func get_texture(slot: int) -> Texture2DRD:
	return wrappers[clampi(slot, 0, SLOT_COUNT - 1)]

func get_texture_rid(slot: int) -> RID:
	return outputs[clampi(slot, 0, SLOT_COUNT - 1)]

func get_completed_serial() -> int:
	return completed_serial

func get_completed_slot() -> int:
	return completed_slot

func _retire_completed() -> void:
	var pending: Array[Dictionary] = []
	for item in retired:
		item["frames"] = int(item["frames"]) - 1
		if int(item["frames"]) > 0: pending.append(item)
		else:
			var set: RID = item.get("set", RID())
			if set.is_valid() and rd.uniform_set_is_valid(set): rd.free_rid(set)
	retired = pending

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var set_rids: Array[RID] = []
	for item in retired: set_rids.append(item.get("set", RID()))
	var rids: Array[RID] = [camera_buffer, sampler, outputs[0], outputs[1], pipeline, shader]
	RenderingServer.call_on_render_thread(func():
		for set in set_rids:
			if set.is_valid() and device.uniform_set_is_valid(set): device.free_rid(set)
		for rid in rids:
			if rid.is_valid(): device.free_rid(rid))
	retired.clear()

func _render_callback(_callback_type: int, render_data: RenderData) -> void:
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	_retire_completed()
	_mutex.lock()
	var slot := _armed_slot; var serial := _armed_serial
	_armed_slot = -1
	_mutex.unlock()
	if slot < 0: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null: return
	var next_size := buffers.get_internal_size()
	if next_size.x <= 0 or next_size.y <= 0: return
	if !shader.is_valid():
		var source: RDShaderFile = load("res://scripts/render/ring_precip_occlusion_capture.glsl")
		if source == null: return
		shader = rd.shader_create_from_spirv(source.get_spirv())
		pipeline = rd.compute_pipeline_create(shader)
		sampler = rd.sampler_create(RDSamplerState.new())
	if next_size != size:
		# The map has a fixed size; a change would only happen on a debug resize. Recreate both outputs after the render delay.
		for i in SLOT_COUNT:
			if outputs[i].is_valid():
				var old_output: RID = outputs[i]
				var device := rd
				RenderingServer.call_on_render_thread(func(): if old_output.is_valid(): device.free_rid(old_output))
			var format := RDTextureFormat.new()
			format.format = RenderingDevice.DATA_FORMAT_R32_SFLOAT
			format.width = next_size.x; format.height = next_size.y
			format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
			outputs[i] = rd.texture_create(format, RDTextureView.new())
			if wrappers[i] == null: wrappers[i] = Texture2DRD.new()
			wrappers[i].texture_rd_rid = outputs[i]
		size = next_size
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if scene == null: return
	var inv := scene.get_cam_projection().inverse()
	var data := PackedByteArray(); data.resize(64)
	var offset := 0
	for column in [inv.x, inv.y, inv.z, inv.w]:
		for value in [column.x, column.y, column.z, column.w]: data.encode_float(offset, value); offset += 4
	if !camera_buffer.is_valid(): camera_buffer = rd.uniform_buffer_create(data.size(), data)
	else: rd.buffer_update(camera_buffer, 0, data.size(), data)
	var resolved_depth := buffers.get_depth_layer(0, false)
	var uniforms: Array[RDUniform] = []
	var depth := RDUniform.new(); depth.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; depth.binding = 0; depth.add_id(sampler); depth.add_id(resolved_depth); uniforms.append(depth)
	var target := RDUniform.new(); target.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; target.binding = 1; target.add_id(outputs[slot]); uniforms.append(target)
	var camera := RDUniform.new(); camera.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; camera.binding = 2; camera.add_id(camera_buffer); uniforms.append(camera)
	var uniform_set := rd.uniform_set_create(uniforms, shader, 0)
	# One capture per second at most: build the set per capture and free it a few render callbacks later.
	retired.append({"frames": max(3, rd.get_frame_delay() + 2), "set": uniform_set})
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, uniform_set, 0)
	rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
	gpu_dispatches += 1
	completed_slot = slot
	completed_serial = serial
