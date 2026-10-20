extends SceneTree

## Bit-identity freeze for terrain recipes v4-v6. `-- --capture` writes the golden once from a known-good build and never overwrites it.
## The v7/v8 freeze (separate golden) runs after it; `-- --capture-v8` writes that golden once.
const TEST := preload("res://tools/TerrainRecipeFreezeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var args := OS.get_cmdline_user_args()
	var code: int = 0
	if args.has("--capture-v8"):
		code = runner.RunV8(true)
	else:
		code = runner.Run(args.has("--capture"))
		if not args.has("--capture"):
			code += runner.RunV8(false)
	runner = null
	if code == 0:
		print("TERRAIN_RECIPE_FREEZE_WRAPPER=PASS")
	else:
		push_error("TERRAIN_RECIPE_FREEZE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
