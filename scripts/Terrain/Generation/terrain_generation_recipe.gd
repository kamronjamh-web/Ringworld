class_name TerrainGenerationRecipe
extends Resource

## Immutable snapshots of this resource are the only recipe inputs used by jobs.
const LEGACY_GENERATOR_ID: String = "ringworld_surface_reference_v1"
const GENERATOR_ID: String = "ringworld_surface_coastal_v2"
const DETAIL_GENERATOR_ID: String = "ringworld_surface_detail_v3"
const EROSION_GENERATOR_ID: String = "ringworld_surface_erosion_v4"
## v5: v4 terrain plus deterministic emitter-site shaping (docs/emitter-site-terrain-spec.md section 5). Same fields as v4.
const SITES_GENERATOR_ID: String = "ringworld_surface_sites_v5"
## v6: v5 terrain plus the 200 Wall Engine climate-machine forecourts (docs/emitter-grid-climate-machines-spec.md section C3). Same fields as v4/v5.
const CLIMATE_GENERATOR_ID: String = "ringworld_surface_climate_v6"
## v7: v6 field set plus lowland landforms (docs/lowland-landform-overhaul-plan.md). Climate machines are the opt-in `climate_machines` field.
const LANDFORM_GENERATOR_ID: String = "ringworld_surface_landform_v7"
const DOMAIN_ID: String = "canonical_ring_noise_v1"
const MIN_GEOMETRIC_WAVELENGTH_M: float = 16.0
const CERTIFIED_CIRCUMFERENCE_M: float = 31415936.0
const CERTIFIED_WIDTH_M: float = 318000.0
const CERTIFIED_MIN_HEIGHT_M: float = -8000.0
const CERTIFIED_MAX_HEIGHT_M: float = 8000.0
const MIN_BASIN_DEPTH_M: float = 1250.0

@export var seed: int = 1337
@export var generator_id: String = GENERATOR_ID
@export var domain_id: String = DOMAIN_ID
@export var circumference_m: float = CERTIFIED_CIRCUMFERENCE_M
@export var width_m: float = CERTIFIED_WIDTH_M
@export var min_height_m: float = CERTIFIED_MIN_HEIGHT_M
@export var max_height_m: float = CERTIFIED_MAX_HEIGHT_M

@export_range(-0.35, 0.35, 0.001) var land_balance: float = 0.0
@export_range(16000.0, 1000000.0, 1.0) var continental_scale_m: float = 300000.0
@export_range(16000.0, 500000.0, 1.0) var mountain_scale_m: float = 80000.0
## This lower bound keeps the fixed -1000 m and -100 m transfer knots monotone.
@export_range(MIN_BASIN_DEPTH_M, 8000.0, 1.0) var basin_depth_m: float = 4500.0
@export_range(0.0, 8000.0, 1.0) var trench_depth_m: float = 3000.0
@export_range(0.0, 1.0, 0.001) var trench_threshold: float = 0.80
@export_range(0.0, 8000.0, 1.0) var mountain_amplitude_m: float = 5400.0
@export_range(0.01, 1.0, 0.001) var valley_width: float = 0.18
@export_range(0.0, 8.0, 0.01) var flattening: float = 3.0
@export_range(16.0, 100000.0, 1.0) var ridge_scale_m: float = 8000.0
@export_range(0.1, 4.0, 0.01) var ridge_sharpness: float = 1.5
@export_range(0.0, 240.0, 0.1) var secondary_amplitude_m: float = 60.0
@export_range(0.0, 32.0, 0.1) var micro_amplitude_m: float = 4.0
@export_range(0.0, 8000.0, 1.0) var detail_start_m: float = 2000.0
@export_range(0.0, 8000.0, 1.0) var detail_full_m: float = 4500.0
@export_range(0.0, 50000.0, 1.0) var warp_amplitude_m: float = 6000.0

