extends SceneTree
const FIXTURE := preload("res://tools/GrassOutputResidencyTest.cs")
var fixture
var frames := 0
func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GRASS_OUTPUT_RESIDENCY=FAIL RenderingDevice required"); quit(1); return
	fixture = FIXTURE.new(); root.add_child(fixture); fixture.call_deferred("Start")
func _process(_delta: float) -> bool:
	frames += 1
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"): print("GRASS_OUTPUT_RESIDENCY=" + result); quit(0)
		else: push_error("GRASS_OUTPUT_RESIDENCY=" + result); quit(1)
		return true
	if frames > 2400: push_error("GRASS_OUTPUT_RESIDENCY=FAIL timeout"); quit(1)
	return false
