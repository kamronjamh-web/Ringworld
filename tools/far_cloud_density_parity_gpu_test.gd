extends SceneTree

# Compares the far-volume background density with a fresh extraction of the
# production Sunshine sampleScene body. Storms, curl, effectors, and temporal
# state are deliberately disabled because far_density_core has no such path.
const CASES := 4096
const SEAM_CASES := 128
const TOTAL_CASES := CASES + SEAM_CASES
const LIVE := "res://addons/SunshineClouds2/SunshineCloudsCompute.glsl"
const FAR_INCLUDE := "res://materials/ring_far_cloud_density.gdshaderinc"
const WEATHER_INCLUDE := "res://materials/ring_cloud_weather_shared.gdshaderinc"

func _init() -> void:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null: _fail("Local RenderingDevice unavailable"); return
	var live := FileAccess.get_file_as_string(LIVE); var far := FileAccess.get_file_as_string(FAR_INCLUDE); var weather := FileAccess.get_file_as_string(WEATHER_INCLUDE)
	var helpers := _function(live, "//Helpers", "float sampleScene(")
	var scene := _function(live, "float sampleScene(", "float sampleSceneCoarse(")
	if live.is_empty() or far.is_empty() or weather.is_empty() or helpers.is_empty() or scene.is_empty(): rd.free(); _fail("live source extraction failed"); return
	helpers = _rewrite(helpers.replace("#include \"SunshineCloudsEntrySkip.glslinc\"", ""))
	scene = _rewrite(scene.replace("float sampleScene(", "float sampleSceneLive("))
	far = _strip_uniforms(far)
	var rd_source := RDShaderSource.new(); rd_source.source_compute = _shader_source(weather, helpers, far, scene)
	var spirv := rd.shader_compile_spirv_from_source(rd_source)
	if spirv.compile_error_compute != "": rd.free(); _fail("density parity shader compile failed: " + spirv.compile_error_compute); return
	var shader := rd.shader_create_from_spirv(spirv)
	var config := _make_config(); var effectors := PackedFloat32Array(); effectors.resize(TOTAL_CASES * 8)
	if config.size() != TOTAL_CASES * 60:
		rd.free_rid(shader); rd.free(); _fail("invalid config layout: expected %d floats, got %d" % [TOTAL_CASES * 60, config.size()]); return
	var config_buffer := rd.storage_buffer_create(config.size() * 4, config.to_byte_array())
	var effector_buffer := rd.storage_buffer_create(effectors.size() * 4, effectors.to_byte_array())
	var output_buffer := rd.storage_buffer_create(TOTAL_CASES * 16)
	if not config_buffer.is_valid() or not effector_buffer.is_valid() or not output_buffer.is_valid():
		if config_buffer.is_valid(): rd.free_rid(config_buffer)
		if effector_buffer.is_valid(): rd.free_rid(effector_buffer)
		if output_buffer.is_valid(): rd.free_rid(output_buffer)
		rd.free_rid(shader); rd.free(); _fail("could not allocate parity RD buffers"); return
	var sampler := _sampler(rd)
	var textures := [_texture(rd, false, 32, 1, 1), _texture(rd, true, 8, 8, 8), _texture(rd, true, 8, 8, 8), _texture(rd, true, 8, 8, 8), _texture(rd, true, 8, 8, 8), _texture(rd, false, 32, 32, 1)]
	var uniforms: Array[RDUniform] = []
	for binding in 3:
		var u := RDUniform.new(); u.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; u.binding = binding; u.add_id([config_buffer, effector_buffer, output_buffer][binding]); uniforms.append(u)
	for binding in 6:
		var u := RDUniform.new(); u.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; u.binding = binding + 3; u.add_id(sampler); u.add_id(textures[binding]); uniforms.append(u)
	var set := rd.uniform_set_create(uniforms, shader, 0); var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0); rd.compute_list_dispatch(list, TOTAL_CASES, 1, 1); rd.compute_list_end(); rd.submit(); rd.sync()
	var results := rd.buffer_get_data(output_buffer).to_float32_array()
	for rid in [pipeline, set, config_buffer, effector_buffer, output_buffer] + textures + [sampler, shader]: rd.free_rid(rid)
	rd.free()
	var positive := 0; var zero := 0; var wind_positive := 0; var weather_clear := 0; var weather_storm := 0; var worst := 0.0
	for i in CASES:
		var source_density := results[i * 4]; var far_density := results[i * 4 + 1]; var delta := results[i * 4 + 3]
		worst = max(worst, delta)
		if is_nan(source_density) or is_inf(source_density) or is_nan(far_density) or is_inf(far_density) or delta > 0.00001: _fail("far/source density mismatch at %d: source=%f far=%f delta=%f" % [i, source_density, far_density, delta]); return
		if source_density > .001: positive += 1
		if source_density <= .00001: zero += 1
		if i % 2 == 1 and source_density > .001: wind_positive += 1
		if int(results[i * 4 + 2]) & 8 != 0 and source_density <= .00001: weather_clear += 1
		if int(results[i * 4 + 2]) & 4 != 0 and source_density > .001: weather_storm += 1
	if positive == 0 or zero == 0 or wind_positive == 0 or weather_clear == 0 or weather_storm == 0: _fail("vacuous fixture: positive=%d zero=%d wind_positive=%d weather_clear=%d weather_storm=%d" % [positive, zero, wind_positive, weather_clear, weather_storm]); return
	var seam_positive: int = 0; var seam_worst_periodic: float = 0.0; var seam_worst_continuity: float = 0.0
	for i in SEAM_CASES:
		var base: int = (CASES + i) * 4
		var at_one: float = results[base]; var at_c_plus_one: float = results[base + 1]; var at_negative_one: float = results[base + 2]; var at_c_minus_one: float = results[base + 3]
		if is_nan(at_one) or is_inf(at_one) or is_nan(at_c_plus_one) or is_inf(at_c_plus_one) or is_nan(at_negative_one) or is_inf(at_negative_one) or is_nan(at_c_minus_one) or is_inf(at_c_minus_one): _fail("non-finite seam density at %d" % i); return
		var periodic: float = maxf(absf(at_one - at_c_plus_one), absf(at_negative_one - at_c_minus_one)); var continuity: float = absf(at_one - at_negative_one)
		seam_worst_periodic = maxf(seam_worst_periodic, periodic); seam_worst_continuity = maxf(seam_worst_continuity, continuity)
		if periodic >= 0.0001 or continuity >= 0.03: _fail("seam mismatch at %d: periodic=%f continuity=%f" % [i, periodic, continuity]); return
		if maxf(maxf(at_one, at_c_plus_one), maxf(at_negative_one, at_c_minus_one)) > .001: seam_positive += 1
	if seam_positive == 0: _fail("vacuous seam fixture"); return
	print("FAR_CLOUD_DENSITY_PARITY_GPU_TEST=PASS core=%d positive=%d zero=%d wind_positive=%d weather_clear=%d weather_storm=%d max_delta=%f seam=%d seam_positive=%d seam_periodic=%f seam_continuity=%f" % [CASES, positive, zero, wind_positive, weather_clear, weather_storm, worst, SEAM_CASES, seam_positive, seam_worst_periodic, seam_worst_continuity]); quit(0)

