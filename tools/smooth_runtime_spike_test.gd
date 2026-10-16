extends SceneTree

## Voxel/cave spike B1 parity test: gen-17 (v5 sites) on the smooth voxel backend, caves off. Pass `-- --capture` once to write
## tools/fixtures/smooth_runtime_gen17_golden.json.
const TEST := preload("res://tools/SmoothRuntimeSpikeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"))
	runner = null
	if code == 0:
		print("SMOOTH_RUNTIME_SPIKE_WRAPPER=PASS")
	else:
		push_error("SMOOTH_RUNTIME_SPIKE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
