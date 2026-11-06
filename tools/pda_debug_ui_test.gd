extends SceneTree
# Run: tools/run_tests.ps1 -Tests pda_debug_ui_test -Label pda_debug -Cache reuse.
# Optional windowed -- --capture writes .godot/pda_debug/panel.png; no model or world is loaded.
# --capture-dir res://.godot/<task> selects a contained directory and refuses an existing panel.png.
const TEST := preload("res://tools/PdaDebugUiTest.cs")

func _init() -> void:
	create_timer(60.0).timeout.connect(func(): push_error("PDA_DEBUG_UI=FAIL watchdog"); quit(1))
	call_deferred("_run")

func _run() -> void:
	var arguments := OS.get_cmdline_user_args()
	var capture := arguments.has("--capture")
	var directory_index := arguments.find("--capture-dir")
	var explicit_directory := directory_index >= 0
	var folder := ProjectSettings.globalize_path("res://.godot/pda_debug")
	if explicit_directory:
		if directory_index + 1 >= arguments.size() or arguments[directory_index + 1].begins_with("--"):
			push_error("PDA_DEBUG_UI=FAIL --capture-dir requires a directory")
			quit(1)
			return
		folder = ProjectSettings.globalize_path(arguments[directory_index + 1]).simplify_path().replace("\\", "/").trim_suffix("/")
		var godot_root := ProjectSettings.globalize_path("res://.godot").simplify_path().replace("\\", "/").trim_suffix("/")
		if not _contained_capture_directory(folder, godot_root):
			push_error("PDA_DEBUG_UI=FAIL capture directory must stay inside project .godot without links")
			quit(1)
			return
		var target_directory := DirAccess.open(folder)
		if FileAccess.file_exists(folder.path_join("panel.png")) or (target_directory != null and target_directory.is_link("panel.png")):
			push_error("PDA_DEBUG_UI=FAIL capture destination already exists; preserving it")
			quit(1)
			return
	if capture and DisplayServer.get_name() == "headless":
		push_error("PDA_DEBUG_UI=FAIL capture requires a windowed run")
		quit(1)
		return
	var runner = TEST.new()
	var code: int = runner.Run(root, capture)
	runner = null
	if code == 0 and capture:
		await process_frame
		await process_frame
		await RenderingServer.frame_post_draw
		var error := DirAccess.make_dir_recursive_absolute(folder)
		if error == OK and explicit_directory and FileAccess.file_exists(folder.path_join("panel.png")):
			push_error("PDA_DEBUG_UI=FAIL capture destination appeared during the run; preserving it")
			error = ERR_ALREADY_EXISTS
		if error == OK:
			error = root.get_texture().get_image().save_png(folder.path_join("panel.png"))
		if error != OK:
			push_error("PDA_DEBUG_UI=FAIL capture error %d" % error)
			code = 1
	if code == 0:
		print("PDA_DEBUG_UI_WRAPPER=PASS")
	else:
		push_error("PDA_DEBUG_UI_WRAPPER=FAIL exit=%d" % code)
	quit(code)

func _contained_capture_directory(folder: String, godot_root: String) -> bool:
	if not folder.is_absolute_path() or not (folder == godot_root or folder.begins_with(godot_root + "/")):
		return false
	var current := folder
	while current == godot_root or current.begins_with(godot_root + "/"):
		var parent := DirAccess.open(current.get_base_dir())
		if parent != null and parent.is_link(current.get_file()):
			return false
		if current == godot_root:
			return true
		current = current.get_base_dir()
	return false
