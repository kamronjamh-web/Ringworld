extends RefCounted

## Test-only frozen GDScript reference. Runtime code uses TerrainGenerationSampler.cs.
const TerrainGenerationRecipeReference = preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const CanonicalNoiseDomainReference = preload("res://scripts/Terrain/Generation/canonical_noise_domain.gd")

const CONTINENTAL_KNOTS := [-1.0, -0.55, -0.20, -0.06, 0.0, 0.45, 1.0]

var _recipe: TerrainGenerationRecipeReference
var _domain: CanonicalNoiseDomainReference
var _continental: FastNoiseLite
var _mountain_belt: FastNoiseLite
var _valley: FastNoiseLite
var _trench: FastNoiseLite
var _ridge: FastNoiseLite
var _secondary: FastNoiseLite
var _micro: FastNoiseLite
var _warp_x: FastNoiseLite
var _warp_z: FastNoiseLite
var _continental_heights: PackedFloat64Array

func _init(recipe: TerrainGenerationRecipeReference = null) -> void:
	var source: TerrainGenerationRecipeReference = recipe if recipe != null else TerrainGenerationRecipeReference.new()
	var errors := source.validation_errors()
	assert(errors.is_empty(), "Terrain recipe identity/domain is incompatible: %s" % "; ".join(errors))
	_recipe = source.snapshot()
	_domain = CanonicalNoiseDomainReference.new(_recipe.circumference_m)
	_continental_heights = PackedFloat64Array([-_recipe.basin_depth_m, -0.80 * _recipe.basin_depth_m, -1000.0, -100.0, 0.0, 1500.0, 2000.0])
	_continental = _make_noise(101, _recipe.continental_scale_m, _recipe.continental_octaves, FastNoiseLite.FRACTAL_FBM)
	_mountain_belt = _make_noise(211, _recipe.mountain_scale_m, _recipe.mountain_octaves, FastNoiseLite.FRACTAL_FBM)
	_valley = _make_noise(307, maxf(TerrainGenerationRecipeReference.MIN_GEOMETRIC_WAVELENGTH_M, _recipe.mountain_scale_m * 0.45), _recipe.valley_octaves, FastNoiseLite.FRACTAL_FBM)
	_trench = _make_noise(401, maxf(TerrainGenerationRecipeReference.MIN_GEOMETRIC_WAVELENGTH_M, _recipe.continental_scale_m * 0.35), _recipe.trench_octaves, FastNoiseLite.FRACTAL_FBM)
	_ridge = _make_noise(503, _recipe.ridge_scale_m, _recipe.ridge_octaves, FastNoiseLite.FRACTAL_RIDGED)
	_secondary = _make_noise(601, _recipe.secondary_scale_m, _recipe.secondary_octaves, FastNoiseLite.FRACTAL_FBM)
	_micro = _make_noise(701, _recipe.micro_scale_m, _recipe.micro_octaves, FastNoiseLite.FRACTAL_FBM)
	_warp_x = _make_noise(809, _recipe.warp_scale_m, _recipe.warp_octaves, FastNoiseLite.FRACTAL_FBM)
	_warp_z = _make_noise(907, _recipe.warp_scale_m, _recipe.warp_octaves, FastNoiseLite.FRACTAL_FBM)

func calculate_1to1_height(x: float, z: float) -> float:
	return float(sample_diagnostics(x, z)["height"])

func sample_diagnostics(x: float, z: float) -> Dictionary:
	var warped := _warped_coordinates(x, z)
	var continental := clampf(_domain.sample_noise(_continental, warped[0], warped[1]) + _recipe.land_balance, -1.0, 1.0)
	var macro_height := continental_height(continental)
	var land_weight := smoothstep(0.0, 0.12, continental)
	var belt := smoothstep(0.4, 0.8, _unit(_domain.sample_noise(_mountain_belt, warped[0], warped[1])))
	var alpine_weight := land_weight * belt
	var sea_weight := 1.0 - land_weight
	var lowland_weight := land_weight * (1.0 - belt)
	var valley_weight := _valley_weight(_domain.sample_noise(_valley, warped[0], warped[1]))
	# FRACTAL_RIDGED is normalized once; do not apply another abs/triangle ridge transform.
	var ridge_weight := pow(_unit(_domain.sample_noise(_ridge, warped[0], warped[1])), _recipe.ridge_sharpness)
	var mountain_uplift := _recipe.mountain_amplitude_m * land_weight * belt * (0.30 + 0.70 * ridge_weight) * (1.0 - 0.70 * valley_weight)
	var trench_weight := smoothstep(_recipe.trench_threshold, 1.0, _unit(_domain.sample_noise(_trench, warped[0], warped[1])))
	var deep_ocean_weight := 1.0 - smoothstep(-3000.0, -1000.0, macro_height)
	var trench_depth := _recipe.trench_depth_m * trench_weight * deep_ocean_weight
	macro_height += mountain_uplift - trench_depth
	var crest_weight := ridge_weight
	var detail_mask := detail_mask_for(macro_height, alpine_weight, crest_weight, valley_weight, _recipe.detail_start_m, _recipe.detail_full_m)
	var detail_height := 0.0
	if detail_mask > 0.0:
		detail_height = detail_mask * (_recipe.secondary_amplitude_m * _domain.sample_noise(_secondary, warped[0], warped[1]) + _recipe.micro_amplitude_m * _domain.sample_noise(_micro, warped[0], warped[1]))
	var raw_height := macro_height + detail_height
	var height := clampf(raw_height, _recipe.min_height_m, _recipe.max_height_m)
	return {
		"height": height, "macro_height": macro_height, "continentalness": continental,
		"sea_weight": sea_weight, "lowland_weight": lowland_weight, "alpine_weight": alpine_weight,
		"detail_mask": detail_mask, "valley_weight": valley_weight, "ridge_weight": ridge_weight,
		"mountain_uplift": mountain_uplift, "trench_depth": trench_depth,
		"detail_height": detail_height, "clamped": raw_height != height
	}

