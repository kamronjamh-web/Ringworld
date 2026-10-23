extends SceneTree

## P7-1 of docs/atmosphere-haze-revamp-plan.md: sky-view LUT contracts. Builds the LUT with the production host
## (scripts/Atmosphere/RingAtmosphereLuts.cs, main RenderingDevice), reads the three RGBA16F textures back, uploads them to a local
## RenderingDevice and compares LUT lookups against the analytic ring_haze_eval_sky in the SAME compute probe (production shader source:
## ring_haze_shared + materials/atmosphere/ring_haze_sky_view.gdshaderinc), so only the cache is under test.
##
## Contracts:
##   sky_terms_assemble_exact   ring_haze_sky_terms_assemble(ring_haze_eval_sky_terms) == ring_haze_eval_sky (S rel 1e-5, T abs 1e-5)
##   sky_lut_texel_centres      lookup at a texel's own direction returns the stored value (S rel 2e-3, T abs 2e-3; uv within 0.02 texel)
##   sky_lut_interpolated       upper-half random directions (visible-pass policy) excluding |e - e_graze| < 0.3 deg: S rel <= 2% (max), mean <= 0.3%, T abs <= 0.005
##   sky_lut_cubemap_lower_half lower-half directions (radiance cubemap only): S rel mean <= 0.5%, p95 <= 1% (max not gated: sidewall / far-floor step)
##   sky_lut_split_respected    graze samples: +0.05 deg side is classified upper and within 3% / 0.01 T; -0.05 deg side is classified lower (analytic)
##   storm_zero_identity        strength 0 leaves the storm params exactly unchanged; strength 1 lowers T; host rebuild 0 -> 1 -> 0 restores the LUT byte-for-byte
##   sky_lut_key_lifecycle      key valid after the build and matching the camera; invalid when disabled / Legacy / near the cap
##   sky_lut_build_time         GPU time of the build (render timestamps) <= BUILD_TIME_BUDGET_MS (SKIP when timestamps are unavailable)
##
## Run (windowed Vulkan, not --headless):
##   godot --path . --script res://tools/atmosphere_lut_gpu_test.gd
## Flags: --report (print every contract detail and always exit 0).

const RING_RADIUS := 5000000.0
const HALF_WIDTH := 159000.0
const CAMERA_HEIGHT := 300.0
const SUN_STATES := {"noon": 60.0, "dusk": 5.0}
const RANDOM_DIRECTIONS := 4096
const TEXEL_SAMPLES := 600
const GRAZE_AZIMUTHS := 64
const GRAZE_OFFSET_RAD := 0.0008727 # 0.05 degrees
const EXCLUDE_GRAZE_DEG := 0.3
const TOL_ASSEMBLE_S := 1e-5
const TOL_TEXEL_S := 2e-3
const TOL_TEXEL_UV_TEXELS := 0.02
const TOL_INTERP_MAX := 0.02
const TOL_INTERP_MEAN := 0.003
const TOL_INTERP_T := 0.005
const TOL_SPLIT_S := 0.03
const TOL_SPLIT_T := 0.01
const TOL_LOWER_MEAN := 0.005
const TOL_LOWER_P95 := 0.01
const S_FLOOR := 0.005
const BUILD_TIME_BUDGET_MS := 2.0
const SAMPLE_FLOATS := 8
const RESULT_FLOATS := 32

const PROBE_HEADER := """#version 450
#define RING_HAZE_NO_LUTS
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 0, std430) readonly buffer Samples { vec4 v[]; } samples;
layout(set = 0, binding = 1, std430) writeonly buffer Results { vec4 v[]; } results;
layout(set = 0, binding = 2) uniform sampler2D lut0;
layout(set = 0, binding = 3) uniform sampler2D lut1;
layout(set = 0, binding = 4) uniform sampler2D lut2;
layout(set = 0, binding = 5, std140) uniform BuildParams {
	vec4 sigma_R_H_R;
	vec4 aerosol;
	vec4 mist;
	vec4 geometry;
	vec4 sides;
	vec4 outward;
	vec4 axis;
	vec4 to_sun;
	vec4 storm;
} build;
"""

