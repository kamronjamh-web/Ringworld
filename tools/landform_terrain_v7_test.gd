extends SceneTree

## Recipe v7 (ringworld_surface_landform_v7) harness: C# sections plus the GDScript recipe/store/preview plumbing checks.
## `-- --section=recipe|gd_recipe|shape|gullies|world` runs one section; the default runs recipe and gd_recipe (shape is slow; request it explicitly).
const TEST := preload("res://tools/LandformTerrainV7Test.cs")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const PREVIEW := preload("res://scripts/Terrain/Generation/terrain_generation_preview.gd")
const STORE := preload("res://scripts/Terrain/Generation/terrain_generation_preview_store.gd")
const STORE_PATH := "res://.godot/terrain-generation-tests/landform_v7_store.tres"
const PREVIEW_PATH := "res://.godot/terrain-generation-tests/landform_v7_preview.tres"

const V7_FLOAT_FIELDS := ["landform_scale_m", "plains_threshold", "hills_threshold", "plateau_scale_m", "plateau_threshold", "plateau_full_threshold", "hill_amplitude_m", "hill_scale_m", "hill_detail_weight", "hill_valley_exponent", "plains_hill_fraction", "hill_valley_floor", "plateau_lift_m", "plateau_step_m", "terrace_riser_width", "terrace_strength", "escarpment_jitter_m", "escarpment_scale_m", "breaks_strength", "breaks_step_m", "landform_shore_start_m", "landform_shore_full_m", "lowland_detail_floor", "plains_detail_fraction", "lowland_erosion_strength_m"]
const V7_INT_FIELDS := ["climate_machines", "lowland_erosion_octaves"]

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var sections := _requested_sections()
	var runner = TEST.new()
	var code := 0
	for section in sections:
		if section == "gd_recipe":
			code += _check_gd_recipe(runner)
		else:
			code += int(runner.Run(section))
	runner = null
	if code == 0:
		print("LANDFORM_TERRAIN_V7_WRAPPER=PASS sections=%s" % ",".join(sections))
	else:
		push_error("LANDFORM_TERRAIN_V7_WRAPPER=FAIL exit=%d" % code)
	quit(code)

func _requested_sections() -> PackedStringArray:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--section="):
			return PackedStringArray([arg.trim_prefix("--section=")])
	return PackedStringArray(["recipe", "gd_recipe"])

func _fail(message: String) -> int:
	push_error("LANDFORM_V7_GD_FAIL " + message)
	return 1

func _bridge_configure(recipe: TerrainGenerationRecipe) -> Array:
	var bridge = BRIDGE.new()
	var ok := bool(bridge.call("Configure", recipe.to_normalized_dictionary()))
	return [bridge, ok]

