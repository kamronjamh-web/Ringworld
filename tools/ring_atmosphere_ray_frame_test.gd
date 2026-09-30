extends SceneTree
# Compiles the production finite-cylinder path include. The reference below is
# deliberately scalar GDScript (double), rather than a copy of shader helpers.
const INCLUDE_PATH := "res://materials/ring_atmosphere_paths.gdshaderinc"
const SHARED_INCLUDE_PATH := "res://materials/ring_atmosphere_shared.gdshaderinc"
const SCALES := [1.0, 0.001]
const CASES := [
	{"name":"zero", "r":5000000.0, "h":100.0, "target":[0.0, 0.0, 0.0], "width":1000.0},
	{"name":"outward", "r":5000000.0, "h":100.0, "target":[100000.0, 0.0, 0.0], "width":1000.0},
	{"name":"tangent_z", "r":5000000.0, "h":100.0, "target":[0.0, 0.0, 100000.0], "width":1000.0},
	{"name":"across_axis", "r":5000000.0, "h":100.0, "target":[0.0, 4000.0, 0.0], "width":1000.0},
	{"name":"left_width", "r":5000000.0, "h":100.0, "target":[-100000.0, -6000.0, 900.0], "width":1000.0},
	{"name":"right_width", "r":5000000.0, "h":100.0, "target":[-100000.0, 6000.0, -900.0], "width":1000.0},
	{"name":"multi_direction", "r":5000000.0, "h":100.0, "target":[-100000.0, 700.0, 50000.0], "width":1000.0},
	{"name":"above_air", "r":5000000.0, "h":25000.0, "target":[-100000.0, 0.0, 0.0], "width":1000.0},
	{"name":"mega_tangent", "r":75000000000.0, "h":100.0, "target":[0.0, 0.0, 200000.0], "width":1000.0},
	{"name":"mega_opposite", "r":75000000000.0, "h":100.0, "target":[-150000000000.0, 0.0, 0.0], "width":1000.0},
	{"name":"wall_negative_edge_toward", "r":5000000.0, "h":100.0, "target":[-100000.0, 200.0, 0.0], "negative":0.0, "positive":2000.0},
	{"name":"wall_positive_edge_away", "r":5000000.0, "h":100.0, "target":[-100000.0, 200.0, 0.0], "negative":2000.0, "positive":0.0},
	{"name":"wall_negative_outside_toward", "r":5000000.0, "h":100.0, "target":[-100000.0, 500.0, 0.0], "negative":-50.0, "positive":2050.0},
	{"name":"wall_positive_outside_away", "r":5000000.0, "h":100.0, "target":[-100000.0, -500.0, 0.0], "negative":2050.0, "positive":-50.0},
	{"name":"certified_above_outward", "r":5000000.0, "h":50000.0, "target":[100000.0, 0.0, 0.0], "negative":1000.0, "positive":1000.0},
	{"name":"wall_outside_parallel", "r":5000000.0, "h":100.0, "target":[-100000.0, 0.0, 0.0], "negative":-10.0, "positive":2010.0},
	{"name":"mega_deep", "r":75000000000.0, "h":-2000.0, "target":[-1000000.0, 500.0, 20000.0], "width":1000.0},
	{"name":"mega_high_outward", "r":75000000000.0, "h":100000.0, "target":[1000000.0, 0.0, 0.0], "width":1000.0},
]

func _init() -> void:
	call_deferred("run")

