extends SceneTree
## Headless test of the adaptive stand window stop rule (tools/perf_bench_adaptive.gd, docs/perf-bench.md "Adaptive windows") on synthetic frames.
## RUN: Godot_console.exe --headless --path . --script res://tools/perf_bench_adaptive_rule_test.gd
## Prints PERF_BENCH_ADAPTIVE_RULE=PASS, or PERF_BENCH_ADAPTIVE_RULE=FAIL <reason> (exit 1).
const ADAPTIVE := preload("res://tools/perf_bench_adaptive.gd")


func _initialize() -> void:
	var why := _run()
	if why != "":
		push_error("PERF_BENCH_ADAPTIVE_RULE=FAIL %s" % why)
		quit(1)
		return
	print("PERF_BENCH_ADAPTIVE_RULE=PASS")
	quit(0)


## Frames at `fps` in 2 s blocks, wall and gpu normal (mean, sd; gpu plus a linear ramp). Returns the first boundary time (4, 6, ... up to max_s) at which the rule settles, or -1.
func _first_settled(seed_value: int, fps: int, max_s: int, mean_wall: float, sd_wall: float, mean_gpu: float, sd_gpu: float, gpu_ramp_ms_per_s: float) -> int:
	var rng := RandomNumberGenerator.new()
	rng.seed = seed_value
	var wall := PackedFloat64Array()
	var gpu := PackedFloat64Array()
	var starts := PackedInt32Array([0])
	var per_block := 2 * fps
	for block in range(1, max_s / 2 + 1):
		for i in per_block:
			var t := float((block - 1) * per_block + i) / float(fps)
			wall.append(rng.randfn(mean_wall, sd_wall))
			gpu.append(rng.randfn(mean_gpu, sd_gpu) + gpu_ramp_ms_per_s * t)
		starts.append(wall.size())
		if block >= 2 and bool(ADAPTIVE.check(wall, gpu, starts, float(block * 2))["settled"]):
			return block * 2
	return -1


func _run() -> String:
	# Quiet frames at 100 fps settle at the first possible check (4 s).
	var quiet := _first_settled(1, 100, 12, 8.0, 0.2, 8.0, 0.2, 0.0)
	if quiet != 4:
		return "quiet N(8, 0.2) at 100 fps settled at %d s, expected 4" % quiet
	# A 3 ms GPU drift across 12 s never settles.
	var drift := _first_settled(1, 100, 12, 8.0, 0.2, 8.0, 0.2, 0.25)
	if drift != -1:
		return "3 ms GPU drift settled at %d s, expected never" % drift
	# Heavy noise never settles.
	var noisy := _first_settled(1, 100, 12, 8.0, 3.0, 8.0, 3.0, 0.0)
	if noisy != -1:
		return "heavy noise N(8, 3) settled at %d s, expected never" % noisy
	# Too few frames at 4 s (60 fps: 240 frames) is not settled at 4 s, even when quiet.
	var slow := _first_settled(1, 60, 4, 16.0, 0.2, 16.0, 0.2, 0.0)
	if slow != -1:
		return "240 frames at 4 s settled, expected not (needs 300)"
	# Not before 4 s even with enough frames (one boundary only).
	var rng := RandomNumberGenerator.new()
	rng.seed = 3
	var w := PackedFloat64Array()
	for i in 500:
		w.append(rng.randfn(8.0, 0.2))
	if bool(ADAPTIVE.check(w, w, PackedInt32Array([0, 200, 400]), 3.9)["settled"]):
		return "settled at 3.9 s, expected not"
	return ""
