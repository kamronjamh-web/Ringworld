extends SceneTree

const TEST := preload("res://tools/EmitterSiteBaselineGoldenTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var fixture := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--fixture="): fixture = arg.substr(10)
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"), fixture)
	runner = null
	if code == 0:
		print("EMITTER_SITE_BASELINE_WRAPPER=PASS")
	else:
		push_error("EMITTER_SITE_BASELINE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
