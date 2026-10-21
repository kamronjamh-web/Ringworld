extends SceneTree

const COORDINATOR = preload("res://scripts/Voxel/LOD/RingTerrainLodCoordinator.cs")

func _init() -> void:
	var coordinator := COORDINATOR.new()
	var summary: String = coordinator.RunAltitudeActivationContract()
	coordinator.free()
	print("LOD_ALTITUDE_ACTIVATION=PASS " + summary)
	quit(0)
