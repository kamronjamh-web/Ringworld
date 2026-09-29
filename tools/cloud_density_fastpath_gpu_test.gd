extends SceneTree

# Compares the immutable pre-fast-path sampleScene body with a fresh extraction
# of the production body.  This is deliberately a local-RD test: it never
# imports, edits, or uses a cached shader resource.
const CASES := 4096
const LIVE := "res://addons/SunshineClouds2/SunshineCloudsCompute.glsl"
const FIXTURE := "res://tools/fixtures/cloud_density_reference.glslinc"

func _init() -> void:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("Local RenderingDevice unavailable; run from a normal GPU-capable display session.")
		return
	var live := FileAccess.get_file_as_string(LIVE)
	var fixture := FileAccess.get_file_as_string(FIXTURE)
	if live.is_empty() or fixture.is_empty():
		rd.free(); _fail("could not read the live shader or portable baseline fixture"); return
	var reference := _function_to_eof(fixture, "float sampleScene(")
	if reference.is_empty():
		rd.free(); _fail("portable baseline sampleScene extraction failed"); return
	var helpers := _function(live, "//Helpers", "float sampleScene(")
	var optimized := _function(live, "float sampleScene(", "float sampleSceneCoarse(")
	if helpers.is_empty() or optimized.is_empty():
		rd.free(); _fail("live sampleScene/helper extraction failed"); return
	helpers = helpers.replace("#include \"SunshineCloudsEntrySkip.glslinc\"", "")
	reference = _rewrite(reference.replace("float sampleScene(", "float sampleSceneReference("))
	optimized = _rewrite(optimized.replace("float sampleScene(", "float sampleSceneOptimized("))
	helpers = _rewrite(helpers)
	var source := _shader_source(helpers, reference, optimized)
	var shader_source := RDShaderSource.new(); shader_source.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		rd.free(); _fail("density parity shader compile failed: " + spirv.compile_error_compute); return
	var shader := rd.shader_create_from_spirv(spirv)
	var config := _make_config_data()
	var effectors := _make_effector_data()
	var config_buffer := rd.storage_buffer_create(config.size() * 4, config.to_byte_array())
	var effector_buffer := rd.storage_buffer_create(effectors.size() * 4, effectors.to_byte_array())
	var output_buffer := rd.storage_buffer_create(CASES * 16)
	var sampler := _sampler(rd)
	var heightmask := _texture(rd, false, 32, 1, 1)
	var large := _texture(rd, true, 8, 8, 8)
	var medium := _texture(rd, true, 8, 8, 8)
	var small := _texture(rd, true, 8, 8, 8)
	var curl := _texture(rd, true, 8, 8, 8)
	var uniforms: Array[RDUniform] = []
	for binding in 3:
		var uniform := RDUniform.new(); uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; uniform.binding = binding
		uniform.add_id([config_buffer, effector_buffer, output_buffer][binding]); uniforms.append(uniform)
	for binding in 5:
		var uniform := RDUniform.new(); uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; uniform.binding = binding + 3
		uniform.add_id(sampler); uniform.add_id([heightmask, large, medium, small, curl][binding]); uniforms.append(uniform)
	var uniform_set := rd.uniform_set_create(uniforms, shader, 0)
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, uniform_set, 0); rd.compute_list_dispatch(list, CASES, 1, 1); rd.compute_list_end(); rd.submit(); rd.sync()
	var results := rd.buffer_get_data(output_buffer).to_float32_array()
	rd.free_rid(pipeline); rd.free_rid(uniform_set); rd.free_rid(config_buffer); rd.free_rid(effector_buffer); rd.free_rid(output_buffer); rd.free_rid(heightmask); rd.free_rid(large); rd.free_rid(medium); rd.free_rid(small); rd.free_rid(curl); rd.free_rid(sampler); rd.free_rid(shader); rd.free()
	var finite := 0; var nonfinite_reference := 0; var positive_background := 0; var positive_storm := 0; var worst := 0.0
	for i in CASES:
		var old := results[i * 4]; var current := results[i * 4 + 1]; var flags := int(results[i * 4 + 2]); var delta := results[i * 4 + 3]
		if flags & 1 != 0: positive_background += 1
		if flags & 2 != 0: positive_storm += 1
		if is_nan(old) or is_inf(old):
			nonfinite_reference += 1
			if is_nan(current) or is_inf(current): _fail("optimized output is non-finite for degenerate reference case %d" % i); return
			continue
		finite += 1; worst = max(worst, delta)
		if is_nan(current) or is_inf(current) or delta > 0.00001:
			_fail("density mismatch at %d: reference=%f optimized=%f delta=%f" % [i, old, current, delta]); return
	if finite == 0 or positive_background == 0 or positive_storm == 0:
		_fail("vacuous parity fixture: finite=%d background=%d storm=%d" % [finite, positive_background, positive_storm]); return
	print("Cloud density fast-path GPU parity: PASS (%d finite, %d reference non-finite, background=%d storm=%d, max delta=%f)" % [finite, nonfinite_reference, positive_background, positive_storm, worst])
	quit(0)

