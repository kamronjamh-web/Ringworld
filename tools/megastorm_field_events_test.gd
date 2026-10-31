extends SceneTree
# Headless: megastorm WO8 field publishing seam against a stub sink and provider, shared fixture tools/fixtures/containment_megastorm_fixture.json.
# Run: Godot --headless --path . --script res://tools/megastorm_field_events_test.gd. Prints MEGASTORM_FIELD_EVENTS=PASS.
const TEST := preload("res://tools/MegastormFieldEventsTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("MEGASTORM_FIELD_EVENTS_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_FIELD_EVENTS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
