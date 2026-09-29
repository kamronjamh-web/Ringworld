extends SceneTree

## Frozen CPU-output parity harness. Capture this fixture only from a known-good
## build; normal runs never update it.
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const GOLDEN_PATH := "res://tools/fixtures/terrain_detail_v3_cpu_golden.json"
const REPORT_PATH := "res://.godot/terrain-generation-tests/cpu-parity-report.json"
const GRID_COLUMNS := 17
const GRID_ROWS := 17
const GRID_SPACING_M := 4.0
const FULL_GRID_COLUMNS := 128
const FULL_GRID_ROWS := 128
const FULL_GRID_SPACING_M := 2.0
const DIAGNOSTIC_FIELDS = ["height", "macro_height", "continentalness", "sea_weight", "lowland_weight", "alpine_weight", "detail_mask", "valley_weight", "ridge_weight", "mountain_uplift", "trench_depth", "detail_height", "foothill_height", "spur_height", "erosion_height"]

var _capture := false
var _checks := 0
var _failures := PackedStringArray()
var _report := {"status": "RUNNING", "checks": 0, "failures": [], "mismatch_keys": []}
var _mismatch_keys := PackedStringArray()

func _init() -> void:
	create_timer(45.0).timeout.connect(func(): _failures.append("45 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	_capture = OS.get_cmdline_user_args().has("--capture")
	if _capture and FileAccess.file_exists(GOLDEN_PATH):
		_failures.append("Refusing to overwrite existing golden fixture: %s" % GOLDEN_PATH)
		_finish()
		return
	var captured := _capture_fixture() if _capture else {}
	if _capture:
		if _failures.is_empty(): _write_fixture(captured)
		_finish()
		return
	var golden := _read_golden()
	if golden.is_empty():
		_finish()
		return
	_validate_fixture(golden)
	_finish()

func _capture_fixture() -> Dictionary:
	var cases := []
	for descriptor in _case_descriptors():
		var result := _capture_case(descriptor)
		if not result.is_empty(): cases.append(result)
	return {
		"format": "terrain_generation_cpu_parity_v1",
		"capture": {"godot_version": Engine.get_version_info().get("string", "unknown"), "engine_hash": Engine.get_version_info().get("hash", "unknown"), "os": OS.get_name(), "processor": OS.get_processor_name(), "note": "Captured from the pre-optimization CPU terrain sampler."},
		"grid": {"columns": GRID_COLUMNS, "rows": GRID_ROWS, "spacing_m": GRID_SPACING_M, "full_columns": FULL_GRID_COLUMNS, "full_rows": FULL_GRID_ROWS, "full_spacing_m": FULL_GRID_SPACING_M, "encoding": "PackedFloat32Array.to_byte_array SHA-256"},
		"diagnostics": {"fields": DIAGNOSTIC_FIELDS, "encoding": "PackedFloat64Array.to_byte_array hex and SHA-256; field order is declared above"},
		"cases": cases
	}

func _case_descriptors() -> Array:
	var descriptors: Array = []
	for seed in [1337, -913, 4294967401]:
		for mode in 4:
			descriptors.append({"name": "v3_mode_%d_seed_%s" % [mode, str(seed)], "seed": seed, "mode": mode, "variant": "default"})
	descriptors.append({"name": "v3_lacunarity_1_5_gain_0_65", "seed": 1337, "mode": 3, "variant": "lacunarity_gain"})
	descriptors.append({"name": "v3_zero_warp_noise_gain", "seed": -913, "mode": 1, "variant": "zero_warp_noise_gain"})
	descriptors.append({"name": "v3_erosion_64m_4oct", "seed": 4294967401, "mode": 2, "variant": "erosion_64m_4oct"})
	descriptors.append({"name": "v3_sanitized_max_amplitude_headroom", "seed": 1337, "mode": 3, "variant": "max_headroom"})
	return descriptors

func _recipe_for(descriptor: Dictionary):
	var recipe = RECIPE.create_detail_default()
	recipe.seed = int(descriptor.seed)
	recipe.mountain_detail_mode = int(descriptor.mode)
	match String(descriptor.variant):
		"lacunarity_gain":
			recipe.noise_lacunarity = 1.5
			recipe.noise_gain = 0.65
			recipe.spur_scale_m = 733.25
			recipe.erosion_scale_m = 193.5
			recipe.foothill_scale_m = 923.5
			recipe.flattening = 4.23
		"zero_warp_noise_gain": recipe.warp_amplitude_m = 0.0; recipe.noise_gain = 0.0
		"erosion_64m_4oct": recipe.erosion_scale_m = 64.0; recipe.erosion_octaves = 4
		"max_headroom":
			recipe.mountain_amplitude_m = 8000.0
			recipe.secondary_amplitude_m = 32.0
			recipe.micro_amplitude_m = 32.0
			recipe.foothill_amplitude_m = 240.0
			recipe.spur_amplitude_m = 400.0
			recipe.erosion_octaves = 4
	return recipe.snapshot()

func _locations() -> Array[Dictionary]:
	var circumference := float(RECIPE.CERTIFIED_CIRCUMFERENCE_M)
	return [
		{"name": "ring_seam_negative", "around": -16.0, "across": 159000.125},
		{"name": "ring_seam_wrap", "around": circumference - 16.0, "across": 159000.125},
		{"name": "known_mountain", "around": 1570796.925, "across": 159000.125},
		{"name": "known_foothill", "around": 2356195.325, "across": 159000.125},
		{"name": "known_ocean", "around": 0.125, "across": 159000.125},
		{"name": "across_boundary", "around": 1570796.925, "across": float(RECIPE.CERTIFIED_WIDTH_M) - 16.0}
	]

func _capture_case(descriptor: Dictionary) -> Dictionary:
	var bridge = BRIDGE.new()
	var recipe = _recipe_for(descriptor)
	if not bridge.Configure(recipe.to_normalized_dictionary()):
		_failures.append("%s configure failed: %s" % [descriptor.name, bridge.LastError]); bridge.Release(); return {}
	var result := {"name": descriptor.name, "canonical_json": bridge.CanonicalJson, "definition_hash": bridge.DefinitionHash, "locations": {}, "full_grid": {}}
	for location in _locations():
		result.locations[location.name] = _grid_entry(bridge, location)
	var default_seed_mode := String(descriptor.variant) == "default" and int(descriptor.seed) == 1337
	if default_seed_mode:
		var mountain := _locations()[2]
		result.full_grid = _grid_hash(bridge.SampleGrid(mountain.around, mountain.across, FULL_GRID_COLUMNS, FULL_GRID_ROWS, FULL_GRID_SPACING_M), FULL_GRID_COLUMNS * FULL_GRID_ROWS, "%s/full128" % descriptor.name)
		result.full_grid["location"] = mountain.name
		result.full_grid["columns"] = FULL_GRID_COLUMNS
		result.full_grid["rows"] = FULL_GRID_ROWS
		result.full_grid["spacing_m"] = FULL_GRID_SPACING_M
	bridge.Release()
	return result

func _grid_entry(bridge, location: Dictionary) -> Dictionary:
	var grid: PackedFloat32Array = bridge.SampleGrid(location.around, location.across, GRID_COLUMNS, GRID_ROWS, GRID_SPACING_M)
	var entry := _grid_hash(grid, GRID_COLUMNS * GRID_ROWS, "%s/grid" % location.name)
	entry["columns"] = GRID_COLUMNS; entry["rows"] = GRID_ROWS; entry["spacing_m"] = GRID_SPACING_M
	entry["diagnostics"] = _diagnostics_entry(bridge, location.around, location.across)
	return entry

func _grid_hash(grid: PackedFloat32Array, expected_count: int, key: String) -> Dictionary:
	_expect(grid.size() == expected_count, "%s/sample_count" % key)
	for value in grid:
		_expect(is_finite(float(value)), "%s/finite" % key)
	return {"sha256": _sha256(grid.to_byte_array()), "sample_count": grid.size()}

func _diagnostics_entry(bridge, around: float, across: float) -> Dictionary:
	var diagnostic: Dictionary = bridge.SampleDiagnostics(around, across)
	var values := PackedFloat64Array()
	for field in DIAGNOSTIC_FIELDS: values.append(float(diagnostic[field]))
	var bytes := values.to_byte_array()
	return {"f64_le_hex": bytes.hex_encode(), "sha256": _sha256(bytes), "clamped": bool(diagnostic.clamped)}

func _validate_fixture(golden: Dictionary) -> void:
	_expect(golden.get("format", "") == "terrain_generation_cpu_parity_v1", "fixture format")
	var expected_cases: Array = golden.get("cases", [])
	_expect(expected_cases.size() == _case_descriptors().size(), "fixture case count")
	var expected_by_name := {}
	for item in expected_cases: expected_by_name[item.get("name", "")] = item
	for descriptor in _case_descriptors():
		var name := String(descriptor.name)
		if not expected_by_name.has(name): _mismatch(name + "/missing_case"); continue
		_validate_case(descriptor, expected_by_name[name])

func _validate_case(descriptor: Dictionary, expected: Dictionary) -> void:
	var name := String(descriptor.name)
	var bridge = BRIDGE.new()
	var recipe = _recipe_for(descriptor)
	if not bridge.Configure(recipe.to_normalized_dictionary()): _mismatch(name + "/configure:" + bridge.LastError); bridge.Release(); return
	_expect_equal(name + "/canonical_json", bridge.CanonicalJson, String(expected.get("canonical_json", "")))
	_expect_equal(name + "/definition_hash", bridge.DefinitionHash, String(expected.get("definition_hash", "")))
	_validate_pass(bridge, expected, false)
	_evict_stencil_cells(bridge)
	_validate_pass(bridge, expected, true)
	var independent = BRIDGE.new()
	if independent.Configure(recipe.to_normalized_dictionary()): _validate_pass(independent, expected, false)
	else: _mismatch(name + "/independent_configure:" + independent.LastError)
	independent.Release(); bridge.Release()

func _validate_pass(bridge, expected: Dictionary, reverse: bool) -> void:
	var entries: Array = _locations()
	if reverse: entries.reverse()
	for location in entries:
		var label := String(expected.name) + "/" + String(location.name) + ("/reverse" if reverse else "")
		var expected_entry: Dictionary = expected.get("locations", {}).get(location.name, {})
		_compare_grid_entry(label, _grid_entry(bridge, location), expected_entry)
	if not reverse and not expected.get("full_grid", {}).is_empty():
		var mountain := _locations()[2]
		_compare_grid_entry(String(expected.name) + "/full128", _grid_hash(bridge.SampleGrid(mountain.around, mountain.across, FULL_GRID_COLUMNS, FULL_GRID_ROWS, FULL_GRID_SPACING_M), FULL_GRID_COLUMNS * FULL_GRID_ROWS, String(expected.name) + "/full128"), expected.full_grid)

func _evict_stencil_cells(bridge) -> void:
	var state := 2463534242
	for index in 97:
		state = int((int(state) * 1664525 + 1013904223) & 0x7fffffff)
		var around := float(state % int(RECIPE.CERTIFIED_CIRCUMFERENCE_M)) + float(index) * 0.37
		state = int((int(state) * 1664525 + 1013904223) & 0x7fffffff)
		var across := float(state % int(RECIPE.CERTIFIED_WIDTH_M)) + 0.19
		bridge.CalculateHeight(around, across)

func _compare_grid_entry(key: String, actual: Dictionary, expected: Dictionary) -> void:
	_expect_equal(key + "/grid_sha256", String(actual.get("sha256", "")), String(expected.get("sha256", "")))
	if actual.has("diagnostics"):
		_expect_equal(key + "/diagnostics_sha256", String(actual.diagnostics.get("sha256", "")), String(expected.get("diagnostics", {}).get("sha256", "")))
		_expect_equal(key + "/diagnostics_f64_le_hex", String(actual.diagnostics.get("f64_le_hex", "")), String(expected.get("diagnostics", {}).get("f64_le_hex", "")))
		_expect(bool(actual.diagnostics.get("clamped", false)) == bool(expected.get("diagnostics", {}).get("clamped", false)), key + "/clamped")

func _read_golden() -> Dictionary:
	var file := FileAccess.open(GOLDEN_PATH, FileAccess.READ)
	if file == null: _failures.append("Golden fixture missing: %s. Run this script once with --capture against the approved pre-optimization build." % GOLDEN_PATH); return {}
	var parsed = JSON.parse_string(file.get_as_text()); file.close()
	if not parsed is Dictionary: _failures.append("Golden fixture is not a JSON object"); return {}
	return parsed

func _write_fixture(fixture: Dictionary) -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(GOLDEN_PATH.get_base_dir()))
	var file := FileAccess.open(GOLDEN_PATH, FileAccess.WRITE)
	if file == null: _failures.append("Could not create golden fixture: %s" % GOLDEN_PATH); return
	file.store_string(JSON.stringify(fixture, "\t") + "\n"); file.close()

func _sha256(bytes: PackedByteArray) -> String:
	var hashing := HashingContext.new()
	if hashing.start(HashingContext.HASH_SHA256) != OK: return ""
	hashing.update(bytes)
	return hashing.finish().hex_encode()

func _expect_equal(key: String, actual: String, expected: String) -> void:
	_expect(actual == expected, key)

func _expect(condition: bool, key: String) -> void:
	_checks += 1
	if not condition: _mismatch(key)

func _mismatch(key: String) -> void:
	if _mismatch_keys.size() < 128: _mismatch_keys.append(key)
	if _failures.size() < 128: _failures.append(key)

func _finish() -> void:
	if _report.get("finished", false): return
	_report["finished"] = true; _report["checks"] = _checks; _report["failures"] = _failures; _report["mismatch_keys"] = _mismatch_keys
	_report["status"] = "PASS" if _failures.is_empty() else "FAIL"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REPORT_PATH.get_base_dir()))
	var report_file := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if report_file: report_file.store_string(JSON.stringify(_report, "\t") + "\n"); report_file.close()
	if _failures.is_empty(): print("TERRAIN_GENERATION_CPU_PARITY=PASS checks=", _checks, " report=", REPORT_PATH); quit(0)
	else: push_error("TERRAIN_GENERATION_CPU_PARITY=FAIL ", "; ".join(_failures)); quit(1)
