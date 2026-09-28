extends SceneTree

const OUTPUT_DIRECTORY := "res://.godot/terrain-generation-tests"
const REPORT_PATH := OUTPUT_DIRECTORY + "/reference-math-report.json"
const MINIMUM_EXPECTED_CHECKS := 10000

var _failures := PackedStringArray()
var _checks := 0
var _stats := {"samples": 0, "minimum_height": INF, "maximum_height": -INF, "guard_clamps": 0, "ocean_samples": 0, "detail_nonzero_samples": 0, "trench_samples": 0, "mean_simple_gradient_rise_per_run": 0.0, "maximum_simple_gradient_rise_per_run": 0.0}
var _run_completed := false

func _init() -> void:
	_run()
	if not _run_completed:
		_failures.append("reference run did not complete")
	if _checks < MINIMUM_EXPECTED_CHECKS:
		_failures.append("reference run completed too few checks: %d" % _checks)
	_write_report()
	if _run_completed and _failures.is_empty():
		print("TERRAIN_GENERATION_REFERENCE=PASS ", JSON.stringify({"checks": _checks}))
		quit(0)
	else:
		push_error("TERRAIN_GENERATION_REFERENCE=FAIL " + "; ".join(_failures))
		quit(1)

func _run() -> void:
	var recipe := TerrainGenerationRecipe.new()
	recipe.generator_id = TerrainGenerationRecipe.LEGACY_GENERATOR_ID
	var model := TerrainHeightModel.new(recipe)
	var fresh := TerrainHeightModel.new(recipe)
	var changed_recipe := recipe.snapshot()
	changed_recipe.seed += 1
	var changed := TerrainHeightModel.new(changed_recipe)
	var points := [PackedFloat64Array([-12.75, -300.5]), PackedFloat64Array([0.0, 0.0]), PackedFloat64Array([1200.25, 42.5]), PackedFloat64Array([recipe.circumference_m - 0.25, 500.0])]
	for point in points:
		_expect(point is PackedFloat64Array, "fractional test point preserves float64 coordinates")
		var a := model.calculate_1to1_height(point[0], point[1])
		_expect(is_finite(a), "finite sample")
		_expect(a >= recipe.min_height_m and a <= recipe.max_height_m, "bounded sample")
		_expect(is_equal_approx(a, model.calculate_1to1_height(point[0], point[1])), "repeatable sample")
		_expect(is_equal_approx(a, fresh.calculate_1to1_height(point[0], point[1])), "fresh-model deterministic sample")
		_expect(absf(a - model.calculate_1to1_height(point[0] + recipe.circumference_m, point[1])) < 0.01, "periodic positive wrap with warp")
		_expect(absf(a - model.calculate_1to1_height(point[0] - recipe.circumference_m * 2.0, point[1])) < 0.01, "periodic negative wrap with warp")
	_expect(not is_equal_approx(model.calculate_1to1_height(100.0, 200.0), changed.calculate_1to1_height(100.0, 200.0)), "seed variation")
	var before_mutation := model.calculate_1to1_height(987.5, -432.25)
	recipe.seed = 9
	recipe.mountain_amplitude_m = 0.0
	_expect(is_equal_approx(before_mutation, model.calculate_1to1_height(987.5, -432.25)), "model owns immutable recipe snapshot")
	var reverse := PackedFloat32Array()
	for index in range(points.size() - 1, -1, -1):
		reverse.append(model.calculate_1to1_height(points[index][0], points[index][1]))
	for index in points.size():
		_expect(is_equal_approx(reverse[points.size() - 1 - index], model.calculate_1to1_height(points[index][0], points[index][1])), "sampling order independent")
	var diagnostics := model.sample_diagnostics(1234.5, -200.25)
	var total_weight: float = diagnostics["sea_weight"] + diagnostics["lowland_weight"] + diagnostics["alpine_weight"]
	_expect(absf(total_weight - 1.0) < 0.0001, "normalized zone weights")
	_expect(not diagnostics["clamped"], "default recipe does not routinely final-clamp")
	for across in [0.125, recipe.width_m * 0.5 + 0.375, recipe.width_m - 0.125]:
		var left := model.calculate_1to1_height(-0.125, across)
		var right := model.calculate_1to1_height(recipe.circumference_m - 0.125, across)
		var forward := model.calculate_1to1_height(0.125, across)
		var backward := model.calculate_1to1_height(recipe.circumference_m + 0.125, across)
		_expect(absf(left - right) < 0.01 and absf(forward - backward) < 0.01, "fractional seam continuity with warp")
	_test_distribution(model, recipe)
	_test_domain(recipe)
	_test_grid(model)
	_test_transfer(model)
	_test_masks()
	_test_incompatible_recipe()
	_run_completed = true

