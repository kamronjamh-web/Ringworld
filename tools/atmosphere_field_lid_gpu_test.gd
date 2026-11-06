extends SceneTree
# Containment field WO9: GPU parity + measurement for the atmosphere lid in materials/ring_haze_shared.gdshaderinc (ring_haze_field_runs and the
# lid-aware ring_haze_eval / ring_haze_eval_sky), compiled as raw GLSL 450 on a local RenderingDevice (the way Sunshine uses it).
#   A. parity: the GLSL air runs against the C# twin RingAtmosphereFieldLid over camera poses x sphere directions x lengths.
#   B. box identity: with no field profile ring_haze_air_interval is the original box function, bit for bit.
#   C. measurement (the WO9 audit): box vs lid haze (T, S) per ray group, printed as a table, plus the sanity assertions.
# Run (windowed Vulkan, not --headless): Godot --path . --script res://tools/atmosphere_field_lid_gpu_test.gd. Prints ATMOSPHERE_FIELD_LID=PASS|FAIL.
const W := 318000.0
const R := 5000000.0
const FLOOR_DEPTH := 2500.0
const TOL_ABS := 2.0
const TOL_REL := 2e-6
const ATMOSPHERE_INCLUDE := "res://materials/ring_atmosphere_shared.gdshaderinc"
const HAZE_INCLUDE := "res://materials/ring_haze_shared.gdshaderinc"
const LENGTHS := [2.0 * R, 500.0, 5000.0, 50000.0, 400000.0]

const HEADER := """#version 450
layout(local_size_x = 64) in;
layout(set = 0, binding = 0, std430) readonly buffer In { vec4 inp[]; };
layout(set = 0, binding = 1, std430) writeonly buffer Out { vec4 outp[]; };
"""
const UBO_DECL := """layout(set = 0, binding = 2, std140) uniform U { vec4 f0; vec4 f1; } u;
"""
const FIELD_MACROS := """#define RING_HAZE_FIELD0 u.f0
#define RING_HAZE_FIELD1 u.f1
"""
# The ORIGINAL box function (git HEAD before WO9), kept verbatim under another name.
const BOX_REFERENCE := """
vec2 ring_haze_air_interval_box_reference(vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m, float cap_height_m) {
	float scale = max(coordinate_scale, 0.000001);
	float length_m = length(target - camera) / scale;
	if (length_m > 0.0001 && length_m < min(min(cap_height_m - camera_height, camera_height + floor_depth_m), min(side_negative, side_positive))) return vec2(0.0, length_m);
	vec2 outer = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m * scale, -floor_depth_m, length_m);
	vec2 upper_vacuum = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m * scale, cap_height_m, length_m);
	if (outer.y <= outer.x) return vec2(1.0, 0.0);
	vec2 air = outer;
	if (upper_vacuum.y > upper_vacuum.x) {
		if (upper_vacuum.x <= air.x) air.x = max(air.x, upper_vacuum.y);
		else air.y = min(air.y, upper_vacuum.x);
	}
	return air;
}
"""
# Input per case: inp[2i] = (camera height, camera across, ray length (<= 0: sky ray), want_second / sun mode), inp[2i+1] = (ray dir, 0).
# Frame: camera at the origin, outward = +x ("down"), axis = +y (across), around tangent = +z; up = -x.
const MAIN_RUNS := """
void main() {
	uint i = gl_GlobalInvocationID.x;
	if (i * 2u + 1u >= uint(inp.length())) return;
	vec4 a = inp[2u * i];
	vec3 dir = normalize(inp[2u * i + 1u].xyz);
	outp[i] = ring_haze_field_runs(dir * a.z, vec3(0.0), a.x, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), a.y, 1.0, 5000000.0, 2500.0, u.f0, u.f1, a.w > 0.5);
}
"""
const MAIN_BOX := """
void main() {
	uint i = gl_GlobalInvocationID.x;
	if (i * 2u + 1u >= uint(inp.length())) return;
	vec4 a = inp[2u * i];
	vec3 dir = normalize(inp[2u * i + 1u].xyz);
	vec3 target = dir * a.z;
	vec2 now = ring_haze_air_interval(target, vec3(0.0), a.x, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), a.y, 318000.0 - a.y, 1.0, 5000000.0, 2500.0, 20000.0);
	vec2 ref = ring_haze_air_interval_box_reference(target, vec3(0.0), a.x, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), a.y, 318000.0 - a.y, 1.0, 5000000.0, 2500.0, 20000.0);
	outp[i] = vec4(now, ref);
}
"""
const MAIN_EVAL := """
void main() {
	uint i = gl_GlobalInvocationID.x;
	if (i * 2u + 1u >= uint(inp.length())) return;
	vec4 a = inp[2u * i];
	vec3 dir = normalize(inp[2u * i + 1u].xyz);
	RingHazeParams p = ring_haze_params_default();
	p.E_sun = vec3(1.0, 0.96, 0.9) * 12.566;
	p.ambient = vec3(0.26, 0.35, 0.49);
	p.day = 1.0;
	p.quality = 8;
	p.to_sun = a.w < 0.5 ? vec3(-1.0, 0.0, 0.0) : normalize(vec3(-0.17364818, 0.0, 0.98480775));
	RingHaze h;
	if (a.z <= 0.0) h = ring_haze_eval_sky(p, dir, vec3(0.0), a.x, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), a.y, 318000.0 - a.y, 1.0, 5000000.0, 2500.0);
	else h = ring_haze_eval(p, dir * a.z, vec3(0.0), a.x, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), a.y, 318000.0 - a.y, 1.0, 5000000.0, 2500.0);
	outp[3u * i] = vec4(h.T, 0.0);
	outp[3u * i + 1u] = vec4(h.S_sun, 0.0);
	outp[3u * i + 2u] = vec4(h.S_amb, 0.0);
}
"""

