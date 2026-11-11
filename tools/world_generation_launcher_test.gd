extends SceneTree

## Run: tools/run_tests.ps1 -Tests world_generation_launcher_test.
## Windowed, with --capture, also saves the generation selector and world cards.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const MENU := preload("res://scenes/StartMenu.tscn")
const PIN := preload("res://tools/world_generation_pin.gd")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const STORE := preload("res://scripts/Terrain/Generation/terrain_generation_preview_store.gd")
const SAMPLER := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const LEDGER := preload("res://scripts/Terrain/Generation/WorldGenerationBridge.cs")
const LANDFORMS_SETTING := "ringworld/runtime/surface_landforms_v7"
const LUNG_SETTING := "ringworld/runtime/surface_weather_lung_v8"

class FixtureSession extends Node:
	var active := {}
	var cards := []
	func get_active_world_settings() -> Dictionary: return active.duplicate(true)
	func consume_new_world_request() -> Dictionary: return {}
	func consume_load_selected_world_request() -> bool: return false
	func get_live_menu_slot() -> int: return 0
	func get_most_recent_slot() -> int: return 1
	func slot_exists(slot: int) -> bool: return slot <= cards.size()
	func get_slot_metadata(slot: int) -> Dictionary: return cards[slot - 1]

var _session: FixtureSession
var _failure := "launcher test stopped before completing its checks"
var _latest := 1
var _fixture := ""
var _capture_dir := ""
var _capture_requested := false

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalGenerationLauncher"
	_session = FixtureSession.new()
	_session.name = "WorldSession"
	root.add_child(_session)
	var stamp := "%d-%d" % [OS.get_process_id(), Time.get_ticks_usec()]
	_fixture = "res://.godot/world-generation-launcher-%s" % stamp
	_capture_requested = "--capture" in OS.get_cmdline_user_args()
	_capture_dir = "res://.godot/audit_baseline/common/t1/launcher-%s" % stamp
	var bridge := LEDGER.new()
	_latest = int(bridge.get("LatestFrozen"))
	var passed: bool = await _test()
	var absolute := ProjectSettings.globalize_path(_fixture)
	if FileAccess.file_exists(absolute + "/preview.tres"):
		DirAccess.remove_absolute(absolute + "/preview.tres")
	if DirAccess.dir_exists_absolute(absolute): DirAccess.remove_absolute(absolute)
	_session.free()
	if prior != null: prior.name = "WorldSession"
	if not passed:
		push_error("WORLD_GENERATION_LAUNCHER=FAIL " + _failure)
		quit(1)
		return
	print("WORLD_GENERATION_LAUNCHER=PASS")
	quit(0)

func _test() -> bool:
	ProjectSettings.set_setting(LANDFORMS_SETTING, true)
	ProjectSettings.set_setting(LUNG_SETTING, true)
	var launcher := _new_launcher()
	var generation: OptionButton = launcher.get("generation_selector")
	var landforms: CheckBox = launcher.get("landforms_toggle")
	var lung: CheckBox = launcher.get("weather_lung_toggle")
	if not _expect(generation.get_selected_id() == _latest and not generation.disabled and not landforms.visible and not lung.visible,
			"new v9 selection did not hide the v7/v8 rows"):
		return false
	if OS.is_debug_build() and not _expect(launcher.find_child("WorldReviewStatus", true, false).text == "No open features", "missing debug review empty state"):
		return false
	var contrast: SpinBox = launcher.get("continental_contrast_spin")
	contrast.value = 1.73
	if not _expect(bool(launcher.call("_prepare_runtime_surface_generation", true)), "default v9 recipe was rejected"):
		return false
	var v9_settings: Dictionary = launcher.call("_build_selected_slot_settings")
	var v9_json := str(v9_settings["surface_generation_recipe_json"])
	var v9: Dictionary = JSON.parse_string(v9_json)
	if not _expect(v9["generator_id"] == RECIPE.WORLD_GENERATOR_ID and v9["world_generation"] == _latest and v9["review_features"] == "",
			"default world does not use the latest frozen recipe"):
		return false
	if not _expect(PackedFloat32Array([v9["continental_contrast"]]) == PackedFloat32Array([v9_settings["continental_contrast"]]), "contrast was not written to both recipe and mirror"):
		return false
	if _capture_requested and not await _capture_selector(launcher, generation): return false
	var legacy := generation.get_item_index(0)
	generation.select(legacy)
	generation.item_selected.emit(legacy)
	launcher.call("_update_readout", (launcher.get("size_slider") as HSlider).value)
	if not _expect(generation.get_selected_id() == 0 and landforms.visible and landforms.button_pressed and lung.visible and lung.button_pressed,
			"explicit Legacy selection was lost or did not restore the legacy controls"):
		return false
	launcher.free()
	if not _check_legacy_defaults(): return false
	if not _check_preview(): return false
	if not _check_stored(v9_settings): return false
	return await _check_cards(v9_settings)

