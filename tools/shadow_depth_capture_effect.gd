extends CompositorEffect

## Test-only CompositorEffect for tools/shadow_quality_capture.gd: when armed, copies the main scene depth buffer into a readable R32F texture as LINEAR view
## distance in metres (a small compute pass; the engine depth texture is not CAN_COPY_FROM) and keeps the newest result. Not used by the game.
## depth value convention is derived from the camera projection (works for reverse-Z): d = P.w.z / (ndc_z + P.z.z); sky / beyond-far pixels come out huge.
const COPY_SHADER := """
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(r32f, set = 0, binding = 1) uniform restrict writeonly image2D out_image;
layout(push_constant, std430) uniform Params { vec4 proj; } params; // x = P.z.z, y = P.w.z
void main() {
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(out_image);
	if (p.x >= size.x || p.y >= size.y) return;
	float ndc = texelFetch(depth_tex, p, 0).r;
	float denom = ndc + params.proj.x;
	float d = abs(denom) < 1e-12 ? 1.0e9 : params.proj.y / denom;
	if (d < 0.0) d = 1.0e9;
	imageStore(out_image, p, vec4(min(d, 1.0e9), 0.0, 0.0, 0.0));
}
"""
var mutex := Mutex.new()
var armed := false
var result := {}
var rd: RenderingDevice
var shader := RID()
var pipeline := RID()
var sampler := RID()
var out_texture := RID()
var out_size := Vector2i.ZERO


func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	enabled = true


func arm() -> void:
	mutex.lock(); armed = true; result = {}; mutex.unlock()


## {size: Vector2i, depth: PackedFloat32Array (metres, row-major)} once captured, else {}.
func take() -> Dictionary:
	mutex.lock(); var copy := result; mutex.unlock()
	return copy


func _ensure_pipeline() -> bool:
	if pipeline.is_valid():
		return true
	rd = RenderingServer.get_rendering_device()
	var source := RDShaderSource.new(); source.language = RenderingDevice.SHADER_LANGUAGE_GLSL; source.source_compute = COPY_SHADER
	var spirv := rd.shader_compile_spirv_from_source(source)
	if spirv.compile_error_compute != "":
		push_error("depth copy shader: " + spirv.compile_error_compute); return false
	shader = rd.shader_create_from_spirv(spirv)
	pipeline = rd.compute_pipeline_create(shader)
	sampler = rd.sampler_create(RDSamplerState.new())
	return pipeline.is_valid()


func _render_callback(_type: int, render_data: RenderData) -> void:
	mutex.lock(); var active := armed; mutex.unlock()
	if not active:
		return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null or not _ensure_pipeline():
		return
	var depth: RID = buffers.get_depth_texture()
	if not depth.is_valid():
		return
	var projection: Projection = render_data.get_render_scene_data().get_cam_projection()
	var size := buffers.get_internal_size()
	if size != out_size:
		if out_texture.is_valid(): rd.free_rid(out_texture)
		var format := RDTextureFormat.new(); format.width = size.x; format.height = size.y; format.format = RenderingDevice.DATA_FORMAT_R32_SFLOAT
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
		out_texture = rd.texture_create(format, RDTextureView.new()); out_size = size
	var input := RDUniform.new(); input.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; input.binding = 0
	input.add_id(sampler); input.add_id(depth)
	var output := RDUniform.new(); output.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; output.binding = 1; output.add_id(out_texture)
	var uniform_set := rd.uniform_set_create([input, output], shader, 0)
	var push := PackedFloat32Array([projection.z.z, projection.w.z, 0.0, 0.0]).to_byte_array()
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, uniform_set, 0)
	rd.compute_list_set_push_constant(list, push, push.size())
	rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
	var data := rd.texture_get_data(out_texture, 0)
	rd.free_rid(uniform_set)
	mutex.lock()
	result = {"size": size, "depth": data.to_float32_array(), "projection_zz": projection.z.z, "projection_wz": projection.w.z}
	armed = false
	mutex.unlock()
