extends SceneTree
# GPU-queued CUDA/VRAM latency comparison. CUDA worker needs no RenderingDevice world fixture.
# Run through tools/run_tests.ps1 -Tests pda_latency_gpu_test -Label pda_latency -Cache reuse.
const PROBE := preload("res://tools/PdaLatencyTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(240.0).timeout.connect(func(): push_error("PDA_LATENCY_VRAM=FAIL watchdog"); quit(1))
	var probe = PROBE.new()
	root.add_child(probe)
	probe.Begin(true)
	while str(probe.get("Result")) == "RUNNING":
		await process_frame
	var result := str(probe.get("Result"))
	probe.queue_free()
	await process_frame
	if result == "PASS":
		print("PDA_LATENCY_VRAM_WRAPPER=PASS")
		quit(0)
	else:
		push_error("PDA_LATENCY_VRAM_WRAPPER=FAIL " + result)
		quit(1)
