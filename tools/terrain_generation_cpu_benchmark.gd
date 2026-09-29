extends SceneTree

## Headless diagnostic only. It deliberately creates independent, preconfigured
## C# samplers and measures only SampleGrid, never recipe normalization/setup.
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const REPORT_PATH := "res://.godot/terrain-generation-tests/detail-cpu-benchmark-report.json"

var _csharp_build := "unverified"
var _csharp_optimize := "unverified"
var _operator_supplied_build_metadata := false

const SEED := 1337
const COLUMNS := 128
const ROWS := 128
const SPACING_M := 2.0
const WARMUP_ROUNDS := 3
const MEASURED_ROUNDS := 12
const MOUNTAIN_AROUND := 1570796.925
const MOUNTAIN_ACROSS := 159000.125

var _failures := PackedStringArray()
var _models: Array = []
var _locations: Array[Dictionary] = []
var _samples := {}
var _digests := {}
var _report := {}

func _init() -> void:
	create_timer(300.0).timeout.connect(func(): _failures.append("300 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	_read_operator_build_metadata()
	_report = {
		"status": "RUNNING",
		"benchmark": {
			"columns": COLUMNS,
			"rows": ROWS,
			"spacing_m": SPACING_M,
			"samples_per_grid": COLUMNS * ROWS,
			"warmup_rounds": WARMUP_ROUNDS,
			"measured_rounds": MEASURED_ROUNDS,
			"seed": SEED,
			"timed_operation": "TerrainGenerationSampler.SampleGrid only; model Configure occurs before timing"
		},
		"hardware": {
			"processor_name": OS.get_processor_name(),
			"processor_count": OS.get_processor_count(),
			"os_name": OS.get_name(),
			"os_version": OS.get_version()
		},
		"build_context": {
			"csharp_build": _csharp_build,
			"csharp_optimize": _csharp_optimize,
			"operator_supplied": _operator_supplied_build_metadata,
			"note": "Build metadata is operator-supplied and is not detected by this benchmark. Defaults are unverified."
		},
		"models": [],
		"locations": [],
		"results": {}
	}
	_setup_models()
	_setup_locations()
	if _models.is_empty() or _locations.is_empty():
		_finish()
		return
	_run_rounds(WARMUP_ROUNDS, false)
	_run_rounds(MEASURED_ROUNDS, true)
	_summarize_results()
	_finish()

func _read_operator_build_metadata() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--csharp-build="):
			_csharp_build = argument.trim_prefix("--csharp-build=")
			_operator_supplied_build_metadata = true
		elif argument.begins_with("--csharp-optimize="):
			_csharp_optimize = argument.trim_prefix("--csharp-optimize=")
			_operator_supplied_build_metadata = true

func _setup_models() -> void:
	var variants: Array[Dictionary] = []
	var v2: TerrainGenerationRecipe = RECIPE.new()
	v2.seed = SEED
	variants.append({"name": "v2_default", "description": "TerrainGenerationRecipe.new()" , "recipe": v2})

	var v3_baseline: TerrainGenerationRecipe = RECIPE.create_detail_default()
	v3_baseline.seed = SEED
	v3_baseline.foothill_amplitude_m = 0.0
	v3_baseline.spur_amplitude_m = 0.0
	v3_baseline.erosion_strength_m = 0.0
	variants.append({"name": "v3_new_layers_0", "description": "v3 with foothills, spurs, and erosion disabled", "recipe": v3_baseline})

	var foothills: TerrainGenerationRecipe = RECIPE.create_detail_default()
	_foot_hills_only(foothills)
	variants.append({"name": "v3_foothills_only", "description": "v3 default foothills with spurs and erosion disabled", "recipe": foothills})

	for mode in 4:
		var detail: TerrainGenerationRecipe = RECIPE.create_detail_default()
		detail.seed = SEED
		detail.mountain_detail_mode = mode
		variants.append({"name": "v3_mode_%d" % mode, "description": _mode_description(mode), "recipe": detail})

	var erosion_two: TerrainGenerationRecipe = RECIPE.create_detail_default()
	erosion_two.seed = SEED
	erosion_two.mountain_detail_mode = 2
	erosion_two.erosion_octaves = 2
	variants.append({"name": "v3_mode_2_erosion_octaves_2", "description": "v3 directional gullies with erosion_octaves=2", "recipe": erosion_two})

	for variant in variants:
		var recipe: TerrainGenerationRecipe = variant.recipe
		var bridge = BRIDGE.new()
		if not bridge.Configure(recipe.to_normalized_dictionary()):
			_failures.append("Failed to configure %s: %s" % [variant.name, bridge.LastError])
			bridge.Release()
			continue
		var identity := {
			"name": variant.name,
			"description": variant.description,
			"generator_id": recipe.snapshot().generator_id,
			"canonical_json": bridge.CanonicalJson,
			"definition_hash": bridge.DefinitionHash
		}
		_models.append({"name": variant.name, "bridge": bridge, "identity": identity})
		_report.models.append(identity)

func _foot_hills_only(recipe: TerrainGenerationRecipe) -> void:
	recipe.seed = SEED
	recipe.spur_amplitude_m = 0.0
	recipe.erosion_strength_m = 0.0

func _mode_description(mode: int) -> String:
	match mode:
		0: return "v3 ridged spurs"
		1: return "v3 Swiss spurs"
		2: return "v3 directional gullies (default)"
		3: return "v3 Swiss plus gullies"
	return "unknown"

func _setup_locations() -> void:
	_locations.append(_location("mountain_known", MOUNTAIN_AROUND, MOUNTAIN_ACROSS, "requested known mountain coordinate"))
	var locator = BRIDGE.new()
	var locator_recipe: TerrainGenerationRecipe = RECIPE.create_detail_default()
	locator_recipe.seed = SEED
	if not locator.Configure(locator_recipe.to_normalized_dictionary()):
		_failures.append("Failed to configure location locator: %s" % locator.LastError)
		locator.Release()
		return
	var ocean := _find_location(locator, func(d): return float(d.continentalness) <= 0.0)
	if not ocean.is_empty(): _locations.append(_location("ocean", ocean.around, ocean.across, "coarse deterministic ocean candidate"))
	else: _failures.append("No ocean location found by deterministic coarse scan")
	var foothill := _find_location(locator, func(d): return float(d.macro_height) < 2000.0 and absf(float(d.foothill_height)) > 0.001)
	if not foothill.is_empty(): _locations.append(_location("foothill", foothill.around, foothill.across, "coarse deterministic foothill candidate"))
	else: _failures.append("No foothill location found by deterministic coarse scan")
	locator.Release()
	for location in _locations: _report.locations.append(location)

func _location(name: String, around: float, across: float, source: String) -> Dictionary:
	return {"name": name, "origin_around": around, "origin_across": across, "source": source}

func _find_location(bridge, predicate: Callable) -> Dictionary:
	for z in 40:
		for x in 40:
			var around := float(x) * 785398.4 + 0.125
			var across := fposmod(159000.125 + float(z) * 7950.0, TerrainGenerationRecipe.CERTIFIED_WIDTH_M)
			var diagnostics: Dictionary = bridge.SampleDiagnostics(around, across)
			if predicate.call(diagnostics): return {"around": around, "across": across}
	return {}

func _run_rounds(round_count: int, measured: bool) -> void:
	var workloads: Array[Dictionary] = []
	for model in _models:
		for location in _locations:
			workloads.append({"model": model, "location": location})
	for round in round_count:
		for offset in workloads.size():
			var workload: Dictionary = workloads[(round + offset) % workloads.size()]
			_run_grid(workload.model, workload.location, measured)

func _run_grid(model: Dictionary, location: Dictionary, measured: bool) -> void:
	var bridge = model.bridge
	var started := Time.get_ticks_usec()
	var grid: PackedFloat32Array = bridge.SampleGrid(location.origin_around, location.origin_across, COLUMNS, ROWS, SPACING_M)
	var elapsed_ms := float(Time.get_ticks_usec() - started) / 1000.0
	var key := "%s/%s" % [model.name, location.name]
	var digest := _digest_grid(grid)
	if grid.size() != COLUMNS * ROWS:
		_failures.append("%s returned %d values, expected %d" % [key, grid.size(), COLUMNS * ROWS])
	for value in grid:
		if not is_finite(float(value)):
			_failures.append("%s returned a non-finite height" % key)
			break
	if _digests.has(key) and _digests[key] != digest:
		_failures.append("%s produced a non-deterministic grid digest" % key)
	else:
		_digests[key] = digest
	if measured:
		if not _samples.has(key): _samples[key] = []
		_samples[key].append(elapsed_ms)

func _summarize_results() -> void:
	for model in _models:
		for location in _locations:
			var key := "%s/%s" % [model.name, location.name]
			var timings: Array = _samples.get(key, [])
			if timings.size() != MEASURED_ROUNDS:
				_failures.append("%s has %d measured samples, expected %d" % [key, timings.size(), MEASURED_ROUNDS])
			var ordered := timings.duplicate()
			ordered.sort()
			_report.results[key] = {
				"model": model.name,
				"location": location.name,
				"sample_count": ordered.size(),
				"timings_ms": timings,
				"min_ms": _at(ordered, 0),
				"median_ms": _median(ordered),
				"p95_ms": _at(ordered, ceili(float(ordered.size()) * 0.95) - 1),
				"max_ms": _at(ordered, ordered.size() - 1),
				"digest_sha256": _digests.get(key, "")
			}

func _at(values: Array, index: int) -> float:
	if values.is_empty(): return NAN
	return float(values[clampi(index, 0, values.size() - 1)])

func _median(values: Array) -> float:
	if values.is_empty(): return NAN
	var upper := int(values.size() / 2)
	if values.size() % 2 == 1: return _at(values, upper)
	return (_at(values, upper - 1) + _at(values, upper)) * 0.5

func _digest_grid(grid: PackedFloat32Array) -> String:
	var hashing := HashingContext.new()
	if hashing.start(HashingContext.HASH_SHA256) != OK: return ""
	hashing.update(grid.to_byte_array())
	return hashing.finish().hex_encode()

func _finish() -> void:
	if _report.get("finished", false): return
	_report["finished"] = true
	_report["status"] = "PASS" if _failures.is_empty() else "FAIL"
	_report["failures"] = _failures
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REPORT_PATH.get_base_dir()))
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output:
		output.store_string(JSON.stringify(_report, "\t") + "\n")
		output.close()
	else:
		_failures.append("Could not write benchmark report: %s" % REPORT_PATH)
	for model in _models:
		model.bridge.Release()
	if _failures.is_empty():
		print("TERRAIN_DETAIL_CPU_BENCHMARK=PASS report=", REPORT_PATH)
		quit(0)
	else:
		push_error("TERRAIN_DETAIL_CPU_BENCHMARK=FAIL ", "; ".join(_failures))
		quit(1)
