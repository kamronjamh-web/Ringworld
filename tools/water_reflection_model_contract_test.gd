extends SceneTree

# R1-2a: shader-text contract for the water reflection model (ringworld_reflection_model 0 = today, 1 = physical composition).

const PROXY_PATH := "res://materials/ring_ocean_proxy.gdshader"
const GLOBAL_PATH := "res://materials/ring_ocean_global.gdshader"
const APPEARANCE_PATH := "res://materials/ring_water_appearance.gdshaderinc"
const BRANCH := "if (ringworld_reflection_model >= 0.5) {"

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
	print("WATER_REFLECTION_MODEL_CONTRACT=PASS proxy_branch,global_branch,physical_f0,model0_verbatim,no_radiance,horizon_march,cloud_consumption")
	quit()

func _check_appearance(appearance: String) -> void:
	for token in ["RING_WATER_F0_PHYSICAL = 0.02", "RING_WATER_SPECULAR = 0.353553", "float ring_water_env_reflectance(float roughness, float n_dot_v)",
			"float ring_water_dielectric_specular()", "uniform float fresnel_strength", "uniform float fresnel_f0"]:
		_check(appearance.find(token) >= 0, "appearance missing: " + token)

# Returns the text between the model branch opening and its matching close, plus the text of the else branch (empty when absent).
func _split_branch(source: String, from: int = 0) -> Dictionary:
	var start := source.find(BRANCH, from)
	if start < 0:
		return {}
	var open := start + BRANCH.length() - 1
	var depth := 0
	var close := -1
	for i in range(open, source.length()):
		var c := source[i]
		if c == "{":
			depth += 1
		elif c == "}":
			depth -= 1
			if depth == 0:
				close = i
				break
	if close < 0:
		return {}
	var model1 := source.substr(open + 1, close - open - 1)
	var tail := source.substr(close + 1).strip_edges(true, false)
	var else_text := ""
	if tail.begins_with("else"):
		var else_open := source.find("{", close)
		var else_depth := 0
		for i in range(else_open, source.length()):
			var c := source[i]
			if c == "{":
				else_depth += 1
			elif c == "}":
				else_depth -= 1
				if else_depth == 0:
					else_text = source.substr(else_open + 1, i - else_open - 1)
					break
	return {"start": start, "close": close, "model1": model1, "else": else_text}

func _check_proxy(proxy: String) -> void:
	# The proxy takes the camera-sky gate too: ring_sky_reflection.gdshaderinc includes ring_reflection_model.gdshaderinc and declares the globals once.
	_check(proxy.find('#include "res://materials/ring_sky_reflection.gdshaderinc"') >= 0, "proxy must include ring_sky_reflection.gdshaderinc")
	_check(proxy.find('#include "res://materials/ring_reflection_model.gdshaderinc"') < 0, "proxy must not include the model include directly (the sky include carries it)")
	_check(proxy.find("global uniform") < 0, "proxy must not declare a global itself")
	var parts := _split_branch(proxy)
	_check(not parts.is_empty(), "proxy has no model branch")
	if parts.is_empty():
		return
	var model1: String = parts["model1"]
	var model0: String = parts["else"]
	_check(not model0.is_empty(), "proxy model branch needs an else (model 0)")
	for token in ["ring_water_env_reflectance(", "(1.0 - reflectance)", "ringworld_sky_reflection", "EMISSION = ", "ssr.rgb * (reflectance * hit_weight)"]:
		_check(model1.find(token) >= 0, "proxy model 1 block missing: " + token)
	var sqrt_at := model1.find("sqrt(")
	_check(sqrt_at >= 0 and model1.find("RING_WATER_SPECULAR", maxi(sqrt_at - 40, 0)) >= 0 and model1.find("RING_WATER_SPECULAR") < sqrt_at, "proxy model 1 SPECULAR must be RING_WATER_SPECULAR * sqrt(...)")
	for token in ["reflection_color", "fresnel_strength", "ocean_specular", "water_ssr_strength", "grazing_color"]:
		_check(model1.find(token) < 0, "proxy model 1 block must not use " + token)
	# Model 0 keeps the old composition (the tokens of water_ssr_contract_test live here).
	for token in ["reflection_color *= mix(1.0, ring_storm_sky(storm_water_level), storm_water_fade)", "ssr_weight = clamp(ssr.a * water_ssr_strength, 0.0, 1.0) * storm_ssr_keep",
			"reflection_color = mix(reflection_color, ssr.rgb, ssr_weight)", "grazing_color = mix(optical_water, reflection_color, reflection_weight)",
			"grazing_color = mix(grazing_color, foam_color, foam)", "ALBEDO = grazing_color * haze.T", "EMISSION = ring_haze_inscatter(haze);",
			"ring_water_dielectric_specular() * ocean_specular * ring_haze_specular_scale(haze) * storm_reflection"]:
		_check(model0.find(token) >= 0, "proxy model 0 (else) branch missing: " + token)
	# storm_reflection is defined before the branch (AO still uses it after it).
	var storm_at := proxy.find("float storm_reflection = ring_storm_ambient(storm_water_level)")
	_check(storm_at >= 0 and storm_at < int(parts["start"]), "storm_reflection must be defined before the model branch")
	_check(proxy.find("storm_reflection);", int(parts["close"])) >= 0, "AO must still use storm_reflection after the branch")
	_check(proxy.find("ring_water_ssr(") >= 0, "proxy must still call ring_water_ssr(")
	_check_horizon_march(proxy)

