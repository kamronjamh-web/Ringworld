extends SceneTree

# Real controller/state tests. PCM is independently checked by the capture
# fixture. Teleports, time changes and muted output are test-only.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
var scene: Node
var weather: Node
var bridge: Node
var explorer: Node
var audio: Node
var started_usec := 0
var failed := false
var finished := false
var sequence := 0

func _initialize() -> void:
	started_usec = Time.get_ticks_usec()
	call_deferred("run")

func _process(_delta: float) -> bool:
	if not finished and real_time() > 55.0:
		check(false, "real-tick deadline exceeded")
	return false

func run() -> void:
	AudioServer.set_bus_volume_db(0, -80.0)
	scene = TEST_SCENE.instantiate()
	if "--thunder-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", scene, 5000000.0)
		launcher.free()
	root.add_child(scene)
	await frames(4)
	weather = scene.get_node("RingVolumetricClouds")
	bridge = scene.get_node("SunshineCloudBridge")
	explorer = scene.get_node("Explorer")
	audio = scene.get_node("WeatherAudio")
	weather.set_process(false)
	explorer.set_physics_process(false)
	scene.get_node("LocalWeatherEffects").set("EnableLightning", false)
	if not await speed_case(0.5): return
	if not await speed_case(2.0): return
	if not await pause_case(): return
	for reset_kind in ["audio", "source", "rewind", "jump"]:
		if not await reset_case(reset_kind): return
	if not await movement_case(): return
	if not await immersion_case(): return
	if not await space_case(): return
	if not await burst_case(): return
	if not await controls_case(): return
	if failed: return
	finished = true
	Engine.time_scale = 1.0
	scene.queue_free()
	await frames(3)
	print("THUNDER_BEHAVIOR_SMOKE=PASS: real-time scale, pending/voice pause, resets, source retention, movement, water/space, 12-strike voice cap, UI")
	quit(0)

func speed_case(scale: float) -> bool:
	Engine.time_scale = scale
	var event := await preview()
	if event.is_empty() or not await wait_for_play(event): return false
	var elapsed := real_time() - float(event.wall)
	if not check(absf(elapsed - float(event.delay)) < 0.25, "time scale %.1f changed real travel: %.3f vs %.3f" % [scale, elapsed, event.delay]): return false
	print("THUNDER_TIMING ", {"scale": scale, "real_seconds": elapsed, "expected_seconds": event.delay})
	Engine.time_scale = 1.0
	return true

func pause_case() -> bool:
	var event := await preview()
	if event.is_empty(): return false
	var before := float(diag().audio_clock)
	paused = true
	await real_wait(0.3)
	if not check(absf(float(diag().audio_clock) - before) < 0.04 and int(diag().played_count) == int(event.base), "pending clock advanced while paused"): return false
	paused = false
	if not await wait_for_play(event): return false
	await real_wait(0.1)
	var voice := active_voice()
	if not check(voice != null, "pause case has no actual active stream"): return false
	paused = true
	await frames(2)
	var position_before := voice.get_playback_position()
	await real_wait(0.3)
	if not check(voice.stream_paused and absf(voice.get_playback_position() - position_before) < 0.04, "actual stream advanced while paused"): return false
	paused = false
	await real_wait(0.2)
	return check(not voice.stream_paused and voice.get_playback_position() > position_before + 0.08, "actual stream did not resume")

func reset_case(kind: String) -> bool:
	var event := await preview()
	if event.is_empty() or not check(int(diag().pending_count) > 0, "reset needs a pending positive control"): return false
	var resets_before := int(diag().reset_count)
	match kind:
		"audio": audio.set("Enabled", false)
		"source": weather.set("EnableSharedLightning", false)
		"rewind": weather.set("CloudSimulationTimeSeconds", -1.0)
		"jump": weather.set("CloudSimulationTimeSeconds", 100.0)
	await frames(3)
	if not check(int(diag().pending_count) == 0 and int(diag().active_voice_count) == 0 and int(diag().reset_count) > resets_before, kind + " did not clear pending/voices"): return false
	audio.set("Enabled", true)
	weather.set("EnableSharedLightning", true)
	await real_wait(float(event.delay) + 0.2)
	return check(int(diag().played_count) == int(event.base), kind + " replayed a stale frozen event")

func movement_case() -> bool:
	var event := await preview(13430.0)
	if event.is_empty(): return false
	# Remove the visual through the real expiry path while sound is in flight.
	weather.set("CloudSimulationTimeSeconds", 1.0)
	weather.call("_Process", 0.0)
	var active_events: Array = weather.call("GetActiveLightningEventDiagnostics")
	for item in active_events:
		if not check(int(item.event_id) != int(event.id), "visual event did not expire"): return false
	place(10000.0, float(event.height), 10343.0)
	await frames(3)
	event.delay = expected_delay(float(event.ground))
	if not await wait_for_play(event): return false
	var voice := active_voice()
	if not check(voice != null, "moving listener has no actual voice"): return false
	var grid := scene.get_node("ChunkGridManager")
	var source: Vector3 = grid.call("CanonicalRingPositionToWorld", Vector3(10000.0, float(event.ground) + 2.0, 10000.0))
	return check(voice.global_position.distance_to(source) < 1.0, "voice did not retain original source")