# Explicit FastNoiseLite configuration. Snapshot sanitization additionally
# limits every octave count so its shortest wavelength remains at least 16 m.
@export var continental_octaves: int = 4
@export var mountain_octaves: int = 4
@export var valley_octaves: int = 3
@export var trench_octaves: int = 3
@export var ridge_octaves: int = 4
@export var secondary_octaves: int = 3
@export var micro_octaves: int = 2
@export var warp_octaves: int = 3
@export var noise_gain: float = 0.5
@export var noise_lacunarity: float = 2.0
@export var secondary_scale_m: float = 256.0
@export var micro_scale_m: float = 32.0
@export var warp_scale_m: float = 180000.0
@export_range(0.005, 0.1, 0.001) var beach_slope: float = 0.025
@export_range(2000.0, 100000.0, 1.0) var shelf_width_m: float = 12000.0
@export_range(50.0, 300.0, 1.0) var shelf_depth_m: float = 150.0
@export_range(5000.0, 100000.0, 1.0) var continental_slope_width_m: float = 30000.0
@export_range(0.0, 240.0, 0.1) var foothill_amplitude_m: float = 0.0
@export_range(128.0, 8192.0, 1.0) var foothill_scale_m: float = 0.0
@export_range(0.0, 400.0, 0.1) var spur_amplitude_m: float = 0.0
@export_range(128.0, 8192.0, 1.0) var spur_scale_m: float = 0.0
## 0: ridges only, 1: Swiss, 2: directional gullies, 3: Swiss plus gullies.
@export_range(0, 3, 1) var mountain_detail_mode: int = 0
@export_range(0.0, 160.0, 0.1) var erosion_strength_m: float = 0.0
@export_range(64.0, 2048.0, 1.0) var erosion_scale_m: float = 0.0
@export_range(1, 4, 1) var erosion_octaves: int = 0
@export_range(0.0, 1.0, 0.01) var swiss_warp: float = 0.0
@export_range(0.0, 1.0, 0.01) var erosion_ridge_preservation: float = 0.75
@export_range(0.0, 1.0, 0.01) var gully_normalization: float = 0.6
# v7 lowland-landform fields. Script defaults are the v7 defaults so an upgraded v4-v6 recipe gets sensible values.
@export_range(0, 1, 1) var climate_machines: int = 0
@export_range(8000.0, 200000.0, 1.0) var landform_scale_m: float = 32000.0
@export_range(0.05, 0.95, 0.001) var plains_threshold: float = 0.445
@export_range(0.06, 0.99, 0.001) var hills_threshold: float = 0.575
@export_range(16000.0, 400000.0, 1.0) var plateau_scale_m: float = 64000.0
@export_range(0.05, 0.95, 0.001) var plateau_threshold: float = 0.58
@export_range(0.06, 0.99, 0.001) var plateau_full_threshold: float = 0.68
@export_range(0.0, 900.0, 1.0) var hill_amplitude_m: float = 480.0
@export_range(1000.0, 16000.0, 1.0) var hill_scale_m: float = 5000.0
@export_range(0.0, 1.0, 0.01) var hill_detail_weight: float = 0.35
@export_range(0.5, 3.0, 0.01) var hill_valley_exponent: float = 1.35
@export_range(0.0, 0.5, 0.01) var plains_hill_fraction: float = 0.08
@export_range(0.0, 1.0, 0.01) var hill_valley_floor: float = 0.6
@export_range(0.0, 900.0, 1.0) var plateau_lift_m: float = 320.0
@export_range(20.0, 600.0, 1.0) var plateau_step_m: float = 160.0
@export_range(0.04, 0.5, 0.01) var terrace_riser_width: float = 0.14
@export_range(0.0, 1.0, 0.01) var terrace_strength: float = 0.85
@export_range(0.0, 300.0, 1.0) var escarpment_jitter_m: float = 70.0
@export_range(2000.0, 50000.0, 1.0) var escarpment_scale_m: float = 12000.0
@export_range(0.0, 1.0, 0.01) var breaks_strength: float = 0.6
@export_range(8.0, 200.0, 1.0) var breaks_step_m: float = 40.0
@export_range(0.0, 500.0, 1.0) var landform_shore_start_m: float = 20.0
@export_range(1.0, 1500.0, 1.0) var landform_shore_full_m: float = 140.0
@export_range(0.0, 1.0, 0.01) var lowland_detail_floor: float = 0.45
@export_range(0.0, 1.0, 0.01) var plains_detail_fraction: float = 0.2
@export_range(0.0, 120.0, 0.1) var lowland_erosion_strength_m: float = 24.0
@export_range(1, 4, 1) var lowland_erosion_octaves: int = 2

