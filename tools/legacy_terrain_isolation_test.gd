extends SceneTree

# Headless guard coverage for the dormant editable backend. This creates no saves.
const GRID := preload("res://scripts/Voxel/Phase2/VoxelChunkGridManager.cs")
const COARSE := preload("res://scripts/Voxel/LOD/RingTerrainCoarseSdfPilot.cs")
const DRIVER := preload("res://tools/LegacyTerrainIsolationTest.cs")
const TERRAIN_SETTINGS := preload("res://resources/voxel/voxel_terrain_settings_default.tres")

var _harness: Node3D
var _driver: RefCounted

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_driver = DRIVER.new()
	_harness = Node3D.new()
	root.add_child(_harness)
	var dormant := _make_grid("DormantGrid", true)
	_harness.add_child(dormant)
	if not _driver.VerifyDormantGrid(dormant):
		_fail()
		return

	var focus := Node3D.new()
	focus.name = "Focus"
	_harness.add_child(focus)
	var lod1 := Node3D.new()
	lod1.name = "Lod1"
	lod1.visible = true
	_harness.add_child(lod1)
	var pilot := COARSE.new()
	pilot.name = "CoarsePilot"
	pilot.Enabled = true
	pilot.GridManagerPath = NodePath("../DormantGrid")
	pilot.FocusPath = NodePath("../Focus")
	pilot.Lod1ProxyPath = NodePath("../Lod1")
	_harness.add_child(pilot)
	for i in range(4): await process_frame
	if not _driver.VerifyCoarseSuppressed(pilot, lod1):
		_fail()
		return

	# Queue a legacy tile, then suppress it before its worker completion can install.
	dormant.DormantEditableBackend = false
	pilot.Enabled = true
	await process_frame
	if not _driver.VerifyCoarseBuildQueued(pilot):
		_fail()
		return
	dormant.DormantEditableBackend = true
	var deadline := Time.get_ticks_msec() + 10000
	while not _driver.IsCoarseIdle(pilot) and Time.get_ticks_msec() < deadline:
		await process_frame
	if not _driver.IsCoarseIdle(pilot):
		push_error("LEGACY_TERRAIN_ISOLATION=FAIL stale coarse build did not drain")
		_fail()
		return
	if not _driver.VerifyCoarseSuppressed(pilot, lod1):
		_fail()
		return

	if not _driver.VerifyLateCompletionIsolation(pilot, dormant):
		_fail()
		return
	for i in range(4): await process_frame
	if not _driver.VerifyCoarseSuppressed(pilot, lod1):
		_fail()
		return

	var legacy := _make_grid("LegacyGrid", false)
	_harness.add_child(legacy)
	if not _driver.VerifyLegacyGridBuild(legacy):
		_fail()
		return
	print("LEGACY_TERRAIN_ISOLATION=PASS")
	_harness.queue_free()
	quit(0)

func _make_grid(name: String, dormant: bool) -> Node3D:
	var grid := GRID.new()
	grid.name = name
	grid.BuildOnReady = false
	grid.DormantEditableBackend = dormant
	grid.EnableChunkStreaming = false
	grid.ChunkRadiusAround = 0
	grid.ChunkRadiusAcross = 0
	grid.ChunkRadiusVertical = 0
	grid.UseDeterministicTerrainGeneration = true
	grid.TerrainSettings = TERRAIN_SETTINGS.duplicate(true)
	return grid

func _fail() -> void:
	if _harness != null:
		_harness.queue_free()
	quit(1)
