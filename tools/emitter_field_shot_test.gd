extends SceneTree
# Headless: emitter field shots, data side. The pure schedule, orb kinematics, EmitterShot bus kind, adapter and caps (EmitterFieldShotTest.Run), then a staged wiring check in
# the real integration scene (EmitterFieldShots node, host adapter, world segment crossing vs the 2D contact, ripples reaching the live bus).
# Run: Godot --headless --path . --script res://tools/emitter_field_shot_test.gd. Prints EMITTER_FIELD_SHOT=PASS and EMITTER_FIELD_SHOT_WRAPPER=PASS.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FINGERPRINT := preload("res://tools/NaturalWeatherFingerprintTest.cs")
const TEST := preload("res://tools/EmitterFieldShotTest.cs")
const CERTIFIED_RADIUS_M := 5000000.0
const CERTIFIED_WIDTH_M := 318000.0
const CERTIFIED_CHUNKS_AROUND := 981748
var _scene: Node
var _frames := 0
var _tool = null

func _initialize() -> void:
	create_timer(240.0).timeout.connect(func():
		push_error("EMITTER_FIELD_SHOT_WRAPPER=FAIL timeout")
		quit(1))
	call_deferred("_setup")

func _setup() -> void:
	_tool = TEST.new()
	var pure: int = _tool.call("Run")
	if pure != 0:
		push_error("EMITTER_FIELD_SHOT_WRAPPER=FAIL pure exit=%d" % pure)
		quit(pure)
		return
	_tool = TEST.new()
	_scene = TEST_SCENE.instantiate()
	_configure_v5_world()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	if _scene == null or _tool == null:
		return false
	_frames += 1
	if _frames < 3:
		return false
	var code: int = _tool.call("Tick", _scene)
	if code < 0:
		return false
	_tool = null
	if code != 0:
		push_error("EMITTER_FIELD_SHOT_WRAPPER=FAIL wiring exit=%d" % code)
	else:
		print("EMITTER_FIELD_SHOT_WRAPPER=PASS")
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
