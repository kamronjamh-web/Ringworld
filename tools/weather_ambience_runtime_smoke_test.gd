extends SceneTree

# Disposable production-scene test. Capture is upstream of a quiet Master bus;
# positive PCM is required, not merely a playing flag or a nonzero target.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const RunnerScript := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")
const EPSILON := 0.000001
var scene: Node
var ambience: Node
var local: Node
var weather: Node
var bridge: Node
var explorer: Node
var runner: RefCounted
var captures: Array[AudioEffectCapture] = []
var started_usec := 0
var budget_started := 0.0
var budget_base := 0
var failed := false
var finished := false

func _initialize() -> void:
	started_usec = Time.get_ticks_usec()
	call_deferred("run")

func _process(_delta: float) -> bool:
	if finished: return false
	if real_time() > 55.0:
		check(false, "real-tick deadline exceeded")
	if is_instance_valid(ambience) and budget_started > 0.0:
		var d := diag()
		check(int(d.probe_frame) <= 1 and int(d.probe_slots) == 16 and int(d.player_count) == 34 and int(d.rain_spatial_player_count) == 8 and int(d.nonpositional_player_count) == 2,
			"per-frame probe or fixed allocation cap exceeded")
		check(int(d.probe_total) - budget_base <= ceili((real_time() - budget_started) * 8.0) + 1,
			"more than eight actual weather queries per real second")
	return false

func run() -> void:
	AudioServer.set_bus_volume_db(0, -80.0)
	scene = SCENE.instantiate()
	if "--weather-ambience-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", scene, 5000000.0)
		launcher.free()
	root.add_child(scene)
	await frames(5)
	ambience = scene.get_node("WeatherAmbience")
	local = scene.get_node("LocalWeatherEffects")
	weather = scene.get_node("RingVolumetricClouds")
	bridge = scene.get_node("SunshineCloudBridge")
	explorer = scene.get_node("Explorer")
	runner = RunnerScript.new()
	weather.set_process(false)
	bridge.set_process(false)
	local.set("EnableLightning", false)
	explorer.set_physics_process(false)
	# Fixture-only clear baseline. Spawn chance has a nonzero lower bound;
	# strongly negative coverage guarantees zero natural precipitation instead.
	weather.set("Coverage", -10.0)
	weather.set("WeatherClusteringStrength", 0.0)
	weather.set("CloudSimulationTimeSeconds", 0.0)
	place(10000.0, 200.0, 10000.0)
	for bus_name in [&"Rain", &"Wind", &"DistantStorm"]:
		var bus := AudioServer.get_bus_index(bus_name)
		if not check(bus >= 0, "dedicated bus missing"): return
		var capture := AudioEffectCapture.new()
		capture.buffer_length = 2.0
		AudioServer.add_bus_effect(bus, capture)
		captures.append(capture)
	if not check(int(diag().asset_count) == 7, "expected seven valid streams"): return
	for group_name in ["RainLight", "RainHeavy"]:
		var group := ambience.get_node(group_name)
		for emitter_name in ["Emitter1", "Emitter2", "Emitter3", "Emitter4"]:
			var emitter := group.get_node(emitter_name)
			if not check(emitter is AudioStreamPlayer3D and emitter.stream is AudioStreamOggVorbis and emitter.stream.loop and is_equal_approx(emitter.pitch_scale, 1.0), "rain emitter is not a normal-pitch 3D loop"): return
	for bed_name in ["Wind", "DistantStorm"]:
		var bed := ambience.get_node(bed_name)
		if not check(bed is AudioStreamPlayer and bed.stream is AudioStreamOggVorbis and bed.stream.loop
			and is_equal_approx(bed.pitch_scale, 1.0), "nonpositional bed lost its looping/pitch contract"): return
	budget_started = real_time()
	budget_base = int(diag().probe_total)
	if not await local_audio_cases(): return
	if not await pause_case(): return
	if not await lifecycle_cases(): return
	if not await distant_case(): return
	if not await controls_case(): return
	if not await missing_stream_case(): return
	if failed: return
	var result := diag()
	finished = true
	scene.queue_free()
	await frames(3)
	print("WEATHER_AMBIENCE_RUNTIME_SMOKE=PASS: local/distant PCM, snow silence, real pause/resume, disable/source/local reset, water/space, bounded probes, invalidation, UI, missing stream ", result)
	quit(0)

