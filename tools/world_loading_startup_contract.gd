extends SceneTree
## Standalone contract; expected missing-dependency warnings/errors are marked below.

const TERRAIN := preload("res://scripts/Terrain/Surface/RingTerrain3DGameplay.cs")
const OWNERS := [
	preload("res://scripts/RingCloudLayer.cs"),
	preload("res://scripts/Voxel/LOD/RingAnalyticGlobalLod.cs"),
	preload("res://scripts/Voxel/LOD/RingTerrainGlobalLod.cs"),
	preload("res://scripts/Voxel/LOD/RingScaledFarViewport.cs"),
	preload("res://scripts/poi/Emitters/EmitterPoiStreamer.cs"),
]

var _errors := PackedStringArray()
var _checks := 0

func _initialize() -> void:
	call_deferred("_run")

func _expect(condition: bool, message: String) -> void:
	_checks += 1
	if not condition:
		_errors.append(message)

func _run() -> void:
	var terrain: Node = TERRAIN.new()
	_expect(not bool(terrain.get("UseStagedStartup")), "terrain staged startup must remain opt-in")
	var result := str(terrain.call("RunStartupStateContract"))
	_expect(result == "PASS", "terrain startup state contract: " + result)
	terrain.free()
	print("WORLD_LOADING_STARTUP_STATE=%s" % result)

	# This isolated parent deliberately has no grid, focus, camera, or environment.
	# Exercise real _Ready and _Process paths, rather than calling failure helpers.
	var fixture := Node.new()
	fixture.name = "MissingStartupDependencies"
	root.add_child(fixture)
	var owners: Array[Node] = []
	var latched_errors := PackedStringArray()
	for index in OWNERS.size():
		var owner: Node = OWNERS[index].new()
		owner.name = "StartupOwner%d" % index
		_expect(not bool(owner.get("UseStagedStartup")), "%s staged startup must remain opt-in" % owner.name)
		owner.set("UseStagedStartup", true)
		if index == 3:
			owner.set("Enabled", true)
		elif index < 2:
			owner.set("BuildEnabled", true)
		print("WORLD_LOADING_STARTUP_EXPECTED_ERROR_BEGIN owner=%s" % owner.name)
		fixture.add_child(owner)
		var status: Dictionary = owner.call("GetLoadingStatus")
		var error := str(status.get("error", ""))
		_expect(not error.is_empty(), "%s did not report missing dependencies" % owner.name)
		_expect(not bool(status.get("ready", true)), "%s reported ready after startup failure" % owner.name)
		print("WORLD_LOADING_STARTUP_EXPECTED_ERROR_END owner=%s error=%s" % [owner.name, error])
		owners.append(owner)
		latched_errors.append(error)

	# Failed owners must retain their diagnostic and never drift into readiness
	# when ordinary processing continues. No arbitrary timeout is needed here.
	for frame in 3:
		await process_frame
		for index in owners.size():
			var status: Dictionary = owners[index].call("GetLoadingStatus")
			_expect(str(status.get("error", "")) == latched_errors[index], "owner %d lost its failure diagnostic on frame %d" % [index, frame])
			_expect(not bool(status.get("ready", true)), "owner %d became ready after failure on frame %d" % [index, frame])
	fixture.queue_free()
	await process_frame
	await process_frame
	if _errors.is_empty():
		print("WORLD_LOADING_STARTUP_CONTRACT=PASS checks=%d" % _checks)
		quit(0)
	else:
		for error in _errors:
			push_error("WORLD_LOADING_STARTUP_CONTRACT_ERROR=" + error)
		print("WORLD_LOADING_STARTUP_CONTRACT=FAIL checks=%d" % _checks)
		quit(1)