func _check_gd_recipe(runner) -> int:
	var failures := 0
	var landform: TerrainGenerationRecipe = RECIPE.create_landform_default()
	if landform.generator_id != RECIPE.LANDFORM_GENERATOR_ID or not landform.validation_errors().is_empty():
		failures += _fail("v7 factory/validation failed")
	# Default dictionary configures the bridge and matches the C# default byte for byte.
	var configured := _bridge_configure(landform)
	var bridge = configured[0]
	if not configured[1]:
		failures += _fail("bridge rejected the v7 default: %s" % str(bridge.get("LastError")))
	elif str(bridge.get("CanonicalJson")) != str(runner.call("LandformDefaultCanonicalJson")):
		failures += _fail("GDScript v7 default canonical json differs from LandformDefault()")
	bridge.call("Release")
	var dictionary := landform.to_normalized_dictionary()
	for key in V7_INT_FIELDS:
		if typeof(dictionary.get(key)) != TYPE_INT:
			failures += _fail("%s must be emitted as an int" % key)
	for key in V7_FLOAT_FIELDS:
		if not dictionary.has(key):
			failures += _fail("normalized dictionary lacks %s" % key)
	if dictionary.size() != RECIPE.create_climate_default().to_normalized_dictionary().size() + V7_INT_FIELDS.size() + V7_FLOAT_FIELDS.size():
		failures += _fail("v7 normalized dictionary field count")
	for id in [RECIPE.EROSION_GENERATOR_ID, RECIPE.SITES_GENERATOR_ID, RECIPE.CLIMATE_GENERATOR_ID]:
		var older: TerrainGenerationRecipe = RECIPE.create_climate_default()
		older.generator_id = id
		if older.to_normalized_dictionary().has("hill_amplitude_m"):
			failures += _fail("%s dictionary leaked v7 fields" % id)
	# C#/GDScript range drift guard: wild out-of-range, NaN and infinite values must all sanitize to something the bridge accepts.
	var rng := RandomNumberGenerator.new()
	rng.seed = 7007
	var wild := [-1.0e9, -1.0, 0.0, 0.5, 1.0, 1.0e9, NAN, INF, -INF]
	var accepted := 0
	for i in 500:
		var wild_recipe: TerrainGenerationRecipe = RECIPE.create_landform_default()
		for key in V7_FLOAT_FIELDS:
			var pick := rng.randi_range(0, 3)
			if pick == 0:
				wild_recipe.set(key, wild[rng.randi_range(0, wild.size() - 1)])
			elif pick == 1:
				wild_recipe.set(key, rng.randf_range(-2.0, 2.0) * pow(10.0, rng.randi_range(-3, 6)))
		wild_recipe.set("climate_machines", rng.randi_range(-3, 4))
		wild_recipe.set("lowland_erosion_octaves", rng.randi_range(-3, 9))
		wild_recipe.set("erosion_octaves", rng.randi_range(-3, 9))
		var result := _bridge_configure(wild_recipe.snapshot())
		if result[1]:
			accepted += 1
		else:
			failures += _fail("randomized recipe %d rejected by the bridge: %s" % [i, str(result[0].get("LastError"))])
		result[0].call("Release")
		if failures > 5:
			break
	print("LANDFORM_V7_GD_RANDOMIZED accepted=%d/500" % accepted)
	# Store round trip of every v7 field.
	var edited: TerrainGenerationRecipe = RECIPE.create_landform_default()
	edited.climate_machines = 1
	edited.landform_scale_m = 41000.0
	edited.plains_threshold = 0.4
	edited.hills_threshold = 0.6
	edited.plateau_scale_m = 70000.0
	edited.plateau_threshold = 0.55
	edited.plateau_full_threshold = 0.7
	edited.hill_amplitude_m = 333.0
	edited.hill_scale_m = 4200.0
	edited.hill_detail_weight = 0.5
	edited.hill_valley_exponent = 1.6
	edited.plains_hill_fraction = 0.12
	edited.hill_valley_floor = 0.7
	edited.plateau_lift_m = 210.0
	edited.plateau_step_m = 120.0
	edited.terrace_riser_width = 0.2
	edited.terrace_strength = 0.5
	edited.escarpment_jitter_m = 55.0
	edited.escarpment_scale_m = 9000.0
	edited.breaks_strength = 0.4
	edited.breaks_step_m = 30.0
	edited.landform_shore_start_m = 30.0
	edited.landform_shore_full_m = 200.0
	edited.lowland_detail_floor = 0.3
	edited.plains_detail_fraction = 0.1
	edited.lowland_erosion_strength_m = 12.0
	edited.lowland_erosion_octaves = 1
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/terrain-generation-tests"))
	var save_error: String = STORE.save_recipe(edited, STORE_PATH)
	if not save_error.is_empty():
		failures += _fail("store save failed: " + save_error)
	else:
		var loaded: Dictionary = STORE.load_recipe(STORE_PATH)
		var loaded_recipe: TerrainGenerationRecipe = loaded.recipe
		if loaded_recipe == null or loaded_recipe.generator_id != RECIPE.LANDFORM_GENERATOR_ID:
			failures += _fail("store load failed: " + str(loaded.error))
		else:
			for key in V7_FLOAT_FIELDS + V7_INT_FIELDS:
				if loaded_recipe.get(key) != edited.get(key):
					failures += _fail("store round trip changed %s: %s != %s" % [key, str(loaded_recipe.get(key)), str(edited.get(key))])
		DirAccess.remove_absolute(ProjectSettings.globalize_path(STORE_PATH))
	# Preview plumbing: upgrade keeps settings and climate, climate toggle switches the field on v7 and the id on v5/v6.
	failures += _check_preview_plumbing()
	if failures == 0:
		print("LANDFORM_V7_GD_RECIPE=PASS")
	return failures

func _check_preview_plumbing() -> int:
	var failures := 0
	for case in [[RECIPE.EROSION_GENERATOR_ID, 0], [RECIPE.SITES_GENERATOR_ID, 0], [RECIPE.CLIMATE_GENERATOR_ID, 1]]:
		var preview = PREVIEW.new()
		preview.configure_test_fixture(PREVIEW_PATH)
		var source: TerrainGenerationRecipe = RECIPE.create_climate_default()
		source.generator_id = case[0]
		source.land_balance = 0.07
		source.mountain_amplitude_m = 4200.0
		preview._recipe = source
		preview.upgrade_to_landforms()
		var upgraded: TerrainGenerationRecipe = preview.get_recipe_snapshot()
		if upgraded.generator_id != RECIPE.LANDFORM_GENERATOR_ID or upgraded.climate_machines != case[1] or absf(upgraded.land_balance - 0.07) > 1e-9 or upgraded.mountain_amplitude_m != 4200.0:
			failures += _fail("upgrade_to_landforms from %s did not preserve settings" % case[0])
		if not upgraded.validation_errors().is_empty():
			failures += _fail("upgraded recipe invalid")
		preview.set_climate_machines(true)
		if preview.get_recipe_snapshot().climate_machines != 1:
			failures += _fail("set_climate_machines(true) did not set the v7 field")
		preview.set_climate_machines(false)
		var off: TerrainGenerationRecipe = preview.get_recipe_snapshot()
		if off.climate_machines != 0 or off.generator_id != RECIPE.LANDFORM_GENERATOR_ID:
			failures += _fail("set_climate_machines(false) on v7")
		preview.upgrade_to_landforms()
		if preview.get_recipe_snapshot().generator_id != RECIPE.LANDFORM_GENERATOR_ID:
			failures += _fail("second upgrade changed the recipe")
		preview.free()
	var legacy = PREVIEW.new()
	legacy.configure_test_fixture(PREVIEW_PATH)
	var v5: TerrainGenerationRecipe = RECIPE.create_sites_default()
	legacy._recipe = v5
	legacy.set_climate_machines(true)
	if legacy.get_recipe_snapshot().generator_id != RECIPE.CLIMATE_GENERATOR_ID:
		failures += _fail("v5 climate toggle changed")
	legacy.set_climate_machines(false)
	if legacy.get_recipe_snapshot().generator_id != RECIPE.SITES_GENERATOR_ID:
		failures += _fail("v6 climate toggle changed")
	legacy.free()
	return failures
