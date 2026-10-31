extends SceneTree
# Headless: megastorm cloud-history fade math (WO-B). Run: Godot --headless --path . --script res://tools/megastorm_history_fade_test.gd [-- --verbose]
const TEST := preload("res://tools/MegastormHistoryFadeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--verbose"))
	runner = null
	if code == 0:
		print("MEGASTORM_HISTORY_FADE_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_HISTORY_FADE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
