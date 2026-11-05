extends SceneTree
# Run one fresh process per sample (Vulkan; acquire the shared GPU queue in the caller):
# Godot --path . --script res://tools/imported_tree_asset_profile.gd -- --mode=cooperative --out=res://.godot/tree_profile/run1.json [--impostor]
# --mode=sync measures the blocking catalog API. No settings or saved worlds are loaded.
# --loader=source|prepared-required|auto selects the route before the process catalog is loaded.
# Perf Release export: Ringworld.exe -- --tree-asset-profile --mode=sync --out=user://generated/profiles/sync1.json
const PROFILE := preload("res://tools/ImportedTreeAssetProfile.cs")
var _run_started := false

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	# The exported entry schedules _run as a fallback after set_script() invokes _init().
	if _run_started:
		return
	_run_started = true
	var mode := "cooperative"
	var loader := "auto"
	var output := "user://generated/profiles/imported_tree_%d.json" % Time.get_ticks_usec()
	var impostor := false
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--mode="):
			mode = arg.trim_prefix("--mode=")
		elif arg.begins_with("--out="):
			output = arg.trim_prefix("--out=")
		elif arg.begins_with("--loader="):
			loader = arg.trim_prefix("--loader=")
		elif arg == "--impostor":
			impostor = true
		elif arg == "--tree-asset-profile":
			pass
		else:
			push_error("IMPORTED_TREE_ASSET_PROFILE_WRAPPER=FAIL unknown argument: " + arg)
			quit(1)
			return
	if RenderingServer.get_rendering_device() == null:
		push_error("IMPORTED_TREE_ASSET_PROFILE_WRAPPER=FAIL requires Vulkan/Forward+")
		quit(1)
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	create_timer(300.0).timeout.connect(func():
		push_error("IMPORTED_TREE_ASSET_PROFILE_WRAPPER=FAIL timeout")
		quit(1))
	var profiler = PROFILE.new()
	profiler.BeginProfile(mode, output, impostor, loader)
	while profiler.AdvanceStep():
		await process_frame
	var result := str(profiler.get("Result"))
	profiler = null
	if result == "PASS":
		print("IMPORTED_TREE_ASSET_PROFILE_WRAPPER=PASS")
		quit(0)
	else:
		push_error("IMPORTED_TREE_ASSET_PROFILE_WRAPPER=FAIL " + result)
		quit(1)
