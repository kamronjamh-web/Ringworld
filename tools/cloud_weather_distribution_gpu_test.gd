extends SceneTree

## Local-RD regression that compiles the production pure weather helper.  The
## fixture deliberately has no second mathematical implementation to drift.
const INCLUDE := "res://materials/ring_cloud_weather_shared.gdshaderinc"
const SPATIAL_SAMPLES := 512
const CLEAR_SAMPLE := SPATIAL_SAMPLES
const STORM_SAMPLE := SPATIAL_SAMPLES + 1
const FIXED_SAMPLE := SPATIAL_SAMPLES + 2
const SHIFTED_SAMPLE := SPATIAL_SAMPLES + 3
const REPEATED_SAMPLE := SPATIAL_SAMPLES + 4
const SEAM_ZERO_SAMPLE := SPATIAL_SAMPLES + 5
const SEAM_CIRCUMFERENCE_SAMPLE := SPATIAL_SAMPLES + 6
const HANDOFF_FIRST_SAMPLE := SPATIAL_SAMPLES + 8
const PHYSICAL_DISTANCE_FIRST_SAMPLE := HANDOFF_FIRST_SAMPLE + 9
const SAMPLES := PHYSICAL_DISTANCE_FIRST_SAMPLE + 2

func _init() -> void:
	var rd: RenderingDevice = RenderingServer.create_local_rendering_device()
	if rd == null: _fail("Local RenderingDevice unavailable"); return
	var helper: String = FileAccess.get_file_as_string(INCLUDE)
	if helper.is_empty() or helper.find("ring_cloud_weather_resolved_coverage") < 0: rd.free(); _fail("production weather helper unavailable"); return
	var source: RDShaderSource = RDShaderSource.new()
	source.source_compute = _shader_source(helper)
	var spirv: RDShaderSPIRV = rd.shader_compile_spirv_from_source(source)
	if spirv.compile_error_compute != "": rd.free(); _fail("weather helper shader compile failed: " + spirv.compile_error_compute); return
	var shader: RID = rd.shader_create_from_spirv(spirv)
	var config: PackedFloat32Array = _config()
	var config_buffer: RID = rd.storage_buffer_create(config.size() * 4, config.to_byte_array())
	var output_buffer: RID = rd.storage_buffer_create(SAMPLES * 16)
	var uniforms: Array[RDUniform] = []
	for binding: int in 2:
		var uniform: RDUniform = RDUniform.new()
		uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
		uniform.binding = binding
		uniform.add_id(config_buffer if binding == 0 else output_buffer)
		uniforms.append(uniform)
	var uniform_set: RID = rd.uniform_set_create(uniforms, shader, 0)
	var pipeline: RID = rd.compute_pipeline_create(shader)
	var list: int = rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline)
	rd.compute_list_bind_uniform_set(list, uniform_set, 0)
	rd.compute_list_dispatch(list, SAMPLES, 1, 1)
	rd.compute_list_end(); rd.submit(); rd.sync()
	var results: PackedFloat32Array = rd.buffer_get_data(output_buffer).to_float32_array()
	for rid: RID in [pipeline, uniform_set, config_buffer, output_buffer, shader]: rd.free_rid(rid)
	rd.free()
	_verify(results)

func _config() -> PackedFloat32Array:
	var values: PackedFloat32Array = PackedFloat32Array()
	for index: int in SAMPLES:
		var base: float = 0.45
		var storm: float = 0.0
		var wind: float = 0.0
		var meters_x: float = fmod(float(index) * 61517.0, 31415926.0)
		var meters_z: float = fmod(float(index) * 11003.0, 160000.0) - 80000.0
		if index == CLEAR_SAMPLE: base = 0.0
		if index == STORM_SAMPLE: base = 0.0; storm = 1.0
		if index == FIXED_SAMPLE or index == SHIFTED_SAMPLE or index == REPEATED_SAMPLE: meters_x = 12345.0; meters_z = 49000.0
		if index == SHIFTED_SAMPLE: wind = 3000.0
		if index == SEAM_ZERO_SAMPLE or index == SEAM_CIRCUMFERENCE_SAMPLE: meters_z = 49000.0; wind = 3000.0
		if index == SEAM_ZERO_SAMPLE: meters_x = 0.0
		if index == SEAM_CIRCUMFERENCE_SAMPLE: meters_x = 31415926.0
		values.append_array([base, storm, 0, 0, 1, 64000, .12, 761, 31415926, wind, 0, base, meters_x, 1800, meters_z, 0])
	return values