func _function(source: String, start_marker: String, end_marker: String) -> String:
	var start := source.find(start_marker)
	var end := source.find(end_marker, start)
	return "" if start < 0 or end < 0 else source.substr(start, end - start)

func _function_to_eof(source: String, start_marker: String) -> String:
	var start := source.find(start_marker)
	return "" if start < 0 else source.substr(start)

func _rewrite(source: String) -> String:
	var index := "[gl_GlobalInvocationID.x]"
	var replacements := {"genericData.data.WindDirection": "testData" + index + ".wind.xy", "genericData.data.pointEffectorCount": "testData" + index + ".wind.z", "genericData.data.windSweptRange": "testData" + index + ".wind.w", "genericData.data.windSweptPower": "testData" + index + ".wind_power.x", "genericData.data.cloud_frame_origin": "testData" + index + ".cloud_frame_origin", "genericData.data.cloud_frame_around": "testData" + index + ".cloud_frame_around", "genericData.data.cloud_frame_up": "testData" + index + ".cloud_frame_up", "genericData.data.cloud_frame_across": "testData" + index + ".cloud_frame_across", "genericData.data.storm_system": "testData" + index + ".storm_system", "genericData.data.storm_shape": "testData" + index + ".storm_shape", "genericData.data.storm_detail": "testData" + index + ".storm_detail", "genericData.data.background_altitude": "testData" + index + ".background_altitude", "genericData.data.storm_altitude": "testData" + index + ".storm_altitude", "genericData.data.temporal_controls": "testData" + index + ".temporal_controls"}
	for key in replacements: source = source.replace(key, replacements[key])
	return source.replace("pointEffectors[i]", "effectors[gl_GlobalInvocationID.x * 2u + uint(i)]")

