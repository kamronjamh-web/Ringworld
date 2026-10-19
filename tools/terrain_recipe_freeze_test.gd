extends SceneTree

## Bit-identity freeze for terrain recipes v4-v6. `-- --capture` writes the golden once from a known-good build and never overwrites it.
const TEST := preload("res://tools/TerrainRecipeFreezeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"))
	runner = null
	if code == 0:
		print("TERRAIN_RECIPE_FREEZE_WRAPPER=PASS")
	else:
		push_error("TERRAIN_RECIPE_FREEZE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
