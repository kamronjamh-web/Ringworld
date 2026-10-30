extends SceneTree

const SECTOR := preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

func _init() -> void:
	var sector: Node = SECTOR.new()
	var result: Dictionary = sector.call("RunPartialCoverageMeshCullingContractTest")
	sector.free()
	var ok := bool(result.get("passed", false))
	print("PROXY_PARTIAL_COVERAGE_TEST=%s %s" % ["PASS" if ok else "FAIL", JSON.stringify(result)])
	quit(0 if ok else 1)