func run() -> void:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		return fail("no local RenderingDevice")
	var include := FileAccess.get_file_as_string(INCLUDE_PATH)
	var shared_include := FileAccess.get_file_as_string(SHARED_INCLUDE_PATH)
	if include.is_empty() or shared_include.is_empty():
		rd.free()
		return fail("missing production include")
	var shader_source := RDShaderSource.new()
	shader_source.source_compute = build_compute_source(shared_include + "\n" + include)
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		rd.free()
		return fail(spirv.compile_error_compute)
	var shader := rd.shader_create_from_spirv(spirv)
	var inputs := PackedFloat32Array()
	var expanded := []
	for scale in SCALES:
		for test_case in CASES:
			expanded.append({"case":test_case, "scale":scale})
			var target: Array = test_case["target"]
			# World coordinates and material radii are intentionally scaled here.
			var negative := float(test_case.get("negative", test_case.get("width",1000.0)))
			var positive := float(test_case.get("positive", test_case.get("width",1000.0)))
			inputs.append_array([float(test_case["r"]) * scale, float(test_case["h"]), scale, 0.0, float(target[0]) * scale, float(target[1]) * scale, float(target[2]) * scale, 0.0, 0.0, negative, positive, 0.0])
	var input_buffer := rd.storage_buffer_create(inputs.size() * 4, inputs.to_byte_array())
	var output_buffer := rd.storage_buffer_create(expanded.size() * 16)
	var input_uniform := RDUniform.new()
	input_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
	input_uniform.binding = 0
	input_uniform.add_id(input_buffer)
	var output_uniform := RDUniform.new()
	output_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
	output_uniform.binding = 1
	output_uniform.add_id(output_buffer)
	var uniform_set := rd.uniform_set_create([input_uniform, output_uniform], shader, 0)
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline)
	rd.compute_list_bind_uniform_set(list, uniform_set, 0)
	rd.compute_list_dispatch(list, expanded.size(), 1, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()
	var output := rd.buffer_get_data(output_buffer).to_float32_array()
	rd.free_rid(uniform_set)
	rd.free_rid(pipeline)
	rd.free_rid(input_buffer)
	rd.free_rid(output_buffer)
	rd.free_rid(shader)
	rd.free()
	var evidence := []
	for index in expanded.size():
		var item: Dictionary = expanded[index]
		var test_case: Dictionary = item["case"]
		var expected := reference_paths(test_case["r"], test_case["h"], test_case["target"], float(test_case.get("negative",test_case.get("width",1000.0))), float(test_case.get("positive",test_case.get("width",1000.0))))
		var actual := PackedFloat64Array([output[index * 4], output[index * 4 + 1], output[index * 4 + 2]])
		for component in 3:
			if not close_enough(actual[component], expected[component]):
				return fail("%s scale=%s component=%d gpu=%s cpu=%s" % [test_case["name"], item["scale"], component, actual, expected])
		if abs((actual[1] + actual[2]) - actual[0]) > 1.1:
			return fail("partition %s scale=%s" % [test_case["name"], item["scale"]])
		evidence.append({"name":test_case["name"], "scale":item["scale"], "gpu":actual, "reference":expected})
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/ring-atmosphere-ray-frame"))
	var file := FileAccess.open("res://.godot/ring-atmosphere-ray-frame/results.json", FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"passed":true,"cases":evidence}, "\t"))
		file.close()
	print("RING_ATMOSPHERE_RAY_FRAME_TEST=PASS cases=%d scales=%d" % [CASES.size(), SCALES.size()])
	quit(0)

func build_compute_source(include: String) -> String:
	var globals := "float ring_radius_meters; float atmosphere_floor_depth_meters; float atmosphere_boundary_height_meters; float atmosphere_upper_height_meters;\n"
	for line in include.split("\n"):
		if line.strip_edges().begins_with("uniform "):
			globals += line.strip_edges().trim_prefix("uniform ").split("=")[0].strip_edges().trim_suffix(";") + ";\n"
			include = include.replace(line, "")
	var main := """
layout(local_size_x=1) in;
layout(set=0,binding=0,std430) readonly buffer Input { vec4 values[]; } input_data;
layout(set=0,binding=1,std430) writeonly buffer Output { vec4 values[]; } output_data;
void main() {
	uint index = gl_GlobalInvocationID.x;
	vec4 header = input_data.values[index * 3u];
	vec4 target = input_data.values[index * 3u + 1u];
	vec4 frame = input_data.values[index * 3u + 2u];
	ring_atmosphere_ray_frame_valid = true;
	ring_atmosphere_camera_world_position = vec3(0.0, frame.x * header.z, 0.0);
	ring_atmosphere_camera_height_meters = header.y;
	ring_atmosphere_outward_world = vec3(1.0, 0.0, 0.0);
	ring_atmosphere_axis_world = vec3(0.0, 1.0, 0.0);
	ring_atmosphere_sidewall_negative_meters = frame.y;
	ring_atmosphere_sidewall_positive_meters = frame.z;
	ring_atmosphere_coordinate_scale = header.z;
	ring_radius_meters = header.x;
	atmosphere_floor_depth_meters = 2500.0 * header.z;
	atmosphere_boundary_height_meters = 2500.0 * header.z;
	atmosphere_upper_height_meters = 20000.0 * header.z;
	output_data.values[index] = vec4(ring_atmosphere_first_air_paths(target.xyz), 1.0);
}
"""
	return "#version 450\n" + globals + include + "\n" + main

