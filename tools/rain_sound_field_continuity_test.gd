extends SceneTree

# Headless: godot --headless --audio-driver Dummy --path . --script res://tools/rain_sound_field_continuity_test.gd [-- --rain-field-certified]
# Replaces rain_surface_continuity_test (whose get_playback_position() vs wall-clock comparison aliased against the Dummy driver's
# 4096-frame mix blocks). Continuity is now judged on what the field promises: no voice restarts while moving, bounded gain and
# cutoff slew per frame, and finite, non-silent PCM in every 50 ms window.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const WEATHER_RUNNER := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")
const SURFACE_RUNNER := preload("res://scripts/Weather/RainSurfaceVerificationRunner.cs")
const FIXTURE := preload("res://scripts/Weather/FixtureRainExposureProvider.cs")
const EYE := 1.54
var scene: Node
var ambience: Node
var grid: Node
var bridge: Node
var explorer: Node
var local: Node
var weather_runner: RefCounted
var surface_runner: RefCounted
var fixture: RefCounted
var capture: AudioEffectCapture
var deadline := 0
var finished := false
var measuring := false
var windows := 0
var min_peak := INF
var window_frames := 2205
var discarded_baseline := 0
var last_gains: Array = []
var last_clock := -1.0
var last_cutoff := 0.0
var max_rate := 0.0
var max_octaves := 0.0
var steps_checked := 0
var track_ground := false

func _initialize() -> void:
	Engine.max_fps = 120
	deadline = Time.get_ticks_usec() + 170000000
	call_deferred("run")

func _process(_delta: float) -> bool:
	if finished: return false
	if Time.get_ticks_usec() > deadline: require(false, "real-time deadline")
	if is_instance_valid(ambience):
		var d := diag()
		require(int(d.surface_queries_frame) <= 3 and int(d.surface_density_query_frame) <= 48, "per-frame scheduling budget")
	return false

