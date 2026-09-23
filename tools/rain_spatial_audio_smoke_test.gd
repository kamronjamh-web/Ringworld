extends SceneTree

# Production emitter geometry + measured stereo output. The scene is disposable;
# a fixture-only camera and quiet buses isolate direction/distance from weather.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const RunnerScript := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")
const OFFSETS := [Vector2(12, 0), Vector2(-12, 0), Vector2(0, 12), Vector2(0, -12)]
var scene: Node
var ambience: Node
var explorer: Node
var bridge: Node
var grid: Node
var camera: Camera3D
var capture: AudioEffectCapture
var runner: RefCounted
var started_usec := 0
var finished := false
var failed := false
var circumference := 0.0

func _initialize() -> void:
	started_usec = Time.get_ticks_usec()
	call_deferred("run")

func _process(_delta: float) -> bool:
	if not finished and real_time() > 45.0: check(false, "real-time deadline exceeded")
	return false

func run() -> void:
	AudioServer.set_bus_volume_db(0, -80.0)
	scene = SCENE.instantiate()
	if "--rain-spatial-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", scene, 5000000.0)
		launcher.free()
	root.add_child(scene)
	await frames(5)
	ambience = scene.get_node("WeatherAmbience")
	explorer = scene.get_node("Explorer")
	bridge = scene.get_node("SunshineCloudBridge")
	grid = scene.get_node("ChunkGridManager")
	circumference = float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var weather := scene.get_node("RingVolumetricClouds")
	weather.set_process(false)
	weather.set("Coverage", -10.0)
	weather.set("WeatherClusteringStrength", 0.0)
	bridge.set_process(false)
	explorer.set_physics_process(false)
	var local := scene.get_node("LocalWeatherEffects")
	local.set("EnableLightning", false)
	camera = Camera3D.new()
	scene.add_child(camera)
	camera.make_current()
	place(10000.0, 10000.0)
	runner = RunnerScript.new()
	runner.ConfigureLocalWeather(local, .45, Vector2.ZERO, false)
	await real_wait(1.7)
	if not check_contract(): return
	if not await geometry_cases(): return
	if not await stereo_and_distance_cases(): return
	if failed: return
	finished = true
	scene.queue_free()
	await frames(3)
	print("RAIN_SPATIAL_AUDIO_SMOKE=PASS: eight pooled mono spatial voices, staggered phases, translation/curvature/seam/edge placement, camera-independent positions, stereo reversal, distance falloff, out-of-range silence")
	quit(0)

func check_contract() -> bool:
	var d := diag()
	if not check(int(d.player_count) == 10 + int(d.surface_detail_pool_count) and int(d.surface_detail_pool_count) == 24 and int(d.rain_spatial_player_count) == 8
		and int(d.nonpositional_player_count) == 2 and int(d.rain_playing_count) == 4,
		"fixed pool or rain-layer selection differs"): return false
	var positions: Array[float] = []
	for group_name in ["RainLight", "RainHeavy"]:
		var group := ambience.get_node(group_name)
		# Channel count is verified independently with ffprobe; Godot's Vorbis
		# resource does not expose the WAV-only stereo property.
		var source_asset: AudioStream = ambience.get(group_name + "Stream")
		if not check(source_asset.resource_path.ends_with("_spatial_loop.ogg"), "scene did not select the mono rain asset"): return false
		if not check(group is Node3D and group.get_child_count() == 4, "rain group is not four spatial sources"): return false
		for index in 4:
			var voice := group.get_child(index) as AudioStreamPlayer3D
			if not check(voice != null and voice.stream is AudioStreamOggVorbis
				and voice.stream.loop and voice.panning_strength > 0.0 and voice.doppler_tracking == 0
				and is_equal_approx(voice.pitch_scale, 1.0) and voice.max_polyphony == 1,
				"rain source lost mono/loop/spatial/pitch contract"): return false
			if group_name == "RainLight": positions.append(voice.get_playback_position())
	for first in 4:
		for second in range(first + 1, 4):
			if not check(absf(positions[first] - positions[second]) > 1.0, "rain loops started in sync"): return false
	return true

func geometry_cases() -> bool:
	for around in [10000.0, 10080.0, circumference * .25, circumference - 6.0]:
		place(around, 10000.0)
		await frames(5)
		if not check_positions(around, 10000.0): return false
		if not check(int(diag().rain_valid_emitter_count) == 4 and int(diag().rain_playing_count) == 4,
			"curvature/movement/seam lost rain sources"): return false
	for across in [3.0, float(grid.get("RingAcrossWidthMeters")) - 3.0]:
		place(10000.0, across)
		await frames(5)
		if not check_positions(10000.0, across): return false
		var rejected := 3 if across < 12.0 else 2
		var voice := ambience.get_node("RainLight").get_child(rejected) as AudioStreamPlayer3D
		if not check(not voice.playing and voice.volume_db <= -80.0 and int(diag().rain_valid_emitter_count) == 3,
			"emitter outside physical ring edge was restarted"): return false
	place(10000.0, 10000.0)
	await frames(5)
	return true

