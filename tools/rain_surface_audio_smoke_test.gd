extends SceneTree

## Lightweight wiring check only. Use rain_surface_behavior_test.gd for real PCM,
## shelter edits, lifecycle checks, and Normal/Certified production coverage.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var scene: Node
var frames := 0

func _initialize() -> void:
	scene = SCENE.instantiate()
	root.add_child(scene)

func _process(_delta: float) -> bool:
	frames += 1
	if frames < 12: return false
	var ambience := scene.get_node_or_null("WeatherAmbience")
	if ambience == null or not ambience.has_method("GetWeatherAmbienceDiagnostics"):
		return fail("weather ambience diagnostics unavailable")
	var d: Dictionary = ambience.call("GetWeatherAmbienceDiagnostics")
	var required := ["surface_detail_pool_count", "surface_detail_valid_count", "surface_detail_positions", "surface_cover_target", "surface_cover_current", "surface_queries_frame", "surface_invalidations"]
	for key in required:
		if not d.has(key): return fail("missing surface diagnostic: " + key)
	if int(d.surface_detail_pool_count) != 24 or int(d.surface_detail_valid_count) > 24:
		return fail("detail pool was not bounded")
	# One listener column plus at most two surface slots; at most 16 density reads each.
	if int(d.surface_queries_frame) > 3 or int(d.surface_density_query_frame) > 48:
		return fail("surface query budget exceeded")
	if float(d.surface_cover_current) < 0.0 or float(d.surface_cover_current) > 1.0:
		return fail("cover mix escaped unit range")
	for point in d.surface_detail_positions:
		if not point.has("around") or not point.has("vertical") or not point.has("across") or not point.has("material"):
			return fail("surface detail position is incomplete")
	print("RAIN_SURFACE_AUDIO_SMOKE=PASS ", d)
	quit(0)
	return true

func fail(message: String) -> bool:
	push_error("RAIN_SURFACE_AUDIO_SMOKE=FAIL: " + message)
	quit(1)
	return true