var flatten_denominator: float = exp(3.0) - 1.0

func validation_errors() -> PackedStringArray:
	var errors := PackedStringArray()
	if generator_id != LANDFORM_GENERATOR_ID and generator_id != CLIMATE_GENERATOR_ID and generator_id != SITES_GENERATOR_ID and generator_id != EROSION_GENERATOR_ID and generator_id != DETAIL_GENERATOR_ID and generator_id != GENERATOR_ID and generator_id != LEGACY_GENERATOR_ID:
		errors.append("Unsupported generator_id: %s" % generator_id)
	if domain_id != DOMAIN_ID:
		errors.append("Unsupported domain_id: %s" % domain_id)
	if not is_finite(circumference_m) or circumference_m <= 0.0:
		errors.append("circumference_m must be finite and positive.")
	elif absf(circumference_m - CERTIFIED_CIRCUMFERENCE_M) > 0.001:
		errors.append("Only the certified preview circumference is supported.")
	if not is_finite(width_m) or width_m <= 0.0:
		errors.append("width_m must be finite and positive.")
	elif absf(width_m - CERTIFIED_WIDTH_M) > 0.001:
		errors.append("Only the certified preview width is supported.")
	if not is_finite(min_height_m) or not is_finite(max_height_m) or min_height_m >= max_height_m:
		errors.append("Height bounds must be finite and ordered.")
	elif absf(min_height_m - CERTIFIED_MIN_HEIGHT_M) > 0.001 or absf(max_height_m - CERTIFIED_MAX_HEIGHT_M) > 0.001:
		errors.append("Only the certified -8000 m to +8000 m height contract is supported.")
	return errors

