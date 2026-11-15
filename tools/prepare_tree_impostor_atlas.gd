extends SceneTree
# TR-7a: writes resources/trees/prepared/lolipop_impostor_atlas.res (+ manifest), the mip table and the contact sheets under .godot/tr7a/.
# Run: tools/use_godot_48.ps1 --headless --script res://tools/prepare_tree_impostor_atlas.gd   (or tools/run_tests.ps1 -Tests prepare_tree_impostor_atlas)
const TOOL := preload("res://tools/PrepareTreeImpostorAtlas.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var out := "res://resources/trees/prepared/lolipop_impostor_atlas.res"
	var scratch := "res://.godot/tr7a"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="): out = arg.trim_prefix("--out=")
		elif arg.begins_with("--scratch="): scratch = arg.trim_prefix("--scratch=")
	var tool = TOOL.new()
	var code: int = tool.Run(out, "res://resources/trees/prepared/lolipop_impostor_atlas.manifest.json", scratch)
	tool = null
	quit(code)
