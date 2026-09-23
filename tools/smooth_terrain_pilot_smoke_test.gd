extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const GeneratedOutputPaths := preload("res://tools/generated_output_paths.gd")
const CAPTURE_FILE := "smooth_terrain_pilot.png"
const MAX_FRAMES := 1200

var _scene: Node
var _grid: Node
var _frame := 0
var _capture_path := ""


func _initialize() -> void:
	_capture_path = GeneratedOutputPaths.smoke_capture_path(CAPTURE_FILE)
	_scene = TEST_SCENE.instantiate()
	_grid = _scene.get_node("ChunkGridManager")
	_grid.set("UseSmoothNaturalTerrainPilot", true)
	_grid.set("StreamingVisualRadiusAround", 1)
	_grid.set("StreamingVisualRadiusAcross", 1)
	_grid.set("StreamingVisualRadiusVertical", 0)
	_grid.set("StreamingCollisionRadiusAround", 0)
	_grid.set("StreamingCollisionRadiusAcross", 0)
	_grid.set("StreamingCollisionRadiusVertical", 0)
	_grid.set("MaxChunkLoadsPerFrame", 1)
	root.add_child(_scene)


func _process(_delta: float) -> bool:
	_frame += 1
	var loaded := int(_grid.get("LoadedChunkCount"))
	var pending := int(_grid.get("PendingChunkLoadCount"))
	if loaded > 0 and _frame > 120:
		var image := root.get_texture().get_image()
		var error := image.save_png(_capture_path)
		if error != OK:
			return _fail("Smooth terrain pilot capture failed: %s" % error)
		print("Smooth terrain pilot passed: loaded=%d, capture=%s" % [loaded, _capture_path])
		quit(0)
		return true
	if _frame >= MAX_FRAMES:
		return _fail("Smooth terrain pilot did not settle: loaded=%d pending=%d" % [loaded, pending])
	return false


func _fail(message: String) -> bool:
	push_error(message)
	quit(1)
	return true
