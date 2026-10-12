extends RefCounted
# No readback: generation completion publishes GPU-resident textures only.
var _request := {}
var _completed := {}
var _rd: RenderingDevice
var _shader := RID()
var _pipeline := RID()
var _global_buffer := RID()
var _levels_buffer := RID()
var _regional_buffer := RID()
var _height_key := ""
var _regional_key := ""
var _textures: Array[RID] = []
var _sets: Array[RID] = []
var _params: Array[RID] = []
var _offsets := [0,0]
var _phase := 0
var _finished_wait := 0
var _released := false
var _frame := 0
var _published: Array[RID] = []
var _owned := {}
var _retired := []
var _diagnostics := {}
var _timestamps_enabled := false
var _timestamp_begin := ""
var _timestamp_end := ""
var _total_texels := 0
func get_diagnostics() -> Dictionary: return _diagnostics.duplicate()
func configure_diagnostics(enabled: bool, begin: String, end: String) -> void:
	RenderingServer.call_on_render_thread(_configure_diagnostics.bind(enabled,begin,end))
func _configure_diagnostics(enabled: bool, begin: String, end: String) -> void:
	_timestamps_enabled=enabled;_timestamp_begin=begin;_timestamp_end=end
func _publish_diagnostics(values: Dictionary) -> void: _diagnostics=values

func begin_generation(request: Dictionary) -> void:
	RenderingServer.call_on_render_thread(_begin.bind(request))
func advance(texels: int) -> void:
	RenderingServer.call_on_render_thread(_advance.bind(texels))
func take_completed() -> Dictionary:
	var result := _completed
	_completed = {}
	return result
func acknowledge_publication(_generation: int) -> void:
	RenderingServer.call_on_render_thread(_acknowledge)
func discard_generation() -> void:
	RenderingServer.call_on_render_thread(_discard)
func release() -> void:
	_released = true
	RenderingServer.call_on_render_thread(_release)

func _deliver(result: Dictionary) -> void:
	if not _released: _completed = result
func _error(message: String) -> void:
	call_deferred("_deliver", {"error":message})

func _begin(request: Dictionary) -> void:
	if _released: return
	_rd = RenderingServer.get_rendering_device()
	if _rd == null: _error("RenderingDevice unavailable"); return
	if not _shader.is_valid():
		var file := load("res://materials/ring_terrain_cloud_sun_generate.glsl") as RDShaderFile
		if file == null: _error("terrain sunlight compute import missing"); return
		_shader = _keep(_rd.shader_create_from_spirv(file.get_spirv()),"shader")
		_pipeline = _keep(_rd.compute_pipeline_create(_shader),"pipeline")
		if not _pipeline.is_valid(): _error("terrain sunlight pipeline invalid"); return
	_request = request
	var height_key := str(request.terrain_key)
	if height_key != _height_key:
		_retire([_global_buffer,_levels_buffer])
		var height_values: PackedFloat32Array = request.height
		var level_values: PackedFloat32Array = request.levels
		_global_buffer = _keep(_rd.storage_buffer_create(height_values.size()*4,height_values.to_byte_array()),"buffer")
		_levels_buffer = _keep(_rd.storage_buffer_create(level_values.size()*4,level_values.to_byte_array()),"buffer")
		_height_key = height_key
	var regional_key := "%s:%s:%s:%s" % [height_key,request.regional_center,request.regional_extent,request.regional_height_size]
	if regional_key != _regional_key:
		_retire([_regional_buffer])
		var values: PackedFloat32Array = request.regional_height
		_regional_buffer = _keep(_rd.storage_buffer_create(values.size()*4,values.to_byte_array()),"buffer")
		_regional_key = regional_key
	_textures = []; _sets = []; _params = []; _offsets = [0,0]; _phase = 0; _finished_wait = 0
	for mode in 2:
		var n := int(request.global_size.x) if mode == 0 else int(request.regional_size)
		var m := int(request.global_size.y) if mode == 0 else int(request.regional_size)
		var format := RDTextureFormat.new()
		format.width=n+2; format.height=(m+2)*int(request.slices)
		format.format=RenderingDevice.DATA_FORMAT_R16G16_SFLOAT
		format.usage_bits=RenderingDevice.TEXTURE_USAGE_STORAGE_BIT|RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT|RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
		var texture := _keep(_rd.texture_create(format,RDTextureView.new()),"texture")
		var params := _keep(_rd.uniform_buffer_create(160),"buffer")
		var uniforms: Array[RDUniform] = []
		for binding in 5:
			var uniform := RDUniform.new(); uniform.binding=binding
			uniform.uniform_type=RenderingDevice.UNIFORM_TYPE_IMAGE if binding==0 else (RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER if binding==3 else RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER)
			uniform.add_id([texture,_global_buffer,_regional_buffer,params,_levels_buffer][binding])
			uniforms.append(uniform)
		var set := _keep(_rd.uniform_set_create(uniforms,_shader,0),"uniform")
		_textures.append(texture); _params.append(params); _sets.append(set)
	# Regional refinement is generated first, though publication remains whole-generation.
	_phase=1

