extends SceneTree

# R1-2a / R1-8: shader-text contract for the water reflection. Model 1 (physical composition, horizon march) is the only path; model 0 and the switch are gone.

const PROXY_PATH := "res://materials/ring_ocean_proxy.gdshader"
const GLOBAL_PATH := "res://materials/ring_ocean_global.gdshader"
const APPEARANCE_PATH := "res://materials/ring_water_appearance.gdshaderinc"

var _failed := false

func _check(condition: bool, message: String) -> void:
	if not condition:
		push_error("Water reflection model contract: " + message)
		_failed = true

func _init() -> void:
	# Normalise line endings: a checkout with core.autocrlf=true writes CRLF, and the body scan looks for LF "}" lines.
	var proxy := FileAccess.get_file_as_string(PROXY_PATH).replace("\r\n", "\n")
	var global := FileAccess.get_file_as_string(GLOBAL_PATH).replace("\r\n", "\n")
	var appearance := FileAccess.get_file_as_string(APPEARANCE_PATH).replace("\r\n", "\n")
	_check(not proxy.is_empty() and not global.is_empty() and not appearance.is_empty(), "shader source unreadable")
	_check_appearance(appearance)
	_check_proxy(proxy)
	_check_global(global)
	for entry in [["proxy", proxy], ["global", global], ["appearance", appearance]]:
		var text: String = entry[1]
		_check(text.find("specular_occlusion_disabled") < 0, str(entry[0]) + " must not use specular_occlusion_disabled")
		_check(text.find("RADIANCE") < 0, str(entry[0]) + " must not write RADIANCE")
	if _failed:
		print("WATER_REFLECTION_MODEL_CONTRACT=FAIL")
		quit(1)
		return
	print("WATER_REFLECTION_MODEL_CONTRACT=PASS single_path,no_switch,physical_f0,no_radiance,horizon_march,cloud_consumption")
	quit()

func _check_appearance(appearance: String) -> void:
	for token in ["RING_WATER_F0_PHYSICAL = 0.02", "RING_WATER_SPECULAR = 0.353553", "float ring_water_env_reflectance(float roughness, float n_dot_v)"]:
		_check(appearance.find(token) >= 0, "appearance missing: " + token)
	for token in ["far_water_roughness", "fresnel_f0", "fresnel_strength", "water_ssr_strength", "ring_water_schlick_fresnel", "ring_water_dielectric_specular"]:
		_check(appearance.find(token) < 0, "appearance must not carry the retired " + token)

func _check_single_path(name: String, text: String) -> void:
	for token in ["ringworld_reflection_model", "ring_reflection_model", "far_water_roughness", "fresnel_f0", "fresnel_strength", "water_ssr_strength", "ring_water_dielectric_specular", "ring_water_schlick_fresnel"]:
		_check(text.find(token) < 0, name + " must not use the retired " + token)

func _check_proxy(proxy: String) -> void:
	_check(proxy.find('#include "res://materials/ring_sky_reflection.gdshaderinc"') >= 0, "proxy must include ring_sky_reflection.gdshaderinc")
	_check(proxy.find("global uniform") < 0, "proxy must not declare a global itself")
	_check_single_path("proxy", proxy)
	_check(proxy.find("ring_water_ssr(") < 0, "proxy must not carry the old ring_water_ssr( march")
	for token in ["reflection_color", "grazing_color", "ssr_weight", "reflection_weight", "float fresnel"]:
		_check(proxy.find(token) < 0, "proxy must not carry the model 0 composition token " + token)
	_check(proxy.find("float far_roughness = ring_water_far_roughness_model1(water_kind_profile);") >= 0, "proxy far roughness must be the per-kind model 1 value, unconditionally")
	for token in ["ring_water_env_reflectance(", "(1.0 - reflectance)", "ringworld_sky_reflection", "EMISSION = ", "ssr.rgb * (reflectance * hit_weight)", "vec3 body = mix(optical_water, foam_color, foam);",
			"ALBEDO = body * (1.0 - reflectance) * haze.T;", "water_ssr_debug_mode != 0", "vec3(ssr_hit_kind)"]:
		_check(proxy.find(token) >= 0, "proxy model 1 composition missing: " + token)
	var sqrt_at := proxy.find("SPECULAR = RING_WATER_SPECULAR * sqrt(")
	_check(sqrt_at >= 0, "proxy SPECULAR must be RING_WATER_SPECULAR * sqrt(...)")
	# storm_reflection is defined before the composition (AO still uses it after it).
	var storm_at := proxy.find("float storm_reflection = ring_storm_ambient(storm_water_level)")
	_check(storm_at >= 0 and storm_at < proxy.find("float final_roughness"), "storm_reflection must be defined before the composition")
	_check(proxy.find("storm_reflection);", sqrt_at) >= 0, "AO must still use storm_reflection after the composition")
	_check_horizon_march(proxy)