func snapshot() -> TerrainGenerationRecipe:
	var copy := TerrainGenerationRecipe.new()
	copy.seed = seed
	copy.generator_id = generator_id
	copy.domain_id = domain_id
	copy.circumference_m = CERTIFIED_CIRCUMFERENCE_M
	copy.width_m = CERTIFIED_WIDTH_M
	copy.min_height_m = CERTIFIED_MIN_HEIGHT_M
	copy.max_height_m = CERTIFIED_MAX_HEIGHT_M
	copy.land_balance = _finite_or(land_balance, 0.0, -0.35, 0.35)
	copy.continental_scale_m = _finite_or(continental_scale_m, 300000.0, MIN_GEOMETRIC_WAVELENGTH_M, 1000000.0)
	copy.mountain_scale_m = _finite_or(mountain_scale_m, 80000.0, MIN_GEOMETRIC_WAVELENGTH_M, 500000.0)
	copy.basin_depth_m = _finite_or(basin_depth_m, 4500.0, MIN_BASIN_DEPTH_M, -copy.min_height_m)
	copy.trench_depth_m = _finite_or(trench_depth_m, 3000.0, 0.0, -copy.min_height_m - copy.basin_depth_m)
	copy.trench_threshold = _finite_or(trench_threshold, 0.80, 0.0, 1.0)
	copy.secondary_amplitude_m = _finite_or(secondary_amplitude_m, 60.0, 0.0, 240.0)
	copy.micro_amplitude_m = _finite_or(micro_amplitude_m, 4.0, 0.0, 32.0)
	if copy.generator_id == DETAIL_GENERATOR_ID or _is_erosion_family(copy.generator_id):
		copy.foothill_amplitude_m = _finite_or(foothill_amplitude_m, 100.0, 0.0, 240.0)
		copy.foothill_scale_m = _finite_or(foothill_scale_m, 1500.0, 128.0, 8192.0)
		copy.spur_amplitude_m = _finite_or(spur_amplitude_m, 220.0, 0.0, 400.0)
		copy.spur_scale_m = _finite_or(spur_scale_m, 1000.0, 128.0, 8192.0)
		copy.mountain_detail_mode = clampi(mountain_detail_mode, 0, 3)
		copy.erosion_strength_m = _finite_or(erosion_strength_m, 60.0, 0.0, 160.0)
		copy.erosion_scale_m = _finite_or(erosion_scale_m, 256.0, 64.0, 2048.0)
		copy.swiss_warp = _finite_or(swiss_warp, 0.35, 0.0, 1.0)
		if _is_erosion_family(copy.generator_id):
			copy.erosion_ridge_preservation = _finite_or(erosion_ridge_preservation, 0.75, 0.0, 1.0)
			copy.gully_normalization = _finite_or(gully_normalization, 0.6, 0.0, 1.0)
	var mountain_headroom := copy.max_height_m - 2000.0 - copy.secondary_amplitude_m - copy.micro_amplitude_m - copy.foothill_amplitude_m - copy.spur_amplitude_m
	copy.mountain_amplitude_m = _finite_or(mountain_amplitude_m, 5400.0, 0.0, maxf(0.0, mountain_headroom))
	copy.valley_width = _finite_or(valley_width, 0.18, 0.01, 1.0)
	copy.flattening = _finite_or(flattening, 3.0, 0.0, 8.0)
	copy.ridge_scale_m = _finite_or(ridge_scale_m, 8000.0, MIN_GEOMETRIC_WAVELENGTH_M, 100000.0)
	copy.ridge_sharpness = _finite_or(ridge_sharpness, 1.5, 0.1, 4.0)
	copy.detail_start_m = _finite_or(detail_start_m, 2000.0, 0.0, copy.max_height_m - 1.0)
	copy.detail_full_m = _finite_or(detail_full_m, 4500.0, copy.detail_start_m + 1.0, copy.max_height_m)
	copy.warp_amplitude_m = _finite_or(warp_amplitude_m, 6000.0, 0.0, 50000.0)
	copy.noise_gain = _finite_or(noise_gain, 0.5, 0.0, 1.0)
	copy.noise_lacunarity = _finite_or(noise_lacunarity, 2.0, 1.0, 2.0)
	copy.secondary_scale_m = _finite_or(secondary_scale_m, 256.0, MIN_GEOMETRIC_WAVELENGTH_M, 10000.0)
	copy.micro_scale_m = _finite_or(micro_scale_m, 32.0, MIN_GEOMETRIC_WAVELENGTH_M, 1000.0)
	copy.warp_scale_m = _finite_or(warp_scale_m, 180000.0, MIN_GEOMETRIC_WAVELENGTH_M, 1000000.0)
	if copy.generator_id == GENERATOR_ID or copy.generator_id == DETAIL_GENERATOR_ID or _is_erosion_family(copy.generator_id):
		copy.beach_slope = _finite_or(beach_slope, 0.025, 0.005, 0.1)
		copy.shelf_width_m = _finite_or(shelf_width_m, 12000.0, 2000.0, 100000.0)
		copy.shelf_depth_m = _finite_or(shelf_depth_m, 150.0, 50.0, 300.0)
		copy.continental_slope_width_m = _finite_or(continental_slope_width_m, 30000.0, 5000.0, 100000.0)
	copy.continental_octaves = _safe_octaves(continental_octaves, copy.continental_scale_m, copy.noise_lacunarity, 8)
	copy.mountain_octaves = _safe_octaves(mountain_octaves, copy.mountain_scale_m, copy.noise_lacunarity, 8)
	copy.valley_octaves = _safe_octaves(valley_octaves, maxf(MIN_GEOMETRIC_WAVELENGTH_M, copy.mountain_scale_m * 0.45), copy.noise_lacunarity, 5)
	copy.trench_octaves = _safe_octaves(trench_octaves, maxf(MIN_GEOMETRIC_WAVELENGTH_M, copy.continental_scale_m * 0.35), copy.noise_lacunarity, 5)
	copy.ridge_octaves = _safe_octaves(ridge_octaves, copy.ridge_scale_m, copy.noise_lacunarity, 8)
	copy.secondary_octaves = _safe_octaves(secondary_octaves, copy.secondary_scale_m, copy.noise_lacunarity, 5)
	copy.micro_octaves = _safe_octaves(micro_octaves, copy.micro_scale_m, copy.noise_lacunarity, 2)
	copy.warp_octaves = _safe_octaves(warp_octaves, copy.warp_scale_m, copy.noise_lacunarity, 5)
	if copy.generator_id == DETAIL_GENERATOR_ID or _is_erosion_family(copy.generator_id):
		copy.erosion_octaves = _safe_octaves(erosion_octaves, copy.erosion_scale_m, copy.noise_lacunarity, 4)
	if _is_landform_family(copy.generator_id):
		# Ranges and cross-field constraints mirror TerrainGenerationParameters (C#), which stays authoritative.
		copy.climate_machines = clampi(climate_machines, 0, 1)
		copy.landform_scale_m = _finite_or(landform_scale_m, 32000.0, 8000.0, 200000.0)
		copy.plains_threshold = _finite_or(plains_threshold, 0.445, 0.05, 0.95)
		copy.hills_threshold = _finite_or(hills_threshold, 0.575, copy.plains_threshold + 0.01, 0.99)
		copy.plateau_scale_m = _finite_or(plateau_scale_m, 64000.0, 16000.0, 400000.0)
		copy.plateau_threshold = _finite_or(plateau_threshold, 0.58, 0.05, 0.95)
		copy.plateau_full_threshold = _finite_or(plateau_full_threshold, 0.68, copy.plateau_threshold + 0.01, 0.99)
		copy.hill_amplitude_m = _finite_or(hill_amplitude_m, 480.0, 0.0, 900.0)
		copy.hill_scale_m = _finite_or(hill_scale_m, 5000.0, 1000.0, 16000.0)
		copy.hill_detail_weight = _finite_or(hill_detail_weight, 0.35, 0.0, 1.0)
		copy.hill_valley_exponent = _finite_or(hill_valley_exponent, 1.35, 0.5, 3.0)
		copy.plains_hill_fraction = _finite_or(plains_hill_fraction, 0.08, 0.0, 0.5)
		copy.hill_valley_floor = _finite_or(hill_valley_floor, 0.6, 0.0, 1.0)
		copy.plateau_lift_m = _finite_or(plateau_lift_m, 320.0, 0.0, 900.0)
		copy.plateau_step_m = _finite_or(plateau_step_m, 160.0, 20.0, 600.0)
		copy.terrace_riser_width = _finite_or(terrace_riser_width, 0.14, 0.04, 0.5)
		copy.terrace_strength = _finite_or(terrace_strength, 0.85, 0.0, 1.0)
		copy.escarpment_jitter_m = _finite_or(escarpment_jitter_m, 70.0, 0.0, 300.0)
		copy.escarpment_scale_m = _finite_or(escarpment_scale_m, 12000.0, 2000.0, 50000.0)
		copy.breaks_strength = _finite_or(breaks_strength, 0.6, 0.0, 1.0)
		copy.breaks_step_m = _finite_or(breaks_step_m, 40.0, 8.0, 200.0)
		copy.landform_shore_start_m = _finite_or(landform_shore_start_m, 20.0, 0.0, 500.0)
		copy.landform_shore_full_m = _finite_or(landform_shore_full_m, 140.0, copy.landform_shore_start_m + 1.0, 1500.0)
		copy.lowland_detail_floor = _finite_or(lowland_detail_floor, 0.45, 0.0, 1.0)
		copy.plains_detail_fraction = _finite_or(plains_detail_fraction, 0.2, 0.0, 1.0)
		copy.lowland_erosion_strength_m = _finite_or(lowland_erosion_strength_m, 24.0, 0.0, 120.0)
		copy.lowland_erosion_octaves = clampi(lowland_erosion_octaves, 1, copy.erosion_octaves)
	copy.flatten_denominator = exp(copy.flattening) - 1.0
	return copy

