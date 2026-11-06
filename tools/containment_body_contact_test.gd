extends SceneTree
# Headless: containment field contact ripples (body-contact adapter) and WO10 vacuum-side audio curves.
# Run: Godot --headless --path . --script res://tools/containment_body_contact_test.gd. Prints CONTAINMENT_BODY_CONTACT=PASS.
const TEST := preload("res://tools/ContainmentBodyContactTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("CONTAINMENT_BODY_CONTACT_WRAPPER=PASS")
	else:
		push_error("CONTAINMENT_BODY_CONTACT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
