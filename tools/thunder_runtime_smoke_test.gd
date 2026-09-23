extends SceneTree

# Lightweight integration fixture: the actual scene's shared preview source is
# used, while a real-tick deadline prevents a stalled headless audio backend
# from hanging CI. It intentionally writes no captures or user settings.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var _scene: Node
var _started_usec := 0
var _previewed := false
var _failed := false

func _initialize() -> void:
	_scene = SCENE.instantiate()
	root.add_child(_scene)
	_started_usec = Time.get_ticks_usec()

func _process(_delta: float) -> bool:
	var elapsed := float(Time.get_ticks_usec() - _started_usec) / 1000000.0
	var weather := _scene.get_node_or_null("RingVolumetricClouds")
	var audio := _scene.get_node_or_null("WeatherAudio")
	if weather != null and audio != null and not _previewed and elapsed > 0.2:
		# RingWeatherAudio consumes the shared event list directly, independent
		# from the local lightning renderer's presentation state.
		weather.call("TriggerSharedLightningPreviewAt", 1000.0, 1000.0, -99002, 1.0, 45000.0)
		_previewed = true
	if elapsed > 3.0:
		if audio == null or not _previewed:
			_fail("WeatherAudio or preview source missing")
		else:
			var diagnostics: Dictionary = audio.call("GetThunderDiagnostics")
			if int(diagnostics.get("asset_count", 0)) != 3:
				_fail("expected three thunder assets")
			elif int(diagnostics.get("voice_count", 0)) > 8:
				_fail("voice pool exceeds cap")
			elif not bool(diagnostics.get("can_hear", false)):
				_fail("listener unexpectedly cannot hear")
			else:
				print("Thunder runtime fixture: PASS ", diagnostics)
				quit(0)
	return false

func _fail(message: String) -> void:
	if _failed:
		return
	_failed = true
	push_error("Thunder runtime fixture failed: " + message)
	quit(1)
