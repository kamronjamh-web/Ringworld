extends SceneTree

const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const REFERENCE := preload("res://tools/fixtures/terrain_height_model_reference.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const REPORT := "res://.godot/terrain-generation-tests/shared-math-report.json"

var _checks := 0
var _failures := PackedStringArray()
var _maximum_error := 0.0

func _init() -> void:
	create_timer(90.0).timeout.connect(func(): push_error("Shared math test timed out"); quit(1))
	_run.call_deferred()

func _run() -> void:
	for variant in 6:
		var recipe = RECIPE.new()
		recipe.generator_id = RECIPE.LEGACY_GENERATOR_ID
		match variant:
			1:
				recipe.seed = -2147483649
				recipe.land_balance = -0.35
			2:
				recipe.seed = 9223372036854775000
				recipe.land_balance = 0.35
				recipe.flattening = 0.000001
			3:
				recipe.basin_depth_m = 1250.0
				recipe.trench_depth_m = 6750.0
				recipe.trench_threshold = 1.0
				recipe.valley_width = 1.0
			4:
				recipe.basin_depth_m = 8000.0
				recipe.mountain_amplitude_m = 8000.0
				recipe.noise_lacunarity = 1.0
				recipe.flattening = 8.0
			5:
				recipe.continental_scale_m = 16.0
				recipe.mountain_scale_m = 16.0
				recipe.ridge_scale_m = 16.0
				recipe.warp_amplitude_m = 50000.0
		var reference = REFERENCE.new(recipe)
		var bridge = BRIDGE.new()
		_expect(bridge.Configure(recipe.to_normalized_dictionary()), "normalized recipe accepted: %d: %s" % [variant, bridge.LastError])
		if not bridge.LastError.is_empty():
			bridge.Release()
			continue
		var identity: String = bridge.CanonicalJson
		var hash: String = bridge.DefinitionHash
		_expect(not identity.is_empty() and not hash.is_empty() and hash != "0", "nonzero complete identity")
		for index in 1024:
			var around := float(index) * 49781.375 - 31415936.0 + 0.125
			var across := fposmod(float(index) * 7759.625, 318000.0) + 0.125
			var expected: Dictionary = reference.sample_diagnostics(around, across)
			var actual: Dictionary = bridge.SampleDiagnostics(around, across)
			for key in expected:
				if key == "clamped":
					_expect(actual[key] == expected[key], "guard parity recipe %d sample %d" % [variant, index])
					continue
				var error := absf(float(actual[key]) - float(expected[key]))
				_maximum_error = maxf(_maximum_error, error)
				_expect(is_finite(float(actual[key])) and error < 0.001, "%s parity recipe %d sample %d: %.9f" % [key, variant, index, error])
		for around in [-0.125, 0.0, 31415935.875, 31415936.125]:
			_expect(absf(bridge.CalculateHeight(around, 159000.375) - reference.calculate_1to1_height(around, 159000.375)) < 0.001, "fractional seam parity")
		var grid: PackedFloat32Array = bridge.SampleGrid(-4.0, 158998.0, 7, 5, 2.0)
		var expected_grid: PackedFloat32Array = reference.sample_grid(-4.0, 158998.0, 7, 5, 2.0)
		_expect(grid == expected_grid, "float32 grid is bit-identical to reference")
		var before: float = bridge.CalculateHeight(1234.5, 54321.125)
		var bad: Dictionary = recipe.to_normalized_dictionary()
		bad["future_field"] = 1
		_expect(not bridge.Configure(bad), "unknown saved fields rejected")
		_expect(bridge.CanonicalJson == identity and bridge.DefinitionHash == hash and bridge.CalculateHeight(1234.5, 54321.125) == before, "failed configure preserves complete live definition")
		bridge.Release()
	var report := {"status": "PASS" if _failures.is_empty() else "FAIL", "checks": _checks, "maximum_error_m": _maximum_error, "failures": _failures}
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REPORT.get_base_dir()))
	var file := FileAccess.open(REPORT, FileAccess.WRITE)
	if file:
		file.store_string(JSON.stringify(report, "\t"))
		file.close()
	print("TERRAIN_GENERATION_SHARED_MATH=", report.status, " checks=", _checks, " maximum_error=", _maximum_error)
	if not _failures.is_empty(): push_error("; ".join(_failures.slice(0, 12)))
	quit(0 if _failures.is_empty() else 1)

func _expect(condition: bool, description: String) -> void:
	_checks += 1
	if not condition and _failures.size() < 64:
		_failures.append(description)