func run() -> void:
	if AudioServer.get_bus_index(&"Rain") < 0: AudioServer.set_bus_layout(load("res://default_bus_layout.tres"))
	AudioServer.set_bus_volume_db(0, -80)
	scene = SCENE.instantiate()
	if "--rain-field-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", scene, 5000000.0); launcher.free()
	root.add_child(scene)
	await frames(6)
	ambience = scene.get_node("WeatherAmbience")
	grid = scene.get_node("ChunkGridManager")
	bridge = scene.get_node("SunshineCloudBridge")
	explorer = scene.get_node("Explorer")
	local = scene.get_node("LocalWeatherEffects")
	weather_runner = WEATHER_RUNNER.new()
	surface_runner = SURFACE_RUNNER.new()
	fixture = FIXTURE.new()
	var weather := scene.get_node("RingVolumetricClouds")
	weather.set_process(false)
	weather.set("Coverage", -10.0); weather.set("WeatherClusteringStrength", 0.0)
	bridge.set_process(false)
	local.set("EnableLightning", false)
	explorer.set_physics_process(false)
	capture = AudioEffectCapture.new()
	capture.buffer_length = 1.0
	window_frames = roundi(AudioServer.get_mix_rate() * .05)
	AudioServer.add_bus_effect(AudioServer.get_bus_index(&"Rain"), capture)
	weather_runner.ConfigureLocalWeather(local, 1.0, Vector2.ZERO, false)

	# 1. Production terrain provider on the flat water route: walk, run, reverse and stop.
	var water: Dictionary = surface_runner.FindSurface(grid, true)
	if not require(bool(water.found), "no generated water control"): return
	for offset in range(0, 101, 10):
		var point: Dictionary = ambience.call("GetRainSurfaceDiagnostic", int(water.around) + offset, int(water.across))
		if not require(bool(point.found) and point.material == "Water" and absf(float(point.top) - float(water.top)) < .01, "flat 100m water route unavailable"): return
	var route := {"around": int(water.around), "across": int(water.across), "height": float(water.top) + 1.0}
	var travelled := 0.0
	if not await settle(route, 3.0): return
	begin_measure()
	var starts := int(diag().voice_start_count)
	travelled = await motion(route, travelled, 4.0, 5.0, "walk")
	if travelled < 0: return
	travelled = await motion(route, travelled, 10.0, 4.5, "run")
	if travelled < 0: return
	travelled = await motion(route, travelled, -4.0, 4.0, "reverse")
	if travelled < 0: return
	travelled = await motion(route, travelled, 0.0, 1.0, "stop")
	if travelled < 0: return
	if not require(int(diag().voice_start_count) == starts, "a voice restarted while moving over uniform water: %d -> %d" % [starts, int(diag().voice_start_count)]): return
	print("RAIN_FIELD_WATER_ROUTE ", {"windows": windows, "max_gain_rate": max_rate, "max_octaves_per_s": max_octaves})

	# 2. Soil -> water and a roof, on the fixture provider (flat virtual ground at the camera, water from +40 m, a roof from +12 to +20 m).
	var found: Array = surface_runner.FindDrySurfaces(grid, 1)
	if not require(found.size() == 1, "no dry site"): return
	var site: Dictionary = found[0]
	var walk := {"around": int(site.around), "across": int(site.across), "height": float(site.top) + 1.0}
	track_ground = true
	ambience.call("SetExposureProviderForFixture", fixture)
	build_walk_world(walk)
	travelled = 0.0
	if not await settle(walk, 3.0): return
	begin_measure()
	starts = int(diag().voice_start_count)
	var cutoff_low := 1e9
	travelled = await motion(walk, travelled, 4.0, 5.0, "walk under a roof")
	if travelled < 0: return
	cutoff_low = minf(cutoff_low, float(diag().cutoff_hz))
	# Cover brings in the two roof voices and, where the roof top is a landing surface, up to four Hard-material field voices; nothing else may start.
	if not require(int(diag().voice_start_count) - starts <= 6, "more than the roof and hard-surface voices started walking through a roof: %d -> %d" % [starts, int(diag().voice_start_count)]): return
	starts = int(diag().voice_start_count)
	if not require(max_octaves > .1, "roof pass never moved the cutoff (roof not noticed)"): return
	travelled = await motion(walk, travelled, 4.0, 8.0, "soil to water")
	if travelled < 0: return
	var d := diag()
	if not require(float(d.sector_material[0][3]) > .5, "water did not take over the +around sector: " + str(d.sector_material)): return
	# Only the four water voices may have started when the surface changed.
	if not require(int(d.voice_start_count) - starts <= 4, "more than the water voices started at the shoreline: %d" % (int(d.voice_start_count) - starts)): return
	travelled = await motion(walk, travelled, 0.0, 1.0, "stop")
	if travelled < 0: return
	# 3. Storm arrival layers (gust roar, hail bed, leaf skitter, fat plips and hail one-shots): a gust front and hail ramp up and down over a standing listener. Only the
	# 12 storm loop voices may start, no gain step may exceed the slew cap, and the PCM stays continuous and finite.
	starts = int(diag().voice_start_count)
	var storm_peak := await storm_ramp(walk, travelled, 12.0, "storm ramp")
	if storm_peak.x < 0: return
	if not require(int(diag().voice_start_count) - starts <= 12, "more than the 12 storm voices started in a storm ramp: %d" % (int(diag().voice_start_count) - starts)): return
	if not require(storm_peak.x > .01 and storm_peak.y > .01, "storm layers never became audible in the ramp: gust %f hail %f" % [storm_peak.x, storm_peak.y]): return
	ambience.call("ClearArrivalOverrideForFixture")
	travelled = await motion(walk, travelled, 0.0, 2.0, "storm ramp end")
	if travelled < 0: return
	if not require(windows > 150 and steps_checked > 300, "too few continuity checks: windows=%d steps=%d" % [windows, steps_checked]): return
	finished = true
	print("RAIN_SOUND_FIELD_CONTINUITY=PASS windows=", windows, " steps=", steps_checked, " shots_started=", int(diag().shot_started_total), " min_peak=", min_peak, " max_gain_rate=", max_rate, " max_octaves_per_s=", max_octaves)
	scene.queue_free()
	await frames(3)
	quit(0)

func build_walk_world(walk: Dictionary) -> void:
	fixture.Clear()
	fixture.GroundTop = float(walk.height) - 1.0
	# 5 m headroom roof from +12 m to +20 m along the around axis (8 m long, 5 m wide), water from +40 m.
	var ground: float = float(fixture.GroundTop)
	var a: float = float(walk.around) + .5
	var c: float = float(walk.across) + .5
	fixture.AddBox(a + 12, ground + 5.0, c - 3, a + 20, ground + 6.0, c + 3, true)
	fixture.AddWater(a + 40, a + 4000, c - 4000, c + 4000)

# Ramps the arrival weights 0 -> 1 -> 0 (sin squared) over `seconds` through the fixture override; returns the peak gust and hail layer gains (x < 0 on failure).
func storm_ramp(route: Dictionary, offset: float, seconds: float, label: String) -> Vector2:
	var start := Time.get_ticks_usec()
	var peak := Vector2.ZERO
	while float(Time.get_ticks_usec() - start) / 1000000.0 < seconds:
		if Time.get_ticks_usec() > deadline: require(false, "real-time deadline"); return Vector2(-1, -1)
		var w := pow(sin(PI * float(Time.get_ticks_usec() - start) / 1000000.0 / seconds), 2.0)
		ambience.call("SetArrivalOverrideForFixture", w, 0.5 * w, w, 0.0)
		place(route, offset)
		await process_frame
		if finished: return Vector2(-1, -1)
		update_ground()
		if measuring and not check_frame(label): return Vector2(-1, -1)
		var d := diag()
		for i in 4:
			peak.x = maxf(peak.x, float(d.voice_gains[int(d.voice_layout.gust[0]) + i]))
			peak.y = maxf(peak.y, float(d.voice_gains[int(d.voice_layout.hail[0]) + i]))
	return peak

