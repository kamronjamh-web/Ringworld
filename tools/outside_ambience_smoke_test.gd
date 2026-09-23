extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")


class AltitudePlayerStub:
	extends Node

	var altitude_meters: float = 100.0

	func GetCanonicalAtmosphereHeightMeters() -> float:
		return altitude_meters


func _initialize() -> void:
	var scene := TEST_SCENE.instantiate()
	var ambience := scene.get_node_or_null("OutsideAmbience") as AudioStreamPlayer
	if ambience == null:
		_fail("OutsideAmbience node is missing from the gameplay scene.", scene)
		return
	if ambience.stream == null or not ambience.stream is AudioStreamOggVorbis:
		_fail("Outside ambience is not wired to the expected Ogg stream.", scene)
		return

	var altitude_cases := {
		0.0: 1.0,
		1200.0: 1.0,
		1600.0: 0.5,
		2000.0: 0.0,
		5000.0: 0.0,
	}
	for altitude: float in altitude_cases:
		var actual := float(ambience.call("_altitude_gain", altitude))
		var expected := float(altitude_cases[altitude])
		if not is_equal_approx(actual, expected):
			_fail(
				"Altitude %.1fm produced gain %.4f; expected %.4f."
				% [altitude, actual, expected],
				scene)
			return

	ambience.call("_configure_looping")
	var loop_stream := ambience.stream as AudioStreamOggVorbis
	if loop_stream == null or not loop_stream.loop:
		_fail("Runtime stream copy did not enable looping.", scene)
		return

	var player_stub := AltitudePlayerStub.new()
	ambience.set("_player", player_stub)
	ambience.call("_process", 2.5)
	if not is_equal_approx(float(ambience.get("_current_gain")), 1.0):
		_fail("Surface ambience did not finish fading in after 2.5 seconds.", scene, player_stub)
		return
	if not is_equal_approx(ambience.volume_db, 6.0):
		_fail("Surface ambience did not reach its configured +6 dB level.", scene, player_stub)
		return

	player_stub.set_meta("ring_weather_ambience_duck", 0.68)
	ambience.call("_process", 2.5)
	if not is_equal_approx(float(ambience.get("_current_gain")), 0.68):
		_fail("Weather metadata did not duck the exterior mix.", scene, player_stub)
		return
	player_stub.set_meta("ring_water_ambience_proximity", 0.5)
	ambience.call("_process", 2.5)
	if not is_equal_approx(float(ambience.get("_current_gain")), 0.68 * 0.775):
		_fail("Weather duck did not preserve the independent water multiplier.", scene, player_stub)
		return
	player_stub.set_meta("ring_weather_ambience_duck", NAN)
	ambience.call("_process", 2.5)
	if not is_equal_approx(float(ambience.get("_current_gain")), 0.775):
		_fail("Invalid weather metadata did not fall back safely.", scene, player_stub)
		return
	player_stub.remove_meta("ring_weather_ambience_duck")
	player_stub.remove_meta("ring_water_ambience_proximity")
	ambience.call("_process", 2.5)
	if not is_equal_approx(float(ambience.get("_current_gain")), 1.0):
		_fail("Removed weather metadata did not restore the exterior mix.", scene, player_stub)
		return

	player_stub.set_meta("ring_camera_underwater", true)
	ambience.call("_process", 2.5)
	if not is_zero_approx(float(ambience.get("_current_gain"))):
		_fail("Underwater ambience did not finish fading out after 2.5 seconds.", scene, player_stub)
		return
	if not is_equal_approx(ambience.volume_db, -80.0):
		_fail("Underwater ambience did not reach silence.", scene, player_stub)
		return

	ambience.stream = null
	player_stub.free()
	scene.free()
	print("Outside ambience smoke test passed: wiring, loop, altitude/fades, weather/water duck independence, invalid metadata, cleanup, underwater mute.")
	call_deferred("_finish_success")


func _finish_success() -> void:
	quit(0)


func _fail(message: String, scene: Node, player_stub: Node = null) -> void:
	push_error("Outside ambience smoke test failed: %s" % message)
	if is_instance_valid(player_stub):
		player_stub.free()
	if is_instance_valid(scene):
		var ambience := scene.get_node_or_null("OutsideAmbience") as AudioStreamPlayer
		if ambience != null:
			ambience.stream = null
		scene.free()
	call_deferred("_finish_failure")


func _finish_failure() -> void:
	quit(1)
