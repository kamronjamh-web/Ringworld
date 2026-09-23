extends SceneTree

## State-only coverage for production-dimension incremental far-atlas publication.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MAX_FRAMES := 420

var _scene: Node3D
var _weather: Node
var _far: Node
var _shell: ShaderMaterial
var _clipmaps: Array[ShaderMaterial] = []
var _phase := 0
var _frame := 0
var _initial_publications := 0
var _forward_publications := 0
var _initial_atlas_hash := ""
var _max_work_ms := 0.0
var _max_work_items := 0
var _max_upload_ms := 0.0


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate() as Node3D
	_weather = _scene.get_node("RingVolumetricClouds")
	_far = _scene.get_node("ScaledFarViewport")
	_far.set("Enabled", true)
	_far.set("FarWeatherAtlasRefreshSeconds", 1.0)
	root.add_child(_scene)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 3 and not _resolve():
		return false
	if _frame == 3:
		_weather.set_process(false)
	if _shell == null:
		return false
	var snapshot := float(_far.get("FarWeatherAtlasSnapshotTimeSeconds"))
	var publications := int(_far.get("FarWeatherAtlasPublicationCount"))
	if _phase >= 1:
		_max_work_ms = maxf(_max_work_ms, float(_far.get("FarWeatherAtlasLastWorkMilliseconds")))
		_max_work_items = maxi(_max_work_items, int(_far.get("FarWeatherAtlasLastWorkItems")))
		_max_upload_ms = maxf(_max_upload_ms, float(_far.get("FarWeatherAtlasLastUploadMilliseconds")))
	if _phase == 0 and _shell.get_shader_parameter("weather_atlas") != null:
		_initial_publications = publications
		_initial_atlas_hash = str(_far.get("FarWeatherAtlasContentHashText"))
		_weather.set("CloudSimulationTimeSeconds", 500040.0)
		_phase = 1
	elif _phase == 1 and absf(snapshot - 500040.0) < 0.000001 and publications > _initial_publications and not bool(_far.get("FarWeatherAtlasRefreshInProgress")):
		_forward_publications = publications
		if str(_far.get("FarWeatherAtlasContentHashText")) == _initial_atlas_hash:
			return _fail("forward lifecycle publication did not change atlas content")
		_weather.set("CloudSimulationTimeSeconds", 500050.0)
		_phase = 2
	elif _phase == 2 and bool(_far.get("FarWeatherAtlasRefreshInProgress")):
		_weather.set("CloudSimulationTimeSeconds", 2.0)
		_phase = 3
	elif _phase == 3 and absf(snapshot - 2.0) < 0.000001 and publications > _forward_publications and not bool(_far.get("FarWeatherAtlasRefreshInProgress")):
		if not _events_match():
			return _fail("published shell/clipmap lightning events diverged after time jumps")
		if not _materials_share_atlas_and_age(): return _fail("shell/clipmap atlas or age uniforms differ")
		print("FAR_WEATHER_REFRESH_SMOKE_TEST=PASS snapshot=%.3f frame=%d max_work_ms=%.3f max_items=%d max_upload_ms=%.3f" % [snapshot, _frame, _max_work_ms, _max_work_items, _max_upload_ms])
		quit(0)
	if _frame >= MAX_FRAMES:
		return _fail("timeout snapshot=%.6f current=%.6f pending=%s publications=%d frame=%d" % [snapshot, float(_weather.get("CloudSimulationTimeSeconds")), bool(_far.get("FarWeatherAtlasRefreshInProgress")), publications, _frame])
	return false


func _resolve() -> bool:
	var far_world := _far.get_node_or_null("FarRingViewport")
	var shell := far_world.get_node_or_null("ScaledCloudShell") as MeshInstance3D if far_world != null else null
	if shell == null or not (shell.material_override is ShaderMaterial):
		return _fail("far shell material unavailable")
	_shell = shell.material_override as ShaderMaterial
	for index in range(3):
		var layer := far_world.get_node_or_null("FarCloudClipmapLayer%d" % index) as MeshInstance3D
		if layer == null or not (layer.material_override is ShaderMaterial):
			return _fail("far clipmap %d material unavailable" % index)
		_clipmaps.append(layer.material_override as ShaderMaterial)
	return true


func _events_match() -> bool:
	var a := _shell.get_shader_parameter("far_lightning_event_a") as Vector4
	var b := _shell.get_shader_parameter("far_lightning_event_b") as Vector4
	for material in _clipmaps:
		var candidate_a := material.get_shader_parameter("far_lightning_event_a") as Vector4
		var candidate_b := material.get_shader_parameter("far_lightning_event_b") as Vector4
		if not candidate_a.is_equal_approx(a) or not candidate_b.is_equal_approx(b): return false
	return true


func _materials_share_atlas_and_age() -> bool:
	var atlas: Texture2D = _shell.get_shader_parameter("weather_atlas") as Texture2D
	var age: float = float(_shell.get_shader_parameter("weather_time_seconds"))
	for material in _clipmaps:
		if material.get_shader_parameter("weather_atlas") != atlas or material.get_shader_parameter("weather_time_seconds") != age: return false
	return true

func _fail(message: String) -> bool:
	push_error("FAR_WEATHER_REFRESH_SMOKE_TEST=FAIL %s" % message)
	quit(1)
	return false
