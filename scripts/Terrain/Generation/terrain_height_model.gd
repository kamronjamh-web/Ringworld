class_name TerrainHeightModel
extends RefCounted

## Thin runtime adapter. The managed sampler is the sole terrain formula authority.
const TerrainGenerationSamplerBridge = preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")

var _sampler

func _init(recipe: TerrainGenerationRecipe = null) -> void:
	var source := recipe if recipe != null else TerrainGenerationRecipe.create_detail_default()
	var errors := source.validation_errors()
	assert(errors.is_empty(), "Terrain recipe identity/domain is incompatible: %s" % "; ".join(errors))
	_sampler = TerrainGenerationSamplerBridge.new()
	var configured: bool = _sampler.Configure(source.to_normalized_dictionary())
	assert(configured, "Terrain generation sampler configuration failed: %s" % _sampler.LastError)

func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE and _sampler != null:
		_sampler.Release()
		_sampler = null

func calculate_1to1_height(x: float, z: float) -> float:
	return float(_sampler.CalculateHeight(x, z))

func sample_diagnostics(x: float, z: float) -> Dictionary:
	return _sampler.SampleDiagnostics(x, z)

func sample_grid(origin_around: float, origin_across: float, columns: int, rows: int, spacing_m: float) -> PackedFloat32Array:
	assert(columns >= 0 and rows >= 0 and spacing_m > 0.0)
	return _sampler.SampleGrid(origin_around, origin_across, columns, rows, spacing_m)

static func flatten_unit(u: float, k: float) -> float:
	return float(TerrainGenerationSamplerBridge.FlattenUnit(u, k))

func continental_height(continentalness: float) -> float:
	return float(_sampler.ContinentalHeight(continentalness))

static func detail_mask_for(macro_height: float, alpine_weight: float, crest_weight: float, valley_weight: float, start_m: float, full_m: float) -> float:
	return float(TerrainGenerationSamplerBridge.DetailMaskFor(macro_height, alpine_weight, crest_weight, valley_weight, start_m, full_m))
