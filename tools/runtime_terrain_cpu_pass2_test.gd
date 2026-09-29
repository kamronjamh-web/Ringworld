extends SceneTree

const TEST := preload("res://tools/RuntimeTerrainCpuPass2Test.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"))
	runner = null
	if code == 0:
		print("RUNTIME_TERRAIN_CPU_PASS2_WRAPPER=PASS")
	else:
		push_error("RUNTIME_TERRAIN_CPU_PASS2_WRAPPER=FAIL exit=%d" % code)
	quit(code)