func _function(source: String, start_marker: String, end_marker: String) -> String:
	var start := source.find(start_marker); var end := source.find(end_marker, start)
	return "" if start < 0 or end < 0 else source.substr(start, end - start)

func _rewrite(source: String) -> String:
	var i := "[gl_GlobalInvocationID.x]"
	var r := {"genericData.data.WindDirection":"testData"+i+".wind.xy", "genericData.data.pointEffectorCount":"testData"+i+".wind.z", "genericData.data.windSweptRange":"testData"+i+".wind.w", "genericData.data.windSweptPower":"testData"+i+".wind_power.x", "genericData.data.cloud_frame_origin":"testData"+i+".cloud_frame_origin", "genericData.data.cloud_frame_around":"testData"+i+".cloud_frame_around", "genericData.data.cloud_frame_up":"testData"+i+".cloud_frame_up", "genericData.data.cloud_frame_across":"testData"+i+".cloud_frame_across", "genericData.data.storm_system":"testData"+i+".storm_system", "genericData.data.storm_shape":"testData"+i+".storm_shape", "genericData.data.storm_detail":"testData"+i+".storm_detail", "genericData.data.background_altitude":"testData"+i+".background_altitude", "genericData.data.storm_altitude":"testData"+i+".storm_altitude", "genericData.data.temporal_controls":"testData"+i+".temporal_controls", "genericData.data.cloud_handoff":"testData"+i+".cloud_handoff", "genericData.data.cloud_weather_params":"testData"+i+".cloud_weather_params", "genericData.data.cloud_weather_frame":"testData"+i+".cloud_weather_frame"}
	for key in r: source = source.replace(key, r[key])
	return source.replace("pointEffectors[i]", "effectors[gl_GlobalInvocationID.x]")

