extends SceneTree

func _init() -> void:
	var shader := FileAccess.get_file_as_string("res://materials/ring_ocean_proxy.gdshader")
	var include := FileAccess.get_file_as_string("res://materials/ring_water_appearance.gdshaderinc")
	var model := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingWaterModel.cs")
	var defaults := FileAccess.get_file_as_string("res://scripts/ui/GameplayWaterDefaults.cs")
	var required := [
		"ring_water_ssr", "water_ssr_enabled", "water_ssr_ray_bias", "water_ssr_coarse_steps",
		"water_ssr_refinement_steps", "depth_agreement", "edge_confidence", "roughness_confidence", "previous_signed_delta < 0.0", "start_scene_depth", "previous_valid = true", "ssr_step_ratio",
		"textureLod(water_screen_texture, hit_uv", "reflection_color = mix(reflection_color, ssr.rgb, ssr_weight)", "grazing_color = mix(optical_water, reflection_color, reflection_weight)",
		"grazing_color = mix(grazing_color, foam_color, foam)"
	]
	for token in required:
		if shader.find(token) < 0 and include.find(token) < 0:
			push_error("SSR contract missing: " + token)
			quit(1)
	var graphics := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingGraphicsQualityController.cs")
	for token in ["WaterSsrEnabled", "WaterSsrRayBias"]:
		if graphics.find(token) < 0 or defaults.find(token) < 0:
			push_error("SSR persistence/binding missing: " + token)
			quit(1)
	if model.find("water_ssr_depth_tolerance_near") < 0 or defaults.find("WaterSsrDepthToleranceNear") < 0:
		push_error("SSR tolerance binding/persistence missing")
		quit(1)
	var reflection_mix := shader.find("reflection_color = mix(reflection_color, ssr.rgb, ssr_weight)")
	var optical_mix := shader.find("grazing_color = mix(optical_water, reflection_color, reflection_weight)", reflection_mix)
	if reflection_mix < 0 or optical_mix < reflection_mix or shader.find("ALBEDO = grazing_color") < optical_mix:
		push_error("SSR reflection color must be resolved before optical composition and final ALBEDO")
		quit(1)
	print("WATER_SSR_CONTRACT=PASS bounded_ray,depth_agreement,reflection_only,persistence")
	quit()
