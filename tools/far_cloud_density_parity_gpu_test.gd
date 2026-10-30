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
	var layer_error := _layers_parity()
	if layer_error != "": _fail(layer_error); return
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
// Storm profile map hooks of the live sampleScene (RingStormProfileSample.glslinc): inert here; the legacy analytic storm shape is compiled in.
const int RING_STORM_FALLBACK = 1;
float g_storm_map_density = 0.0;
float g_storm_other = 0.0;
float ring_storm_eroded(float mediumshape, float small_raw) { return 0.0; }
bool g_layers_on = false; // S6 cloud layers are off in this legacy-parity harness (RingCloudLayerSample.glslinc)
float ring_layers_background(vec3 canonical, float weather_storm, float extra_large, vec3 large_pos, vec3 medium_pos, vec3 small_pos, float large_scale, float medium_scale, float small_scale, float small_power, float curl_power, float lod, bool ambient, bool coarse) { return 0.0; }
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
void main() { uint i=gl_GlobalInvocationID.x; if(i>=4096u) { float f=float(i-4096u); float y=150.0+mod(f*17.0,2100.0); float z=-3500.0+mod(f*43.0,7000.0); float C=502654.8246; outData[i]=vec4(far_density_sample_coverage(vec3(1.0,y,z),1.1),far_density_sample_coverage(vec3(C+1.0,y,z),1.1),far_density_sample_coverage(vec3(-1.0,y,z),1.1),far_density_sample_coverage(vec3(C-1.0,y,z),1.1)); return; } float f=float(i); vec3 p=vec3(12000.0+mod(f*37.0,18000.0),100.0+mod(f*19.0,2300.0),-4000.0+mod(f*29.0,8000.0)); float R=80000.0; float shell=R-p.y; vec3 world=vec3(shell*sin(p.x/R),R-shell*cos(p.x/R),p.z); vec3 canonical=to_cloud_space(world); float coverage=(i%7u)==0u?0.0:((i%7u)==1u?0.33:((i%7u)==2u?0.67:0.74+float(i%4u)*0.04)); if(i<128u) coverage=1.1; float weather_storm=testData[i].cloud_handoff.z; float weather_coverage=ring_cloud_weather_resolved_coverage(testData[i].cloud_weather_frame.w,weather_storm,canonical,testData[i].cloud_weather_params,testData[i].cloud_weather_frame); float weather_gate=ring_cloud_weather_clear_gate(weather_coverage,weather_storm,testData[i].cloud_weather_params); float far_coverage=testData[i].cloud_weather_params.y>0.0?mix(mix(0.70,0.90,weather_coverage),1.05,smoothstep(0.35,0.80,weather_storm)):coverage; float extra=texture(extra_noise,canonical.xz/1200.0).a; float live=sampleSceneLive(vec3(0.0),vec3(0.0),vec3(0.0),world,2400.0,100.0,extra,900.0,600.0,240.0,coverage,0.8,0.0,0.0,false,false); float current=far_density_core(canonical,far_coverage)*weather_gate; float policy=testData[i].cloud_weather_params.y>0.0?(weather_storm>.5?4.0:(weather_gate<.01?8.0:16.0)):float(i&1u); outData[i]=vec4(live,current,policy,abs(live-current)); }
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

# ---- S6 layer parity: far_layer_density (ring_far_cloud_layers.gdshaderinc, lowpass 0) against the live ring_layer_density ----------
# The near side runs lod 0 (no effectors or curl) with g_layer_extent 0 (no thin-layer overlap) and fine sun lighting, primed the way the
# march primes it (ring_layers_set_closure). Returns "" on success, else the failure message.
const LAYER_CASES := 2304
const LAYER_SAMPLE := "res://addons/SunshineClouds2/RingCloudLayerSample.glslinc"
const LAYER_BLOCK := "res://addons/SunshineClouds2/RingCloudLayers.glslinc"
const FAR_LAYERS_INCLUDE := "res://materials/ring_far_cloud_layers.gdshaderinc"

