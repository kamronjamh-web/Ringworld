extends CompositorEffect
## Megastorm shock distortion (docs/megastorm-design.md 5.5.1, WO11): a short screen-space refraction band that follows the boom front across the world as it
## approaches and passes. Per pixel the shader rebuilds the world point (scene depth, or a 6 km ray for sky and far terrain), measures its distance behind the
## front along the storm's direction of travel and shifts the sampled colour along the projected travel direction inside a narrow lens band. It is dispatched only
## while RingMegastormHazardFx attaches it (the front within 6 km ahead to 1.5 km behind the observer) and does nothing at amplitude 0; "Reduced flashes" scales
## the amplitude down (MegastormHazardFx.ShockDistortionScale).
const FRAME_SIZE := 176  # inv_proj (64) + camera world matrix (64) + 3 vec4
var _mutex := Mutex.new()
var _params := PackedFloat32Array()  # 12 floats: n.xyz, front, origin.xyz, amplitude px, band width m, tail width m, 0, 0
var _active := false
var ready := false
var dispatches := 0
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var sampler: RID
var buffer: RID
var copy: RID
var copy_size := Vector2i.ZERO

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

func publish_frame(params: PackedFloat32Array, active: bool) -> void:
	_mutex.lock()
	_params = params; _active = active
	_mutex.unlock()

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [pipeline, shader, sampler, buffer, copy]
	RenderingServer.call_on_render_thread(func():
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	ready = false

func _render_callback(_type: int, render_data: RenderData) -> void:
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	if not shader.is_valid():
		var file: RDShaderFile = load("res://scripts/render/ring_shock_distortion.glsl")
		if file == null: return
		shader = rd.shader_create_from_spirv(file.get_spirv())
		if not shader.is_valid(): return
		pipeline = rd.compute_pipeline_create(shader)
		if not pipeline.is_valid():
			rd.free_rid(shader); shader = RID()
			return
		var linear := RDSamplerState.new(); linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		sampler = rd.sampler_create(linear)
		buffer = rd.uniform_buffer_create(FRAME_SIZE)
	ready = true
	_mutex.lock()
	var params := _params; var active := _active
	_mutex.unlock()
	if not active or params.size() != 12: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var bytes := PackedByteArray(); bytes.resize(FRAME_SIZE)
	var offset := 0
	var inverse: Projection = scene.get_cam_projection().inverse()
	for column in [inverse.x, inverse.y, inverse.z, inverse.w]:
		for value in [column.x, column.y, column.z, column.w]: bytes.encode_float(offset, value); offset += 4
	var camera: Transform3D = scene.get_cam_transform()
	var axes: Array[Vector3] = [camera.basis.x, camera.basis.y, camera.basis.z, camera.origin]
	for index in 4:
		var axis := axes[index]
		bytes.encode_float(offset, axis.x); bytes.encode_float(offset + 4, axis.y); bytes.encode_float(offset + 8, axis.z); bytes.encode_float(offset + 12, 1.0 if index == 3 else 0.0); offset += 16
	for value in params: bytes.encode_float(offset, value); offset += 4
	for view in buffers.get_view_count():
		var color := buffers.get_color_layer(view, false)
		var depth := buffers.get_depth_layer(view, false)
		if not color.is_valid() or not depth.is_valid(): continue
		if not copy.is_valid() or copy_size != size:
			if copy.is_valid(): rd.free_rid(copy)
			var f := RDTextureFormat.new()
			f.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT; f.width = size.x; f.height = size.y
			f.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT
			copy = rd.texture_create(f, RDTextureView.new())
			copy_size = size
		if not copy.is_valid(): continue
		rd.buffer_update(buffer, 0, FRAME_SIZE, bytes)
		var target := RDUniform.new(); target.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; target.binding = 0; target.add_id(color)
		var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(sampler); d.add_id(depth)
		var c := RDUniform.new(); c.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; c.binding = 2; c.add_id(copy)
		var b := RDUniform.new(); b.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; b.binding = 3; b.add_id(buffer)
		var set := UniformSetCacheRD.get_cache(shader, 0, [target, d, c, b])
		if not set.is_valid(): continue
		var list := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(list, pipeline)
		rd.compute_list_bind_uniform_set(list, set, 0)
		for pass_index in 2:
			var push := PackedInt32Array([pass_index, 0, 0, 0]).to_byte_array()
			rd.compute_list_set_push_constant(list, push, push.size())
			rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1)
			if pass_index == 0: rd.compute_list_add_barrier(list)
		rd.compute_list_end()
		dispatches += 1
