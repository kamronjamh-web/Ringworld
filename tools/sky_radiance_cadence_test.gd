extends SceneTree
# Headless: sky radiance cadence decisions (R1-6c). Run: Godot --headless --path . --script res://tools/sky_radiance_cadence_test.gd [-- --verbose]
const TEST := preload("res://tools/SkyRadianceCadenceTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--verbose"))
	runner = null
	if code == 0:
		print("SKY_RADIANCE_CADENCE_WRAPPER=PASS")
	else:
		push_error("SKY_RADIANCE_CADENCE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
