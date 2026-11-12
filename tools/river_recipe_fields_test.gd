extends SceneTree
## Run with tools/run_tests.ps1 -Tests river_recipe_fields_test.

const TEST := preload("res://tools/RiverRecipeFieldsTest.cs")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var test = TEST.new()
	var error := _check_bridge(test)
	if not error.is_empty():
		_finish(1, error)
		return
	_finish(test.Run(), "")

func _check_bridge(test) -> String:
	var recipe := RECIPE.create_world_default(1, 1337, PackedStringArray(["water.rivers"]))
	if recipe == null:
		return "could not create the GDScript river recipe"
	var errors := recipe.validation_errors()
	if not errors.is_empty():
		return "river recipe was rejected: " + ", ".join(errors)
	for name in ["river_density", "lake_abundance", "river_valley_width", "channel_depth", "meander_strength", "river_ferocity"]:
		if not recipe.extension_values.has(name):
			return "GDScript recipe lacks " + name
	var error := _compare(recipe.to_normalized_dictionary(), test.CanonicalReview(1337))
	if not error.is_empty():
		return error
	recipe.extension_values["meander_strength"] = 1.5
	recipe.extension_values["lake_abundance"] = 0.25
	error = _compare(recipe.to_normalized_dictionary(), test.CanonicalTuned())
	if not error.is_empty():
		return error
	recipe.extension_values["river_density"] = 9.0
	if recipe.validation_errors().is_empty():
		return "GDScript validation accepted river_density out of range"
	return ""

func _compare(fields: Dictionary, expected: String) -> String:
	var bridge = BRIDGE.new()
	var configured: bool = bridge.Configure(fields)
	var error := str(bridge.LastError)
	var actual := str(bridge.CanonicalJson)
	bridge.Release()
	if not configured:
		return "C# bridge rejected the GDScript snapshot: " + error
	if actual != expected:
		return "GDScript and C# river canonical JSON differ"
	return ""

func _finish(code: int, error: String) -> void:
	if code == 0:
		print("RIVER_RECIPE_FIELDS_WRAPPER=PASS")
	else:
		if not error.is_empty():
			push_error("RIVER_RECIPE_FIELDS=FAIL " + error)
		push_error("RIVER_RECIPE_FIELDS_WRAPPER=FAIL")
	quit(code)
