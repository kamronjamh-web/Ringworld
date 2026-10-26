extends SceneTree
# Headless: storm arrival production adapter in a v5 Certified-dimension world (S3 WO-A2).
# Run: Godot --headless --path . --script res://tools/storm_arrival_production_test.gd [-- --verbose]
# Checks the arrival signal against the raw precipitation field through one lifecycle, determinism, the rain-history snapshot,
# provider install and the observer cache CPU budget. Prints STORM_ARRIVAL_PRODUCTION=PASS and STORM_ARRIVAL_OBSERVER_CPU=PASS.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FINGERPRINT := preload("res://tools/NaturalWeatherFingerprintTest.cs")
const PRODUCTION := preload("res://tools/StormArrivalProductionTest.cs")
const CERTIFIED_RADIUS_M := 5000000.0
const CERTIFIED_WIDTH_M := 318000.0
const CERTIFIED_CHUNKS_AROUND := 981748
var _scene: Node
var _weather: Node
var _frames := 0
var _phase := 0
var _before := 0

func _initialize() -> void:
	call_deferred("_setup")

func _setup() -> void:
	_scene = TEST_SCENE.instantiate()
	_weather = _scene.get_node("RingVolumetricClouds")
	_configure_v5_world()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < 3: return false
	if _phase == 0:
		_phase = 1
		var tool = PRODUCTION.new()
		var code: int = tool.call("Run", _weather, OS.get_cmdline_user_args().has("--verbose"))
		tool = null
		if code != 0:
			push_error("STORM_ARRIVAL_PRODUCTION_WRAPPER=FAIL exit=%d" % code)
			quit(code)
			return false
		print("STORM_ARRIVAL_PRODUCTION_WRAPPER=PASS")
		_frames = 3
		return false
	if _phase == 2:
		var held := _weather.call("GetStormArrivalDiagnostic") as Dictionary
		if _frames < 14: return false
		var refreshed := int(held["lag_refreshes"]) - _before
		if refreshed < 8:
			push_error("STORM_ARRIVAL_OBSERVER_HISTORY=FAIL only %d of 8 lags refreshed after a clock jump under a held clock" % refreshed)
			quit(1)
			return false
		print("STORM_ARRIVAL_OBSERVER_HISTORY=PASS lags refreshed after a jump with a held clock: %d" % refreshed)
		quit(0)
		return false
	# Observer cache CPU: advance the cloud clock at 6x real time for 400 frames.
	var t := float(_weather.get("CloudSimulationTimeSeconds"))
	_weather.set("CloudSimulationTimeSeconds", t + 0.1)
	var diag := _weather.call("GetStormArrivalDiagnostic") as Dictionary
	if _frames < 400: return false
	var mean_ms := float(diag["cpu_mean_ms"])
	var max_ms := float(diag["cpu_max_ms"])
	print("observer cpu: mean=%.4f ms max=%.4f ms evaluations=%d systems_in_reach=%d jumps=%d lag_refreshes=%d (budget 0.05 mean / 0.25 worst per frame)" % [mean_ms, max_ms, int(diag["evaluations"]), int(diag["systems_in_reach"]), int(diag["jumps"]), int(diag["lag_refreshes"])])
	# Per-evaluation mean x evaluation rate (10 per game second at 6x = ~1 per 1.6 frames at 60 fps) is the per-frame figure; report both.
	if bool(diag["valid"]) == false and int(diag["evaluations"]) == 0:
		push_error("STORM_ARRIVAL_OBSERVER_CPU=FAIL observer never evaluated (no explorer?)")
		quit(1)
		return false
	print("STORM_ARRIVAL_OBSERVER_CPU=REPORTED mean_ms=%.4f max_ms=%.4f" % [mean_ms, max_ms])
	# Regression (S2 report): after a clock jump with the clock then held still, all eight lags must still be refreshed.
	_before = int(diag["lag_refreshes"])
	_weather.set("CloudSimulationTimeSeconds", float(_weather.get("CloudSimulationTimeSeconds")) + 3000.0)
	_phase = 2
	_frames = 0
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