func _strip_uniforms(source: String) -> String:
	var lines: PackedStringArray = []
	for line in source.split("\n"): if not line.strip_edges().begins_with("uniform "): lines.append(line)
	return "\n".join(lines)

func _shader_source(weather: String, helpers: String, far: String, scene: String) -> String:
	return """#version 450
#define PI 3.141592
layout(local_size_x=1) in;
struct PointEffector { vec3 position; float radius; float power; float attenuation; vec2 reserved; };
struct TestData { vec4 wind; vec4 wind_power; vec4 cloud_frame_origin; vec4 cloud_frame_around; vec4 cloud_frame_up; vec4 cloud_frame_across; vec4 storm_system; vec4 storm_shape; vec4 storm_detail; vec4 background_altitude; vec4 storm_altitude; vec4 temporal_controls; vec4 cloud_handoff; vec4 cloud_weather_params; vec4 cloud_weather_frame; };
layout(set=0,binding=0,std430) readonly buffer Config { TestData testData[]; };
layout(set=0,binding=1,std430) readonly buffer Effectors { PointEffector effectors[]; };
layout(set=0,binding=2,std430) writeonly buffer Output { vec4 outData[]; };
layout(binding=3) uniform sampler2D heightmask;
layout(binding=4) uniform sampler3D large_noise;
layout(binding=5) uniform sampler3D noise_medium;
layout(binding=6) uniform sampler3D noise_small;
layout(binding=7) uniform sampler3D curl_noise;
// Camera-independent clouds are disabled in this legacy-parity harness: the live sampleScene calls these four hooks,
// which the production shader defines in RingWeatherSystems.glslinc / SunshineCloudsCompute.glsl.
bool ring_ws_enabled() { return false; }
float ring_ws_storm_level() { return 0.0; }
float ring_ws_frame_coverage() { return 0.0; }
float ring_ws_sculpt_density(vec3 canonical, float background_floor, float background_ceiling) { return 0.0; }
layout(binding=8) uniform sampler2D extra_noise;
""" + weather + helpers + """
#define far_density_enabled true
#define far_density_strength 1.0
#define far_density_opacity_multiplier 1.0
#define far_density_extra_large_noise_patterns extra_noise
#define far_density_height_gradient heightmask
#define far_density_large_scale_noise large_noise
#define far_density_medium_scale_noise noise_medium
#define far_density_small_scale_noise noise_small
#define far_density_extra_large_position vec3(0.0)
#define far_density_large_position vec3(0.0)
#define far_density_medium_position vec3(0.0)
#define far_density_detail_position vec3(0.0)
#define far_density_extra_large_scale 1200.0
#define far_density_large_scale 900.0
#define far_density_medium_scale 600.0
#define far_density_small_scale 240.0
#define far_density_clouds_coverage 0.82
#define far_density_coverage_bias 0.0
#define far_density_clouds_density 1.0
#define far_density_clouds_detail_power 0.8
#define far_density_wind_swept_range testData[gl_GlobalInvocationID.x].wind.w
#define far_density_wind_swept_strength testData[gl_GlobalInvocationID.x].wind_power.x
#define far_density_wind_direction vec3(testData[gl_GlobalInvocationID.x].wind.x,0.0,testData[gl_GlobalInvocationID.x].wind.y)
#define far_density_background_altitude testData[gl_GlobalInvocationID.x].background_altitude.xy
#define far_density_ring_circumference_meters 502654.8246
// Legacy far/source parity uses the append-only weather ABI's zero values.
// Constants avoid macro-expanding the rewritten Sunshine member accesses.
const vec4 cloud_weather_params = vec4(0.0);
const vec4 cloud_weather_frame = vec4(0.0);
""" + far + scene + """
void main() { uint i=gl_GlobalInvocationID.x; if(i>=4096u) { float f=float(i-4096u); float y=150.0+mod(f*17.0,2100.0); float z=-3500.0+mod(f*43.0,7000.0); float C=502654.8246; outData[i]=vec4(far_density_sample_coverage(vec3(1.0,y,z),1.1),far_density_sample_coverage(vec3(C+1.0,y,z),1.1),far_density_sample_coverage(vec3(-1.0,y,z),1.1),far_density_sample_coverage(vec3(C-1.0,y,z),1.1)); return; } float f=float(i); vec3 p=vec3(12000.0+mod(f*37.0,18000.0),100.0+mod(f*19.0,2300.0),-4000.0+mod(f*29.0,8000.0)); float R=80000.0; float shell=R-p.y; vec3 world=vec3(shell*sin(p.x/R),R-shell*cos(p.x/R),p.z); vec3 canonical=to_cloud_space(world); float coverage=(i%7u)==0u?0.0:((i%7u)==1u?0.33:((i%7u)==2u?0.67:0.74+float(i%4u)*0.04)); if(i<128u) coverage=1.1; float weather_storm=testData[i].cloud_handoff.z; float weather_coverage=ring_cloud_weather_resolved_coverage(testData[i].cloud_weather_frame.w,weather_storm,canonical,testData[i].cloud_weather_params,testData[i].cloud_weather_frame); float weather_gate=ring_cloud_weather_clear_gate(weather_coverage,weather_storm,testData[i].cloud_weather_params); float far_coverage=testData[i].cloud_weather_params.y>0.0?mix(0.70,0.90,weather_coverage):coverage; float extra=texture(extra_noise,canonical.xz/1200.0).a; float live=sampleSceneLive(vec3(0.0),vec3(0.0),vec3(0.0),world,2400.0,100.0,extra,900.0,600.0,240.0,coverage,0.8,0.0,0.0,false,false); float current=far_density_core(canonical,far_coverage)*weather_gate; float policy=testData[i].cloud_weather_params.y>0.0?(weather_storm>.5?4.0:(weather_gate<.01?8.0:16.0)):float(i&1u); outData[i]=vec4(live,current,policy,abs(live-current)); }
"""

