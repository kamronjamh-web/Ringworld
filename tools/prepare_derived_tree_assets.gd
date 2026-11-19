extends SceneTree
# Writes resources/trees/prepared/lolipop_derived_catalog.res (+ manifest): snags, fallen trees and stumps (indices 30-42) from the prepared base catalog.
# Run: tools/use_godot_48.ps1 --headless --script res://tools/prepare_derived_tree_assets.gd
const TOOL := preload("res://tools/PrepareDerivedTreeAssets.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var out := "res://resources/trees/prepared/lolipop_derived_catalog.res"
	var manifest := "res://resources/trees/prepared/lolipop_derived_catalog.manifest.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="): out = arg.trim_prefix("--out=")
		elif arg.begins_with("--manifest="): manifest = arg.trim_prefix("--manifest=")
	var tool = TOOL.new()
	var code: int = tool.Run(out, manifest)
	tool = null
	quit(code)
