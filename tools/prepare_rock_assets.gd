extends SceneTree
# T5-6 rock generator wrapper. Args: --out=<res path> --manifest=<res path> [--check]. See tools/prepare_rock_assets.ps1.
const GENERATOR := preload("res://tools/PrepareRockAssets.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var output := "res://resources/rocks/prepared/rock_catalog.bin"
	var manifest := "res://resources/rocks/prepared/rock_catalog.manifest.json"
	var check := false
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="):
			output = arg.trim_prefix("--out=")
		elif arg.begins_with("--manifest="):
			manifest = arg.trim_prefix("--manifest=")
		elif arg == "--check":
			check = true
		else:
			push_error("PREPARE_ROCK_ASSETS_WRAPPER=FAIL unknown argument: " + arg)
			quit(1)
			return
	var generator = GENERATOR.new()
	var code: int = generator.Run(output, manifest, check)
	generator = null
	if code != 0:
		push_error("PREPARE_ROCK_ASSETS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
