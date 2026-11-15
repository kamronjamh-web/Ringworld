extends SceneTree
# TR-7c: builds the distance-stable leaf mips. Default: stages and promotes resources/trees/prepared/lolipop_leaf_mips.res (+ manifest) once every gate passes; the table goes to res://.godot/tr7c/ship/.
# --preview: the TR-7c-PV behaviour (open-loop and closed-loop textures under res://.godot/tr7c/preview/, nothing under resources/). --out=<folder under res://.godot/> moves the scratch/preview folder.
# Run: tools/run_tests.ps1 -Tests prepare_tree_leaf_mips -Label tr7c
const TOOL := preload("res://tools/PrepareTreeLeafMips.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var out := "res://.godot/tr7c/ship"
	var shipped := "res://resources/trees/prepared/lolipop_leaf_mips.res"
	var manifest := "res://resources/trees/prepared/lolipop_leaf_mips.manifest.json"
	var args := Array(OS.get_cmdline_user_args())
	args.append_array(OS.get_environment("PREPARE_TREE_LEAF_MIPS_ARGS").split(" ", false)) # run_tests.ps1 passes no arguments: the same switches by environment
	for arg in args:
		if arg == "--preview":
			out = "res://.godot/tr7c/preview"
			shipped = ""
			manifest = ""
		elif arg.begins_with("--out="): out = arg.trim_prefix("--out=")
		elif arg.begins_with("--manifest="): manifest = arg.trim_prefix("--manifest=")
	var tool = TOOL.new()
	var code: int = tool.Run(out, "", shipped, manifest)
	tool = null
	quit(code)
