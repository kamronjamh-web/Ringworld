extends SceneTree
## Run with tools/run_tests.ps1 -Tests world_generation_recipe_test.
## Manual golden capture adds -- --capture --generation=N.

const TEST := preload("res://tools/WorldGenerationRecipeTest.cs")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const PREVIEW_STORE := preload("res://scripts/Terrain/Generation/terrain_generation_preview_store.gd")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var args := OS.get_cmdline_user_args()
	var generation := 1
	for arg in args:
		if arg.begins_with("--generation="):
			var value := arg.trim_prefix("--generation=")
			if not value.is_valid_int():
				_finish(1, "invalid --generation argument")
				return
			generation = int(value)
	var test = TEST.new()
	var error := _check_bridge(test)
	if not error.is_empty():
		_finish(1, error)
		return
	var code: int = test.Run(args.has("--capture"), generation)
	_finish(code, "")

func _check_bridge(test) -> String:
	for seed in [1337, 90210]:
		var recipe := RECIPE.create_world_default(1, seed)
		if recipe == null:
			return "could not create the GDScript world default"
		var errors := recipe.validation_errors()
		if not errors.is_empty():
			return "default recipe was rejected: " + ", ".join(errors)
		var fields := recipe.to_normalized_dictionary()
		if typeof(fields["world_generation"]) != TYPE_INT or typeof(fields["macro_region_size_m"]) != TYPE_INT or typeof(fields["review_features"]) != TYPE_STRING:
			return "v9 integer or string Variant types differ"
		for name in RECIPE.WORLD_CLIMATE_FIELDS:
			if name != "macro_region_size_m" and typeof(fields[name]) != TYPE_FLOAT:
				return "v9 climate Variant is not a float: " + name
		var error := _compare(fields, test.CanonicalDefault(seed))
		if not error.is_empty():
			return error

	var tuned := RECIPE.create_world_default()
	tuned.continental_frequency = 4.1234e-6
	tuned.mountain_rain_shadow_strength = 0.6
	var error := _compare(tuned.to_normalized_dictionary(), test.CanonicalTuned())
	if not error.is_empty():
		return error
	tuned.continental_frequency = INF
	if tuned.validation_errors().is_empty():
		return "GDScript validation accepted a nonfinite climate field"
	return _check_preview_store()

func _check_preview_store() -> String:
	var recipe := RECIPE.create_world_default()
	var path := "user://world_generation_preview_%d_%d.tres" % [OS.get_process_id(), Time.get_ticks_usec()]
	var absolute_path := ProjectSettings.globalize_path(path)
	if PREVIEW_STORE.save_recipe(recipe, path).is_empty():
		DirAccess.remove_absolute(absolute_path)
		return "preview store saved a v9 recipe"
	if ResourceSaver.save(recipe, path) != OK:
		return "could not write the isolated v9 preview fixture"
	var loaded := PREVIEW_STORE.load_recipe(path)
	DirAccess.remove_absolute(absolute_path)
	if loaded["recipe"] != null or str(loaded["error"]).is_empty():
		return "preview store loaded a v9 recipe"
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
		return "GDScript and C# v9 canonical JSON differ"
	return ""

func _finish(code: int, error: String) -> void:
	if code == 0:
		print("WORLD_GENERATION_RECIPE_WRAPPER=PASS")
	else:
		if not error.is_empty():
			push_error("WORLD_GENERATION_RECIPE=FAIL " + error)
		push_error("WORLD_GENERATION_RECIPE_WRAPPER=FAIL")
	quit(code)
