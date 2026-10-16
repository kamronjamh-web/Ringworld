extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
# G0 fingerprint modes (docs/emitter-grid-climate-machines-spec.md): build a v5 Certified-dimension world, then capture or compare
# tools/fixtures/natural_weather_v5_fingerprint_golden.json instead of running the lifecycle checks.
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
	_scene = TEST_SCENE.instantiate()
	if "--weather-lifecycle-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER_SCENE.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", _scene, 5000000.0)
		launcher.free()
		if not bool(_scene.get_node("ScaledFarViewport").get("Enabled")):
			_fail("Certified launcher did not enable far weather")
			return
	_weather = _scene.get_node("RingVolumetricClouds")
	if _fingerprint_mode() != "":
		_configure_v5_world()
	else:
		_weather.set("NaturalWeatherLifecyclesEnabled", true)
		_weather.set("WeatherWindMetersPerSecond", Vector2.ZERO)
		_weather.set("MacroSystemSpawnChance", 0.8)
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < 3: return false
	if _fingerprint_mode() != "": return _run_fingerprint()
	# Verify the active preset reaches Sunshine, not just a C# default property.
	var bridge := _scene.get_node("SunshineCloudBridge")
	var clouds := _scene.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	var altitude: Vector4 = clouds.get("background_altitude")
	if absf(float(bridge.get("NaturalCloudTopAltitudeMeters")) - 6000.0) > 0.01 \
			or absf(float(_weather.get("CloudTopMeters")) - 6000.0) > 0.01 \
			or absf(altitude.x - 2000.0) > 0.01 or absf(altitude.y - 6000.0) > 0.01 \
			or float(clouds.get("cloud_ceiling")) < 6000.0:
		return _fail("preset did not publish the default 2000-6000m natural cloud deck")
	var stages := {}
	var boundaries := []
	var previous := ""
	for second in range(0, 2201):
		var d := _weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, float(second)) as Dictionary
		var stage := String(d["stage"])
		if not stages.has(stage): stages[stage] = second
		if not previous.is_empty() and stage != previous: boundaries.append(float(second))
		previous = stage
	if not stages.has("Birth") or not stages.has("Develop") or not stages.has("Mature") or not stages.has("Dissipate") or not stages.has("Quiet"):
		return _fail("production lifecycle did not expose every configured stage")
	for boundary in boundaries:
		var exact_boundary := _bisect_stage_boundary(boundary - 1.0, boundary, String((_weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, boundary - 1.0) as Dictionary)["stage"]))
		var before := _weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, exact_boundary - 0.01) as Dictionary
		var after := _weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, exact_boundary + 0.01) as Dictionary
		if absf(float(before["envelope"]) - float(after["envelope"])) > 0.02:
			return _fail("lifecycle envelope jumped at %.2f" % boundary)
	var quiet := _weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, float(stages["Quiet"]) + 0.1) as Dictionary
	var mature := _weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, float(stages["Mature"]) + 0.1) as Dictionary
	if float(quiet["envelope"]) != 0.0 or float(mature["envelope"]) < 0.999:
		return _fail("quiet/mature envelope contract failed")
	var generation_start := _weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, 0.0) as Dictionary
	var generation_next := _weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, 2200.0) as Dictionary
	if int(generation_start["generation"]) == int(generation_next["generation"]) \
			or int(generation_start["system_id"]) == int(generation_next["system_id"]):
		return _fail("generation boundary did not create a distinct positive natural ID")
	var on_a := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, 300.0) as Dictionary
	var on_b := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, 1500.0) as Dictionary
	var on_replay := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, 300.0) as Dictionary
	var invalid := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, -1.0) as Dictionary
	var nonfinite := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, INF) as Dictionary
	var zero := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, 0.0) as Dictionary
	if not _same_weather(on_a, on_replay) or not _same_weather(invalid, zero) or not _same_weather(nonfinite, zero):
		return _fail("weather replay or invalid-time clamp was not deterministic")
	if int(generation_start["system_id"]) <= 0 or int(generation_next["system_id"]) <= 0:
		return _fail("natural lifecycle generated a non-positive system ID")
	var grid := _scene.get_node("ChunkGridManager")
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var seam_plus := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0 + circumference, 10000.0, 300.0) as Dictionary
	var seam_minus := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0 - circumference, 10000.0, 300.0) as Dictionary
	if not _same_weather(on_a, seam_plus) or not _same_weather(on_a, seam_minus):
		return _fail("canonical plus/minus circumference weather seam mismatch")
	_weather.set("NaturalWeatherLifecyclesEnabled", false)
	var off_a := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, 300.0) as Dictionary
	var off_b := _weather.call("GetNaturalWeatherDiagnosticAtTime", 112000.0, 10000.0, 1500.0) as Dictionary
	if absf(float(off_a["cloud_coverage"]) - float(off_b["cloud_coverage"])) > 0.000001:
		return _fail("lifecycle-off changed with zero wind")
	if absf(float(on_a["cloud_coverage"]) - float(on_b["cloud_coverage"])) < 0.00001 and absf(float(on_a["precipitation"]) - float(on_b["precipitation"])) < 0.00001:
		return _fail("lifecycle-on did not evolve with zero wind")
	_weather.set("NaturalWeatherLifecyclesEnabled", true)
	_weather.set("WeatherWindMetersPerSecond", Vector2(2.2, 0.7))
	var active := 0
	for around in range(0, 480000, 32000):
		for across in range(1000, 20000, 4000):
			var sample := _weather.call("GetNaturalWeatherDiagnosticAtTime", float(around), float(across), 10000000.0) as Dictionary
			if int(sample["system_id"]) > 0 and float(sample["influence"]) > 0.0: active += 1
	if active == 0: return _fail("long-session crosswind weather drained")
	print("NATURAL_WEATHER_LIFECYCLE_SMOKE=PASS active=%d cloud_top=6000m" % active)
	quit(0)
	return false

