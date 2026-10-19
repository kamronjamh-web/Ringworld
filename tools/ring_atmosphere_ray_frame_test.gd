extends SceneTree
# Compiles the production finite-cylinder path include. The reference below is
# deliberately scalar GDScript (double), rather than a copy of shader helpers.
const INCLUDE_PATH := "res://materials/ring_atmosphere_paths.gdshaderinc"
const SHARED_INCLUDE_PATH := "res://materials/ring_atmosphere_shared.gdshaderinc"
const HAZE_INCLUDE_PATH := "res://materials/ring_haze_shared.gdshaderinc"
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

var haze_case_count := 0

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
	var haze_error: String = await run_haze_tests()
	if haze_error != "":
		return fail(haze_error)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/ring-atmosphere-ray-frame"))
	var file := FileAccess.open("res://.godot/ring-atmosphere-ray-frame/results.json", FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"passed":true,"cases":evidence}, "\t"))
		file.close()
	print("RING_ATMOSPHERE_RAY_FRAME_TEST=PASS cases=%d scales=%d haze_cases=%d" % [CASES.size(), SCALES.size(), haze_case_count])
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

# ---------------------------------------------------------------------------------------------------------------------
# P1 physical haze (materials/ring_haze_shared.gdshaderinc): optical depth vs a 2000-step double-precision reference,
# physical invariants, and a Godot spatial-shader compile check.
# ---------------------------------------------------------------------------------------------------------------------
const HAZE_QUALITIES := [4, 8, 16]
const HAZE_REF_STEPS := 2000
const HAZE_TAU_REL_TOL := 0.01
const HAZE_TAU_ABS_FLOOR := 1.0e-6
const HAZE_SATURATED_TAU := 8.0
const HAZE_SIGMA_R := [5.802e-6, 13.558e-6, 33.1e-6]
const HAZE_H_R := 8000.0
const HAZE_H_A := 1500.0
const HAZE_H_MIST := 400.0
const HAZE_SIGMA_M0 := 4.0e-6
const HAZE_FLOOR_DEPTH := 2500.0
const HAZE_CAP := 20000.0

func haze_sigma_a() -> float:
	return maxf(3.912 / 180000.0 - HAZE_SIGMA_R[1] - HAZE_SIGMA_M0, 0.0)

func haze_cases() -> Array:
	var cases := []
	for test_case in CASES:
		var entry: Dictionary = test_case.duplicate()
		entry["mode"] = 0
		entry["pset"] = 0
		entry["sun"] = 60.0
		entry["negative"] = float(test_case.get("negative", test_case.get("width", 1000.0)))
		entry["positive"] = float(test_case.get("positive", test_case.get("width", 1000.0)))
		entry["h"] = float(test_case["h"])
		cases.append(entry)
	for elevation in [0.0, 2.0]:
		for distance_km in [5.0, 15.0, 100.0, 800.0, 2000.0]:
			cases.append(haze_ray_case("around_%sdeg_%skm" % [elevation, distance_km], elevation, distance_km * 1000.0, 300.0))
	for elevation in [10.0, 45.0, 90.0]:
		for distance_km in [10.0, 30.0, 100.0, 800.0]:
			cases.append(haze_ray_case("steep_%sdeg_%skm" % [elevation, distance_km], elevation, distance_km * 1000.0, 300.0))
	for target in [[0.0, 0.95, 0.3122], [-0.2, 0.9, 0.3873]]:
		var ray := Vector3(target[0], target[1], target[2]).normalized()
		var wall := haze_ray_case("sidewall_far_%s" % target[0], 0.0, 2000000.0, 300.0)
		wall["target"] = [ray.x * 2000000.0, ray.y * 2000000.0, ray.z * 2000000.0]
		cases.append(wall)
	var zero := haze_ray_case("zero_density", 10.0, 100000.0, 300.0)
	zero["pset"] = 2
	cases.append(zero)
	return cases