func _new_launcher(target := -1, review := "") -> Node:
	PIN.pin(target, review)
	var launcher := LAUNCHER.instantiate()
	launcher.set("preview_recipe_path", _fixture + "/preview.tres")
	root.add_child(launcher)
	PIN.unpin()
	if _session.active.is_empty():
		launcher.call("_apply_new_world_request", {"preset": "standard", "terrain_seed": 1337})
	return launcher

func _check_legacy_defaults() -> bool:
	ProjectSettings.set_setting(LANDFORMS_SETTING, false)
	var launcher := _new_launcher()
	if not _prepare_equals(launcher, RECIPE.create_sites_default()): return false
	(launcher.get("climate_machines_toggle") as CheckBox).button_pressed = true
	if not _prepare_equals(launcher, RECIPE.create_climate_default()): return false
	launcher.free()
	ProjectSettings.set_setting(LANDFORMS_SETTING, true)
	ProjectSettings.set_setting(LUNG_SETTING, false)
	launcher = _new_launcher()
	if not _prepare_equals(launcher, RECIPE.create_landform_default()): return false
	launcher.free()
	ProjectSettings.set_setting(LUNG_SETTING, true)
	launcher = _new_launcher(0, "test.ignored_review")
	if not _prepare_equals(launcher, RECIPE.create_weather_lung_default()): return false
	launcher.free()
	return true

func _prepare_equals(launcher: Node, expected: TerrainGenerationRecipe) -> bool:
	var bridge := SAMPLER.new()
	var configured := bool(bridge.call("Configure", expected.to_normalized_dictionary()))
	var json := str(bridge.get("CanonicalJson"))
	bridge.call("Release")
	return _expect(configured and bool(launcher.call("_prepare_runtime_surface_generation", true)) and str(launcher.get("pending_surface_generation_recipe_json")) == json,
			"legacy recipe differs from the canonical %s" % expected.generator_id)

func _check_preview() -> bool:
	var preview := RECIPE.new()
	preview.land_balance = 0.125
	if not _expect(STORE.save_recipe(preview, _fixture + "/preview.tres").is_empty(), "could not save the isolated preview fixture"):
		return false
	var launcher := _new_launcher()
	if not _expect(bool(launcher.call("_prepare_runtime_surface_generation", true)), "valid legacy preview could not be promoted to v9"):
		return false
	var overlaid: Dictionary = JSON.parse_string(str(launcher.get("pending_surface_generation_recipe_json")))
	if not _expect(overlaid["land_balance"] == preview.land_balance and overlaid["hill_amplitude_m"] == RECIPE.create_weather_lung_default().hill_amplitude_m,
			"v9 promotion lost the preview shape or omitted v8 defaults"):
		return false
	launcher.set("use_preview_recipe", false)
	if not _expect(bool(launcher.call("_prepare_runtime_surface_generation", true)), "preview-free recipe could not be prepared"):
		return false
	var clean: Dictionary = JSON.parse_string(str(launcher.get("pending_surface_generation_recipe_json")))
	launcher.free()
	return _expect(clean["land_balance"] == RECIPE.create_weather_lung_default().land_balance, "preview-free launch used the preview recipe")