const PROBE_MAIN := """
RingHazeSkyTerms lut_terms(RingHazeSkyFrame f, vec3 dir) {
	vec2 uv = ring_haze_sky_view_uv(f, dir);
	vec4 s0 = textureLod(lut0, uv, 0.0);
	vec4 s1 = textureLod(lut1, uv, 0.0);
	vec4 s2 = textureLod(lut2, uv, 0.0);
	RingHazeSkyTerms t;
	t.T = s0.rgb;
	t.A_R = s1.rgb;
	t.A_M = s2.rgb;
	t.B = vec3(s0.a, s1.a, s2.a);
	return t;
}

void main() {
	uint i = gl_GlobalInvocationID.x;
	vec4 a = samples.v[i * 2u];
	vec4 b = samples.v[i * 2u + 1u];
	int mode = int(a.w + 0.5);
	RingHazeParams p = ring_haze_params_default();
	p.sigma_R = build.sigma_R_H_R.xyz;
	p.H_R = build.sigma_R_H_R.w;
	p.sigma_A = build.aerosol.x;
	p.omega_A = build.aerosol.y;
	p.H_A = build.aerosol.z;
	p.sigma_M0 = build.aerosol.w;
	p.omega_M = build.mist.x;
	p.H_mist = build.mist.y;
	p.cap_height = build.mist.z;
	p.g = build.mist.w;
	p.quality = int(build.geometry.w + 0.5);
	p.to_sun = normalize(build.to_sun.xyz);
	p = ring_haze_apply_storm(p, build.storm);
	p.E_sun = vec3(12.566, 12.0, 11.3);
	p.ambient = vec3(0.2631, 0.3464, 0.4922);
	p.night_ambient = vec3(0.004, 0.005, 0.008);
	p.day = b.x;
	RingHazeSkyFrame f = ring_haze_sky_frame(build.outward.xyz, build.axis.xyz, build.sides.z);
	vec3 dir;
	if (mode == 1) {
		dir = ring_haze_sky_texel_direction(f, ivec2(int(b.y + 0.5), int(b.z + 0.5)));
	} else if (mode == 2) {
		float psi = a.x;
		float e = ring_haze_sky_graze_elevation(f.k, psi) + a.y;
		dir = f.axis * (cos(e) * cos(psi)) + f.around * (cos(e) * sin(psi)) + f.up * sin(e);
	} else {
		dir = normalize(a.xyz);
	}
	dir = normalize(dir);
	RingHaze ref = ring_haze_eval_sky(p, dir, vec3(0.0), build.geometry.z, build.outward.xyz, build.axis.xyz, build.sides.x, build.sides.y, 1.0, build.geometry.x, build.geometry.y);
	RingHazeSkyTerms ref_terms = ring_haze_eval_sky_terms(p, dir, vec3(0.0), build.geometry.z, build.outward.xyz, build.axis.xyz, build.sides.x, build.sides.y, 1.0, build.geometry.x, build.geometry.y);
	RingHaze assembled = ring_haze_sky_terms_assemble(p, ref_terms, dir);
	RingHaze lut = ring_haze_sky_terms_assemble(p, lut_terms(f, dir), dir);
	RingHazeSkyTerms lut_raw = lut_terms(f, dir);
	vec2 uv = ring_haze_sky_view_uv(f, dir);
	float e_deg = degrees(asin(clamp(dot(dir, f.up), -1.0, 1.0)));
	float psi_dir = atan(dot(dir, f.around), dot(dir, f.axis));
	float e_g_deg = degrees(ring_haze_sky_graze_elevation(f.k, psi_dir));
	RingHazeParams p_zero = ring_haze_apply_storm(p, vec4(0.0, 0.00026, 0.85, 0.5));
	RingHazeParams p_one = ring_haze_apply_storm(p, vec4(1.0, 0.00026, 0.85, 0.5));
	RingHazeSkyTerms one_terms = ring_haze_eval_sky_terms(p_one, dir, vec3(0.0), build.geometry.z, build.outward.xyz, build.axis.xyz, build.sides.x, build.sides.y, 1.0, build.geometry.x, build.geometry.y);
	uint o = i * 8u;
	results.v[o] = vec4(ref.S_sun + ref.S_amb, 0.0);
	results.v[o + 1u] = vec4(assembled.S_sun + assembled.S_amb, 0.0);
	results.v[o + 2u] = vec4(lut.S_sun + lut.S_amb, 0.0);
	results.v[o + 3u] = vec4(ref.T, 0.0);
	results.v[o + 4u] = vec4(assembled.T, 0.0);
	results.v[o + 5u] = vec4(lut_raw.T, 0.0);
	results.v[o + 6u] = vec4(uv, e_deg, e_g_deg);
	results.v[o + 7u] = vec4(abs(p_zero.sigma_A - p.sigma_A) + abs(p_zero.omega_A - p.omega_A) + abs(p_zero.g - p.g), one_terms.T.g, ref_terms.T.g, ring_haze_sky_view_upper_half(f, dir) ? 1.0 : 0.0);
}
"""

