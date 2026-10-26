extends SceneTree
# Headless: flight-ready wind query (S3 WO-B2). Pure part always; with --world it also builds the v5 Certified-dimension world and
# checks the production provider (surface identity at 2 m, finite columns, core sigma, determinism, cost).
# Run: Godot --headless --path . --script res://tools/ring_wind_query_test.gd -- [--world] [--verbose]
const TEST := preload("res://tools/RingWindQueryTest.cs")
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FINGERPRINT := preload("res://tools/NaturalWeatherFingerprintTest.cs")
const CERTIFIED_RADIUS_M := 5000000.0
const CERTIFIED_WIDTH_M := 318000.0
const CERTIFIED_CHUNKS_AROUND := 981748
var _scene: Node
var _weather: Node
var _frames := 0

func _initialize() -> void:
	call_deferred("_setup")

func _setup() -> void:
	if not OS.get_cmdline_user_args().has("--world"):
		_finish(null)
		return
	_scene = TEST_SCENE.instantiate()
	_weather = _scene.get_node("RingVolumetricClouds")
	_configure_v5_world()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	if _scene == null: return false
	_frames += 1
	if _frames < 3: return false
	_finish(_weather)
	return false

func _finish(weather: Node) -> void:
	var runner = TEST.new()
	var code: int = runner.Run(weather, OS.get_cmdline_user_args().has("--verbose"))
	runner = null
	if code == 0:
		print("RING_WIND_QUERY_WRAPPER=PASS world=%s" % str(weather != null))
	else:
		push_error("RING_WIND_QUERY_WRAPPER=FAIL exit=%d" % code)
	quit(code)

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
