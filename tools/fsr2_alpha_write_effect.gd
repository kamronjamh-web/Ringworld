extends CompositorEffect

## Diagnostic CompositorEffect: overwrites only the alpha channel of the internal colour buffer at POST_TRANSPARENT with a constant
## (the Sunshine display pass writes vec4(rgb, 1.0) over every pixel). Used to test whether FSR2 reads colour alpha.
const SHADER := """
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba16f, set = 0, binding = 0) uniform restrict image2D color_image;
layout(push_constant, std430) uniform Params { vec4 alpha; } params;
void main() {
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(color_image);
	if (p.x >= size.x || p.y >= size.y) return;
	vec4 c = imageLoad(color_image, p);
	imageStore(color_image, p, vec4(c.rgb, params.alpha.x < 0.0 ? c.a : params.alpha.x));
}
"""
var alpha_value := 1.0 # < 0 = copy unchanged
var rd: RenderingDevice
var shader := RID()
var pipeline := RID()
var mutex := Mutex.new()
var alpha_samples: Array = []

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	enabled = true

func _render_callback(_type: int, render_data: RenderData) -> void:
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null: return
	if not pipeline.is_valid():
		rd = RenderingServer.get_rendering_device()
		var source := RDShaderSource.new(); source.language = RenderingDevice.SHADER_LANGUAGE_GLSL; source.source_compute = SHADER
		shader = rd.shader_create_from_spirv(rd.shader_compile_spirv_from_source(source)); pipeline = rd.compute_pipeline_create(shader)
	var size := buffers.get_internal_size()
	var uniform := RDUniform.new(); uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; uniform.binding = 0; uniform.add_id(buffers.get_color_layer(0, false))
	var set := rd.uniform_set_create([uniform], shader, 0)
	var push := PackedFloat32Array([alpha_value, 0, 0, 0]).to_byte_array()
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0)
	rd.compute_list_set_push_constant(list, push, push.size())
	rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
	rd.free_rid(set)
