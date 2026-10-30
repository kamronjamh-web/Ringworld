extends SceneTree

const TEST := preload("res://tools/GpuVegetationSnapshotContractTest.cs")

func _init() -> void:
	var result: Dictionary = TEST.new().Run()
	var ok := bool(result.get("pass", false))
	print("GPU_VEGETATION_SNAPSHOT_CONTRACT=%s %s" % ["PASS" if ok else "FAIL", JSON.stringify(result)])
	quit(0 if ok else 1)
