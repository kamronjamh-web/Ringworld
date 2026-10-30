extends SceneTree

const SECTOR := preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var sector: Node = SECTOR.new()
	sector.set("CoordinatorManaged", true)
	root.add_child(sector)
	var result: Dictionary = sector.call("RunVerticalLodCoverageContractTest")
	sector.queue_free()
	await process_frame
	if not bool(result.get("passed", false)):
		push_error("TERRAIN_VERTICAL_LOD_COVERAGE=FAIL %s" % result)
		quit(1)
		return
	print("TERRAIN_VERTICAL_LOD_COVERAGE=PASS %s" % result)
	quit(0)
