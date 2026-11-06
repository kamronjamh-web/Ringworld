extends CompositorEffect
## Containment field post-Sunshine pass (docs/containment-field-design.md 3.7; decision 7 of 2026-11-04: the field is built for flight). While the
## camera is ABOVE the field, every cloud is behind the field, but Sunshine composites after the transparent pass and would overwrite the shimmer of the
## spatial patches. RingContainmentField then hides its MeshInstance3Ds and publishes the same patches here: POST_TRANSPARENT, ordered after Sunshine and the
## lightning channel overlay and before the rainbow, rain streaks and rain haze (RingContainmentField.EnsureCompositorSlot). Per frame: copy the resolved
## colour ONCE, then rasterise onto it with premultiplied blending, testing the opaque depth by hand: the near patch (ring_containment_field_post.glsl, with
## its up to 4 integrated ripples) and one draw per active ripple patch slot (ring_containment_ripple_post.glsl, WO4).
## The two activities are independent flags (near: publish_frame, ripples: publish_ripples). No GPU work at all while neither is active.
## Near UBO (std140) 560 bytes = 176 render-thread bytes (proj, inv_proj, camera basis x/y/z) + 32 mixed (anchor - camera + quads, viewport size + max px + debug)
## + 352 CPU bytes (params, noise, lattice, to_sun, up, sun, sky, ground, ambient, ripple_info, ripples[12]: RingContainmentField.PublishPostFrame / PublishNearRipplesPost).
## Near params array: PARAM_FLOATS = 92 floats: [0] max px, [1] debug, [2,3] pad, [4..39] the 9 vec4 params..ambient, [40..43] ripple_info (x count 0..4, y ripple max px),
## [44..91] ripples[12] (per ripple: wave, crawl, (centre uv x, y, metric k, strike level)).
## Near vertex records: 48 bytes, vec4(position anchor-relative, uv2.x) vec4(normal, uv.x) vec4(around tangent, uv.y); keep in sync with RingContainmentField.cs.
## Ripple UBO (std140) 320 bytes = 176 camera + viewport (size xy, ripple max px, debug) + params (glass, lattice, near range N, near active) + far_fade (start, end)
## + to_sun + up + sun + sky + ground + flow (seam flow phase, seam energy scale); shared array SHARED_FLOATS = 36: [0] max px, [1] debug, [2,3] pad, [4..35] those 8 vec4 from params on.
## Ripple vertex records: 16 slots x 1089 (33 x 33) vertices x 48 bytes, slot s at s x 1089 (same layout as the near records). Slot records (SSBO, 16 x 80 bytes):
## anchor minus camera xyz + quads, wave, crawl, frame (lattice origin xy, metric k, strike level), flags (near cut 0/1, 0, 0, 0); slot_params = 16 floats per slot (wave, crawl, frame, flags).
const CAMERA_BYTES := 176
const PARAM_BYTES := 384
const PARAM_FLOATS := 92
const UBO_SIZE := CAMERA_BYTES + PARAM_BYTES
const VERTEX_BYTES := 48
const RIPPLE_SLOTS := 16
const RIPPLE_MAX_QUADS := 32
const RIPPLE_STRIDE := (RIPPLE_MAX_QUADS + 1) * (RIPPLE_MAX_QUADS + 1)   # vertices per slot
const SLOT_PARAM_FLOATS := 16
const SLOT_BYTES := 80
const SHARED_FLOATS := 36
const RIPPLE_UBO_SIZE := CAMERA_BYTES + 144
var _mutex := Mutex.new()
var _vertices := PackedByteArray()
var _quads := 0
var _vertices_dirty := false
var _anchor_world := Vector3.ZERO
var _params := PackedFloat32Array()   # PARAM_FLOATS, see the header
var _active := false
var _ripples_active := false
var _ripple_anchors := PackedVector3Array()
var _ripple_params := PackedFloat32Array()
var _ripple_shared := PackedFloat32Array()
var _ripple_slots := PackedInt32Array()
var _ripple_dirty := {}   # slot -> [PackedByteArray, quads]; published on a patch rebuild only
var _ripple_quads := PackedInt32Array()   # render thread: quads of the records last uploaded per slot (0 = none)
var ready := false
var dispatches := 0
var ripple_dispatches := 0
var last_error := ""
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var pipeline_format := -1
var ripple_shader: RID
var ripple_pipeline: RID
var ripple_pipeline_format := -1
var copy_shader: RID
var copy_pipeline: RID
var depth_sampler: RID
var linear_sampler: RID
var ubo_buffer: RID
var ripple_ubo: RID
var vertex_buffer: RID
var vertex_capacity := 0
var vertex_count := 0
var ripple_vertex_buffer: RID
var slot_buffer: RID
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

