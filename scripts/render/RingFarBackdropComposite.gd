extends CompositorEffect
## Injects the scaled far viewport (linear HDR) into the main 3D colour buffer wherever the main pass drew nothing,
## so main's tonemap runs once and the root viewport can stay LDR 2D.
var _mutex := Mutex.new()
var _far: Texture2D
var _regional: Texture2D
var _active := false
var ready := false
var dispatches := 0
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var sampler: RID
var views: Array[Dictionary] = []
var retired: Array[Dictionary] = []

func _init() -> void:
	# After the sky pass (which paints nothing for BG_COLOR), before transparents so clouds/water blend over it.
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_SKY
	access_resolved_color = true
	access_resolved_depth = true

func publish(far: Texture2D, regional: Texture2D, active: bool) -> void:
	_mutex.lock()
	_far = far; _regional = regional; _active = active
	_mutex.unlock()

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var sets: Array[RID] = []
	var resources: Array[RID] = [pipeline, shader, sampler]
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
	var far := _far; var regional := _regional; var active := _active
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
	while views.size() < buffers.get_view_count(): views.append({"set": RID(), "color": RID(), "depth": RID(), "far": RID(), "regional": RID()})
	var push := PackedFloat32Array([has_regional, 0.0, 0.0, 0.0]).to_byte_array()
	var size := buffers.get_internal_size()
	for index in buffers.get_view_count():
		var view := views[index]
		var color := buffers.get_color_layer(index, false)
		var depth := buffers.get_depth_layer(index, false)
		if not color.is_valid() or not depth.is_valid(): continue
		var set: RID = view["set"]
		if not set.is_valid() or not rd.uniform_set_is_valid(set) or view["color"] != color or view["depth"] != depth or view["far"] != far_rid or view["regional"] != regional_rid:
			if set.is_valid(): retired.append({"set": set, "frames": max(2, rd.get_frame_delay() + 2)})
			var uniforms: Array[RDUniform] = []
			var target := RDUniform.new(); target.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; target.binding = 0; target.add_id(color); uniforms.append(target)
			for entry in [[1, depth], [2, far_rid], [3, regional_rid]]:
				var u := RDUniform.new(); u.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; u.binding = entry[0]; u.add_id(sampler); u.add_id(entry[1]); uniforms.append(u)
			set = rd.uniform_set_create(uniforms, shader, 0)
			view["set"] = set; view["color"] = color; view["depth"] = depth; view["far"] = far_rid; view["regional"] = regional_rid
		if not set.is_valid(): continue
		var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0)
		rd.compute_list_set_push_constant(list, push, push.size())
		rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
		dispatches += 1
