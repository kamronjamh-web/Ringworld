extends SceneTree
# Writes resources/trees/prepared/lolipop_render_lods.res (+ manifest) and levels A/C under .godot/tr4/.
# Run: tools/use_godot_48.ps1 --headless --script res://tools/prepare_tree_render_lods.gd
const TOOL := preload("res://tools/PrepareTreeRenderLods.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var out := "res://resources/trees/prepared/lolipop_render_lods.res"
	var manifest := "res://resources/trees/prepared/lolipop_render_lods.manifest.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="): out = arg.trim_prefix("--out=")
		elif arg.begins_with("--manifest="): manifest = arg.trim_prefix("--manifest=")
	var tool = TOOL.new()
	var code: int = tool.Run(out, manifest, "res://.godot/tr4/levels", "res://.godot/tr4/triangle_table.md")
	tool = null
	quit(code)
