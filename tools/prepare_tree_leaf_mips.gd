extends SceneTree
# TR-7c-PV (preview only): builds the distance-stable leaf mips into res://.godot/tr7c/preview/ (it refuses any folder outside res://.godot/). Writes nothing under resources/.
# Run: tools/run_tests.ps1 -Tests prepare_tree_leaf_mips -Label tr7c
const TOOL := preload("res://tools/PrepareTreeLeafMips.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var out := "res://.godot/tr7c/preview"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="): out = arg.trim_prefix("--out=")
	var tool = TOOL.new()
	var code: int = tool.Run(out, "")
	tool = null
	quit(code)