func _layers_parity() -> String:
	var sample := FileAccess.get_file_as_string(LAYER_SAMPLE); var block := FileAccess.get_file_as_string(LAYER_BLOCK)
	var far := FileAccess.get_file_as_string(FAR_LAYERS_INCLUDE); var weather := FileAccess.get_file_as_string(WEATHER_INCLUDE)
	var globals := _function(sample, "vec4 g_layer_low", "// Sun-march subsampling")
	var closure := _function(sample, "#ifndef RING_LAYER_WEATHER_CLOSE", "// Deck weight of a sample at")
	var density := _function(sample, "// Background density of one layer", "// Background density at a sample")
	var smooth_fn := _function(block, "float ring_cl_smooth(", "// 1 at s <= to")
	var resolve := _function(block, "vec4 ring_cloud_layer_resolve(", "// Hull of the volumetric")
	if far.is_empty() or weather.is_empty() or globals.is_empty() or closure.is_empty() or density.is_empty() or smooth_fn.is_empty() or resolve.is_empty():
		return "layer parity source extraction failed"
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null: return "Local RenderingDevice unavailable (layer parity)"
	var rd_source := RDShaderSource.new(); rd_source.source_compute = _layer_shader_source(weather, globals + smooth_fn + resolve + closure + density, _strip_uniforms(far))
	var spirv := rd.shader_compile_spirv_from_source(rd_source)
	if spirv.compile_error_compute != "": rd.free(); return "layer parity shader compile failed: " + spirv.compile_error_compute
	var shader := rd.shader_create_from_spirv(spirv)
	var config := _make_layer_config(); var table := _layer_table().to_byte_array()
	var config_buffer := rd.storage_buffer_create(config.size() * 4, config.to_byte_array())
	var output_buffer := rd.storage_buffer_create(LAYER_CASES * 32)
	var table_buffer := rd.uniform_buffer_create(table.size(), table)
	var sampler := _sampler(rd)
	var textures := [_gradient_texture(rd), _texture(rd, false, 32, 32, 1), _texture(rd, true, 8, 8, 8), _texture(rd, true, 8, 8, 8), _texture(rd, true, 8, 8, 8), _texture(rd, true, 8, 8, 8), _array_texture(rd), _texture(rd, false, 32, 1, 1)]
	var uniforms: Array[RDUniform] = []
	for binding in 4:
		var u := RDUniform.new(); u.binding = binding; u.uniform_type = [RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER, RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER, RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER, RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER][binding]
		u.add_id([config_buffer, output_buffer, table_buffer, table_buffer][binding]); uniforms.append(u)
	for binding in textures.size():
		var u := RDUniform.new(); u.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; u.binding = binding + 4; u.add_id(sampler); u.add_id(textures[binding]); uniforms.append(u)
	var set := rd.uniform_set_create(uniforms, shader, 0)
	if not set.is_valid(): rd.free(); return "layer parity uniform set creation failed"
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0); rd.compute_list_dispatch(list, LAYER_CASES, 1, 1); rd.compute_list_end(); rd.submit(); rd.sync()
	var results := rd.buffer_get_data(output_buffer).to_float32_array()
	for rid in [pipeline, set, config_buffer, output_buffer, table_buffer] + textures + [sampler, shader]: rd.free_rid(rid)
	rd.free()
	var positive := 0; var zero := 0; var closed := 0; var worst := 0.0; var worst_resolve_m := 0.0; var worst_resolve_f := 0.0
	for i in LAYER_CASES:
		var near := results[i * 8]; var far_density := results[i * 8 + 1]; var closure_value := results[i * 8 + 2]; var delta := results[i * 8 + 3]
		worst = maxf(worst, delta); worst_resolve_m = maxf(worst_resolve_m, results[i * 8 + 4]); worst_resolve_f = maxf(worst_resolve_f, results[i * 8 + 5])
		for value in [near, far_density, closure_value, delta, results[i * 8 + 4], results[i * 8 + 5]]:
			if is_nan(value) or is_inf(value): return "non-finite layer parity value at %d" % i
		if delta > 0.00001: return "far/near layer density mismatch at %d: near=%f far=%f delta=%f closure=%f case=%s" % [i, near, far_density, delta, closure_value, str(results.slice(i * 8 + 6, i * 8 + 8))]
		if results[i * 8 + 4] > 0.001 or results[i * 8 + 5] > 0.000001: return "far/near layer resolve mismatch at %d: meters=%f fraction=%f" % [i, results[i * 8 + 4], results[i * 8 + 5]]
		if near > .001: positive += 1
		if near == 0.0: zero += 1
		if closure_value > .5 and near > 0.0: closed += 1
	if positive * 10 < LAYER_CASES or zero * 10 < LAYER_CASES or closed == 0: return "vacuous layer fixture: positive=%d zero=%d closed=%d of %d" % [positive, zero, closed, LAYER_CASES]
	print("FAR_CLOUD_LAYERS_PARITY=PASS cases=%d positive=%d zero=%d closed=%d max_delta=%f resolve_meters=%f resolve_fraction=%f" % [LAYER_CASES, positive, zero, closed, worst, worst_resolve_m, worst_resolve_f])
	return ""

