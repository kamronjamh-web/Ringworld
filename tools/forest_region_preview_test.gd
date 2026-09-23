extends SceneTree

const PREVIEW_SCENE: PackedScene = preload("res://scenes/ForestRegionPreview.tscn")
const DEFAULT_SEED := 1337
const DEFAULT_AROUND := 0
const DEFAULT_ACROSS := 159000
const DEFAULT_SPAN_KM := 128.0
const MAP_TIMEOUT_MS := 20000
const WATCHDOG_SECONDS := 90.0

var _failed := false
var _failure_messages: Array[String] = []
var _watchdog: Timer

func _initialize() -> void:
	call_deferred("_run")

func _start_watchdog() -> void:
	_watchdog = Timer.new()
	_watchdog.one_shot = true
	_watchdog.process_mode = Node.PROCESS_MODE_ALWAYS
	_watchdog.timeout.connect(_watchdog_timeout)
	root.add_child(_watchdog)
	_watchdog.start(WATCHDOG_SECONDS)

func _watchdog_timeout() -> void:
	_fail("global 90-second watchdog expired")
	_finish()

func _run() -> void:
	_start_watchdog()
	var preview: Node = PREVIEW_SCENE.instantiate()
	root.add_child(preview)
	await _wait_ready(preview, "initial map")
	var initial: Dictionary = _diagnostics(preview)
	print("FOREST_REGION_PREVIEW_STATS ", initial)
	_check_initial(initial)
	if _capture_requested() and not _failed:
		await _capture_modes(preview)
		await _exercise_ui_controls(preview)

	# Recoloring must be a cache-only operation.
	# UI navigation intentionally generated several maps before this point.
	var samples_before_modes := int(_diagnostics(preview).get("sample_count", -1))
	for mode in [1, 2, 0]:
		preview.call("SetMapMode", mode)
		await process_frame
		var mode_diag: Dictionary = _diagnostics(preview)
		_require(int(mode_diag.get("sample_count", -2)) == samples_before_modes, "mode %d resampled the map" % mode)

	# The exact completed default request must reproduce its cached checksums.
	preview.call("RequestMap", DEFAULT_SEED, DEFAULT_AROUND, DEFAULT_ACROSS, DEFAULT_SPAN_KM)
	await _wait_ready(preview, "repeat default map")
	var repeated: Dictionary = _diagnostics(preview)
	_require(int(repeated.get("terrain_checksum", -1)) == int(initial.get("terrain_checksum", -2)), "repeat terrain checksum changed")
	_require(int(repeated.get("forest_checksum", -1)) == int(initial.get("forest_checksum", -2)), "repeat forest checksum changed")

	# Switch modes while a fresh build is active: the last completed snapshot must remain stable.
	preview.call("RequestMap", 7330, 9000, 145000, 64.0)
	await process_frame
	var active_before: Dictionary = _diagnostics(preview)
	preview.call("SetMapMode", 2)
	await process_frame
	var active_after: Dictionary = _diagnostics(preview)
	_require(int(active_after.get("terrain_checksum", -1)) == int(active_before.get("terrain_checksum", -2)), "mode switch changed completed terrain during build")
	_require(int(active_after.get("forest_checksum", -1)) == int(active_before.get("forest_checksum", -2)), "mode switch changed completed forest during build")

	# A newer queued request must supersede an incomplete one.
	preview.call("RequestMap", 7331, 12000, 145000, 64.0)
	await process_frame
	await process_frame
	await _wait_ready(preview, "superseding map")
	var superseded: Dictionary = _diagnostics(preview)
	_require(int(superseded.get("completed_seed", -1)) == 7331, "superseding seed was not completed")
	_require(int(superseded.get("completed_around", -1)) == 12000, "superseding around coordinate was not completed")
	_require(int(superseded.get("completed_across", -1)) == 145000, "superseding across coordinate was not completed")
	_require(int(superseded.get("completed_across", -1)) >= 0 and int(superseded.get("completed_across", 999999)) < 318000, "completed center is outside certified ring width")

	# Exit safety: abandon a deliberately active build without waiting for it.
	preview.call("RequestMap", 7332, 0, DEFAULT_ACROSS, DEFAULT_SPAN_KM)
	await process_frame
	preview.queue_free()
	await process_frame
	await process_frame
	_finish()

