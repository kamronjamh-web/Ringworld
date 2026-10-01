extends SceneTree

## Contract coverage for the versioned erosion recipe. This intentionally does
## not update the frozen v1-v3 golden fixture: v4 is a new generator identity.
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const PREVIEW_SCENE := preload("res://scenes/TerrainGenerationPreview.tscn")
const REPORT_PATH := "res://.godot/terrain-generation-tests/erosion-v4-report.json"
const PREVIEW_FIXTURE := "res://.godot/terrain-generation-tests/erosion-v4-preview-recipe.tres"

const LOCATIONS := [
	Vector2(-0.125, 159000.125),
	Vector2(120045.5, 159000.25),
	Vector2(1570796.925, 159000.125),
	Vector2(31415935.875, 317999.875),
]

var _checks := 0
var _failures := PackedStringArray()
var _report := {"checks": 0, "metrics": {}}

func _init() -> void:
	create_timer(90.0).timeout.connect(func(): _failures.append("90 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	_test_identity_and_strict_schema()
	_test_parameter_identity()
	_test_bounded_carve_and_disable()
	_test_periodicity_and_grid_contract()
	_test_fast_path_exactness()
	await _test_isolated_preview_upgrade()
	_finish()

func _test_identity_and_strict_schema() -> void:
	var v4: TerrainGenerationRecipe = RECIPE.create_erosion_default()
	_expect(v4.generator_id == RECIPE.EROSION_GENERATOR_ID, "v4 factory selects erosion generator identity")
	_expect(is_equal_approx(v4.erosion_ridge_preservation, 0.75) and is_equal_approx(v4.gully_normalization, 0.6), "v4 factory uses documented refinement defaults")
	var bridge = BRIDGE.new()
	_expect(bridge.Configure(v4.to_normalized_dictionary()), "v4 default configures")
	if bridge.LastError.is_empty():
		var canonical: String = bridge.CanonicalJson
		var hash: String = bridge.DefinitionHash
		_expect(bridge.ConfigureJson(canonical), "v4 canonical JSON round trips")
		_expect(bridge.CanonicalJson == canonical and bridge.DefinitionHash == hash, "v4 canonical/hash round trip")
		for old_id in [RECIPE.LEGACY_GENERATOR_ID, RECIPE.GENERATOR_ID, RECIPE.DETAIL_GENERATOR_ID]:
			var old_recipe: TerrainGenerationRecipe = RECIPE.create_detail_default() if old_id == RECIPE.DETAIL_GENERATOR_ID else RECIPE.new()
			old_recipe.generator_id = old_id
			var payload := old_recipe.to_normalized_dictionary()
			payload["erosion_ridge_preservation"] = 0.75
			payload["gully_normalization"] = 0.6
			var old_bridge = BRIDGE.new()
			_expect(not old_bridge.Configure(payload), "v4 fields rejected by old identity: %s" % old_id)
			old_bridge.Release()
		for invalid_value in [NAN, -0.001, 1.001]:
			var invalid := v4.to_normalized_dictionary(); invalid["erosion_ridge_preservation"] = invalid_value
			var invalid_bridge = BRIDGE.new()
			_expect(not invalid_bridge.Configure(invalid), "v4 ridge preservation rejects non-finite/out-of-range value")
			invalid_bridge.Release()
		var missing := v4.to_normalized_dictionary(); missing.erase("gully_normalization")
		var missing_bridge = BRIDGE.new()
		_expect(not missing_bridge.Configure(missing), "v4 requires gully normalization field")
		missing_bridge.Release()
	bridge.Release()

func _test_parameter_identity() -> void:
	var recipe: TerrainGenerationRecipe = RECIPE.create_erosion_default()
	var reference = BRIDGE.new()
	_expect(reference.Configure(recipe.to_normalized_dictionary()), "reference v4 configures")
	if not reference.LastError.is_empty():
		reference.Release(); return
	var hash: String = reference.DefinitionHash
	for field in ["erosion_ridge_preservation", "gully_normalization"]:
		var payload := recipe.to_normalized_dictionary()
		payload[field] = 0.25 if field == "erosion_ridge_preservation" else 0.15
		var alternate = BRIDGE.new()
		_expect(alternate.Configure(payload), "v4 refinement field configures: %s" % field)
		if alternate.LastError.is_empty(): _expect(alternate.DefinitionHash != hash, "v4 refinement field changes definition hash: %s" % field)
		alternate.Release()
	reference.Release()
	var points := _active_erosion_points(24)
	_expect(not points.is_empty(), "v4 scan finds active erosion samples for parameter-effect coverage")
	if points.is_empty(): return
	for field in ["gully_normalization", "erosion_ridge_preservation"]:
		var low: TerrainGenerationRecipe = RECIPE.create_erosion_default(); low.set(field, 0.0)
		var high: TerrainGenerationRecipe = RECIPE.create_erosion_default(); high.set(field, 1.0)
		var low_bridge = _configured(low, "v4 %s low" % field)
		var high_bridge = _configured(high, "v4 %s high" % field)
		if low_bridge == null or high_bridge == null:
			if low_bridge != null: low_bridge.Release()
			if high_bridge != null: high_bridge.Release()
			continue
		var difference := 0.0
		for point in points: difference += absf(float(low_bridge.SampleDiagnostics(point.x, point.y).erosion_height) - float(high_bridge.SampleDiagnostics(point.x, point.y).erosion_height))
		_expect(difference > 0.0001, "v4 %s changes active erosion output" % field)
		_report.metrics[field + "_active_erosion_abs_difference"] = difference
		low_bridge.Release(); high_bridge.Release()

func _test_bounded_carve_and_disable() -> void:
	var recipe: TerrainGenerationRecipe = RECIPE.create_erosion_default()
	var bridge = _configured(recipe, "bounded v4")
	if bridge == null: return
	var minimum := 0.0
	var maximum := 0.0
	for point in _sample_points(20, 14):
		var erosion := float(bridge.SampleDiagnostics(point.x, point.y).get("erosion_height", NAN))
		minimum = minf(minimum, erosion); maximum = maxf(maximum, erosion)
		_expect(is_finite(erosion) and erosion >= -recipe.erosion_strength_m - 0.001 and erosion <= 0.001, "v4 erosion remains finite and bounded carve")
	_report.metrics["erosion_height_range"] = {"min": minimum, "max": maximum}
	bridge.Release()
	for mutation in [{"erosion_strength_m": 0.0}, {"mountain_detail_mode": 0}]:
		var disabled: TerrainGenerationRecipe = RECIPE.create_erosion_default()
		for key in mutation: disabled.set(key, mutation[key])
		var disabled_bridge = _configured(disabled, "disabled v4 erosion")
		if disabled_bridge == null: continue
		for point in _sample_points(8, 8): _expect(is_zero_approx(float(disabled_bridge.SampleDiagnostics(point.x, point.y).get("erosion_height", NAN))), "strength zero or mode zero disables erosion")
		disabled_bridge.Release()

func _test_periodicity_and_grid_contract() -> void:
	var recipe: TerrainGenerationRecipe = RECIPE.create_erosion_default()
	var bridge = _configured(recipe, "v4 periodicity")
	if bridge == null: return
	for point in LOCATIONS:
		var here: float = bridge.CalculateHeight(point.x, point.y)
		_expect(absf(here - bridge.CalculateHeight(point.x + recipe.circumference_m, point.y)) < 0.001, "v4 continuous positive ring wrap")
		_expect(absf(here - bridge.CalculateHeight(point.x - recipe.circumference_m, point.y)) < 0.001, "v4 continuous negative ring wrap")
	# Overlap across both a normal cell boundary and the ring seam must remain
	# order independent because tile jobs sample independently.
	for origin in [Vector2(2048.0, 159000.0), Vector2(recipe.circumference_m - 16.0, 159000.0)]:
		var left: PackedFloat32Array = bridge.SampleGrid(origin.x - 16.0, origin.y, 17, 17, 4.0)
		var right: PackedFloat32Array = bridge.SampleGrid(origin.x, origin.y, 17, 17, 4.0)
		for row in 17:
			for column in 13: _expect(left[row * 17 + column + 4] == right[row * 17 + column], "v4 neighboring grids share exact float32 overlap")
	bridge.Release()

func _test_fast_path_exactness() -> void:
	# Height-only jobs must preserve the exact diagnostic Height result for every
	# supported identity. Packed bytes compare IEEE-754 values rather than a tolerance.
	for recipe in _all_recipe_identities():
		var bridge = _configured(recipe, "height fast path %s" % recipe.generator_id)
		if bridge == null: continue
		for point in _fast_path_points(recipe):
			var diagnostic := float(bridge.SampleDiagnostics(point.x, point.y).get("height", NAN))
			var direct := float(bridge.CalculateHeight(point.x, point.y))
			_expect(_float64_bits(diagnostic) == _float64_bits(direct), "height fast path is bit-identical to diagnostic Height: %s" % recipe.generator_id)
		bridge.Release()

func _test_isolated_preview_upgrade() -> void:
	# This deliberately uses a .godot fixture, never the user preview recipe.
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(PREVIEW_FIXTURE.get_base_dir()))
	var v3: TerrainGenerationRecipe = RECIPE.create_detail_default()
	v3.seed = 90210; v3.erosion_strength_m = 83.0
	_expect(TerrainGenerationPreviewStore.save_recipe(v3, PREVIEW_FIXTURE).is_empty(), "isolated v3 preview fixture saves")
	var before := FileAccess.get_file_as_bytes(PREVIEW_FIXTURE)
	var preview: TerrainGenerationPreview = PREVIEW_SCENE.instantiate()
	preview.configure_test_fixture(PREVIEW_FIXTURE, 1024.0, 512.0)
	root.add_child(preview)
	for frame in 240:
		if preview.get_preview_diagnostics().ready: break
		await process_frame
	_expect(preview.get_preview_diagnostics().ready, "isolated v3 preview fixture becomes ready before upgrade")
	_expect(preview.get_recipe_snapshot().generator_id == RECIPE.DETAIL_GENERATOR_ID, "isolated preview starts from saved v3 recipe")
	preview.upgrade_to_enhanced_erosion()
	var upgraded: TerrainGenerationRecipe = preview.get_recipe_snapshot()
	_expect(upgraded.generator_id == RECIPE.EROSION_GENERATOR_ID and is_equal_approx(upgraded.erosion_strength_m, 83.0), "preview upgrade preserves v3 settings and selects v4")
	_expect(is_equal_approx(upgraded.erosion_ridge_preservation, .75) and is_equal_approx(upgraded.gully_normalization, .6), "preview upgrade supplies v4 refinement defaults")
	_expect(FileAccess.get_file_as_bytes(PREVIEW_FIXTURE) == before, "preview upgrade remains in memory until explicit save")
	_expect(preview.save_recipe(), "explicit preview v4 save succeeds")
	var loaded := TerrainGenerationPreviewStore.load_recipe(PREVIEW_FIXTURE)
	_expect(loaded.recipe != null and loaded.recipe.generator_id == RECIPE.EROSION_GENERATOR_ID, "explicit preview save reloads v4 identity")
	preview.queue_free()
	await process_frame

func _all_recipe_identities() -> Array:
	var recipes: Array = []
	for seed in [1337, -12345]:
		var v1 := TerrainGenerationRecipe.new(); v1.generator_id = RECIPE.LEGACY_GENERATOR_ID; v1.seed = seed; recipes.append(v1)
		var v2 := TerrainGenerationRecipe.new(); v2.seed = seed; recipes.append(v2)
		for mode in 4:
			var v3 := TerrainGenerationRecipe.create_detail_default(); v3.seed = seed; v3.mountain_detail_mode = mode; recipes.append(v3)
			var v4 := TerrainGenerationRecipe.create_erosion_default(); v4.seed = seed; v4.mountain_detail_mode = mode; recipes.append(v4)
		var disabled := TerrainGenerationRecipe.create_erosion_default(); disabled.seed = seed; disabled.foothill_amplitude_m = 0.0; disabled.spur_amplitude_m = 0.0; disabled.erosion_strength_m = 0.0; recipes.append(disabled)
	return recipes

func _fast_path_points(recipe: TerrainGenerationRecipe) -> Array:
	var points: Array = LOCATIONS.duplicate()
	var bridge = _configured(recipe, "fast path location scan")
	if bridge == null: return points
	for candidate in _sample_points(16, 12):
		var diagnostic: Dictionary = bridge.SampleDiagnostics(candidate.x, candidate.y)
		if float(diagnostic.continentalness) <= 0.0 or float(diagnostic.macro_height) > 2000.0: points.append(candidate)
	bridge.Release()
	return points

func _active_erosion_points(limit: int) -> Array:
	var bridge = _configured(TerrainGenerationRecipe.create_erosion_default(), "active erosion scan")
	if bridge == null: return []
	var points: Array = []
	for point in _sample_points(48, 28):
		if absf(float(bridge.SampleDiagnostics(point.x, point.y).get("erosion_height", 0.0))) > 0.001:
			points.append(point)
			if points.size() >= limit: break
	bridge.Release()
	return points

func _sample_points(columns: int, rows: int) -> Array:
	var points: Array = []
	for z in rows:
		for x in columns: points.append(Vector2(float(x) * 1570796.8 + .125, fposmod(159000.125 + float(z) * 7950.0, TerrainGenerationRecipe.CERTIFIED_WIDTH_M)))
	return points

func _configured(recipe: TerrainGenerationRecipe, label: String):
	var bridge = BRIDGE.new()
	_expect(bridge.Configure(recipe.to_normalized_dictionary()), "%s configures" % label)
	if not bridge.LastError.is_empty(): bridge.Release(); return null
	return bridge

func _float64_bits(value: float) -> PackedByteArray:
	return PackedFloat64Array([value]).to_byte_array()

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition and _failures.size() < 64: _failures.append(label)

func _finish() -> void:
	if _report.get("finished", false): return
	_report["finished"] = true; _report["checks"] = _checks; _report["failures"] = _failures; _report["status"] = "PASS" if _failures.is_empty() else "FAIL"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REPORT_PATH.get_base_dir()))
	var file := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if file: file.store_string(JSON.stringify(_report, "\t") + "\n"); file.close()
	if _failures.is_empty(): print("TERRAIN_GENERATION_EROSION_V4=PASS checks=", _checks); quit(0)
	else: push_error("TERRAIN_GENERATION_EROSION_V4=FAIL ", "; ".join(_failures)); quit(1)