var rd: RenderingDevice
var failures: Array[String] = []
var atmosphere_text := ""
var haze_text := ""

func _init() -> void:
	call_deferred("_run")

func _fail(reason: String) -> void:
	printerr("ATMOSPHERE_FIELD_LID=FAIL ", reason)
	quit(1)

func _build_shader(main_text: String, with_field: bool, with_box_reference: bool) -> RID:
	var src := HEADER
	if with_field:
		src += UBO_DECL + FIELD_MACROS
	src += atmosphere_text + "\n" + haze_text + "\n"
	if with_box_reference:
		src += BOX_REFERENCE
	src += main_text
	var source := RDShaderSource.new()
	source.source_compute = src
	var spirv := rd.shader_compile_spirv_from_source(source)
	if spirv.compile_error_compute != "":
		printerr("compile error: ", spirv.compile_error_compute)
		return RID()
	return rd.shader_create_from_spirv(spirv)

# Runs one kernel over `inputs` (8 floats per case). Returns the output buffer as float32 (floats_per_case per case) or an empty array.
func _dispatch(shader: RID, inputs: PackedFloat32Array, floats_per_case: int, ubo_floats: PackedFloat32Array) -> PackedByteArray:
	var count := inputs.size() / 8
	var input_buffer := rd.storage_buffer_create(inputs.size() * 4, inputs.to_byte_array())
	var output_buffer := rd.storage_buffer_create(count * floats_per_case * 4)
	var ids: Array = [input_buffer, output_buffer]
	var ubo := RID()
	if not ubo_floats.is_empty():
		ubo = rd.uniform_buffer_create(32, ubo_floats.to_byte_array())
		ids.append(ubo)
	var uniforms: Array[RDUniform] = []
	for binding in ids.size():
		var u := RDUniform.new()
		u.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER if binding == 2 else RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
		u.binding = binding
		u.add_id(ids[binding])
		uniforms.append(u)
	var uset := rd.uniform_set_create(uniforms, shader, 0)
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline)
	rd.compute_list_bind_uniform_set(list, uset, 0)
	rd.compute_list_dispatch(list, int((count + 63) / 64), 1, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()
	var out := rd.buffer_get_data(output_buffer)
	for rid in [pipeline, uset, input_buffer, output_buffer]:
		rd.free_rid(rid)
	if ubo.is_valid():
		rd.free_rid(ubo)
	return out

static func _dir_theta(theta_deg: float, phi_deg: float) -> Vector3:
	var t := deg_to_rad(theta_deg)
	var p := deg_to_rad(phi_deg)
	return Vector3(-cos(t), sin(t) * cos(p), sin(t) * sin(p))

# elevation above the horizon, horizontal direction (hy along the axis, hz around)
static func _dir_el(el_deg: float, hy: float, hz: float) -> Vector3:
	var e := deg_to_rad(el_deg)
	return Vector3(-sin(e), cos(e) * hy, cos(e) * hz)

static func _push(inputs: PackedFloat32Array, h: float, across: float, length: float, flag: float, d: Vector3) -> void:
	inputs.append_array(PackedFloat32Array([h, across, length, flag, d.x, d.y, d.z, 0.0]))

# The grid includes the exact poles and axis-aligned directions (degenerate: crossings land exactly on a shell or tangent); the generic set is
# the same 26 x 24 grid shifted off every axis and shell-aligned direction.
func _sphere_dirs(generic: bool = false) -> Array:
	var dirs: Array = []
	for i in 26:
		for k in 24:
			if generic:
				dirs.append(_dir_theta((i + 0.37) * 180.0 / 26.0, (k + 0.31) * 15.0))
			else:
				dirs.append(_dir_theta(i * 180.0 / 25.0, k * 15.0))
	return dirs

func _run() -> void:
	var t_start := Time.get_ticks_msec()
	rd = RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("Local RenderingDevice unavailable")
		return
	atmosphere_text = FileAccess.get_file_as_string(ATMOSPHERE_INCLUDE)
	haze_text = FileAccess.get_file_as_string(HAZE_INCLUDE)
	if atmosphere_text.is_empty() or haze_text.is_empty():
		_fail("include missing")
		return
	var probe = load("res://tools/AtmosphereFieldLidProbe.cs").new()
	var p0: Vector4 = probe.Packed0
	var p1: Vector4 = probe.Packed1
	var field_ubo := PackedFloat32Array([p0.x, p0.y, p0.z, p0.w, p1.x, p1.y, p1.z, p1.w])
	var zero_ubo := PackedFloat32Array([0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0])
	var x20: float = probe.AcrossAtHeight(20000.0)
	print("ATMOSPHERE_FIELD_LID profile packed0=", p0, " packed1=", p1, " x20=%.3f (H=%.3f)" % [x20, probe.Height(x20)])

	var shader_runs := _build_shader(MAIN_RUNS, true, false)
	var shader_box := _build_shader(MAIN_BOX, false, true)
	var shader_eval := _build_shader(MAIN_EVAL, true, false)
	if not shader_runs.is_valid() or not shader_box.is_valid() or not shader_eval.is_valid():
		_fail("shader compile")
		return

	_part_a(shader_runs, probe, x20)
	_part_b(shader_box)
	_part_c(shader_eval, x20)

	for rid in [shader_runs, shader_box, shader_eval]:
		rd.free_rid(rid)
	rd.free()
	print("ATMOSPHERE_FIELD_LID elapsed_s=%.1f" % ((Time.get_ticks_msec() - t_start) / 1000.0))
	if not failures.is_empty():
		for f in failures:
			printerr("ATMOSPHERE_FIELD_LID failure: ", f)
		_fail("%d check(s) failed" % failures.size())
		return
	print("ATMOSPHERE_FIELD_LID=PASS")
	quit(0)

# ---- A. runs parity ------------------------------------------------------------------------------------------------------------------------
func _part_a(shader: RID, probe, x20: float) -> void:
	var poses := [
		[300.0, W / 2.0], [300.0, 1000.0], [300.0, W - 5000.0],
		[10401.7, -30.0], [10401.7, W + 30.0], [10401.7, -100.0], [10700.0, -400.0],
		[15000.0, 3000.0], [19600.0, x20], [20400.0, x20],
		[39700.0, W / 2.0], [40300.0, W / 2.0], [45000.0, W / 2.0], [11500.0, 20.0],
	]
	_parity_set("generic", shader, probe, poses, _sphere_dirs(true), true)
	_parity_set("grid_degenerate", shader, probe, poses, _sphere_dirs(false), true)

func _parity_set(label: String, shader: RID, probe, poses: Array, dirs: Array, assert_set: bool) -> void:
	var inputs := PackedFloat32Array()
	for pose in poses:
		for d in dirs:
			for length in LENGTHS:
				_push(inputs, pose[0], pose[1], length, 1.0, d)
	var count := inputs.size() / 8
	var out_bytes := _dispatch(shader, inputs, 4, field_ubo_of(probe))
	if out_bytes.is_empty():
		failures.append("A: GPU dispatch returned nothing")
		return
	var gpu := out_bytes.to_float32_array()
	var cpu: PackedFloat64Array = probe.RunsBatch(inputs)
	var worst_err := 0.0
	var worst_ratio := 0.0
	var worst_case := -1
	var mismatches := 0
	var tiny_exist := 0
	var shown := 0
	var per_pose := {}
	var first_runs := 0
	var second_runs := 0
	var nontrivial := 0
	for i in count:
		for r in 2:
			var cx0 := cpu[i * 4 + r * 2]
			var cx1 := cpu[i * 4 + r * 2 + 1]
			var gx0 := gpu[i * 4 + r * 2]
			var gx1 := gpu[i * 4 + r * 2 + 1]
			if is_nan(gx0) or is_nan(gx1):
				mismatches += 1
				failures.append("A: NaN GPU run at case %d" % i)
				continue
			var cpu_e := cx1 > cx0
			var gpu_e := gx1 > gx0
			if cpu_e and r == 0: first_runs += 1
			if cpu_e and r == 1: second_runs += 1
			if cpu_e and r == 0 and (cx0 != 0.0 or cx1 != inputs[i * 8 + 2]): nontrivial += 1
			var bad := false
			var detail := ""
			if cpu_e and gpu_e:
				for e in [[gx0, cx0], [gx1, cx1]]:
					var err := absf(e[0] - e[1])
					var tol := maxf(TOL_ABS, TOL_REL * absf(e[1]))
					if err > worst_err:
						worst_err = err
					if err / tol > worst_ratio:
						worst_ratio = err / tol
						worst_case = i
					if err > tol:
						bad = true
						detail = "endpoint"
			elif cpu_e != gpu_e:
				var run_len := (cx1 - cx0) if cpu_e else (gx1 - gx0)
				var tol2 := maxf(TOL_ABS, TOL_REL * absf(cx0 if cpu_e else gx0))
				if run_len > tol2:
					bad = true
					detail = "existence (cpu=%s gpu=%s)" % [cpu_e, gpu_e]
				else:
					tiny_exist += 1
			if bad:
				mismatches += 1
				var key := "h=%.1f across=%.1f" % [inputs[i * 8], inputs[i * 8 + 1]]
				per_pose[key] = per_pose.get(key, 0) + 1
				if shown < 8:
					shown += 1
					print("ATMOSPHERE_FIELD_LID [%s] mismatch run%d %s: pose h=%.1f across=%.1f dir=%s len=%.0f gpu=(%.3f, %.3f, %.3f, %.3f) cpu=(%.3f, %.3f, %.3f, %.3f)" % [label, r, detail, inputs[i * 8], inputs[i * 8 + 1], Vector3(inputs[i * 8 + 4], inputs[i * 8 + 5], inputs[i * 8 + 6]), inputs[i * 8 + 2], gpu[i * 4], gpu[i * 4 + 1], gpu[i * 4 + 2], gpu[i * 4 + 3], cpu[i * 4], cpu[i * 4 + 1], cpu[i * 4 + 2], cpu[i * 4 + 3]])
	print("ATMOSPHERE_FIELD_LID [%s] parity cases=%d first_runs=%d nontrivial_first=%d second_runs=%d tiny_existence_diffs=%d mismatches=%d worst_endpoint_err_m=%.4f worst_err_over_tol=%.3f (tol = max(%s m, %s |t|))" % [label, count, first_runs, nontrivial, second_runs, tiny_exist, mismatches, worst_err, worst_ratio, TOL_ABS, TOL_REL])
	print("ATMOSPHERE_FIELD_LID [%s] mismatches per pose: %s" % [label, per_pose])
	if worst_case >= 0:
		print("ATMOSPHERE_FIELD_LID [%s] worst case: pose h=%.1f across=%.1f dir=%s len=%.0f gpu=(%.3f, %.3f, %.3f, %.3f) cpu=(%.3f, %.3f, %.3f, %.3f)" % [label, inputs[worst_case * 8], inputs[worst_case * 8 + 1], Vector3(inputs[worst_case * 8 + 4], inputs[worst_case * 8 + 5], inputs[worst_case * 8 + 6]), inputs[worst_case * 8 + 2], gpu[worst_case * 4], gpu[worst_case * 4 + 1], gpu[worst_case * 4 + 2], gpu[worst_case * 4 + 3], cpu[worst_case * 4], cpu[worst_case * 4 + 1], cpu[worst_case * 4 + 2], cpu[worst_case * 4 + 3]])
	if not assert_set:
		return
	if second_runs < 20 or nontrivial < 1000:
		failures.append("A[%s]: vacuous (second_runs=%d nontrivial_first=%d)" % [label, second_runs, nontrivial])
	if mismatches > 0:
		failures.append("A[%s]: %d GPU/CPU run mismatches beyond tolerance" % [label, mismatches])

func field_ubo_of(probe) -> PackedFloat32Array:
	var p0: Vector4 = probe.Packed0
	var p1: Vector4 = probe.Packed1
	return PackedFloat32Array([p0.x, p0.y, p0.z, p0.w, p1.x, p1.y, p1.z, p1.w])

# ---- B. box identity -----------------------------------------------------------------------------------------------------------------------
func _part_b(shader: RID) -> void:
	var inputs := PackedFloat32Array()
	for pose in [[300.0, W / 2.0], [300.0, 1000.0], [300.0, W - 5000.0]]:
		for d in _sphere_dirs():
			for length in LENGTHS:
				_push(inputs, pose[0], pose[1], length, 1.0, d)
	var count := inputs.size() / 8
	var out_bytes := _dispatch(shader, inputs, 4, PackedFloat32Array())
	if out_bytes.is_empty():
		failures.append("B: GPU dispatch returned nothing")
		return
	var bits := out_bytes.to_int32_array()
	var floats := out_bytes.to_float32_array()
	var different := 0
	var non_trivial := 0
	for i in count:
		if bits[i * 4] != bits[i * 4 + 2] or bits[i * 4 + 1] != bits[i * 4 + 3]:
			different += 1
		if floats[i * 4] != 0.0 or floats[i * 4 + 1] != inputs[i * 8 + 2]:
			non_trivial += 1
	print("ATMOSPHERE_FIELD_LID box_identity cases=%d non_trivial=%d bit_differences=%d" % [count, non_trivial, different])
	if different > 0:
		failures.append("B: %d box identity bit differences" % different)
	if non_trivial < 500:
		failures.append("B: vacuous (non_trivial=%d)" % non_trivial)

# ---- C. measurement ------------------------------------------------------------------------------------------------------------------------
func _part_c(shader: RID, x20: float) -> void:
	var cases: Array = []   # [group, h, across, length, dir]
	var rim_az := [-60.0, -30.0, 0.0, 30.0, 60.0]
	# g1
	for el in range(0, 91, 5):
		for k in 8:
			var az := deg_to_rad(k * 45.0)
			cases.append(["g1", 300.0, W / 2.0, 0.0, _dir_el(el, cos(az), sin(az))])
	# g2
	for base in [1000.0, 5000.0, 20000.0]:
		for mirrored in [false, true]:
			var across: float = (W - base) if mirrored else base
			var s := 1.0 if mirrored else -1.0   # toward the near rim: -across for the across-0 cameras, +across for the mirrored ones
			for el in range(0, 31, 5):
				for off in rim_az:
					var az := deg_to_rad(off)
					for length in [0.0, 2000.0, 10000.0, 50000.0]:
						cases.append(["g2", 300.0, across, length, _dir_el(el, s * cos(az), sin(az))])
	# g3
	for across in [-30.0, W + 30.0]:
		for el in range(0, 91, 10):
			for k in 24:
				var az := deg_to_rad(k * 15.0)
				for length in [0.0, 5000.0, 50000.0]:
					cases.append(["g3", 10401.7, across, length, _dir_el(el, cos(az), sin(az))])
	# g4
	for pose in [[15000.0, 3000.0], [19600.0, x20]]:
		for d in _sphere_dirs():
			for length in [0.0, 50000.0]:
				cases.append(["g4", pose[0], pose[1], length, d])
	# g5
	for length in [1000.0, 3000.0, 10000.0, 30000.0]:
		for el in [-10.0, -5.0, 0.0, 5.0]:
			for k in 8:
				var az := deg_to_rad(k * 45.0)
				cases.append(["g5", 300.0, W / 2.0, length, _dir_el(el, cos(az), sin(az))])
	# sanity
	for el in [15.0, 30.0, 60.0, 90.0]:
		for k in 8:
			var az := deg_to_rad(k * 45.0)
			cases.append(["san_apex", 45000.0, W / 2.0, 30000.0, _dir_el(el, cos(az), sin(az))])
			cases.append(["san_apex", 45000.0, W / 2.0, 200000.0, _dir_el(el, cos(az), sin(az))])
			# Sky rays are informational: a vacuum camera's "first air" is the far-arch air within 2R, the same as the box.
			cases.append(["apex_sky", 45000.0, W / 2.0, 0.0, _dir_el(el, cos(az), sin(az))])
	cases.append(["san_walltop", 10401.7, -30.0, 0.0, Vector3(-1.0, 0.0, 0.0)])
	cases.append(["san_vacside", 10700.0, -400.0, 0.0, _dir_el(20.0, -1.0, 0.0)])

	var group_names := ["g1", "g2", "g3", "g4", "g5", "san_apex", "apex_sky", "san_walltop", "san_vacside"]
	var results := {}   # sun -> [lid floats, box floats]
	var inputs_by_sun := {}
	for sun in [0, 1]:
		var inputs := PackedFloat32Array()
		for c in cases:
			_push(inputs, c[1], c[2], c[3], float(sun), c[4])
		inputs_by_sun[sun] = inputs
		var lid := _dispatch(shader, inputs, 12, field_ubo_of_cached())
		var box := _dispatch(shader, inputs, 12, PackedFloat32Array([0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]))
		if lid.is_empty() or box.is_empty():
			failures.append("C: GPU dispatch returned nothing")
			return
		results[sun] = [lid.to_float32_array(), box.to_float32_array()]

	print("ATMOSPHERE_FIELD_LID measurement: box (cap 20 km, side planes at the inner faces) vs lid (field profile); sun 0 = noon, 1 = 10 degree sun")
	print("ATMOSPHERE_FIELD_LID NOTE: S relative = |S_lid - S_box| / max(S_box, 1e-4) on luminance of S_sun + S_amb; code columns are an ESTIMATE: 255 * clamp(pow(S * 0.18, 1/2.2), 0, 1) of the luminance.")
	print("ATMOSPHERE_FIELD_LID %-12s %-4s %6s | %10s %8s | %9s %9s | %10s | %s" % ["group", "sun", "n", "max dT.g", "x255", "max relS", "mean relS", "max dcode", "max relS case"])
	var t_equal_g5 := true
	for g in group_names:
		for sun in [0, 1]:
			var lid_f: PackedFloat32Array = results[sun][0]
			var box_f: PackedFloat32Array = results[sun][1]
			var n := 0
			var max_dt := 0.0
			var max_rel := 0.0
			var max_abs := 0.0
			var sum_rel := 0.0
			var max_code := 0.0
			var max_case := -1
			var max_dt_case := -1
			var s_diff_cases := 0
			for ci in cases.size():
				if cases[ci][0] != g:
					continue
				n += 1
				var o := ci * 12
				var dt := absf(lid_f[o + 1] - box_f[o + 1])
				if dt > max_dt:
					max_dt = dt
					max_dt_case = ci
				var sl := _lum(lid_f, o + 4) + _lum(lid_f, o + 8)
				var sb := _lum(box_f, o + 4) + _lum(box_f, o + 8)
				var rel := absf(sl - sb) / maxf(sb, 1e-4)
				sum_rel += rel
				max_abs = maxf(max_abs, absf(sl - sb))
				if rel > max_rel:
					max_rel = rel
					max_case = ci
				if rel > 0.0:
					s_diff_cases += 1
				max_code = maxf(max_code, absf(_code(sl) - _code(sb)))
				if g == "g5":
					for ch in 3:
						if lid_f[o + ch] != box_f[o + ch]:
							t_equal_g5 = false
				# sanity
				if g == "san_apex" and (lid_f[o] != 1.0 or lid_f[o + 1] != 1.0 or lid_f[o + 2] != 1.0 or lid_f[o + 4] != 0.0 or lid_f[o + 5] != 0.0 or lid_f[o + 6] != 0.0 or lid_f[o + 8] != 0.0 or lid_f[o + 9] != 0.0 or lid_f[o + 10] != 0.0):
					failures.append("sanity apex: above the apex looking up the lid is not vacuum: T=%s case %d sun %d" % [Vector3(lid_f[o], lid_f[o + 1], lid_f[o + 2]), ci, sun])
				if g == "san_walltop":
					if not (lid_f[o + 1] < 0.999 and box_f[o + 1] == 1.0):
						failures.append("sanity wall top: lid T.g=%f (want < 0.999), box T.g=%f (want 1) sun %d" % [lid_f[o + 1], box_f[o + 1], sun])
				if g == "san_vacside" and (lid_f[o] != 1.0 or lid_f[o + 1] != 1.0 or lid_f[o + 2] != 1.0):
					failures.append("sanity vacuum side: lid T=%s not 1 sun %d" % [Vector3(lid_f[o], lid_f[o + 1], lid_f[o + 2]), sun])
			var case_text := "-"
			if max_case >= 0 and max_rel > 0.0:
				var c: Array = cases[max_case]
				case_text = "h=%.1f across=%.1f len=%.0f dir=%s" % [c[1], c[2], c[3], c[4]]
			var dt_text := ""
			if max_dt_case >= 0 and max_dt > 0.0:
				var c2: Array = cases[max_dt_case]
				dt_text = " dT case: h=%.1f across=%.1f len=%.0f dir=%s" % [c2[1], c2[2], c2[3], c2[4]]
			var code_text := "%10.2f" % max_code if (g == "g1" or g == "g2") else "%10s" % "n/a"
			print("ATMOSPHERE_FIELD_LID %-12s %-4d %6d | %10.6f %8.3f | %9.5f %9.5f %9.5f | %s | %s%s" % [g, sun, n, max_dt, max_dt * 255.0, max_rel, sum_rel / maxf(n, 1.0), max_abs, code_text, case_text, dt_text])
	if not t_equal_g5:
		failures.append("sanity g5: T differs between box and lid for low-altitude rays away from the walls")
	print("ATMOSPHERE_FIELD_LID sanity g5 T bit-equal box vs lid: ", t_equal_g5)

func field_ubo_of_cached() -> PackedFloat32Array:
	var probe = load("res://tools/AtmosphereFieldLidProbe.cs").new()
	return field_ubo_of(probe)

static func _lum(f: PackedFloat32Array, o: int) -> float:
	return 0.2126 * f[o] + 0.7152 * f[o + 1] + 0.0722 * f[o + 2]

static func _code(s: float) -> float:
	return 255.0 * clampf(pow(maxf(s * 0.18, 0.0), 1.0 / 2.2), 0.0, 1.0)
