extends SceneTree
# Run: tools/run_tests.ps1 -Tests pda_debug_ui_test -Label pda_debug -Cache reuse.
# Optional windowed -- --capture writes .godot/pda_debug/panel.png; no model or world is loaded.
const TEST := preload("res://tools/PdaDebugUiTest.cs")

func _init() -> void:
	create_timer(60.0).timeout.connect(func(): push_error("PDA_DEBUG_UI=FAIL watchdog"); quit(1))
	call_deferred("_run")

func _run() -> void:
	var capture := OS.get_cmdline_user_args().has("--capture")
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
		var folder := ProjectSettings.globalize_path("res://.godot/pda_debug")
		DirAccess.make_dir_recursive_absolute(folder)
		var error := root.get_texture().get_image().save_png(folder.path_join("panel.png"))
		if error != OK:
			push_error("PDA_DEBUG_UI=FAIL capture error %d" % error)
			code = 1
	if code == 0:
		print("PDA_DEBUG_UI_WRAPPER=PASS")
	else:
		push_error("PDA_DEBUG_UI_WRAPPER=FAIL exit=%d" % code)
	quit(code)
