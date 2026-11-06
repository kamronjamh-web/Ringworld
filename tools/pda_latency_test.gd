extends SceneTree
# Bounded RAM/CPU latency comparison. Synthetic snapshots, no live world or save access.
# Run through tools/run_tests.ps1 -Tests pda_latency_test -Label pda_latency -Cache reuse.
const PROBE := preload("res://tools/PdaLatencyTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(240.0).timeout.connect(func(): push_error("PDA_LATENCY_RAM=FAIL watchdog"); quit(1))
	var probe = PROBE.new()
	root.add_child(probe)
	probe.Begin(false)
	while str(probe.get("Result")) == "RUNNING":
		await process_frame
	var result := str(probe.get("Result"))
	probe.queue_free()
	await process_frame
	if result == "PASS":
		print("PDA_LATENCY_RAM_WRAPPER=PASS")
		quit(0)
	else:
		push_error("PDA_LATENCY_RAM_WRAPPER=FAIL " + result)
		quit(1)
