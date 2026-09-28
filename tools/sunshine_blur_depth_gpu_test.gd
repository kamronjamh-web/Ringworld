extends SceneTree

## Local-RD contract for the production depth-aware bicubic reconstruction.
## Run with a display GPU; this fixture deliberately compiles the helper from
## SunshineCloudsPostCompute.comp instead of carrying a shader copy.
const SIZE := 32

func _init() -> void:
	var post := FileAccess.get_file_as_string("res://addons/SunshineClouds2/SunshineCloudsPostCompute.comp")
	var begin := post.find("float w0")
	var end := post.find("vec4 radialBlurData", begin)
	if begin < 0 or end < begin or post.find("texture2D_bicubic_depth_aware", begin) < 0:
		_fail("production depth-aware bicubic helper missing")
		return
	if post.find("return weightSum > 0.000001 ? sum / weightSum : centerColor", begin) < 0:
		_fail("production no-compatible-tap fallback changed")
		return
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("Local RenderingDevice unavailable; run from a display session")
		return
	var helper := post.substr(begin, end - begin)
	var source := "#version 450\n" + helper + "\n" + _kernel()
	var shader_source := RDShaderSource.new()
	shader_source.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		rd.free()
		_fail(spirv.compile_error_compute)
		return
	var color := PackedFloat32Array()
	var breaks := PackedFloat32Array()
	var homogeneous := PackedFloat32Array()
	var checker := PackedFloat32Array()
	for y: int in range(SIZE):
		for x: int in range(SIZE):
			var dark_side := x >= SIZE / 2 and y >= SIZE / 2
			color.append_array([0.08 if dark_side else 1.0, 0.08 if dark_side else 1.0, 0.08 if dark_side else 1.0, 1.0])
			breaks.append_array([0.0, 0.0, 0.0, 1.0 if dark_side else 0.0])
			var smooth := float(x + y) / float((SIZE - 1) * 2)
			homogeneous.append_array([smooth, smooth, smooth, 1.0])
			checker.append_array([0.0, 0.0, 0.0, 1.0 if x == 8 and y == 8 else 0.0])
	var shader := rd.shader_create_from_spirv(spirv)
	var output := rd.storage_buffer_create(SIZE * SIZE * 4 * 16)
	var sampler := _sampler(rd)
	var textures := [_texture(rd, color), _texture(rd, breaks), _texture(rd, homogeneous), _texture(rd, checker)]
	var uniforms: Array[RDUniform] = []
	for binding: int in range(4):
		var uniform := RDUniform.new()
		uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		uniform.binding = binding
		uniform.add_id(sampler)
		uniform.add_id(textures[binding])
		uniforms.append(uniform)
	var result := RDUniform.new()
	result.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
	result.binding = 4
	result.add_id(output)
	uniforms.append(result)
	var set := rd.uniform_set_create(uniforms, shader, 0)
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline)
	rd.compute_list_bind_uniform_set(list, set, 0)
	rd.compute_list_dispatch(list, 4, 4, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()
	var values := rd.buffer_get_data(output).to_float32_array()
	rd.free_rid(pipeline); rd.free_rid(set)
	for texture: RID in textures: rd.free_rid(texture)
	rd.free_rid(output); rd.free_rid(sampler); rd.free_rid(shader); rd.free()
	var boundary := _output(values, 16, 24, 0)
	var homogeneous_result := _output(values, 8, 8, 1)
	var radial := _output(values, 16, 24, 2)
	var fallback := _output(values, 8, 8, 3)
	for value: float in values:
		if not is_finite(value):
			_fail("nonfinite depth-aware reconstruction output")
			return
	print("SUNSHINE_BLUR_DEPTH_GPU_TEST=METRICS boundary=%s homogeneous=%s radial=%s fallback=%s" % [boundary, homogeneous_result, radial, fallback])
	if boundary[1] >= boundary[0] - 0.05:
		_fail("depth-aware reconstruction did not reduce bright-side bleed")
		return
	if absf(homogeneous_result[0] - homogeneous_result[1]) > 0.00001:
		_fail("homogeneous metadata changed bicubic output")
		return
	if radial[1] >= radial[0] - 0.05:
		_fail("depth-aware radial blur crossed the foreground break")
		return
	if absf(fallback[1] - fallback[2]) > 0.00001:
		_fail("no-compatible-tap fallback did not preserve center color")
		return
	print("SUNSHINE_BLUR_DEPTH_GPU_TEST=PASS bleed=%.6f->%.6f homogeneous=%.6f radial=%.6f->%.6f fallback=%.6f" % [boundary[0], boundary[1], homogeneous_result[1], radial[0], radial[1], fallback[1]])
	quit(0)

func _kernel() -> String:
	return """
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0) uniform sampler2D colorTex;
layout(set = 0, binding = 1) uniform sampler2D breakTex;
layout(set = 0, binding = 2) uniform sampler2D homogeneousTex;
layout(set = 0, binding = 3) uniform sampler2D checkerTex;
layout(set = 0, binding = 4, std430) buffer O { vec4 o[]; };
vec4 legacyRadial(vec4 startColor, vec2 uv, vec2 size) {
	vec4 sum = startColor; float count = 1.0;
	for (float d = 0.0; d < 6.28318530718; d += 6.28318530718 / 4.0)
		for (float i = 1.0; i <= 1.0; i += 1.0)
			{ sum += texture2D_bicubic(colorTex, uv + vec2(cos(d) * .7 / 32.0 * i, sin(d) * 2.0 / 32.0 * i), size); count += 1.0; }
	return sum / count;
}
void main() {
	uvec2 id = gl_GlobalInvocationID.xy;
	if (id.x >= 32u || id.y >= 32u) return;
	vec2 uv = (vec2(id) + vec2(.5)) / 32.0;
	vec2 size = vec2(32.0);
	uint n = (id.y * 32u + id.x) * 4u;
	vec4 center = texture(colorTex, uv);
	float centerBreak = depth_break_at_center(breakTex, uv, size);
	vec4 legacy = texture2D_bicubic(colorTex, uv, size);
	vec4 guarded = texture2D_bicubic_depth_aware(colorTex, breakTex, center, centerBreak, uv, size);
	o[n] = vec4(legacy.r, guarded.r, center.r, centerBreak);
	vec4 homCenter = texture(homogeneousTex, uv);
	vec4 homLegacy = texture2D_bicubic(homogeneousTex, uv, size);
	vec4 homGuarded = texture2D_bicubic_depth_aware(homogeneousTex, breakTex, homCenter, centerBreak, uv, size);
	o[n + 1u] = vec4(homLegacy.r, homGuarded.r, homCenter.r, 0.0);
	vec4 oldRadial = legacyRadial(center, uv, size);
	vec4 newRadial = radialBlurColor(center, colorTex, breakTex, uv, size, 4.0, 2.0 / 32.0, .7 / 32.0, 1.0);
	o[n + 2u] = vec4(oldRadial.r, newRadial.r, center.r, 0.0);
	vec2 fallbackUV = (vec2(id) + vec2(.25, .75)) / 32.0;
	float checkerBreak = depth_break_at_center(checkerTex, fallbackUV, size);
	vec4 fallback = texture2D_bicubic_depth_aware(homogeneousTex, checkerTex, vec4(.25), checkerBreak, fallbackUV, size);
	o[n + 3u] = vec4(0.0, fallback.r, .25, checkerBreak);
}
"""

func _sampler(rd: RenderingDevice) -> RID:
	var state := RDSamplerState.new()
	state.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	state.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	return rd.sampler_create(state)

func _texture(rd: RenderingDevice, pixels: PackedFloat32Array) -> RID:
	var format := RDTextureFormat.new()
	format.width = SIZE; format.height = SIZE
	format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT
	format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
	return rd.texture_create(format, RDTextureView.new(), [pixels.to_byte_array()])

func _output(values: PackedFloat32Array, x: int, y: int, slot: int) -> PackedFloat32Array:
	var index := (y * SIZE + x) * 16 + slot * 4
	return PackedFloat32Array([values[index], values[index + 1], values[index + 2], values[index + 3]])

func _fail(message: String) -> void:
	push_error("SUNSHINE_BLUR_DEPTH_GPU_TEST=FAIL " + message)
	quit(1)
