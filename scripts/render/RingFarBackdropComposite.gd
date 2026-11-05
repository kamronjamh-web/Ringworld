extends CompositorEffect
## Injects the scaled far viewport (linear HDR) into the main 3D colour buffer wherever the main pass drew nothing,
## so main's tonemap runs once and the root viewport can stay LDR 2D.
## While mask_wanted (the megastorm wedge pass is visible) view 0 also writes tile_mask, one r8 texel per 8x8 tile of the internal-size image
## (1 = the whole tile has main depth); get_mask_texture() hands its RID, size and write counter to the far pass, which skips pixels main will cover.
var _mutex := Mutex.new()
var _far: Texture2D
var _regional: Texture2D
var _active := false
var ready := false
var dispatches := 0
## FSR2/TAA jitter compensation: the far layer is rendered unjittered, so it is sampled at uv + 0.5 * projection z.xy (0 without jitter).
var jitter_compensation := true
var jitter_uv_sign := 1.0 # debug: multiplies the applied offset
var last_uv_offset := Vector2.ZERO
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var sampler: RID
var views: Array[Dictionary] = []
var retired: Array[Dictionary] = []
var _mask_wanted := false
var _mask_rid := RID()
var _mask_size := Vector2i.ZERO
var _mask_writes := 0
var _retired_masks: Array[Dictionary] = []
var _mask_wrapper: Texture2DRD
var _mask_wrapper_rid := RID()

func _init() -> void:
	# After the sky pass (which paints nothing for BG_COLOR), before transparents so clouds/water blend over it.
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_SKY
	access_resolved_color = true
	access_resolved_depth = true

func publish(far: Texture2D, regional: Texture2D, active: bool) -> void:
	_mutex.lock()
	_far = far; _regional = regional; _active = active
	_mutex.unlock()

func set_mask_wanted(wanted: bool) -> void:
	_mutex.lock()
	_mask_wanted = wanted
	_mutex.unlock()

