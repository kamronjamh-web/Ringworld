extends SceneTree
# WO-G2: GPU height backend routing and CPU fallback contract.
# Run: Godot --headless --path . --script res://tools/proxy_gpu_backend_routing_contract_test.gd
const TEST := preload("res://tools/ProxyGpuBackendRoutingContractTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PROXY_GPU_BACKEND_ROUTING_WRAPPER=PASS")
	else:
		push_error("PROXY_GPU_BACKEND_ROUTING_WRAPPER=FAIL exit=%d" % code)
	quit(code)
