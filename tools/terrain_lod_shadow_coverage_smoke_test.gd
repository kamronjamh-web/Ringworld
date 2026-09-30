extends SceneTree

const SMOKE := preload("res://tools/TerrainLodShadowCoverageSmoke.cs")

func _initialize() -> void:
	call_deferred("run_probe")

func run_probe() -> void:
	var result: Dictionary = SMOKE.new().Run()
	if not bool(result.get("pass", false)):
		push_error("TERRAIN_LOD_SHADOW_COVERAGE=FAIL " + str(result.get("detail", "unknown failure")))
		quit(1)
		return
	print("TERRAIN_LOD_SHADOW_COVERAGE=PASS " + str(result.get("detail", "")) + " terrain3d_default_cast_shadows=" + str(result.get("terrain3d_default_cast_shadows", -1)))
	quit(0)
