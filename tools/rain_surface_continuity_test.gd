extends SceneTree

# Sequential production regression: surface-only PCM must remain continuous while
# the benchmark viewer walks/runs across the old refresh boundary.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const RUNNER := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")
const SURFACE := preload("res://scripts/Weather/RainSurfaceVerificationRunner.cs")
var scene: Node
var ambience: Node
var grid: Node
var bridge: Node
var explorer: Node
var local: Node
var weather_runner: RefCounted
var surface_runner: RefCounted
var capture: AudioEffectCapture
var land: Dictionary
var traveled := 0.0
var deadline := 0
var failures := 0
var windows := 0
var measuring := false
var finished := false
var window_frames := 2205
var min_peak := INF
var max_sources := 0
var phase_checks := 0
var discarded_baseline := 0

func _initialize() -> void:
	Engine.max_fps = 120
	deadline = Time.get_ticks_usec() + 45000000
	call_deferred("run")

func _process(_delta: float) -> bool:
	if finished: return false
	if Time.get_ticks_usec() > deadline: require(false, "45 second deadline")
	if is_instance_valid(ambience):
		var d := diag()
		require(int(d.surface_detail_valid_count) <= 24 and int(d.surface_queries_frame) <= 3 and int(d.surface_density_query_frame) <= 48, "per-frame scheduling budget")
	return false

func run() -> void:
	AudioServer.set_bus_volume_db(0, -80)
	scene = SCENE.instantiate()
	if "--rain-continuity-certified" in OS.get_cmdline_user_args():
		var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", scene, 5000000.0); launcher.free()
	root.add_child(scene)
	await frames(6)
	# A/B isolation of the optional regional visual renderer; never changes audio.
	if "--rain-continuity-no-curtains" in OS.get_cmdline_user_args():
		var curtains := scene.get_node_or_null("DistantRainCurtains")
		if curtains: curtains.set("Enabled", false)
	ambience = scene.get_node("WeatherAmbience")
	grid = scene.get_node("ChunkGridManager")
	bridge = scene.get_node("SunshineCloudBridge")
	explorer = scene.get_node("Explorer")
	local = scene.get_node("LocalWeatherEffects")
	weather_runner = RUNNER.new()
	surface_runner = SURFACE.new()
	var weather := scene.get_node("RingVolumetricClouds")
	weather.set_process(false)
	weather.set("Coverage", -10.0); weather.set("WeatherClusteringStrength", 0.0)
	bridge.set_process(false)
	local.set("EnableLightning", false)
	explorer.set_physics_process(false)
	AudioServer.add_bus()
	var sink := AudioServer.bus_count - 1
	AudioServer.set_bus_name(sink, &"ContinuityFieldSink")
	AudioServer.set_bus_volume_db(sink, -80)
	for group_name in ["RainLight", "RainHeavy"]:
		for voice in ambience.get_node(group_name).get_children(): voice.bus = &"ContinuityFieldSink"
	capture = AudioEffectCapture.new()
	capture.buffer_length = 1.0
	window_frames = roundi(AudioServer.get_mix_rate() * .05)
	AudioServer.add_bus_effect(AudioServer.get_bus_index(&"Rain"), capture)
	land = surface_runner.FindSurface(grid, true)
	if not require(bool(land.found), "no generated water control"): return
	# A water route avoids legitimate changing slope/roof material across travel.
	for offset in range(0, 101, 10):
		var point: Dictionary = ambience.call("GetRainSurfaceDiagnostic", int(land.around) + offset, int(land.across))
		if not require(bool(point.found) and point.material == "Water" and absf(float(point.top) - float(land.top)) < .01, "flat 100m water route unavailable"): return
	weather_runner.ConfigureLocalWeather(local, 1.0, Vector2.ZERO, false)
	if not await move_segment(0.0, 2.0, false): return # startup / first eight placement queries
	capture.clear_buffer()
	discarded_baseline = capture.get_discarded_frames()
	measuring = true
	if not require(int(diag().surface_detail_pool_count) == 24 and int(diag().surface_queries_frame) <= 3 and int(diag().surface_density_query_frame) <= 48, "capacity or query contract"): return
	# 4m/s walk crosses 18m; 10m/s run carries old anchors beyond 42m.  Each
	# 50ms window is drained, finite, and non-silent while rain is active.
	if not await motion_case(4.0, 5.2, "walk"): return
	if not await motion_case(10.0, 4.5, "run"): return
	if not await motion_case(-4.0, 3.0, "reverse"): return
	if not await move_segment(0.0, .6, true): return
	if not require(float(diag().surface_lead_meters) == 0.0, "stop retained velocity lead"): return
	if not require(windows > 100 and failures == 0 and phase_checks > 10 and max_sources > 8, "continuous surface PCM/overlap/phase controls"): return
	finished = true
	print("RAIN_SURFACE_CONTINUITY=PASS windows=", windows, " min_peak=", min_peak, " max_sources=", max_sources, " phase_checks=", phase_checks, " diagnostics=", diag())
	scene.queue_free(); await frames(3); quit(0)

