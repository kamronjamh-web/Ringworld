extends CompositorEffect

## Diagnostic CompositorEffect: copies the scene velocity buffer into a readable RG32F texture on the render thread (a small compute
## pass; the engine texture lacks CAN_COPY_FROM) for a few frames and records per-frame statistics (mean / mean-abs velocity, fraction
## of non-zero pixels, most common values) next to the projection jitter z.xy, so a static-camera capture shows whether the motion
## vectors carry a per-frame offset. Used by FSR2 stability diagnostics only.
const COPY_SHADER := """
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0) uniform sampler2D velocity_tex;
layout(rg32f, set = 0, binding = 1) uniform restrict writeonly image2D out_image;
void main() {
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(out_image);
	if (p.x >= size.x || p.y >= size.y) return;
	imageStore(out_image, p, vec4(texelFetch(velocity_tex, p, 0).xy, 0.0, 0.0));
}
"""
var mutex := Mutex.new()
var frames_left := 0
var records: Array = []
var previous_jitter := Vector2.ZERO
var stride := 4
var mask_path := "" # optional PNG of which pixels carry written motion vectors (first captured frame)
var rd: RenderingDevice
var shader := RID()
var pipeline := RID()
var sampler := RID()
var out_texture := RID()
var out_size := Vector2i.ZERO

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	needs_motion_vectors = true
	enabled = true

func capture(count: int) -> void:
	mutex.lock(); frames_left = count; records.clear(); mutex.unlock()

func take() -> Array:
	mutex.lock(); var result := records.duplicate(); mutex.unlock()
	return result

func _ensure_pipeline() -> bool:
	if pipeline.is_valid(): return true
	rd = RenderingServer.get_rendering_device()
	var source := RDShaderSource.new(); source.language = RenderingDevice.SHADER_LANGUAGE_GLSL; source.source_compute = COPY_SHADER
	var spirv := rd.shader_compile_spirv_from_source(source)
	if spirv.compile_error_compute != "": push_error("velocity copy shader: " + spirv.compile_error_compute); return false
	shader = rd.shader_create_from_spirv(spirv)
	pipeline = rd.compute_pipeline_create(shader)
	var state := RDSamplerState.new()
	sampler = rd.sampler_create(state)
	return pipeline.is_valid()

func _render_callback(_type: int, render_data: RenderData) -> void:
	var scene_data := render_data.get_render_scene_data()
	var projection: Projection = scene_data.get_cam_projection()
	var jitter := Vector2(projection.z.x, projection.z.y)
	var jitter_delta := jitter - previous_jitter
	previous_jitter = jitter
	mutex.lock(); var active := frames_left > 0; mutex.unlock()
	if not active: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null or not _ensure_pipeline(): return
	var velocity: RID = buffers.get_velocity_texture()
	var record := {"jitter": jitter, "jitter_delta_ndc": jitter_delta, "velocity_rid": velocity.get_id(),
		"color_rid": buffers.get_color_texture().get_id(), "depth_rid": buffers.get_depth_texture().get_id(),
		"time": Time.get_ticks_usec(), "cam": cam_string(scene_data.get_cam_transform()), "proj_x": "%.9f %.9f" % [projection.x.x, projection.y.y]}
	if not velocity.is_valid():
		record["error"] = "no velocity texture"
	else:
		var size := buffers.get_internal_size()
		if size != out_size:
			if out_texture.is_valid(): rd.free_rid(out_texture)
			var format := RDTextureFormat.new(); format.width = size.x; format.height = size.y; format.format = RenderingDevice.DATA_FORMAT_R32G32_SFLOAT
			format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
			out_texture = rd.texture_create(format, RDTextureView.new()); out_size = size
		var input := RDUniform.new(); input.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; input.binding = 0
		input.add_id(sampler); input.add_id(velocity)
		var output := RDUniform.new(); output.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; output.binding = 1; output.add_id(out_texture)
		var set := rd.uniform_set_create([input, output], shader, 0)
		var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0)
		rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1); rd.compute_list_end()
		var data := rd.texture_get_data(out_texture, 0)
		rd.free_rid(set)
		var sum := Vector2.ZERO; var sum_abs := Vector2.ZERO; var nonzero := 0; var count := 0
		var histogram := {}
		for y in range(0, size.y, stride):
			for x in range(0, size.x, stride):
				var offset := (y * size.x + x) * 8
				var v := Vector2(data.decode_float(offset), data.decode_float(offset + 4))
				sum += v; sum_abs += v.abs(); count += 1
				if v.length() > 1.0e-7: nonzero += 1
				var key := "%.6f,%.6f" % [v.x, v.y]
				histogram[key] = int(histogram.get(key, 0)) + 1
		if mask_path != "" and records.is_empty():
			# 255 = the object wrote a motion vector, 0 = FSR2 "derive from depth" sentinel (-1, -1); 128 = written but non-zero.
			var mask := PackedByteArray(); mask.resize(size.x * size.y)
			for i in size.x * size.y:
				var mx := data.decode_float(i * 8); var my := data.decode_float(i * 8 + 4)
				mask[i] = 0 if (mx <= -1.0 and my <= -1.0) else (255 if absf(mx) + absf(my) < 1.0e-5 else 128)
			Image.create_from_data(size.x, size.y, false, Image.FORMAT_L8, mask).save_png(mask_path)
		var top: Array = []
		for key in histogram: top.append([int(histogram[key]), key])
		top.sort_custom(func(a, b): return a[0] > b[0])
		record["size"] = size
		record["mean"] = sum / maxf(1.0, count); record["mean_abs"] = sum_abs / maxf(1.0, count)
		record["nonzero_fraction"] = float(nonzero) / maxf(1.0, count)
		record["top"] = top.slice(0, 5)
	mutex.lock(); records.append(record); frames_left -= 1; mutex.unlock()

func cam_string(t: Transform3D) -> String:
	return "o=(%.9f %.9f %.9f) x=(%.9f %.9f %.9f) z=(%.9f %.9f %.9f)" % [t.origin.x, t.origin.y, t.origin.z, t.basis.x.x, t.basis.x.y, t.basis.x.z,
		t.basis.z.x, t.basis.z.y, t.basis.z.z]
