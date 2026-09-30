extends SceneTree

const COORDINATOR = preload("res://scripts/Voxel/LOD/RingTerrainLodCoordinator.cs")

func _init() -> void:
	var coordinator := COORDINATOR.new()
	var summary: String = coordinator.RunIntermediateTierTopologyRegressionTest()
	coordinator.free()
	print("INTERMEDIATE_LOD_TOPOLOGY=PASS " + summary)
	quit(0)
