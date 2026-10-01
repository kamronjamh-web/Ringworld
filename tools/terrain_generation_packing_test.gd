extends SceneTree

## Exact byte parity for the C# worker packing helper. The legacy GDScript
## window packer remains the reference; this test never publishes a terrain job.
const PACKING := preload("res://scripts/Terrain/Generation/TerrainGenerationPacking.cs")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const REPORT_PATH := "res://.godot/terrain-generation-tests/packing-report.json"

var _checks := 0
var _failures := PackedStringArray()
var _metrics := {}

class CancellationProbe extends RefCounted:
	var cancelled := false
	func is_cancelled() -> bool: return cancelled

func _uncancelled() -> CancellationProbe: return CancellationProbe.new()

func _init() -> void:
	create_timer(60.0).timeout.connect(func(): _failures.append("60 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	_test_rf_byte_parity()
	_test_invalid_and_cancellation()
	_test_assembly()
	_test_overlay_parity()
	_measure_packing_only()
	_finish()

func _test_rf_byte_parity() -> void:
	var heights := _heights()
	var frame := float(heights[128 * 384 + 128])
	var legacy: Array[Image] = _legacy_pack_reference(heights, frame)
	var packing = PACKING.new()
	var native: Array = packing.PackRelativeImages(heights, frame, _uncancelled())
	_expect(native.size() == 9 and legacy.size() == 9, "both packers produce nine RF images")
	for index in mini(native.size(), legacy.size()):
		var actual: Image = native[index]
		var expected: Image = legacy[index]
		_expect(actual.get_width() == 128 and actual.get_height() == 128 and actual.get_format() == Image.FORMAT_RF, "native RF dimensions and format")
		_expect(actual.get_data() == expected.get_data(), "native RF bytes exactly match legacy pack order and subtraction")

func _test_invalid_and_cancellation() -> void:
	var packing = PACKING.new()
	var invalid := _heights(); invalid[17] = NAN
	_expect(packing.PackRelativeImages(invalid, 0.0, _uncancelled()).is_empty(), "native RF rejects non-finite sample")
	invalid = _heights(); invalid[17] = 8000.1
	_expect(packing.PackRelativeImages(invalid, 0.0, _uncancelled()).is_empty(), "native RF rejects out-of-contract sample")
	_expect(packing.PackRelativeImages(_heights(), NAN, _uncancelled()).is_empty(), "native RF rejects non-finite frame height")
	var cancelled := CancellationProbe.new(); cancelled.cancelled = true
	var cancelled_images: Array = packing.PackRelativeImages(_heights(), 0.0, cancelled)
	_expect(cancelled_images.is_empty() and packing.WasCancelled, "native RF honors cancellation before producing partial images")

func _test_assembly() -> void:
	var heights := _heights()
	var tiles: Array = []
	for region_z in 3:
		for region_x in 3:
			var tile := PackedFloat32Array(); tile.resize(128 * 128)
			for row in 128:
				for column in 128: tile[row * 128 + column] = heights[(region_z * 128 + row) * 384 + region_x * 128 + column]
			tiles.append(tile)
	var packing = PACKING.new()
	var assembled: PackedFloat32Array = packing.AssembleAbsoluteWindow(tiles, _uncancelled())
	_expect(assembled == heights, "native assembly is exact z-major/x-major float32 reconstruction")
	_expect(packing.AssembleAbsoluteWindow([], _uncancelled()).is_empty(), "native assembly rejects invalid tile count")
	var cancelled := CancellationProbe.new(); cancelled.cancelled = true
	var cancelled_window: PackedFloat32Array = packing.AssembleAbsoluteWindow(tiles, cancelled)
	_expect(cancelled_window.is_empty() and packing.WasCancelled, "native assembly honors cancellation")

func _test_overlay_parity() -> void:
	# ComposeSampledTile receives sixteen ascending 8-row strips. Reverse output
	# reverses each strip, matching the sampling job's per-strip source layout.
	var source := PackedFloat32Array(); source.resize(128 * 128)
	for index in source.size(): source[index] = float(index) * .125 - 64.0
	for direction in [-1, 1]:
		var expected := PackedFloat32Array(); expected.resize(128 * 128)
		var actual := PackedFloat32Array(); actual.resize(128 * 128)
		var edits := {Vector2i(31415932, 1000): 91.0, Vector2i(4, 1000): 431.25, Vector2i(12, 1008): -321.5, Vector2i(0, 1000): 77.0}
		_overlay_reference(expected, source, edits, -4, 1000, direction, 31415936)
		var packing = PACKING.new()
		actual = packing.ComposeSampledTile(source, edits, -4, 1000, direction, 31415936, _uncancelled())
		_expect(actual.size() == expected.size(), "native compose accepts valid %s source" % direction)
		_expect(actual == expected, "native overlay retains legacy sample reversal, wrap, and edit override parity")
		var malformed := {Vector2i(4, 1000): "not a height"}
		_expect(packing.ComposeSampledTile(source, malformed, -4, 1000, direction, 31415936, _uncancelled()).is_empty(), "native overlay rejects invalid edit values")

func _measure_packing_only() -> void:
	# This isolates the same RF conversion only. It is diagnostic evidence, not a
	# claim about full terrain-generation or frame-time speed.
	var heights := _heights(); var frame := float(heights[128 * 384 + 128])
	for round in 3:
		_legacy_pack_reference(heights, frame)
		PACKING.new().PackRelativeImages(heights, frame, _uncancelled())
	var legacy_ms: Array = []; var native_ms: Array = []
	for round in 12:
		var started := Time.get_ticks_usec(); _legacy_pack_reference(heights, frame); legacy_ms.append(float(Time.get_ticks_usec() - started) / 1000.0)
		started = Time.get_ticks_usec(); PACKING.new().PackRelativeImages(heights, frame, _uncancelled()); native_ms.append(float(Time.get_ticks_usec() - started) / 1000.0)
	_metrics["rf_packing_only_ms"] = {"samples": 12, "legacy_median": _median(legacy_ms), "native_median": _median(native_ms), "scope": "RF conversion only; excludes sampling, collision preparation, uploads, and rendering"}
	_expect(_median(legacy_ms) >= 0.0 and _median(native_ms) >= 0.0, "packing-only benchmark records non-negative timings")

func _median(values: Array) -> float:
	var ordered := values.duplicate(); ordered.sort()
	return (float(ordered[5]) + float(ordered[6])) * 0.5

func _overlay_reference(destination: PackedFloat32Array, strip: PackedFloat32Array, edits: Dictionary, around: int, across: int, direction: int, circumference: int) -> void:
	for row in 128:
		var source_row := row if direction > 0 else (row & ~7) + 7 - (row % 8)
		var canonical_across := across + direction * row * 2
		for column in 128:
			var key := Vector2i(posmod(around + column * 2, circumference), canonical_across)
			destination[row * 128 + column] = float(edits.get(key, strip[source_row * 128 + column]))

func _heights() -> PackedFloat32Array:
	var heights := PackedFloat32Array(); heights.resize(384 * 384)
	for row in 384:
		for column in 384: heights[row * 384 + column] = -7999.875 + float(row * 384 + column) * (15999.75 / float(heights.size() - 1))
	return heights

## Frozen mechanical reference of the former GDScript operation. Do not route
## through TerrainGenerationWindow.pack(): it now delegates to the C# helper.
func _legacy_pack_reference(absolute_heights: PackedFloat32Array, frame_height: float) -> Array[Image]:
	var images: Array[Image] = []
	for region_z in 3:
		for region_x in 3:
			var relative := PackedFloat32Array(); relative.resize(128 * 128)
			for row in 128:
				var source_offset := (region_z * 128 + row) * 384 + region_x * 128
				var destination_offset := row * 128
				for column in 128: relative[destination_offset + column] = absolute_heights[source_offset + column] - frame_height
			images.append(Image.create_from_data(128, 128, false, Image.FORMAT_RF, relative.to_byte_array()))
	return images

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition and _failures.size() < 64: _failures.append(label)

func _finish() -> void:
	var report := {"checks": _checks, "metrics": _metrics, "failures": _failures, "status": "PASS" if _failures.is_empty() else "FAIL"}
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REPORT_PATH.get_base_dir()))
	var file := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if file: file.store_string(JSON.stringify(report, "\t") + "\n"); file.close()
	if _failures.is_empty(): print("TERRAIN_GENERATION_PACKING=PASS checks=", _checks); quit(0)
	else: push_error("TERRAIN_GENERATION_PACKING=FAIL ", "; ".join(_failures)); quit(1)
