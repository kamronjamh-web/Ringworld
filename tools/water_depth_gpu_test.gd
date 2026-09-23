extends SceneTree

## Production helper GPU regression using physically scaled reverse-Z samples.
const STRIDE_FLOATS: int = 6
const OPTICALLY_DEEP: float = 5000.0

func _init() -> void:
	var rd: RenderingDevice = RenderingServer.create_local_rendering_device()
	if rd == null: _fail("Local RenderingDevice unavailable"); return
	var helper: String = FileAccess.get_file_as_string("res://materials/ring_water_depth.gdshaderinc")
	if helper.is_empty(): rd.free(); _fail("could not read production water-depth helper"); return
	var source: String = "#version 450\n" + helper + "\nlayout(local_size_x=1) in;\n" + \
		"layout(set=0,binding=0,std430) readonly buffer In { float d[]; } input_data;\nlayout(set=0,binding=1,std430) writeonly buffer Out { vec4 d[]; } output_data;\n" + \
		"void main(){uint i=gl_GlobalInvocationID.x;uint b=i*6u;vec4 h=vec4(input_data.d[b],input_data.d[b+1u],input_data.d[b+2u],input_data.d[b+3u]);vec2 r=ring_water_depth_from_scene(h,input_data.d[b+4u],input_data.d[b+5u]);float old_depth=-h.z/max(0.00001,h.w);float old_thickness=clamp(old_depth-input_data.d[b+5u],0.0,5000.0);output_data.d[i]=vec4(r,old_thickness,0.0);}\n"
	var shader_source: RDShaderSource = RDShaderSource.new()
	shader_source.source_compute = source
	var spirv: RDShaderSPIRV = rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "": rd.free(); _fail("GPU compile failed: " + spirv.compile_error_compute); return
	# h=(-1, 1/bed) and raw=.1/bed are real perspective/reverse-Z scale.
	# X/Y are off-axis samples only; they do not model tilt (the helper ignores them).
	var cases: Array[Dictionary] = []
	_add_perspective(cases, "deep_60km", 60000.0, 61000.0, Vector2(0.31, -0.27))
	_add_perspective(cases, "deep_99999m", 99999.0, 100999.0, Vector2(-0.42, 0.18))
	_add_perspective(cases, "deep_100km", 100000.0, 101000.0, Vector2(0.20, 0.41))
	_add_perspective(cases, "deep_110km", 110000.0, 111000.0, Vector2(-0.37, -0.33))
	_add_perspective(cases, "deep_150km", 150000.0, 151000.0, Vector2(0.12, -0.44))
	_add_perspective(cases, "bounded_deep_water", 150000.0, 180000.0, Vector2.ZERO)
	_add_perspective(cases, "shallow_110km", 110000.0, 110005.0, Vector2.ZERO)
	_add_perspective(cases, "shore_20m", 20.0, 20.2, Vector2.ZERO)
	_add_perspective(cases, "exact_contact", 100.0, 100.0, Vector2.ZERO)
	for pitch_degrees: float in [20.0, 40.0, 60.0, 80.0]:
		var angle: float = deg_to_rad(pitch_degrees)
		var surface: float = 180000.0 * cos(angle) + 58800.0 * sin(angle)
		var bed: float = surface + 1000.0 * sin(angle)
		_add_perspective(cases, "pitch_%ddeg" % int(pitch_degrees), surface, bed, Vector2(0.23, -0.19))
	_add_invalid(cases, "clear_sky", Vector4(0.0, 0.0, -1.0, 0.01), 0.0, 100.0)
	_add_invalid(cases, "invalid_w", Vector4(0.0, 0.0, -1.0, -0.01), 0.01, 100.0)
	_add_invalid(cases, "zero_w", Vector4(0.0, 0.0, -1.0, 0.0), 0.01, 100.0)
	_add_invalid(cases, "negative_raw", Vector4(0.0, 0.0, -1.0, 0.01), -0.01, 100.0)
	_add_invalid(cases, "raw_above_one", Vector4(0.0, 0.0, -1.0, 0.01), 1.01, 100.0)
	_add_invalid(cases, "foreground", Vector4(0.0, 0.0, -1.0, 1.0 / 99.0), 0.1 / 99.0, 100.0)
	_add_invalid(cases, "nonfinite_h", Vector4(0.0, 0.0, -INF, 0.01), 0.01, 100.0)
	_add_invalid(cases, "nonfinite_raw", Vector4(0.0, 0.0, -1.0, 0.01), NAN, 100.0)
	_add_invalid(cases, "nonfinite_surface", Vector4(0.0, 0.0, -1.0, 0.01), 0.01, INF)
	var input: PackedFloat32Array = PackedFloat32Array()
	for item: Dictionary in cases:
		var h: Vector4 = item.h
		input.append_array(PackedFloat32Array([h.x, h.y, h.z, h.w, float(item.raw), float(item.surface)]))
	var shader: RID = rd.shader_create_from_spirv(spirv)
	if not shader.is_valid(): rd.free(); _fail("GPU shader creation failed"); return
	var in_buffer: RID = rd.storage_buffer_create(input.size() * 4, input.to_byte_array())
	var out_buffer: RID = rd.storage_buffer_create(cases.size() * 16)
	if not in_buffer.is_valid() or not out_buffer.is_valid(): rd.free(); _fail("GPU buffer creation failed"); return
	var u0: RDUniform = RDUniform.new(); u0.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; u0.binding = 0; u0.add_id(in_buffer)
	var u1: RDUniform = RDUniform.new(); u1.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; u1.binding = 1; u1.add_id(out_buffer)
	var uniform_set: RID = rd.uniform_set_create([u0, u1], shader, 0)
	var pipeline: RID = rd.compute_pipeline_create(shader)
	if not uniform_set.is_valid() or not pipeline.is_valid(): rd.free(); _fail("GPU pipeline/bindings creation failed"); return
	var list: int = rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, uniform_set, 0); rd.compute_list_dispatch(list, cases.size(), 1, 1); rd.compute_list_end(); rd.submit(); rd.sync()
	var data: PackedFloat32Array = rd.buffer_get_data(out_buffer).to_float32_array()
	rd.free_rid(pipeline); rd.free_rid(uniform_set); rd.free_rid(in_buffer); rd.free_rid(out_buffer); rd.free_rid(shader); rd.free()
	if data.size() != cases.size() * 4: _fail("GPU readback size mismatch: got %d expected %d" % [data.size(), cases.size() * 4]); return
	var max_error: float = 0.0
	for index: int in cases.size():
		var item: Dictionary = cases[index]
		var thickness: float = data[index * 4]
		var contact: float = data[index * 4 + 1]
		var old_thickness: float = data[index * 4 + 2]
		if is_nan(thickness) or is_inf(thickness) or thickness < 0.0 or thickness > OPTICALLY_DEEP: _fail("non-finite/out-of-range current thickness at %s" % item.name); return
		if absf(thickness - float(item.expected)) > 0.03: _fail("unexpected thickness at %s: got %f expected %f" % [item.name, thickness, item.expected]); return
		if contact != float(item.contact): _fail("shore-contact mismatch at %s" % item.name); return
		if item.name in ["deep_110km", "deep_150km", "pitch_20deg"] and old_thickness > 0.03: _fail("old clamp did not false-zero %s" % item.name); return
		if item.name == "pitch_80deg" and old_thickness < 0.03: _fail("old clamp incorrectly false-zeroed pitch 80"); return
		max_error = maxf(max_error, absf(thickness - float(item.expected)))
		print("WATER_DEPTH_GPU_CASE name=%s current=%f expected=%f contact=%f old=%f" % [item.name, thickness, item.expected, contact, old_thickness])
	print("Water depth GPU fixture: PASS (%d cases, max error=%f m)" % [cases.size(), max_error])
	quit(0)

func _add_perspective(cases: Array[Dictionary], name: String, surface: float, bed: float, off_axis: Vector2) -> void:
	cases.append({"name": name, "h": Vector4(off_axis.x, off_axis.y, -1.0, 1.0 / bed), "raw": 0.1 / bed, "surface": surface, "expected": clampf(bed - surface, 0.0, OPTICALLY_DEEP), "contact": 1.0})

func _add_invalid(cases: Array[Dictionary], name: String, h: Vector4, raw: float, surface: float) -> void:
	cases.append({"name": name, "h": h, "raw": raw, "surface": surface, "expected": OPTICALLY_DEEP, "contact": 0.0})

func _fail(message: String) -> void:
	push_error(message)
	quit(1)
