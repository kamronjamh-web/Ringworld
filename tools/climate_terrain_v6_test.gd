extends SceneTree

const TEST := preload("res://tools/ClimateTerrainV6Test.cs")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	code += _check_recipe_flow()
	if code == 0:
		print("CLIMATE_TERRAIN_V6_WRAPPER=PASS")
	else:
		push_error("CLIMATE_TERRAIN_V6_WRAPPER=FAIL exit=%d" % code)
	quit(code)

# GDScript recipe resource -> C# bridge flow used by the launcher and the generation preview.
func _check_recipe_flow() -> int:
	var failures := 0
	var v5: TerrainGenerationRecipe = RECIPE.create_sites_default()
	var v6: TerrainGenerationRecipe = RECIPE.create_climate_default()
	if v6.generator_id != RECIPE.CLIMATE_GENERATOR_ID or not v6.validation_errors().is_empty():
		push_error("v6 recipe factory/validation failed"); failures += 1
	if v5.generator_id != RECIPE.SITES_GENERATOR_ID:
		push_error("v5 factory changed"); failures += 1
	var v5_dict := v5.to_normalized_dictionary()
	var v6_dict := v6.to_normalized_dictionary()
	if v5_dict.size() != v6_dict.size():
		push_error("v6 normalized dictionary has different fields than v5"); failures += 1
	var bridge5 = BRIDGE.new()
	var bridge6 = BRIDGE.new()
	if not bool(bridge5.call("Configure", v5_dict)) or not bool(bridge6.call("Configure", v6_dict)):
		push_error("bridge rejected a recipe: %s %s" % [str(bridge5.get("LastError")), str(bridge6.get("LastError"))]); failures += 1
	else:
		if str(bridge5.get("DefinitionHash")) == str(bridge6.get("DefinitionHash")):
			push_error("v5 and v6 recipe hashes are equal"); failures += 1
		if not str(bridge6.get("CanonicalJson")).contains("ringworld_surface_climate_v6"):
			push_error("bridge canonical json lost the v6 id"); failures += 1
		# Far from every machine and site the v6 bridge equals v5 (machines only shape their forecourt).
		var same := true
		for i in 50:
			var around := float(1234567 + i * 98765)
			var across := float(40000 + i * 1999)
			if bridge5.call("CalculateHeight", around, across) != bridge6.call("CalculateHeight", around, across):
				same = false
		if not same:
			push_error("v6 bridge differs from v5 away from the forecourts"); failures += 1
		# A west-wall forecourt centre is exactly the machine pad and differs from v5 unless the pad equals natural.
		print("CLIMATE_V6_GD_BRIDGE far_samples_equal=%s v5_hash=%s v6_hash=%s" % [str(same), str(bridge5.get("DefinitionHash")), str(bridge6.get("DefinitionHash"))])
	bridge5.call("Release")
	bridge6.call("Release")
	var round_trip := RECIPE.create_climate_default().snapshot()
	if round_trip.generator_id != RECIPE.CLIMATE_GENERATOR_ID:
		push_error("snapshot lost the v6 generator id"); failures += 1
	return failures