var _report_mode := false
var _dump_mode := false
var _failed := false
var _storm_host_ok := false
var _storm_host_detail := "not run"
var _helper: RefCounted
var _luts: RefCounted
var _contracts: Array = []
# Per-contract worst-case accumulators across sun states.
var _stats := {}

func _initialize() -> void:
	_report_mode = "--report" in OS.get_cmdline_user_args()
	_dump_mode = "--dump" in OS.get_cmdline_user_args()
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required (run windowed Vulkan, not --headless)")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(240.0).timeout.connect(func() -> void: _fail("240-second watchdog"))
	_helper = (load("res://tools/HazeConsistencyProbeHelper.cs") as Script).new()
	_luts = (load("res://scripts/Atmosphere/RingAtmosphereLuts.cs") as Script).new()
	_helper.SetHazeModel(1)
	_helper.ApplyPreset(0, 1.0)
	_helper.SetHazeStorm(0.0)
	_luts.call("SetSkyViewEnabled", true)

	for state in SUN_STATES:
		var ok: bool = await _run_sun_state(state, float(SUN_STATES[state]))
		if _failed or not ok:
			if not _failed: _fail("LUT build did not complete: " + String(_luts.call("GetStatus")))
			return
	await _storm_host()
	if _failed: return
	await _key_lifecycle()
	if _failed: return
	await _build_time()
	if _failed: return
	_finish()

# --- helpers --------------------------------------------------------------------------------------------

func _to_sun(elev_deg: float) -> Vector3:
	var s := deg_to_rad(elev_deg)
	return (Vector3(-1.0, 0.0, 0.0) * sin(s) + Vector3(0.0, 0.0, 1.0) * cos(s)).normalized()

func _request(sun: Vector3) -> bool:
	return bool(_luts.call("RequestSkyView", RING_RADIUS, Vector3(1.0, 0.0, 0.0), Vector3(0.0, 1.0, 0.0), CAMERA_HEIGHT, HALF_WIDTH, HALF_WIDTH, sun))

func _wait_for_build(previous_builds: int) -> bool:
	for frame in 300:
		await RenderingServer.frame_post_draw
		if int(_luts.call("GetBuildsCompleted")) > previous_builds:
			return true
		var status := String(_luts.call("GetStatus"))
		if status.begins_with("Sky-view LUT"):
			return false
	return false

func _expand_includes(path: String, seen: Dictionary) -> String:
	seen[path] = true
	var text := FileAccess.get_file_as_string(path)
	if text.is_empty():
		_fail("cannot read " + path)
		return ""
	var out := ""
	for line in text.split("\n"):
		if line.strip_edges().begins_with("#include"):
			var include_path := line.strip_edges().get_slice("\"", 1)
			if not seen.has(include_path):
				out += _expand_includes(include_path, seen) + "\n"
		else:
			out += line + "\n"
	return out

func _probe_source() -> String:
	var seen := {}
	var includes := _expand_includes("res://materials/ring_atmosphere_shared.gdshaderinc", seen)
	includes += _expand_includes("res://materials/ring_haze_shared.gdshaderinc", seen)
	includes += _expand_includes("res://materials/atmosphere/ring_haze_sky_view.gdshaderinc", seen)
	return PROBE_HEADER + includes + PROBE_MAIN