## Complete normalized payload for the C# terrain authority. Derived values stay local.
func to_normalized_dictionary() -> Dictionary:
	var normalized := snapshot()
	return normalized._normalized_dictionary_fields()

func _normalized_dictionary_fields() -> Dictionary:
	var result := {
		"seed": seed, "generator_id": generator_id, "domain_id": domain_id,
		"circumference_m": circumference_m, "width_m": width_m,
		"min_height_m": min_height_m, "max_height_m": max_height_m,
		"land_balance": land_balance, "continental_scale_m": continental_scale_m,
		"mountain_scale_m": mountain_scale_m, "basin_depth_m": basin_depth_m,
		"trench_depth_m": trench_depth_m, "trench_threshold": trench_threshold,
		"mountain_amplitude_m": mountain_amplitude_m, "valley_width": valley_width,
		"flattening": flattening, "ridge_scale_m": ridge_scale_m,
		"ridge_sharpness": ridge_sharpness, "secondary_amplitude_m": secondary_amplitude_m,
		"micro_amplitude_m": micro_amplitude_m, "detail_start_m": detail_start_m,
		"detail_full_m": detail_full_m, "warp_amplitude_m": warp_amplitude_m,
		"continental_octaves": continental_octaves, "mountain_octaves": mountain_octaves,
		"valley_octaves": valley_octaves, "trench_octaves": trench_octaves,
		"ridge_octaves": ridge_octaves, "secondary_octaves": secondary_octaves,
		"micro_octaves": micro_octaves, "warp_octaves": warp_octaves,
		"noise_gain": noise_gain, "noise_lacunarity": noise_lacunarity,
		"secondary_scale_m": secondary_scale_m, "micro_scale_m": micro_scale_m,
		"warp_scale_m": warp_scale_m
	}
	if generator_id == GENERATOR_ID or generator_id == DETAIL_GENERATOR_ID or _is_erosion_family(generator_id):
		result["beach_slope"] = beach_slope
		result["shelf_width_m"] = shelf_width_m
		result["shelf_depth_m"] = shelf_depth_m
		result["continental_slope_width_m"] = continental_slope_width_m
	if generator_id == DETAIL_GENERATOR_ID or _is_erosion_family(generator_id):
		result["foothill_amplitude_m"] = foothill_amplitude_m
		result["foothill_scale_m"] = foothill_scale_m
		result["spur_amplitude_m"] = spur_amplitude_m
		result["spur_scale_m"] = spur_scale_m
		result["mountain_detail_mode"] = mountain_detail_mode
		result["erosion_strength_m"] = erosion_strength_m
		result["erosion_scale_m"] = erosion_scale_m
		result["erosion_octaves"] = erosion_octaves
		result["swiss_warp"] = swiss_warp
		if _is_erosion_family(generator_id):
			result["erosion_ridge_preservation"] = erosion_ridge_preservation
			result["gully_normalization"] = gully_normalization
	if _is_landform_family(generator_id):
		result["climate_machines"] = climate_machines
		result["landform_scale_m"] = landform_scale_m
		result["plains_threshold"] = plains_threshold
		result["hills_threshold"] = hills_threshold
		result["plateau_scale_m"] = plateau_scale_m
		result["plateau_threshold"] = plateau_threshold
		result["plateau_full_threshold"] = plateau_full_threshold
		result["hill_amplitude_m"] = hill_amplitude_m
		result["hill_scale_m"] = hill_scale_m
		result["hill_detail_weight"] = hill_detail_weight
		result["hill_valley_exponent"] = hill_valley_exponent
		result["plains_hill_fraction"] = plains_hill_fraction
		result["hill_valley_floor"] = hill_valley_floor
		result["plateau_lift_m"] = plateau_lift_m
		result["plateau_step_m"] = plateau_step_m
		result["terrace_riser_width"] = terrace_riser_width
		result["terrace_strength"] = terrace_strength
		result["escarpment_jitter_m"] = escarpment_jitter_m
		result["escarpment_scale_m"] = escarpment_scale_m
		result["breaks_strength"] = breaks_strength
		result["breaks_step_m"] = breaks_step_m
		result["landform_shore_start_m"] = landform_shore_start_m
		result["landform_shore_full_m"] = landform_shore_full_m
		result["lowland_detail_floor"] = lowland_detail_floor
		result["plains_detail_fraction"] = plains_detail_fraction
		result["lowland_erosion_strength_m"] = lowland_erosion_strength_m
		result["lowland_erosion_octaves"] = lowland_erosion_octaves
	return result