func haze_ray_case(case_name: String, elevation_deg: float, distance: float, height: float) -> Dictionary:
	var elevation := deg_to_rad(elevation_deg)
	return {"name":case_name, "r":5000000.0, "h":height, "mode":0, "pset":0, "sun":60.0, "negative":159000.0, "positive":159000.0,
		"target":[-sin(elevation) * distance, 0.0, cos(elevation) * distance]}

func haze_sky_case(case_name: String, elevation_deg: float, pset: int, sun: float) -> Dictionary:
	var elevation := deg_to_rad(elevation_deg)
	return {"name":case_name, "r":5000000.0, "h":300.0, "mode":1, "pset":pset, "sun":sun, "negative":159000.0, "positive":159000.0,
		"target":[-sin(elevation), 0.0, cos(elevation)]}

func haze_compute_source(include: String) -> String:
	var main := """
layout(local_size_x=1) in;
layout(set=0,binding=0,std430) readonly buffer Input { vec4 values[]; } input_data;
layout(set=0,binding=1,std430) writeonly buffer Output { vec4 values[]; } output_data;
void main() {
	uint index = gl_GlobalInvocationID.x;
	vec4 v0 = input_data.values[index * 3u];
	vec4 v1 = input_data.values[index * 3u + 1u];
	vec4 v2 = input_data.values[index * 3u + 2u];
	float radius = v0.x; float height = v0.y; float scale = v0.z; bool sky = v0.w > 0.5;
	vec3 outward = vec3(1.0, 0.0, 0.0); vec3 axis = vec3(0.0, 1.0, 0.0); vec3 camera = vec3(0.0);
	RingHazeParams p = ring_haze_params_default();
	int pset = int(v2.z + 0.5);
	if (pset == 1) { p.sigma_A = 0.0; p.sigma_M0 = 0.0; }
	if (pset == 2) { p.sigma_R = vec3(0.0); p.sigma_A = 0.0; p.sigma_M0 = 0.0; }
	if (pset == 3) { p.day = 0.0; }
	p.quality = int(v1.w + 0.5);
	float sun = radians(v2.w);
	p.to_sun = vec3(-sin(sun), 0.0, cos(sun));
	vec3 target = sky ? camera + normalize(v1.xyz) * (2.0 * radius * scale) : v1.xyz;
	vec3 ray = normalize(target - camera);
	vec2 interval = ring_haze_air_interval(target, camera, height, outward, axis, v2.x, v2.y, scale, radius, 2500.0, p.cap_height);
	RingHazeOptics o = ring_haze_optics(p, ray, interval, height, outward, axis, radius);
	RingHaze haze = sky ? ring_haze_eval_sky(p, v1.xyz, camera, height, outward, axis, v2.x, v2.y, scale, radius, 2500.0)
		: ring_haze_eval(p, target, camera, height, outward, axis, v2.x, v2.y, scale, radius, 2500.0);
	output_data.values[index * 5u] = vec4(o.tau_R, 0.0);
	output_data.values[index * 5u + 1u] = vec4(o.tau_A, o.tau_M, interval.x, interval.y);
	output_data.values[index * 5u + 2u] = vec4(haze.T, 0.0);
	output_data.values[index * 5u + 3u] = vec4(haze.S_sun, 0.0);
	output_data.values[index * 5u + 4u] = vec4(haze.S_amb, 0.0);
}
"""
	return "#version 450\n" + include + "\n" + main

