extends CompositorEffect
## Draws rain streaks and splashes onto the resolved scene colour AFTER the Sunshine cloud composite (POST_TRANSPARENT, ordered Sunshine, streaks, rain haze by
## RingLocalWeatherEffects.EnsureRainHazeCompositorLast). Sunshine blends cloud over the whole resolved colour, so any ordinary transparent streak would be clouded
## over; this raster pass draws on top of that result with a manual depth test against the resolved depth (docs/storm-revamp-s1-design.md section 2).
## No vertex buffers: the shader generates every quad from gl_VertexIndex / gl_InstanceIndex and the frame payload published by RingRainStreaks.cs.
const PAYLOAD_SIZE := 880  # 208 render-thread bytes (camera) + 42 CPU vec4 below; keep in sync with ring_rain_streaks.glslinc and RingRainStreaks.cs
const CAMERA_BYTES := 208
var _mutex := Mutex.new()
var _frame := PackedByteArray()
var _gust: Texture2D
var _occ: Texture2D
var _storm: RID  # Sunshine macro weather map array (RGBA16F), or an invalid RID
var _active := false
var _draws := PackedInt32Array([0, 0, 0, 0, 0, 0])  # instance counts: near streaks, mid streaks, near splashes, mid splashes, S2 drips; [5] = vertices per drip instance
var ready := false
var dispatches := 0
var last_error := ""
@export var gpu_timestamp_diagnostics_enabled := false
@export var gpu_timestamp_begin_marker := ""
@export var gpu_timestamp_end_marker := ""
var timestamp_scopes := 0
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var pipeline_format := -1
var depth_sampler: RID
var gust_sampler: RID
var map_sampler: RID
var storm_sampler: RID
var storm_fallback: RID
var buffer: RID

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

## data: the CPU payload (PAYLOAD_SIZE - CAMERA_BYTES bytes); counts: instances for [near streak, mid streak, near splash, mid splash].
func publish_frame(data: PackedByteArray, counts: PackedInt32Array, gust: Texture2D, occ: Texture2D, active: bool, storm: RID = RID()) -> void:
	_mutex.lock()
	_frame = data; _draws = counts; _gust = gust; _occ = occ; _active = active; _storm = storm
	_mutex.unlock()

func publish_gpu_markers(enabled: bool, begin: String, end: String) -> void:
	_mutex.lock()
	gpu_timestamp_diagnostics_enabled = enabled
	gpu_timestamp_begin_marker = begin
	gpu_timestamp_end_marker = end
	_mutex.unlock()

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [pipeline, shader, depth_sampler, gust_sampler, map_sampler, storm_sampler, storm_fallback, buffer]
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
	attachment.src_color_blend_factor = RenderingDevice.BLEND_FACTOR_SRC_ALPHA
	attachment.dst_color_blend_factor = RenderingDevice.BLEND_FACTOR_ONE_MINUS_SRC_ALPHA
	attachment.color_blend_op = RenderingDevice.BLEND_OP_ADD
	# Leave the scene alpha untouched: FSR2 reads it as the reactive mask (see SunshineClouds.gd initialize_raster_pipelines).
	attachment.write_a = false
	blend.attachments.append(attachment)
	pipeline = rd.render_pipeline_create(shader, format, RenderingDevice.INVALID_ID, RenderingDevice.RENDER_PRIMITIVE_TRIANGLES, raster, multisample, depth_stencil, blend)
	pipeline_format = format
	return pipeline.is_valid()

