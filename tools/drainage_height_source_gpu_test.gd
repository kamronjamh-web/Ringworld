extends SceneTree

## T2R-3: GPU drainage height source against the CPU source (see tools/DrainageHeightSourceGpuTest.cs). Local RenderingDevice, no world.
## Run (windowed Vulkan, not --headless) with tools/run_tests.ps1 -Tests drainage_height_source_gpu_test.
const TEST := preload("res://tools/DrainageHeightSourceGpuTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(280.0).timeout.connect(func():
		push_error("DRAINAGE_HEIGHT_SOURCE_GPU=FAIL timeout")
		quit(1))
	if RenderingServer.get_rendering_device() == null:
		push_error("DRAINAGE_HEIGHT_SOURCE_GPU=FAIL requires Vulkan/Forward+ (run windowed)")
		quit(1)
		return
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("DRAINAGE_HEIGHT_SOURCE_GPU_WRAPPER=PASS")
	else:
		push_error("DRAINAGE_HEIGHT_SOURCE_GPU_WRAPPER=FAIL exit=%d" % code)
	quit(code)
