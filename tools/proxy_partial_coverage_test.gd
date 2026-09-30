extends SceneTree

const SECTOR := preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

func _init() -> void:
	var sector: Node = SECTOR.new()
	var result: Dictionary = sector.call("RunPartialCoverageMeshCullingContractTest")
	sector.free()
	print("PROXY_PARTIAL_COVERAGE_TEST=" + JSON.stringify(result))
	quit(0 if bool(result.get("passed", false)) else 1)
