extends SceneTree

## P0 of docs/atmosphere-haze-revamp-plan.md: haze consistency harness. No production change.
##
## Evaluates the three haze paths with PRODUCTION shader source in the same view rays:
##   (a) object/spatial path  : sample_ring_atmosphere (materials/ring_atmosphere.gdshaderinc)
##   (b) cloud/compute path   : ring_cloud_atmosphere_haze -> ring_atmosphere_local_haze_shared (Legacy) or
##                              ring_cloud_atmosphere_haze_physical -> ring_haze_from_profile (Physical, P2b)
##                              (addons/SunshineClouds2/RingAtmosphereCompute.glslinc) with the 16-slot profile
##                              packed by RingAtmosphereModel.PackCloudProfile, the same call as RingSunshineCloudBridge
##   (c) sky path             : sky() of materials/ring_local_sky.gdshader
## (a) and (c) run as the production include text transformed to GLSL compute (uniforms become globals whose
## values come from a ShaderMaterial configured by RingAtmosphereModel.ConfigureMaterial); a few cases are
## re-rendered through real SubViewports (spatial probe quad, sky) to prove the transform is faithful.
## Comparisons are relative between paths, never byte-exact goldens (GPU goldens are machine-specific).
##
## Run (windowed Vulkan, not --headless; kill other Godot processes first):
##   godot --path . --script res://tools/haze_consistency_gpu_test.gd -- --report
##   godot --path . --script res://tools/haze_consistency_gpu_test.gd            (assert contracts)
## Flags: --report (print table, write baseline JSON, always exit 0), --strict (ignore KNOWN_FAILING),
##        --model=physical (P2a: the object path uses ring_haze_sample with atmosphere_haze_model = 1; default is Legacy),
##        --toward-sun (view azimuth toward the sun instead of away), --cinematic, --altitude=<m>,
##        --out=<path> (baseline JSON path; default user://haze_consistency_baseline.json).

const RING_RADIUS := 5000000.0
const RING_HALF_WIDTH := 159000.0
const DEFAULT_ALTITUDE_M := 300.0
const DISTANCES_KM := [1.0, 10.0, 50.0, 200.0, 800.0]
const ELEVATIONS_DEG := [0.0, 2.0, 10.0, 45.0, 90.0]
const LIGHTING := ["noon", "dusk", "night", "eclipse"]
# Sun elevation above the local horizon (degrees), global solar visibility and night-light scale per state.
const SUN_ELEVATION := {"noon": 60.0, "dusk": 5.0, "night": -60.0, "eclipse": 60.0}
const SOLAR_VISIBILITY := {"noon": 1.0, "dusk": 1.0, "night": 1.0, "eclipse": 0.0}
const NIGHT_SCALE := {"noon": 1.0, "dusk": 1.0, "night": 1.0, "eclipse": 0.25}
const UP := Vector3(-1.0, 0.0, 0.0)
const OUTWARD := Vector3(1.0, 0.0, 0.0)
const AXIS := Vector3(0.0, 1.0, 0.0)
const TANGENT := Vector3(0.0, 0.0, 1.0)

# Contracts expected to fail today, with the plan step that should flip them. Default mode reports these
# as XFAIL and exits 0. A listed contract that passes is XPASS and FAILS the run: delete it from this list.
const KNOWN_FAILING := {
	"horizon_convergence": "P3 (unify sky: object in-scatter must converge to sky radiance)",
	"zenith_convergence": "P3 (same, steeper view rays)",
}

# Physical model (--model=physical): the object path is ring_haze_sample; alpha is reported as 1 - T.g and S as S_sun + S_amb.
# The cloud path uses the same physical function through the 16-slot profile (P2b), so object-vs-cloud agreement holds.
# Since P3 the sky evaluates the same function (ring_haze_eval_sky), so the convergence contracts pass exactly and nothing is expected to fail.
const KNOWN_FAILING_PHYSICAL := {}

const TOL_ALPHA := 0.01
const TOL_INSCATTER_REL := 0.05
const TOL_CONVERGENCE_REL := 0.15
const TOL_MONOTONIC := 0.00001
const TOL_CPU_SKY_PARITY := 0.02
const SKY_NIGHT_MAX_RATIO := 0.1
const SKY_ECLIPSE_MAX_RATIO := 1.2
# P10 far-arch visibility (Physical): rays that leave the local air and land on the far arch. Camera at local night / noon.
const ARCH_ELEVATIONS := [20.0, 30.0, 40.0, 50.0, 60.0]
const ARCH_ALBEDO := 0.1
const ARCH_MIN_CONTRAST := 3.0
const ARCH_MIN_TRANSMITTANCE := 0.3

var _args: PackedStringArray
var _report_mode := false
var _strict := false
var _toward_sun := false
var _cinematic := false
var _physical := false
var _p4_controls := false
var _altitude := DEFAULT_ALTITUDE_M
var _helper: RefCounted
var _cpu_sky: RefCounted
var _day_side: Node
var _sky_material: ShaderMaterial
var _probe_material: ShaderMaterial
var _failed := false
var _all_cases: Array = []
var _arch_results: Array = []
# P9: the sun irradiance the consumers see, overridden with the production DaySideAtmosphere value (haze_sun_irradiance_published).
var _sun_override_active := false
var _sun_override := Vector3.ZERO

func _initialize() -> void:
	_args = OS.get_cmdline_user_args()
	_report_mode = "--report" in _args
	_strict = "--strict" in _args
	_toward_sun = "--toward-sun" in _args
	_cinematic = "--cinematic" in _args
	_physical = "--model=physical" in _args
	_p4_controls = "--p4-controls" in _args
	for arg in _args:
		if arg.begins_with("--altitude="):
			_altitude = float(arg.get_slice("=", 1))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required (run windowed Vulkan, not --headless)")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(180.0).timeout.connect(func() -> void: _fail("180-second watchdog"))
	_helper = (load("res://tools/HazeConsistencyProbeHelper.cs") as Script).new()
	_day_side = (load("res://scripts/day_side_atmosphere.gd") as Script).new()
	_cpu_sky = (load("res://scripts/Atmosphere/RingHazeSkyIrradiance.cs") as Script).new()
	_build_materials()
	_build_cases()

	# Main evaluation at the preset's density, then the zero-density identity pass.
	var results := _evaluate(1.0)
	if _failed: return
	var zero_results := _evaluate(0.0)
	if _failed: return
	# Restore the model state used for renders (zero pass changed it).
	_helper.ApplyPreset(1 if _cinematic else 0, 1.0)

	var p9_contract := {}
	if _physical:
		p9_contract = _run_p9_air_visibility()
		if _failed: return
	var cross := await _render_crosschecks(results)
	if _failed: return
	var color_space := await _measure_far_viewport_color_space()
	if _failed: return
	_restore_globals()

	var metrics := _compute_metrics(results)
	var contracts := _evaluate_contracts(metrics, zero_results, cross, color_space)
	if not p9_contract.is_empty():
		contracts.append(p9_contract)
	if _report_mode:
		_print_report(metrics, cross, color_space)
	_write_baseline(metrics, contracts, cross, color_space)
	_print_contracts(contracts)
	_finish(contracts)

# --- scene / material setup -------------------------------------------------------------------------------

