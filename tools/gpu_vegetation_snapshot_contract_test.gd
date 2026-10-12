extends SceneTree

const TEST := preload("res://tools/GpuVegetationSnapshotContractTest.cs")

func _init() -> void:
	var result: Dictionary = TEST.new().Run()
	print("GPU_VEGETATION_SNAPSHOT_CONTRACT=" + JSON.stringify(result))
	quit(0 if bool(result.get("pass", false)) else 1)