# R1-5a: the model 1 march (design 3.1 to 3.4) is the only march.
func _check_horizon_march(proxy: String) -> void:
	var define_at := proxy.find("vec4 ring_water_ssr_horizon(")
	_check(define_at >= 0, "proxy must define ring_water_ssr_horizon(")
	if define_at < 0:
		return
	var body_end := proxy.find("\n}\n", define_at)
	var body := proxy.substr(define_at, body_end - define_at) if body_end > define_at else ""
	_check(not body.is_empty(), "ring_water_ssr_horizon body not found")
	for token in ["same_surface", "hit_kind = 0.5", "hit_kind = 1.0", "ceil(", "1e-6", "smoothstep(0.001, 0.004,", "smoothstep(50.0, 150.0,", "water_ssr_camera_distance", "water_ssr_coarse_steps", "water_ssr_refinement_steps"]:
		_check(body.find(token) >= 0, "horizon march missing: " + token)
	for token in ["water_ssr_strength", "water_ssr_max_ray_distance", "water_ssr_depth_tolerance_far", "distance_confidence"]:
		_check(body.find(token) < 0, "horizon march must not use " + token)
	var march_call := proxy.find("ring_water_ssr_horizon(ssr_origin")
	_check(march_call >= 0 and proxy.find("ssr = ring_water_ssr_horizon(ssr_origin") >= 0, "the march call must be the plain unconditional assignment")
	_check(proxy.rfind("ringworld_reflection_model", march_call) < 0 and proxy.find("else\n\t\tssr = ", march_call - 40) < 0, "the march call must not sit under a model gate")
	_check(proxy.find("vec3(ssr_hit_kind)") >= 0, "model 1 debug mode 2 must show ssr_hit_kind")
	# R3a-3a: the model 1 march consumes the published cloud image; the far viewport shaders do not.
	_check(proxy.find('#include "res://materials/ring_cloud_reflection.gdshaderinc"') >= 0, "proxy must include ring_cloud_reflection.gdshaderinc")
	# One colour-only sample for both hit kinds, not placed by the cloud distance (placement stepped mirrored cloud edges and cost a second sample).
	# W3 (2026-11-20): the colour sample takes the cloud uv, so a hit at infinity can clamp it to the nearest on-screen column.
	_check(body.find("ring_cloud_reflection_sample_color_uv(") >= 0 and body.count("ring_cloud_reflection_sample_color") == 1, "horizon march must take one ring_cloud_reflection_sample_color_uv( sample")
	_check(body.find("vec2 horizon_uv = vec2(clamp(uv1.x, lo.x, hi.x), uv1.y);") >= 0, "a hit at infinity must reuse the nearest on-screen column (W3)")
	_check(body.find("ring_cloud_reflection_near_fade(") >= 0, "horizon march must apply ring_cloud_reflection_near_fade( on geometry hits")
	_check(body.find("ring_cloud_reflection_sample_placed(") < 0 and body.find("ring_cloud_reflection_sample(") < 0, "horizon march must not take the full or placed sample")
	_check(proxy.find("mix(storm_ssr_keep, 1.0, ssr_cloud_valid)") >= 0, "model 1 hit weight must lift the storm fade where the cloud image is valid")
	var global_text := FileAccess.get_file_as_string(GLOBAL_PATH).replace("\r\n", "\n")
	_check(global_text.find("ring_cloud_reflection.gdshaderinc") < 0, "ring_ocean_global must not include the cloud reflection include")
	for file_name in DirAccess.get_files_at("res://materials"):
		if file_name.ends_with(".gdshader") and file_name.to_lower().find("far") >= 0:
			var far_text := FileAccess.get_file_as_string("res://materials/" + file_name)
			_check(far_text.find("ring_cloud_reflection.gdshaderinc") < 0, file_name + " must not include the cloud reflection include")


func _check_global(global: String) -> void:
	# The global shader also renders in the far viewport: no camera-sky gate, no model include.
	_check_single_path("global", global)
	_check(global.find("ring_sky_reflection") < 0, "global must not use the camera-sky gate")
	_check(global.find("global uniform") < 0, "global shader must not declare a global itself")
	for token in ["float fresnel", "reflection_weight", "grazing_color", "vec3(0.12, 0.28, 0.42)"]:
		_check(global.find(token) < 0, "global must not carry the model 0 token " + token)
	_check(global.find("float far_roughness = ring_water_far_roughness_model1(water_kind_profile);") >= 0, "global far roughness must be the per-kind model 1 value")
	for token in ["ring_water_env_reflectance(", "(1.0 - ring_water_env_reflectance(", "SPECULAR = RING_WATER_SPECULAR * sqrt(ring_haze_specular_scale(haze))", "haze.T"]:
		_check(global.find(token) >= 0, "global model 1 composition missing: " + token)
	_check(global.find("METALLIC = 0.0;", global.find("SPECULAR = RING_WATER_SPECULAR")) > 0, "global composition must come before METALLIC")
	_check(global.find("ring_water_anchor") < 0, "the far ocean must keep its legacy wave coordinate")
