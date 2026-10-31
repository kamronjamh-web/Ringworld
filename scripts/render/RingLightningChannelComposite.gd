extends CompositorEffect
## Megastorm lightning channel overlay: crisp HDR bolt lines drawn onto the resolved scene colour AFTER the Sunshine cloud composite (POST_TRANSPARENT, ordered Sunshine,
## this effect, rain streaks, rain haze by RingLocalWeatherEffects.LightningChannels.cs). Sunshine replaces the whole resolved colour by cloud density, so a spatial bolt
## mesh is lost behind any cloud; this pass draws on top, tests terrain depth by hand, and fades by the cloud Sunshine itself marched IN FRONT of the bolt (its
## accumulation data and colour textures). No dispatch at all while inactive or empty.
## UBO (std140) 416 bytes = 224 render-thread bytes (proj, inv_proj, camera basis, viewport, sun flags) + 192 CPU bytes (slot intensities x4 vec4: 8 bolts + 8 scythe cores, tip fractions / bolt stroke index x4 vec4, radiance, min px multipliers, core colour, glow colour); keep in sync with
## ring_lightning_channel.glsl and RingLocalWeatherEffects.LightningChannels.cs. Segment records: 48 bytes, vec4(A.xyz, widthA) vec4(B.xyz, widthB) vec4(intensity, slot, alongA, alongB).
const CAMERA_BYTES := 224
const PARAM_BYTES := 192
const UBO_SIZE := CAMERA_BYTES + PARAM_BYTES
const SEGMENT_BYTES := 48
var _mutex := Mutex.new()
var _segments := PackedByteArray()
var _segment_count := 0
var _params := PackedByteArray()
var _active := false
var _dirty := false
var _upload_pending := false  # render thread: new segment bytes not yet in the storage buffer
var _sunshine_ref: WeakRef
var ready := false
var dispatches := 0
var last_error := ""
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var pipeline_format := -1
var depth_sampler: RID
var linear_sampler: RID
var ubo_buffer: RID
var segment_buffer: RID
var segment_capacity := 0
var sun_fallback: RID

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

## segments: SEGMENT_BYTES per segment, or an empty array to keep the previously uploaded segments (only the params change); params: PARAM_BYTES; active false skips every GPU call.
func publish_frame(segments: PackedByteArray, segment_count: int, params: PackedByteArray, active: bool) -> void:
	_mutex.lock()
	if segments.size() > 0 or segment_count == 0:
		_segments = segments; _segment_count = segment_count; _dirty = true
	_params = params; _active = active
	_mutex.unlock()

func set_sunshine(effect: Object) -> void:
	_mutex.lock()
	_sunshine_ref = weakref(effect) if effect != null else null
	_mutex.unlock()

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [pipeline, shader, depth_sampler, linear_sampler, ubo_buffer, segment_buffer, sun_fallback]
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

func _far_depth_rid(sunshine: Object) -> RID:
	if sunshine == null: return RID()
	var tex = sunshine.get("far_opaque_depth_texture")
	if tex == null or not (tex is Texture2DRD): return RID()
	var rid: RID = (tex as Texture2DRD).texture_rd_rid
	if not rid.is_valid() or not rd.texture_is_valid(rid): return RID()
	return rid

func _sunshine_textures(sunshine: Object, view: int) -> Array[RID]:
	var none: Array[RID] = []
	if sunshine == null or not bool(sunshine.get("enabled")): return none
	var textures = sunshine.get("accumulation_textures")
	if textures == null or textures.size() < view * 7 + 2: return none
	var data: RID = textures[view * 7]
	var color: RID = textures[view * 7 + 1]
	if not data.is_valid() or not color.is_valid() or not rd.texture_is_valid(data) or not rd.texture_is_valid(color): return none
	if not (rd.texture_get_format(data).usage_bits & RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT): return none
	if not (rd.texture_get_format(color).usage_bits & RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT): return none
	return [data, color]