func _test_domain(recipe: TerrainGenerationRecipe) -> void:
	var domain := CanonicalNoiseDomain.new(recipe.circumference_m)
	_expect(absf(domain.wrap_around(-0.25) - (recipe.circumference_m - 0.25)) < 0.001, "negative fractional wrap")
	_expect(domain.floor_lattice_index(-0.1, 2.0) == -1, "negative lattice floor")
	_expect(domain.floor_lattice_index(-2.1, 2.0) == -2, "negative lattice floor below cell")
	_expect(domain.region_index(-1, 128) == -1, "negative region floor")
	_expect(domain.region_index(-129, 128) == -2, "negative region floor below pitch")
	_expect(domain.region_index(256, 128) == 2, "positive region pitch")

func _test_grid(model: TerrainHeightModel) -> void:
	var grid := model.sample_grid(11.25, -5.5, 3, 2, 2.5)
	_expect(grid.size() == 6, "grid size")
	for row in 2:
		for column in 3:
			var expected := model.calculate_1to1_height(11.25 + column * 2.5, -5.5 + row * 2.5)
			_expect(absf(grid[row * 3 + column] - expected) < 0.002, "row-major float32 grid")

func _test_distribution(model: TerrainHeightModel, recipe: TerrainGenerationRecipe) -> void:
	for index in 4096:
		var around := (float(index) * 7919.375) + 0.125
		var across := fposmod(float(index) * 3571.875 + 0.375, recipe.width_m)
		var diagnostics := model.sample_diagnostics(around, across)
		var height: float = diagnostics["height"]
		var simple_slope := absf(model.calculate_1to1_height(around + 2.0, across) - height) / 2.0
		_stats["samples"] += 1
		_stats["minimum_height"] = minf(_stats["minimum_height"], height)
		_stats["maximum_height"] = maxf(_stats["maximum_height"], height)
		_stats["mean_simple_gradient_rise_per_run"] += simple_slope
		_stats["maximum_simple_gradient_rise_per_run"] = maxf(_stats["maximum_simple_gradient_rise_per_run"], simple_slope)
		if height < 0.0:
			_stats["ocean_samples"] += 1
		if not is_zero_approx(diagnostics["detail_height"]):
			_stats["detail_nonzero_samples"] += 1
		if diagnostics["trench_depth"] > 0.0:
			_stats["trench_samples"] += 1
		if diagnostics["clamped"]:
			_stats["guard_clamps"] += 1
		_expect(is_finite(height) and height >= recipe.min_height_m and height <= recipe.max_height_m, "distribution finite/bounded")
		_expect(absf((diagnostics["sea_weight"] + diagnostics["lowland_weight"] + diagnostics["alpine_weight"]) - 1.0) < 0.0001, "distribution normalized weights")
		if diagnostics["continentalness"] <= 0.0:
			_expect(is_zero_approx(diagnostics["mountain_uplift"]), "ocean has no mountain uplift")
		if diagnostics["macro_height"] >= -1000.0:
			_expect(is_zero_approx(diagnostics["trench_depth"]), "shallow water/land has no trench")
		if diagnostics["detail_mask"] == 0.0:
			_expect(diagnostics["detail_height"] == 0.0, "zero mask gives exactly zero detail")
		if diagnostics["valley_weight"] == 1.0:
			_expect(diagnostics["detail_mask"] == 0.0 and diagnostics["detail_height"] == 0.0, "fully protected sampled valley has zero detail")
	_stats["mean_simple_gradient_rise_per_run"] /= float(_stats["samples"])
	_expect(_stats["guard_clamps"] == 0, "default distribution uses no final clamps")

