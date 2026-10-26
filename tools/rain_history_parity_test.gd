extends SceneTree
# Run: Godot --headless --path . --script res://tools/rain_history_parity_test.gd
const TEST := preload("res://tools/RainHistoryParityTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RAIN_HISTORY_PARITY_WRAPPER=PASS")
	else:
		push_error("RAIN_HISTORY_PARITY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
