extends SceneTree

func _init() -> void:
	var shader := FileAccess.get_file_as_string("res://materials/ring_ocean_proxy.gdshader")
	var include := FileAccess.get_file_as_string("res://materials/ring_water_appearance.gdshaderinc")
	var model := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingWaterModel.cs")
	var defaults := FileAccess.get_file_as_string("res://scripts/ui/GameplayWaterDefaults.cs")
	var required := [
		"ring_water_ssr_horizon", "water_ssr_enabled", "water_ssr_ray_bias", "water_ssr_coarse_steps",
		"water_ssr_refinement_steps", "roughness_confidence", "previous_signed_delta < 0.0", "start_scene_depth", "ssr_step_ratio",
		"body = mix(optical_water, foam_color, foam)", "ALBEDO = body * (1.0 - reflectance) * haze.T", "ssr.rgb * (reflectance * hit_weight)"
	]
	for token in required:
		if shader.find(token) < 0 and include.find(token) < 0:
			push_error("SSR contract missing: " + token)
			quit(1)
	# R1-8: model 1 is the only path; the old march, the fixed-colour composition and the retired uniforms are gone.
	for token in ["ring_water_ssr(", "reflection_weight", "grazing_color", "ssr_weight", "water_ssr_strength", "fresnel_strength"]:
		if shader.find(token) >= 0 or include.find(token) >= 0:
			push_error("SSR contract: retired token still present: " + token)
			quit(1)
	var graphics := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingGraphicsQualityController.cs")
	for token in ["WaterSsrEnabled", "WaterSsrRayBias"]:
		if graphics.find(token) < 0 or defaults.find(token) < 0:
			push_error("SSR persistence/binding missing: " + token)
			quit(1)
	if model.find("water_ssr_depth_tolerance_near") < 0 or defaults.find("WaterSsrDepthToleranceNear") < 0:
		push_error("SSR tolerance binding/persistence missing")
		quit(1)
	var march_call := shader.find("ring_water_ssr_horizon(ssr_origin")
	var composition := shader.find("ALBEDO = body * (1.0 - reflectance) * haze.T")
	if march_call < 0 or composition < march_call:
		push_error("SSR must be marched before the reflection composition")
		quit(1)
	print("WATER_SSR_CONTRACT=PASS horizon_march,model1_composition,persistence")
	quit()
