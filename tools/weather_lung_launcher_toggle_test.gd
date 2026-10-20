extends SceneTree

# S4c of docs/weather-lung-integration-plan.md: the launcher toggle for the Weather Lung (v8) recipe.
# - ON by default (project setting ringworld/runtime/surface_weather_lung_v8), enabled only for a NEW Terrain3D runtime-generation world with the landforms toggle ON.
# - With it ON the world is ringworld_surface_weather_lung_v8: exactly the v7 field set, the climate toggle still drives climate_machines.
# - With it OFF (or landforms OFF) the world keeps the v7 / v5 / v6 recipes unchanged; existing recipes (stored JSON) still configure and stay canonical.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const PREVIEW := preload("res://scripts/Terrain/Generation/terrain_generation_preview.gd")

var failures := 0

func _init() -> void:
	call_deferred("_run")

func _fail(message: String) -> void:
	push_error("WEATHER_LUNG_LAUNCHER_FAIL " + message)
	failures += 1

func _generator_id(launcher: Node) -> String:
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		return "<prepare failed>"
	var parsed: Variant = JSON.parse_string(str(launcher.get("pending_surface_generation_recipe_json")))
	return str((parsed as Dictionary).get("generator_id", "")) if parsed is Dictionary else "<unparsable>"

func _json(launcher: Node) -> String:
	launcher.call("_prepare_runtime_surface_generation", true)
	return str(launcher.get("pending_surface_generation_recipe_json"))

