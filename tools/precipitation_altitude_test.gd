extends SceneTree
# Run: Godot --headless --path . --script res://tools/precipitation_altitude_test.gd
const TEST := preload("res://tools/PrecipitationAltitudeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PRECIP_ALTITUDE_WRAPPER=PASS")
	else:
		push_error("PRECIP_ALTITUDE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
