extends RefCounted

const GENERATION_SETTING := "ringworld/runtime/surface_world_generation"
const REVIEW_SETTING := "ringworld/runtime/surface_review_features"

static var _previous: Array[Dictionary] = []

## Pin before adding the launcher to the tree. Its initial selection is captured in _ready.
static func pin(generation: int, review := "") -> void:
	_previous.append({
		GENERATION_SETTING: ProjectSettings.get_setting(GENERATION_SETTING, null),
		REVIEW_SETTING: ProjectSettings.get_setting(REVIEW_SETTING, null),
	})
	ProjectSettings.set_setting(GENERATION_SETTING, generation)
	ProjectSettings.set_setting(REVIEW_SETTING, review)

static func unpin() -> void:
	if _previous.is_empty(): return
	var settings: Dictionary = _previous.pop_back()
	for key in settings:
		ProjectSettings.set_setting(key, settings[key])
