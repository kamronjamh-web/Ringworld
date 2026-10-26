extends CompositorEffect
## Full-screen additive rainbow (docs/storm-revamp-s3-storm-arrival-design.md section 6, WO-F1). POST_TRANSPARENT, placed by RingRainbow.cs after Sunshine's effects and before
## the rain streak and rain haze compositors (RingLocalWeatherEffects.EnsureRainHazeCompositorLast only moves those two to the end, so this effect keeps its slot), so near
## rain and the heavy-rain veil draw in front of the bow and Sunshine's dark storm sits behind it. No dispatch at all while the published strength is 0.
## Payload: 208 bytes = 128 render-thread bytes (inverse projection, camera axes, viewport) + 80 CPU bytes (5 vec4); keep in sync with ring_rainbow.glsl and RingRainbow.cs.
const PAYLOAD_SIZE := 208
const CAMERA_BYTES := 128
var _mutex := Mutex.new()
var _frame := PackedByteArray()
var _active := false
var ready := false
var dispatches := 0
var last_error := ""
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var pipeline_format := -1
var depth_sampler: RID
var buffer: RID

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

## data: the 80 CPU bytes (anti, top, side, az_a, az_b as 5 vec4); active: false skips every GPU call.
func publish_frame(data: PackedByteArray, active: bool) -> void:
	_mutex.lock()
	_frame = data; _active = active
	_mutex.unlock()

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [pipeline, shader, depth_sampler, buffer]
	RenderingServer.call_on_render_thread(func():
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	ready = false

func _ensure_pipeline(framebuffer: RID) -> bool:
	var format := rd.framebuffer_get_format(framebuffer)
	if pipeline.is_valid() and pipeline_format == format: return true
	if pipeline.is_valid(): rd.free_rid(pipeline); pipeline = RID()
	var raster := RDPipelineRasterizationState.new()
	var multisample := RDPipelineMultisampleState.new()
	var depth_stencil := RDPipelineDepthStencilState.new()
	var blend := RDPipelineColorBlendState.new()
	var attachment := RDPipelineColorBlendStateAttachment.new()
	attachment.enable_blend = true
	attachment.src_color_blend_factor = RenderingDevice.BLEND_FACTOR_ONE
	attachment.dst_color_blend_factor = RenderingDevice.BLEND_FACTOR_ONE
	attachment.color_blend_op = RenderingDevice.BLEND_OP_ADD
	# Leave the scene alpha untouched: FSR2 reads it as the reactive mask.
	attachment.write_a = false
	blend.attachments.append(attachment)
	pipeline = rd.render_pipeline_create(shader, format, RenderingDevice.INVALID_ID, RenderingDevice.RENDER_PRIMITIVE_TRIANGLES, raster, multisample, depth_stencil, blend)
	pipeline_format = format
	return pipeline.is_valid()

func _render_callback(_type: int, render_data: RenderData) -> void:
	_mutex.lock()
	var data := _frame; var active := _active
	_mutex.unlock()
	if not active or data.size() != PAYLOAD_SIZE - CAMERA_BYTES: return
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	if not shader.is_valid():
		var file: RDShaderFile = load("res://scripts/render/ring_rainbow.glsl")
		if file == null: last_error = "shader file missing"; return
		var spirv := file.get_spirv()
		if spirv.compile_error_vertex != "" or spirv.compile_error_fragment != "":
			last_error = "compile: " + spirv.compile_error_vertex + spirv.compile_error_fragment; push_error("ring_rainbow.glsl " + last_error); return
		shader = rd.shader_create_from_spirv(spirv)
		if not shader.is_valid(): last_error = "shader create failed"; return
		depth_sampler = rd.sampler_create(RDSamplerState.new())
		buffer = rd.uniform_buffer_create(PAYLOAD_SIZE)
	ready = true
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var bytes := PackedByteArray(); bytes.resize(PAYLOAD_SIZE)
	var inverse: Projection = scene.get_cam_projection().inverse()
	var offset := 0
	for column in [inverse.x, inverse.y, inverse.z, inverse.w]:
		for value in [column.x, column.y, column.z, column.w]: bytes.encode_float(offset, value); offset += 4
	var camera: Transform3D = scene.get_cam_transform()
	var camera_axes: Array[Vector3] = [camera.basis.x, camera.basis.y, camera.basis.z]
	for axis in camera_axes:
		bytes.encode_float(offset, axis.x); bytes.encode_float(offset + 4, axis.y); bytes.encode_float(offset + 8, axis.z); bytes.encode_float(offset + 12, 0.0); offset += 16
	bytes.encode_float(offset, float(size.x)); bytes.encode_float(offset + 4, float(size.y)); bytes.encode_float(offset + 8, 0.0); bytes.encode_float(offset + 12, 0.0); offset += 16
	for i in data.size(): bytes[CAMERA_BYTES + i] = data[i]
	for view in buffers.get_view_count():
		var color := buffers.get_color_layer(view, false)
		var depth := buffers.get_depth_layer(view, false)
		if not color.is_valid() or not depth.is_valid(): continue
		var framebuffer := FramebufferCacheRD.get_cache_multipass([color], [], 1)
		if not framebuffer.is_valid() or not _ensure_pipeline(framebuffer): continue
		rd.buffer_update(buffer, 0, PAYLOAD_SIZE, bytes)
		var ubo := RDUniform.new(); ubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; ubo.binding = 0; ubo.add_id(buffer)
		var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(depth_sampler); d.add_id(depth)
		var set := UniformSetCacheRD.get_cache(shader, 0, [ubo, d])
		if not set.is_valid(): continue
		var list := rd.draw_list_begin(framebuffer, RenderingDevice.DRAW_DEFAULT_ALL)
		rd.draw_list_bind_render_pipeline(list, pipeline)
		rd.draw_list_bind_uniform_set(list, set, 0)
		rd.draw_list_draw(list, false, 1, 3)
		rd.draw_list_end()
		dispatches += 1