func _advance(texels: int) -> void:
	if _released or _rd == null: return
	_frame += 1
	_retire_completed()
	if _textures.size()!=2: return
	if _phase==2:
		_finished_wait+=1
		if _finished_wait==2:
			call_deferred("_deliver",{"global":_textures[0],"regional":_textures[1],"generation":_request.generation})
		return
	var mode := _phase
	var n := int(_request.global_size.x) if mode==0 else int(_request.regional_size)
	var m := int(_request.global_size.y) if mode==0 else int(_request.regional_size)
	var total := (n+2)*(m+2)*int(_request.slices)
	var batch := mini(texels,total-_offsets[mode])
	var p := PackedFloat32Array()
	var sun: Vector3 = _request.sun; var center: Vector2 = _request.regional_center
	var maximum := float(_request.regional_maximum); var minimum := float(_request.regional_minimum)
	p.append_array([_request.circumference,_request.width,_request.radius,_request.vertical_offset])
	p.append_array([_request.altitudes.x,_request.altitudes.y,float(n),float(m)])
	p.append_array([float(_request.slices),float(_offsets[mode]),float(batch),float(_request.visits)])
	p.append_array([sun.x,sun.y,sun.z,_request.maximum_height])
	p.append_array([center.x,center.y,_request.regional_extent,float(_request.regional_height_size)])
	p.append_array([float(mode),float(_request.levels.size()/4),_request.walls.x,_request.walls.y])
	p.append_array([_request.minimum_height,maximum,minimum,0.0])
	p.resize(40)
	_rd.buffer_update(_params[mode],0,160,p.to_byte_array())
	var measure := _timestamps_enabled and _timestamp_begin != "" and _timestamp_end != ""
	if measure: _rd.capture_timestamp(_timestamp_begin)
	var list := _rd.compute_list_begin()
	_rd.compute_list_bind_compute_pipeline(list,_pipeline)
	_rd.compute_list_bind_uniform_set(list,_sets[mode],0)
	_rd.compute_list_dispatch(list,int((batch+63)/64),1,1)
	_rd.compute_list_end()
	if measure: _rd.capture_timestamp(_timestamp_end)
	_total_texels+=batch
	call_deferred("_publish_diagnostics",{"generation":_request.generation,"lastBatchTexels":batch,"totalDispatchedTexels":_total_texels,"phase":mode,"traceVisits":_request.visits,"timestamped":measure})
	_offsets[mode]+=batch
	if _offsets[mode]>=total: _phase=0 if mode==1 else 2

func _discard() -> void:
	_retire(_textures+_sets+_params)
	_textures=[];_sets=[];_params=[]
func _acknowledge() -> void:
	_retire(_published)
	_published=_textures.duplicate()
	_retire(_sets+_params)
	_sets=[]; _params=[]; _textures=[]
func _retire(rids: Array) -> void:
	var valid := []
	for rid: RID in rids:
		if rid.is_valid(): valid.append(rid)
	if not valid.is_empty(): _retired.append({"frame":_frame+8,"rids":valid})
func _keep(rid: RID, kind: String) -> RID:
	if rid.is_valid(): _owned[rid]=kind
	return rid
func _free_batch(rids: Array) -> void:
	# Free descriptors before their dependencies; dependencies may auto-invalidate sets.
	for rid: RID in rids:
		if _owned.get(rid,"")=="uniform":
			if _rd.uniform_set_is_valid(rid): _rd.free_rid(rid)
			_owned.erase(rid)
	for rid: RID in rids:
		if not _owned.has(rid): continue
		if _owned[rid]!="texture" or _rd.texture_is_valid(rid): _rd.free_rid(rid)
		_owned.erase(rid)
func _retire_completed() -> void:
	var ready := []
	for i in range(_retired.size()-1,-1,-1):
		if _retired[i].frame<=_frame:
			ready.append_array(_retired[i].rids)
			_retired.remove_at(i)
	_free_batch(ready)
func _release() -> void:
	if _rd==null: return
	var all := []
	for group in _retired: all.append_array(group.rids)
	all.append_array(_published+_textures+_sets+_params+[_global_buffer,_regional_buffer,_levels_buffer,_pipeline,_shader])
	_retired=[]
	_free_batch(all)
	_published=[];_textures=[];_sets=[];_params=[]
