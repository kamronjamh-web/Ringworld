extends SceneTree

# Positive integration test for the production thunder controller. The capture
# effect is before a temporary muted sink, so this never produces loud output.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const THUNDER_BUS := &"Thunder"
const SINK_BUS := &"ThunderTestSink"
const EPSILON := 0.000001

var scene: Node
var weather: Node
var bridge: Node
var explorer: Node
var audio: Node
var capture: AudioEffectCapture
var thunder_index := -1
var sink_index := -1
var started_usec := 0
var phase := 0
var baseline_played := 0
var expected_delay := 0.0
var emission_clock := 0.0
var event_id := -1
var ground_height := 0.0
var peak_before := 0.0
var peak_after := 0.0

func _initialize() -> void:
	started_usec = Time.get_ticks_usec()
	call_deferred("setup")

func setup() -> void:
	scene = SCENE.instantiate()
	if "--thunder-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready():
			await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", scene, 5000000.0)
		launcher.free()
	root.add_child(scene)

func _process(_delta: float) -> bool:
	var elapsed := float(Time.get_ticks_usec() - started_usec) / 1000000.0
	if elapsed > 30.0:
		fail("real-tick deadline exceeded")
		return false
	if scene == null or not scene.is_node_ready():
		return false
	if phase == 0:
		weather = scene.get_node_or_null("RingVolumetricClouds")
		bridge = scene.get_node_or_null("SunshineCloudBridge")
		explorer = scene.get_node_or_null("Explorer")
		audio = scene.get_node_or_null("WeatherAudio")
		if weather == null or bridge == null or explorer == null or audio == null:
			fail("required integration nodes missing")
			return false
		stop_fixture_loops()
		install_capture()
		phase = 1
		return false
	if phase == 1:
		# Let the controller and its eight reusable voices reach Ready first.
		if elapsed < 0.35:
			return false
		weather.set_process(false)
		var local := scene.get_node_or_null("LocalWeatherEffects")
		if local != null:
			local.set("EnableLightning", false)
		audio.set("Enabled", false)
		phase = 2
		return false
	if phase == 2:
		# A disable/enable transition must clear stale pending state before this test strike.
		audio.set("Enabled", true)
		var preview: Dictionary = weather.call("TriggerSharedLightningPreviewAt", 10000.0, 10000.0, -99001, 1.0, 45000.0)
		if preview.is_empty():
			fail("shared preview was not created")
			return false
		event_id = int(preview.get("event_id", -1))
		ground_height = float(preview["ground_vertical_meters"])
		bridge.call("SetBenchmarkViewerPosition", 10000.0, maxf(200.0, ground_height + 200.0), 10686.0)
		explorer.set_physics_process(false)
		phase = 3
		return false
	if phase == 3:
		# The first observation establishes the controller's active-audio epoch.
		var d: Dictionary = audio.call("GetThunderDiagnostics")
		if not d.has("listener_vertical"):
			fail("runtime diagnostics lack listener position")
			return false
		var vertical_delta := float(d["listener_vertical"]) - ground_height - 2.0
		expected_delay = sqrt(686.0 * 686.0 + vertical_delta * vertical_delta) / 343.0
		emission_clock = float(d.get("audio_clock", 0.0))
		baseline_played = int(d.get("played_count", 0))
		phase = 4
		return false
	if phase == 4:
		var d: Dictionary = audio.call("GetThunderDiagnostics")
		var age := float(d.get("audio_clock", 0.0)) - emission_clock
		var peak := drain_capture()
		if age < expected_delay - 0.10:
			peak_before = maxf(peak_before, peak)
			if int(d.get("played_count", 0)) != baseline_played:
				fail("thunder played before propagation delay")
				return false
			return false
		peak_after = maxf(peak_after, peak)
		if int(d.get("played_count", 0)) < baseline_played + 1:
			return false
		if int(d.get("last_id", -1)) != event_id:
			fail("arrival id differs from preview")
			return false
		if absf(float(d.get("last_delay_seconds", -99.0)) - expected_delay) > 0.05:
			fail("reported propagation delay differs from geometric expectation")
			return false
		if peak_before > EPSILON:
			fail("PCM was present before arrival")
			return false
		if peak_after <= 0.00001:
			return false
		phase = 5
		return false
	if phase == 5:
		var d: Dictionary = audio.call("GetThunderDiagnostics")
		peak_after = maxf(peak_after, drain_capture())
		if float(d.get("audio_clock", 0.0)) - emission_clock < expected_delay + 0.8:
			return false
		if int(d.get("played_count", 0)) != baseline_played + 1:
			fail("one event was played more than once")
			return false
		if int(d.get("active_voice_count", 99)) > 8 or int(d.get("asset_count", 0)) != 3:
			fail("voice cap or asset count contract failed")
			return false
		if peak_after <= 0.00001:
			fail("capture backend produced no post-arrival PCM")
			return false
		print("Thunder audio capture test: PASS ", {"delay_seconds": expected_delay, "pre_arrival_peak": peak_before, "post_arrival_peak": peak_after, "event_id": event_id})
		cleanup_and_quit(0)
	return false

func install_capture() -> void:
	thunder_index = AudioServer.get_bus_index(THUNDER_BUS)
	if thunder_index < 0:
		fail("Thunder bus missing")
		return
	# Godot sends buses only to buses on their left. Insert the quiet sink before
	# Thunder, then refresh its shifted index; capture remains upstream of gain.
	sink_index = 1
	AudioServer.add_bus(sink_index)
	AudioServer.set_bus_name(sink_index, SINK_BUS)
	AudioServer.set_bus_volume_db(sink_index, -80.0)
	thunder_index = AudioServer.get_bus_index(THUNDER_BUS)
	AudioServer.set_bus_send(thunder_index, SINK_BUS)
	capture = AudioEffectCapture.new()
	capture.buffer_length = 2.0
	AudioServer.add_bus_effect(thunder_index, capture)

func drain_capture() -> float:
	var peak := 0.0
	while capture != null and capture.get_frames_available() > 0:
		for frame in capture.get_buffer(mini(capture.get_frames_available(), 2048)):
			peak = maxf(peak, maxf(absf(frame.x), absf(frame.y)))
	return peak

func stop_fixture_loops() -> void:
	for path in ["OutsideAmbience", "WaterAmbience/Shore", "WaterAmbience/OpenWater", "WaterAmbience/River", "WaterAmbience/Underwater"]:
		var player := scene.get_node_or_null(path)
		if player != null and player.has_method("stop"):
			player.call("stop")

func fail(message: String) -> void:
	push_error("Thunder audio capture test failed: " + message)
	cleanup_and_quit(1)

func cleanup_and_quit(code: int) -> void:
	if audio != null:
		audio.set("Enabled", false)
		for voice in audio.get_children():
			if voice is AudioStreamPlayer3D:
				voice.stop()
	if thunder_index >= 0 and AudioServer.get_bus_index(SINK_BUS) >= 0:
		AudioServer.set_bus_send(thunder_index, &"Master")
	if sink_index >= 0 and sink_index < AudioServer.bus_count:
		AudioServer.remove_bus(sink_index)
	if scene != null:
		scene.queue_free()
	quit(code)
