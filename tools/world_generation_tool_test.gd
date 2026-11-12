extends SceneTree

# Run: tools/run_tests.ps1 -Tests world_generation_tool_test
const TOOL := preload("res://tools/new_generation_world.gd")
const SESSION := preload("res://scripts/world_session.gd")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const PREVIEW := preload("res://scripts/Terrain/Generation/terrain_generation_preview_store.gd")

var _error := "tool test stopped before completing its checks"


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	var prior := root.get_node_or_null("WorldSession")
	if prior != null:
		prior.name = "WorldSessionOriginalGenerationTool"
	var session := SESSION.new()
	session.name = "WorldSession"
	session.worlds_root = "user://world_generation_tool_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
	root.add_child(session)
	var passed := _checks(session)
	var fixture := ProjectSettings.globalize_path(session.worlds_root)
	if DirAccess.dir_exists_absolute(fixture) and TOOL._clean_stage(session, fixture) != true and passed:
		passed = _fail("could not remove the isolated test fixture")
	session.free()
	if prior != null:
		prior.name = "WorldSession"
	if passed != true:
		push_error("WORLD_GENERATION_TOOL=FAIL " + _error)
		print("WORLD_GENERATION_TOOL_WRAPPER=FAIL")
		quit(1)
		return
	print("WORLD_GENERATION_TOOL=PASS")
	print("WORLD_GENERATION_TOOL_WRAPPER=PASS")
	quit(0)


