extends SceneTree

## T4-7 stage 2: headless pose query and recipe check for the river-bed stills (see tools/RiverBedPoseQuery.cs). Prints RIVER_BED_POSE_QUERY AT=<--at string>.
const TEST := preload("res://tools/RiverBedPoseQuery.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RIVER_BED_POSE_QUERY=PASS")
	quit(code)
