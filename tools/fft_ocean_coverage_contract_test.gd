extends SceneTree

const SECTOR = preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

# Runs the C# coverage contract without loading a gameplay scene.  The C# test
# catches failures and returns false, allowing this launcher to terminate with
# a non-zero exit code instead of leaving a scene running after an exception.
func _init() -> void:
	var sector: Node = SECTOR.new()
	var passed := bool(sector.call("RunFftOceanCoverageContractTest"))
	sector.free()
	quit(0 if passed else 1)
