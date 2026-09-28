extends SceneTree

const PREVIEW_SCENE := preload("res://scenes/TerrainGenerationPreview.tscn")
const FIXTURE_DIRECTORY := "res://.godot/terrain-generation-tests"
const TEST_PATH := FIXTURE_DIRECTORY + "/preview_recipe_fixture.tres"
const INCOMPATIBLE_PATH := FIXTURE_DIRECTORY + "/preview_recipe_incompatible_fixture.tres"
const REPORT_PATH := FIXTURE_DIRECTORY + "/preview-smoke-report.json"
const MAX_FRAMES := 5000
const MINIMUM_CHECKS := 20

var _failures := PackedStringArray()
var _checks := 0
var _completed := false
var _preview_stats: Dictionary = {}
var _default_preview_stats: Dictionary = {}
var _full_overview := false

func _init() -> void:
	_run()

func _run() -> void:
	_full_overview = "--full-overview" in OS.get_cmdline_user_args()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
	# This named test fixture is reset before use; no user preview path is touched.
	if FileAccess.file_exists(TEST_PATH):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(TEST_PATH))
	_expect(TerrainGenerationPreviewStore.save_recipe(TerrainGenerationRecipe.new(), TEST_PATH).is_empty(), "fresh valid fixture publishes")
	var incompatible := TerrainGenerationRecipe.new(); incompatible.generator_id = "future_preview_generator"
	ResourceSaver.save(incompatible, INCOMPATIBLE_PATH)
	var incompatible_load := TerrainGenerationPreviewStore.load_recipe(INCOMPATIBLE_PATH)
	_expect(incompatible_load.recipe == null and not incompatible_load.error.is_empty(), "incompatible identity is rejected")
	var preview := _fixture(); root.add_child(preview)
	if not await _wait_ready(preview): _finish(); return
	var first: Dictionary = preview.get_preview_diagnostics()
	_default_preview_stats = first.duplicate()
	_expect(first.ready and first.sample_count > 0 and first.min_height <= first.max_height, "fixture overview ready with bounds")
	if "--capture" in OS.get_cmdline_user_args():
		await RenderingServer.frame_post_draw
		root.get_texture().get_image().save_png(FIXTURE_DIRECTORY + "/preview-smoke-overview.png")
	var overview_before := _texture_hash(preview.get("_map"))
	preview.apply_recipe_setting("land_balance", 0.1); preview.apply_recipe_setting("land_balance", -0.1)
	if not await _wait_ready(preview): _finish(); return
	var latest: Dictionary = preview.get_preview_diagnostics()
	_preview_stats = latest.duplicate()
	_expect(latest.committed_revision == latest.revision and latest.revision > first.revision, "latest rapid revision committed")
	var overview_modes: Dictionary = {}
	var detail_modes: Dictionary = {}
	for mode in 4:
		preview.set_view_mode(mode); await process_frame
		_expect(preview.get_preview_diagnostics().sample_count == latest.sample_count and preview.get_preview_diagnostics().revision == latest.revision, "mode %d did not resample" % mode)
		_expect(_texture_hash(preview.get("_map")) != 0, "mode %d recolored overview texture" % mode)
		overview_modes[_texture_hash(preview.get("_map"))] = true
		detail_modes[_texture_hash(preview.get("_detail_map"))] = true
	_expect(overview_modes.size() > 1 and detail_modes.size() > 1, "view modes change both overview and detail pixels")
	_expect(overview_before != 0, "overview texture had image data")
	preview.select_location(0.0, 0.0)
	if not await _wait_ready(preview): _finish(); return
	_expect(preview.get_preview_diagnostics().sample_count == latest.sample_count, "selection retains overview")
	_expect(_texture_hash(preview.get("_detail_map")) != 0, "selection produces detail texture")
	var locations: Array = preview.get("_location_buttons")
	var available_locations: Dictionary = preview.get("_locations")
	_expect(locations.size() == 4, "four representative location controls exist")
	for button in locations:
		var kind: String = button.text.trim_prefix("Select ").to_lower()
		_expect(button.disabled == (not available_locations.has(kind)), "representative control matches sampled availability: " + kind)
	preview.apply_recipe_setting("land_balance", 0.05)
	preview.select_location(100.0, 100.0)
	_expect(preview.get_preview_diagnostics().committed_revision == latest.committed_revision, "selection during regeneration does not commit partial output")
	if not await _wait_ready(preview): _finish(); return
	_expect(preview.save_recipe(), "fixture saves through common store")
	preview.queue_free(); await process_frame
	var reload := _fixture(); root.add_child(reload)
	if not await _wait_ready(reload): _finish(); return
	_expect(is_equal_approx(reload.get_recipe_snapshot().land_balance, 0.05), "fixture reloads saved recipe")
	reload.queue_free(); await process_frame
	var corrupt := FileAccess.open(TEST_PATH, FileAccess.WRITE); corrupt.store_string("corrupt terrain recipe bytes"); corrupt.close()
	var corrupt_bytes := FileAccess.get_file_as_bytes(TEST_PATH)
	var protected_preview := _fixture(); root.add_child(protected_preview); await process_frame
	var protected: Dictionary = protected_preview.get_preview_diagnostics()
	_expect(not protected.error.is_empty() and protected.save_blocked, "corrupt fixture is blocked")
	_expect(not protected_preview.save_recipe(), "blocked fixture cannot save")
	_expect(FileAccess.get_file_as_bytes(TEST_PATH) == corrupt_bytes, "blocked save preserves corrupt bytes for explicit recovery")
	protected_preview.reset_recipe()
	if not await _wait_ready(protected_preview): _finish(); return
	_expect(not protected_preview.get_preview_diagnostics().save_blocked, "explicit reset unblocks saving")
	var reset_load := TerrainGenerationPreviewStore.load_recipe(TEST_PATH)
	_expect(reset_load.recipe != null and reset_load.error.is_empty(), "explicit reset publishes a valid recipe")
	protected_preview.queue_free(); await process_frame
	var drawer_fixture := _fixture(); root.add_child(drawer_fixture)
	if not await _wait_ready(drawer_fixture): _finish(); return
	var drawer := drawer_fixture.get_node_or_null("RingLiveTuningDrawer")
	_expect(drawer != null, "drawer exists")
	if drawer != null:
		drawer_fixture.apply_recipe_setting("seed", 90210)
		await create_timer(0.25).timeout
		var seed_control := drawer.find_child("seed", true, false) as SpinBox
		_expect(seed_control != null and seed_control.value == 90210, "seed control follows the current recipe")
		drawer_fixture.reset_recipe()
		await create_timer(0.25).timeout
		_expect(seed_control != null and seed_control.value == 1337, "seed control refreshes after reset")
		if not await _wait_ready(drawer_fixture): _finish(); return
		var grave_key := InputEventKey.new(); grave_key.keycode = KEY_QUOTELEFT; grave_key.pressed = true
		Input.parse_input_event(grave_key); await process_frame
		var panel := drawer.get_node_or_null("LiveTuningDrawer")
		_expect(panel != null and panel.visible, "grave key opens visible drawer")
		Input.parse_input_event(grave_key); await process_frame
		_expect(panel != null and not panel.visible, "grave key closes visible drawer")
		if "--capture" in OS.get_cmdline_user_args():
			drawer.call("SetBenchmarkDrawerOpen", true)
			var recipe_section := drawer.find_child("SectionRecipeshapesettings", true, false) as FoldableContainer
			if recipe_section != null:
				recipe_section.folded = false
			await create_timer(0.25).timeout
			await process_frame; await process_frame; await RenderingServer.frame_post_draw
			_expect(panel != null and panel.visible, "benchmark capture drawer is visible")
			var capture_result := root.get_texture().get_image().save_png(FIXTURE_DIRECTORY + "/preview-smoke-drawer.png")
			_expect(capture_result == OK, "drawer capture writes PNG")
			drawer.call("SetBenchmarkDrawerOpen", false)
	var exit_pending := _fixture(); root.add_child(exit_pending); exit_pending.apply_recipe_setting("land_balance", 0.04); exit_pending.queue_free(); await process_frame
	_expect(not is_instance_valid(exit_pending), "scene exits while generation is pending")
	drawer_fixture.queue_free(); await process_frame
	_completed = true; _finish()