func reference_paths(radius: float, height: float, target: Array, negative: float, positive: float) -> PackedFloat64Array:
	var tx := float(target[0])
	var ty := float(target[1])
	var tz := float(target[2])
	var length_m := sqrt(tx * tx + ty * ty + tz * tz)
	if length_m <= 0.0001:
		return PackedFloat64Array([0.0, 0.0, 0.0])
	var ray := PackedFloat64Array([tx / length_m, ty / length_m, tz / length_m])
	var outer := layer_interval(radius, height, ray, -2500.0, length_m, negative, positive)
	var upper := layer_interval(radius, height, ray, 20000.0, length_m, negative, positive)
	var air := first_shell_interval(outer, upper)
	if air[1] <= air[0]:
		return PackedFloat64Array([0.0, 0.0, 0.0])
	var boundary := layer_interval(radius, height, ray, 2500.0, length_m, negative, positive)
	var air_length := air[1] - air[0]
	var boundary_length := air_length
	if boundary[1] > boundary[0]:
		boundary_length = max(0.0, min(air[1], boundary[0]) - air[0]) + max(0.0, air[1] - max(air[0], boundary[1]))
	return PackedFloat64Array([air_length, boundary_length, max(0.0, air_length - boundary_length)])

func layer_interval(radius: float, height: float, ray: PackedFloat64Array, layer_height: float, length_m: float, negative: float, positive: float) -> PackedFloat64Array:
	var radial_a := ray[0] * ray[0] + ray[2] * ray[2]
	var b := 2.0 * (radius - height) * ray[0]
	var c := (layer_height - height) * (2.0 * radius - height - layer_height)
	var result := PackedFloat64Array([1.0, 0.0])
	if radial_a <= 0.000000000001:
		result = PackedFloat64Array([0.0, length_m]) if c <= 0.0 else result
	else:
		var discriminant := b * b - 4.0 * radial_a * c
		if discriminant < 0.0:
			return result
		var signed_root := sqrt(discriminant) if b >= 0.0 else -sqrt(discriminant)
		var q := -0.5 * (b + signed_root)
		var t0 := q / radial_a if abs(q) > 0.000001 else -b / (2.0 * radial_a)
		var t1 := c / q if abs(q) > 0.000001 else t0
		result = PackedFloat64Array([min(t0, t1), max(t0, t1)])
	var axis_direction := ray[1]
	if abs(axis_direction) > 0.000001:
		var wall0 := -negative / axis_direction
		var wall1 := positive / axis_direction
		result[0] = max(result[0], min(wall0, wall1))
		result[1] = min(result[1], max(wall0, wall1))
	elif negative < 0.0 or positive < 0.0:
		return PackedFloat64Array([1.0, 0.0])
	result[0] = max(result[0], 0.0)
	result[1] = min(result[1], length_m)
	return result

func first_shell_interval(outer: PackedFloat64Array, upper: PackedFloat64Array) -> PackedFloat64Array:
	if upper[1] <= upper[0]:
		return outer
	if upper[0] <= outer[0]:
		return PackedFloat64Array([max(outer[0], upper[1]), outer[1]])
	return PackedFloat64Array([outer[0], min(outer[1], upper[0])])

func close_enough(actual: float, expected: float) -> bool:
	return abs(actual - expected) <= max(1.0, abs(expected) * 0.001)

func fail(message: String) -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/ring-atmosphere-ray-frame"))
	var file := FileAccess.open("res://.godot/ring-atmosphere-ray-frame/results.json", FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"passed":false,"failure":message}, "\t"))
		file.close()
	push_error("RING_ATMOSPHERE_RAY_FRAME_TEST=FAIL " + message)
	quit(1)
