extends SceneTree
# Headless: gust front debris static helpers (S3 WO-D1). Run: Godot --headless --path . --script res://tools/gust_front_debris_headless_test.gd
const TEST := preload("res://tools/GustFrontDebrisHeadlessTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("GUST_FRONT_DEBRIS_WRAPPER=PASS")
	else:
		push_error("GUST_FRONT_DEBRIS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
