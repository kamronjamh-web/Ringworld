extends SceneTree

## T4-4b: generates (--write after --) or verifies (default) materials/biomes/alpine_rock_detail_normal.png from alpine_rock_albedo.png (see tools/GroundRockDetailNormal.cs). Headless.
const TOOL := preload("res://tools/GroundRockDetailNormal.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TOOL.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("GROUND_ROCK_DETAIL_NORMAL_WRAPPER=PASS")
	else:
		push_error("GROUND_ROCK_DETAIL_NORMAL_WRAPPER=FAIL exit=%d" % code)
	quit(code)