func immersion_case() -> bool:
	var event := await preview()
	if event.is_empty() or not await wait_for_play(event): return false
	# Exercise actual generated water, not just manually changed metadata.
	place(10000.0, -2.0, 10000.0)
	if not check(bool(explorer.get_meta("ring_camera_underwater", false)), "fixture did not enter real generated water"): return false
	await frames(3)
	return check(not bool(diag().can_hear) and int(diag().active_voice_count) == 0, "underwater did not silence active thunder")

func space_case() -> bool:
	var event := await preview()
	if event.is_empty() or not await wait_for_play(event): return false
	place(10000.0, 30000.0, 10000.0)
	await frames(3)
	return check(not bool(diag().can_hear) and int(diag().active_voice_count) == 0, "above-atmosphere listener still hears thunder")

func controls_case() -> bool:
	var toggle: CheckButton
	var volume: HSlider
	for node in scene.find_children("*", "CheckButton", true, false):
		if node.text == "Thunder": toggle = node
	for node in scene.find_children("*", "Label", true, false):
		if node.text == "Thunder volume":
			volume = node.get_parent().get_child(node.get_index() + 1).get_child(0)
	if not check(toggle != null and volume != null, "thunder controls missing"): return false
	toggle.button_pressed = false
	await frames(2)
	if not check(not bool(audio.get("Enabled")), "UI toggle not connected"): return false
	toggle.button_pressed = true
	volume.value = -18.0
	return check(bool(audio.get("Enabled")) and is_equal_approx(float(audio.get("ThunderVolumeDb")), -18.0), "UI controls not applied")

func burst_case() -> bool:
	audio.set("Enabled", false)
	weather.set("EnableSharedLightning", false)
	weather.call("_Process", 0.0)
	await frames(3)
	weather.set("EnableSharedLightning", true)
	audio.set("Enabled", true)
	await frames(3)
	var base := int(diag().played_count)
	for index in 12:
		var event: Dictionary = weather.call("TriggerSharedLightningPreviewAt", 10000.0, 10000.0, -99500 - index, 1.0, 45000.0)
		if not check(not event.is_empty(), "burst preview missing"): return false
		if index == 0: place(10000.0, maxf(200.0, float(event.ground_vertical_meters) + 200.0), 10686.0)
	await frames(3)
	if not check(int(diag().pending_count) == 12, "burst did not queue twelve actual shared events"): return false
	var start := real_time()
	while int(diag().pending_count) > 0:
		if not check(real_time() - start < 5.0, "burst did not arrive"): return false
		await frames(1)
	if not check(int(diag().played_count) == base + 8 and int(diag().active_voice_count) == 8, "burst exceeded or underfilled eight-voice pool"): return false
	audio.set("Enabled", false)
	await frames(3)
	return check(int(diag().active_voice_count) == 0, "disable did not stop full voice pool")

func preview(across: float = 10686.0) -> Dictionary:
	audio.set("Enabled", false)
	await frames(3)
	weather.set("CloudSimulationTimeSeconds", 0.0)
	weather.set("EnableSharedLightning", true)
	audio.set("Enabled", true)
	await frames(3)
	sequence += 1
	var result := {"base": int(diag().played_count), "wall": real_time()}
	var event: Dictionary = weather.call("TriggerSharedLightningPreviewAt", 10000.0, 10000.0, -99200 - sequence, 1.0, 45000.0)
	if not check(not event.is_empty(), "preview missing"): return {}
	result.id = int(event.event_id)
	result.ground = float(event.ground_vertical_meters)
	result.height = maxf(200.0, float(result.ground) + 200.0)
	place(10000.0, float(result.height), across)
	await frames(3)
	result.delay = expected_delay(float(result.ground))
	return result

func place(around: float, height: float, across: float) -> void:
	bridge.call("SetBenchmarkViewerPosition", around, height, across)
	explorer.call("ClearBenchmarkOverride")
	explorer.call("_PhysicsProcess", 0.0)
	explorer.set_physics_process(false)

func expected_delay(ground: float) -> float:
	var d := diag()
	return sqrt(pow(float(d.listener_across) - 10000.0, 2) + pow(float(d.listener_vertical) - ground - 2.0, 2)) / 343.0

func wait_for_play(event: Dictionary) -> bool:
	var start := real_time()
	while int(diag().played_count) == int(event.base):
		if not check(real_time() - start < 12.0, "arrival timeout"): return false
		await frames(1)
	return check(int(diag().played_count) == int(event.base) + 1 and int(diag().last_id) == int(event.id)
		and absf(float(diag().last_delay_seconds) - float(event.delay)) < 0.05, "arrival identity/count/distance differs")

func active_voice() -> AudioStreamPlayer3D:
	for child in audio.get_children():
		if child is AudioStreamPlayer3D and child.playing: return child
	return null

func diag() -> Dictionary:
	return audio.call("GetThunderDiagnostics") as Dictionary

func frames(count: int) -> void:
	for _index in count: await process_frame

func real_wait(seconds: float) -> void:
	await create_timer(seconds, true, false, true).timeout

func real_time() -> float:
	return float(Time.get_ticks_usec() - started_usec) / 1000000.0

func check(condition: bool, message: String) -> bool:
	if condition: return true
	failed = true
	finished = true
	paused = false
	Engine.time_scale = 1.0
	push_error("THUNDER_BEHAVIOR_SMOKE=FAIL " + message)
	quit(1)
	return false
