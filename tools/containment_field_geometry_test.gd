extends SceneTree
# Headless: containment field WO1 geometry core (profile, skirt, arch length, nearest, signed queries, placement, world segments, wall modes, cloud ceiling).
# Run: Godot --headless --path . --script res://tools/containment_field_geometry_test.gd. Prints CONTAINMENT_FIELD_GEOMETRY=PASS.
const TEST := preload("res://tools/ContainmentFieldGeometryTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("CONTAINMENT_FIELD_GEOMETRY_WRAPPER=PASS")
	else:
		push_error("CONTAINMENT_FIELD_GEOMETRY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
