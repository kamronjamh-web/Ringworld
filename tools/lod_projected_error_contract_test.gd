extends SceneTree

# Headless projected-error contract for the terrain proxy chain on the Certified ring
# (docs/terrain-altitude-lod-coverage-plan.md). For Low, Medium, High and Very High,
# every visible proxy tier must stay within 2.5x the target pixel error, and the global
# backdrop within 5x, at 0.5, 2, 8, 16, 23 and 60 km altitude.

const SCENE_PATH := "res://scenes/VoxelRingIntegrationTest.tscn"
const CERTIFIED_CIRCUMFERENCE_M := 31415936.0
const CERTIFIED_WIDTH_M := 318000.0
# Medium baselines the launcher assigns to the Certified global lod node.
const CERTIFIED_MEDIUM_AROUND := 2048
const CERTIFIED_MEDIUM_ACROSS := 64

func _init() -> void:
	var packed := load(SCENE_PATH) as PackedScene
	if packed == null:
		push_error("LOD_PROJECTED_ERROR_CONTRACT=FAIL could not load scene")
		quit(2)
		return
	var root := packed.instantiate()
	var coordinator := root.get_node_or_null("TerrainLodCoordinator")
	if coordinator == null:
		push_error("LOD_PROJECTED_ERROR_CONTRACT=FAIL no TerrainLodCoordinator")
		root.free()
		quit(2)
		return
	# --factor=<k> overrides the profiles' altitude error factor (5 must report violations: the check can fail).
	for raw in OS.get_cmdline_user_args():
		if String(raw).begins_with("--factor="):
			coordinator.set("ContractAltitudeErrorFactorOverride", float(String(raw).substr(9)))
	var result: Dictionary = coordinator.call(
		"RunProjectedErrorContract",
		CERTIFIED_CIRCUMFERENCE_M, CERTIFIED_WIDTH_M, 1080.0, 80.0,
		CERTIFIED_MEDIUM_AROUND, CERTIFIED_MEDIUM_ACROSS)
	print(result["report"])
	var violations: Array = result["violations"]
	for line in violations:
		print("  VIOLATION ", line)
	var passed: bool = result["passed"]
	root.free()
	print("LOD_PROJECTED_ERROR_CONTRACT=", "PASS" if passed else "FAIL", " violations=", violations.size())
	quit(0 if passed else 1)