func _verify(results: PackedFloat32Array) -> void:
	var low: float = 1.0
	var high: float = 0.0
	var cloud: int = 0
	var clear: int = 0
	for index: int in SPATIAL_SAMPLES:
		var resolved: float = results[index * 4 + 1]
		var gate: float = results[index * 4 + 2]
		low = minf(low, resolved); high = maxf(high, resolved)
		if gate > .01: cloud += 1
		if gate < .01: clear += 1
	if high - low < .08 or cloud == 0 or clear == 0: _fail("weather distribution lacks required cloud and clear regions range=%.5f cloud=%d clear=%d low=%.5f high=%.5f" % [high - low, cloud, clear, low, high]); return
	if results[CLEAR_SAMPLE * 4 + 2] > .00001: _fail("cloud-free coverage did not clear density gate"); return
	if results[STORM_SAMPLE * 4 + 1] < .999 or results[STORM_SAMPLE * 4 + 2] < .999: _fail("storm strength did not override clear patch"); return
	var shifted: float = results[SHIFTED_SAMPLE * 4]
	var fixed_a: float = results[FIXED_SAMPLE * 4]
	var fixed_b: float = results[REPEATED_SAMPLE * 4]
	if absf(shifted - fixed_a) < .0001: _fail("wind offset did not move weather field"); return
	if not is_equal_approx(fixed_a, fixed_b): _fail("fixed seed/weather input was not deterministic"); return
	var seam: float = absf(results[SEAM_ZERO_SAMPLE * 4] - results[SEAM_CIRCUMFERENCE_SAMPLE * 4])
	if seam > .00001: _fail("periodic weather seam mismatch %.8f" % seam); return
	# These values come from the production near-fade helper.  The far owner is
	# its complement, so every tested point must retain full combined coverage.
	for range_index: int in 3:
		var first: int = HANDOFF_FIRST_SAMPLE + range_index * 3
		var at_start: float = results[first * 4]
		var at_mid: float = results[(first + 1) * 4]
		var at_end: float = results[(first + 2) * 4]
		if at_start < .999 or at_mid < .49 or at_mid > .51 or at_end > .001: _fail("near/far handoff contract failed for range %d" % range_index); return
	# The production helper must convert both a certified .001 world-scale
	# position and an unscaled position to the same 80 km physical distance.
	# The nonzero 20.7 km altitude prevents an accidental origin-only test.
	for index: int in 2:
		var distance: float = results[(PHYSICAL_DISTANCE_FIRST_SAMPLE + index) * 4]
		if absf(distance - 80000.0) > .01: _fail("physical far-cloud distance scale conversion failed index=%d value=%.5f" % [index, distance]); return
	print("CLOUD_WEATHER_DISTRIBUTION_GPU_TEST=PASS range=%.4f clouds=%d clear=%d seam=%.8f" % [high - low, cloud, clear, seam])
	quit(0)

func _shader_source(helper: String) -> String:
	return """#version 450
layout(local_size_x=1) in;
struct Sample { vec4 base; vec4 params; vec4 frame; vec4 meters; };
layout(set=0,binding=0,std430) readonly buffer Config { Sample samples[]; };
layout(set=0,binding=1,std430) writeonly buffer Output { vec4 output_data[]; };
""" + helper + """
void main() { uint i=gl_GlobalInvocationID.x; if (i >= 529u) { uint scaleCase=i-529u; vec3 camera=scaleCase==0u?vec3(0.0,20.7,0.0):vec3(0.0,20700.0,0.0); vec3 point=scaleCase==0u?vec3(80.0,20.7,0.0):vec3(80000.0,20700.0,0.0); float scale=scaleCase==0u?0.001:1.0; output_data[i]=vec4(ring_cloud_physical_distance(point,camera,scale),0.0,0.0,0.0); return; } if (i >= 520u) { uint range=(i-520u)/3u; uint point=(i-520u)%3u; float start=range==0u?45000.0:(range==1u?5000.0:80000.0); float end=range==0u?68500.0:(range==1u?6000.0:80500.0); float distance=point==0u?start:(point==1u?(start+end)*0.5:end); float near=ring_cloud_handoff_near_weight(distance,vec4(start,end,0.0,0.0)); output_data[i]=vec4(near,1.0-near,0.0,0.0); return; } Sample s=samples[i]; float noise=ring_cloud_weather_noise(s.meters.xyz,s.params,s.frame); float resolved=ring_cloud_weather_resolved_coverage(s.base.x,s.base.y,s.meters.xyz,s.params,s.frame); float gate=ring_cloud_weather_clear_gate(resolved,s.base.y,s.params); output_data[i]=vec4(noise,resolved,gate,0.0); }
"""

func _fail(message: String) -> void:
	push_error("CLOUD_WEATHER_DISTRIBUTION_GPU_TEST=FAIL " + message)
	quit(1)