func _build_materials() -> void:
	_helper.SetHazeModel(1 if _physical else 0)
	_helper.ApplyPreset(1 if _cinematic else 0, 1.0)
	if _p4_controls:
		# Non-default P4 controls through the Atmosphere tab's code path (AtmosphereVisualDefaults); every consumer below must receive them.
		_helper.ApplyMenuControls(1 if _cinematic else 0, 1 if _physical else 0)
	_sky_material = ShaderMaterial.new()
	_sky_material.shader = load("res://materials/ring_local_sky.gdshader") as Shader
	_helper.ConfigureMaterial(_sky_material, RING_RADIUS)
	# Isolate atmosphere radiance: no sun disk/halo, giant, stars or planet-light haze in the sky probe.
	var fixed := {
		"sun_disk_intensity": 0.0, "sun_halo_intensity": 0.0, "planet_enabled": false, "stars_enabled": false,
		"star_visibility": 0.0, "planet_haze_enabled": false, "atmosphere_factor": 1.0, "ring_interior_factor": 1.0,
		"hub_level": 0.0, "camera_across_ratio": 0.0, "sky_camera_height_meters": _altitude,
		"ring_half_width_ratio": RING_HALF_WIDTH / RING_RADIUS,
		"camera_radial_ratio": (RING_RADIUS - _altitude) / RING_RADIUS,
		"ring_axis_direction": AXIS, "local_surface_up": UP,
		"exterior_day_scattering_boost": _day_side.exterior_day_scattering_boost,
		"exterior_night_scattering_boost": _day_side.exterior_night_scattering_boost,
		"atmosphere_sun_forward_scattering": _day_side.atmosphere_sun_forward_scattering,
		"space_color": _vec3(_day_side.space_sky_color), "day_start_dot": _day_side.day_start_dot,
		"day_full_dot": _day_side.day_full_dot,
	}
	for key in fixed:
		_sky_material.set_shader_parameter(key, fixed[key])

	var probe := Shader.new()
	var probe_body := """	RingHaze haze = ring_haze_sample(ring_atmosphere_camera_world_position, probe_world, vec3(0.0), 0.0);
	ALBEDO = vec3(1.0 - haze.T.g);""" if _physical else """	vec4 haze = sample_ring_atmosphere(ring_atmosphere_camera_world_position, probe_world, vec3(0.0), 0.0);
	ALBEDO = vec3(haze.a);"""
	probe.code = """shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;
#include "res://materials/ring_atmosphere.gdshaderinc"
varying vec3 probe_world;
void vertex() { probe_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
%s
}
""" % probe_body
	_probe_material = ShaderMaterial.new()
	_probe_material.shader = probe
	_helper.ConfigureMaterial(_probe_material, RING_RADIUS)
	_probe_material.set_shader_parameter("ring_atmosphere_ray_frame_valid", true)
	_probe_material.set_shader_parameter("ring_atmosphere_camera_world_position", Vector3.ZERO)
	_probe_material.set_shader_parameter("ring_atmosphere_camera_height_meters", _altitude)
	_probe_material.set_shader_parameter("ring_atmosphere_outward_world", OUTWARD)
	_probe_material.set_shader_parameter("ring_atmosphere_axis_world", AXIS)
	_probe_material.set_shader_parameter("ring_atmosphere_sidewall_negative_meters", RING_HALF_WIDTH)
	_probe_material.set_shader_parameter("ring_atmosphere_sidewall_positive_meters", RING_HALF_WIDTH)
	_probe_material.set_shader_parameter("ring_atmosphere_coordinate_scale", 1.0)

func _build_cases() -> void:
	_all_cases.clear()
	for light_index in LIGHTING.size():
		for elev in ELEVATIONS_DEG:
			for dist_km in DISTANCES_KM:
				_all_cases.append({"light": LIGHTING[light_index], "light_index": light_index, "elev": elev, "dist_km": dist_km})
	if _physical:
		for light in ["night", "noon"]:
			for elev in ARCH_ELEVATIONS:
				_all_cases.append({"light": light, "light_index": LIGHTING.find(light), "elev": elev, "dist_km": _arch_distance(elev) / 1000.0, "arch": true})

## Distance along the elevation-`elev` chord from the camera to the far arch floor (circle of radius RING_RADIUS).
func _arch_distance(elev_deg: float) -> float:
	var r0 := RING_RADIUS - _altitude
	var s := sin(deg_to_rad(elev_deg))
	return r0 * s + sqrt(r0 * r0 * s * s - r0 * r0 + RING_RADIUS * RING_RADIUS)

## Local up at the arch target of an arch case.
func _arch_up(case: Dictionary) -> Vector3:
	var target := _dir(float(case["elev"])) * (float(case["dist_km"]) * 1000.0)
	var centre := OUTWARD * -(RING_RADIUS - _altitude)
	return -(target - centre).normalized()

func _dir(elev_deg: float) -> Vector3:
	var e := deg_to_rad(elev_deg)
	return UP * sin(e) + TANGENT * cos(e)

func _to_sun(light: String) -> Vector3:
	var s := deg_to_rad(float(SUN_ELEVATION[light]))
	var azimuth := 1.0 if _toward_sun else -1.0
	return (UP * sin(s) + TANGENT * cos(s) * azimuth).normalized()

func _vec3(c: Color) -> Vector3:
	return Vector3(c.r, c.g, c.b)

func _sky_palette(light: String) -> Array:
	# Mirrors DaySideAtmosphere._update_sky's CPU palette (this is the code P3 deletes).
	var geometric := smoothstep(float(_day_side.day_start_dot), float(_day_side.day_full_dot), UP.dot(_to_sun(light)))
	var day := geometric * float(SOLAR_VISIBILITY[light])
	var scale := float(NIGHT_SCALE[light])
	var night_top: Color = _day_side.night_sky_top_color
	var night_horizon: Color = _day_side.night_sky_horizon_color
	var top := Color(night_top.r * scale, night_top.g * scale, night_top.b * scale).lerp(_day_side.day_sky_top_color, day)
	var horizon := Color(night_horizon.r * scale, night_horizon.g * scale, night_horizon.b * scale).lerp(_day_side.day_sky_horizon_color, day)
	return [_vec3(top), _vec3(horizon)]

# --- GLSL transform of the production include text -------------------------------------------------------

func _expand_includes(path: String, seen: Dictionary) -> String:
	seen[path] = true
	var text := FileAccess.get_file_as_string(path)
	if text.is_empty():
		_fail("cannot read " + path)
		return ""
	var out := ""
	for line in text.split("\n"):
		var trimmed := line.strip_edges()
		if trimmed.begins_with("#include"):
			var include_path := trimmed.get_slice("\"", 1)
			if not seen.has(include_path):
				out += _expand_includes(include_path, seen) + "\n"
		else:
			out += line + "\n"
	return out

func _literal(value: Variant, glsl_type: String) -> String:
	match typeof(value):
		TYPE_BOOL: return "true" if value else "false"
		TYPE_INT, TYPE_FLOAT:
			return ("%.9f" % float(value)) if glsl_type == "float" else str(int(value))
		TYPE_VECTOR2: return "vec2(%.9f, %.9f)" % [value.x, value.y]
		TYPE_VECTOR3: return "vec3(%.9f, %.9f, %.9f)" % [value.x, value.y, value.z]
		TYPE_VECTOR4: return "vec4(%.9f, %.9f, %.9f, %.9f)" % [value.x, value.y, value.z, value.w]
		TYPE_COLOR:
			if glsl_type == "vec3": return "vec3(%.9f, %.9f, %.9f)" % [value.r, value.g, value.b]
			return "vec4(%.9f, %.9f, %.9f, %.9f)" % [value.r, value.g, value.b, value.a]
	return ""

## Turns every `uniform` / `global uniform` into a plain global. Values come from the configured material
## (so they are exactly what ConfigureMaterial uploads); otherwise the shader's own default text is kept.
func _uniforms_to_globals(source: String, material: ShaderMaterial) -> String:
	var regex := RegEx.new()
	regex.compile("^\\s*(?:global\\s+)?uniform\\s+(\\w+)\\s+(\\w+)\\s*(?::[^=;]*)?(?:=\\s*([^;]+?))?\\s*;")
	var out := ""
	for line in source.split("\n"):
		var found := regex.search(line)
		if found == null:
			out += line + "\n"
			continue
		var glsl_type := found.get_string(1)
		var name := found.get_string(2)
		var default_text := found.get_string(3)
		var value: Variant = material.get_shader_parameter(name)
		if name == "ringworld_haze_sun_irradiance" and _sun_override_active:
			value = _sun_override
		elif value == null and line.strip_edges().begins_with("global"):
			# global_shader_parameter_get is editor-only; the project defaults are what an unpublished global holds.
			var declared: Variant = ProjectSettings.get_setting("shader_globals/" + name, null)
			if declared is Dictionary and declared.has("value"):
				value = declared["value"]
		var literal := _literal(value, glsl_type) if value != null else ""
		if literal.is_empty():
			literal = default_text
		out += (glsl_type + " " + name + (" = " + literal if not literal.is_empty() else "") + ";\n")
	return out

