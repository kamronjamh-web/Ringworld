extends SceneTree

# Executes the production canonical-stencil normal contract against the
# certified ring geometry without loading a gameplay scene.
const SECTOR := preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

func _init() -> void:
	var sector: Node = SECTOR.new()
	var passed := bool(sector.call("RunSmoothProxyNormalSeamContractTest"))
	sector.free()
	if not passed:
		push_error("Proxy surface normal contract failed")
		quit(1)
		return
	print("Proxy surface normal contract passed")
	quit(0)