func local_audio_cases() -> bool:
	runner.ConfigureLocalWeather(local, 0.0, Vector2.ZERO, false)
	await real_wait(2.2)
	var peaks := await capture_peaks()
	if not check(max_gain("target") <= EPSILON and max_gain("current") <= EPSILON and maxf(peaks[0], maxf(peaks[1], peaks[2])) <= EPSILON,
		"dry baseline was not silent: " + str(diag())): return false
	runner.ConfigureLocalWeather(local, .45, Vector2(8, 0), false)
	await real_wait(1.7)
	peaks = await capture_peaks()
	var d := diag()
	if not check(float(d.current[0]) > .1 and float(d.current[1]) == 0.0 and is_equal_approx(float(d.source_wind), 8.0)
		and peaks[0] > .00001 and peaks[1] > .00001, "light rain/wind did not produce PCM"): return false
	print("WEATHER_AMBIENCE_LIGHT_PCM ", peaks)
	runner.ConfigureLocalWeather(local, 1.0, Vector2(16, 0), false)
	await real_wait(1.7)
	peaks = await capture_peaks()
	d = diag()
	if not check(float(d.current[0]) == 0.0 and float(d.current[1]) > .99 and float(d.current[2]) > .99
		and peaks[0] > .00001 and peaks[1] > .00001 and float(d.duck) < .7, "heavy rain/wind/duck failed"): return false
	print("WEATHER_AMBIENCE_HEAVY_PCM ", peaks)
	runner.ConfigureLocalWeather(local, 1.0, Vector2.ZERO, true)
	await real_wait(1.7)
	peaks = await capture_peaks()
	return check(bool(diag().source_snow) and max_gain("current") == 0.0 and maxf(peaks[0], maxf(peaks[1], peaks[2])) <= EPSILON,
		"snow with calm wind retained liquid rain audio")

func pause_case() -> bool:
	runner.ConfigureLocalWeather(local, 1.0, Vector2(16, 0), false)
	await real_wait(1.7)
	var voice: AudioStreamPlayer3D = ambience.get_node("RainHeavy/Emitter1")
	if not check(voice.playing, "pause needs a playing stream"): return false
	var before := float(diag().clock)
	var probes := int(diag().probe_total)
	paused = true
	await frames(2)
	var position := voice.get_playback_position()
	await real_wait(.3)
	if not check(voice.stream_paused and absf(voice.get_playback_position() - position) < .04
		and absf(float(diag().clock) - before) < .04 and int(diag().probe_total) == probes,
		"pause advanced stream, clock or probes"): return false
	paused = false
	await real_wait(.2)
	return check(not voice.stream_paused and voice.get_playback_position() > position + .08, "stream did not resume")

func lifecycle_cases() -> bool:
	for node in [ambience, weather, local]:
		node.set("Enabled", false)
		await frames(3)
		if not check(suspended(), "disable did not silence/reset ambience"): return false
		var resets := int(diag().reset_count)
		await frames(3)
		if not check(int(diag().reset_count) == resets, "disabled node repeatedly invalidates cache"): return false
		node.set("Enabled", true)
		runner.ConfigureLocalWeather(local, 1.0, Vector2(16, 0), false)
		await real_wait(.15)
		if not check(bool(diag().can_hear) and int(diag().playing_count) > 0, "enable did not restart fade"): return false
	local.set_process(false)
	await frames(3)
	if not check(suspended(), "stopped local publisher retained stale audio"): return false
	local.set_process(true)
	place(10000.0, -2.0, 10000.0)
	if not check(bool(explorer.get_meta("ring_camera_underwater", false)), "fixture did not enter actual water"): return false
	await frames(3)
	if not check(suspended(), "underwater did not silence ambience"): return false
	place(10000.0, 30000.0, 10000.0)
	await frames(3)
	if not check(suspended(), "space did not silence ambience"): return false
	place(10000.0, 200.0, 10000.0)
	runner.ConfigureLocalWeather(local, 0.0, Vector2.ZERO, false)
	await frames(3)
	return check(bool(diag().can_hear), "atmosphere reentry did not resume")

func distant_case() -> bool:
	# Observer stays dry; the authoritative source, not the local override, feeds
	# a small storm at the first 4km probe. No lightning preview is emitted.
	weather.call("RegisterManualWeatherAt", "ambience_fixture", 14000.0, 10000.0, 1800.0, -9981)
	var sample: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", 14000.0, 10000.0, 0.0)
	if not check(float(sample.precipitation) > .99, "source storm positive control missing"): return false
	await real_wait(2.0)
	var peaks := await capture_peaks()
	if not check(float(diag().source_rain) == 0.0 and float(diag().current[3]) > .5
		and peaks[2] > .00001 and peaks[0] <= EPSILON, "dry observer cannot hear distant rain bed"): return false
	print("WEATHER_AMBIENCE_DISTANT_PCM ", peaks)
	var reset_count := int(diag().reset_count)
	weather.call("UnregisterManualWeather", "ambience_fixture")
	await frames(3)
	if not check(int(diag().reset_count) > reset_count and float(diag().target[3]) == 0.0,
		"manual removal retained stale distant target"): return false
	await real_wait(1.7)
	peaks = await capture_peaks()
	if not check(peaks[2] <= EPSILON, "removed storm retained audible PCM"): return false
	for jump in [100.0, 0.0]:
		reset_count = int(diag().reset_count)
		weather.set("CloudSimulationTimeSeconds", jump)
		await frames(3)
		if not check(int(diag().reset_count) > reset_count, "weather jump/rewind did not invalidate probes"): return false
	weather.call("RegisterManualWeatherAt", "ambience_fixture", 14000.0, 10000.0, 1800.0, -9981)
	await real_wait(.3)
	if not check(float(diag().target[3]) > .5, "movement needs cached positive storm"): return false
	reset_count = int(diag().reset_count)
	place(50000.0, 200.0, 10000.0)
	await frames(3)
	if not check(int(diag().reset_count) > reset_count and float(diag().target[3]) == 0.0, "teleport kept old nearby storm"): return false
	weather.call("UnregisterManualWeather", "ambience_fixture")
	place(10000.0, 200.0, 10000.0)
	return true

