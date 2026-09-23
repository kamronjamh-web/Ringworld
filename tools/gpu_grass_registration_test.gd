extends SceneTree
const FIXTURE := preload("res://tools/GpuGrassRegistrationTest.cs")
var fixture
var frames := 0
func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GPU_GRASS_REGISTRATION=FAIL RenderingDevice required"); quit(1); return
	fixture = FIXTURE.new(); root.add_child(fixture); fixture.call_deferred("Start")
func _process(_delta: float) -> bool:
	frames += 1
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"): print("GPU_GRASS_REGISTRATION=" + result); quit(0)
		else: push_error("GPU_GRASS_REGISTRATION=" + result); quit(1)
		return true
	if frames > 600: push_error("GPU_GRASS_REGISTRATION=FAIL timeout"); quit(1)
	return false