func check_positions(around: float, across: float) -> bool:
	for index in 4:
		var offset: Vector2 = OFFSETS[index]
		var expected := world(fposmod(around + offset.x, circumference), 201.5, across + offset.y)
		for group_name in ["RainLight", "RainHeavy"]:
			var voice := ambience.get_node(group_name).get_child(index) as AudioStreamPlayer3D
			if not check(voice.global_position.distance_to(expected) < .3, "rain source did not follow canonical ring placement"): return false
	return true

func stereo_and_distance_cases() -> bool:
	# Route seven voices to a quiet fixture sink; leave one actual production
	# emitter on Rain. We do not replace its audio or spatialization settings.
	AudioServer.add_bus(1)
	AudioServer.set_bus_name(1, &"SpatialRainTestSink")
	AudioServer.set_bus_volume_db(1, -80.0)
	for group_name in ["RainLight", "RainHeavy", "RainSurfaceDetail"]:
		for voice in ambience.get_node(group_name).get_children():
			voice.bus = &"SpatialRainTestSink"
	var source := ambience.get_node("RainLight/Emitter1") as AudioStreamPlayer3D
	source.bus = &"Rain"
	capture = AudioEffectCapture.new()
	capture.buffer_length = 1.0
	AudioServer.add_bus_effect(AudioServer.get_bus_index(&"Rain"), capture)
	var original := source.global_position
	var up := (world(10000.0, 211.5, 10000.0) - camera.global_position).normalized()
	var toward_source := (source.global_position - camera.global_position).normalized()
	var forward := up.cross(toward_source).normalized()
	camera.look_at(camera.global_position + forward, up)
	var right := await measure(source)
	camera.look_at(camera.global_position - forward, up)
	var left := await measure(source)
	if not check(source.global_position.distance_to(original) < .01, "camera turn rotated/moved rain field"): return false
	if not check(float(right.rms_right) > float(right.rms_left) * 1.5
		and float(left.rms_left) > float(left.rms_right) * 1.5,
		"stereo output did not reverse with listener turn: " + str([right, left])): return false
	print("RAIN_SPATIAL_STEREO ", {"right": right, "left": left})
	# A detached test listener measures real attenuation without dragging the
	# character's local precipitation field along with it.
	camera.global_position = world(9960.0, 201.5, 10000.0)
	camera.look_at(source.global_position, up)
	var far := await measure(source)
	var near_energy := float(right.rms_left) * float(right.rms_left) + float(right.rms_right) * float(right.rms_right)
	var far_energy := float(far.rms_left) * float(far.rms_left) + float(far.rms_right) * float(far.rms_right)
	if not check(far_energy > .0000000001 and far_energy < near_energy * .3,
		"spatial distance did not attenuate actual rain PCM"): return false
	print("RAIN_SPATIAL_DISTANCE ", {"near_energy": near_energy, "far_energy": far_energy})
	camera.global_position = world(9800.0, 201.5, 10000.0)
	await frames(5)
	# Stop is queued to physics/audio mixing; discard the already-mixed tail
	# before asserting a new, fully out-of-range PCM window is silent.
	await real_wait(.25)
	capture.clear_buffer()
	await real_wait(.2)
	var silent := drain()
	print("RAIN_SPATIAL_OUT_OF_RANGE ", {"state": diag(), "pcm": silent})
	return check(int(diag().rain_playing_count) == 0 and int(diag().rain_valid_emitter_count) == 0
		and float(diag().duck) == 1.0 and float(silent.peak) <= .000001,
		"out-of-range rain continued playing or ducking")

func measure(source: AudioStreamPlayer3D) -> Dictionary:
	source.play(3.0)
	await real_wait(.2)
	capture.clear_buffer()
	await real_wait(.35)
	return drain()

func drain() -> Dictionary:
	var energy := Vector2.ZERO
	var peak := 0.0
	var count := 0
	while capture.get_frames_available() > 0:
		for frame in capture.get_buffer(mini(capture.get_frames_available(), 2048)):
			if not check(is_finite(frame.x) and is_finite(frame.y), "nonfinite PCM"): return {}
			energy.x += frame.x * frame.x
			energy.y += frame.y * frame.y
			peak = maxf(peak, maxf(absf(frame.x), absf(frame.y)))
			count += 1
	if not check(count > 1024, "capture backend did not supply PCM frames"): return {}
	return {"rms_left": sqrt(energy.x / count), "rms_right": sqrt(energy.y / count), "peak": peak, "frames": count}

func place(around: float, across: float) -> void:
	bridge.call("SetBenchmarkViewerPosition", around, 200.0, across)
	explorer.call("ClearBenchmarkOverride")
	explorer.call("_PhysicsProcess", 0.0)
	explorer.set_physics_process(false)
	camera.global_position = world(around, 201.5, across)
	camera.make_current()

func world(around: float, height: float, across: float) -> Vector3:
	return grid.call("CanonicalRingPositionToWorld", Vector3(around, height, across))

func diag() -> Dictionary:
	return ambience.call("GetWeatherAmbienceDiagnostics") as Dictionary

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
	push_error("RAIN_SPATIAL_AUDIO_SMOKE=FAIL " + message)
	quit(1)
	return false
