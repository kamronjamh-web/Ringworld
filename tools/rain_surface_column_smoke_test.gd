extends SceneTree

const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const Runner := preload("res://scripts/Weather/RainSurfaceDiagnosticRunner.cs")
var scene: Node

func _initialize() -> void:
	scene = SCENE.instantiate(); root.add_child(scene); call_deferred("run")

func run() -> void:
	await process_frame
	var grid = scene.get_node_or_null("ChunkGridManager")
	if grid == null: return fail("grid unavailable")
	var runner = Runner.new()
	var circumference: int = grid.GetCanonicalRingCircumferenceMeters64()
	var result: String = runner.RunColumnEdits(grid)
	if not result.ends_with("PASS"): return fail(result)
	var baseline := 200
	# The public API must canonicalize seam positions and reject width bounds.
	var seam_a: Dictionary = runner.Query(grid, 0, 100, baseline)
	var seam_b: Dictionary = runner.Query(grid, circumference, 100, baseline)
	if int(seam_a.top) != int(seam_b.top): return fail("seam canonicalization")
	var outside: Dictionary = runner.Query(grid, 0, -1, baseline)
	if bool(outside.found): return fail("across bounds")
	print("RAIN_SURFACE_COLUMN_SMOKE=PASS")
	quit(0)

func fail(message: String) -> void:
	push_error("RAIN_SURFACE_COLUMN_SMOKE=FAIL: " + message); quit(1)