func _check_initial(diagnostic: Dictionary) -> void:
	_require(bool(diagnostic.get("ready", false)), "initial map is not ready")
	_require(not bool(diagnostic.get("building", true)), "initial map still building")
	_require(int(diagnostic.get("map_width", 0)) == 256 and int(diagnostic.get("map_height", 0)) == 192, "map dimensions are not 256x192")
	_require(int(diagnostic.get("sample_count", 0)) == 256 * 192, "initial map did not sample every raster pixel")
	_require(int(diagnostic.get("valid_samples", 0)) > 0, "initial map has no valid terrain/forest samples")
	_require(int(diagnostic.get("dense_count", 0)) > 0 and int(diagnostic.get("open_count", 0)) > 0 and int(diagnostic.get("clearing_count", 0)) > 0, "reference map lacks dense forest, woodland, or clearings")
	_require(int(diagnostic.get("maple_count", 0)) > int(diagnostic.get("fir_count", 0)) and int(diagnostic.get("maple_count", 0)) > int(diagnostic.get("pine_count", 0)), "maple is not most prominent in reference map")
	_require(int(diagnostic.get("fir_count", 0)) > 0 and int(diagnostic.get("pine_count", 0)) > 0, "reference map lacks conifer pockets")
	_require(int(diagnostic.get("terrain_checksum", 0)) != 0, "terrain checksum is empty")
	_require(int(diagnostic.get("forest_checksum", 0)) != 0, "forest checksum is empty")
	_require(String(diagnostic.get("last_error", "")) == "", "initial map reported an error")

func _wait_ready(preview: Node, label: String) -> void:
	var started := Time.get_ticks_msec()
	while Time.get_ticks_msec() - started < MAP_TIMEOUT_MS:
		if not is_instance_valid(preview):
			_fail(label + " preview was freed unexpectedly")
			return
		var diagnostic: Dictionary = _diagnostics(preview)
		if String(diagnostic.get("last_error", "")) != "":
			_fail(label + " failed: " + String(diagnostic.get("last_error", "")))
			return
		if bool(diagnostic.get("ready", false)) and not bool(diagnostic.get("building", false)):
			return
		await process_frame
	_fail(label + " timed out")

func _capture_requested() -> bool:
	return "--capture" in OS.get_cmdline_user_args()

func _capture_modes(preview: Node) -> void:
	_require(RenderingServer.get_rendering_device() != null, "--capture requires an actual GPU rendering device")
	if _failed:
		return
	var directory := ProjectSettings.globalize_path("res://.godot/forest_regions")
	_require(DirAccess.make_dir_recursive_absolute(directory) == OK, "could not create capture directory")
	for mode_and_name in [[0, "preview_habitat.png"], [1, "preview_density.png"], [2, "preview_species.png"]]:
		preview.call("SetMapMode", int(mode_and_name[0]))
		await RenderingServer.frame_post_draw
		var image: Image = root.get_viewport().get_texture().get_image()
		var path := "res://.godot/forest_regions/" + String(mode_and_name[1])
		_require(image.save_png(path) == OK, "failed to write " + path)
		_require(FileAccess.file_exists(path), "capture missing " + path)

