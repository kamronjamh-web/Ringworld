extends SceneTree

const OUTPUT_DIRECTORY := "res://.godot/terrain-generation-tests"
const REPORT_PATH := OUTPUT_DIRECTORY + "/terrain-generation-window-report.json"
const MINIMUM_CHECKS := 80

var _checks := 0
var _failures := PackedStringArray()
var _completed := false
var _finished := false

func _init() -> void:
	call_deferred("_run_deferred")
	# A deferred run that faults cannot mark completion. The separate watchdog
	# terminates the headless process with a report instead of hanging silently.
	create_timer(30.0).timeout.connect(_watchdog_timeout)

func _run_deferred() -> void:
	_run()
	_finish()

func _watchdog_timeout() -> void:
	if not _finished:
		_failures.append("test watchdog expired before explicit successful run end")
		_finish()

func _finish() -> void:
	if _finished:
		return
	_finished = true
	if not _completed:
		_failures.append("test run did not complete")
	if _checks < MINIMUM_CHECKS:
		_failures.append("too few checks: %d" % _checks)
	_write_report()
	if _failures.is_empty() and _checks >= MINIMUM_CHECKS:
		print("TERRAIN_GENERATION_WINDOW=PASS ", JSON.stringify({"checks": _checks}))
		quit(0)
	else:
		push_error("TERRAIN_GENERATION_WINDOW=FAIL " + "; ".join(_failures))
		quit(1)

func _run() -> void:
	if not _test_synthetic_packing():
		return
	if not _test_invalid_packing():
		return
	if not _test_model_windows():
		return
	_completed = true

func _test_synthetic_packing() -> bool:
	var absolute := PackedFloat32Array()
	absolute.resize(TerrainGenerationWindow.SIDE * TerrainGenerationWindow.SIDE)
	for row in TerrainGenerationWindow.SIDE:
		for column in TerrainGenerationWindow.SIDE:
			absolute[row * TerrainGenerationWindow.SIDE + column] = -7999.875 + float(row * TerrainGenerationWindow.SIDE + column) * (15999.75 / float(absolute.size() - 1))
	var original := absolute.duplicate()
	var frame := 123.125
	var images := TerrainGenerationWindow.pack(absolute, frame)
	_expect(images.size() == 9, "synthetic fixture packs nine native regions")
	_expect(absolute == original, "packing does not mutate absolute input")
	for region_z in 3:
		for region_x in 3:
			var image: Image = images[region_z * 3 + region_x]
			_expect(image.get_width() == 128 and image.get_height() == 128 and image.get_format() == Image.FORMAT_RF, "RF image dimensions and format")
			_expect(image.get_data().size() == 128 * 128 * 4, "RF image byte count")
			for local in [Vector2i(0, 0), Vector2i(127, 0), Vector2i(0, 127), Vector2i(127, 127), Vector2i(63, 77)]:
				var global_row: int = region_z * 128 + local.y
				var global_column: int = region_x * 128 + local.x
				var expected := absolute[global_row * TerrainGenerationWindow.SIDE + global_column] - frame
				_expect(absf(image.get_pixel(local.x, local.y).r - expected) < 0.001, "z-major/x-major region order and shared frame offset")
	return true

func _test_invalid_packing() -> bool:
	_expect(TerrainGenerationWindow.pack(PackedFloat32Array(), 0.0).is_empty(), "packing rejects incorrect sample count")
	var invalid := PackedFloat32Array()
	invalid.resize(TerrainGenerationWindow.SIDE * TerrainGenerationWindow.SIDE)
	invalid[17] = NAN
	_expect(TerrainGenerationWindow.pack(invalid, 0.0).is_empty(), "packing rejects non-finite height")
	invalid[17] = 8000.5
	_expect(TerrainGenerationWindow.pack(invalid, 0.0).is_empty(), "packing rejects out-of-range height")
	invalid[17] = 0.0
	_expect(TerrainGenerationWindow.pack(invalid, NAN).is_empty(), "packing rejects non-finite frame height")
	_expect(TerrainGenerationWindow.pack(invalid, 8000.5).is_empty(), "packing rejects out-of-range frame height")
	_expect(TerrainGenerationWindow.pack(invalid, 1.0e300).is_empty(), "packing rejects extreme finite frame height")
	return true