func sample_grid(origin_around: float, origin_across: float, columns: int, rows: int, spacing_m: float) -> PackedFloat32Array:
	assert(columns >= 0 and rows >= 0 and spacing_m > 0.0)
	var samples := PackedFloat32Array()
	samples.resize(columns * rows)
	for row in rows:
		var across := origin_across + float(row) * spacing_m
		for column in columns:
			samples[row * columns + column] = calculate_1to1_height(origin_around + float(column) * spacing_m, across)
	return samples

static func flatten_unit(u: float, k: float) -> float:
	u = clampf(u, 0.0, 1.0)
	if is_zero_approx(k):
		return u
	return (exp(k * u) - 1.0) / (exp(k) - 1.0)

func continental_height(continentalness: float) -> float:
	var c := clampf(continentalness, -1.0, 1.0)
	for index in range(CONTINENTAL_KNOTS.size() - 1):
		if c <= CONTINENTAL_KNOTS[index + 1]:
			var u: float = (c - float(CONTINENTAL_KNOTS[index])) / (float(CONTINENTAL_KNOTS[index + 1]) - float(CONTINENTAL_KNOTS[index]))
			var eased_u: float = smoothstep(0.0, 1.0, u)
			if _continental_heights[index] >= 0.0:
				if is_zero_approx(_recipe.flattening):
					return lerpf(_continental_heights[index], _continental_heights[index + 1], eased_u)
				# Flatten within this positive interval, preserving every transfer knot.
				return lerpf(_continental_heights[index], _continental_heights[index + 1], (exp(_recipe.flattening * eased_u) - 1.0) / _recipe.flatten_denominator)
			return lerpf(_continental_heights[index], _continental_heights[index + 1], eased_u)
	return 2000.0

static func detail_mask_for(macro_height: float, alpine_weight: float, crest_weight: float, valley_weight: float, start_m: float, full_m: float) -> float:
	if alpine_weight <= 0.0 or crest_weight <= 0.0 or valley_weight >= 1.0:
		return 0.0
	return clampf(alpine_weight, 0.0, 1.0) * smoothstep(start_m, maxf(start_m, full_m), macro_height) * clampf(crest_weight, 0.0, 1.0) * (1.0 - clampf(valley_weight, 0.0, 1.0))

func _warped_coordinates(x: float, z: float) -> PackedFloat64Array:
	var around_offset := _domain.sample_noise(_warp_x, x, z) * _recipe.warp_amplitude_m
	var across_offset := _domain.sample_noise(_warp_z, x, z) * _recipe.warp_amplitude_m
	return PackedFloat64Array([_domain.wrap_around(x + around_offset), z + across_offset])

func _make_noise(seed_offset: int, scale_m: float, octaves: int, fractal: FastNoiseLite.FractalType) -> FastNoiseLite:
	var noise := FastNoiseLite.new()
	noise.seed = _recipe.seed + seed_offset
	noise.noise_type = FastNoiseLite.TYPE_SIMPLEX_SMOOTH
	noise.frequency = 1.0 / scale_m
	noise.fractal_type = fractal
	noise.fractal_octaves = octaves
	noise.fractal_gain = _recipe.noise_gain
	noise.fractal_lacunarity = _recipe.noise_lacunarity
	return noise

static func _unit(value: float) -> float:
	return clampf(value * 0.5 + 0.5, 0.0, 1.0)

func _valley_weight(value: float) -> float:
	var distance_from_corridor := absf(value)
	return 1.0 - smoothstep(_recipe.valley_width, minf(1.0, _recipe.valley_width * 2.0), distance_from_corridor)
