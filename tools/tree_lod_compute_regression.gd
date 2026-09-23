extends SceneTree
const DRIVER := preload("res://tools/TreeLodComputeRegression.cs")
var driver
var frames := 0
func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: quit(1); return
	driver = DRIVER.new(); root.add_child(driver); driver.call_deferred("Start")
func _process(_delta: float) -> bool:
	frames += 1
	var result: String = driver.get("Result")
	if result != "RUNNING":
		print("TREE_LOD_COMPUTE_REGRESSION=" + result)
		quit(0 if result.begins_with("PASS") else 1)
	if frames > 900: push_error("TREE_LOD_COMPUTE_REGRESSION=FAIL timeout"); quit(1)
	return false