## anchor_world: the patch anchor's world position this frame; params: PARAM_FLOATS floats (see the header); active false skips the near patch's GPU calls.
func publish_frame(anchor_world: Vector3, params: PackedFloat32Array, active: bool) -> void:
	_mutex.lock()
	_anchor_world = anchor_world; _params = params; _active = active
	_mutex.unlock()

## One ripple slot's vertex records (VERTEX_BYTES x (quads + 1)^2, quads <= RIPPLE_MAX_QUADS); call on a ripple patch rebuild only.
func publish_ripple_mesh(slot: int, vertices: PackedByteArray, quads: int) -> void:
	if slot < 0 or slot >= RIPPLE_SLOTS or quads <= 0 or quads > RIPPLE_MAX_QUADS: return
	_mutex.lock()
	_ripple_dirty[slot] = [vertices, quads]
	_mutex.unlock()

## anchors_world: RIPPLE_SLOTS anchor positions (world); slot_params: RIPPLE_SLOTS x SLOT_PARAM_FLOATS; shared_params: SHARED_FLOATS; active_slots: the slots to draw.
## active false skips every ripple GPU call.
func publish_ripples(active: bool, anchors_world: PackedVector3Array, slot_params: PackedFloat32Array, shared_params: PackedFloat32Array, active_slots: PackedInt32Array) -> void:
	_mutex.lock()
	_ripples_active = active
	_ripple_anchors = anchors_world; _ripple_params = slot_params; _ripple_shared = shared_params; _ripple_slots = active_slots
	_mutex.unlock()

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [pipeline, shader, ripple_pipeline, ripple_shader, copy_pipeline, copy_shader, depth_sampler, linear_sampler, ubo_buffer, ripple_ubo,
		vertex_buffer, ripple_vertex_buffer, slot_buffer, copy]
	RenderingServer.call_on_render_thread(func():
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	ready = false

func _make_pipeline(program: RID, framebuffer: RID) -> RID:
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
	return rd.render_pipeline_create(program, rd.framebuffer_get_format(framebuffer), RenderingDevice.INVALID_ID, RenderingDevice.RENDER_PRIMITIVE_TRIANGLES, raster, multisample, depth_stencil, blend)

func _ensure_pipeline(framebuffer: RID) -> bool:
	var format := rd.framebuffer_get_format(framebuffer)
	if pipeline.is_valid() and pipeline_format == format: return true
	if pipeline.is_valid(): rd.free_rid(pipeline); pipeline = RID()
	pipeline = _make_pipeline(shader, framebuffer)
	pipeline_format = format
	return pipeline.is_valid()

func _ensure_ripple_pipeline(framebuffer: RID) -> bool:
	var format := rd.framebuffer_get_format(framebuffer)
	if ripple_pipeline.is_valid() and ripple_pipeline_format == format: return true
	if ripple_pipeline.is_valid(): rd.free_rid(ripple_pipeline); ripple_pipeline = RID()
	ripple_pipeline = _make_pipeline(ripple_shader, framebuffer)
	ripple_pipeline_format = format
	return ripple_pipeline.is_valid()

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

func _ensure_common() -> bool:
	if copy_shader.is_valid(): return true
	copy_shader = _load_shader("res://scripts/render/ring_containment_field_copy.glsl", false)
	if not copy_shader.is_valid(): return false
	copy_pipeline = rd.compute_pipeline_create(copy_shader)
	depth_sampler = rd.sampler_create(RDSamplerState.new())
	var linear := RDSamplerState.new(); linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear_sampler = rd.sampler_create(linear)
	return true

func _ensure_near() -> bool:
	if shader.is_valid(): return true
	shader = _load_shader("res://scripts/render/ring_containment_field_post.glsl", true)
	if not shader.is_valid(): return false
	ubo_buffer = rd.uniform_buffer_create(UBO_SIZE)
	return true

func _ensure_ripples() -> bool:
	if ripple_shader.is_valid(): return true
	ripple_shader = _load_shader("res://scripts/render/ring_containment_ripple_post.glsl", true)
	if not ripple_shader.is_valid(): return false
	ripple_ubo = rd.uniform_buffer_create(RIPPLE_UBO_SIZE)
	ripple_vertex_buffer = rd.storage_buffer_create(RIPPLE_SLOTS * RIPPLE_STRIDE * VERTEX_BYTES)
	slot_buffer = rd.storage_buffer_create(RIPPLE_SLOTS * SLOT_BYTES)
	_ripple_quads.resize(RIPPLE_SLOTS); _ripple_quads.fill(0)
	return true

## Uploads the near patch's vertex records; false when the buffer has never received a complete patch.
func _prepare_near(quads: int, vertices: PackedByteArray, upload: bool) -> bool:
	var needed := (quads + 1) * (quads + 1)
	if needed > vertex_capacity:
		if vertex_buffer.is_valid(): rd.free_rid(vertex_buffer)
		vertex_buffer = rd.storage_buffer_create(needed * VERTEX_BYTES)
		vertex_capacity = needed
		upload = true
	if not vertex_buffer.is_valid(): return false
	if upload and vertices.size() >= needed * VERTEX_BYTES:
		rd.buffer_update(vertex_buffer, 0, needed * VERTEX_BYTES, vertices)
		vertex_count = needed
	return vertex_count >= needed

func _upload_ripple_meshes() -> void:
	_mutex.lock()
	var dirty := _ripple_dirty
	_ripple_dirty = {}
	_mutex.unlock()
	for slot in dirty.keys():
		var entry: Array = dirty[slot]
		var bytes: PackedByteArray = entry[0]
		var q: int = entry[1]
		var needed := (q + 1) * (q + 1) * VERTEX_BYTES
		if bytes.size() < needed: continue
		rd.buffer_update(ripple_vertex_buffer, slot * RIPPLE_STRIDE * VERTEX_BYTES, needed, bytes)
		_ripple_quads[slot] = q

func _restore_upload(upload: bool) -> void:
	if not upload: return
	_mutex.lock(); _vertices_dirty = true; _mutex.unlock()

func _render_callback(_type: int, render_data: RenderData) -> void:
	_mutex.lock()
	var near_active := _active; var params := _params; var quads := _quads; var anchor := _anchor_world
	var vertices := _vertices; var upload := _vertices_dirty
	_vertices_dirty = false
	var ripples_on := _ripples_active; var ripple_anchors := _ripple_anchors; var ripple_params := _ripple_params
	var ripple_shared := _ripple_shared; var ripple_slots := _ripple_slots
	_mutex.unlock()
	var near_ok := near_active and quads > 0 and params.size() == PARAM_FLOATS
	var ripples_ok := ripples_on and ripple_slots.size() > 0 and ripple_shared.size() == SHARED_FLOATS \
		and ripple_params.size() == RIPPLE_SLOTS * SLOT_PARAM_FLOATS and ripple_anchors.size() == RIPPLE_SLOTS
	if not near_ok and not ripples_ok:
		_restore_upload(upload)
		return
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: _restore_upload(upload); return
	if not _ensure_common(): _restore_upload(upload); return
	if near_ok and not _ensure_near(): near_ok = false
	if ripples_ok and not _ensure_ripples(): ripples_ok = false
	if not near_ok and not ripples_ok: _restore_upload(upload); return
	ready = true
	if near_ok:
		if not _prepare_near(quads, vertices, upload): near_ok = false
	else:
		_restore_upload(upload)
	var drawable := PackedInt32Array()
	if ripples_ok:
		_upload_ripple_meshes()
		for slot in ripple_slots:
			if slot >= 0 and slot < RIPPLE_SLOTS and _ripple_quads[slot] > 0: drawable.append(slot)
		if drawable.is_empty(): ripples_ok = false
	if not near_ok and not ripples_ok: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var camera_bytes := PackedByteArray(); camera_bytes.resize(CAMERA_BYTES)
	var projection: Projection = scene.get_cam_projection()
	var inverse: Projection = projection.inverse()
	var offset := 0
	for matrix in [projection, inverse]:
		for column in [matrix.x, matrix.y, matrix.z, matrix.w]:
			for value in [column.x, column.y, column.z, column.w]: camera_bytes.encode_float(offset, value); offset += 4
	var camera: Transform3D = scene.get_cam_transform()
	for axis in [camera.basis.x, camera.basis.y, camera.basis.z]:
		camera_bytes.encode_float(offset, axis.x); camera_bytes.encode_float(offset + 4, axis.y); camera_bytes.encode_float(offset + 8, axis.z); camera_bytes.encode_float(offset + 12, 0.0); offset += 16
	if near_ok:
		var bytes := camera_bytes.duplicate(); bytes.resize(UBO_SIZE)
		offset = CAMERA_BYTES
		var rel := anchor - camera.origin
		for value in [rel.x, rel.y, rel.z, float(quads), float(size.x), float(size.y), params[0], params[1]]:
			bytes.encode_float(offset, value); offset += 4
		for i in range(4, PARAM_FLOATS):
			bytes.encode_float(offset, params[i]); offset += 4
		rd.buffer_update(ubo_buffer, 0, UBO_SIZE, bytes)
	if ripples_ok:
		var rbytes := camera_bytes.duplicate(); rbytes.resize(RIPPLE_UBO_SIZE)
		offset = CAMERA_BYTES
		for value in [float(size.x), float(size.y), ripple_shared[0], ripple_shared[1]]:
			rbytes.encode_float(offset, value); offset += 4
		for i in range(4, SHARED_FLOATS):
			rbytes.encode_float(offset, ripple_shared[i]); offset += 4
		rd.buffer_update(ripple_ubo, 0, RIPPLE_UBO_SIZE, rbytes)
		var slot_floats := PackedFloat32Array(); slot_floats.resize(RIPPLE_SLOTS * SLOT_BYTES / 4)
		for slot in drawable:
			var slot_rel := ripple_anchors[slot] - camera.origin
			var at: int = slot * (SLOT_BYTES / 4)
			slot_floats[at] = slot_rel.x; slot_floats[at + 1] = slot_rel.y; slot_floats[at + 2] = slot_rel.z; slot_floats[at + 3] = float(_ripple_quads[slot])
			for j in SLOT_PARAM_FLOATS: slot_floats[at + 4 + j] = ripple_params[slot * SLOT_PARAM_FLOATS + j]
		var slot_bytes := slot_floats.to_byte_array()
		rd.buffer_update(slot_buffer, 0, slot_bytes.size(), slot_bytes)
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
		var framebuffer := FramebufferCacheRD.get_cache_multipass([color], [], 1)
		if not framebuffer.is_valid(): continue
		var draw_near := near_ok and _ensure_pipeline(framebuffer)
		var draw_ripples := ripples_ok and _ensure_ripple_pipeline(framebuffer)
		if not draw_near and not draw_ripples: continue
		var near_set := RID()
		var ripple_set := RID()
		var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(depth_sampler); d.add_id(depth)
		var sc := RDUniform.new(); sc.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; sc.binding = 3; sc.add_id(linear_sampler); sc.add_id(copy)
		if draw_near:
			var ubo := RDUniform.new(); ubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; ubo.binding = 0; ubo.add_id(ubo_buffer)
			var vb := RDUniform.new(); vb.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; vb.binding = 2; vb.add_id(vertex_buffer)
			near_set = UniformSetCacheRD.get_cache(shader, 0, [ubo, d, vb, sc])
			draw_near = near_set.is_valid()
		if draw_ripples:
			var rubo := RDUniform.new(); rubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; rubo.binding = 0; rubo.add_id(ripple_ubo)
			var rvb := RDUniform.new(); rvb.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; rvb.binding = 2; rvb.add_id(ripple_vertex_buffer)
			var slots := RDUniform.new(); slots.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; slots.binding = 4; slots.add_id(slot_buffer)
			ripple_set = UniformSetCacheRD.get_cache(ripple_shader, 0, [rubo, d, rvb, sc, slots])
			draw_ripples = ripple_set.is_valid()
		if not draw_near and not draw_ripples: continue
		# One copy of the colour serves both draws: the patches refract the scene as it was after Sunshine, never each other.
		var compute := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(compute, copy_pipeline)
		rd.compute_list_bind_uniform_set(compute, copy_set, 0)
		var push := PackedInt32Array([0, 0, size.x, size.y]).to_byte_array()
		rd.compute_list_set_push_constant(compute, push, push.size())
		rd.compute_list_dispatch(compute, (size.x + 7) / 8, (size.y + 7) / 8, 1)
		rd.compute_list_end()
		var list := rd.draw_list_begin(framebuffer, RenderingDevice.DRAW_DEFAULT_ALL)
		if draw_near:
			rd.draw_list_bind_render_pipeline(list, pipeline)
			rd.draw_list_bind_uniform_set(list, near_set, 0)
			rd.draw_list_draw(list, false, quads * quads, 6)
			dispatches += 1
		if draw_ripples:
			rd.draw_list_bind_render_pipeline(list, ripple_pipeline)
			rd.draw_list_bind_uniform_set(list, ripple_set, 0)
			for slot in drawable:
				var slot_push := PackedInt32Array([slot, 0, 0, 0]).to_byte_array()
				rd.draw_list_set_push_constant(list, slot_push, slot_push.size())
				rd.draw_list_draw(list, false, _ripple_quads[slot] * _ripple_quads[slot], 6)
				ripple_dispatches += 1
		rd.draw_list_end()
