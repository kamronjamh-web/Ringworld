extends SceneTree

const FIXTURE := preload("res://tools/TreeRenderPolicyRegression.cs")
var fixture
var frames := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("TREE_RENDER_POLICY_REGRESSION=FAIL RenderingDevice required")
		quit(1)
		return
	fixture = FIXTURE.new()
	root.add_child(fixture)
	fixture.call_deferred("Start")

func _process(_delta: float) -> bool:
	frames += 1
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		print("TREE_RENDER_POLICY_REGRESSION=" + result)
		quit(0 if result.begins_with("PASS") else 1)
		return true
	if frames > 1200:
		push_error("TREE_RENDER_POLICY_REGRESSION=FAIL timeout")
		quit(1)
	return false
