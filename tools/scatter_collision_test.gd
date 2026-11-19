extends SceneTree

## T5-8: the scatter collider pool (boulders convex, outcrops triangle mesh), see tools/ScatterCollisionTest.cs. No rendering needed: run headless.
## godot --headless --path . --script res://tools/scatter_collision_test.gd   (or tools/run_tests.ps1 -Tests scatter_collision_test; user arg --radius=0 is the can-it-fail run)
const FIXTURE := preload("res://tools/ScatterCollisionTest.cs")
var fixture
var frames := 0

func _initialize() -> void:
	fixture = FIXTURE.new()
	root.add_child(fixture)
	fixture.call_deferred("Start")

func _process(_delta: float) -> bool:
	frames += 1
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("SCATTER_COLLISION=" + result)
			quit(0)
		else:
			push_error("SCATTER_COLLISION=" + result)
			quit(1)
		return true
	if frames > 40000:
		push_error("SCATTER_COLLISION=FAIL timeout")
		quit(1)
	return false
