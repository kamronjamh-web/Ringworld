class_name TerrainGenerationJob
extends RefCounted

const MODEL := preload("res://scripts/Terrain/Generation/terrain_height_model.gd")
const WINDOW := preload("res://scripts/Terrain/Generation/terrain_generation_window.gd")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const GEOMETRY := preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
const SIDE := 128
const WINDOW_SIDE := 384

class Token extends RefCounted:
	var _mutex := Mutex.new()
	var _cancelled := false
	func cancel() -> void:
		_mutex.lock()
		_cancelled = true
		_mutex.unlock()
	func is_cancelled() -> bool:
		_mutex.lock()
		var value: bool = _cancelled
		_mutex.unlock()
		return value

var recipe: TerrainGenerationRecipe
var edits: Dictionary = {}
var around := 0
var across := 0
var direction := 1
var token: Token
var tiles: Array = []
var mode := "sample"
var result: Dictionary = {}
var frame_around := 0
var frame_across := 0
var tile_cells := 32

func run() -> void:
	if mode == "pack":
		_pack()
	else:
		_sample()

func _sample() -> void:
	var started := Time.get_ticks_usec()
	if recipe == null or direction not in [-1, 1] or token == null:
		result = {"ok": false, "error": "invalid sample job"}
		return
	var model := MODEL.new(recipe)
	var samples := PackedFloat32Array()
	samples.resize(SIDE * SIDE)
	var circumference := int(RECIPE.CERTIFIED_CIRCUMFERENCE_M)
	for strip_start in range(0, SIDE, 8):
		if token.is_cancelled():
			result = {"ok": false, "cancelled": true}
			return
		var strip_rows: int = min(8, SIDE - strip_start)
		# sample_grid is the sole model bridge for this strip. For reverse-across
		# windows, sample ascending rows and copy them into descending output rows.
		var source_first_row: int = strip_start if direction > 0 else strip_start + strip_rows - 1
		var source_across: int = across + direction * source_first_row * 2
		var strip: PackedFloat32Array = model.sample_grid(around, source_across, SIDE, strip_rows, 2.0)
		if token.is_cancelled():
			result = {"ok": false, "cancelled": true}
			return
		if strip.size() != SIDE * strip_rows:
			result = {"ok": false, "error": "invalid sampled strip"}
			return
		for local_row in strip_rows:
			var row: int = strip_start + local_row
			var source_row: int = local_row if direction > 0 else strip_rows - 1 - local_row
			var canonical_across: int = across + direction * row * 2
			for column in SIDE:
				var canonical_around: int = posmod(around + column * 2, circumference)
				var key := Vector2i(canonical_around, canonical_across)
				var value: float = float(edits[key]) if edits.has(key) else strip[source_row * SIDE + column]
				if not is_finite(value) or value < -8000.0 or value > 8000.0:
					result = {"ok": false, "error": "invalid sampled height"}
					return
				samples[row * SIDE + column] = value
	if token.is_cancelled():
		result = {"ok": false, "cancelled": true}
		return
	result = {"ok": true, "samples": samples, "sampling_ms": float(Time.get_ticks_usec() - started) / 1000.0}

func _pack() -> void:
	var started := Time.get_ticks_usec()
	if token == null or tiles.size() != 9:
		result = {"ok": false, "error": "invalid pack job"}
		return
	var heights := PackedFloat32Array()
	heights.resize(WINDOW_SIDE * WINDOW_SIDE)
	for region_z in 3:
		for region_x in 3:
			var tile: PackedFloat32Array = tiles[region_z * 3 + region_x]
			if tile.size() != SIDE * SIDE:
				result = {"ok": false, "error": "invalid tile"}
				return
			for row in SIDE:
				if row % 8 == 0 and token.is_cancelled():
					result = {"ok": false, "cancelled": true}
					return
				for column in SIDE:
					heights[(region_z * SIDE + row) * WINDOW_SIDE + region_x * SIDE + column] = tile[row * SIDE + column]
	var frame_height: float = heights[128 * WINDOW_SIDE + 128]
	var images: Array[Image] = WINDOW.pack(heights, frame_height)
	var packing_ms := float(Time.get_ticks_usec() - started) / 1000.0
	if token.is_cancelled():
		result = {"ok": false, "cancelled": true}
		return
	if images.size() != 9:
		result = {"ok": false, "error": "RF packing failed"}
		return
	var geometry = GEOMETRY.new()
	if token.is_cancelled() or not geometry.Prepare(heights, frame_around, frame_across, frame_height, direction, tile_cells) or token.is_cancelled():
		result = {"ok": false, "cancelled": token.is_cancelled(), "error": geometry.GetDiagnostics().get("error", "collision geometry preparation failed")}
		return
	result = {"ok": true, "absolute_heights": heights, "height_images": images, "frame_height": frame_height, "packing_ms": packing_ms, "projection_ms": geometry.GetDiagnostics().get("projection_ms", 0.0), "geometry": geometry}
