extends SceneTree

# Phase C2: the launcher toggle for the v6 climate-machine recipe is OFF by default, selects ringworld_surface_climate_v6 for NEW
# runtime-generation worlds only, and leaves the v5 default untouched. Also covers the preview toggle on the preview GDScript.

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
	var loaded: Dictionary = STORE.load_recipe()
	var stored: Resource = loaded.get("recipe", null) as Resource
	var stored_id := "" if stored == null else str(stored.get("generator_id"))
	var family_ok := stored == null or stored_id == RECIPE.EROSION_GENERATOR_ID or stored_id == RECIPE.SITES_GENERATOR_ID or stored_id == RECIPE.CLIMATE_GENERATOR_ID
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
	# Smooth backend: toggle is disabled and cleared.
	selector.select(1)
	selector.emit_signal("item_selected", 1)
	if not toggle.disabled or toggle.button_pressed:
		_fail("toggle should be disabled and cleared for non-runtime backends")
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
	preview.free()

	if failures == 0:
		print("CLIMATE_LAUNCHER_TOGGLE=PASS stored_recipe=%s" % stored_id)
	else:
		push_error("CLIMATE_LAUNCHER_TOGGLE=FAIL failures=%d" % failures)
	quit(failures)