func _check_stored(settings: Dictionary) -> bool:
	_session.active = settings.duplicate(true)
	var original := str(settings["surface_generation_recipe_json"])
	var launcher := _new_launcher(0)
	var generation: OptionButton = launcher.get("generation_selector")
	if not _expect(generation.get_selected_id() == _latest and generation.disabled and bool(launcher.call("_prepare_runtime_surface_generation", true)),
			"saved v9 generation was not locked or did not load"):
		return false
	var resaved: Dictionary = launcher.call("_build_selected_slot_settings")
	if not _expect(str(resaved["surface_generation_recipe_json"]) == original and resaved["continental_contrast"] == settings["continental_contrast"],
			"stored v9 recipe or mirror changed on resave"):
		return false
	launcher.free()
	_session.active["continental_contrast"] = float(settings["continental_contrast"]) + 0.0000003
	launcher = _new_launcher()
	if not _expect(not bool(launcher.call("_prepare_runtime_surface_generation", true)) and "Continental contrast differs" in (launcher.get("warning_label") as Label).text,
			"stored contrast mismatch was not rejected by float32 bits"):
		return false
	launcher.free()
	_session.active = settings.duplicate(true)
	_session.active["surface_generation_recipe_json"] = original.replace('"world_generation":%d' % _latest, '"world_generation":%d' % (_latest + 1))
	launcher = _new_launcher()
	if not _expect(not bool(launcher.call("_prepare_runtime_surface_generation", true)) and "is unavailable" in (launcher.get("warning_label") as Label).text,
			"unavailable stored generation did not report a ledger error"):
		return false
	launcher.free()
	_session.active.clear()
	return true

func _check_cards(settings: Dictionary) -> bool:
	var v8 := settings.duplicate(true)
	var bridge := SAMPLER.new()
	if not _expect(bool(bridge.call("Configure", RECIPE.create_weather_lung_default().to_normalized_dictionary())), "could not create v8 card fixture"):
		return false
	v8["surface_generation_recipe_json"] = str(bridge.get("CanonicalJson"))
	bridge.call("Release")
	var review := settings.duplicate(true)
	review["surface_generation_recipe_json"] = str(settings["surface_generation_recipe_json"]).replace('"review_features":""', '"review_features":"test.open,test.water"')
	_session.cards = [
		{"name": "Legacy reference", "generation_settings": v8},
		{"name": "Certified baseline", "generation_settings": settings},
		{"name": "Review example", "generation_settings": review},
	]
	var menu := MENU.instantiate()
	menu.set("freeze_cover", true)
	root.add_child(menu)
	menu.call("_show_worlds")
	var cards: Array = menu.get("_cards")
	if not _expect(cards.size() == 3 and (cards[0]["kicker"] as Label).is_visible_in_tree() and (cards[0]["kicker"] as Label).text.ends_with("GEN 1") \
			and (cards[1]["kicker"] as Label).is_visible_in_tree() and (cards[1]["kicker"] as Label).text.ends_with("GEN %d" % _latest),
			"v8 or v9 card lacks a visible generation kicker"):
		return false
	if not _expect((cards[2]["kicker"] as Label).text.ends_with("GEN %d · REVIEW" % _latest) and (cards[2]["title"] as Label).text == "Review example",
			"review card did not preserve its title or badge"):
		return false
	if _capture_requested and not await _capture_png("world_cards.png"): return false
	menu.call("_expand_card", 3)
	menu.call("_toggle_details")
	var details: Label = cards[2]["details"]
	if not _expect(details.visible and "test.open,test.water" in details.text, "expanded review card lacks the stored IDs"):
		return false
	var corrupt := settings.duplicate(true)
	var corrupt_json := str(settings["surface_generation_recipe_json"])
	corrupt_json = corrupt_json.replace('"world_generation":%d' % _latest, '"world_generation":[]')
	corrupt["surface_generation_recipe_json"] = corrupt_json.replace('"review_features":""', '"review_features":[]')
	_session.cards[0] = {"name": "Corrupt metadata fixture", "generation_settings": corrupt}
	menu.call("_show_worlds")
	var passed := _expect((cards[0]["kicker"] as Label).text.ends_with("GEN ?") and (cards[1]["kicker"] as Label).text.ends_with("GEN %d" % _latest),
			"malformed v9 metadata did not show an unknown generation or broke the adjacent card")
	menu.free()
	return passed

