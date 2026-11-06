extends SceneTree

## WO-G1 of docs/gpu-offload-plan.md: GPU K1 height + climate kernel (v7/v8 natural path) against the C# runtime sampler (see tools/ProxyGpuHeightParityTest.cs).
## Local RenderingDevice, no world. Run (windowed Vulkan, not --headless; no other Godot process running):
##   Godot --path . --script res://tools/proxy_gpu_height_parity_test.gd [-- --out=<dir>]
## Prints PROXY_GPU_HEIGHT_PARITY=PASS and writes .godot/proxy_gpu_height_parity/report.json.
const TEST := preload("res://tools/ProxyGpuHeightParityTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(560.0).timeout.connect(func():
		push_error("PROXY_GPU_HEIGHT_PARITY=FAIL timeout")
		quit(1))
	if RenderingServer.get_rendering_device() == null:
		push_error("PROXY_GPU_HEIGHT_PARITY=FAIL requires Vulkan/Forward+ (run windowed)")
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
		print("PROXY_GPU_HEIGHT_PARITY_WRAPPER=PASS")
	else:
		push_error("PROXY_GPU_HEIGHT_PARITY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
