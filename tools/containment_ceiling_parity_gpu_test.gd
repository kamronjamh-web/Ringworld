extends SceneTree
# Containment field WO6: the GLSL cloud ceiling (addons/SunshineClouds2/RingContainmentCeiling.glslinc, the code both Sunshine computes multiply into the
# density) against its CPU twin (ContainmentFieldGeometry.CloudCeilingAt / CloudDensityFactor) over 70 across values (both rims, the 7 km band edge,
# the habitat middle, outside the roots) x 6 altitudes around each ceiling, plus the zero profile (no field: exactly 1 and 18 km).
# Run (windowed Vulkan, not --headless): Godot --path . --script res://tools/containment_ceiling_parity_gpu_test.gd. Prints CONTAINMENT_CEILING_PARITY=PASS.
const INCLUDE := "res://addons/SunshineClouds2/RingContainmentCeiling.glslinc"
const SOURCE := """#version 450
layout(local_size_x = 64) in;
struct GenericData { vec4 ring_field_profile0; vec4 ring_field_profile1; };
layout(set = 0, binding = 0, std140) uniform U { GenericData data; } genericData;
layout(set = 0, binding = 1, std430) readonly buffer In { vec4 inp[]; };
layout(set = 0, binding = 2, std430) writeonly buffer Out { vec4 outp[]; };
%s
void main() {
	uint i = gl_GlobalInvocationID.x;
	vec4 q = inp[i];
	outp[i] = vec4(ring_containment_ceiling(q.x, q.y), ring_containment_ceiling_meters(q.x), 0.0, 0.0);
}
"""

func _init() -> void:
	call_deferred("_run")

func _fail(reason: String) -> void:
	printerr("CONTAINMENT_CEILING_PARITY=FAIL ", reason)
	quit(1)

func _run() -> void:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("Local RenderingDevice unavailable")
		return
	var probe = load("res://tools/ContainmentCeilingProbe.cs").new()
	var include_text := FileAccess.get_file_as_string(INCLUDE)
	if include_text.is_empty():
		_fail("include missing")
		return
	var source := RDShaderSource.new()
	source.source_compute = SOURCE % include_text
	var spirv := rd.shader_compile_spirv_from_source(source)
	if spirv.compile_error_compute != "":
		_fail("compile: " + spirv.compile_error_compute)
		return
	var shader := rd.shader_create_from_spirv(spirv)
	var acrosses: Array[float] = [-100.0, -64.0, -63.0, 0.0, 1.0, 10.0, 50.0, 100.0, 300.0, 600.0, 1000.0, 2000.0, 3000.0, 4000.0, 4400.0, 4500.0, 5000.0, 6000.0, 6900.0, 7000.0, 7063.0, 7064.0, 7065.0, 7100.0, 8000.0, 50000.0, 159000.0]
	for a in 27:
		acrosses.append(318000.0 - acrosses[a] if acrosses[a] < 8000.0 else acrosses[a] + 1234.5)
	for k in 16:
		acrosses.append(1.0 + k * k * 27.0)
	var inputs := PackedFloat32Array()
	var expected_factor := PackedFloat64Array()
	var expected_ceiling := PackedFloat64Array()
	for across in acrosses:
		var ceiling: float = probe.Ceiling(across)
		for altitude in [ceiling - 800.0, ceiling - 400.0, ceiling - 100.0, ceiling, ceiling + 300.0, 17900.0]:
			inputs.append_array(PackedFloat32Array([across, altitude, 0.0, 0.0]))
			expected_factor.append(probe.Factor(across, altitude))
			expected_ceiling.append(ceiling)
	var count := inputs.size() / 4
	var worst_factor := 0.0
	var worst_ceiling := 0.0
	var bitten := 0
	for with_field in [true, false]:
		var profile := PackedFloat32Array()
		if with_field:
			var p0: Vector4 = probe.Profile0
			var p1: Vector4 = probe.Profile1
			profile.append_array(PackedFloat32Array([p0.x, p0.y, p0.z, p0.w, p1.x, p1.y, p1.z, p1.w]))
		else:
			profile.resize(8)
		var ubo := rd.uniform_buffer_create(32, profile.to_byte_array())
		var input_buffer := rd.storage_buffer_create(inputs.size() * 4, inputs.to_byte_array())
		var output_buffer := rd.storage_buffer_create(count * 16)
		var uniforms: Array[RDUniform] = []
		for binding in 3:
			var u := RDUniform.new()
			u.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER if binding == 0 else RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
			u.binding = binding
			u.add_id([ubo, input_buffer, output_buffer][binding])
			uniforms.append(u)
		var set := rd.uniform_set_create(uniforms, shader, 0)
		var pipeline := rd.compute_pipeline_create(shader)
		var list := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(list, pipeline)
		rd.compute_list_bind_uniform_set(list, set, 0)
		rd.compute_list_dispatch(list, int((count + 63) / 64), 1, 1)
		rd.compute_list_end()
		rd.submit()
		rd.sync()
		var out := rd.buffer_get_data(output_buffer).to_float32_array()
		for i in count:
			var factor := out[i * 4]
			var ceiling := out[i * 4 + 1]
			if is_nan(factor) or is_nan(ceiling):
				_fail("NaN at %d (across %f altitude %f)" % [i, inputs[i * 4], inputs[i * 4 + 1]])
				return
			if with_field:
				worst_factor = maxf(worst_factor, absf(factor - expected_factor[i]))
				worst_ceiling = maxf(worst_ceiling, absf(ceiling - expected_ceiling[i]))
				if expected_factor[i] < 0.999:
					bitten += 1
				if inputs[i * 4 + 1] >= expected_ceiling[i] and factor != 0.0 and expected_ceiling[i] < 18000.0:
					_fail("density above the ceiling at across %f altitude %f: %f" % [inputs[i * 4], inputs[i * 4 + 1], factor])
					return
			elif factor != 1.0 or ceiling != 18000.0:
				_fail("zero profile is not the identity at across %f: factor %f ceiling %f" % [inputs[i * 4], factor, ceiling])
				return
		for rid in [pipeline, set, ubo, input_buffer, output_buffer]:
			rd.free_rid(rid)
	rd.free_rid(shader)
	rd.free()
	print("CONTAINMENT_CEILING_PARITY cases=%d bitten=%d worst_factor=%.6f worst_ceiling_m=%.4f" % [count, bitten, worst_factor, worst_ceiling])
	if bitten < 40:
		_fail("vacuous: only %d cases had a factor below 1" % bitten)
		return
	if worst_factor > 0.003 or worst_ceiling > 0.05:
		_fail("GLSL differs from the CPU twin: factor %f ceiling %f m" % [worst_factor, worst_ceiling])
		return
	print("CONTAINMENT_CEILING_PARITY=PASS")
	quit(0)