static func create_detail_default() -> TerrainGenerationRecipe:
	var recipe := TerrainGenerationRecipe.new()
	recipe.generator_id = DETAIL_GENERATOR_ID
	recipe.foothill_amplitude_m = 100.0
	recipe.foothill_scale_m = 1500.0
	recipe.spur_amplitude_m = 220.0
	recipe.spur_scale_m = 1000.0
	recipe.mountain_detail_mode = 2
	recipe.erosion_strength_m = 60.0
	recipe.erosion_scale_m = 256.0
	recipe.erosion_octaves = 3
	recipe.swiss_warp = 0.35
	return recipe

static func create_erosion_default() -> TerrainGenerationRecipe:
	var recipe := create_detail_default()
	recipe.generator_id = EROSION_GENERATOR_ID
	recipe.erosion_ridge_preservation = 0.75
	recipe.gully_normalization = 0.6
	return recipe

static func _finite_or(value: float, fallback: float, minimum: float, maximum: float) -> float:
	if not is_finite(value):
		return clampf(fallback, minimum, maximum)
	return clampf(value, minimum, maximum)

static func _safe_octaves(requested: int, scale_m: float, lacunarity: float, configured_maximum: int) -> int:
	var maximum := 1
	var wavelength := scale_m
	while maximum < configured_maximum and wavelength / lacunarity >= MIN_GEOMETRIC_WAVELENGTH_M:
		wavelength /= lacunarity
		maximum += 1
	return clampi(requested, 1, maximum)

## True for recipes that carry the full v4 field set (v4 itself, v5 with emitter sites, v6 with climate machines and v7 with lowland landforms).
static func _is_erosion_family(id: String) -> bool:
	return id == EROSION_GENERATOR_ID or id == SITES_GENERATOR_ID or id == CLIMATE_GENERATOR_ID or id == LANDFORM_GENERATOR_ID

## True for recipes that also carry the v7 lowland-landform fields.
static func _is_landform_family(id: String) -> bool:
	return id == LANDFORM_GENERATOR_ID

static func create_sites_default() -> TerrainGenerationRecipe:
	var recipe := create_erosion_default()
	recipe.generator_id = SITES_GENERATOR_ID
	return recipe

static func create_climate_default() -> TerrainGenerationRecipe:
	var recipe := create_sites_default()
	recipe.generator_id = CLIMATE_GENERATOR_ID
	return recipe

## v7 default: the v5 field set with the landform id; the v7 fields keep their script defaults and climate machines stay off.
static func create_landform_default() -> TerrainGenerationRecipe:
	var recipe := create_sites_default()
	recipe.generator_id = LANDFORM_GENERATOR_ID
	return recipe
