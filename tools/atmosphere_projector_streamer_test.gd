extends SceneTree
# Headless: atmosphere projector streaming pool (observations, hysteresis, wrap, two slots, dwell), scene contract, stand-in transforms.
# Run: Godot --headless --path . --script res://tools/atmosphere_projector_streamer_test.gd. Prints ATMOSPHERE_PROJECTOR_STREAMER=PASS.
const TEST := preload("res://tools/AtmosphereProjectorStreamerTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("ATMOSPHERE_PROJECTOR_STREAMER_WRAPPER=PASS")
	else:
		push_error("ATMOSPHERE_PROJECTOR_STREAMER_WRAPPER=FAIL exit=%d" % code)
	quit(code)