func settle(route: Dictionary, seconds: float) -> bool:
	place(route, 0.0)
	var end := Time.get_ticks_usec() + int(seconds * 1000000.0)
	while Time.get_ticks_usec() < end:
		await process_frame
		if finished: return false
		update_ground()
	return true

func begin_measure() -> void:
	capture.clear_buffer()
	discarded_baseline = capture.get_discarded_frames()
	measuring = true
	last_gains = []
	last_clock = -1.0
	last_cutoff = 0.0
	diag_reset()

func diag_reset() -> void:
	ambience.call("ResetGainStepPeak")

func motion(route: Dictionary, start_offset: float, speed: float, seconds: float, label: String) -> float:
	var offset := start_offset
	var start := Time.get_ticks_usec()
	var last := start
	while float(Time.get_ticks_usec() - start) / 1000000.0 < seconds:
		if Time.get_ticks_usec() > deadline: require(false, "real-time deadline"); return -1.0
		var now := Time.get_ticks_usec()
		var dt := float(now - last) / 1000000.0
		last = now
		offset += speed * dt
		place(route, offset)
		await process_frame
		if finished: return -1.0
		update_ground()
		if measuring and not check_frame(label): return -1.0
	return offset

# The fixture's ground follows the camera so terrain relief under the route never reads as a ceiling.
func update_ground() -> void:
	if not track_ground: return
	fixture.GroundTop = float(diag().listener_vertical) - EYE

func check_frame(label: String) -> bool:
	if not require(capture.get_discarded_frames() == discarded_baseline, "PCM capture overflow"): return false
	while capture.get_frames_available() >= window_frames:
		var peak := 0.0
		for sample in capture.get_buffer(window_frames):
			if not require(is_finite(sample.x) and is_finite(sample.y), label + ": nonfinite PCM"): return false
			peak = maxf(peak, maxf(absf(sample.x), absf(sample.y)))
		windows += 1
		min_peak = minf(min_peak, peak)
		if not require(peak > .0000001, label + ": silent PCM window %d" % windows): return false
	var d := diag()
	var clock := float(d.clock)
	var gains: Array = d.voice_gains
	var cutoff := float(d.cutoff_hz)
	# Shots are not loop voices: they never enter voice_gains or voice_start_count, but they are capped.
	if not require(int(d.shot_active_count) <= 6, label + ": %d shots active" % int(d.shot_active_count)): return false
	if not require(int(d.voice_layout.distant[1]) == gains.size() and int(d.voice_layout.bed[0]) == 0, label + ": voice_layout does not cover voice_gains"): return false
	if last_clock >= 0.0 and clock - last_clock > 1e-4 and last_gains.size() == gains.size():
		var dt := clock - last_clock
		for index in gains.size():
			var rate := absf(float(gains[index]) - float(last_gains[index])) / dt
			max_rate = maxf(max_rate, rate)
			if not require(absf(float(gains[index]) - float(last_gains[index])) <= .8 * dt + 1e-4, "%s: voice %d gain step %f in %f s" % [label, index, absf(float(gains[index]) - float(last_gains[index])), dt]): return false
		var octaves := absf(log(cutoff / last_cutoff) / log(2.0))
		max_octaves = maxf(max_octaves, octaves / dt)
		if not require(octaves <= 2.5 * dt + 1e-3, "%s: cutoff moved %f octaves in %f s" % [label, octaves, dt]): return false
		steps_checked += 1
	last_clock = clock
	last_gains = gains
	last_cutoff = cutoff
	return true

func place(route: Dictionary, offset: float) -> void:
	bridge.call("SetBenchmarkViewerPosition", float(route.around) + .5 + offset, float(route.height), float(route.across) + .5)
	explorer.call("ClearBenchmarkOverride")
	explorer.call("_PhysicsProcess", 0.0)

func diag() -> Dictionary:
	return ambience.call("GetWeatherAmbienceDiagnostics")

func frames(count: int) -> void:
	for _i in count: await process_frame

func require(ok: bool, message: String) -> bool:
	if ok: return true
	finished = true
	push_error("RAIN_SOUND_FIELD_CONTINUITY=FAIL: " + message + (" " + str(diag()) if is_instance_valid(ambience) else ""))
	quit(1)
	return false
