extends SceneTree

const SMOKE := preload("res://scripts/Tools/WaterFftGpuSmoke.cs")
var _runner: Node
var _frames := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("unsupported GPU renderer (no main RenderingDevice)")
		return
	_runner = SMOKE.new()
	root.add_child(_runner)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames > 960:
		_fail("wrapper timeout")
		return true
	if not bool(_runner.get("Completed")): return false
	if not bool(_runner.get("Passed")):
		_fail(str(_runner.get("LastFailure")))
		return true
	_runner.queue_free()
	quit(0)
	return true

func _fail(reason: String) -> void:
	push_error("WATER_FFT_GPU_SMOKE=FAIL " + reason)
	if _runner != null: _runner.queue_free()
	quit(1)