func _test_model_windows() -> bool:
	var model := TerrainHeightModel.new(TerrainGenerationRecipe.new())
	var around := -2048
	var anchor_across := 1000
	var positive := TerrainGenerationWindow.prepare(model, around, anchor_across, 1, 7)
	_expect(positive.ok and positive.absolute_heights.size() == 384 * 384 and positive.height_images.size() == 9, "default model prepares a complete positive-direction window")
	_expect(positive.anchor_around == int(posmod(around, int(TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M))), "negative around anchor is wrapped in result metadata")
	_expect(positive.frame_height == positive.absolute_heights[128 * 384 + 128], "frame height is the cached float32 anchor sample")
	_expect(positive.sampling_ms >= 0.0 and positive.packing_ms >= 0.0, "preparation records non-negative timings")
	for index in [0, 1, 127, 128, 383, 128 * 384 + 128, 384 * 384 - 1]:
		_expect(is_finite(positive.absolute_heights[index]) and positive.absolute_heights[index] >= -8000.0 and positive.absolute_heights[index] <= 8000.0, "prepared certified heights remain finite and bounded")
	var negative := TerrainGenerationWindow.prepare(model, around, 317000, -1, 8)
	_expect(negative.ok and negative.across_direction == -1, "negative across direction prepares within safe boundary")
	var direction_a := TerrainGenerationWindow.prepare(model, 4096, 1000, 1, 8)
	var direction_b := TerrainGenerationWindow.prepare(model, 4096, 1000, -1, 8)
	_expect(direction_a.ok and direction_b.ok, "both directions prepare the common anchor")
	_expect(direction_a.absolute_heights[128 * 384 + 128] == direction_b.absolute_heights[128 * 384 + 128], "both directions preserve the anchor canonical sample")
	for offset in [2, 64, 127]:
		_expect(direction_a.absolute_heights[(128 + offset) * 384 + 128] == direction_b.absolute_heights[(128 - offset) * 384 + 128], "direction reversal samples the same canonical across positions")
	# Moving an anchor by a region pitch exposes samples which overlap the old
	# window. Canonical heights must match exactly after their float32 cache step.
	var shifted := TerrainGenerationWindow.prepare(model, around + 256, anchor_across, 1, 9)
	_expect(shifted.ok, "adjacent around rebase prepares")
	for row in [0, 64, 255, 383]:
		for column in [0, 1, 127, 255, 383]:
			var shifted_column: int = column - 128
			if shifted_column >= 0:
				_expect(positive.absolute_heights[row * 384 + column] == shifted.absolute_heights[row * 384 + shifted_column], "adjacent rebases share canonical float32 samples")
	var seam := TerrainGenerationWindow.prepare(model, int(TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M) - 256, anchor_across, 1, 10)
	_expect(seam.ok, "around window is allowed to cross the periodic seam")
	var canonical_positive := TerrainGenerationWindow.prepare(model, 256, anchor_across, 1, 11)
	var canonical_negative := TerrainGenerationWindow.prepare(model, 256 - int(TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M), anchor_across, 1, 12)
	_expect(canonical_positive.ok and canonical_negative.ok, "positive and negative circumference anchors prepare")
	for index in [0, 127, 128 * 384 + 128, 384 * 384 - 1]:
		_expect(canonical_positive.absolute_heights[index] == canonical_negative.absolute_heights[index], "circumference-equivalent anchors have identical canonical samples")
	var bad_direction := TerrainGenerationWindow.prepare(model, 0, anchor_across, 0, 1)
	_expect(not bad_direction.ok and bad_direction.height_images.is_empty(), "invalid across direction returns an empty failure")
	var bad_revision := TerrainGenerationWindow.prepare(model, 0, anchor_across, 1, 0)
	_expect(not bad_revision.ok and not bad_revision.error.is_empty(), "invalid revision returns an error dictionary")
	var off_lattice := TerrainGenerationWindow.prepare(model, 1, anchor_across, 1, 1)
	_expect(not off_lattice.ok, "off-lattice anchor is rejected")
	var crossing_low := TerrainGenerationWindow.prepare(model, 0, 0, 1, 1)
	_expect(not crossing_low.ok, "positive window crossing low across boundary is rejected")
	var crossing_high := TerrainGenerationWindow.prepare(model, 0, int(TerrainGenerationRecipe.CERTIFIED_WIDTH_M) - 2, 1, 1)
	_expect(not crossing_high.ok, "positive window crossing high across boundary is rejected")
	return true

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition:
		_failures.append(label)

func _write_report() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT_DIRECTORY))
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output == null:
		_failures.append("could not write report")
		return
	output.store_string(JSON.stringify({"checks": _checks, "completed": _completed, "failures": _failures}, "  "))
	output.close()
