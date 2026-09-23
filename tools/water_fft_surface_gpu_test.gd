extends SceneTree
const SMOKE := preload("res://scripts/Tools/WaterFftSurfaceGpuSmoke.cs")
var smoke
var frames := 0
func _init() -> void:
	smoke = SMOKE.new()
	root.add_child(smoke)
func _process(_delta: float) -> bool:
	frames += 1
	if smoke.Completed:
		quit(0 if smoke.Passed else 1)
	elif frames > 1000:
		push_error("WATER_FFT_SURFACE_GPU_SMOKE=FAIL launcher timeout")
		quit(1)
	return false
