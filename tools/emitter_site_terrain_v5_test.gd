extends SceneTree

const TEST := preload("res://tools/EmitterSiteTerrainV5Test.cs")
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
		print("EMITTER_SITE_TERRAIN_V5_WRAPPER=PASS")
	else:
		push_error("EMITTER_SITE_TERRAIN_V5_WRAPPER=FAIL exit=%d" % code)
	quit(code)

# GDScript recipe resource -> C# bridge flow used by the launcher and the generation preview.
func _check_recipe_flow() -> int:
	var failures := 0
	var v5: TerrainGenerationRecipe = RECIPE.create_sites_default()
	if v5.generator_id != RECIPE.SITES_GENERATOR_ID or not v5.validation_errors().is_empty():
		push_error("v5 recipe factory/validation failed"); failures += 1
	var v4: TerrainGenerationRecipe = RECIPE.create_erosion_default()
	var v4_dict := v4.to_normalized_dictionary()
	var v5_dict := v5.to_normalized_dictionary()
	if v4_dict.size() != v5_dict.size():
		push_error("v5 normalized dictionary has different fields than v4"); failures += 1
	var bridge4 = BRIDGE.new()
	var bridge5 = BRIDGE.new()
	if not bool(bridge4.call("Configure", v4_dict)) or not bool(bridge5.call("Configure", v5_dict)):
		push_error("bridge rejected a recipe: %s %s" % [str(bridge4.get("LastError")), str(bridge5.get("LastError"))]); failures += 1
	else:
		if str(bridge4.get("DefinitionHash")) == str(bridge5.get("DefinitionHash")):
			push_error("v4 and v5 recipe hashes are equal"); failures += 1
		# Far from every site the v5 bridge equals v4; v4 stays natural.
		var same := true
		for i in 50:
			var around := float(1234567 + i * 98765)
			var across := float(40000 + i * 1999)
			if bridge4.call("CalculateHeight", around, across) != bridge5.call("CalculateHeight", around, across):
				same = false
		print("EMITTER_V5_GD_BRIDGE far_samples_equal=%s v4_hash=%s v5_hash=%s" % [str(same), str(bridge4.get("DefinitionHash")), str(bridge5.get("DefinitionHash"))])
	bridge4.call("Release")
	bridge5.call("Release")
	# JSON round trip of the snapshot used by the preview store / launcher.
	var round_trip := RECIPE.create_sites_default().snapshot()
	if round_trip.generator_id != RECIPE.SITES_GENERATOR_ID:
		push_error("snapshot lost the v5 generator id"); failures += 1
	return failures
