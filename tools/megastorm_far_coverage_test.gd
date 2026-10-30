extends SceneTree
# Headless: megastorm far-layer coverage table (WO5): EvaluateMap parity after the AccumulateSystems extraction, T/P table composition against
# EvaluateMap, inactive behaviour, determinism, range and the T plateau (docs/megastorm-design.md 3.4).
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FINGERPRINT := preload("res://tools/NaturalWeatherFingerprintTest.cs")
const TEST := preload("res://tools/MegastormFarCoverageTest.cs")
const CERTIFIED_RADIUS_M := 5000000.0
const CERTIFIED_WIDTH_M := 318000.0
const CERTIFIED_CHUNKS_AROUND := 981748
var _scene: Node
var _weather: Node
var _frames := 0

func _initialize() -> void:
	create_timer(120.0).timeout.connect(func():
		push_error("MEGASTORM_FAR_COVERAGE_WRAPPER=FAIL timeout")
		quit(1))
	call_deferred("_setup")

func _setup() -> void:
	_scene = TEST_SCENE.instantiate()
	_weather = _scene.get_node("RingVolumetricClouds")
	_configure_v5_world()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < 3: return false
	var tool = TEST.new()
	var code: int = tool.call("Run", _weather, OS.get_cmdline_user_args().has("--verbose"))
	tool = null
	if code != 0:
		push_error("MEGASTORM_FAR_COVERAGE_WRAPPER=FAIL exit=%d" % code)
	else:
		print("MEGASTORM_FAR_COVERAGE_WRAPPER=PASS")
	quit(code)
	return false

func _configure_v5_world() -> void:
	var tool = FINGERPRINT.new()
	var grid := _scene.get_node("ChunkGridManager")
	grid.set("AroundRingChunkLength", CERTIFIED_CHUNKS_AROUND)
	grid.set("DeriveRingRadiusFromChunkLength", true)
	grid.set("RingRadiusMeters", CERTIFIED_RADIUS_M)
	grid.set("RingAcrossWidthMeters", CERTIFIED_WIDTH_M)
	grid.set("RingAcrossBaseOffsetMeters", -CERTIFIED_WIDTH_M * 0.5)
	grid.set("MaxBuildGlobalY", 8400)
	grid.set("MinExcavationGlobalY", -8192)
	grid.set("TerrainBottomGlobalY", -8192)
	var settings := (grid.get("TerrainSettings") as Resource).duplicate(true)
	settings.set("Seed", 1337)
	settings.set("BedrockY", -8000)
	settings.set("SeaLevelY", 0)
	settings.set("SurfaceGenerationRecipeJson", tool.call("RecipeJson"))
	settings.set("SurfaceGenerationContractId", tool.call("ContractId"))
	settings.set("EnableProceduralCaves", false)
	settings.set("EnableDesignedCaveDensityComposition", false)
	settings.set("EnableUnified3dDensityTerrain", false)
	settings.set("EnableNaturalOverhangs", false)
	grid.set("TerrainSettings", settings)
	grid.call("RefreshRuntimeTerrainDefinition")