func _test_transfer(model: TerrainHeightModel) -> void:
	_expect(is_zero_approx(model.continental_height(0.0)), "continental sea anchor")
	_expect(is_zero_approx(TerrainHeightModel.flatten_unit(0.5, 0.0) - 0.5), "flattening k zero")
	_expect(absf(TerrainHeightModel.flatten_unit(0.5, 3.0) - 0.1824) < 0.001, "flattening reference value")
	for knot in [-1.0, -0.55, -0.20, -0.06, 0.0, 0.45, 1.0]:
		var left := model.continental_height(maxf(-1.0, knot - 0.0001))
		var right := model.continental_height(minf(1.0, knot + 0.0001))
		_expect(absf(left - right) < 1.0, "continental transfer continuity at %s" % knot)
		if knot > -1.0 and knot < 1.0:
			var left_slope := (model.continental_height(knot) - model.continental_height(knot - 0.0001)) / 0.0001
			var right_slope := (model.continental_height(knot + 0.0001) - model.continental_height(knot)) / 0.0001
			_expect(absf(left_slope) < 20.0 and absf(right_slope) < 20.0, "continental transfer smooth derivative at %s" % knot)

func _test_masks() -> void:
	_expect(TerrainHeightModel.detail_mask_for(6000.0, 1.0, 1.0, 1.0, 2000.0, 4500.0) == 0.0, "full valley suppresses detail")
	_expect(TerrainHeightModel.detail_mask_for(6000.0, 0.0, 1.0, 0.0, 2000.0, 4500.0) == 0.0, "no alpine suppresses detail")
	_expect(TerrainHeightModel.detail_mask_for(6000.0, 1.0, 0.0, 0.0, 2000.0, 4500.0) == 0.0, "no crest suppresses detail")
	_expect(TerrainHeightModel.detail_mask_for(1000.0, 1.0, 1.0, 0.0, 2000.0, 4500.0) == 0.0, "low macro suppresses detail")

func _test_incompatible_recipe() -> void:
	var bad_generator := TerrainGenerationRecipe.new()
	bad_generator.generator_id = "future_generator"
	_expect(not bad_generator.validation_errors().is_empty(), "incompatible generator is rejected")
	var bad_domain := TerrainGenerationRecipe.new()
	bad_domain.domain_id = "tiled_domain"
	_expect(not bad_domain.validation_errors().is_empty(), "incompatible domain is rejected")
	var bad_scale := TerrainGenerationRecipe.new()
	bad_scale.circumference_m = 1.0
	_expect(not bad_scale.validation_errors().is_empty(), "non-certified circumference is rejected")
	var invalid_numeric := TerrainGenerationRecipe.new()
	invalid_numeric.basin_depth_m = 8000.0
	invalid_numeric.trench_depth_m = NAN
	invalid_numeric.detail_start_m = 8000.0
	invalid_numeric.detail_full_m = NAN
	var cleaned := invalid_numeric.snapshot()
	_expect(is_finite(cleaned.trench_depth_m) and is_zero_approx(cleaned.trench_depth_m), "NaN trench fallback observes dynamic headroom")
	_expect(is_finite(cleaned.detail_full_m) and cleaned.detail_full_m > cleaned.detail_start_m, "NaN detail endpoint retains nonzero interval")

func _expect(condition: bool, description: String) -> void:
	_checks += 1
	if not condition:
		_failures.append(description)

func _write_report() -> void:
	var report := {"status": "PASS" if _run_completed and _failures.is_empty() else "FAIL", "checks": _checks, "statistics": _stats, "failures": _failures}
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT_DIRECTORY)) != OK:
		_failures.append("could not create isolated report directory")
		return
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output == null:
		_failures.append("could not write isolated report")
		return
	output.store_string(JSON.stringify(report, "\t") + "\n")
	output.close()
