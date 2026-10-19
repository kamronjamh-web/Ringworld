extends SceneTree

## P5 of docs/atmosphere-haze-revamp-plan.md: transmittance-weighted cloud depth (.b channel).
## Runs the production helpers from SunshineCloudsDepth.glslinc (the same include the Sunshine march uses)
## over synthetic march sample lists on the GPU and compares to a double-precision GDScript reference and to
## the old "densest sample" rule. Run windowed Vulkan (not --headless):
##   godot --path . --script res://tools/cloud_depth_weighting_gpu_test.gd

const INCLUDE := "res://addons/SunshineClouds2/SunshineCloudsDepth.glslinc"
const MAX_SAMPLES := 8

# Each case: name, samples [[distance, density_added], ...], traveled, fallback, expected kind.
var _cases := [
	{"name": "thin veil over dense far cloud", "samples": [[500.0, 0.4], [5000.0, 1.0]], "traveled": 5000.0},
	{"name": "single dense cloud (rising density)", "samples": [[1000.0, 0.1], [1100.0, 0.3], [1200.0, 0.5], [1300.0, 0.8]], "traveled": 1400.0},
	{"name": "faint veil, no dense cloud", "samples": [[800.0, 0.02], [900.0, 0.02], [1000.0, 0.02]], "traveled": 3000.0},
	{"name": "empty ray falls back", "samples": [], "traveled": 2000.0},
	{"name": "mean clamped to traveled", "samples": [[900.0, 0.5]], "traveled": 600.0},
]
var _failed := false

func _init() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required (run windowed Vulkan, not --headless)")
		return
	call_deferred("_run")

func _reference(samples: Array, traveled: float, fallback: float) -> Array:
	var density := 0.0
	var sw := 0.0
	var swt := 0.0
	var highest := 0.0
	var highest_dist := fallback
	for s in samples:
		var w: float = float(s[1]) * (1.0 - clampf(density, 0.0, 1.0))
		sw += w
		swt += w * float(s[0])
		if float(s[1]) > highest:
			highest = float(s[1])
			highest_dist = float(s[0])
		density += float(s[1])
		if density >= 1.0:
			break
	var weighted := minf(traveled, swt / sw) if sw > 1e-5 else minf(traveled, fallback)
	return [weighted, minf(traveled, highest_dist)]

func _run() -> void:
	var include_text := FileAccess.get_file_as_string(INCLUDE)
	if include_text.is_empty():
		_fail("cannot read " + INCLUDE)
		return
	var source := """#version 450
%s
layout(local_size_x=1) in;
layout(set=0, binding=0, std430) readonly buffer Cases { vec4 v[]; } cases;
layout(set=0, binding=1, std430) writeonly buffer Results { float v[]; } results;
const int MAX_SAMPLES = %d;
void main() {
	uint i = gl_GlobalInvocationID.x;
	vec4 head = cases.v[i * uint(MAX_SAMPLES + 1)]; // x = sample count, y = traveled, z = fallback
	float density = 0.0, sw = 0.0, swt = 0.0;
	for (int s = 0; s < int(head.x); s++) {
		vec4 smp = cases.v[i * uint(MAX_SAMPLES + 1) + uint(s) + 1u];
		ring_cloud_depth_accumulate(sw, swt, density, smp.y, smp.x);
		density += smp.y;
		if (density >= 1.0) break;
	}
	results.v[i] = ring_cloud_depth_resolve(sw, swt, head.y, head.z);
}
""" % [include_text, MAX_SAMPLES]
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("no local RenderingDevice")
		return
	var shader_source := RDShaderSource.new()
	shader_source.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		_fail("compile error: " + spirv.compile_error_compute)
		return
	var shader := rd.shader_create_from_spirv(spirv)
	var data := PackedFloat32Array()
	for c in _cases:
		var samples: Array = c["samples"]
		data.append_array([float(samples.size()), float(c["traveled"]), 9000.0, 0.0])
		for k in MAX_SAMPLES:
			if k < samples.size():
				data.append_array([float(samples[k][0]), float(samples[k][1]), 0.0, 0.0])
			else:
				data.append_array([0.0, 0.0, 0.0, 0.0])
	var case_buffer := rd.storage_buffer_create(data.size() * 4, data.to_byte_array())
	var result_buffer := rd.storage_buffer_create(_cases.size() * 4)
	var uniforms: Array[RDUniform] = []
	for pair in [[0, case_buffer], [1, result_buffer]]:
		var u := RDUniform.new()
		u.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
		u.binding = pair[0]
		u.add_id(pair[1])
		uniforms.append(u)
	var uniform_set := rd.uniform_set_create(uniforms, shader, 0)
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline)
	rd.compute_list_bind_uniform_set(list, uniform_set, 0)
	rd.compute_list_dispatch(list, _cases.size(), 1, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()
	var out := rd.buffer_get_data(result_buffer).to_float32_array()
	rd.free()

	var gpu := {}
	for i in _cases.size():
		var c: Dictionary = _cases[i]
		var ref := _reference(c["samples"], float(c["traveled"]), 9000.0)
		gpu[i] = out[i]
		print("CLOUD_DEPTH case=%s gpu=%.2f reference=%.2f old_densest=%.2f" % [c["name"], out[i], ref[0], ref[1]])
		if absf(out[i] - float(ref[0])) > 0.5:
			_fail("%s: GPU %.2f != reference %.2f" % [c["name"], out[i], ref[0]])
	if _failed: return
	# Veil over dense far cloud: weights 0.4 @500 and 0.6 @5000 -> 3200 m, well short of the old densest-sample 5000 m.
	if absf(out[0] - 3200.0) > 1.0 or out[0] >= 5000.0 or out[0] <= 500.0:
		_fail("veil case should be the veil-weighted mean 3200 m, got %.2f" % out[0])
		return
	# Single cloud: between its first and densest sample (front-weighted), not at the densest sample.
	if not (out[1] > 1000.0 and out[1] < 1300.0):
		_fail("single cloud depth should be front-weighted inside (1000, 1300), got %.2f" % out[1])
		return
	if absf(out[2] - 900.0) > 2.0:
		_fail("faint veil depth should be ~900, got %.2f" % out[2])
		return
	if absf(out[3] - 2000.0) > 0.01:
		_fail("empty ray should fall back to traveled (2000), got %.2f" % out[3])
		return
	if absf(out[4] - 600.0) > 0.01:
		_fail("mean must clamp to traveled distance (600), got %.2f" % out[4])
		return
	print("CLOUD_DEPTH_WEIGHTING_GPU_TEST=PASS")
	quit(0)

func _fail(message: String) -> void:
	if _failed: return
	_failed = true
	push_error("CLOUD_DEPTH_WEIGHTING_GPU_TEST=FAIL " + message)
	quit(1)
