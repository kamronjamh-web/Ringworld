extends CompositorEffect
## Containment field near patch, post-Sunshine pass (docs/containment-field-design.md 3.7; decision 7 of 2026-11-04: the field is built for flight). While the
## camera is ABOVE the field, every cloud is behind the field, but Sunshine composites after the transparent pass and would overwrite the shimmer of the
## spatial patch. RingContainmentField then hides its MeshInstance3D and publishes the same patch here: POST_TRANSPARENT, ordered after Sunshine and the
## lightning channel overlay and before the rainbow, rain streaks and rain haze (RingContainmentField.EnsureCompositorSlot). Per frame: copy the resolved
## colour, then rasterise the patch onto it with premultiplied blending (ring_containment_field_post.glsl), testing the opaque depth by hand.
## No GPU work at all while inactive (camera below the field, out of range, field off).
## UBO (std140) 256 bytes = 176 render-thread bytes (proj, inv_proj, camera basis x/y/z) + 80 CPU bytes (anchor + quads, viewport/max px/debug, params, noise, lattice).
## Vertex records: 48 bytes, vec4(position anchor-relative, uv2.x) vec4(normal, uv.x) vec4(around tangent, uv.y); keep in sync with RingContainmentField.cs.
const CAMERA_BYTES := 176
const PARAM_BYTES := 80
const UBO_SIZE := CAMERA_BYTES + PARAM_BYTES
const VERTEX_BYTES := 48
var _mutex := Mutex.new()
var _vertices := PackedByteArray()
var _quads := 0
var _vertices_dirty := false
var _anchor_world := Vector3.ZERO
var _params := PackedFloat32Array()   # 16 floats: max px, debug mode, shimmer, sheen, range m, haze visibility, noise x4, lattice strength, lattice origin xy, 0, 0, 0
var _active := false
var ready := false
var dispatches := 0
var last_error := ""
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var pipeline_format := -1
var copy_shader: RID
var copy_pipeline: RID
var depth_sampler: RID
var linear_sampler: RID
var ubo_buffer: RID
var vertex_buffer: RID
var vertex_capacity := 0
var vertex_count := 0
var copy: RID
var copy_size := Vector2i.ZERO

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

## vertices: VERTEX_BYTES per vertex, (quads + 1)^2 vertices; call on a patch rebuild only.
func publish_mesh(vertices: PackedByteArray, quads: int) -> void:
	_mutex.lock()
	_vertices = vertices; _quads = quads; _vertices_dirty = true
	_mutex.unlock()

