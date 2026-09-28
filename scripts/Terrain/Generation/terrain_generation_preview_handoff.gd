class_name TerrainGenerationPreviewHandoff
extends RefCounted

## One-process authoring handoff. It deliberately contains no save operation:
## exploring a preview must not overwrite the versioned recipe resource.
static var _recipe: TerrainGenerationRecipe
static var _around: int = 0
static var _across: int = 0
static var _recipe_path := ""
static var _save_blocked := false
static var _error := ""

static func stage(recipe: TerrainGenerationRecipe, around: float, across: float, recipe_path: String, save_blocked: bool, error: String) -> void:
	_recipe = recipe.snapshot()
	_around = int(round(around / 2.0)) * 2
	_across = int(round(across / 2.0)) * 2
	_recipe_path = recipe_path
	_save_blocked = save_blocked
	_error = error

static func peek() -> Dictionary:
	if _recipe == null:
		return {}
	return {"recipe": _recipe.snapshot(), "around": _around, "across": _across, "recipe_path": _recipe_path, "save_blocked": _save_blocked, "error": _error}

static func consume() -> Dictionary:
	var result := peek()
	clear()
	return result

static func clear() -> void:
	_recipe = null
	_recipe_path = ""
	_save_blocked = false
	_error = ""
