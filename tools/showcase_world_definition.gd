extends RefCounted
## Builds the showcase world definition (res://resources/menu/showcase_world.json): the frozen Standard runtime-generation
## `generation_settings` of a new seed-1337 world, exactly as the launcher would store them for a New World, plus the showcase id.
## Shared by tools/generate_showcase_world.gd (writes the file) and tools/showcase_world_definition_test.gd (guards it).
##
## Bump SHOWCASE_ID whenever the definition changes on purpose, then regenerate the bundled picture
## (tools/showcase_snapshot_capture.ps1): the menu ignores a picture whose showcase_id differs from the definition's.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const PREVIEW_RECIPE_PATH := "user://terrain_generation_preview/recipe_v3.tres"
const SHOWCASE_ID := "standard_1337_v1"
const SHOWCASE_NAME := "Showcase World"
const SHOWCASE_SEED := 1337
const OUTPUT_PATH := "res://resources/menu/showcase_world.json"


## Returns {definition, error}. The launcher must be added to the tree for its _ready, so this needs the running SceneTree.
## A previewed terrain recipe in the user profile would leak into the result, so it refuses to run when one exists.
static func build(tree: SceneTree) -> Dictionary:
	if FileAccess.file_exists(PREVIEW_RECIPE_PATH):
		return {"definition": {}, "error": "A previewed terrain recipe exists in user://; run through an isolated APPDATA."}
	var launcher := LAUNCHER.instantiate()
	tree.root.add_child(launcher)
	launcher.call("_apply_new_world_request", {"preset": "standard", "terrain_seed": SHOWCASE_SEED, "auto_start": false})
	var settings: Dictionary = {}
	var error := ""
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		error = "Launcher rejected the Standard runtime-generation settings: " + str(launcher.get("warning_label").text)
	else:
		settings = launcher.call("_build_selected_slot_settings")
	tree.root.remove_child(launcher)
	launcher.free()
	if not error.is_empty():
		return {"definition": {}, "error": error}
	return {
		"definition": {"showcase_id": SHOWCASE_ID, "name": SHOWCASE_NAME, "generation_settings": settings},
		"error": "",
	}


## Canonical text of a definition (sorted keys), so equal definitions compare equal as strings.
static func to_text(definition: Dictionary) -> String:
	return JSON.stringify(definition, "\t", true) + "\n"