func _build_compute_source() -> String:
	var seen := {}
	var sky_source := _expand_includes("res://materials/ring_local_sky.gdshader", seen)
	if _failed: return ""
	sky_source = sky_source.replace("shader_type sky;", "")
	if not sky_source.contains("void sky() {") or not sky_source.contains("COLOR = color;"):
		_fail("ring_local_sky.gdshader no longer matches the harness transform (void sky() / COLOR = color;)")
		return ""
	sky_source = sky_source.replace("void sky() {", "void ring_sky_entry() {")
	sky_source = sky_source.replace("COLOR = color;", "COLOR = color; DBG_AIR = curved_air;")
	var adapter := FileAccess.get_file_as_string("res://addons/SunshineClouds2/RingAtmosphereCompute.glslinc")
	if adapter.is_empty():
		_fail("cannot read RingAtmosphereCompute.glslinc")
		return ""
	var globals_source := _uniforms_to_globals(sky_source, _sky_material)
	var header := "#version 450\nvec3 EYEDIR; vec3 COLOR; vec4 DBG_AIR; bool AT_CUBEMAP_PASS = false;\n"
	var object_call := """	RingHaze oh = ring_haze_sample(vec3(0.0), dir * dist, vec3(0.0), 0.0);
	results.v[i * 4u] = vec4(oh.S_sun + oh.S_amb, 1.0 - oh.T.g);""" if _physical else """	vec4 o = sample_ring_atmosphere(vec3(0.0), dir * dist, vec3(0.0), 0.0);
	results.v[i * 4u] = vec4(o.rgb * atmosphere_scattering_energy, o.a);"""
	# Physical profile: rgb = S_sun + S_amb, a = 1 - T.g (the same convention as the object path). Legacy: rgb colour, a opacity.
	var cloud_call := """	vec3 cloud_T; vec3 cloud_S;
	ring_cloud_atmosphere_haze_physical(vec3(0.0), dir, dist, P, cloud_T, cloud_S);
	results.v[i * 4u + 1u] = ring_haze_profile_physical(P) ? vec4(cloud_S, 1.0 - cloud_T.g) : ring_cloud_atmosphere_haze(vec3(0.0), dir, dist, P);""" if _physical else """	results.v[i * 4u + 1u] = ring_cloud_atmosphere_haze(vec3(0.0), dir, dist, P);"""
	var main := """
layout(local_size_x=1) in;
layout(set=0, binding=0, std430) readonly buffer Cases { vec4 v[]; } cases;
layout(set=0, binding=1, std430) readonly buffer Profiles { vec4 v[]; } profiles;
layout(set=0, binding=2, std430) writeonly buffer Results { vec4 v[]; } results;
const float CAM_H = %s;
const float HALF_W = %s;
const float RADIUS = %s;
void main() {
	uint i = gl_GlobalInvocationID.x;
	vec4 c0 = cases.v[i * 5u];
	vec4 c1 = cases.v[i * 5u + 1u];
	vec4 c2 = cases.v[i * 5u + 2u];
	vec3 dir = c0.xyz;
	float dist = c0.w;
	vec3 to_sun = c1.xyz;
	ringworld_solar_visibility = c1.w;
	ringworld_hub_day = 0.0;
	ringworld_hub_light_weight = 0.0;
	ringworld_night_light_scale = c2.x;
	// (a) object path with a valid camera ray frame at the viewport origin.
	ring_atmosphere_ray_frame_valid = true;
	ring_atmosphere_camera_world_position = vec3(0.0);
	ring_atmosphere_camera_height_meters = CAM_H;
	ring_atmosphere_outward_world = vec3(1.0, 0.0, 0.0);
	ring_atmosphere_axis_world = vec3(0.0, 1.0, 0.0);
	ring_atmosphere_sidewall_negative_meters = HALF_W;
	ring_atmosphere_sidewall_positive_meters = HALF_W;
	ring_atmosphere_coordinate_scale = 1.0;
	ring_atmosphere_to_sun = to_sun;
	eclipse_solar_transmittance = c1.w;
%s
	// (b) cloud path through the Sunshine adapter.
	vec4 P[16];
	for (uint k = 0u; k < 16u; k++) P[k] = profiles.v[uint(c2.y) * 16u + k];
%s
	// (c) sky path (infinite distance along dir).
	local_surface_up = vec3(-1.0, 0.0, 0.0);
	ring_axis_direction = vec3(0.0, 1.0, 0.0);
	camera_radial_ratio = (RADIUS - CAM_H) / RADIUS;
	camera_across_ratio = 0.0;
	sun_direction = to_sun;
	sky_top_color = cases.v[i * 5u + 3u].rgb;
	sky_horizon_color = cases.v[i * 5u + 4u].rgb;
	EYEDIR = dir;
	DBG_AIR = vec4(0.0);
	ring_sky_entry();
	results.v[i * 4u + 2u] = vec4(COLOR, 1.0);
	results.v[i * 4u + 3u] = DBG_AIR;
}
""" % [_f(_altitude), _f(RING_HALF_WIDTH), _f(RING_RADIUS), object_call, cloud_call]
	return header + globals_source + "\n" + adapter + "\n" + main

func _f(value: float) -> String:
	return "%.6f" % value

# --- GPU evaluation ---------------------------------------------------------------------------------------