# R1-5a: the model 1 march (design 3.1 to 3.4) is a separate function; model 0 still calls the old one, unedited.
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
	var model1_call := proxy.find("ring_water_ssr_horizon(ssr_origin")
	var model0_call := proxy.find("ring_water_ssr(ssr_origin")
	_check(model1_call >= 0 and model0_call > model1_call, "model 1 call must come first with the old call in the else path")
	var gate := proxy.rfind("if (ringworld_reflection_model >= 0.5)", model1_call)
	_check(gate >= 0 and model1_call - gate < 80 and proxy.find("else", model1_call) > 0 and proxy.find("else", model1_call) < model0_call, "march call must sit under the model gate with an else for the old call")
	_check(proxy.find("vec3(ssr_hit_kind)") >= 0, "model 1 debug mode 2 must show ssr_hit_kind")
	# R3a-3a: the model 1 march consumes the published cloud image; the far viewport shaders do not.
	_check(proxy.find('#include "res://materials/ring_cloud_reflection.gdshaderinc"') >= 0, "proxy must include ring_cloud_reflection.gdshaderinc")
	# One colour-only sample for both hit kinds, not placed by the cloud distance (placement stepped mirrored cloud edges and cost a second sample).
	_check(body.find("ring_cloud_reflection_sample_color(") >= 0, "horizon march must call ring_cloud_reflection_sample_color(")
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
	# The global shader also renders in the far viewport: model include only, no camera-sky gate.
	_check(global.find('#include "res://materials/ring_reflection_model.gdshaderinc"') >= 0, "global must include ring_reflection_model.gdshaderinc")
	_check(global.find("ring_sky_reflection") < 0, "global must not use the camera-sky gate")
	_check(global.find("global uniform") < 0, "global shader must not declare a global itself")
	var old_specular := global.find("SPECULAR = ring_water_dielectric_specular() * ocean_specular")
	_check(old_specular >= 0, "global old SPECULAR statement missing")
	var parts := _split_branch(global)
	_check(not parts.is_empty(), "global has no model branch")
	if parts.is_empty():
		return
	_check(int(parts["start"]) > old_specular, "global model 1 block must come after the old SPECULAR statement")
	_check(global.find("METALLIC = 0.0;", int(parts["close"])) > 0, "global model 1 block must come before METALLIC")
	var model1: String = parts["model1"]
	for token in ["ring_water_env_reflectance(", "(1.0 - ring_water_env_reflectance(", "RING_WATER_SPECULAR * sqrt(", "ring_haze_specular_scale(haze)", "haze.T"]:
		_check(model1.find(token) >= 0, "global model 1 block missing: " + token)
	_check(model1.find("vec3(0.12, 0.28, 0.42)") < 0, "global model 1 block must not use the constant blue")
	_check(global.find("vec3(0.12, 0.28, 0.42)") >= 0, "global model 0 constant blue must stay for model 0")