func _capture_selector(launcher: Node, generation: OptionButton) -> bool:
	if not _expect(RenderingServer.get_rendering_device() != null, "--capture requires windowed Vulkan"): return false
	launcher.call("_open_advanced_knobs")
	await process_frame
	var scroll := generation.get_parent().get_parent() as ScrollContainer
	scroll.ensure_control_visible(generation)
	await process_frame
	var review := launcher.find_child("WorldReviewStatus", true, false) as Label
	var review_visible := review != null and review.is_visible_in_tree()
	if review_visible:
		scroll.ensure_control_visible(review)
		await process_frame
	var popup := launcher.get("advanced_popup") as PopupPanel
	var root_bounds := Rect2i(Vector2i.ZERO, root.size)
	var popup_root_bounds := Rect2i(popup.position, popup.size)
	var popup_bounds := Rect2(Vector2.ZERO, Vector2(popup.size))
	var scroll_bounds := scroll.get_global_rect()
	var generation_bounds := generation.get_global_rect()
	var review_bounds := review.get_global_rect() if review_visible else Rect2()
	var geometry := "root=%s popup_root=%s scroll=%s generation=%s review=%s" % [root_bounds, popup_root_bounds, scroll_bounds, generation_bounds, review_bounds]
	print("WORLD_GENERATION_LAUNCHER_GEOMETRY " + geometry)
	if not _expect(root_bounds.encloses(popup_root_bounds) and popup_bounds.encloses(scroll_bounds) \
			and scroll_bounds.encloses(generation_bounds) and popup_bounds.encloses(generation_bounds) \
			and (not review_visible or (scroll_bounds.encloses(review_bounds) and popup_bounds.encloses(review_bounds))),
			"advanced generation controls are clipped: " + geometry):
		await _capture_png("generation_selector_clipped.png")
		return false
	generation.show_popup()
	await process_frame
	var dropdown := generation.get_popup()
	var dropdown_root_bounds := Rect2i(dropdown.position, dropdown.size)
	print("WORLD_GENERATION_LAUNCHER_GEOMETRY dropdown_root=%s" % str(dropdown_root_bounds))
	if not _expect(dropdown.visible and root_bounds.encloses(dropdown_root_bounds), "generation selector dropdown is hidden or clipped: %s" % str(dropdown_root_bounds)):
		await _capture_png("generation_dropdown_clipped.png")
		return false
	var passed := await _capture_png("generation_selector.png")
	launcher.call("_close_advanced_knobs")
	return passed

func _capture_png(name: String) -> bool:
	for frame in range(20): await process_frame
	await RenderingServer.frame_post_draw
	var directory := ProjectSettings.globalize_path(_capture_dir)
	if not _expect(DirAccess.make_dir_recursive_absolute(directory) == OK, "could not create the capture directory"): return false
	var output := directory.path_join(name)
	if not _expect(root.get_texture().get_image().save_png(output) == OK, "could not save " + name): return false
	print("WORLD_GENERATION_LAUNCHER_CAPTURE " + output)
	return true

func _expect(condition: bool, message: String) -> bool:
	if not condition: _failure = message
	return condition
