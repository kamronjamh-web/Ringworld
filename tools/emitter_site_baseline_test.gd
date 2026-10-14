extends SceneTree

const TEST := preload("res://tools/EmitterSiteBaselineGoldenTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"))
	runner = null
	if code == 0:
		print("EMITTER_SITE_BASELINE_WRAPPER=PASS")
	else:
		push_error("EMITTER_SITE_BASELINE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