func _make_config() -> PackedFloat32Array:
	var out := PackedFloat32Array()
	for i in TOTAL_CASES:
		var strength := 180.0 if i % 2 == 1 else 0.0
		var coverage := 1.1 if i < 128 else (0.0 if i % 7 == 0 else (0.33 if i % 7 == 1 else (0.67 if i % 7 == 2 else 0.74 + float(i % 4) * 0.04)))
		var weather_enabled := i < CASES and i % 2 == 0
		var weather_storm := 1.0 if weather_enabled and i % 31 == 0 else 0.0
		var weather_params: Vector4 = Vector4(1.0,64000.0,.12,761.0) if weather_enabled else Vector4.ZERO
		var weather_frame: Vector4 = Vector4(502654.8246,2300.0,-700.0,coverage) if weather_enabled else Vector4.ZERO
		# Keep the legacy source/far density comparison on its pre-weather path.
		out.append_array([0.35,-0.2,0,0.8, strength,0,0,0, 0,0,0,0, 1,0,0,0, 0,1,0,80000, 0,0,1,0, 0,0,0,0, 1,0,0,0, 750,1.8,0.68,0, 100,2400,0,0, 100,2400,0,0, 0,0,0,0, 0,0,weather_storm,0, weather_params.x,weather_params.y,weather_params.z,weather_params.w, weather_frame.x,weather_frame.y,weather_frame.z,weather_frame.w])
	return out

func _sampler(rd: RenderingDevice) -> RID:
	var s := RDSamplerState.new(); s.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; s.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; s.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT; s.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT; s.repeat_w = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT; return rd.sampler_create(s)

func _texture(rd: RenderingDevice, volume: bool, width: int, height: int, depth: int) -> RID:
	var pixels := PackedFloat32Array()
	for z in depth:
		for y in height:
			for x in width:
				var cell: int = x * 17 + y * 31 + z * 47
				# Deliberate dense and sparse cells make the enabled 0.70-0.90
				# background threshold meaningful while clear gates still own zeros.
				var dense: bool = cell % 5 != 0
				var large_value: float = 0.96 if dense else 0.28
				var erosion_value: float = 0.05 if dense else 0.74
				var extra_value: float = 0.95 if dense else 0.34
				pixels.append_array([large_value, 0.86, erosion_value, extra_value])
	var f := RDTextureFormat.new(); f.texture_type = RenderingDevice.TEXTURE_TYPE_3D if volume else RenderingDevice.TEXTURE_TYPE_2D; f.width=width; f.height=height; f.depth=depth; f.format=RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; f.usage_bits=RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
	return rd.texture_create(f, RDTextureView.new(), [pixels.to_byte_array()])

func _fail(message: String) -> void: push_error(message); quit(1)
