class_name TerrainGenerationPreviewStore
extends RefCounted

const RECIPE_PATH := "user://terrain_generation_preview/recipe_v3.tres"

static func load_recipe(path: String = RECIPE_PATH) -> Dictionary:
	if path.is_empty() or not (path.begins_with("user://") or path.begins_with("res://")):
		return {"recipe": null, "error": "Recipe path is invalid."}
	if not FileAccess.file_exists(path):
		return {"recipe": null, "error": ""}
	var resource := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE)
	if resource == null or not resource is TerrainGenerationRecipe:
		return {"recipe": null, "error": "Saved recipe is corrupt or incompatible. Reset explicitly to recover."}
	var recipe: TerrainGenerationRecipe = resource
	if (recipe.generator_id != TerrainGenerationRecipe.SITES_GENERATOR_ID and recipe.generator_id != TerrainGenerationRecipe.EROSION_GENERATOR_ID and recipe.generator_id != TerrainGenerationRecipe.DETAIL_GENERATOR_ID and recipe.generator_id != TerrainGenerationRecipe.GENERATOR_ID and recipe.generator_id != TerrainGenerationRecipe.LEGACY_GENERATOR_ID) or recipe.domain_id != TerrainGenerationRecipe.DOMAIN_ID:
		return {"recipe": null, "error": "Saved recipe is incompatible. Reset explicitly to recover."}
	if not recipe.validation_errors().is_empty():
		return {"recipe": null, "error": "Saved recipe is invalid. Reset explicitly to recover."}
	return {"recipe": recipe.snapshot(), "error": ""}

static func save_recipe(recipe: TerrainGenerationRecipe, path: String = RECIPE_PATH) -> String:
	if recipe == null: return "Recipe is missing."
	if path.is_empty() or not (path.begins_with("user://") or path.begins_with("res://")):
		return "Recipe path is invalid."
	var errors := recipe.validation_errors()
	if not errors.is_empty(): return "Recipe is invalid: " + ", ".join(errors)
	var temp_path := path + ".tmp.tres"
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path.get_base_dir())) != OK: return "Could not create recipe directory."
	if FileAccess.file_exists(temp_path):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(temp_path))
	if ResourceSaver.save(recipe.snapshot(), temp_path) != OK: return "Could not write temporary recipe."
	var absolute_path := ProjectSettings.globalize_path(path)
	var absolute_temp := ProjectSettings.globalize_path(temp_path)
	# Rename is the publication point. Do not delete or move an existing recipe:
	# a failed replacement leaves the last known-good destination intact.
	if DirAccess.rename_absolute(absolute_temp, absolute_path) != OK:
		return "Could not publish recipe."
	return ""
