extends SceneTree
# Headless: megastorm near strike stream (WO7): rates, frequency / reduced flashes scaling, determinism, save/load, sprite share, and a staged layer run
# (appear, expire, rewind, no source) on the weather clock (docs/megastorm-design.md 3.3, 8).
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FINGERPRINT := preload("res://tools/NaturalWeatherFingerprintTest.cs")
const TEST := preload("res://tools/MegastormLightningTest.cs")
const CERTIFIED_RADIUS_M := 5000000.0
const CERTIFIED_WIDTH_M := 318000.0
const CERTIFIED_CHUNKS_AROUND := 981748
var _scene: Node
var _weather: Node
var _frames := 0
var _tool = null

func _initialize() -> void:
	create_timer(120.0).timeout.connect(func():
		push_error("MEGASTORM_LIGHTNING_WRAPPER=FAIL timeout")
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
	if _tool == null:
		_tool = TEST.new()
		var pure: int = _tool.call("RunPure")
		if pure != 0:
			push_error("MEGASTORM_LIGHTNING_WRAPPER=FAIL pure exit=%d" % pure)
			quit(pure)
			return false
	var code: int = _tool.call("Tick", _weather)
	if code < 0: return false
	_tool = null
	if code != 0:
		push_error("MEGASTORM_LIGHTNING_WRAPPER=FAIL exit=%d" % code)
	else:
		print("MEGASTORM_LIGHTNING_WRAPPER=PASS")
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