func motion_case(speed: float, seconds: float, label: String) -> bool:
	var before := tracked_audible()
	var start := Time.get_ticks_usec()
	var last := start
	var phase := {}
	var lead_max := 0.0
	while float(Time.get_ticks_usec() - start) / 1000000.0 < seconds:
		if Time.get_ticks_usec() > deadline: return require(false, "45 second deadline")
		var now := Time.get_ticks_usec()
		var dt := float(now - last) / 1000000.0
		last = now
		traveled += speed * dt
		place(traveled)
		for key in before:
			if not phase.has(key):
				var v: AudioStreamPlayer3D = ambience.get_node("RainSurfaceDetail/Surface" + str(int(key) + 1))
				phase[key] = {"position": v.get_playback_position(), "time": now, "stream": v.stream}
		await process_frame
		if finished or (measuring and not pcm_window()): return false
		var d := diag()
		max_sources = maxi(max_sources, int(d.detail_playing_count))
		lead_max = maxf(lead_max, float(d.surface_lead_meters))
		for key in before.keys():
			var old: Dictionary = before[key]
			var point := point_for(int(old.index))
			# Only demand continuity inside the conservative 38m audible zone.
			if canonical_distance(old, listener()) < 38.0:
				if not require(point.size() > 0, label + " lost audible canonical anchor"): return false
				var voice: AudioStreamPlayer3D = ambience.get_node("RainSurfaceDetail/Surface" + str(int(old.index) + 1))
				if not require(voice.playing and voice.stream == phase[key].stream and point.around == old.around and point.across == old.across and point.vertical == old.vertical, label + " moved/stole audible canonical anchor"): return false
				var checked_now := Time.get_ticks_usec()
				var elapsed := float(checked_now - int(phase[key].time)) / 1000000.0
				if elapsed > .2:
					var advanced := fposmod(voice.get_playback_position() - float(phase[key].position), voice.stream.get_length())
					if not require(absf(advanced - elapsed) < .1, label + " restarted surface phase index=%s advanced=%.6f elapsed=%.6f before=%.6f after=%.6f length=%.6f" % [key, advanced, elapsed, float(phase[key].position), voice.get_playback_position(), voice.stream.get_length()]): return false
					phase[key] = {"position": voice.get_playback_position(), "time": checked_now, "stream": voice.stream}
					phase_checks += 1
			else:
				# Once outside the protected zone, later reentry may legitimately use a new source.
				before.erase(key)
	if speed != 0.0 and not require(lead_max > .1, label + " no walking lead"): return false
	return true

func move_segment(speed: float, seconds: float, stop := false) -> bool:
	return await motion_case(speed, seconds, "startup" if not stop else "stop")

func place(offset: float) -> void:
	bridge.call("SetBenchmarkViewerPosition", float(land.around) + .5 + offset, float(land.top) + 1.0, float(land.across) + .5)
	explorer.call("ClearBenchmarkOverride")
	explorer.call("_PhysicsProcess", 0.0)

func pcm_window() -> bool:
	if not require(capture.get_discarded_frames() == discarded_baseline, "PCM capture overflow"): return false
	while capture.get_frames_available() >= window_frames:
		var peak := 0.0
		for sample in capture.get_buffer(window_frames):
			if not require(is_finite(sample.x) and is_finite(sample.y), "nonfinite PCM"): return false
			peak = maxf(peak, maxf(absf(sample.x), absf(sample.y)))
		windows += 1
		min_peak = minf(min_peak, peak)
		if not require(peak > .0000001, "silent PCM window " + str(windows)): return false
	return true

func tracked_audible() -> Dictionary:
	var result := {}
	for point in diag().surface_detail_positions:
		if bool(point.playing): result[int(point.index)] = point
	return result
func point_for(index: int) -> Dictionary:
	for point in diag().surface_detail_positions:
		if int(point.index) == index: return point
	return {}
func listener() -> Dictionary:
	var d := diag()
	return {"around": d.listener_around, "across": d.listener_across, "vertical": d.listener_vertical}
func canonical_distance(a: Dictionary, b: Dictionary) -> float:
	var x := float(a.around) - float(b.around)
	var c := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	x = fposmod(x + c * .5, c) - c * .5
	return sqrt(x * x + pow(float(a.across) - float(b.across), 2.0) + pow(float(a.vertical) - float(b.vertical), 2.0))
func diag() -> Dictionary: return ambience.call("GetWeatherAmbienceDiagnostics")
func frames(count: int) -> void:
	for _i in count: await process_frame
func require(ok: bool, message: String) -> bool:
	if ok: return true
	finished = true
	failures += 1
	push_error("RAIN_SURFACE_CONTINUITY=FAIL: " + message + (" " + str(diag()) if is_instance_valid(ambience) else ""))
	quit(1)
	return false
