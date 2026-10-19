extends SceneTree

# Phase C2: the launcher toggle for the v6 climate-machine recipe is OFF by default, selects ringworld_surface_climate_v6 for NEW
# runtime-generation worlds only, and leaves the v5 default untouched. Also covers the preview toggle on the preview GDScript.
# v7 (plan Step 6): the hill-country landforms toggle defaults ON from ringworld/runtime/surface_landforms_v7; with it ON the world is
# v7 and the climate toggle drives the climate_machines field. The v5/v6 checks below run with landforms OFF.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const STORE := preload("res://scripts/Terrain/Generation/terrain_generation_preview_store.gd")
const PREVIEW := preload("res://scripts/Terrain/Generation/terrain_generation_preview.gd")

var failures := 0

func _init() -> void:
	call_deferred("_run")

func _fail(message: String) -> void:
	push_error("CLIMATE_LAUNCHER_FAIL " + message)
	failures += 1

func _run() -> void:
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	var toggle: CheckBox = launcher.get("climate_machines_toggle")
	var landforms: CheckBox = launcher.get("landforms_toggle")
	if landforms == null:
		_fail("launcher has no landforms_toggle")
		quit(1)
		return
	if toggle == null:
		_fail("launcher has no climate_machines_toggle")
		quit(1)
		return
	if toggle.button_pressed:
		_fail("climate toggle must be OFF by default")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	selector.select(2)
	selector.emit_signal("item_selected", 2)
	if toggle.disabled:
		_fail("toggle should be enabled for a new runtime-generation world")
	if landforms.disabled or not landforms.button_pressed:
		_fail("landforms toggle should be enabled and ON for a new runtime-generation world")
	landforms.button_pressed = false
	var loaded: Dictionary = STORE.load_recipe()
	var stored: Resource = loaded.get("recipe", null) as Resource
	var stored_id := "" if stored == null else str(stored.get("generator_id"))
	var family_ok := stored == null or stored_id == RECIPE.EROSION_GENERATOR_ID or stored_id == RECIPE.SITES_GENERATOR_ID or stored_id == RECIPE.CLIMATE_GENERATOR_ID or stored_id == RECIPE.LANDFORM_GENERATOR_ID
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("default preparation failed")
	var off_json := str(launcher.get("pending_surface_generation_recipe_json"))
	if off_json.contains("climate_v6"):
		_fail("default (toggle off) world must not be v6")
	if family_ok and not off_json.contains("ringworld_surface_sites_v5"):
		_fail("default world should still be the v5 recipe (stored recipe %s)" % stored_id)
	toggle.button_pressed = true
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("toggle-on preparation failed")
	var on_json := str(launcher.get("pending_surface_generation_recipe_json"))
	if family_ok and not on_json.contains("ringworld_surface_climate_v6"):
		_fail("toggle-on world should use the v6 recipe (stored recipe %s)" % stored_id)
	# Same fields otherwise: only the generator id differs.
	if family_ok and off_json.replace("ringworld_surface_sites_v5", "ringworld_surface_climate_v6") != on_json:
		_fail("v5 and v6 pending recipes differ beyond the generator id")
	toggle.button_pressed = false
	if bool(launcher.call("_prepare_runtime_surface_generation", true)) and str(launcher.get("pending_surface_generation_recipe_json")).contains("climate_v6"):
		_fail("switching the toggle off did not return to v5")
	# Landforms ON: the four combinations with the climate toggle.
	landforms.button_pressed = true
	var v7_ids := {}
	for climate_on in [false, true]:
		toggle.button_pressed = climate_on
		if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
			_fail("landforms preparation failed (climate %s)" % climate_on)
			continue
		var v7_json := str(launcher.get("pending_surface_generation_recipe_json"))
		var v7_parsed: Variant = JSON.parse_string(v7_json)
		var v7_dict: Dictionary = v7_parsed if v7_parsed is Dictionary else {}
		if str(v7_dict.get("generator_id", "")) != RECIPE.LANDFORM_GENERATOR_ID:
			_fail("landforms ON world must be v7 (climate %s): %s" % [climate_on, v7_json.left(120)])
		if int(v7_dict.get("climate_machines", -1)) != int(climate_on):
			_fail("v7 climate_machines should follow the climate toggle (%s): got %s" % [climate_on, str(v7_dict.get("climate_machines", "missing"))])
		v7_ids[climate_on] = v7_json
	if v7_ids.size() == 2 and v7_ids[false] == v7_ids[true]:
		_fail("v7 recipes with and without climate machines must differ")
	# Landforms OFF with climate OFF/ON is the v5/v6 pair already checked above; re-check the opt-out after a v7 round trip.
	landforms.button_pressed = false
	toggle.button_pressed = false
	if bool(launcher.call("_prepare_runtime_surface_generation", true)):
		var back_json := str(launcher.get("pending_surface_generation_recipe_json"))
		if back_json.contains("landform_v7") or back_json.contains("climate_v6"):
			_fail("landforms OFF + climate OFF did not return to v5")
	# Smooth backend: both toggles are disabled and cleared; returning to runtime restores the landforms default.
	selector.select(1)
	selector.emit_signal("item_selected", 1)
	if not toggle.disabled or toggle.button_pressed:
		_fail("toggle should be disabled and cleared for non-runtime backends")
	if not landforms.disabled or landforms.button_pressed:
		_fail("landforms toggle should be disabled and cleared for non-runtime backends")
	selector.select(2)
	selector.emit_signal("item_selected", 2)
	if landforms.disabled or not landforms.button_pressed:
		_fail("landforms toggle should return to its ON default for a new runtime-generation world")
	launcher.queue_free()

	# Preview toggle (recipe level; the preview scene itself needs no window here).
	var preview := PREVIEW.new()
	preview.set("_recipe", RECIPE.create_sites_default())
	preview.call("set_climate_machines", true)
	var after_on: Resource = preview.call("get_recipe_snapshot") as Resource
	if after_on == null or str(after_on.get("generator_id")) != RECIPE.CLIMATE_GENERATOR_ID:
		_fail("preview toggle on did not select v6")
	preview.call("set_climate_machines", false)
	var after_off: Resource = preview.call("get_recipe_snapshot") as Resource
	if after_off == null or str(after_off.get("generator_id")) != RECIPE.SITES_GENERATOR_ID:
		_fail("preview toggle off did not return to v5")
	# v7 preview: the climate toggle drives the climate_machines field and keeps the v7 id.
	preview.set("_recipe", RECIPE.create_landform_default())
	preview.call("set_climate_machines", true)
	var v7_on: Resource = preview.call("get_recipe_snapshot") as Resource
	if v7_on == null or str(v7_on.get("generator_id")) != RECIPE.LANDFORM_GENERATOR_ID or int(v7_on.get("climate_machines")) != 1:
		_fail("preview toggle on did not set climate_machines on v7")
	preview.call("set_climate_machines", false)
	var v7_off: Resource = preview.call("get_recipe_snapshot") as Resource
	if v7_off == null or str(v7_off.get("generator_id")) != RECIPE.LANDFORM_GENERATOR_ID or int(v7_off.get("climate_machines")) != 0:
		_fail("preview toggle off did not clear climate_machines on v7")
	preview.free()

	if failures == 0:
		print("CLIMATE_LAUNCHER_TOGGLE=PASS stored_recipe=%s" % stored_id)
	else:
		push_error("CLIMATE_LAUNCHER_TOGGLE=FAIL failures=%d" % failures)
	quit(failures)
