extends SceneTree

const SHADER_PATH := "res://materials/ring_ocean_proxy.gdshader"

func _init() -> void:
	var source := FileAccess.get_file_as_string(SHADER_PATH)
	var active_refraction := source.contains("vec2 refracted_uv = clamp(") \
		and source.contains("water_normal_view.xy * refraction_strength") \
		and source.contains("textureLod(water_screen_texture, refracted_uv")
	var rgb_optics := source.contains("scene_transmission_rgb = exp(") \
		and source.contains("refracted_scene * volume_transmission_rgb") \
		and source.contains("water_tint * (1.0 - volume_transmission_rgb)")
	var volume_controls := source.contains("float volume_coverage = mix(shallow_alpha, deep_alpha, absorption)") \
		and source.contains("vec3 volume_transmission_rgb = mix(vec3(1.0), scene_transmission_rgb, volume_coverage)")
	var shallow_only_scatter := source.contains("* scene_transmission * shallow_inscatter_strength")
	var single_composition := source.contains("ALPHA = 0.995")
	var crest_tint := source.contains("vec3 transmission_tint = mix(vec3(0.20, 0.85, 0.78)")
	if not (active_refraction and rgb_optics and volume_controls and shallow_only_scatter and single_composition and crest_tint):
		push_error("WATER_OPTICS_CONTRACT_FAIL refraction=%s rgb=%s volume=%s shallow=%s single=%s crest=%s" % [active_refraction, rgb_optics, volume_controls, shallow_only_scatter, single_composition, crest_tint])
		quit(1)
		return
	print("WATER_OPTICS_CONTRACT=PASS refraction,rgb_beer_lambert,volume_controls,shallow_scatter,single_composition,crest_tint")
	quit()