## anchor_world: the patch anchor's world position this frame; params: 16 floats (see _params); active false skips every GPU call.
func publish_frame(anchor_world: Vector3, params: PackedFloat32Array, active: bool) -> void:
	_mutex.lock()
	_anchor_world = anchor_world; _params = params; _active = active
	_mutex.unlock()

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [pipeline, shader, copy_pipeline, copy_shader, depth_sampler, linear_sampler, ubo_buffer, vertex_buffer, copy]
	RenderingServer.call_on_render_thread(func():
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	ready = false

func _ensure_pipeline(framebuffer: RID) -> bool:
	var format := rd.framebuffer_get_format(framebuffer)
	if pipeline.is_valid() and pipeline_format == format: return true
	if pipeline.is_valid(): rd.free_rid(pipeline); pipeline = RID()
	var raster := RDPipelineRasterizationState.new()
	raster.cull_mode = RenderingDevice.POLYGON_CULL_DISABLED
	var multisample := RDPipelineMultisampleState.new()
	var depth_stencil := RDPipelineDepthStencilState.new()
	var blend := RDPipelineColorBlendState.new()
	var attachment := RDPipelineColorBlendStateAttachment.new()
	attachment.enable_blend = true
	attachment.src_color_blend_factor = RenderingDevice.BLEND_FACTOR_ONE
	attachment.dst_color_blend_factor = RenderingDevice.BLEND_FACTOR_ONE_MINUS_SRC_ALPHA
	attachment.color_blend_op = RenderingDevice.BLEND_OP_ADD
	# Leave the scene alpha untouched: FSR2 reads it as the reactive mask.
	attachment.write_a = false
	blend.attachments.append(attachment)
	pipeline = rd.render_pipeline_create(shader, format, RenderingDevice.INVALID_ID, RenderingDevice.RENDER_PRIMITIVE_TRIANGLES, raster, multisample, depth_stencil, blend)
	pipeline_format = format
	return pipeline.is_valid()

func _load_shader(path: String, raster_stage: bool) -> RID:
	var file: RDShaderFile = load(path)
	if file == null: last_error = "shader file missing: " + path; return RID()
	var spirv := file.get_spirv()
	var errors := (spirv.compile_error_vertex + spirv.compile_error_fragment) if raster_stage else spirv.compile_error_compute
	if errors != "":
		last_error = "compile: " + errors; push_error(path.get_file() + " " + last_error); return RID()
	var rid := rd.shader_create_from_spirv(spirv)
	if not rid.is_valid(): last_error = "shader create failed: " + path
	return rid

func _render_callback(_type: int, render_data: RenderData) -> void:
	_mutex.lock()
	var active := _active; var params := _params; var quads := _quads; var anchor := _anchor_world
	var vertices := _vertices; var upload := _vertices_dirty
	_vertices_dirty = false
	_mutex.unlock()
	if not active or quads <= 0 or params.size() != 16:
		if upload: _mutex.lock(); _vertices_dirty = true; _mutex.unlock()
		return
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	if not shader.is_valid():
		shader = _load_shader("res://scripts/render/ring_containment_field_post.glsl", true)
		if not shader.is_valid(): return
		copy_shader = _load_shader("res://scripts/render/ring_containment_field_copy.glsl", false)
		if not copy_shader.is_valid(): return
		copy_pipeline = rd.compute_pipeline_create(copy_shader)
		depth_sampler = rd.sampler_create(RDSamplerState.new())
		var linear := RDSamplerState.new(); linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		linear_sampler = rd.sampler_create(linear)
		ubo_buffer = rd.uniform_buffer_create(UBO_SIZE)
	ready = true
	var needed := (quads + 1) * (quads + 1)
	if needed > vertex_capacity:
		if vertex_buffer.is_valid(): rd.free_rid(vertex_buffer)
		vertex_buffer = rd.storage_buffer_create(needed * VERTEX_BYTES)
		vertex_capacity = needed
		upload = true
	if not vertex_buffer.is_valid(): return
	if upload and vertices.size() >= needed * VERTEX_BYTES:
		rd.buffer_update(vertex_buffer, 0, needed * VERTEX_BYTES, vertices)
		vertex_count = needed
	if vertex_count < needed: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var bytes := PackedByteArray(); bytes.resize(UBO_SIZE)
	var projection: Projection = scene.get_cam_projection()
	var inverse: Projection = projection.inverse()
	var offset := 0
	for matrix in [projection, inverse]:
		for column in [matrix.x, matrix.y, matrix.z, matrix.w]:
			for value in [column.x, column.y, column.z, column.w]: bytes.encode_float(offset, value); offset += 4
	var camera: Transform3D = scene.get_cam_transform()
	for axis in [camera.basis.x, camera.basis.y, camera.basis.z]:
		bytes.encode_float(offset, axis.x); bytes.encode_float(offset + 4, axis.y); bytes.encode_float(offset + 8, axis.z); bytes.encode_float(offset + 12, 0.0); offset += 16
	var rel := anchor - camera.origin
	for value in [rel.x, rel.y, rel.z, float(quads), float(size.x), float(size.y), params[0], params[1],
			params[2], params[3], params[4], params[5], params[6], params[7], params[8], params[9], params[10], params[11], params[12], 0.0]:
		bytes.encode_float(offset, value); offset += 4
	rd.buffer_update(ubo_buffer, 0, UBO_SIZE, bytes)
	for view in buffers.get_view_count():
		var color := buffers.get_color_layer(view, false)
		var depth := buffers.get_depth_layer(view, false)
		if not color.is_valid() or not depth.is_valid(): continue
		if not copy.is_valid() or copy_size != size:
			if copy.is_valid(): rd.free_rid(copy)
			var f := RDTextureFormat.new()
			f.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT; f.width = size.x; f.height = size.y
			f.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
			copy = rd.texture_create(f, RDTextureView.new())
			copy_size = size
		if not copy.is_valid(): continue
		var src := RDUniform.new(); src.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; src.binding = 0; src.add_id(color)
		var dst := RDUniform.new(); dst.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; dst.binding = 1; dst.add_id(copy)
		var copy_set := UniformSetCacheRD.get_cache(copy_shader, 0, [src, dst])
		if not copy_set.is_valid(): continue
		var compute := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(compute, copy_pipeline)
		rd.compute_list_bind_uniform_set(compute, copy_set, 0)
		var push := PackedInt32Array([0, 0, size.x, size.y]).to_byte_array()
		rd.compute_list_set_push_constant(compute, push, push.size())
		rd.compute_list_dispatch(compute, (size.x + 7) / 8, (size.y + 7) / 8, 1)
		rd.compute_list_end()
		var framebuffer := FramebufferCacheRD.get_cache_multipass([color], [], 1)
		if not framebuffer.is_valid() or not _ensure_pipeline(framebuffer): continue
		var ubo := RDUniform.new(); ubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; ubo.binding = 0; ubo.add_id(ubo_buffer)
		var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(depth_sampler); d.add_id(depth)
		var vb := RDUniform.new(); vb.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; vb.binding = 2; vb.add_id(vertex_buffer)
		var sc := RDUniform.new(); sc.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; sc.binding = 3; sc.add_id(linear_sampler); sc.add_id(copy)
		var set := UniformSetCacheRD.get_cache(shader, 0, [ubo, d, vb, sc])
		if not set.is_valid(): continue
		var list := rd.draw_list_begin(framebuffer, RenderingDevice.DRAW_DEFAULT_ALL)
		rd.draw_list_bind_render_pipeline(list, pipeline)
		rd.draw_list_bind_uniform_set(list, set, 0)
		rd.draw_list_draw(list, false, quads * quads, 6)
		rd.draw_list_end()
		dispatches += 1
