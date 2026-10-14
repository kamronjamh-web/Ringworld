extends SceneTree

const COORDINATOR = preload("res://scripts/Voxel/LOD/RingTerrainLodCoordinator.cs")

func _init() -> void:
	var coordinator := COORDINATOR.new()
	var summary: String = coordinator.RunBuildStartSchedulingContract()
	coordinator.free()
	print("LOD_BUILD_START_SCHEDULING=PASS " + summary)
	quit(0)
