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
	var near_map: Dictionary = SMOKE.new().RunNearMap(self)
	if not bool(near_map.get("pass", false)):
		push_error("TERRAIN_LOD_SHADOW_COVERAGE=FAIL near map: " + str(near_map.get("detail", "unknown failure")))
		quit(1)
		return
	print("TERRAIN_LOD_SHADOW_COVERAGE near map: " + str(near_map.get("detail", "")))
	var near_planet: Dictionary = SMOKE.new().RunNearPlanetMap(self)
	if not bool(near_planet.get("pass", false)):
		push_error("TERRAIN_LOD_SHADOW_COVERAGE=FAIL near planet map: " + str(near_planet.get("detail", "unknown failure")))
		quit(1)
		return
	print("TERRAIN_LOD_SHADOW_COVERAGE near planet map: " + str(near_planet.get("detail", "")))
	print("TERRAIN_LOD_SHADOW_COVERAGE=PASS " + str(result.get("detail", "")) + " terrain3d_default_cast_shadows=" + str(result.get("terrain3d_default_cast_shadows", -1)))
	quit(0)