func _fixture() -> TerrainGenerationPreview:
	var preview: TerrainGenerationPreview = PREVIEW_SCENE.instantiate()
	if _full_overview:
		preview.configure_test_fixture(TEST_PATH, 256000.0, 512.0, 318000.0)
	else:
		preview.configure_test_fixture(TEST_PATH, 4096.0, 512.0)
	return preview

func _texture_hash(texture_rect: Variant) -> int:
	if texture_rect == null or texture_rect.texture == null:
		return 0
	return hash(texture_rect.texture.get_image().get_data())

func _wait_ready(preview: TerrainGenerationPreview) -> bool:
	var started := Time.get_ticks_msec()
	for frame in MAX_FRAMES:
		if preview.get_preview_diagnostics().ready: return true
		if Time.get_ticks_msec() - started > 30000: _fail("wall-clock timeout"); return false
		await process_frame
	_fail("frame timeout")
	return false

func _expect(condition: bool, description: String) -> void:
	_checks += 1
	if not condition: _fail(description)

func _fail(message: String) -> void:
	if message not in _failures: _failures.append(message)

func _finish() -> void:
	if not _completed: _fail("smoke did not complete")
	if _checks < MINIMUM_CHECKS: _fail("too few checks: %d" % _checks)
	var report := {"status": "PASS" if _completed and _failures.is_empty() else "FAIL", "checks": _checks, "full_overview": _full_overview, "default_overview": _default_preview_stats, "preview": _preview_stats, "failures": _failures}
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output != null: output.store_string(JSON.stringify(report, "\t") + "\n"); output.close()
	if _completed and _failures.is_empty(): print("TERRAIN_GENERATION_PREVIEW_SMOKE=PASS ", JSON.stringify(report)); quit(0)
	else: push_error("TERRAIN_GENERATION_PREVIEW_SMOKE=FAIL " + "; ".join(_failures)); quit(1)