func _fail(message: String) -> bool:
	push_error("NATURAL_WEATHER_LIFECYCLE_SMOKE=FAIL %s" % message)
	quit(1)
	return false

func _bisect_stage_boundary(low: float, high: float, low_stage: String) -> float:
	for _iteration in range(18):
		var mid := (low + high) * 0.5
		var stage := String((_weather.call("GetNaturalWeatherLifecycleDiagnostic", 3, 4, mid) as Dictionary)["stage"])
		if stage == low_stage: low = mid
		else: high = mid
	return (low + high) * 0.5

func _same_weather(a: Dictionary, b: Dictionary) -> bool:
	return int(a["system_id"]) == int(b["system_id"]) \
		and int(a["system_kind"]) == int(b["system_kind"]) \
		and (a["center_meters"] as Vector2).is_equal_approx(b["center_meters"] as Vector2) \
		and (a["radii_meters"] as Vector2).is_equal_approx(b["radii_meters"] as Vector2) \
		and is_equal_approx(float(a["cloud_coverage"]), float(b["cloud_coverage"])) \
		and is_equal_approx(float(a["precipitation"]), float(b["precipitation"])) \
		and is_equal_approx(float(a["influence"]), float(b["influence"]))

func _fingerprint_mode() -> String:
	var args := OS.get_cmdline_user_args()
	if "--weather-fingerprint-capture" in args: return "capture"
	if "--weather-fingerprint-compare" in args: return "compare"
	return ""

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

func _run_fingerprint() -> bool:
	var tool = FINGERPRINT.new()
	var code: int = tool.call("Run", _weather, _fingerprint_mode() == "capture")
	tool = null
	if code != 0:
		push_error("NATURAL_WEATHER_FINGERPRINT_WRAPPER=FAIL exit=%d" % code)
	else:
		print("NATURAL_WEATHER_FINGERPRINT_WRAPPER=PASS mode=%s" % _fingerprint_mode())
	quit(code)
	return false