func _exercise_ui_controls(preview: Node) -> void:
	var map := _find_named(preview, "RegionMap") as TextureRect
	var seed_box := _find_named(preview, "Seed") as SpinBox
	var readout := _find_named(preview, "SampleReadout") as Label
	_require(map != null and seed_box != null and readout != null, "preview UI controls are missing")
	if _failed:
		return
	await _press_and_wait(preview, "Right", "UI right")
	_require(int(_diagnostics(preview).get("completed_around", -1)) == 32000, "UI right did not move to 32000")
	await _press_and_wait(preview, "Left", "UI left")
	_require(int(_diagnostics(preview).get("completed_around", -1)) == 0, "UI left did not restore around zero")
	await _press_and_wait(preview, "Across +", "UI across plus")
	_require(int(_diagnostics(preview).get("completed_across", -1)) == 183000, "UI across plus did not move to 183000")
	await _press_and_wait(preview, "Across -", "UI across minus")
	_require(int(_diagnostics(preview).get("completed_across", -1)) == DEFAULT_ACROSS, "UI across minus did not restore center")
	await _press_and_wait(preview, "Zoom in", "UI zoom in")
	_require(is_equal_approx(float(_diagnostics(preview).get("completed_span_km", -1.0)), 64.0), "UI zoom in did not set 64 km span")
	await _press_and_wait(preview, "Zoom out", "UI zoom out")
	_require(is_equal_approx(float(_diagnostics(preview).get("completed_span_km", -1.0)), DEFAULT_SPAN_KM), "UI zoom out did not restore 128 km span")
	seed_box.value = 1338
	await _press_and_wait(preview, "Generate", "UI generate changed seed")
	_require(int(_diagnostics(preview).get("completed_seed", -1)) == 1338, "UI seed generation did not use 1338")
	seed_box.value = DEFAULT_SEED
	await _press_and_wait(preview, "Generate", "UI generate restored seed")
	var restored: Dictionary = _diagnostics(preview)
	_require(int(restored.get("completed_seed", -1)) == DEFAULT_SEED and int(restored.get("completed_around", -1)) == DEFAULT_AROUND and int(restored.get("completed_across", -1)) == DEFAULT_ACROSS, "UI did not restore the default center")
	var hover := InputEventMouseMotion.new()
	hover.position = map.size * 0.5
	map.emit_signal("gui_input", hover)
	await RenderingServer.frame_post_draw
	_require(readout.text.contains("Around") and readout.text.contains("Across") and readout.text.contains("Density"), "hover readout lacks sample fields")
	_require(not readout.text.to_lower().contains("move cursor"), "hover readout retained its initial prompt")
	var image: Image = root.get_viewport().get_texture().get_image()
	var path := "res://.godot/forest_regions/preview_hover.png"
	_require(image.save_png(path) == OK and FileAccess.file_exists(path), "failed to write hover capture")
	if not _failed:
		print("FOREST_REGION_PREVIEW_UI=PASS")

func _press_and_wait(preview: Node, text: String, label: String) -> void:
	var button := _find_button(preview, text)
	_require(button != null, "missing UI button " + text)
	if button == null:
		return
	button.emit_signal("pressed")
	await _wait_ready(preview, label)

func _find_named(node: Node, node_name: String) -> Node:
	if node.name == node_name:
		return node
	for child in node.get_children():
		var found := _find_named(child as Node, node_name)
		if found != null:
			return found
	return null

func _find_button(node: Node, text: String) -> Button:
	if node is Button and (node as Button).text == text:
		return node as Button
	for child in node.get_children():
		var found := _find_button(child as Node, text)
		if found != null:
			return found
	return null

func _diagnostics(preview: Node) -> Dictionary:
	var value: Variant = preview.call("GetPreviewDiagnostics")
	if value is Dictionary:
		return value as Dictionary
	_fail("GetPreviewDiagnostics did not return a Dictionary")
	return {}

func _require(condition: bool, message: String) -> void:
	if not condition:
		_fail(message)

func _fail(message: String) -> void:
	if message not in _failure_messages:
		_failure_messages.append(message)
	_failed = true

func _finish() -> void:
	if _watchdog != null:
		_watchdog.stop()
	if _failed:
		for message in _failure_messages:
			push_error("FOREST_REGION_PREVIEW=FAIL " + message)
		quit(1)
		return
	print("FOREST_REGION_PREVIEW=PASS")
	quit(0)