# The shipped table (resources/clouds/cloud_layer_table.tres) as the 41 vec4 of RingCloudLayersBlock.
func _layer_table() -> PackedFloat32Array:
	var v: Array[Vector4] = []
	for i in 41: v.append(Vector4.ZERO)
	v[0] = Vector4(18000.0, 0, 0, 0)
	var layers := [
		[Vector4(2000, 500, 5000, 0), Vector4(800, 2800, 400, 1500), Vector4(3, 1, 0.5, 4e-4), Vector4(0.55, 1, 1, 0), Vector4.ZERO],
		[Vector4(2400, 2000, 7000, 1), Vector4(1000, 2500, 1000, 4000), Vector4(2.5, 1.3, 0.6, 3e-4), Vector4(0.5, 1, 1, 0), Vector4(13000, 0, 7000, 0)],
		[Vector4(9500, 9500, 13000, 2), Vector4(500, 1500, 500, 2000), Vector4(6, 0.5, 0.3, 6e-5), Vector4(0.7, 1, 0.12, 1), Vector4(37000, 0, 21000, 0)]]
	for k in 3:
		for part in 4: v[5 + k * 4 + part] = layers[k][part]
		v[37 + k] = layers[k][4]
	var out := PackedFloat32Array()
	for e in v: out.append_array([e.x, e.y, e.z, e.w])
	return out

# Per case (7 vec4): wind, wind_power, weather params, weather frame, map, (x, altitude as a fraction of the layer band, z, layer), (coverage bias, owned level, owned, 0).
func _make_layer_config() -> PackedFloat32Array:
	var rng := RandomNumberGenerator.new(); rng.seed = 6006
	var coverage_values := [0.0, 0.1, 0.3, 0.5, 0.7, 0.9, 1.0]; var weather_values := [0.0, 0.0, 0.15, 0.4, 0.7, 0.9, 1.0]
	var band_fractions := [-0.1, 0.0, 0.02, 0.08, 0.25, 0.5, 0.75, 0.92, 0.98, 1.0, 1.1]
	var biases := [0.0, 0.1, -0.1]; var levels := [0.0, 0.5, 0.9]; var owned_values := [0.0, 0.5]
	var out := PackedFloat32Array()
	for i in LAYER_CASES:
		var weather_on := i % 4 < 2
		var windy := i % 3 != 0
		out.append_array([0.35, -0.2, 0, 0.8 if i % 5 != 0 else 0.5, 180.0 if windy else 0.0, 0, 0, 0])
		out.append_array([(1.0 if (i / 4) % 2 == 0 else 0.6) if weather_on else 0.0, 64000.0 if weather_on else 0.0, 0.12 if (i / 8) % 2 == 0 else 0.05, 761.0 if weather_on else 0.0])
		out.append_array([502654.8246 if weather_on else 0.0, 2300.0 if weather_on else 0.0, -700.0 if weather_on else 0.0, 0.0])
		out.append_array([coverage_values[rng.randi() % 7], coverage_values[rng.randi() % 7], coverage_values[rng.randi() % 7], weather_values[rng.randi() % 7]])
		out.append_array([rng.randf_range(0.0, 500000.0), band_fractions[rng.randi() % 11], rng.randf_range(-50000.0, 50000.0), float(i % 2)])
		out.append_array([biases[rng.randi() % 3], levels[rng.randi() % 3], owned_values[rng.randi() % 2], 0.0])
	return out