func _run() -> void:
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	var climate: CheckBox = launcher.get("climate_machines_toggle")
	var landforms: CheckBox = launcher.get("landforms_toggle")
	var lung: CheckBox = launcher.get("weather_lung_toggle")
	if climate == null or landforms == null or lung == null:
		_fail("launcher is missing a toggle (climate %s landforms %s weather lung %s)" % [climate, landforms, lung])
		quit(1)
		return
	var selector: OptionButton = launcher.get("terrain_backend_selector")

	# Not a runtime-generation world: disabled and cleared.
	selector.select(1)
	selector.emit_signal("item_selected", 1)
	if not lung.disabled or lung.button_pressed:
		_fail("toggle must be disabled and cleared outside runtime generation")

	# New runtime-generation world: enabled and ON by default (landforms default ON).
	selector.select(2)
	selector.emit_signal("item_selected", 2)
	if lung.disabled or not lung.button_pressed:
		_fail("toggle should be enabled and ON by default for a new runtime-generation world (disabled=%s pressed=%s)" % [lung.disabled, lung.button_pressed])
	if not landforms.button_pressed:
		_fail("landforms toggle should default ON")

	# ON: v8, climate toggle drives climate_machines, and v8 is exactly v7 plus the id.
	var v8_json := {}
	for climate_on in [false, true]:
		climate.button_pressed = climate_on
		var json := _json(launcher)
		var parsed: Variant = JSON.parse_string(json)
		var dict: Dictionary = parsed if parsed is Dictionary else {}
		if str(dict.get("generator_id", "")) != RECIPE.WEATHER_LUNG_GENERATOR_ID:
			_fail("Weather Lung ON world must be v8 (climate %s): %s" % [climate_on, json.left(120)])
		if int(dict.get("climate_machines", -1)) != int(climate_on):
			_fail("v8 climate_machines should follow the climate toggle (%s): got %s" % [climate_on, str(dict.get("climate_machines", "missing"))])
		v8_json[climate_on] = json
	if v8_json.size() == 2 and v8_json[false] == v8_json[true]:
		_fail("v8 recipes with and without climate machines must differ")

	# Turning the Weather Lung OFF returns to v7 with identical other fields.
	climate.button_pressed = false
	lung.button_pressed = false
	var v7_json := _json(launcher)
	if _generator_id(launcher) != RECIPE.LANDFORM_GENERATOR_ID:
		_fail("Weather Lung OFF world must be v7, got %s" % _generator_id(launcher))
	if v8_json.has(false) and v8_json[false].replace(RECIPE.WEATHER_LUNG_GENERATOR_ID, RECIPE.LANDFORM_GENERATOR_ID) != v7_json:
		_fail("v7 and v8 pending recipes differ beyond the generator id")

	# Climate toggle still maps to climate_machines on v7 (unchanged behaviour with the Weather Lung OFF).
	climate.button_pressed = true
	var v7_climate: Variant = JSON.parse_string(_json(launcher))
	if not (v7_climate is Dictionary) or int((v7_climate as Dictionary).get("climate_machines", -1)) != 1 or str((v7_climate as Dictionary).get("generator_id", "")) != RECIPE.LANDFORM_GENERATOR_ID:
		_fail("v7 climate toggle no longer maps to climate_machines")
	climate.button_pressed = false

	# Landforms OFF: the Weather Lung toggle is disabled and cleared, and the world is v5 (never v8).
	lung.button_pressed = true
	landforms.button_pressed = false
	if not lung.disabled or lung.button_pressed:
		_fail("toggle must be disabled and cleared while landforms are OFF (disabled=%s pressed=%s)" % [lung.disabled, lung.button_pressed])
	var off_json := _json(launcher)
	if off_json.contains("weather_lung") or off_json.contains("landform_v7") or not off_json.contains("ringworld_surface_sites_v5"):
		_fail("landforms OFF must produce the v5 recipe: %s" % off_json.left(120))
	climate.button_pressed = true
	if not _json(launcher).contains("ringworld_surface_climate_v6"):
		_fail("landforms OFF + climate ON must produce v6")
	climate.button_pressed = false

	# Landforms ON again: the Weather Lung toggle returns to its ON default.
	landforms.button_pressed = true
	if lung.disabled or not lung.button_pressed:
		_fail("toggle should return to ON when landforms come back (disabled=%s pressed=%s)" % [lung.disabled, lung.button_pressed])
	if _generator_id(launcher) != RECIPE.WEATHER_LUNG_GENERATOR_ID:
		_fail("world is not v8 after re-enabling landforms")

	# Even if the previewed recipe is already v8 the toggles are authoritative.
	lung.button_pressed = false
	if _generator_id(launcher) != RECIPE.LANDFORM_GENERATOR_ID:
		_fail("toggle OFF must win over a v8 preview recipe")
	lung.button_pressed = true
	launcher.queue_free()

	# Recipe plumbing: the GDScript recipe validates, normalizes to the v7 field set and round-trips through the C# bridge.
	var recipe8: TerrainGenerationRecipe = RECIPE.create_weather_lung_default()
	var recipe7: TerrainGenerationRecipe = RECIPE.create_landform_default()
	if recipe8.generator_id != RECIPE.WEATHER_LUNG_GENERATOR_ID or not recipe8.validation_errors().is_empty():
		_fail("v8 recipe factory/validation failed: %s" % str(recipe8.validation_errors()))
	var d8 := recipe8.to_normalized_dictionary()
	var d7 := recipe7.to_normalized_dictionary()
	if d8.size() != d7.size():
		_fail("v8 normalized dictionary has a different field set than v7")
	var bridge8 = BRIDGE.new()
	var bridge7 = BRIDGE.new()
	if not bool(bridge8.call("Configure", d8)) or not bool(bridge7.call("Configure", d7)):
		_fail("bridge rejected a recipe: %s %s" % [str(bridge8.get("LastError")), str(bridge7.get("LastError"))])
	else:
		if str(bridge8.get("DefinitionHash")) == str(bridge7.get("DefinitionHash")):
			_fail("v7 and v8 recipe hashes are equal")
		if str(bridge8.get("CanonicalJson")).replace(RECIPE.WEATHER_LUNG_GENERATOR_ID, RECIPE.LANDFORM_GENERATOR_ID) != str(bridge7.get("CanonicalJson")):
			_fail("v8 canonical json differs from v7 beyond the id")
		# Existing worlds: stored canonical JSON of every older recipe still configures and stays canonical (the launcher stores and reloads it untouched).
		for old in [RECIPE.create_sites_default(), RECIPE.create_climate_default(), recipe7]:
			var old_bridge = BRIDGE.new()
			if not bool(old_bridge.call("Configure", old.to_normalized_dictionary())):
				_fail("bridge rejected %s" % old.generator_id)
			else:
				var stored := str(old_bridge.get("CanonicalJson"))
				var again = BRIDGE.new()
				if not bool(again.call("ConfigureJson", stored)) or str(again.get("CanonicalJson")) != stored:
					_fail("stored %s recipe is no longer canonical" % old.generator_id)
				again.call("Release")
			old_bridge.call("Release")
		var v8_stored := str(bridge8.get("CanonicalJson"))
		var v8_again = BRIDGE.new()
		if not bool(v8_again.call("ConfigureJson", v8_stored)) or str(v8_again.get("CanonicalJson")) != v8_stored:
			_fail("stored v8 recipe is not canonical")
		v8_again.call("Release")
		print("WEATHER_LUNG_GD_BRIDGE v7_hash=%s v8_hash=%s" % [str(bridge7.get("DefinitionHash")), str(bridge8.get("DefinitionHash"))])
	bridge8.call("Release")
	bridge7.call("Release")
	var snap := RECIPE.create_weather_lung_default().snapshot()
	if snap.generator_id != RECIPE.WEATHER_LUNG_GENERATOR_ID or snap.climate_machines != 0:
		_fail("snapshot lost the v8 id or the climate default")

	# Preview recipe-level toggle keeps the v8 id and drives climate_machines like v7.
	var preview := PREVIEW.new()
	preview.set("_recipe", RECIPE.create_weather_lung_default())
	preview.call("set_climate_machines", true)
	var on_recipe: Resource = preview.call("get_recipe_snapshot") as Resource
	if on_recipe == null or str(on_recipe.get("generator_id")) != RECIPE.WEATHER_LUNG_GENERATOR_ID or int(on_recipe.get("climate_machines")) != 1:
		_fail("preview climate toggle on a v8 recipe did not set climate_machines")
	preview.call("set_climate_machines", false)
	on_recipe = preview.call("get_recipe_snapshot") as Resource
	if on_recipe == null or str(on_recipe.get("generator_id")) != RECIPE.WEATHER_LUNG_GENERATOR_ID or int(on_recipe.get("climate_machines")) != 0:
		_fail("preview climate toggle off on a v8 recipe did not clear climate_machines")
	preview.free()

	if failures == 0:
		print("WEATHER_LUNG_LAUNCHER_TOGGLE=PASS")
	else:
		push_error("WEATHER_LUNG_LAUNCHER_TOGGLE=FAIL failures=%d" % failures)
	quit(failures)
