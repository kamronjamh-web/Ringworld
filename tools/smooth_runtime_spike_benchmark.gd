extends SceneTree

## Voxel/cave spike B1 density benchmark (gen-16 vs gen-17, cached vs uncached). Prints SMOOTH_BENCH lines; headless is fine.
const TEST := preload("res://tools/SmoothRuntimeSpikeBenchmark.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	quit(code)
