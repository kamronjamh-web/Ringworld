extends SceneTree
# WO-C1: TerrainBuildSampleCache.Get is bit-identical to the original three-call path.
# Run: Godot --headless --path . --script res://tools/proxy_sample_cache_dedupe_contract_test.gd
const TEST := preload("res://tools/ProxySampleCacheDedupeContractTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PROXY_SAMPLE_CACHE_DEDUPE_WRAPPER=PASS")
	else:
		push_error("PROXY_SAMPLE_CACHE_DEDUPE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
