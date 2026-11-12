extends SceneTree

## T2R-9: cold bake, warm load and second cold bake of the whole-ring river graph (see tools/DrainageBakeBenchmark.cs). The GPU source needs a windowed run.
## Manual: Godot_console.exe --path . --script res://tools/drainage_bake_benchmark.gd -- --source=gpu [--seed=1337 --label=x]
const TOOL := preload("res://tools/DrainageBakeBenchmark.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TOOL.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("DRAINAGE_BAKE_BENCHMARK_WRAPPER=PASS")
	else:
		push_error("DRAINAGE_BAKE_BENCHMARK_WRAPPER=FAIL exit=%d" % code)
	quit(code)
