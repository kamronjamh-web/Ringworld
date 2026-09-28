extends SceneTree

# Exercises periodic structural-wall request spans without loading gameplay.
const SECTOR := preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

func _init() -> void:
	var sector: Node = SECTOR.new()
	var passed := bool(sector.call("RunStructuralWallPeriodicRangeContract"))
	sector.free()
	if not passed:
		push_error("Structural wall periodic-range contract failed")
		quit(1)
		return
	quit(0)