func _shader_source(helpers: String, reference: String, optimized: String) -> String:
	return """#version 450
#define PI 3.141592
layout(local_size_x=1) in;
struct PointEffector { vec3 position; float radius; float power; float attenuation; vec2 reserved; };
struct TestData { vec4 wind; vec4 wind_power; vec4 cloud_frame_origin; vec4 cloud_frame_around; vec4 cloud_frame_up; vec4 cloud_frame_across; vec4 storm_system; vec4 storm_shape; vec4 storm_detail; vec4 background_altitude; vec4 storm_altitude; vec4 temporal_controls; };
layout(set=0,binding=0,std430) readonly buffer Config { TestData testData[]; };
layout(set=0,binding=1,std430) readonly buffer Effectors { PointEffector effectors[]; };
layout(set=0,binding=2,std430) writeonly buffer Output { vec4 outData[]; };
layout(binding=3) uniform sampler2D heightmask;
layout(binding=4) uniform sampler3D large_noise;
layout(binding=5) uniform sampler3D noise_medium;
layout(binding=6) uniform sampler3D noise_small;
layout(binding=7) uniform sampler3D curl_noise;
""" + helpers + reference + optimized + """
void main() { uint i=gl_GlobalInvocationID.x; float f=float(i); bool control=i<128u; vec3 p=control?vec3(2000.0,1200.0,5000.0):vec3(mod(f*137.0, 16000.0)-8000.0, 250.0+mod(f*71.0, 2100.0), mod(f*97.0, 14000.0)-1000.0); vec3 a=vec3(f*3.1, 0.0, f*1.7); float lod=(i&1u)==0u?0.0:1.0; float curl=(i%5u)==0u?0.0:72.0; bool ambient=(i%3u)==0u; bool coarse=(i%7u)==0u; float coverage=control?(1.1+float(i%2u)*0.1):((i%6u)==0u?0.0:((i%6u)==1u?0.33:((i%6u)==2u?0.67:(0.74+float(i%3u)*0.05)))); float old=sampleSceneReference(a,a*1.3,a*1.7,p,2400.0,100.0,1.0,900.0,600.0,240.0,coverage,0.8,curl,lod,ambient,coarse); float now=sampleSceneOptimized(a,a*1.3,a*1.7,p,2400.0,100.0,1.0,900.0,600.0,240.0,coverage,0.8,curl,lod,ambient,coarse); float background=(testData[i].storm_shape.z<=0.001&&old>0.001)?1.0:0.0; float storm=(testData[i].storm_shape.z>0.001&&now>0.001)?2.0:0.0; outData[i]=vec4(old,now,background+storm,abs(old-now)); }
"""

func _make_config_data() -> PackedFloat32Array:
	var out := PackedFloat32Array()
	for i in CASES:
		var profile := float(i % 11); var influence := 0.0 if i % 13 == 0 else 0.85
		var around_offset := 0.0 if i < 128 or i % 2 == 0 else 502554.0
		var background_floor := 100.0 if i % 4 != 0 else 600.0
		var background_ceiling := 2400.0 if i % 4 != 0 else 1600.0
		var storm_floor := 100.0 if i % 3 != 0 else 800.0
		var storm_ceiling := 2400.0 if i % 3 != 0 else 2100.0
		out.append_array([0.35,-0.2,2.0,1400.0, 35.0,0,0,0, 0,0,0,0, 1,0,0,around_offset, 0,1,0,80000, 0,0,1,0, 2000,5000,2600,1800, 1,0, influence,profile, 750,1.8,0.68,profile, background_floor,background_ceiling,0,0, storm_floor,storm_ceiling,0,0, 0,12000,0,0])
	return out

func _sampler(rd: RenderingDevice) -> RID:
	var state := RDSamplerState.new(); state.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; state.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT; state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT; state.repeat_w = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	return rd.sampler_create(state)

func _texture(rd: RenderingDevice, volume: bool, width: int, height: int, depth: int) -> RID:
	var pixels := PackedFloat32Array()
	for z in depth:
		for y in height:
			for x in width:
				var t := float(x * 17 + y * 31 + z * 47)
				pixels.append_array([0.18 + fmod(t, 61.0) / 100.0, 0.31 + fmod(t, 37.0) / 100.0, 0.22 + fmod(t, 53.0) / 100.0, 0.44 + fmod(t, 29.0) / 100.0])
	var format := RDTextureFormat.new(); format.texture_type = RenderingDevice.TEXTURE_TYPE_3D if volume else RenderingDevice.TEXTURE_TYPE_2D; format.width = width; format.height = height; format.depth = depth; format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
	return rd.texture_create(format, RDTextureView.new(), [pixels.to_byte_array()])

func _make_effector_data() -> PackedFloat32Array:
	var out := PackedFloat32Array()
	for i in CASES:
		out.append_array([2000,1200,4500,1800,0.22,0,0,0, 3600,1700,7000,2100,-0.18,0,0,0])
	return out

func _fail(message: String) -> void:
	push_error(message); quit(1)