## Main thread. [Texture2DRD wrapper of the tile mask or null, writes]: writes counts the frames the mask was written (a stale mask does not
## advance it); the wrapper is replaced whenever the render thread recreated the mask (internal size change).
func get_mask_texture() -> Array:
	_mutex.lock()
	var rid := _mask_rid
	var writes := _mask_writes
	_mutex.unlock()
	if not rid.is_valid():
		_mask_wrapper = null; _mask_wrapper_rid = RID()
	elif _mask_wrapper == null or _mask_wrapper_rid != rid:
		_mask_wrapper = Texture2DRD.new()
		_mask_wrapper.texture_rd_rid = rid
		_mask_wrapper_rid = rid
	return [_mask_wrapper, writes]

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var sets: Array[RID] = []
	var resources: Array[RID] = [pipeline, shader, sampler, _mask_rid]
	for item in _retired_masks: resources.append(item.get("rid", RID()))
	_mask_rid = RID(); _retired_masks.clear()
	for view in views: sets.append(view.get("set", RID()))
	for item in retired: sets.append(item.get("set", RID()))
	RenderingServer.call_on_render_thread(func():
		for set in sets:
			if set.is_valid() and device.uniform_set_is_valid(set): device.free_rid(set)
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	views.clear(); retired.clear(); ready = false

func _render_callback(_type: int, render_data: RenderData) -> void:
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	for index in range(retired.size() - 1, -1, -1):
		retired[index]["frames"] -= 1
		if retired[index]["frames"] <= 0:
			var old: RID = retired[index]["set"]
			if old.is_valid() and rd.uniform_set_is_valid(old): rd.free_rid(old)
			retired.remove_at(index)
	for index in range(_retired_masks.size() - 1, -1, -1):
		_retired_masks[index]["frames"] -= 1
		if _retired_masks[index]["frames"] <= 0:
			var old_mask: RID = _retired_masks[index]["rid"]
			if old_mask.is_valid(): rd.free_rid(old_mask)
			_retired_masks.remove_at(index)
	if not shader.is_valid():
		var file: RDShaderFile = load("res://scripts/render/ring_far_backdrop_composite.glsl")
		if file == null: return
		shader = rd.shader_create_from_spirv(file.get_spirv())
		if not shader.is_valid(): return
		pipeline = rd.compute_pipeline_create(shader)
		if not pipeline.is_valid():
			rd.free_rid(shader); shader = RID()
			return
		var state := RDSamplerState.new()
		state.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; state.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE; state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
		sampler = rd.sampler_create(state)
	ready = true
	_mutex.lock()
	var far := _far; var regional := _regional; var active := _active; var want_mask := _mask_wanted
	_mutex.unlock()
	if not active or far == null: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null: return
	var far_rid := RenderingServer.texture_get_rd_texture(far.get_rid())
	if not far_rid.is_valid(): return
	var regional_rid := far_rid
	var has_regional := 0.0
	if regional != null:
		var candidate := RenderingServer.texture_get_rd_texture(regional.get_rid())
		if candidate.is_valid(): regional_rid = candidate; has_regional = 1.0
	while views.size() < buffers.get_view_count(): views.append({"set": RID(), "color": RID(), "depth": RID(), "far": RID(), "regional": RID(), "mask": RID()})
	var jitter_uv := Vector2.ZERO
	if jitter_compensation:
		var cam_projection := render_data.get_render_scene_data().get_cam_projection()
		jitter_uv = Vector2(cam_projection.z.x, cam_projection.z.y) * (0.5 * jitter_uv_sign)
	last_uv_offset = jitter_uv
	var size := buffers.get_internal_size()
	var tiles := Vector2i((size.x + 7) / 8, (size.y + 7) / 8)
	if _mask_rid.is_valid() and tiles != _mask_size:
		_retired_masks.append({"rid": _mask_rid, "frames": max(4, rd.get_frame_delay() + 4)})
		_mutex.lock(); _mask_rid = RID(); _mask_size = Vector2i.ZERO; _mutex.unlock()
	if not _mask_rid.is_valid() and tiles.x > 0 and tiles.y > 0:
		var format := RDTextureFormat.new()
		format.format = RenderingDevice.DATA_FORMAT_R8_UNORM
		format.width = tiles.x; format.height = tiles.y
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_TO_BIT
		var created := rd.texture_create(format, RDTextureView.new())
		if created.is_valid():
			rd.texture_clear(created, Color(0, 0, 0, 0), 0, 1, 0, 1)
			_mutex.lock(); _mask_rid = created; _mask_size = tiles; _mutex.unlock()
	var mask_rid := _mask_rid
	if not mask_rid.is_valid(): return
	var mask_written := false
	for index in buffers.get_view_count():
		var view := views[index]
		var color := buffers.get_color_layer(index, false)
		var depth := buffers.get_depth_layer(index, false)
		if not color.is_valid() or not depth.is_valid(): continue
		var set: RID = view["set"]
		if not set.is_valid() or not rd.uniform_set_is_valid(set) or view["color"] != color or view["depth"] != depth or view["far"] != far_rid or view["regional"] != regional_rid or view.get("mask", RID()) != mask_rid:
			if set.is_valid(): retired.append({"set": set, "frames": max(2, rd.get_frame_delay() + 2)})
			var uniforms: Array[RDUniform] = []
			var target := RDUniform.new(); target.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; target.binding = 0; target.add_id(color); uniforms.append(target)
			for entry in [[1, depth], [2, far_rid], [3, regional_rid]]:
				var u := RDUniform.new(); u.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; u.binding = entry[0]; u.add_id(sampler); u.add_id(entry[1]); uniforms.append(u)
			var mask_uniform := RDUniform.new(); mask_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; mask_uniform.binding = 4; mask_uniform.add_id(mask_rid); uniforms.append(mask_uniform)
			set = rd.uniform_set_create(uniforms, shader, 0)
			view["set"] = set; view["color"] = color; view["depth"] = depth; view["far"] = far_rid; view["regional"] = regional_rid; view["mask"] = mask_rid
		if not set.is_valid(): continue
		# Only view 0 writes the mask (multiview is not used for gameplay).
		var write_mask := want_mask and index == 0 and mask_rid.is_valid()
		var push := PackedFloat32Array([has_regional, jitter_uv.x, jitter_uv.y, 1.0 if write_mask else 0.0]).to_byte_array()
		mask_written = mask_written or write_mask
		var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0)
		rd.compute_list_set_push_constant(list, push, push.size())
		rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
		dispatches += 1
	if mask_written:
		_mutex.lock(); _mask_writes += 1; _mutex.unlock()
