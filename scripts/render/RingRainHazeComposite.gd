extends CompositorEffect
## Applies rain once, after cloud providers, to the resolved HDR scene.
var _mutex := Mutex.new()
var _frame := PackedByteArray()
var _gust: Texture2D
var _active := false
var ready := false
var dispatches := 0
@export var gpu_timestamp_diagnostics_enabled := false
@export var gpu_timestamp_begin_marker := ""
@export var gpu_timestamp_end_marker := ""
var timestamp_scopes := 0
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var sampler: RID
var gust_sampler: RID
var views: Array[Dictionary] = []
var retired: Array[Dictionary] = []

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

func publish_frame(data: PackedByteArray, texture: Texture2D, active: bool) -> void:
	_mutex.lock()
	_frame = data; _gust = texture; _active = active
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
	var sets: Array[RID] = []
	var resources: Array[RID] = [pipeline, shader, sampler, gust_sampler]
	for view in views:
		sets.append(view.get("set", RID())); resources.append(view.get("buffer", RID()))
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
		var file: RDShaderFile = load("res://scripts/render/ring_rain_haze_composite.glsl")
		if file == null: return
		shader = rd.shader_create_from_spirv(file.get_spirv())
		if not shader.is_valid(): return
		pipeline = rd.compute_pipeline_create(shader)
		if not pipeline.is_valid():
			ready = false
			rd.free_rid(shader); shader = RID()
			return
		sampler = rd.sampler_create(RDSamplerState.new())
		var repeat := RDSamplerState.new(); repeat.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT; repeat.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
		repeat.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; repeat.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		gust_sampler = rd.sampler_create(repeat)
	if not pipeline.is_valid(): return
	ready = true
	_mutex.lock()
	var data := _frame; var texture := _gust; var active := _active
	var timestamps := gpu_timestamp_diagnostics_enabled
	var timestamp_begin := gpu_timestamp_begin_marker
	var timestamp_end := gpu_timestamp_end_marker
	_mutex.unlock()
	if not active or data.size() != 240 or texture == null: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var inverse: Projection = scene.get_cam_projection().inverse()
	var offset := 0
	for column in [inverse.x, inverse.y, inverse.z, inverse.w]:
		for value in [column.x,column.y,column.z,column.w]: data.encode_float(offset,value); offset += 4
	var gust_rid := RenderingServer.texture_get_rd_texture(texture.get_rid())
	if not gust_rid.is_valid(): return
	while views.size() < buffers.get_view_count(): views.append({"buffer": RID(),"set": RID(),"color": RID(),"depth": RID(),"gust": RID()})
	var timestamp_started := false
	for index in buffers.get_view_count():
		var view := views[index]
		var buffer: RID = view["buffer"]
		if not buffer.is_valid(): view["buffer"] = rd.uniform_buffer_create(240,data)
		else: rd.buffer_update(view["buffer"],0,240,data)
		var color := buffers.get_color_layer(index,false)
		var depth := buffers.get_depth_layer(index,false)
		if not color.is_valid() or not depth.is_valid(): continue
		var set: RID = view["set"]
		if not set.is_valid() or not rd.uniform_set_is_valid(set) or view["color"] != color or view["depth"] != depth or view["gust"] != gust_rid:
			if set.is_valid(): retired.append({"set":set,"frames":max(2,rd.get_frame_delay()+2)})
			var uniforms: Array[RDUniform] = []
			var target := RDUniform.new(); target.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; target.binding = 0; target.add_id(color); uniforms.append(target)
			var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(sampler); d.add_id(depth); uniforms.append(d)
			var g := RDUniform.new(); g.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; g.binding = 2; g.add_id(gust_sampler); g.add_id(gust_rid); uniforms.append(g)
			var b := RDUniform.new(); b.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; b.binding = 3; b.add_id(view["buffer"]); uniforms.append(b)
			set = rd.uniform_set_create(uniforms,shader,0); view["set"] = set; view["color"] = color; view["depth"] = depth; view["gust"] = gust_rid
		if not set.is_valid(): continue
		# One interval spans all dispatched views, outside active compute lists.
		# A dry frame or a frame with no valid views emits no owned markers.
		if not timestamp_started and timestamps and timestamp_begin != "" and timestamp_end != "":
			rd.capture_timestamp(timestamp_begin)
			timestamp_started = true
		var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list,pipeline); rd.compute_list_bind_uniform_set(list,set,0); rd.compute_list_dispatch(list,(size.x+7)/8,(size.y+7)/8,1); rd.compute_list_end()
		dispatches += 1
	if timestamp_started:
		rd.capture_timestamp(timestamp_end)
		timestamp_scopes += 1
