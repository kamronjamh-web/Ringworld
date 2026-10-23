extends SceneTree
## Writes res://resources/menu/showcase_world.json from the launcher's Standard runtime-generation defaults (seed 1337).
## Run through tools/generate_showcase_world.ps1 (isolated APPDATA). After changing SHOWCASE_ID, also regenerate the bundled picture
## with tools/showcase_snapshot_capture.ps1.

const DEFINITION := preload("res://tools/showcase_world_definition.gd")


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("showcase_tools"):
		push_error("Refusing to run: user:// (%s) is not an isolated showcase_tools APPDATA. Use generate_showcase_world.ps1." % user_dir)
		quit(2)
		return
	var built: Dictionary = DEFINITION.build(self)
	if not str(built["error"]).is_empty():
		push_error(str(built["error"]))
		quit(1)
		return
	var file := FileAccess.open(DEFINITION.OUTPUT_PATH, FileAccess.WRITE)
	if file == null:
		push_error("Could not write " + DEFINITION.OUTPUT_PATH)
		quit(1)
		return
	file.store_string(DEFINITION.to_text(built["definition"]))
	file.close()
	print("SHOWCASE_DEFINITION_WRITTEN id=%s path=%s" % [DEFINITION.SHOWCASE_ID, ProjectSettings.globalize_path(DEFINITION.OUTPUT_PATH)])
	quit(0)
