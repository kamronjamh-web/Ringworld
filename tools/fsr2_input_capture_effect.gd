extends CompositorEffect

## Diagnostic CompositorEffect: copies the internal-resolution (jittered, pre-upscale) scene colour at POST_TRANSPARENT into a readable
## texture for a number of frames and keeps the linear luminance of a sparse pixel grid, so a test can compare the temporal noise of the
## upscaler's input with that of its output (an upscaler that accumulates history must reduce it). FSR2 stability diagnostics only.
const COPY_SHADER := """
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0) uniform sampler2D color_tex;
layout(rgba32f, set = 0, binding = 1) uniform restrict writeonly image2D out_image;
void main() {
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(out_image);
	if (p.x >= size.x || p.y >= size.y) return;
	imageStore(out_image, p, texelFetch(color_tex, p, 0));
}
"""
const GRID := 96 # samples per axis
var mutex := Mutex.new()
var frames_left := 0
var frames: Array[PackedFloat32Array] = []
var rd: RenderingDevice
var shader := RID()
var pipeline := RID()
var sampler := RID()
var out_texture := RID()
var out_size := Vector2i.ZERO

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	enabled = true

func capture(count: int) -> void:
	mutex.lock(); frames_left = count; frames.clear(); mutex.unlock()

func take() -> Array[PackedFloat32Array]:
	mutex.lock(); var result := frames.duplicate(); mutex.unlock()
	return result

func _render_callback(_type: int, render_data: RenderData) -> void:
	mutex.lock(); var active := frames_left > 0; mutex.unlock()
	if not active: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null: return
	if not pipeline.is_valid():
		rd = RenderingServer.get_rendering_device()
		var source := RDShaderSource.new(); source.language = RenderingDevice.SHADER_LANGUAGE_GLSL; source.source_compute = COPY_SHADER
		var spirv := rd.shader_compile_spirv_from_source(source)
		shader = rd.shader_create_from_spirv(spirv); pipeline = rd.compute_pipeline_create(shader); sampler = rd.sampler_create(RDSamplerState.new())
	var size := buffers.get_internal_size()
	if size != out_size:
		if out_texture.is_valid(): rd.free_rid(out_texture)
		var format := RDTextureFormat.new(); format.width = size.x; format.height = size.y; format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
		out_texture = rd.texture_create(format, RDTextureView.new()); out_size = size
	var input := RDUniform.new(); input.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; input.binding = 0
	input.add_id(sampler); input.add_id(buffers.get_color_layer(0, false))
	var output := RDUniform.new(); output.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; output.binding = 1; output.add_id(out_texture)
	var set := rd.uniform_set_create([input, output], shader, 0)
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0)
	rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
	var data := rd.texture_get_data(out_texture, 0)
	rd.free_rid(set)
	var lum := PackedFloat32Array(); lum.resize(GRID * GRID)
	for gy in GRID:
		for gx in GRID:
			var x := int((gx + 0.5) * size.x / GRID); var y := int((gy + 0.5) * size.y / GRID)
			var o := (y * size.x + x) * 16
			lum[gy * GRID + gx] = 0.2126 * data.decode_float(o) + 0.7152 * data.decode_float(o + 4) + 0.0722 * data.decode_float(o + 8)
	mutex.lock(); frames.append(lum); frames_left -= 1; mutex.unlock()

## Output side: linearised (sRGB 2.2) luminance of the same normalised grid in a captured RGBA8 display image.
static func sample_output(image: Image) -> PackedFloat32Array:
	var lum := PackedFloat32Array(); lum.resize(GRID * GRID)
	var data := image.get_data(); var w := image.get_width(); var h := image.get_height()
	for gy in GRID:
		for gx in GRID:
			var x := int((gx + 0.5) * w / GRID); var y := int((gy + 0.5) * h / GRID)
			var o := (y * w + x) * 4
			var l := (0.2126 * data[o] + 0.7152 * data[o + 1] + 0.0722 * data[o + 2]) / 255.0
			lum[gy * GRID + gx] = pow(l, 2.2)
	return lum

## Median and 90th percentile over grid points of the temporal coefficient of variation (stddev / mean), points with mean > floor only.
static func relative_noise(series: Array, floor_value: float) -> Dictionary:
	if series.size() < 2: return {}
	var values: Array[float] = []
	var means: Array[float] = []
	var count := (series[0] as PackedFloat32Array).size()
	for i in count:
		var sum := 0.0; var sum2 := 0.0
		for frame in series: var v := float((frame as PackedFloat32Array)[i]); sum += v; sum2 += v * v
		var mean := sum / series.size()
		if mean <= floor_value: continue
		var variance := maxf(0.0, sum2 / series.size() - mean * mean) * series.size() / (series.size() - 1)
		values.append(sqrt(variance) / mean); means.append(mean)
	values.sort(); means.sort()
	if values.is_empty(): return {}
	return {"median": values[values.size() / 2], "p90": values[int(0.9 * (values.size() - 1))], "points": values.size(), "mean_median": means[means.size() / 2], "mean_max": means[means.size() - 1]}
