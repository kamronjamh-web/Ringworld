extends SceneTree
# Headless: containment field WO14 natural near-wall field strikes (docs/containment-field-design.md 3.10.6): the pure stream (gates, rate, geometry, determinism, the
# real adapter and bus on a dense stream), then a staged run through the real layer with a registered storm parked on the wall.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FINGERPRINT := preload("res://tools/NaturalWeatherFingerprintTest.cs")
const TEST := preload("res://tools/NaturalFieldStrikeTest.cs")
const CERTIFIED_RADIUS_M := 5000000.0
const CERTIFIED_WIDTH_M := 318000.0
const CERTIFIED_CHUNKS_AROUND := 981748
var _scene: Node
var _weather: Node
var _frames := 0
var _tool = null

func _initialize() -> void:
	create_timer(240.0).timeout.connect(func():
		push_error("NATURAL_FIELD_STRIKE_WRAPPER=FAIL timeout")
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
			push_error("NATURAL_FIELD_STRIKE_WRAPPER=FAIL pure exit=%d" % pure)
			quit(pure)
			return false
	var code: int = _tool.call("Tick", _weather)
	if code < 0: return false
	_tool = null
	if code != 0:
		push_error("NATURAL_FIELD_STRIKE_WRAPPER=FAIL exit=%d" % code)
	else:
		print("NATURAL_FIELD_STRIKE=PASS")
		print("NATURAL_FIELD_STRIKE_WRAPPER=PASS")
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
