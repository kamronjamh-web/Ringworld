extends SceneTree

const FAR_SCRIPT := preload("res://scripts/Voxel/LOD/RingScaledFarViewport.cs")

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var far: Node = FAR_SCRIPT.new()
	var checks := [
		"RunAdaptiveGeometryContract",
		"RunAdaptiveTerrainTransitionContract",
		"RunStandaloneFarRegionalTransitionContract",
	]
	for method in checks:
		var result := str(far.call(method))
		if result != "PASS":
			push_error("FAR_ADAPTIVE_GEOMETRY_CONTRACT=FAIL method=%s result=%s" % [method, result])
			far.free()
			quit(1)
			return
		print("FAR_ADAPTIVE_GEOMETRY_CHECK=PASS method=%s" % method)
	far.free()
	print("FAR_ADAPTIVE_GEOMETRY_CONTRACT=PASS")
	quit(0)
