extends SceneTree

## WO-G0 of docs/gpu-offload-plan.md: GPU FastNoiseLite OpenSimplex2S and df64 projection parity against the CPU, plus the fp64 probe and
## noise timings. Local RenderingDevice, no world (see tools/GpuNoiseParityTest.cs).
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   Godot --path . --script res://tools/gpu_noise_parity_test.gd [-- --out=<dir>]
## Prints GPU_NOISE_PARITY=PASS and writes .godot/gpu_noise_parity/report.json.
const TEST := preload("res://tools/GpuNoiseParityTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(280.0).timeout.connect(func():
		push_error("GPU_NOISE_PARITY=FAIL timeout")
		quit(1))
	if RenderingServer.get_rendering_device() == null:
		push_error("GPU_NOISE_PARITY=FAIL requires Vulkan/Forward+ (run windowed)")
		quit(1)
		return
	var out := ""
	for a in OS.get_cmdline_user_args():
		if String(a).begins_with("--out="):
			out = String(a).substr(6)
	var runner = TEST.new()
	var code: int = runner.Run(out)
	runner = null
	if code == 0:
		print("GPU_NOISE_PARITY_WRAPPER=PASS")
	else:
		push_error("GPU_NOISE_PARITY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
