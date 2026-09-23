extends SceneTree

const SECTOR := preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

func _init() -> void:
	var sector := SECTOR.new()
	var passed: bool = bool(sector.call("RunOceanReplacementOwnershipContractTest"))
	sector.free()
	if not passed:
		push_error("Ocean replacement ownership contract failed")
		quit(1)
		return
	print("Ocean replacement ownership contract passed")
	quit(0)