# 8-row layer gradient (rows 2k / 2k + 1 = layer k convective / stratiform) with a distinct value per row and a hump along the height axis.
func _gradient_texture(rd: RenderingDevice) -> RID:
	var pixels := PackedFloat32Array()
	for row in 8:
		for x in 32:
			var hump := 0.5 + 0.5 * sin(PI * (float(x) + 0.5) / 32.0)
			pixels.append_array([(0.45 + 0.07 * row) * hump + 0.05, 0.3 + 0.08 * row, 0.6 + 0.05 * row, 0.1 * row])
	var f := RDTextureFormat.new(); f.texture_type = RenderingDevice.TEXTURE_TYPE_2D; f.width = 32; f.height = 8; f.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; f.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
	return rd.texture_create(f, RDTextureView.new(), [pixels.to_byte_array()])

# Placeholder for far_macro_map (declared by the far include, never sampled by the density path).
func _array_texture(rd: RenderingDevice) -> RID:
	var pixels := PackedFloat32Array(); pixels.resize(4 * 4 * 4)
	var f := RDTextureFormat.new(); f.texture_type = RenderingDevice.TEXTURE_TYPE_2D_ARRAY; f.width = 4; f.height = 4; f.array_layers = 2; f.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; f.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
	return rd.texture_create(f, RDTextureView.new(), [pixels.to_byte_array(), pixels.to_byte_array()])