func _render_callback(_type: int, render_data: RenderData) -> void:
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	if not shader.is_valid():
		var file: RDShaderFile = load("res://scripts/render/ring_rain_streaks.glsl")
		if file == null: last_error = "shader file missing"; return
		var spirv := file.get_spirv()
		if spirv.compile_error_vertex != "" or spirv.compile_error_fragment != "":
			last_error = "compile: " + spirv.compile_error_vertex + spirv.compile_error_fragment; push_error("ring_rain_streaks.glsl " + last_error); return
		shader = rd.shader_create_from_spirv(spirv)
		if not shader.is_valid(): last_error = "shader create failed"; return
		depth_sampler = rd.sampler_create(RDSamplerState.new())
		var repeat := RDSamplerState.new(); repeat.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT; repeat.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
		repeat.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; repeat.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		gust_sampler = rd.sampler_create(repeat)
		map_sampler = rd.sampler_create(RDSamplerState.new())
		var linear := RDSamplerState.new(); linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		storm_sampler = rd.sampler_create(linear)
		var format := RDTextureFormat.new()
		format.texture_type = RenderingDevice.TEXTURE_TYPE_2D_ARRAY; format.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
		format.width = 1; format.height = 1; format.array_layers = 2; format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
		var zero := PackedByteArray(); zero.resize(8)
		storm_fallback = rd.texture_create(format, RDTextureView.new(), [zero, zero])  # bound while the storm map is unavailable (the shader then never samples it)
		buffer = rd.uniform_buffer_create(PAYLOAD_SIZE)
	ready = true
	_mutex.lock()
	var data := _frame; var gust := _gust; var occ := _occ; var active := _active; var counts := _draws; var storm := _storm
	var timestamps := gpu_timestamp_diagnostics_enabled
	var timestamp_begin := gpu_timestamp_begin_marker
	var timestamp_end := gpu_timestamp_end_marker
	_mutex.unlock()
	if not active or data.size() != PAYLOAD_SIZE - CAMERA_BYTES or gust == null or occ == null or counts.size() < 4: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var gust_rid := RenderingServer.texture_get_rd_texture(gust.get_rid())
	var occ_rid := RenderingServer.texture_get_rd_texture(occ.get_rid())
	if not gust_rid.is_valid() or not occ_rid.is_valid(): return
	var bytes := PackedByteArray(); bytes.resize(PAYLOAD_SIZE)
	var projection: Projection = scene.get_cam_projection()
	var inverse: Projection = projection.inverse()
	var offset := 0
	for matrix in [projection, inverse]:
		for column in [matrix.x, matrix.y, matrix.z, matrix.w]:
			for value in [column.x, column.y, column.z, column.w]: bytes.encode_float(offset, value); offset += 4
	var camera: Transform3D = scene.get_cam_transform()
	var camera_axes: Array[Vector3] = [camera.basis.x, camera.basis.y, camera.basis.z, camera.origin]
	for index in 4:
		var axis := camera_axes[index]
		bytes.encode_float(offset, axis.x); bytes.encode_float(offset + 4, axis.y); bytes.encode_float(offset + 8, axis.z); bytes.encode_float(offset + 12, 1.0 if index == 3 else 0.0); offset += 16
	bytes.encode_float(offset, float(size.x)); bytes.encode_float(offset + 4, float(size.y)); bytes.encode_float(offset + 8, 0.0); bytes.encode_float(offset + 12, 0.0); offset += 16
	for i in data.size(): bytes[CAMERA_BYTES + i] = data[i]
	var timestamp_started := false
	for view in buffers.get_view_count():
		var color := buffers.get_color_layer(view, false)
		var depth := buffers.get_depth_layer(view, false)
		if not color.is_valid() or not depth.is_valid(): continue
		var framebuffer := FramebufferCacheRD.get_cache_multipass([color], [], 1)
		if not framebuffer.is_valid() or not _ensure_pipeline(framebuffer): continue
		rd.buffer_update(buffer, 0, PAYLOAD_SIZE, bytes)
		var ubo := RDUniform.new(); ubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; ubo.binding = 0; ubo.add_id(buffer)
		var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(depth_sampler); d.add_id(depth)
		var g := RDUniform.new(); g.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; g.binding = 2; g.add_id(gust_sampler); g.add_id(gust_rid)
		var m := RDUniform.new(); m.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; m.binding = 3; m.add_id(map_sampler); m.add_id(occ_rid)
		var s := RDUniform.new(); s.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; s.binding = 4; s.add_id(storm_sampler); s.add_id(storm if storm.is_valid() else storm_fallback)
		var set := UniformSetCacheRD.get_cache(shader, 0, [ubo, d, g, m, s])
		if not set.is_valid(): continue
		if not timestamp_started and timestamps and timestamp_begin != "" and timestamp_end != "":
			rd.capture_timestamp(timestamp_begin)
			timestamp_started = true
		var list := rd.draw_list_begin(framebuffer, RenderingDevice.DRAW_DEFAULT_ALL)
		rd.draw_list_bind_render_pipeline(list, pipeline)
		rd.draw_list_bind_uniform_set(list, set, 0)
		# Draw order: streaks (mid then near), splashes (ring 6 + crown 72 vertices), then the S2 drip class (mode 2, see drip_vertex in the shader). Push constant: mode, layer.
		var draw_order := [[0, 1, counts[1], 6], [0, 0, counts[0], 6], [1, 1, counts[3], 78], [1, 0, counts[2], 78]]
		if counts.size() >= 6 and counts[4] > 0 and counts[5] > 0: draw_order.append([2, 0, counts[4], counts[5]])
		for item in draw_order:
			if item[2] <= 0: continue
			var push := PackedInt32Array([item[0], item[1], 0, 0]).to_byte_array()
			rd.draw_list_set_push_constant(list, push, push.size())
			rd.draw_list_draw(list, false, item[2], item[3])
			dispatches += 1
		rd.draw_list_end()
	if timestamp_started:
		rd.capture_timestamp(timestamp_end)
		timestamp_scopes += 1
