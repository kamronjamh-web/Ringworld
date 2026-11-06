extends SceneTree
# Headless: atmosphere-shield projector sites (252 towers on the wall tops: transforms, walk footprint data, analytic wall-top collision).
# Run: Godot --headless --path . --script res://tools/atmosphere_projector_sites_test.gd. Prints ATMOSPHERE_PROJECTOR_SITES=PASS.
const TEST := preload("res://tools/AtmosphereProjectorSitesTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("ATMOSPHERE_PROJECTOR_SITES_WRAPPER=PASS")
	else:
		push_error("ATMOSPHERE_PROJECTOR_SITES_WRAPPER=FAIL exit=%d" % code)
	quit(code)
