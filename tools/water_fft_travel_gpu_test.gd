extends SceneTree

const SMOKE := preload("res://scripts/Tools/WaterFftTravelGpuSmoke.cs")
var _runner: Node
var _frames := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("WATER_FFT_TRAVEL=FAIL unsupported GPU renderer")
		quit(1)
		return
	_runner = SMOKE.new()
	root.add_child(_runner)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames > 4000:
		push_error("WATER_FFT_TRAVEL=FAIL wrapper timeout")
		quit(1)
		return true
	if not bool(_runner.get("Completed")): return false
	if not bool(_runner.get("Passed")):
		push_error("WATER_FFT_TRAVEL=FAIL " + str(_runner.get("LastFailure")))
		quit(1)
		return true
	_runner.queue_free()
	quit(0)
	return true