func _checks(session: Node) -> bool:
	var directory: String = session.get("worlds_root")
	var created := TOOL.execute(self, session, {"step": "baseline", "slot": 1})
	if created.get("ok") != true or created.get("name") != "g01-baseline-s1337" or created.get("generation") != 1:
		return _fail("baseline creation failed: " + str(created.get("error", created)))
	var stored: Dictionary = session.call("get_slot_metadata", 1)
	var settings: Dictionary = stored.get("generation_settings", {})
	var recipe: Variant = JSON.parse_string(str(settings.get("surface_generation_recipe_json", "")))
	if (stored.get("name") != "g01-baseline-s1337" or not (recipe is Dictionary)
			or recipe.get("generator_id") != TOOL.V9 or recipe.get("world_generation") != 1
			or recipe.get("seed") != 1337):
		return _fail("saved baseline metadata or recipe differs")

	# The launcher can consume a preview, while the tool's expected settings must come from the clean launcher path.
	var preview_path := directory.path_join("preview.tres")
	var preview := RECIPE.create_weather_lung_default()
	preview.warp_amplitude_m = 8000.0
	var saved_preview := PREVIEW.save_recipe(preview, preview_path)
	if not saved_preview.is_empty():
		return _fail("could not write preview fixture: " + saved_preview)
	var expected := TOOL.build_settings(self, session, 1, 1337, "", false, preview_path)
	if expected.get("ok") != true or JSON.parse_string(JSON.stringify(expected.get("settings"), "  ")) != settings:
		return _fail("stored settings do not match the launcher with previews disabled")
	var with_preview := TOOL.build_settings(self, session, 1, 1337, "", true, preview_path)
	if with_preview.get("ok") != true or JSON.parse_string(JSON.stringify(with_preview.get("settings"), "  ")) == settings:
		return _fail("preview fixture did not exercise a distinct launcher settings path")

	if session.call("create_world", 2, "World 1") != true:
		return _fail("could not create ordinary slot fixture")
	var ordinary_path := directory.path_join("slot_2/world.json")
	var ordinary_before := FileAccess.get_file_as_string(ordinary_path)
	for replace in [false, true]:
		var refused := TOOL.execute(self, session, {"step": "baseline", "slot": 2, "replace": replace})
		if not _refused(refused, "occupied" if not replace else "not a managed"):
			return _fail("ordinary slot was not refused: " + str(refused))
	if FileAccess.get_file_as_string(ordinary_path) != ordinary_before:
		return _fail("ordinary slot metadata changed after refusal")

	var marker := FileAccess.open(directory.path_join("slot_1/regions/old-terrain"), FileAccess.WRITE)
	if marker == null:
		return _fail("could not write old terrain fixture")
	marker.store_string("old terrain")
	marker.close()
	var invalid := TOOL.execute(self, session, {"step": "invalid", "slot": 1, "replace": true, "generation": 99})
	if invalid.get("ok") != false or not FileAccess.file_exists(directory.path_join("slot_1/regions/old-terrain")):
		return _fail("invalid replacement changed the existing slot")
	var replaced := TOOL.execute(self, session, {"step": "replacement", "slot": 1, "seed": 42, "replace": true})
	if (replaced.get("ok") != true or replaced.get("name") != "g01-replacement-s42"
			or FileAccess.file_exists(directory.path_join("slot_1/regions/old-terrain"))):
		return _fail("managed replacement failed or carried over old terrain: " + str(replaced))

	var unknown := TOOL.execute(self, session, {"step": "baseline", "slot": 3, "review": "unknown.feature"})
	if not _refused(unknown, "Unknown review feature"):
		return _fail("unknown review did not return the ledger error: " + str(unknown))
	var none := TOOL.execute(self, session, {"step": "baseline", "slot": 3, "review": ""})
	if not _refused(none, "Choose at least one review feature."):
		return _fail("missing review feature did not ask for a review feature: " + str(none))
	# The production ledger now has an open feature, so the "no open features" path needs a ledger without one.
	var bridge := TOOL.BRIDGE.new()
	if (bridge.call("ListOpenFeatures") as PackedStringArray).is_empty():
		return _fail("production ledger has no open feature, so the choose-a-feature path is not covered")

	var orphan_path := directory.path_join("slot_3")
	if DirAccess.make_dir_absolute(ProjectSettings.globalize_path(orphan_path)) != OK:
		return _fail("could not make the orphaned slot fixture")
	var recovery := FileAccess.open(orphan_path.path_join("world.json.tmp"), FileAccess.WRITE)
	if recovery == null:
		return _fail("could not write the recovery fixture")
	recovery.store_string('{"slot":3,"name":"review-baseline-s1337","generation_settings":{}}')
	recovery.close()
	session.set("active_slot", 3)
	var before := _snapshot(directory)
	if before.is_empty():
		return _fail("could not read the dry run fixture")
	var dry := TOOL.execute(self, session, {"step": "dry", "slot": 1, "replace": true, "dry_run": true})
	var orphan := TOOL.execute(self, session, {"step": "dry", "slot": 3, "replace": true, "dry_run": true})
	if dry.get("ok") != true or not _refused(orphan, "no readable world.json") or before != _snapshot(directory):
		return _fail("dry run changed the listing, recovered metadata, or did not build a valid plan")
	session.set("active_slot", 0)
	if TOOL._clean_stage(session, ProjectSettings.globalize_path(orphan_path)) != true:
		return _fail("could not remove the orphaned fixture")

	if session.call("create_world", 3, "review-baseline-s1337") != true or session.call("rename_slot", 1, "review-other-s42") != true:
		return _fail("could not create review deletion fixtures")
	var deletion := TOOL.execute(self, session, {"step": "baseline", "delete_review": true})
	if (deletion.get("ok") != true or deletion.get("slots") != [3]
			or session.call("slot_exists", 3) != false or session.call("slot_exists", 1) != true
			or FileAccess.get_file_as_string(ordinary_path) != ordinary_before):
		return _fail("review deletion did not limit itself to the chosen step: " + str(deletion))
	return true


func _snapshot(path: String) -> Dictionary:
	var directory := DirAccess.open(path)
	if directory == null:
		return {}
	directory.include_hidden = true
	var files := {}
	for file in directory.get_files():
		files[file] = FileAccess.get_sha256(path.path_join(file))
	for child in directory.get_directories():
		files[child + "/"] = _snapshot(path.path_join(child))
	return files


func _refused(result: Dictionary, message: String) -> bool:
	return result.get("ok") == false and str(result.get("error", "")).contains(message)


func _fail(message: String) -> bool:
	_error = message
	return false