func _render_callback(_type: int, render_data: RenderData) -> void:
	_mutex.lock()
	var active := _active; var count := _segment_count; var params := _params
	var segments := _segments
	if _dirty: _upload_pending = true; _dirty = false
	var sunshine: Object = _sunshine_ref.get_ref() if _sunshine_ref != null else null
	_mutex.unlock()
	if not active or count <= 0 or params.size() != PARAM_BYTES: return
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	if not shader.is_valid():
		var file: RDShaderFile = load("res://scripts/render/ring_lightning_channel.glsl")
		if file == null: last_error = "shader file missing"; return
		var spirv := file.get_spirv()
		if spirv.compile_error_vertex != "" or spirv.compile_error_fragment != "":
			last_error = "compile: " + spirv.compile_error_vertex + spirv.compile_error_fragment; push_error("ring_lightning_channel.glsl " + last_error); return
		shader = rd.shader_create_from_spirv(spirv)
		if not shader.is_valid(): last_error = "shader create failed"; return
		depth_sampler = rd.sampler_create(RDSamplerState.new())
		var linear := RDSamplerState.new(); linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		linear_sampler = rd.sampler_create(linear)
		var format := RDTextureFormat.new()
		format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; format.width = 1; format.height = 1
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
		var zero := PackedByteArray(); zero.resize(16)
		sun_fallback = rd.texture_create(format, RDTextureView.new(), [zero])
		ubo_buffer = rd.uniform_buffer_create(UBO_SIZE)
	ready = true
	# Segment storage buffer: power-of-two growth; upload only when new bytes were published.
	if count > segment_capacity:
		var capacity := 64
		while capacity < count: capacity *= 2
		if segment_buffer.is_valid(): rd.free_rid(segment_buffer)
		segment_buffer = rd.storage_buffer_create(capacity * SEGMENT_BYTES)
		segment_capacity = capacity
		_upload_pending = true
	if not segment_buffer.is_valid(): return
	if _upload_pending and segments.size() >= count * SEGMENT_BYTES:
		rd.buffer_update(segment_buffer, 0, count * SEGMENT_BYTES, segments)
		_upload_pending = false
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var min_step := 400.0; var max_step := 500.0
	if sunshine != null:
		min_step = float(sunshine.get("min_step_distance")); max_step = float(sunshine.get("max_step_distance"))
	var bytes := PackedByteArray(); bytes.resize(UBO_SIZE)
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
	bytes.encode_float(offset, float(size.x)); bytes.encode_float(offset + 4, float(size.y)); bytes.encode_float(offset + 8, min_step); bytes.encode_float(offset + 12, max_step); offset += 16
	var sun_flags_offset := offset
	for i in params.size(): bytes[CAMERA_BYTES + i] = params[i]
	for view in buffers.get_view_count():
		var color := buffers.get_color_layer(view, false)
		var depth := buffers.get_depth_layer(view, false)
		if not color.is_valid() or not depth.is_valid(): continue
		var framebuffer := FramebufferCacheRD.get_cache_multipass([color], [], 1)
		if not framebuffer.is_valid() or not _ensure_pipeline(framebuffer): continue
		var sun := _sunshine_textures(sunshine, view)
		var have_sun := sun.size() == 2
		bytes.encode_float(sun_flags_offset, 1.0 if have_sun else 0.0)
		rd.buffer_update(ubo_buffer, 0, UBO_SIZE, bytes)
		var ubo := RDUniform.new(); ubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; ubo.binding = 0; ubo.add_id(ubo_buffer)
		var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(depth_sampler); d.add_id(depth)
		var seg := RDUniform.new(); seg.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; seg.binding = 2; seg.add_id(segment_buffer)
		var sd := RDUniform.new(); sd.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; sd.binding = 3; sd.add_id(linear_sampler); sd.add_id(sun[0] if have_sun else sun_fallback)
		var sc := RDUniform.new(); sc.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; sc.binding = 4; sc.add_id(linear_sampler); sc.add_id(sun[1] if have_sun else sun_fallback)
		# The scaled far viewport's opaque depth in metres (Sunshine's far_opaque_depth_texture, RingFarDepthCapture.gd): terrain and trees the far view
		# draws are not in the main depth buffer, so the fragment stage tests this too, as Sunshine's own composite does. 0 = none.
		var far_rid := _far_depth_rid(sunshine)
		var fd := RDUniform.new(); fd.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; fd.binding = 5; fd.add_id(depth_sampler); fd.add_id(far_rid if far_rid.is_valid() else sun_fallback)
		var set := UniformSetCacheRD.get_cache(shader, 0, [ubo, d, seg, sd, sc, fd])
		if not set.is_valid(): continue
		var list := rd.draw_list_begin(framebuffer, RenderingDevice.DRAW_DEFAULT_ALL)
		rd.draw_list_bind_render_pipeline(list, pipeline)
		rd.draw_list_bind_uniform_set(list, set, 0)
		rd.draw_list_draw(list, false, count, 6)
		rd.draw_list_end()
		dispatches += 1
