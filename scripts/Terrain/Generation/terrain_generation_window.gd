class_name TerrainGenerationWindow
extends RefCounted

## A bounded, frame-relative Terrain3D payload prepared from absolute canonical
## heights. This class has no Terrain3D node or scene ownership; publication is
## deliberately left to the main-thread native adapter.
const REGION_SIDE: int = 128
const REGION_COUNT: int = 3
const SIDE: int = REGION_SIDE * REGION_COUNT
const SPACING_M: float = 2.0
const ORIGIN_SAMPLE: int = -REGION_SIDE
const PACKING := preload("res://scripts/Terrain/Generation/TerrainGenerationPacking.cs")

class CancellationProbe extends RefCounted:
	var source: Variant
	func is_cancelled() -> bool:
		if source is Callable:
			var callback: Callable = source
			return callback.is_valid() and bool(callback.call())
		if source is Object and source.has_method("is_cancelled"):
			return bool(source.is_cancelled())
		return false

static func prepare(model: TerrainHeightModel, anchor_around: int, anchor_across: int, across_direction: int, revision: int) -> Dictionary:
	var error := _validation_error(model, anchor_around, anchor_across, across_direction, revision)
	if not error.is_empty():
		return _failure(error)
	var wrapped_anchor_around := _wrap_anchor(anchor_around)

	var sampling_started := Time.get_ticks_usec()
	var absolute_heights := PackedFloat32Array()
	absolute_heights.resize(SIDE * SIDE)
	for row in SIDE:
		var across := float(anchor_across + across_direction * (ORIGIN_SAMPLE + row) * int(SPACING_M))
		for column in SIDE:
			var around := float(wrapped_anchor_around + (ORIGIN_SAMPLE + column) * int(SPACING_M))
			var height := model.calculate_1to1_height(around, across)
			if not is_finite(height) or height < TerrainGenerationRecipe.CERTIFIED_MIN_HEIGHT_M or height > TerrainGenerationRecipe.CERTIFIED_MAX_HEIGHT_M:
				return _failure("Model returned an invalid certified height at row %d, column %d." % [row, column])
			absolute_heights[row * SIDE + column] = height
	var sampling_ms := float(Time.get_ticks_usec() - sampling_started) / 1000.0

	# The anchor is at local sample (0, 0), which is index 128 in this 384-wide
	# window. Read the float32 cache so every RF payload shares its exact frame.
	var anchor_index := (-ORIGIN_SAMPLE) * SIDE + -ORIGIN_SAMPLE
	var frame_height := float(absolute_heights[anchor_index])
	var packing_started := Time.get_ticks_usec()
	var height_images := pack(absolute_heights, frame_height)
	if height_images.size() != REGION_COUNT * REGION_COUNT:
		return _failure("RF packing rejected the sampled height window.")
	return {
		"ok": true,
		"absolute_heights": absolute_heights,
		"height_images": height_images,
		"frame_height": frame_height,
		"anchor_around": wrapped_anchor_around,
		"anchor_across": anchor_across,
		"across_direction": across_direction,
		"revision": revision,
		"sampling_ms": sampling_ms,
		"packing_ms": float(Time.get_ticks_usec() - packing_started) / 1000.0,
	}

## Packs row-major absolute window samples as nine RF images in native region
## order: z=-1..1, then x=-1..1. Each texel stores one common frame offset.
static func pack(absolute_heights: PackedFloat32Array, frame_height: float, cancellation: Variant = Callable()) -> Array[Image]:
	var packing := PACKING.new()
	# C# receives a Godot object, not a Callable: native/custom GDS callables do
	# not provide a reliable C# invocation target. The probe owns optionality.
	var cancellation_probe := CancellationProbe.new()
	cancellation_probe.source = cancellation
	var packed = packing.PackRelativeImages(absolute_heights, frame_height, cancellation_probe)
	if packing.WasCancelled or packed.size() != REGION_COUNT * REGION_COUNT:
		return []
	var images: Array[Image] = []
	for image in packed:
		images.append(image)
	return images

static func _validation_error(model: TerrainHeightModel, anchor_around: int, anchor_across: int, across_direction: int, revision: int) -> String:
	if model == null:
		return "Terrain height model is required."
	if revision < 1:
		return "Revision must be at least one."
	if across_direction != -1 and across_direction != 1:
		return "Across direction must be -1 or 1."
	if anchor_around % int(SPACING_M) != 0 or anchor_across % int(SPACING_M) != 0:
		return "Window anchors must lie on the %d m certified lattice." % int(SPACING_M)
	if anchor_across < 0 or anchor_across >= int(TerrainGenerationRecipe.CERTIFIED_WIDTH_M):
		return "Window anchor is outside the certified across-ring boundary."
	var first_across := anchor_across + across_direction * ORIGIN_SAMPLE * int(SPACING_M)
	var last_across := anchor_across + across_direction * (ORIGIN_SAMPLE + SIDE - 1) * int(SPACING_M)
	var minimum_across := mini(first_across, last_across)
	var maximum_across := maxi(first_across, last_across)
	if minimum_across < 0 or maximum_across >= int(TerrainGenerationRecipe.CERTIFIED_WIDTH_M):
		return "Window crosses the certified across-ring boundary."
	return ""

static func _wrap_anchor(anchor_around: int) -> int:
	var circumference := int(TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M)
	return posmod(anchor_around, circumference)

static func _failure(error: String) -> Dictionary:
	return {
		"ok": false,
		"error": error,
		"absolute_heights": PackedFloat32Array(),
		"height_images": [],
	}