func controls_case() -> bool:
	var toggle: CheckButton
	var controls := {}
	for node in scene.find_children("*", "CheckButton", true, false):
		if node.text == "Weather ambience": toggle = node
	for node in scene.find_children("*", "Label", true, false):
		if node.text in ["Rain volume", "Wind volume", "Distant storm volume"]:
			controls[node.text] = node.get_parent().get_child(node.get_index() + 1).get_child(0)
	if not check(toggle != null and controls.size() == 3, "ambience controls missing"): return false
	var thunder: Node = scene.get_node("WeatherAudio")
	var thunder_enabled := bool(thunder.get("Enabled"))
	toggle.button_pressed = false
	await frames(3)
	if not check(not bool(ambience.get("Enabled")) and suspended(), "UI toggle not wired"): return false
	toggle.button_pressed = true
	controls["Rain volume"].value = -20.0
	controls["Wind volume"].value = -21.0
	controls["Distant storm volume"].value = -22.0
	return check(bool(ambience.get("Enabled")) and is_equal_approx(float(ambience.get("RainVolumeDb")), -20.0)
		and is_equal_approx(float(ambience.get("WindVolumeDb")), -21.0)
		and is_equal_approx(float(ambience.get("DistantStormVolumeDb")), -22.0)
		and bool(thunder.get("Enabled")) == thunder_enabled, "volume controls or thunder independence failed")

func missing_stream_case() -> bool:
	runner.ConfigureLocalWeather(local, .45, Vector2.ZERO, false)
	for emitter_name in ["Emitter1", "Emitter2", "Emitter3", "Emitter4"]:
		ambience.get_node("RainLight/" + emitter_name).stream = null
	await real_wait(1.7)
	var peaks := await capture_peaks()
	return check(int(diag().asset_count) == 6 and float(diag().current[0]) == 0.0
		and peaks[0] <= EPSILON and is_equal_approx(float(diag().duck), 1.0), "missing asset played or caused phantom duck")

func suspended() -> bool:
	return not bool(diag().can_hear) and int(diag().playing_count) == 0 and max_gain("current") == 0.0 and not explorer.has_meta("ring_weather_ambience_duck")

func place(around: float, height: float, across: float) -> void:
	bridge.call("SetBenchmarkViewerPosition", around, height, across)
	explorer.call("ClearBenchmarkOverride")
	explorer.call("_PhysicsProcess", 0.0)
	explorer.set_physics_process(false)

func capture_peaks() -> PackedFloat32Array:
	for capture in captures: capture.clear_buffer()
	await real_wait(.25)
	var peaks := PackedFloat32Array([0.0, 0.0, 0.0])
	for index in captures.size():
		var capture := captures[index]
		while capture.get_frames_available() > 0:
			for frame in capture.get_buffer(mini(capture.get_frames_available(), 2048)):
				if not check(is_finite(frame.x) and is_finite(frame.y), "nonfinite PCM"): return peaks
				peaks[index] = maxf(peaks[index], maxf(absf(frame.x), absf(frame.y)))
	return peaks

func diag() -> Dictionary:
	return ambience.call("GetWeatherAmbienceDiagnostics") as Dictionary

func max_gain(key: String) -> float:
	var value := 0.0
	for item in diag()[key]: value = maxf(value, float(item))
	return value

func frames(count: int) -> void:
	for _i in count: await process_frame

func real_wait(seconds: float) -> void:
	await create_timer(seconds, true, false, true).timeout

func real_time() -> float:
	return float(Time.get_ticks_usec() - started_usec) / 1000000.0

func check(condition: bool, message: String) -> bool:
	if condition: return true
	failed = true
	finished = true
	paused = false
	push_error("WEATHER_AMBIENCE_RUNTIME_SMOKE=FAIL " + message)
	quit(1)
	return false
