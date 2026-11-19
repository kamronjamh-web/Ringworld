extends SceneTree

## T6-6: the dead-wood collider pool of the understory layer (capsule logs, cylinder snags and stumps), see tools/UnderstoryCollisionTest.cs. No rendering needed: run headless.
## godot --headless --path . --script res://tools/understory_collision_test.gd   (or tools/run_tests.ps1 -Tests understory_collision_test)
const FIXTURE := preload("res://tools/UnderstoryCollisionTest.cs")
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
			print("UNDERSTORY_COLLISION=" + result)
			quit(0)
		else:
			push_error("UNDERSTORY_COLLISION=" + result)
			quit(1)
		return true
	if frames > 40000:
		push_error("UNDERSTORY_COLLISION=FAIL timeout")
		quit(1)
	return false
