extends SceneTree

const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const GOLDEN_PATH := "res://tools/fixtures/terrain_detail_legacy_golden.json"
const REPORT_PATH := "res://.godot/terrain-generation-tests/detail-report.json"
const DETAIL_FIELDS := ["foothill_amplitude_m", "foothill_scale_m", "spur_amplitude_m", "spur_scale_m", "mountain_detail_mode", "erosion_strength_m", "erosion_scale_m", "erosion_octaves", "swiss_warp"]

var _checks := 0
var _failures := PackedStringArray()
var _report := {"locations": {}, "timings_ms": {}, "metrics": {}}

func _init() -> void:
	create_timer(90.0).timeout.connect(func(): _failures.append("90 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	_test_recipe_contract()
	_test_legacy_golden()
	_test_v3_identity_and_rejection()
	_test_sanitizer()
	_test_sampling_contract()
	_test_detail_semantics_and_timing()
	_finish()

func _test_recipe_contract() -> void:
	var v2: TerrainGenerationRecipe = RECIPE.new()
	var v3: TerrainGenerationRecipe = RECIPE.create_detail_default()
	_expect(v2.generator_id == RECIPE.GENERATOR_ID, "Recipe.new remains v2")
	_expect(v3.generator_id == RECIPE.DETAIL_GENERATOR_ID, "create_detail_default creates v3")
	_expect(v3.to_normalized_dictionary().get("mountain_detail_mode", -1) == 2, "v3 defaults include detail mode")
	_expect(not v2.to_normalized_dictionary().has("foothill_amplitude_m"), "v2 normalized payload excludes v3 fields")

func _test_legacy_golden() -> void:
	var file := FileAccess.open(GOLDEN_PATH, FileAccess.READ)
	_expect(file != null, "legacy golden fixture readable")
	if file == null: return
	var golden = JSON.parse_string(file.get_as_text())
	file.close()
	_expect(golden is Array and golden.size() == 4, "four captured v1/v2 baseline cases")
	if not golden is Array: return
	for entry in golden:
		var recipe: TerrainGenerationRecipe = RECIPE.new()
		recipe.generator_id = entry.generator_id
		recipe.seed = int(entry.seed)
		var bridge = BRIDGE.new()
		_expect(bridge.Configure(recipe.to_normalized_dictionary()), "legacy baseline configures: %s/%s" % [entry.generator_id, entry.seed])
		if bridge.LastError.is_empty():
			_expect(bridge.CanonicalJson == entry.canonical_json, "legacy canonical JSON preserved: %s/%s" % [entry.generator_id, entry.seed])
			_expect(bridge.DefinitionHash == entry.definition_hash, "legacy definition hash preserved: %s/%s" % [entry.generator_id, entry.seed])
			for sample in entry.samples:
				_expect(absf(bridge.CalculateHeight(sample.around, sample.across) - float(sample.height)) < 0.001, "legacy sampled height preserved: %.3f, %.3f" % [sample.around, sample.across])
		bridge.Release()

func _test_v3_identity_and_rejection() -> void:
	var recipe: TerrainGenerationRecipe = RECIPE.create_detail_default()
	var bridge = BRIDGE.new()
	_expect(bridge.Configure(recipe.to_normalized_dictionary()), "v3 default configures")
	if not bridge.LastError.is_empty(): return
	var canonical: String = bridge.CanonicalJson
	var hash: String = bridge.DefinitionHash
	var before: float = bridge.CalculateHeight(1234.5, 98765.25)
	_expect(bridge.ConfigureJson(canonical), "v3 canonical JSON round trips")
	_expect(bridge.CanonicalJson == canonical and bridge.DefinitionHash == hash, "v3 canonical/hash round trip")
	var bad: Dictionary = recipe.to_normalized_dictionary()
	bad["unknown_detail_field"] = 1
	_expect(not bridge.Configure(bad), "unknown v3 field rejected")
	_expect(bridge.CanonicalJson == canonical and bridge.DefinitionHash == hash and bridge.CalculateHeight(1234.5, 98765.25) == before, "unknown field preserves last good model")
	bad = recipe.to_normalized_dictionary()
	bad.erase("erosion_scale_m")
	_expect(not bridge.Configure(bad), "missing v3 field rejected")
	_expect(bridge.CanonicalJson == canonical and bridge.DefinitionHash == hash, "missing field preserves last good model")
	bad = recipe.to_normalized_dictionary()
	bad["mountain_detail_mode"] = 4
	_expect(not bridge.Configure(bad), "invalid v3 field rejected")
	_expect(bridge.CanonicalJson == canonical and bridge.DefinitionHash == hash, "invalid field preserves last good model")
	for field in DETAIL_FIELDS:
		var changed: Dictionary = recipe.to_normalized_dictionary()
		match field:
			"foothill_amplitude_m", "spur_amplitude_m", "erosion_strength_m": changed[field] = float(changed[field]) + 1.0
			"foothill_scale_m", "spur_scale_m", "erosion_scale_m": changed[field] = float(changed[field]) + 1.0
			"mountain_detail_mode": changed[field] = 3
			"erosion_octaves": changed[field] = 2
			"swiss_warp": changed[field] = 0.36
		var alternate = BRIDGE.new()
		_expect(alternate.Configure(changed), "hash-sensitive v3 field configures: %s" % field)
		if alternate.LastError.is_empty(): _expect(alternate.DefinitionHash != hash, "v3 field affects hash: %s" % field)
		alternate.Release()
	bridge.Release()

func _test_sanitizer() -> void:
	var recipe: TerrainGenerationRecipe = RECIPE.create_detail_default()
	recipe.foothill_amplitude_m = 999.0
	recipe.foothill_scale_m = 1.0
	recipe.spur_amplitude_m = 999.0
	recipe.spur_scale_m = 1.0
	recipe.mountain_detail_mode = 99
	recipe.erosion_strength_m = 999.0
	recipe.erosion_scale_m = 1.0
	recipe.erosion_octaves = 99
	recipe.swiss_warp = 9.0
	recipe.mountain_amplitude_m = 8000.0
	var clean: TerrainGenerationRecipe = recipe.snapshot()
	_expect(clean.foothill_amplitude_m == 240.0 and clean.spur_amplitude_m == 400.0, "detail amplitudes sanitize to resource ranges")
	_expect(clean.foothill_scale_m == 128.0 and clean.spur_scale_m == 128.0 and clean.erosion_scale_m == 64.0, "detail scales sanitize to resource ranges")
	_expect(clean.mountain_detail_mode == 3 and clean.erosion_strength_m == 160.0 and clean.swiss_warp == 1.0, "detail scalar ranges sanitize")
	_expect(clean.mountain_amplitude_m <= clean.max_height_m - 2000.0 - clean.secondary_amplitude_m - clean.micro_amplitude_m - clean.foothill_amplitude_m - clean.spur_amplitude_m, "mountain headroom includes v3 amplitudes")
	_expect(clean.erosion_octaves == 3, "erosion octave sanitizer retains 16 m shortest wavelength")
	var normalized: Dictionary = RECIPE.create_detail_default().to_normalized_dictionary()
	var bridge = BRIDGE.new()
	_expect(bridge.Configure(normalized), "normalized v3 default configures")
	var canonical: String = bridge.CanonicalJson
	_expect(bridge.ConfigureJson(canonical) and bridge.CanonicalJson == canonical, "normalized current default retains canonical parity after strict bridge round trip")
	bridge.Release()

func _test_sampling_contract() -> void:
	for mode in 4:
		for seed in [1337, -12345]:
			var recipe: TerrainGenerationRecipe = RECIPE.create_detail_default()
			recipe.seed = seed
			recipe.mountain_detail_mode = mode
			var bridge = BRIDGE.new()
			_expect(bridge.Configure(recipe.to_normalized_dictionary()), "v3 mode configures: %d/%d" % [mode, seed])
			if not bridge.LastError.is_empty(): continue
			for point in [[-0.125, 0.125], [120045.5, 159000.25], [recipe.circumference_m - 0.125, recipe.width_m - 0.125]]:
				var d: Dictionary = bridge.SampleDiagnostics(point[0], point[1])
				_expect(is_finite(float(d.height)) and d.height >= recipe.min_height_m and d.height <= recipe.max_height_m, "v3 finite bounded diagnostic sample")
				_expect(absf(bridge.CalculateHeight(point[0], point[1]) - bridge.CalculateHeight(point[0] + recipe.circumference_m, point[1])) < 0.01, "v3 periodic positive wrap")
				_expect(absf(bridge.CalculateHeight(point[0], point[1]) - bridge.CalculateHeight(point[0] - recipe.circumference_m, point[1])) < 0.01, "v3 periodic negative wrap")
			var left: PackedFloat32Array = bridge.SampleGrid(-16.0, 159000.0, 17, 17, 4.0)
			var right: PackedFloat32Array = bridge.SampleGrid(0.0, 159000.0, 17, 17, 4.0)
			for row in 17:
				for column in 13: _expect(left[row * 17 + column + 4] == right[row * 17 + column], "neighboring grids share canonical overlap")
			for index in 8:
				_expect(absf(float(left[index]) - bridge.CalculateHeight(-16.0 + (index % 17) * 4.0, 159000.0 + (index / 17) * 4.0)) < 0.001, "grid matches rounded point sample")
			bridge.Release()

func _test_detail_semantics_and_timing() -> void:
	var v2: TerrainGenerationRecipe = RECIPE.new()
	var v3: TerrainGenerationRecipe = RECIPE.create_detail_default()
	var old = BRIDGE.new(); var detail = BRIDGE.new()
	_expect(old.Configure(v2.to_normalized_dictionary()) and detail.Configure(v3.to_normalized_dictionary()), "comparison models configure")
	if not old.LastError.is_empty() or not detail.LastError.is_empty(): return
	var hill := _find(detail, func(d): return float(d.macro_height) < 2000.0 and absf(float(d.foothill_height)) > 0.001)
	_expect(not hill.is_empty(), "coarse scan finds sub-2000 m foothill candidate")
	if not hill.is_empty():
		var old_d: Dictionary = old.SampleDiagnostics(hill.around, hill.across)
		_expect(is_zero_approx(float(old_d.detail_height)), "old detail remains zero at foothill candidate")
		_report.locations["foothill"] = hill
	var mountain := _find(detail, func(d): return float(d.macro_height) > 2000.0 and (absf(float(d.spur_height)) > 0.001 or absf(float(d.erosion_height)) > 0.001))
	_expect(not mountain.is_empty(), "coarse scan finds mountain spur or erosion candidate")
	if not mountain.is_empty(): _report.locations["mountain"] = mountain
	if not hill.is_empty(): _report.metrics["foothill_contributions"] = hill.diagnostics
	if not mountain.is_empty(): _report.metrics["mountain_contributions"] = mountain.diagnostics
	var ocean := _find(detail, func(d): return float(d.continentalness) <= 0.0)
	_expect(not ocean.is_empty(), "coarse scan finds ocean candidate")
	if not ocean.is_empty():
		var d: Dictionary = detail.SampleDiagnostics(ocean.around, ocean.across)
		_expect(is_zero_approx(float(d.foothill_height)) and is_zero_approx(float(d.spur_height)) and is_zero_approx(float(d.erosion_height)), "ocean protects all v3 contributions")
		_expect(absf(float(d.height) - old.CalculateHeight(ocean.around, ocean.across)) < .001, "ocean final height remains v2-compatible")
	for mode in 4:
		var mode_recipe: TerrainGenerationRecipe = RECIPE.create_detail_default(); mode_recipe.mountain_detail_mode = mode
		var mode_bridge = BRIDGE.new(); _expect(mode_bridge.Configure(mode_recipe.to_normalized_dictionary()), "detail mode configures: %d" % mode)
		var mode_point := _find(mode_bridge, func(d): return float(d.macro_height) > 2000.0 and absf(float(d.spur_height)) > .001)
		_expect(not mode_point.is_empty(), "mode has nonzero spur candidate: %d" % mode)
		if not mode_point.is_empty():
			var mode_d: Dictionary = mode_point.diagnostics
			if mode < 2: _expect(is_zero_approx(float(mode_d.erosion_height)), "modes 0/1 disable gullies")
			else: _expect(not is_zero_approx(float(mode_d.erosion_height)), "modes 2/3 enable gullies")
		mode_bridge.Release()
	var valley_recipe: TerrainGenerationRecipe = RECIPE.create_detail_default(); valley_recipe.valley_width = 0.5
	var valley_bridge = BRIDGE.new(); _expect(valley_bridge.Configure(valley_recipe.to_normalized_dictionary()), "valley protection model configures")
	var valley := _find(valley_bridge, func(d): return float(d.valley_weight) == 1.0)
	_expect(not valley.is_empty(), "coarse scan finds fully protected valley")
	if not valley.is_empty():
		var d: Dictionary = valley_bridge.SampleDiagnostics(valley.around, valley.across)
		_expect(is_zero_approx(float(d.foothill_height)) and is_zero_approx(float(d.spur_height)) and is_zero_approx(float(d.erosion_height)), "full valley protects all v3 contributions")
	var micro_recipe: TerrainGenerationRecipe = RECIPE.create_detail_default(); micro_recipe.micro_amplitude_m = 32.0
	var micro_bridge = BRIDGE.new(); _expect(micro_bridge.Configure(micro_recipe.to_normalized_dictionary()), "micro protection model configures")
	if not hill.is_empty():
		var micro_d: Dictionary = micro_bridge.SampleDiagnostics(hill.around, hill.across)
		var base_d: Dictionary = detail.SampleDiagnostics(hill.around, hill.across)
		_expect(absf(float(micro_d.height) - float(base_d.height)) < .001, "micro detail remains zero below detail threshold")
	var benchmark_point := mountain if not mountain.is_empty() else {"around": 120045.5, "across": 159000.25}
	_measure_grid("v2", old, benchmark_point)
	_measure_grid("v3", detail, benchmark_point)
	for point in [hill, mountain]:
		if point.is_empty(): continue
		_expect(absf(detail.CalculateHeight(point.around, point.across) - detail.CalculateHeight(point.around + v3.circumference_m, point.across)) < .01, "featured candidate remains periodic")
	old.Release(); detail.Release(); valley_bridge.Release(); micro_bridge.Release()

func _find(bridge, predicate: Callable) -> Dictionary:
	for z in 40:
		for x in 40:
			var around := float(x) * 785398.4 + 0.125
			var across := fposmod(159000.125 + float(z) * 7950.0, TerrainGenerationRecipe.CERTIFIED_WIDTH_M)
			var d: Dictionary = bridge.SampleDiagnostics(around, across)
			if predicate.call(d): return {"around": around, "across": across, "diagnostics": d}
	return {}

func _measure_grid(name: String, bridge, point: Dictionary) -> void:
	var started := Time.get_ticks_usec()
	var grid: PackedFloat32Array = bridge.SampleGrid(point.around, point.across, 128, 128, 2.0)
	var elapsed := float(Time.get_ticks_usec() - started) / 1000.0
	_report.timings_ms[name + "_128x128"] = elapsed
	var minimum := INF; var maximum := -INF; var rise_squared := 0.0; var rises := 0
	for row in 128:
		for column in 128:
			var value := float(grid[row * 128 + column]); minimum = minf(minimum, value); maximum = maxf(maximum, value)
			if column > 0:
				var rise := value - float(grid[row * 128 + column - 1]); rise_squared += rise * rise; rises += 1
	_report.metrics[name + "_grid"] = {"minimum": minimum, "maximum": maximum, "local_relief": maximum - minimum, "rms_neighbor_slope": sqrt(rise_squared / max(1, rises)) / 2.0}
	_expect(grid.size() == 16384, "%s timing grid completed" % name)

func _expect(condition: bool, description: String) -> void:
	_checks += 1
	if not condition and _failures.size() < 64: _failures.append(description)

func _finish() -> void:
	if _report.get("finished", false): return
	_report["finished"] = true
	_report["status"] = "PASS" if _failures.is_empty() else "FAIL"
	_report["checks"] = _checks
	_report["failures"] = _failures
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REPORT_PATH.get_base_dir()))
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output: output.store_string(JSON.stringify(_report, "\t") + "\n"); output.close()
	if _failures.is_empty(): print("TERRAIN_GENERATION_DETAIL=PASS checks=", _checks); quit(0)
	else: push_error("TERRAIN_GENERATION_DETAIL=FAIL ", "; ".join(_failures)); quit(1)