# Runs every entry of `entries` ({case, scale, quality}) through the production haze include; returns 20 floats per entry.
func haze_dispatch(rd: RenderingDevice, shader: RID, entries: Array) -> PackedFloat32Array:
	var inputs := PackedFloat32Array()
	for entry in entries:
		var test_case: Dictionary = entry["case"]
		var scale: float = entry["scale"]
		var target: Array = test_case["target"]
		var is_sky := int(test_case["mode"]) == 1
		var target_scale := 1.0 if is_sky else scale
		inputs.append_array([float(test_case["r"]), float(test_case["h"]), scale, float(test_case["mode"]),
			float(target[0]) * target_scale, float(target[1]) * target_scale, float(target[2]) * target_scale, float(entry["quality"]),
			float(test_case["negative"]), float(test_case["positive"]), float(test_case["pset"]), float(test_case["sun"])])
	var input_buffer := rd.storage_buffer_create(inputs.size() * 4, inputs.to_byte_array())
	var output_buffer := rd.storage_buffer_create(entries.size() * 80)
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
	rd.compute_list_dispatch(list, entries.size(), 1, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()
	var output := rd.buffer_get_data(output_buffer).to_float32_array()
	rd.free_rid(uniform_set)
	rd.free_rid(pipeline)
	rd.free_rid(input_buffer)
	rd.free_rid(output_buffer)
	return output

func haze_reference(test_case: Dictionary) -> Dictionary:
	var radius := float(test_case["r"])
	var height := float(test_case["h"])
	var target: Array = test_case["target"]
	var tx := float(target[0])
	var ty := float(target[1])
	var tz := float(target[2])
	var length_m := sqrt(tx * tx + ty * ty + tz * tz)
	var pset := int(test_case["pset"])
	if int(test_case["mode"]) == 1 and length_m > 0.0:
		var far_scale := 2.0 * radius / length_m
		tx *= far_scale
		ty *= far_scale
		tz *= far_scale
		length_m = 2.0 * radius
	var result := {"tau_r":Vector3.ZERO, "tau_a":0.0, "tau_m":0.0, "t0":1.0, "t1":0.0, "length":length_m}
	if length_m <= 0.0001:
		return result
	var ray := PackedFloat64Array([tx / length_m, ty / length_m, tz / length_m])
	var negative := float(test_case["negative"])
	var positive := float(test_case["positive"])
	var outer := layer_interval(radius, height, ray, -HAZE_FLOOR_DEPTH, length_m, negative, positive)
	var upper := layer_interval(radius, height, ray, HAZE_CAP, length_m, negative, positive)
	if outer[1] <= outer[0]:
		return result
	var air := first_shell_interval(outer, upper)
	result["t0"] = air[0]
	result["t1"] = air[1]
	if air[1] <= air[0] or pset == 2:
		return result
	var steps := HAZE_REF_STEPS
	var dt: float = (air[1] - air[0]) / steps
	var sum_r := 0.0
	var sum_a := 0.0
	var sum_m := 0.0
	for k in steps:
		var t: float = air[0] + (k + 0.5) * dt
		var px := (radius - height) + t * ray[0]
		var pz := t * ray[2]
		var h := radius - sqrt(px * px + pz * pz)
		sum_r += exp(-h / HAZE_H_R)
		sum_a += exp(-h / HAZE_H_A)
		sum_m += exp(-h / HAZE_H_MIST)
	var sigma_a := 0.0 if pset == 1 else haze_sigma_a()
	var sigma_m := 0.0 if pset == 1 else HAZE_SIGMA_M0
	result["tau_r"] = Vector3(HAZE_SIGMA_R[0], HAZE_SIGMA_R[1], HAZE_SIGMA_R[2]) * (sum_r * dt)
	result["tau_a"] = sigma_a * sum_a * dt
	result["tau_m"] = sigma_m * sum_m * dt
	return result

func haze_tau_error(actual: float, expected: float) -> float:
	return abs(actual - expected) / maxf(abs(expected), HAZE_TAU_ABS_FLOOR / 0.01)

func run_haze_tests() -> String:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		return "no local RenderingDevice (haze)"
	var shared_include := FileAccess.get_file_as_string(SHARED_INCLUDE_PATH)
	var haze_include := FileAccess.get_file_as_string(HAZE_INCLUDE_PATH)
	if haze_include.is_empty():
		rd.free()
		return "missing " + HAZE_INCLUDE_PATH
	var shader_source := RDShaderSource.new()
	shader_source.source_compute = haze_compute_source(shared_include + "\n" + haze_include)
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		rd.free()
		return "haze GLSL compile: " + spirv.compile_error_compute
	var shader := rd.shader_create_from_spirv(spirv)

	# --- optical depth vs reference ---------------------------------------------------------------------------------
	var cases := haze_cases()
	haze_case_count = cases.size()
	if cases.size() < 40:
		for cleanup in [shader]:
			rd.free_rid(cleanup)
		rd.free()
		return "haze case generation failed"
	var entries := []
	for scale in SCALES:
		for quality in HAZE_QUALITIES:
			for test_case in cases:
				entries.append({"case":test_case, "scale":scale, "quality":quality})
	var output := haze_dispatch(rd, shader, entries)
	var references := {}
	for test_case in cases:
		references[test_case["name"]] = haze_reference(test_case)
	var max_error := {4:0.0, 8:0.0, 16:0.0}
	var worst := {4:"", 8:"", 16:""}
	var max_long_error := {4:0.0, 8:0.0, 16:0.0}
	var worst_long := {4:"", 8:"", 16:""}
	var failure := ""
	for index in entries.size():
		var entry: Dictionary = entries[index]
		var test_case: Dictionary = entry["case"]
		var quality: int = entry["quality"]
		var reference: Dictionary = references[test_case["name"]]
		var base := index * 20
		var actual_r := Vector3(output[base], output[base + 1], output[base + 2])
		var expected_r: Vector3 = reference["tau_r"]
		var errors := [haze_tau_error(actual_r.x, expected_r.x), haze_tau_error(actual_r.y, expected_r.y), haze_tau_error(actual_r.z, expected_r.z),
			haze_tau_error(output[base + 4], reference["tau_a"]), haze_tau_error(output[base + 5], reference["tau_m"])]
		var worst_error := 0.0
		# Skip rays whose green-channel transmittance is already below exp(-8) = 3e-4: their tau error is invisible.
		if expected_r.y + float(reference["tau_a"]) + float(reference["tau_m"]) <= HAZE_SATURATED_TAU:
			for error in errors:
				worst_error = maxf(worst_error, error)
		var label := "%s scale=%s N=%d" % [test_case["name"], entry["scale"], quality]
		var is_short: bool = float(reference["length"]) < 1000000.0
		if is_short and worst_error > max_error[quality]:
			max_error[quality] = worst_error
			worst[quality] = label
		if not is_short and worst_error > max_long_error[quality]:
			max_long_error[quality] = worst_error
			worst_long[quality] = label
		# Interval sanity against the reference (2 m / 0.1%).
		if reference["t1"] > reference["t0"]:
			if abs(output[base + 6] - reference["t0"]) > maxf(2.0, abs(reference["t0"]) * 0.001) or abs(output[base + 7] - reference["t1"]) > maxf(2.0, abs(reference["t1"]) * 0.001):
				failure = "interval %s gpu=(%s,%s) cpu=(%s,%s)" % [label, output[base + 6], output[base + 7], reference["t0"], reference["t1"]]
		elif output[base + 7] > output[base + 6] + 1.0:
			failure = "interval should be empty %s gpu=(%s,%s)" % [label, output[base + 6], output[base + 7]]
		if failure == "" and quality == 8 and is_short and worst_error > HAZE_TAU_REL_TOL:
			failure = "tau error %.4f > %.2f %s gpu_r=%s cpu_r=%s gpu_a=%s cpu_a=%s gpu_m=%s cpu_m=%s" % [worst_error, HAZE_TAU_REL_TOL, label, actual_r, expected_r, output[base + 4], reference["tau_a"], output[base + 5], reference["tau_m"]]
		var transmittance := Vector3(output[base + 8], output[base + 9], output[base + 10])
		var s_sun := Vector3(output[base + 12], output[base + 13], output[base + 14])
		var s_amb := Vector3(output[base + 16], output[base + 17], output[base + 18])
		for value in [transmittance.x, transmittance.y, transmittance.z]:
			if not is_finite(value) or value < 0.0 or value > 1.0:
				failure = "T out of [0,1] %s T=%s" % [label, transmittance]
		for value in [s_sun.x, s_sun.y, s_sun.z, s_amb.x, s_amb.y, s_amb.z]:
			if not is_finite(value) or value < 0.0:
				failure = "S not finite/non-negative %s S_sun=%s S_amb=%s" % [label, s_sun, s_amb]
		if int(test_case["pset"]) == 2 and (transmittance != Vector3.ONE or s_sun != Vector3.ZERO or s_amb != Vector3.ZERO):
			failure = "zero density must give T=1,S=0 %s" % label
		if failure != "":
			rd.free_rid(shader)
			rd.free()
			return failure
	print("HAZE_TAU max rel error (rays < 1000 km): N=4 %.5f (%s) | N=8 %.5f (%s) | N=16 %.5f (%s)" % [max_error[4], worst[4], max_error[8], worst[8], max_error[16], worst[16]])
	print("HAZE_TAU max rel error (rays >= 1000 km, report only): N=4 %.5f (%s) | N=8 %.5f (%s) | N=16 %.5f (%s)" % [max_long_error[4], worst_long[4], max_long_error[8], worst_long[8], max_long_error[16], worst_long[16]])

	# --- monotone T in distance -------------------------------------------------------------------------------------
	var sweep_entries := []
	var sweep_distances_km := [1.0, 2.0, 5.0, 10.0, 15.0, 19.0, 21.0, 30.0, 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0]
	var sweep_elevations := [0.0, 2.0, 10.0, 45.0, 90.0]
	for elevation in sweep_elevations:
		for distance_km in sweep_distances_km:
			sweep_entries.append({"case":haze_ray_case("sweep", elevation, distance_km * 1000.0, 300.0), "scale":1.0, "quality":8})
	var sweep_output := haze_dispatch(rd, shader, sweep_entries)
	for elevation_index in sweep_elevations.size():
		var previous := Vector3.ONE
		for distance_index in sweep_distances_km.size():
			var base := (elevation_index * sweep_distances_km.size() + distance_index) * 20
			var transmittance := Vector3(sweep_output[base + 8], sweep_output[base + 9], sweep_output[base + 10])
			if transmittance.x > previous.x + 0.0002 or transmittance.y > previous.y + 0.0002 or transmittance.z > previous.z + 0.0002:
				rd.free_rid(shader)
				rd.free()
				return "T not monotone elevation=%s distance_km=%s T=%s previous=%s" % [sweep_elevations[elevation_index], sweep_distances_km[distance_index], transmittance, previous]
			previous = transmittance

	# --- sky variant ------------------------------------------------------------------------------------------------
	var sky_entries := []
	for elevation in [0.0, 2.0, 10.0, 45.0, 90.0]:
		for sun in [60.0, 5.0, -10.0, -60.0]:
			sky_entries.append({"case":haze_sky_case("sky", elevation, 0, sun), "scale":1.0, "quality":8})
	sky_entries.append({"case":haze_sky_case("sky_rayleigh_zenith", 90.0, 1, 60.0), "scale":1.0, "quality":8})
	sky_entries.append({"case":haze_sky_case("sky_rayleigh_zenith", 90.0, 1, 60.0), "scale":0.001, "quality":8})
	sky_entries.append({"case":haze_sky_case("sky_night", 45.0, 3, 60.0), "scale":1.0, "quality":8})
	sky_entries.append({"case":haze_sky_case("sky_zero", 45.0, 2, 60.0), "scale":1.0, "quality":8})
	var sky_output := haze_dispatch(rd, shader, sky_entries)
	rd.free_rid(shader)
	rd.free()
	var horizon_t := Vector3.ZERO
	var zenith_t := Vector3.ZERO
	var rayleigh_zenith_g := 0.0
	for index in sky_entries.size():
		var base := index * 20
		var transmittance := Vector3(sky_output[base + 8], sky_output[base + 9], sky_output[base + 10])
		var s_sun := Vector3(sky_output[base + 12], sky_output[base + 13], sky_output[base + 14])
		var s_amb := Vector3(sky_output[base + 16], sky_output[base + 17], sky_output[base + 18])
		var test_case: Dictionary = sky_entries[index]["case"]
		for value in [transmittance.x, transmittance.y, transmittance.z, s_sun.x, s_sun.y, s_sun.z, s_amb.x, s_amb.y, s_amb.z]:
			if not is_finite(value) or value < 0.0:
				return "sky non-finite %s T=%s" % [test_case["name"], transmittance]
		if index == 0:
			horizon_t = transmittance
			if maxf(transmittance.x, maxf(transmittance.y, transmittance.z)) > 0.01:
				return "sky horizon T should be ~0, got %s" % transmittance
		if index == 16:
			zenith_t = transmittance
		if test_case["name"] == "sky_rayleigh_zenith":
			# Ray from h0 = 300 m to the 20 km cap: tau = sigma H (e^-h0/H - e^-cap/H); spec shorthand is exp(-sigma H).
			var exact := exp(-HAZE_SIGMA_R[1] * HAZE_H_R * (exp(-300.0 / HAZE_H_R) - exp(-HAZE_CAP / HAZE_H_R)))
			var shorthand := exp(-HAZE_SIGMA_R[1] * HAZE_H_R)
			rayleigh_zenith_g = transmittance.y
			if abs(transmittance.y - exact) > 0.001 or abs(transmittance.y / shorthand - 1.0) > 0.05:
				return "sky zenith T(green)=%.5f expected %.5f (exp(-sigma H)=%.5f) scale=%s" % [transmittance.y, exact, shorthand, sky_entries[index]["scale"]]
		if test_case["name"] == "sky_zero" and (transmittance != Vector3.ONE or s_sun != Vector3.ZERO or s_amb != Vector3.ZERO):
			return "sky zero density must be identity"
		if test_case["name"] == "sky_night" and s_sun != Vector3.ZERO:
			return "night (day=0) must have no S_sun, got %s" % s_sun
	print("HAZE_SKY noon (default atmosphere, camera 300 m): T horizon 0deg=%s | T zenith 90deg=%s | Rayleigh-only zenith T.g=%.5f vs exp(-sigma_R.g*H_R)=%.5f" % [horizon_t, zenith_t, rayleigh_zenith_g, exp(-HAZE_SIGMA_R[1] * HAZE_H_R)])
	return await haze_godot_compile_check()

# Compiles the include as a Godot spatial shader and renders it; a compile error leaves the clear colour on screen.
func haze_godot_compile_check() -> String:
	var shader := Shader.new()
	shader.code = """shader_type spatial;
render_mode unshaded, cull_disabled;
#include "res://materials/ring_atmosphere_shared.gdshaderinc"
#include "res://materials/ring_haze_shared.gdshaderinc"
void fragment() {
	RingHazeParams p = ring_haze_params_default();
	p.quality = 16;
	RingHaze h = ring_haze_eval(p, vec3(100.0, 0.0, 5000.0), vec3(0.0), 300.0, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), 1000.0, 1000.0, 1.0, 5000000.0, 2500.0);
	RingHaze s = ring_haze_eval_sky(p, vec3(0.0, 0.2, 1.0), vec3(0.0), 300.0, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), 1000.0, 1000.0, 0.001, 5000000.0, 2500.0);
	ALBEDO = vec3(0.1, 0.5, 0.9) + (h.T + h.S_sun + h.S_amb + s.T + s.S_sun + s.S_amb) * 0.0;
}
"""
	var material := ShaderMaterial.new()
	material.shader = shader
	var viewport := SubViewport.new()
	viewport.size = Vector2i(16, 16)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.fov = 90.0
	viewport.add_child(camera)
	var quad := MeshInstance3D.new()
	var mesh := QuadMesh.new()
	mesh.size = Vector2(4.0, 4.0)
	quad.mesh = mesh
	quad.material_override = material
	quad.position = Vector3(0.0, 0.0, -1.0)
	viewport.add_child(quad)
	for frame in 4:
		await process_frame
	var image := viewport.get_texture().get_image()
	var pixel := image.get_pixel(8, 8)
	viewport.queue_free()
	if not (pixel.b > pixel.r + 0.3 and pixel.b > 0.5):
		return "ring_haze_shared.gdshaderinc failed to compile/render as a Godot spatial shader (pixel=%s)" % pixel
	print("HAZE_GODOT_SHADER_COMPILE=PASS pixel=%s" % pixel)
	return ""