func _layer_shader_source(weather: String, near_source: String, far: String) -> String:
	return """#version 450
layout(local_size_x=1) in;
struct LCase { vec4 wind; vec4 wind_power; vec4 weather_params; vec4 weather_frame; vec4 map; vec4 pos; vec4 misc; };
layout(set=0,binding=0,std430) readonly buffer Config { LCase cases[]; };
layout(set=0,binding=1,std430) writeonly buffer Output { vec4 outData[]; };
// The same 41 vec4 seen as the near std140 block and as the far uniform array.
layout(set=0,binding=2,std140) uniform RingCloudLayersBlock { vec4 header0; vec4 header1; vec4 header2; vec4 header3; vec4 sun; vec4 layers[16]; vec4 genus[16]; vec4 noise[4]; } ringCloudLayers;
layout(set=0,binding=3,std140) uniform FarLayerBlock { vec4 far_cloud_layers[41]; };
layout(binding=4) uniform sampler2D layer_heightmask;
layout(binding=5) uniform sampler2D extra_large_noise;
layout(binding=6) uniform sampler3D large_noise;
layout(binding=7) uniform sampler3D noise_medium;
layout(binding=8) uniform sampler3D noise_small;
layout(binding=9) uniform sampler3D curl_noise;
layout(binding=10) uniform sampler2DArray far_macro_map;
layout(binding=11) uniform sampler2D far_layer_atlas;
struct RingWeatherSystemsStub { vec4 header0; };
RingWeatherSystemsStub ringWeatherSystems;
struct GenericDataMembers { vec2 WindDirection; float windSweptRange; float windSweptPower; float pointEffectorCount; vec3 extralargenoiseposition; float extralargenoisescale; vec4 cloud_weather_params; vec4 cloud_weather_frame; };
struct GenericDataStub { GenericDataMembers data; };
GenericDataStub genericData;
struct PointEffector { vec3 position; float radius; float power; float attenuation; };
PointEffector pointEffectors[1];
float g_ws_storm_owned = 0.0;
vec4 cloud_weather_params = vec4(0.0);
vec4 cloud_weather_frame = vec4(0.0);
vec4 far_ws_header = vec4(0.5, 0.82, 0.0, 0.0);
// Inert bindings of the far include's macro-map and sculpt paths (compiled, never called).
vec4 far_macro_map_a = vec4(0.0);
vec4 far_macro_map_b = vec4(0.0);
bool far_layer_atlas_enabled = false;
#define far_fine_atlas far_layer_atlas
vec4 far_fine_a = vec4(0.0);
vec4 far_fine_b = vec4(0.0);
vec2 far_fine_wind = vec2(0.0);
int far_sculpt_count = 0;
vec4 far_sculpt_a[8];
vec4 far_sculpt_b[8];
vec4 far_layers_march = vec4(1.0, 0.746, 958.0, 0.0);
#define far_cloud_layer_gradient layer_heightmask
#define TEST_LARGE_POS vec3(1500.0, 10.0, -900.0)
#define TEST_MEDIUM_POS vec3(-700.0, 0.0, 300.0)
#define TEST_DETAIL_POS vec3(250.0, 0.0, -40.0)
#define far_density_extra_large_noise_patterns extra_large_noise
#define far_density_large_scale_noise large_noise
#define far_density_medium_scale_noise noise_medium
#define far_density_small_scale_noise noise_small
#define far_density_extra_large_position genericData.data.extralargenoiseposition
#define far_density_large_position TEST_LARGE_POS
#define far_density_medium_position TEST_MEDIUM_POS
#define far_density_detail_position TEST_DETAIL_POS
#define far_density_extra_large_scale genericData.data.extralargenoisescale
#define far_density_large_scale 900.0
#define far_density_medium_scale 600.0
#define far_density_small_scale 240.0
#define far_density_clouds_detail_power 0.8
#define far_density_wind_swept_range genericData.data.windSweptRange
#define far_density_wind_swept_strength genericData.data.windSweptPower
#define far_density_wind_direction vec3(genericData.data.WindDirection.x, 0.0, genericData.data.WindDirection.y)
""" + weather + near_source + far + """
void main() {
	uint i = gl_GlobalInvocationID.x;
	LCase c = cases[i];
	genericData.data.WindDirection = c.wind.xy; genericData.data.windSweptRange = c.wind.w; genericData.data.windSweptPower = c.wind_power.x;
	genericData.data.pointEffectorCount = 0.0; genericData.data.extralargenoiseposition = vec3(400.0, 0.0, -250.0); genericData.data.extralargenoisescale = 1200.0;
	genericData.data.cloud_weather_params = c.weather_params; genericData.data.cloud_weather_frame = c.weather_frame;
	cloud_weather_params = c.weather_params; cloud_weather_frame = c.weather_frame;
	float bias = c.misc.x; float ownedLevel = c.misc.y; float owned = c.misc.z; vec4 map = c.map; int layer = int(c.pos.w + 0.5);
	ringWeatherSystems.header0 = vec4(0.0, 0.0, 0.0, bias); far_ws_header = vec4(0.5, 0.82, bias, 0.0);
	// Near priming, as the march does it: resolve both layers from the map value, then the closures.
	g_layer_low = ring_cloud_layer_resolve(0, map); g_layer_mid = ring_cloud_layer_resolve(1, map);
	g_layer_weather = map.w; g_ws_storm_owned = owned; g_layer_extent = 0.0;
	ring_layers_set_closure(ownedLevel);
	vec4 col = layer == 0 ? g_layer_low : g_layer_mid;
	vec3 canonical = vec3(c.pos.x, col.y + c.pos.y * (col.z - col.y), c.pos.z);
	float extra = texture(extra_large_noise, (canonical.xz - genericData.data.extralargenoiseposition.xz) / genericData.data.extralargenoisescale).a;
	float nearDensity = ring_layer_density(layer, col, canonical, ownedLevel, extra, TEST_LARGE_POS, TEST_MEDIUM_POS, TEST_DETAIL_POS, 900.0, 600.0, 240.0, 0.8, 0.0, 0.0, false, false);
	vec4 farCol = far_layers_resolve(layer, map);
	float farClosure = far_layers_closure(farCol, map.w, owned, ownedLevel);
	float farDensity = far_layer_density(layer, farCol, canonical, ownedLevel, farClosure, 0.0);
	int k = int(i % 3u);
	vec4 a = ring_cloud_layer_resolve(k, map); vec4 b = far_layers_resolve(k, map);
	outData[i * 2u] = vec4(nearDensity, farDensity, farClosure, abs(nearDensity - farDensity));
	outData[i * 2u + 1u] = vec4(max(abs(a.y - b.y), abs(a.z - b.z)), max(abs(a.x - b.x), abs(a.w - b.w)), canonical.y, float(layer));
}
"""