func _evaluate(density: float) -> Array:
	_helper.ApplyPreset(1 if _cinematic else 0, density)
	var source := _build_compute_source()
	if _failed: return []
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("no local RenderingDevice")
		return []
	var shader_source := RDShaderSource.new()
	shader_source.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		var dump := ProjectSettings.globalize_path("user://haze_consistency_probe_failed.comp")
		var file := FileAccess.open("user://haze_consistency_probe_failed.comp", FileAccess.WRITE)
		if file != null:
			file.store_string(source)
			file.close()
		rd.free()
		_fail("compute compile error (source dumped to %s): %s" % [dump, spirv.compile_error_compute])
		return []
	var shader := rd.shader_create_from_spirv(spirv)

	# Per-lighting cloud profiles, packed like the Sunshine bridge.
	var profile_data := PackedFloat32Array()
	for light in LIGHTING:
		_helper.SetEclipseNightScale(float(NIGHT_SCALE[light]))
		var profile: Array = _helper.BuildCloudProfile(RING_RADIUS, OUTWARD, AXIS, _altitude, RING_HALF_WIDTH, RING_HALF_WIDTH, _to_sun(light), float(SOLAR_VISIBILITY[light]))
		for slot in profile:
			profile_data.append_array([slot.x, slot.y, slot.z, slot.w])
	_helper.SetEclipseNightScale(1.0)

	var case_data := PackedFloat32Array()
	for c in _all_cases:
		var dir := _dir(float(c["elev"]))
		var to_sun := _to_sun(c["light"])
		var palette := _sky_palette(c["light"])
		var top: Vector3 = palette[0]
		var horizon: Vector3 = palette[1]
		case_data.append_array([dir.x, dir.y, dir.z, float(c["dist_km"]) * 1000.0,
			to_sun.x, to_sun.y, to_sun.z, float(SOLAR_VISIBILITY[c["light"]]),
			float(NIGHT_SCALE[c["light"]]), float(c["light_index"]), 0.0, 0.0,
			top.x, top.y, top.z, 0.0,
			horizon.x, horizon.y, horizon.z, 0.0])

	var case_buffer := rd.storage_buffer_create(case_data.size() * 4, case_data.to_byte_array())
	var profile_buffer := rd.storage_buffer_create(profile_data.size() * 4, profile_data.to_byte_array())
	var result_buffer := rd.storage_buffer_create(_all_cases.size() * 4 * 16)
	var uniforms: Array[RDUniform] = []
	for pair in [[0, case_buffer], [1, profile_buffer], [2, result_buffer]]:
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
	rd.compute_list_dispatch(list, _all_cases.size(), 1, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()
	var out := rd.buffer_get_data(result_buffer).to_float32_array()
	rd.free_rid(uniform_set)
	rd.free_rid(pipeline)
	rd.free_rid(case_buffer)
	rd.free_rid(profile_buffer)
	rd.free_rid(result_buffer)
	rd.free_rid(shader)
	rd.free()

	var results: Array = []
	for index in _all_cases.size():
		var base := index * 16
		results.append({
			"case": _all_cases[index],
			"obj_alpha": out[base + 3], "obj_s": Vector3(out[base], out[base + 1], out[base + 2]),
			"cld_alpha": out[base + 7], "cld_s": Vector3(out[base + 4], out[base + 5], out[base + 6]) * (1.0 if _physical else out[base + 7]),
			"sky": Vector3(out[base + 8], out[base + 9], out[base + 10]),
			"sky_air_alpha": out[base + 15],
		})
		# Legacy object S is rgb*energy*alpha (terrain: emission = rgb * a * energy), already multiplied by energy above.
		# Physical already is the final S_sun + S_amb.
		if not _physical:
			results[index]["obj_s"] = results[index]["obj_s"] * out[base + 3]
	var standard: Array = []
	var arch: Array = []
	for r in results:
		if r["case"].has("arch"):
			arch.append(r)
		else:
			standard.append(r)
	if density == 1.0:
		_arch_results = arch
	return standard

# --- P9: camera-column sun visibility ------------------------------------------------------------------------

const P9_VISIBILITY := 0.3
const P9_TOL_LINEAR := 0.01

## Production sun irradiance for an air sun visibility: DaySideAtmosphere.haze_sun_irradiance_published() (the value
## _publish_haze_lighting hands to the shader global and RingSunshineCloudBridge.SetHazeLighting), relative to visibility 1
## and applied to the harness's baseline sun so the lighting matches the rest of the run.
func _p9_sun(visibility: float, base: Vector3) -> Vector3:
	_day_side.current_air_sun_visibility = 1.0
	var full: Vector3 = _day_side.haze_sun_irradiance_published()
	_day_side.current_air_sun_visibility = visibility
	var scaled: Vector3 = _day_side.haze_sun_irradiance_published()
	_day_side.current_air_sun_visibility = 1.0
	return Vector3(base.x * scaled.x / full.x, base.y * scaled.y / full.y, base.z * scaled.z / full.z)

func _p9_evaluate(sun: Vector3) -> Array:
	_sun_override = sun
	_sun_override_active = true
	_helper.SetHazeSunIrradiance(sun)
	var saved_arch := _arch_results
	var results := _evaluate(1.0)
	_arch_results = saved_arch
	return results

## With air sun visibility v, S_sun of the object, cloud and sky paths all scale by v (S = S_sun + S_amb is linear in E_sun with the
## ambient unchanged: S(v) = S(0) + v (S(1) - S(0))) and the three paths still agree.
func _run_p9_air_visibility() -> Dictionary:
	var base: Vector3 = _helper.GetHazeSunIrradiance()
	var base_global := _global_default("ringworld_haze_sun_irradiance")
	var r_full := _p9_evaluate(_p9_sun(1.0, base_global))
	if _failed: return {}
	var r_dim := _p9_evaluate(_p9_sun(P9_VISIBILITY, base_global))
	if _failed: return {}
	var r_zero := _p9_evaluate(_p9_sun(0.0, base_global))
	_sun_override_active = false
	_helper.SetHazeSunIrradiance(base)
	if _failed: return {}
	var worst_linear := 0.0
	var worst_agree := 0.0
	var worst_conv := 0.0
	var worst_case := ""
	var dimmed_checked := 0
	for i in r_full.size():
		var c: Dictionary = r_full[i]["case"]
		if c["light"] != "noon":
			continue
		for key in ["obj_s", "cld_s"] + (["sky"] if float(c["dist_km"]) == 800.0 else []):
			var s1: Vector3 = r_full[i][key]
			var s0: Vector3 = r_zero[i][key]
			var sv: Vector3 = r_dim[i][key]
			var expected := s0 + (s1 - s0) * P9_VISIBILITY
			var err := _max_component((sv - expected).abs()) / maxf(_max_component(s1), 0.001)
			if err > worst_linear:
				worst_linear = err
				worst_case = "%s %s" % [key, _case_label(c)]
			if _lum(s1 - s0) > 0.0005 and float(c["elev"]) >= 0.0:
				dimmed_checked += 1
				# The sun share must really be v of the full sun share (guards against a path ignoring the dimmed value).
				var share := _lum(sv - s0) / _lum(s1 - s0)
				if absf(share - P9_VISIBILITY) > 0.01:
					worst_linear = maxf(worst_linear, absf(share - P9_VISIBILITY))
					worst_case = "share %s %s=%.3f" % [key, _case_label(c), share]
		var os: Vector3 = r_dim[i]["obj_s"]
		var cs: Vector3 = r_dim[i]["cld_s"]
		var rel := _max_component((os - cs).abs()) / maxf(maxf(_max_component(os), _max_component(cs)), 0.001)
		worst_agree = maxf(worst_agree, rel)
		if float(c["dist_km"]) == 800.0:
			worst_conv = maxf(worst_conv, absf(float(_lum(os)) / maxf(_lum(r_dim[i]["sky"]), 0.0001) - 1.0))
	var passed := worst_linear <= P9_TOL_LINEAR and worst_agree <= TOL_INSCATTER_REL and worst_conv <= TOL_CONVERGENCE_REL and dimmed_checked > 20
	return _contract("air_sun_visibility_scales_s_sun", passed, "v=%.1f: max linear/share error %.4f (%s), tol %.2f; object-vs-cloud max rel %.4f; 800 km convergence gap %.4f; %d sun-share checks" % [P9_VISIBILITY, worst_linear, worst_case, P9_TOL_LINEAR, worst_agree, worst_conv, dimmed_checked])

# --- metrics ----------------------------------------------------------------------------------------------

func _lum(v: Vector3) -> float:
	return v.x * 0.2126 + v.y * 0.7152 + v.z * 0.0722

func _max_component(v: Vector3) -> float:
	return maxf(v.x, maxf(v.y, v.z))

func _case_label(c: Dictionary) -> String:
	return "%s e=%.0f d=%.0fkm" % [c["light"], c["elev"], c["dist_km"]]

func _compute_metrics(results: Array) -> Array:
	var metrics: Array = []
	for r in results:
		var os: Vector3 = r["obj_s"]
		var cs: Vector3 = r["cld_s"]
		var sky: Vector3 = r["sky"]
		var d_s := _max_component((os - cs).abs())
		var scale := maxf(maxf(_max_component(os), _max_component(cs)), 0.001)
		var sky_lum := _lum(sky)
		var m := {
			"case": r["case"], "label": _case_label(r["case"]),
			"obj_alpha": r["obj_alpha"], "cld_alpha": r["cld_alpha"],
			"obj_s": os, "cld_s": cs, "sky": sky, "sky_air_alpha": r["sky_air_alpha"],
			"d_alpha": absf(float(r["obj_alpha"]) - float(r["cld_alpha"])),
			"d_s": d_s, "d_s_rel": d_s / scale,
			"obj_lum": _lum(os), "cld_lum": _lum(cs), "sky_lum": sky_lum,
			"conv_ratio": _lum(os) / maxf(sky_lum, 0.0001),
			"conv_rel_max_channel": _max_component(Vector3(
				absf(os.x - sky.x) / maxf(sky.x, 0.001), absf(os.y - sky.y) / maxf(sky.y, 0.001), absf(os.z - sky.z) / maxf(sky.z, 0.001))),
			"obj_transmittance": 1.0 - float(r["obj_alpha"]),
		}
		metrics.append(m)
	return metrics

func _evaluate_contracts(metrics: Array, zero_results: Array, cross: Dictionary, color_space: Dictionary) -> Array:
	var contracts: Array = []
	# finite outputs
	var bad := ""
	for m in metrics:
		for key in ["obj_alpha", "cld_alpha", "d_s", "sky_lum"]:
			if not is_finite(float(m[key])):
				bad = m["label"] + " " + key
	contracts.append(_contract("finite_outputs", bad.is_empty(), bad if not bad.is_empty() else "all finite"))

	# zero density => identity (alpha 0, no in-scatter) for object and cloud paths
	var worst_zero := 0.0
	var worst_zero_case := ""
	for r in zero_results:
		var v := maxf(maxf(float(r["obj_alpha"]), float(r["cld_alpha"])), maxf(_max_component(r["obj_s"]), _max_component(r["cld_s"])))
		if v > worst_zero:
			worst_zero = v
			worst_zero_case = _case_label(r["case"])
	contracts.append(_contract("zero_density_identity", worst_zero <= 0.00001, "max |alpha|,|S| at density 0 = %.6f %s" % [worst_zero, worst_zero_case]))

	# object vs cloud agreement
	var worst_alpha := _worst(metrics, "d_alpha")
	contracts.append(_contract("object_cloud_alpha_agree", float(worst_alpha[0]) <= TOL_ALPHA,
		"max |alpha_obj - alpha_cloud| = %.4f (%s), tol %.3f" % [worst_alpha[0], worst_alpha[1], TOL_ALPHA]))
	var worst_s := _worst(metrics, "d_s_rel")
	contracts.append(_contract("object_cloud_inscatter_agree", float(worst_s[0]) <= TOL_INSCATTER_REL,
		"max rel |S_obj - S_cloud| = %.4f (%s), tol %.2f" % [worst_s[0], worst_s[1], TOL_INSCATTER_REL]))

	# monotonic opacity in distance
	var worst_drop := 0.0
	var worst_drop_case := ""
	for light in LIGHTING:
		for elev in ELEVATIONS_DEG:
			var previous_obj := -1.0
			var previous_cld := -1.0
			for m in metrics:
				if m["case"]["light"] != light or m["case"]["elev"] != elev:
					continue
				for pair in [[previous_obj, float(m["obj_alpha"]), "obj"], [previous_cld, float(m["cld_alpha"]), "cloud"]]:
					if pair[0] - pair[1] > worst_drop:
						worst_drop = pair[0] - pair[1]
						worst_drop_case = "%s %s" % [pair[2], m["label"]]
				previous_obj = float(m["obj_alpha"])
				previous_cld = float(m["cld_alpha"])
	contracts.append(_contract("opacity_monotonic_in_distance", worst_drop <= TOL_MONOTONIC, "largest alpha decrease with distance = %.6f %s" % [worst_drop, worst_drop_case]))

	# convergence of far in-scatter to the sky radiance in the same direction
	for entry in [["horizon_convergence", [0.0, 2.0, 10.0]], ["zenith_convergence", [45.0, 90.0]]]:
		var worst_gap := 0.0
		var worst_gap_case := ""
		for m in metrics:
			if float(m["case"]["dist_km"]) != 800.0 or not (float(m["case"]["elev"]) in entry[1]):
				continue
			var gap := absf(float(m["conv_ratio"]) - 1.0)
			if gap > worst_gap:
				worst_gap = gap
				worst_gap_case = "%s obj/sky lum=%.3f" % [m["label"], m["conv_ratio"]]
		contracts.append(_contract(entry[0], worst_gap <= TOL_CONVERGENCE_REL, "max |lum(S_obj@800km)/lum(sky) - 1| = %.3f (%s), tol %.2f" % [worst_gap, worst_gap_case, TOL_CONVERGENCE_REL]))

	# Physical sky (P3): CPU double-precision port agrees with the GPU sky, night/eclipse skies are dark.
	if _physical:
		contracts.append_array(_sky_contracts(metrics))
		contracts.append_array(_arch_contracts())

	# harness validity: the GLSL transform must match real SubViewport renders
	contracts.append(_contract("spatial_probe_crosscheck", float(cross["spatial_max_error"]) <= 0.02, "max |alpha(spatial render) - alpha(compute)| = %.4f over %d renders" % [cross["spatial_max_error"], cross["spatial_count"]]))
	contracts.append(_contract("sky_render_crosscheck", float(cross["sky_max_rel_error"]) <= 0.08, "max rel |sky(render) - sky(compute)| = %.4f over %d renders" % [cross["sky_max_rel_error"], cross["sky_count"]]))
	if _p4_controls:
		contracts.append(_p4_contract())

	# colour space: the far viewport must reach the main viewport without a second tonemap
	contracts.append(_contract("far_viewport_single_tonemap", String(color_space["classification"]) == "single_tonemap", "far sky classification = %s" % color_space["classification"]))
	return contracts

## --p4-controls: the model statics (CPU sky port reads them), the spatial materials' uniforms and the packed cloud profile all carry the non-default controls.
func _p4_contract() -> Dictionary:
	var st: Dictionary = _helper.PhysicalControlState()
	var sigma_r: Vector3 = st["sigma_r"]
	var profile: Array = _helper.BuildCloudProfile(RING_RADIUS, OUTWARD, AXIS, _altitude, RING_HALF_WIDTH, RING_HALF_WIDTH, _to_sun("noon"), 1.0)
	var errors: PackedStringArray = PackedStringArray()
	if int(st["quality"]) != 16: errors.append("model quality")
	if not is_equal_approx(float(st["h_mist"]) / 700.0, 1.0) or not is_equal_approx(float(st["h_a"]) / 2500.0, 1.0): errors.append("model heights")
	if not is_equal_approx(float(st["albedo"]), 0.25) or not is_equal_approx(float(st["gain"]), 1.5): errors.append("model bounce/gain")
	if int(_cpu_sky.call("GetQuality")) != 16: errors.append("cpu sky quality")
	for material in [_sky_material, _probe_material]:
		if int(material.get_shader_parameter("ring_haze_quality")) != 16: errors.append("material quality")
		if not is_equal_approx(float((material.get_shader_parameter("ring_haze_sigma_R") as Vector3).x) / sigma_r.x, 1.0): errors.append("material sigma_R")
		if not is_equal_approx(float(material.get_shader_parameter("ring_haze_H_mist")) / 700.0, 1.0): errors.append("material H_mist")
		if not is_equal_approx(float(material.get_shader_parameter("ring_haze_H_A")) / 2500.0, 1.0): errors.append("material H_A")
		if not is_equal_approx(float(material.get_shader_parameter("ring_haze_sigma_M0")) / float(st["sigma_m"]), 1.0): errors.append("material sigma_M0")
	if int(profile[13].y) != 16: errors.append("cloud quality")
	if not is_equal_approx(profile[10].z / sigma_r.x, 1.0) or not is_equal_approx(profile[12].x / 2500.0, 1.0) 			or not is_equal_approx(profile[12].w / 700.0, 1.0) or not is_equal_approx(profile[12].y / float(st["sigma_m"]), 1.0): errors.append("cloud profile values")
	return _contract("p4_controls_reach_consumers", errors.is_empty(), "rayleigh 1.5x, mist 2x/700 m, H_A 2500 m, quality 16: " + (", ".join(errors) if not errors.is_empty() else "model, materials, CPU sky and cloud profile agree"))

## Global lighting values the compute probe uses (the project defaults of the shader globals) as Vector3s.
func _global_default(name: String) -> Vector3:
	if name == "ringworld_haze_sun_irradiance" and _sun_override_active:
		return _sun_override
	var declared: Variant = ProjectSettings.get_setting("shader_globals/" + name, null)
	if declared is Dictionary and declared.has("value"):
		return declared["value"]
	return Vector3.ZERO

## CPU reference of the GPU sky S for one case: the helper's ring_haze_eval_sky port with the same lighting globals, day weight and tint.
func _cpu_sky_s(case: Dictionary) -> Vector3:
	var light: String = case["light"]
	var to_sun := _to_sun(light)
	var geometric := smoothstep(-0.2, 0.35, UP.dot(to_sun))
	var day := geometric * float(SOLAR_VISIBILITY[light])
	var sun_tint: Vector3 = _cpu_sky.GetSunTint()
	var sun_e: Vector3 = _global_default("ringworld_haze_sun_irradiance") * sun_tint
	var ambient_day := _global_default("ringworld_haze_ambient_day")
	var ambient_night := _global_default("ringworld_haze_ambient_night")
	var ambient := ambient_night.lerp(ambient_day, day)
	var result: Array = _cpu_sky.EvalSky(_dir(float(case["elev"])), OUTWARD, AXIS, _altitude, RING_HALF_WIDTH, RING_HALF_WIDTH, RING_RADIUS, to_sun, sun_e, ambient, int(_cpu_sky.call("GetQuality")))
	var s_sun: Vector3 = result[1]
	var s_amb: Vector3 = result[2]
	return s_sun * day + s_amb

## P10: from the local night side the sunlit far arch (elevation >= 20 deg) must stay clearly visible against the dark local sky, under
## its veil. Final arch radiance = S_obj + T_obj * L_surf with L_surf a representative lit surface (albedo ARCH_ALBEDO, remote sun).
func _arch_contracts() -> Array:
	var contracts: Array = []
	var worst_contrast := 1.0e9
	var worst_case := ""
	var worst_t := 1.0e9
	var detail := ""
	var checked := 0
	var noon_detail := ""
	var sun_e: Vector3 = _global_default("ringworld_haze_sun_irradiance") * _cpu_sky.GetSunTint()
	for r in _arch_results:
		var c: Dictionary = r["case"]
		var to_sun := _to_sun(c["light"])
		var up := _arch_up(c)
		var ndl := maxf(up.dot(to_sun), 0.0)
		var remote_day := smoothstep(-0.2, 0.35, up.dot(to_sun)) * float(SOLAR_VISIBILITY[c["light"]])
		var surface := sun_e * (ARCH_ALBEDO / PI) * ndl * 0.9 + _global_default("ringworld_haze_ambient_day") * ARCH_ALBEDO * remote_day
		var transmittance := 1.0 - float(r["obj_alpha"])
		var arch_radiance: Vector3 = (r["obj_s"] as Vector3) + surface * transmittance
		var sky_lum := _lum(r["sky"])
		var arch_lum := _lum(arch_radiance)
		var contrast := arch_lum / maxf(sky_lum, 0.0005)
		var veil := _lum(r["obj_s"]) / maxf(arch_lum, 0.000001)
		var line := "%s e=%.0f: remote_day=%.2f T.g=%.3f L_surf=%.4f arch=%.5f sky=%.5f contrast=%.1f veil_share=%.2f" % [c["light"], c["elev"], remote_day, transmittance, _lum(surface), arch_lum, sky_lum, contrast, veil]
		print("ARCH ", line)
		# Only arch sections that are actually lit (remote day >= 0.5); the part still in the local night's shadow reads as dark land.
		if c["light"] == "night" and remote_day >= 0.5:
			checked += 1
			if contrast < worst_contrast:
				worst_contrast = contrast
				worst_case = line
			worst_t = minf(worst_t, transmittance)
	contracts.append(_contract("arch_visible_from_night", checked >= 3 and worst_contrast >= ARCH_MIN_CONTRAST and worst_t >= ARCH_MIN_TRANSMITTANCE,
		"min arch/sky luminance contrast %.1f (need >= %.1f), min arch T.g %.3f (need >= %.2f); worst: %s" % [worst_contrast, ARCH_MIN_CONTRAST, worst_t, ARCH_MIN_TRANSMITTANCE, worst_case]))
	return contracts

func _sky_contracts(metrics: Array) -> Array:
	var contracts: Array = []
	# CPU port vs GPU sky (noon + dusk, every elevation, 800 km rows carry the sky radiance).
	var worst_parity := 0.0
	var worst_parity_case := ""
	for m in metrics:
		if float(m["case"]["dist_km"]) != 800.0 or not (m["case"]["light"] in ["noon", "dusk"]):
			continue
		var cpu := _cpu_sky_s(m["case"])
		var gpu: Vector3 = m["sky"]
		var err := _max_component(Vector3(absf(gpu.x - cpu.x) / maxf(cpu.x, 0.002), absf(gpu.y - cpu.y) / maxf(cpu.y, 0.002), absf(gpu.z - cpu.z) / maxf(cpu.z, 0.002)))
		if err > worst_parity:
			worst_parity = err
			worst_parity_case = "%s gpu=%s cpu=%s" % [m["label"], gpu, cpu]
	contracts.append(_contract("cpu_sky_parity", worst_parity <= TOL_CPU_SKY_PARITY, "max rel |sky(GPU) - S(CPU port)| = %.4f (%s), tol %.2f" % [worst_parity, worst_parity_case, TOL_CPU_SKY_PARITY]))
	# Night and eclipse skies are dark relative to noon / night.
	var worst_night := 0.0
	var worst_night_case := ""
	var worst_eclipse := 0.0
	var worst_eclipse_case := ""
	for elev in ELEVATIONS_DEG:
		var noon_lum := 0.0
		var night_lum := 0.0
		var eclipse_lum := 0.0
		for m in metrics:
			if float(m["case"]["dist_km"]) != 800.0 or float(m["case"]["elev"]) != float(elev):
				continue
			match String(m["case"]["light"]):
				"noon": noon_lum = float(m["sky_lum"])
				"night": night_lum = float(m["sky_lum"])
				"eclipse": eclipse_lum = float(m["sky_lum"])
		var night_ratio := night_lum / maxf(noon_lum, 0.0001)
		if night_ratio > worst_night:
			worst_night = night_ratio
			worst_night_case = "e=%.0f night=%.5f noon=%.5f" % [elev, night_lum, noon_lum]
		var eclipse_ratio := eclipse_lum / maxf(night_lum, 0.00001)
		if eclipse_ratio > worst_eclipse:
			worst_eclipse = eclipse_ratio
			worst_eclipse_case = "e=%.0f eclipse=%.5f night=%.5f" % [elev, eclipse_lum, night_lum]
	contracts.append(_contract("sky_night_dark", worst_night <= SKY_NIGHT_MAX_RATIO, "max night/noon sky luminance = %.4f (%s), tol %.2f" % [worst_night, worst_night_case, SKY_NIGHT_MAX_RATIO]))
	contracts.append(_contract("sky_eclipse_dark", worst_eclipse <= SKY_ECLIPSE_MAX_RATIO, "max eclipse/night sky luminance = %.4f (%s), tol %.2f" % [worst_eclipse, worst_eclipse_case, SKY_ECLIPSE_MAX_RATIO]))
	return contracts

func _worst(metrics: Array, key: String) -> Array:
	var best := -1.0
	var label := ""
	for m in metrics:
		if float(m[key]) > best:
			best = float(m[key])
			label = m["label"]
	return [best, label]

func _contract(name: String, passed: bool, detail: String) -> Dictionary:
	return {"name": name, "passed": passed, "detail": detail, "known": (KNOWN_FAILING_PHYSICAL if _physical else KNOWN_FAILING).get(name, "")}

# --- real-render cross checks -----------------------------------------------------------------------------

func _make_viewport(size: int, tonemap_linear := true) -> SubViewport:
	var viewport := SubViewport.new()
	viewport.size = Vector2i(size, size)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	return viewport

func _center_linear(viewport: SubViewport) -> Color:
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty viewport image")
		return Color.BLACK
	return image.get_pixel(image.get_width() / 2, image.get_height() / 2).srgb_to_linear()

func _set_globals(light: String) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", float(SOLAR_VISIBILITY[light]))
	RenderingServer.global_shader_parameter_set("ringworld_night_light_scale", float(NIGHT_SCALE[light]))
	RenderingServer.global_shader_parameter_set("ringworld_hub_day", 0.0)
	RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", 0.0)

func _restore_globals() -> void:
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	RenderingServer.global_shader_parameter_set("ringworld_night_light_scale", 1.0)

func _render_crosschecks(results: Array) -> Dictionary:
	var spatial_max := 0.0
	var spatial_count := 0
	var spatial_rows: Array = []
	var sky_max := 0.0
	var sky_count := 0
	var sky_rows: Array = []
	# Spatial probe: unshaded quad at the target, ALBEDO = object alpha.
	for light in ["noon", "night"]:
		_set_globals(light)
		_probe_material.set_shader_parameter("ring_atmosphere_to_sun", _to_sun(light))
		for elev in [0.0, 10.0, 90.0]:
			for dist_km in [10.0, 200.0, 800.0]:
				var viewport := _make_viewport(16)
				var env := Environment.new()
				env.background_mode = Environment.BG_COLOR
				env.background_color = Color.BLACK
				env.tonemap_mode = Environment.TONE_MAPPER_LINEAR
				var world := WorldEnvironment.new()
				world.environment = env
				viewport.add_child(world)
				var camera := Camera3D.new()
				camera.fov = 2.0
				camera.near = 1.0
				camera.far = 3000000.0
				viewport.add_child(camera)
				camera.position = Vector3.ZERO
				var dir := _dir(elev)
				camera.look_at(dir, Vector3(0.0, 1.0, 0.0))
				var quad := MeshInstance3D.new()
				var mesh := QuadMesh.new()
				mesh.size = Vector2(dist_km * 1000.0 * 0.2, dist_km * 1000.0 * 0.2)
				quad.mesh = mesh
				quad.material_override = _probe_material
				quad.extra_cull_margin = 16384.0
				viewport.add_child(quad)
				quad.look_at_from_position(dir * dist_km * 1000.0, Vector3.ZERO, Vector3(0.0, 1.0, 0.0))
				for frame in 5:
					await RenderingServer.frame_post_draw
				var pixel := _center_linear(viewport)
				viewport.queue_free()
				if _failed: return {}
				var expected := _find(results, light, elev, dist_km)
				var error := absf(pixel.r - float(expected["obj_alpha"]))
				spatial_max = maxf(spatial_max, error)
				spatial_count += 1
				spatial_rows.append({"case": expected["case"], "render": pixel.r, "compute": expected["obj_alpha"], "error": error})
	# Sky probe: BG_SKY with the production sky shader, centre pixel looking along dir.
	for light in ["noon", "dusk", "night"]:
		_set_globals(light)
		var palette := _sky_palette(light)
		_sky_material.set_shader_parameter("sun_direction", _to_sun(light))
		_sky_material.set_shader_parameter("ring_atmosphere_to_sun", _to_sun(light))
		_sky_material.set_shader_parameter("sky_top_color", palette[0])
		_sky_material.set_shader_parameter("sky_horizon_color", palette[1])
		for elev in [0.0, 10.0, 45.0]:
			var viewport := _make_viewport(16)
			var env := Environment.new()
			env.background_mode = Environment.BG_SKY
			var sky := Sky.new()
			sky.sky_material = _sky_material
			env.sky = sky
			env.tonemap_mode = Environment.TONE_MAPPER_LINEAR
			env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
			env.ambient_light_energy = 0.0
			var world := WorldEnvironment.new()
			world.environment = env
			viewport.add_child(world)
			var camera := Camera3D.new()
			camera.fov = 2.0
			viewport.add_child(camera)
			camera.position = Vector3.ZERO
			camera.look_at(_dir(elev), Vector3(0.0, 1.0, 0.0))
			for frame in 6:
				await RenderingServer.frame_post_draw
			var pixel := _center_linear(viewport)
			viewport.queue_free()
			if _failed: return {}
			var expected := _find(results, light, elev, 800.0)
			var sky_compute: Vector3 = expected["sky"]
			# The 16 px render is LDR (clips at 1.0); bright non-default controls can exceed it.
			sky_compute = Vector3(minf(sky_compute.x, 1.0), minf(sky_compute.y, 1.0), minf(sky_compute.z, 1.0))
			var render := Vector3(pixel.r, pixel.g, pixel.b)
			var error := _max_component(Vector3(
				absf(render.x - sky_compute.x) / maxf(sky_compute.x, 0.01),
				absf(render.y - sky_compute.y) / maxf(sky_compute.y, 0.01),
				absf(render.z - sky_compute.z) / maxf(sky_compute.z, 0.01)))
			sky_max = maxf(sky_max, error)
			sky_count += 1
			sky_rows.append({"light": light, "elev": elev, "render": render, "compute": sky_compute, "rel_error": error})
	return {"spatial_max_error": spatial_max, "spatial_count": spatial_count, "spatial_rows": spatial_rows,
		"sky_max_rel_error": sky_max, "sky_count": sky_count, "sky_rows": sky_rows}

func _find(results: Array, light: String, elev: float, dist_km: float) -> Dictionary:
	for r in results:
		var c: Dictionary = r["case"]
		if c["light"] == light and float(c["elev"]) == elev and float(c["dist_km"]) == dist_km:
			return r
	_fail("case not found %s %s %s" % [light, elev, dist_km])
	return {"case": {}, "obj_alpha": 0.0, "sky": Vector3.ZERO}

# --- colour space: does the far-viewport sky arrive linear-HDR or tonemapped-LDR? --------------------------

func _tonemap_environment(mode: int, background: Color) -> Environment:
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = background
	env.tonemap_mode = mode
	env.tonemap_exposure = float(_day_side.tonemap_exposure)
	if mode == Environment.TONE_MAPPER_AGX:
		env.set("tonemap_agx_white", float(_day_side.agx_white))
		env.set("tonemap_agx_contrast", float(_day_side.agx_contrast))
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_energy = 0.0
	return env

func _direct_color(background: Color, mode: int) -> Color:
	var viewport := _make_viewport(16)
	var world := WorldEnvironment.new()
	world.environment = _tonemap_environment(mode, background)
	viewport.add_child(world)
	var camera := Camera3D.new()
	viewport.add_child(camera)
	for frame in 5:
		await RenderingServer.frame_post_draw
	var color := _center_linear(viewport)
	viewport.queue_free()
	return color

## Reproduces RingScaledFarViewport via the production RingFarViewportColorPipeline: far SubViewport (HDR 2D, LINEAR
## tonemap) -> RingFarBackdropComposite POST_SKY effect in the LDR main viewport's compositor, main env AgX.
## The only test-local part is the stimulus.
func _via_far_color(background: Color, mode: int) -> Color:
	var pipeline: RefCounted = load("res://scripts/Voxel/LOD/RingFarViewportColorPipeline.cs").new()
	var far := _make_viewport(16)
	pipeline.call("ConfigureFarViewport", far)
	var far_env := _tonemap_environment(Environment.TONE_MAPPER_AGX, background)
	pipeline.call("ConfigureFarEnvironment", far_env)
	var far_world := WorldEnvironment.new()
	far_world.environment = far_env
	far.add_child(far_world)
	far.add_child(Camera3D.new())
	var main_viewport := _make_viewport(16)
	var main_env := _tonemap_environment(mode, Color.BLACK)
	pipeline.call("ConfigureMainEnvironment", main_env)
	var main_world := WorldEnvironment.new()
	main_world.environment = main_env
	main_viewport.add_child(main_world)
	main_viewport.add_child(Camera3D.new())
	var effect: CompositorEffect = pipeline.call("InstallBackdropEffect", main_world)
	pipeline.call("PublishBackdrop", effect, far.get_texture(), null, true)
	for frame in 8:
		await RenderingServer.frame_post_draw
	var color := _center_linear(main_viewport)
	pipeline.call("RemoveBackdropEffect", main_world, effect)
	far.queue_free()
	main_viewport.queue_free()
	return color

func _measure_far_viewport_color_space() -> Dictionary:
	var rows: Array = []
	var single_error := 0.0
	var double_error := 0.0
	var encoded_error := 0.0
	var stimuli := [Color(0.15, 0.30, 0.45), Color(0.50, 0.60, 0.69), Color(3.0, 2.0, 1.0)]
	for stimulus in stimuli:
		var direct: Color = await _direct_color(stimulus, Environment.TONE_MAPPER_AGX)
		var via_far: Color = await _via_far_color(stimulus, Environment.TONE_MAPPER_AGX)
		# What a second AgX pass over the already-tonemapped far image would give.
		var doubled: Color = await _direct_color(direct, Environment.TONE_MAPPER_AGX)
		# Same second pass, but fed the sRGB-ENCODED bytes of the far image as if they were linear radiance.
		var doubled_encoded: Color = await _direct_color(direct.linear_to_srgb(), Environment.TONE_MAPPER_AGX)
		if _failed: return {}
		var e_single := _max_diff(via_far, direct)
		var e_double := _max_diff(via_far, doubled)
		var e_encoded := _max_diff(via_far, doubled_encoded)
		single_error = maxf(single_error, e_single)
		double_error = maxf(double_error, e_double)
		encoded_error = maxf(encoded_error, e_encoded)
		rows.append({"stimulus_linear": [stimulus.r, stimulus.g, stimulus.b], "direct_single_tonemap": [direct.r, direct.g, direct.b],
			"via_far_viewport": [via_far.r, via_far.g, via_far.b], "predicted_double_tonemap": [doubled.r, doubled.g, doubled.b],
			"predicted_double_srgb_bytes_as_linear": [doubled_encoded.r, doubled_encoded.g, doubled_encoded.b],
			"err_vs_single": e_single, "err_vs_double": e_double, "err_vs_double_srgb_bytes_as_linear": e_encoded})
	var classification := "other"
	if single_error < 0.02:
		classification = "single_tonemap"
	elif double_error < 0.02:
		classification = "double_tonemap_with_srgb_decode"
	elif encoded_error < 0.02:
		classification = "double_tonemap_srgb_bytes_treated_as_linear"
	return {"classification": classification, "max_err_vs_single": single_error, "max_err_vs_double": double_error,
		"max_err_vs_double_srgb_bytes_as_linear": encoded_error, "rows": rows}

func _max_diff(a: Color, b: Color) -> float:
	return maxf(absf(a.r - b.r), maxf(absf(a.g - b.g), absf(a.b - b.b)))

# --- output -----------------------------------------------------------------------------------------------

func _print_report(metrics: Array, cross: Dictionary, color_space: Dictionary) -> void:
	print("HAZE_CONSISTENCY model=%s altitude=%.0fm preset=%s azimuth=%s" % ["physical" if _physical else "legacy", _altitude, "cinematic" if _cinematic else "natural", "toward-sun" if _toward_sun else "away-from-sun"])
	print("Columns: obj/cld = alpha (1-T); Sobj/Scld = in-scatter luminance (rgb*alpha*energy); dS = max-channel rel diff; sky = sky radiance luminance; S/sky = Sobj/sky")
	for light in LIGHTING:
		print("--- %s ---" % light)
		print("  elev  dist_km |  a_obj  a_cld   dA   | Sobj    Scld    dS_rel | sky     S/sky")
		for m in metrics:
			if m["case"]["light"] != light:
				continue
			print("  %4.0f  %6.0f | %6.3f %6.3f %5.3f | %7.4f %7.4f %6.3f | %7.4f %6.3f" % [
				m["case"]["elev"], m["case"]["dist_km"], m["obj_alpha"], m["cld_alpha"], m["d_alpha"],
				m["obj_lum"], m["cld_lum"], m["d_s_rel"], m["sky_lum"], m["conv_ratio"]])
	print("--- summary ---")
	for light in LIGHTING:
		var max_alpha := 0.0
		var max_s := 0.0
		for m in metrics:
			if m["case"]["light"] == light:
				max_alpha = maxf(max_alpha, m["d_alpha"])
				max_s = maxf(max_s, m["d_s_rel"])
		var conv := ""
		for m in metrics:
			if m["case"]["light"] == light and float(m["case"]["dist_km"]) == 800.0:
				conv += " e%.0f:S/sky=%.3f(T=%.2f)" % [m["case"]["elev"], m["conv_ratio"], m["obj_transmittance"]]
		print("%-8s max|dAlpha|=%.4f max dS_rel=%.4f | 800km convergence:%s" % [light, max_alpha, max_s, conv])
	print("--- colour space (far viewport -> main viewport, AgX) ---")
	for row in color_space["rows"]:
		print("  stimulus=%s direct=%s via_far=%s predicted_double=%s predicted_double_srgb_bytes_as_linear=%s" % [_fmt3(row["stimulus_linear"]), _fmt3(row["direct_single_tonemap"]), _fmt3(row["via_far_viewport"]), _fmt3(row["predicted_double_tonemap"]), _fmt3(row["predicted_double_srgb_bytes_as_linear"])])
	print("  classification=%s (max err vs single %.4f, vs double %.4f, vs double-srgb-bytes %.4f)" % [color_space["classification"], color_space["max_err_vs_single"], color_space["max_err_vs_double"], color_space["max_err_vs_double_srgb_bytes_as_linear"]])
	print("--- harness crosschecks ---")
	print("  spatial render vs compute alpha: max err %.4f over %d" % [cross["spatial_max_error"], cross["spatial_count"]])
	print("  sky render vs compute radiance: max rel err %.4f over %d" % [cross["sky_max_rel_error"], cross["sky_count"]])

func _fmt3(a: Array) -> String:
	return "(%.3f,%.3f,%.3f)" % [a[0], a[1], a[2]]

func _print_contracts(contracts: Array) -> void:
	print("--- contracts ---")
	for c in contracts:
		var status := "PASS"
		if not c["passed"]:
			status = "FAIL" if (_strict or String(c["known"]).is_empty()) else "XFAIL"
		elif not String(c["known"]).is_empty() and not _strict:
			status = "XPASS"
		print("  [%s] %s: %s%s" % [status, c["name"], c["detail"], ("  (expected until " + c["known"] + ")") if status in ["XFAIL", "XPASS"] else ""])

func _write_baseline(metrics: Array, contracts: Array, cross: Dictionary, color_space: Dictionary) -> void:
	if not _report_mode:
		return
	var path := "user://haze_consistency_baseline.json"
	for arg in _args:
		if arg.begins_with("--out="):
			path = arg.get_slice("=", 1)
	var cases: Array = []
	for m in metrics:
		cases.append({
			"light": m["case"]["light"], "elev_deg": m["case"]["elev"], "dist_km": m["case"]["dist_km"],
			"obj_alpha": m["obj_alpha"], "cld_alpha": m["cld_alpha"], "d_alpha": m["d_alpha"],
			"obj_s": [m["obj_s"].x, m["obj_s"].y, m["obj_s"].z], "cld_s": [m["cld_s"].x, m["cld_s"].y, m["cld_s"].z],
			"d_s_rel": m["d_s_rel"], "sky": [m["sky"].x, m["sky"].y, m["sky"].z], "sky_air_alpha": m["sky_air_alpha"],
			"conv_ratio": m["conv_ratio"], "obj_transmittance": m["obj_transmittance"]})
	var payload := {"model": "physical" if _physical else "legacy", "altitude_m": _altitude, "preset": "cinematic" if _cinematic else "natural", "toward_sun": _toward_sun,
		"cases": cases, "contracts": contracts, "crosschecks": cross, "color_space": color_space}
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_warning("could not write baseline to " + path)
		return
	file.store_string(JSON.stringify(payload, "\t"))
	file.close()
	print("baseline written: ", ProjectSettings.globalize_path(path) if path.begins_with("user://") else path)

func _finish(contracts: Array) -> void:
	var failures: Array = []
	var xfails := 0
	for c in contracts:
		var known := not String(c["known"]).is_empty() and not _strict
		if c["passed"] and known:
			failures.append("XPASS %s: remove it from KNOWN_FAILING" % c["name"])
		elif not c["passed"] and not known:
			failures.append("%s: %s" % [c["name"], c["detail"]])
		elif not c["passed"]:
			xfails += 1
	if _report_mode or failures.is_empty():
		print("HAZE_CONSISTENCY_GPU=%s contracts=%d expected_failures=%d unexpected=%d" % ["REPORT" if _report_mode else "PASS", contracts.size(), xfails, failures.size()])
		for f in failures:
			print("  would fail: ", f)
		_cleanup()
		quit(0)
	else:
		for f in failures:
			push_error("HAZE_CONSISTENCY_GPU=FAIL " + f)
		_cleanup()
		quit(1)

func _cleanup() -> void:
	_restore_globals()
	if _day_side != null:
		_day_side.free()

func _fail(message: String) -> void:
	if _failed:
		return
	_failed = true
	push_error("HAZE_CONSISTENCY_GPU=FAIL " + message)
	_restore_globals()
	quit(1)
