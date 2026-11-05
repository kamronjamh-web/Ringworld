extends SceneTree
# Authoring wrapper first forces editor reimport, then invokes this baker with distinct .godot staging outputs.
const BAKER := preload("res://tools/PrepareTreeAssets.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var output := ""
	var manifest := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="):
			output = arg.trim_prefix("--out=")
		elif arg.begins_with("--manifest="):
			manifest = arg.trim_prefix("--manifest=")
		else:
			push_error("PREPARE_TREE_ASSETS_WRAPPER=FAIL unknown argument: " + arg)
			quit(1)
			return
	if output.is_empty() or manifest.is_empty():
		push_error("PREPARE_TREE_ASSETS_WRAPPER=FAIL --out and --manifest are required")
		quit(1)
		return
	var baker = BAKER.new()
	var code: int = baker.Run(output, manifest)
	baker = null
	if code == 0:
		print("PREPARE_TREE_ASSETS_WRAPPER=PASS")
	else:
		push_error("PREPARE_TREE_ASSETS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