func _build_samples() -> Dictionary:
	var rng := RandomNumberGenerator.new()
	rng.seed = 20261019
	var data := PackedFloat32Array()
	var kinds: Array = []
	for i in RANDOM_DIRECTIONS:
		var z := rng.randf_range(-1.0, 1.0)
		var phi := rng.randf_range(0.0, TAU)
		var r := sqrt(maxf(1.0 - z * z, 0.0))
		data.append_array([r * cos(phi), r * sin(phi), z, 0.0, 1.0 if i % 2 == 0 else 0.45, 0.0, 0.0, 0.0])
		kinds.append("dir")
	for i in TEXEL_SAMPLES:
		data.append_array([0.0, 0.0, 0.0, 1.0, 1.0, float(rng.randi_range(0, 191)), float(rng.randi_range(0, 107)), 0.0])
		kinds.append("texel")
	for i in GRAZE_AZIMUTHS:
		var psi := (float(i) + 0.37) / float(GRAZE_AZIMUTHS) * TAU
		for side in [-1.0, 1.0]:
			data.append_array([psi, side * GRAZE_OFFSET_RAD, 0.0, 2.0, 1.0, 0.0, 0.0, 0.0])
			kinds.append("graze")
	while (data.size() / SAMPLE_FLOATS) % 64 != 0:
		data.append_array([0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0])
		kinds.append("pad")
	return {"data": data, "kinds": kinds}

func _readback_luts() -> Array:
	var main_rd := RenderingServer.get_rendering_device()
	var images: Array = []
	for index in 3:
		var rid: RID = _luts.call("GetSkyViewTexture", index)
		if not rid.is_valid():
			_fail("LUT texture %d is not valid" % index)
			return []
		images.append(main_rd.texture_get_data(rid, 0))
	return images

func _run_probe(luts: Array, params: PackedFloat32Array, samples: PackedFloat32Array) -> PackedFloat32Array:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("no local RenderingDevice")
		return PackedFloat32Array()
	var source := _probe_source()
	if _failed: return PackedFloat32Array()
	var shader_source := RDShaderSource.new()
	shader_source.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		var file := FileAccess.open("user://atmosphere_lut_probe_failed.comp", FileAccess.WRITE)
		if file != null:
			file.store_string(source)
			file.close()
		rd.free()
		_fail("probe compile error (source dumped to user://atmosphere_lut_probe_failed.comp): " + spirv.compile_error_compute)
		return PackedFloat32Array()
	var shader := rd.shader_create_from_spirv(spirv)
	var count := samples.size() / SAMPLE_FLOATS
	var sample_buffer := rd.storage_buffer_create(samples.size() * 4, samples.to_byte_array())
	var result_buffer := rd.storage_buffer_create(count * RESULT_FLOATS * 4)
	var params_buffer := rd.uniform_buffer_create(params.size() * 4, params.to_byte_array())
	var format := RDTextureFormat.new()
	format.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
	format.width = 192
	format.height = 108
	format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
	var textures: Array[RID] = []
	for image in luts:
		textures.append(rd.texture_create(format, RDTextureView.new(), [image]))
	var sampler_state := RDSamplerState.new()
	sampler_state.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	sampler_state.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	sampler_state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	sampler_state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	var sampler := rd.sampler_create(sampler_state)
	var uniforms: Array[RDUniform] = []
	for pair in [[0, sample_buffer], [1, result_buffer]]:
		var u := RDUniform.new()
		u.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
		u.binding = pair[0]
		u.add_id(pair[1])
		uniforms.append(u)
	for index in 3:
		var u := RDUniform.new()
		u.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		u.binding = 2 + index
		u.add_id(sampler)
		u.add_id(textures[index])
		uniforms.append(u)
	var block := RDUniform.new()
	block.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
	block.binding = 5
	block.add_id(params_buffer)
	uniforms.append(block)
	var uniform_set := rd.uniform_set_create(uniforms, shader, 0)
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline)
	rd.compute_list_bind_uniform_set(list, uniform_set, 0)
	rd.compute_list_dispatch(list, count / 64, 1, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()
	var out := rd.buffer_get_data(result_buffer).to_float32_array()
	rd.free_rid(uniform_set)
	rd.free_rid(pipeline)
	for texture in textures: rd.free_rid(texture)
	rd.free_rid(sampler)
	rd.free_rid(sample_buffer)
	rd.free_rid(result_buffer)
	rd.free_rid(params_buffer)
	rd.free_rid(shader)
	rd.free()
	return out

func _rel(a: Vector3, b: Vector3, floor_value: float) -> float:
	return maxf(maxf(absf(a.x - b.x) / maxf(absf(b.x), floor_value), absf(a.y - b.y) / maxf(absf(b.y), floor_value)), absf(a.z - b.z) / maxf(absf(b.z), floor_value))

func _abs_max(a: Vector3, b: Vector3) -> float:
	return maxf(maxf(absf(a.x - b.x), absf(a.y - b.y)), absf(a.z - b.z))

func _vec(out: PackedFloat32Array, base: int) -> Vector3:
	return Vector3(out[base], out[base + 1], out[base + 2])

func _track(name: String, value: float, label: String) -> void:
	if not _stats.has(name) or value > float(_stats[name]["value"]):
		_stats[name] = {"value": value, "label": label}

func _run_sun_state(state: String, sun_elevation: float) -> bool:
	var before := int(_luts.call("GetBuildsCompleted"))
	_request(_to_sun(sun_elevation))
	if not await _wait_for_build(before):
		return false
	_request(_to_sun(sun_elevation))
	if not bool(_luts.call("IsReady")):
		return false
	var luts := _readback_luts()
	if _failed or luts.is_empty(): return false
	var params: PackedFloat32Array = _luts.call("GetLastBuildParams")
	var built := _build_samples()
	var samples: PackedFloat32Array = built["data"]
	var kinds: Array = built["kinds"]
	var out := _run_probe(luts, params, samples)
	if _failed or out.is_empty(): return false
	var interp_errors: Array = []
	var interp_t_max := 0.0
	var interp_worst := -1.0
	var interp_worst_label := ""
	var lower_errors: Array = []
	var lower_t_max := 0.0
	var split_up_max := 0.0
	var split_up_t_max := 0.0
	var split_up_bad := 0
	var split_low_bad := 0
	var texel_max := 0.0
	var texel_t_max := 0.0
	var texel_uv_max := 0.0
	var assemble_max := 0.0
	var assemble_t_max := 0.0
	var zero_max := 0.0
	var storm_lower := 0
	var storm_total := 0
	for i in kinds.size():
		var kind: String = kinds[i]
		if kind == "pad": continue
		var base := i * RESULT_FLOATS
		var s_ref := _vec(out, base)
		var s_asm := _vec(out, base + 4)
		var s_lut := _vec(out, base + 8)
		var t_ref := _vec(out, base + 12)
		var t_asm := _vec(out, base + 16)
		var t_lut := _vec(out, base + 20)
		var e_deg := out[base + 26]
		var e_g_deg := out[base + 27]
		var label := "%s %s #%d (e=%.3f e_g=%.3f)" % [state, kind, i, e_deg, e_g_deg]
		if kind == "dir":
			assemble_max = maxf(assemble_max, _rel(s_asm, s_ref, 1e-4))
			assemble_t_max = maxf(assemble_t_max, _abs_max(t_asm, t_ref))
			var upper := out[base + 31] > 0.5
			if absf(e_deg - e_g_deg) >= EXCLUDE_GRAZE_DEG:
				var err := _rel(s_lut, s_ref, S_FLOOR)
				var dt := _abs_max(t_lut, t_ref)
				if upper:
					interp_errors.append(err)
					if err > interp_worst:
						interp_worst = err
						interp_worst_label = label
					if _dump_mode and (err >= TOL_INTERP_MAX or dt >= TOL_INTERP_T): print("DUMP interp %s psi=%.3f err=%.4f dT=%.4f Sref=%s Slut=%s Tref=%s Tlut=%s v=%.4f" % [label, out[base + 24] * 360.0, err, dt, s_ref, s_lut, t_ref, t_lut, out[base + 25] * 108.0])
					interp_t_max = maxf(interp_t_max, dt)
				else:
					lower_errors.append(err)
					lower_t_max = maxf(lower_t_max, dt)
			# Storm contract (zero strength identity, full strength lowers T).
			var t_one := out[base + 29]
			var t_plain := out[base + 30]
			zero_max = maxf(zero_max, out[base + 28])
			storm_total += 1
			if t_one <= t_plain + 1e-7: storm_lower += 1
		elif kind == "texel":
			var uv_u := out[base + 24]
			var uv_v := out[base + 25]
			var texel_index := int(round(samples[i * SAMPLE_FLOATS + 5]))
			var row_index := int(round(samples[i * SAMPLE_FLOATS + 6]))
			if row_index >= 1 and row_index <= 106:
				var du := absf(uv_u * 192.0 - (float(texel_index) + 0.5))
				du = minf(du, 192.0 - du)
				var dv := absf(uv_v * 108.0 - (float(row_index) + 0.5))
				texel_uv_max = maxf(texel_uv_max, maxf(du, dv))
			texel_max = maxf(texel_max, _rel(s_lut, s_ref, S_FLOOR))
			texel_t_max = maxf(texel_t_max, _abs_max(t_lut, t_ref))
		elif kind == "graze":
			var err_split := _rel(s_lut, s_ref, S_FLOOR)
			var dt_split := _abs_max(t_lut, t_ref)
			var upper_flag := out[base + 31] > 0.5
			if samples[i * SAMPLE_FLOATS + 1] > 0.0:
				# Upper side: the visible pass reads the LUT here.
				if not upper_flag: split_up_bad += 1
				if _dump_mode and (err_split >= TOL_SPLIT_S or dt_split >= TOL_SPLIT_T): print("DUMP split %s psi=%.3f err=%.4f dT=%.4f Sref=%s Slut=%s Tref=%s Tlut=%s v=%.4f" % [label, out[base + 24] * 360.0, err_split, dt_split, s_ref, s_lut, t_ref, t_lut, out[base + 25] * 108.0])
				split_up_max = maxf(split_up_max, err_split)
				split_up_t_max = maxf(split_up_t_max, dt_split)
			elif upper_flag:
				# Lower side: the visible pass is analytic here.
				split_low_bad += 1
	var interp_max := 0.0
	var interp_mean := 0.0
	for err in interp_errors:
		interp_max = maxf(interp_max, float(err))
		interp_mean += float(err)
	interp_mean /= maxf(float(interp_errors.size()), 1.0)
	_track("assemble", assemble_max, state)
	_track("assemble_t", assemble_t_max, state)
	_track("texel", texel_max, state)
	_track("texel_t", texel_t_max, state)
	_track("texel_uv", texel_uv_max, state)
	_track("interp_max", interp_max, interp_worst_label if interp_worst_label != "" else state)
	lower_errors.sort()
	var lower_mean := 0.0
	for err in lower_errors: lower_mean += float(err)
	lower_mean /= maxf(float(lower_errors.size()), 1.0)
	_track("lower_mean", lower_mean, state)
	_track("lower_p95", float(lower_errors[int(lower_errors.size() * 0.95)]) if lower_errors.size() > 0 else INF, state)
	_track("lower_p99", float(lower_errors[int(lower_errors.size() * 0.99)]) if lower_errors.size() > 0 else INF, state)
	_track("lower_max", float(lower_errors[-1]) if lower_errors.size() > 0 else INF, state)
	_track("lower_t", lower_t_max, state)
	_track("lower_count_missing", 0.0 if lower_errors.size() > 0 else 1.0, state)
	_track("interp_mean", interp_mean, state)
	_track("interp_t", interp_t_max, state)
	_track("split_up", split_up_max, state)
	_track("split_up_t", split_up_t_max, state)
	_track("split_up_bad", float(split_up_bad), state)
	_track("split_low_bad", float(split_low_bad), state)
	_track("zero", zero_max, state)
	_track("storm_not_lower", float(storm_total - storm_lower), state)
	return true

func _sci(value: float) -> String:
	return String.num_scientific(value) if is_finite(value) else str(value)

func _stat(name: String) -> float:
	return float(_stats[name]["value"]) if _stats.has(name) else INF

func _storm_build(strength: float) -> Array:
	_helper.SetHazeStorm(strength)
	var before := int(_luts.call("GetBuildsCompleted"))
	_request(_to_sun(60.0))
	if not await _wait_for_build(before):
		_fail("storm LUT build (strength %.1f) did not complete: %s" % [strength, String(_luts.call("GetStatus"))])
		return []
	return _readback_luts()

# Host part of the storm contract: strength 0 -> 1 -> 0 rebuilds; the first and last are byte-identical, the stormed T is lower.
func _storm_host() -> void:
	var a := await _storm_build(0.0)
	if _failed or a.is_empty(): return
	var b := await _storm_build(1.0)
	if _failed or b.is_empty(): return
	var c := await _storm_build(0.0)
	if _failed or c.is_empty(): return
	var mismatched := 0
	for index in 3:
		if (a[index] as PackedByteArray) != (c[index] as PackedByteArray): mismatched += 1
	var a0: PackedByteArray = a[0]
	var b0: PackedByteArray = b[0]
	var higher := 0
	var not_strict := 0
	var texels := a0.size() / 8
	for texel in texels:
		for channel in 3:
			var offset := texel * 8 + channel * 2
			var t_a := a0.decode_half(offset)
			var t_b := b0.decode_half(offset)
			if t_b > t_a: higher += 1
			elif t_a > 1e-3 and not (t_b < t_a): not_strict += 1
	_storm_host_ok = mismatched == 0 and higher == 0 and not_strict == 0
	_storm_host_detail = "0->1->0 textures differing %d/3, T raised by storm %d, T not strictly lower %d (of %d texels)" % [mismatched, higher, not_strict, texels]

func _key_lifecycle() -> void:
	var sun := _to_sun(60.0)
	var details: Array = []
	_request(sun)
	await RenderingServer.frame_post_draw
	_request(sun)
	var key: Vector4 = _luts.call("GetPublishedKey")
	var ok := key.w > 0.5 and absf(key.x - CAMERA_HEIGHT) < 0.01 and absf(key.y) < 0.01 and key.z > 0.0
	details.append("valid key=%s" % [key])
	_luts.call("SetSkyViewEnabled", false)
	_request(sun)
	var disabled_key: Vector4 = _luts.call("GetPublishedKey")
	ok = ok and disabled_key.w < 0.5
	_luts.call("SetSkyViewEnabled", true)
	var near_cap := bool(_luts.call("RequestSkyView", RING_RADIUS, Vector3(1.0, 0.0, 0.0), Vector3(0.0, 1.0, 0.0), 19990.0, HALF_WIDTH, HALF_WIDTH, sun))
	var near_cap_key: Vector4 = _luts.call("GetPublishedKey")
	ok = ok and not near_cap and near_cap_key.w < 0.5
	_helper.SetHazeModel(0)
	_request(sun)
	var legacy_key: Vector4 = _luts.call("GetPublishedKey")
	ok = ok and legacy_key.w < 0.5
	_helper.SetHazeModel(1)
	await RenderingServer.frame_post_draw
	_request(sun)
	await RenderingServer.frame_post_draw
	var restored := bool(_request(sun))
	ok = ok and restored
	details.append("disabled=%s nearCap=%s legacy=%s restored=%s" % [disabled_key.w, near_cap_key.w, legacy_key.w, restored])
	_contracts.append({"name": "sky_lut_key_lifecycle", "passed": ok, "detail": " ".join(details)})

func _build_time() -> void:
	var main_rd := RenderingServer.get_rendering_device()
	var elevation := 40.0
	var best_ms := -1.0
	for frame in 10:
		elevation += 0.5
		_request(_to_sun(elevation))
		await RenderingServer.frame_post_draw
		var count := main_rd.get_captured_timestamps_count()
		var begin_ns := -1
		var end_ns := -1
		for index in count:
			var timestamp_name := main_rd.get_captured_timestamp_name(index)
			if timestamp_name == "ring_sky_lut_begin": begin_ns = main_rd.get_captured_timestamp_gpu_time(index)
			elif timestamp_name == "ring_sky_lut_end": end_ns = main_rd.get_captured_timestamp_gpu_time(index)
		if begin_ns >= 0 and end_ns >= begin_ns:
			best_ms = float(end_ns - begin_ns) / 1000000.0
	if best_ms < 0.0:
		_contracts.append({"name": "sky_lut_build_time", "passed": true, "detail": "SKIP: no GPU timestamps captured (count=%d)" % main_rd.get_captured_timestamps_count()})
	else:
		_contracts.append({"name": "sky_lut_build_time", "passed": best_ms <= BUILD_TIME_BUDGET_MS, "detail": "%.3f ms (budget %.1f ms)" % [best_ms, BUILD_TIME_BUDGET_MS]})

func _finish() -> void:
	_contracts.append({"name": "sky_terms_assemble_exact", "passed": _stat("assemble") <= TOL_ASSEMBLE_S and _stat("assemble_t") <= TOL_ASSEMBLE_S,
		"detail": "S rel %s (%s), T abs %s" % [_sci(_stat("assemble")), _stats["assemble"]["label"], _sci(_stat("assemble_t"))]})
	_contracts.append({"name": "sky_lut_texel_centres", "passed": _stat("texel") <= TOL_TEXEL_S and _stat("texel_t") <= TOL_TEXEL_S and _stat("texel_uv") <= TOL_TEXEL_UV_TEXELS,
		"detail": "S rel %s, T abs %s, uv %.4f texel (worst %s)" % [_sci(_stat("texel")), _sci(_stat("texel_t")), _stat("texel_uv"), _stats["texel"]["label"]]})
	_contracts.append({"name": "sky_lut_interpolated", "passed": _stat("interp_max") <= TOL_INTERP_MAX and _stat("interp_mean") <= TOL_INTERP_MEAN and _stat("interp_t") <= TOL_INTERP_T,
		"detail": "upper half: S rel max %.3f%% mean %.3f%%, T abs %.4f (worst %s)" % [_stat("interp_max") * 100.0, _stat("interp_mean") * 100.0, _stat("interp_t"), _stats["interp_max"]["label"]]})
	# Radiance cubemap only (the visible pass is analytic in the lower half). The max is not gated because of the sidewall / far-floor step.
	_contracts.append({"name": "sky_lut_cubemap_lower_half", "passed": _stat("lower_count_missing") == 0.0 and _stat("lower_mean") <= TOL_LOWER_MEAN and _stat("lower_p95") <= TOL_LOWER_P95,
		"detail": "lower half S rel mean %.3f%% p95 %.3f%% p99 %.3f%% max %.3f%% (not gated), T abs max %.4f" % [_stat("lower_mean") * 100.0, _stat("lower_p95") * 100.0, _stat("lower_p99") * 100.0, _stat("lower_max") * 100.0, _stat("lower_t")]})
	_contracts.append({"name": "sky_lut_split_respected", "passed": _stat("split_up") <= TOL_SPLIT_S and _stat("split_up_t") <= TOL_SPLIT_T and _stat("split_up_bad") == 0.0 and _stat("split_low_bad") == 0.0,
		"detail": "upper side (+0.05 deg) S rel %.3f%%, T abs %.4f; misclassified upper=%d lower=%d" % [_stat("split_up") * 100.0, _stat("split_up_t"), int(_stat("split_up_bad")), int(_stat("split_low_bad"))]})
	_contracts.append({"name": "storm_zero_identity", "passed": _stat("zero") == 0.0 and _stat("storm_not_lower") == 0.0 and _storm_host_ok,
		"detail": "zero-strength param diff %s, directions where full storm did not lower T: %d; host: %s" % [_sci(_stat("zero")), int(_stat("storm_not_lower")), _storm_host_detail]})
	var failures: Array = []
	print("--- atmosphere LUT contracts ---")
	for contract in _contracts:
		print("%s %s: %s" % ["PASS" if contract["passed"] else "FAIL", contract["name"], contract["detail"]])
		if not contract["passed"]:
			failures.append(contract["name"])
	if _report_mode or failures.is_empty():
		print("ATMOSPHERE_LUT_GPU=%s contracts=%d failed=%d" % ["REPORT" if _report_mode else "PASS", _contracts.size(), failures.size()])
		_cleanup()
		quit(0)
	else:
		push_error("ATMOSPHERE_LUT_GPU=FAIL " + ", ".join(failures))
		_cleanup()
		quit(1)

func _cleanup() -> void:
	if _luts != null:
		_luts.call("Release")
		_luts = null
	if _helper != null:
		_helper.SetHazeModel(1)

func _fail(message: String) -> void:
	if _failed:
		return
	_failed = true
	push_error("ATMOSPHERE_LUT_GPU=FAIL " + message)
	_cleanup()
	quit(1)
