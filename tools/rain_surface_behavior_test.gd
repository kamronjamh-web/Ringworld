extends SceneTree

# Real controller, generated surfaces and unsaved edits. Only weather input and
# listener movement are fixture-controlled. Rain capture excludes the old field.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const WEATHER_RUNNER := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")
const SURFACE_RUNNER := preload("res://scripts/Weather/RainSurfaceVerificationRunner.cs")
var scene: Node
var ambience: Node
var grid: Node
var smooth: Node
var explorer: Node
var bridge: Node
var local: Node
var runner: RefCounted
var weather_runner: RefCounted
var capture: AudioEffectCapture
var start_usec := 0
var done := false
var budget_base := 0
var budget_clock := 0.0

func _initialize() -> void:
	start_usec = Time.get_ticks_usec()
	call_deferred("run")

func _process(_delta: float) -> bool:
	if done: return false
	if float(Time.get_ticks_usec() - start_usec) > 65000000.0: check(false, "real-time deadline")
	if is_instance_valid(ambience) and budget_clock > 0:
		var d := diag()
		check(int(d.surface_queries_frame) <= 3 and int(d.surface_density_query_frame) <= 48, "per-frame column/density budget")
		check(int(d.surface_query_total) - budget_base <= 3 * (ceili((float(d.clock) - budget_clock) / .12) + 1), "cadence burst after edit/movement")
	return false

func run() -> void:
	AudioServer.set_bus_volume_db(0, -80.0)
	scene = SCENE.instantiate()
	if "--rain-surface-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", scene, 5000000.0)
		launcher.free()
	root.add_child(scene)
	await frames(5)
	ambience = scene.get_node("WeatherAmbience")
	grid = scene.get_node("ChunkGridManager")
	smooth = scene.get_node("SmoothTerrainGrid")
	explorer = scene.get_node("Explorer")
	bridge = scene.get_node("SunshineCloudBridge")
	local = scene.get_node("LocalWeatherEffects")
	runner = SURFACE_RUNNER.new()
	weather_runner = WEATHER_RUNNER.new()
	var weather := scene.get_node("RingVolumetricClouds")
	weather.set_process(false)
	weather.set("Coverage", -10.0)
	weather.set("WeatherClusteringStrength", 0.0)
	bridge.set_process(false)
	local.set("EnableLightning", false)
	explorer.set_physics_process(false)
	AudioServer.add_bus()
	var sink := AudioServer.bus_count - 1
	AudioServer.set_bus_name(sink, &"SurfaceTestSink")
	AudioServer.set_bus_volume_db(sink, -80)
	for group_name in ["RainLight", "RainHeavy"]:
		for voice in ambience.get_node(group_name).get_children(): voice.bus = &"SurfaceTestSink"
	capture = AudioEffectCapture.new()
	capture.buffer_length = 1.0
	AudioServer.add_bus_effect(AudioServer.get_bus_index(&"Rain"), capture)
	var land: Dictionary = runner.FindSurface(grid, false)
	var water: Dictionary = runner.FindSurface(grid, true)
	if not check(bool(land.found) and bool(water.found), "real land/water controls"): return
	place(land, float(land.top) + 1)
	rain(1.0)
	await wait_real(1.7)
	budget_base = int(diag().surface_query_total)
	budget_clock = float(diag().clock)
	if not check(int(diag().player_count) == 34 and int(diag().surface_detail_pool_count) == 24 and int(diag().asset_count) == 7 and int(diag().detail_playing_count) > 0, "pool/assets/playback"): return
	if not check(await peak() > .00001, "ground detail lacks PCM"): return
	var fixed_positions: Array = diag().surface_detail_positions
	var moved := land.duplicate()
	moved.around = int(land.around) + 2
	place(moved, float(land.top) + 1)
	await wait_real(.2)
	for before in fixed_positions:
		if not check(same_anchor(before, detail_at_index(int(before.index))), "small translation lost/repositioned an existing source"): return
	# Crossing the former 18m refresh boundary must retain an audible, world-anchored
	# loop and its playback phase. Continuous walking/lead placement has a separate
	# fixture; here surface PCM is isolated by routing field voices to the sink.
	var retained_index := -1
	for point in diag().surface_detail_positions:
		if bool(point.playing): retained_index = int(point.index); break
	if not check(retained_index >= 0, "no continuity control voice"): return
	var retained: AudioStreamPlayer3D = ambience.get_node("RainSurfaceDetail/Surface" + str(retained_index + 1))
	var retained_position := detail_at_index(retained_index)
	var retained_phase := retained.get_playback_position()
	var retained_stream := retained.stream
	var retained_time := Time.get_ticks_usec()
	moved = land.duplicate()
	moved.around = int(land.around) + 26
	place(moved, float(land.top) + 1)
	await wait_real(.45)
	var phase_delta := fposmod(retained.get_playback_position() - retained_phase, retained.stream.get_length())
	var phase_elapsed := float(Time.get_ticks_usec() - retained_time) / 1000000.0
	if not check(retained.playing and retained.stream == retained_stream and same_anchor(retained_position, detail_at_index(retained_index)) and absf(phase_delta - phase_elapsed) < .12 and await peak() > .00001, "boundary movement cut/repositioned audible surface loop"): return
	if not check(int(diag().surface_detail_valid_count) <= 24, "movement exceeded bounded surface pool"): return
	place(land, float(land.top) + 1)
	await wait_real(.25)
	if not check(retained.playing and same_anchor(retained_position, detail_at_index(retained_index)), "reverse movement stole audible surface voice"): return
	place(land, float(land.top) + 1)
	var roof_y := ceili(float(land.top)) + 6
	if not check(runner.Roof(grid, int(land.around), int(land.across), roof_y, false), "roof edit rejected"): return
	await wait_real(1.7)
	if not check(float(diag().surface_cover_current) > .8 and float(diag().rain_filter_cutoff_hz) < 2500, "roof did not soften/filter rain"): return
	var roof: Dictionary = surface(land)
	if not check(bool(roof.found) and roof.material == "Hard" and absf(float(roof.top) - roof_y - 1) < .01 and not bool(diag().deep_shelter), "roof top/material/deep-rock"): return
	if not check(await peak() > .000001, "shelter erased all nearby rain PCM"): return
	if not check(runner.WaterCell(grid, int(land.around), int(land.across), roof_y + 1, false), "dynamic water control"): return
	var dynamic_water: Dictionary = surface(land)
	if not check(dynamic_water.material == "Water" and absf(float(dynamic_water.top) - roof_y - 2) < .01, "immediate dynamic water top/classification"): return
	if not check(runner.WaterCell(grid, int(land.around), int(land.across), roof_y + 1, true), "dynamic water removal"): return
	if not check(runner.Roof(grid, int(land.around), int(land.across), roof_y, true), "roof removal"): return
	await wait_real(1.7)
	if not check(float(diag().surface_cover_current) == 0 and float(diag().rain_filter_cutoff_hz) > 17000, "removal retained muffling"): return
	var high_roof := ceili(float(land.top)) + 200
	if not check(runner.Roof(grid, int(land.around), int(land.across), high_roof, false), "high roof control"): return
	await wait_real(1.4)
	if not check(float(diag().surface_cover_current) > .8 and not bool(diag().deep_shelter), "roof above scan range ignored or treated as deep rock"): return
	if not check(runner.Roof(grid, int(land.around), int(land.across), high_roof, true), "high roof removal"): return
	await wait_real(1.4)
	# Added natural matter is valid even where the legacy voxel baseline is AIR.
	var hill := land.duplicate()
	hill.around = int(land.around) + 64
	var hill_before: Dictionary = surface(hill)
	var hill_y := ceili(float(hill_before.natural)) + 10
	runner.Stamp(smooth, int(hill.around), int(hill.across), hill_y, 4.0, true)
	var hill_after: Dictionary = surface(hill)
	if not check(bool(hill_after.found) and absf(float(hill_after.natural) - hill_y - 4) < .08, "added smooth surface ignored"): return
	# Open shaft: unchanged legacy ground must not be mistaken for a ceiling.
	var cut_y := floori(float(land.top)) - 7
	runner.Stamp(smooth, int(land.around), int(land.across), cut_y, 14.0, false)
	place(land, cut_y)
	await wait_real(1.7)
	if not check(runner.IsAir(smooth, int(land.around), int(land.across), cut_y) and float(diag().surface_cover_current) == 0 and not bool(diag().deep_shelter), "open excavation falsely sheltered"): return
	var cave := land.duplicate()
	cave.around = int(land.around) + 100
	var cave_surface: Dictionary = surface(cave)
	var cave_y := floori(float(cave_surface.natural)) - 38
	runner.Stamp(smooth, int(cave.around), int(cave.across), cave_y, 5.0, false)
	place(cave, cave_y)
	await wait_real(1.7)
	if not check(runner.IsAir(smooth, int(cave.around), int(cave.across), cave_y) and bool(diag().deep_shelter) and int(diag().detail_playing_count) == 0 and await peak() <= .000001, "deep cave retained local rain"): return
	place(water, float(water.top) + 1)
	await wait_real(1.7)
	var wet: Dictionary = surface(water)
	if not check(bool(wet.found) and wet.material == "Water" and absf(float(wet.top) - float(water.top)) < .01 and await peak() > .00001, "generated water placement/PCM"): return
	var water_roof := ceili(float(water.top)) + 6
	if not check(runner.Roof(grid, int(water.around), int(water.across), water_roof, false), "water roof rejected"): return
	if not check(surface(water).material == "Hard" and float(surface(water).top) > float(water.top), "water overrode bridge"): return
	if not check(runner.Roof(grid, int(water.around), int(water.across), water_roof, true), "water roof removal"): return
	await wait_real(.7)
	var voice: AudioStreamPlayer3D = ambience.get_node("RainSurfaceDetail/Surface1")
	if not check(voice.playing and voice.stream is AudioStreamOggVorbis and voice.stream.loop, "surface loop control"): return
	var pos := voice.get_playback_position()
	await wait_real(.3)
	var advanced := fposmod(voice.get_playback_position() - pos, voice.stream.get_length())
	if not check(advanced > .15 and advanced < .6, "detail restarted every frame"): return
	paused = true
	await frames(2)
	pos = voice.get_playback_position()
	var queries := int(diag().surface_query_total)
	await wait_real(.25)
	if not check(voice.stream_paused and absf(voice.get_playback_position() - pos) < .04 and int(diag().surface_query_total) == queries and int(diag().surface_queries_frame) == 0, "pause/query freeze"): return
	paused = false
	await wait_real(.2)
	if not check(not voice.stream_paused, "surface resume"): return
	rain(0.0)
	await wait_real(1.7)
	queries = int(diag().surface_query_total)
	await wait_real(.3)
	if not check(int(diag().surface_query_total) == queries and int(diag().detail_playing_count) == 0 and await peak() <= .000001, "dry queries/audio"): return
	rain(1.0, true)
	await wait_real(1.7)
	if not check(int(diag().detail_playing_count) == 0 and int(diag().surface_query_total) == queries, "snow queries/audio"): return
	rain(1.0)
	await wait_real(.7)
	ambience.set("Enabled", false)
	await frames(3)
	if not check(int(diag().playing_count) == 0 and int(diag().surface_detail_valid_count) == 0 and float(diag().surface_cover_current) == 0, "disable stale cache"): return
	ambience.set("Enabled", true)
	await wait_real(.7)
	place(water, float(water.top) - 4)
	await frames(3)
	if not check(bool(explorer.get("IsCameraUnderwater")) and int(diag().playing_count) == 0, "actual underwater silence"): return
	place(water, 30000)
	await frames(3)
	if not check(not bool(diag().can_hear) and int(diag().playing_count) == 0, "space silence"): return
	place(water, float(water.top) + 1)
	await wait_real(.7)
	if not check(int(diag().detail_playing_count) > 0, "missing-stream positive control"): return
	for property in ["RainSoilStream", "RainHardStream", "RainWaterStream"]: ambience.set(property, null)
	for group_name in ["RainLight", "RainHeavy"]:
		for field_voice in ambience.get_node(group_name).get_children(): field_voice.stream = null
	await wait_real(1.7)
	if not check(int(diag().playing_count) == 0 and is_equal_approx(float(diag().duck), 1.0) and await peak() <= .000001, "missing rain retained audio/duck"): return
	var effects_before := AudioServer.get_bus_effect_count(AudioServer.get_bus_index(&"Rain"))
	done = true
	scene.queue_free()
	await frames(4)
	if not check(AudioServer.get_bus_effect_count(AudioServer.get_bus_index(&"Rain")) == effects_before - 1, "filter exit cleanup"): return
	print("RAIN_SURFACE_BEHAVIOR=PASS: land/water/roof PCM, smooth add/excavation/cave, anchored positions, real pause, loop progress, dry/snow silence, water/space, budgets, filter cleanup")
	quit(0)

func surface(column: Dictionary) -> Dictionary:
	return ambience.call("GetRainSurfaceDiagnostic", int(column.around), int(column.across))

func diag() -> Dictionary:
	return ambience.call("GetWeatherAmbienceDiagnostics")

func detail_at_index(index: int) -> Dictionary:
	for point in diag().surface_detail_positions:
		if int(point.index) == index: return point
	return {}

func same_anchor(before: Dictionary, after: Dictionary) -> bool:
	return not after.is_empty() and before.around == after.around and before.across == after.across and before.vertical == after.vertical

func rain(amount: float, snow := false) -> void:
	weather_runner.ConfigureLocalWeather(local, amount, Vector2.ZERO, snow)

func place(column: Dictionary, height: float) -> void:
	bridge.call("SetBenchmarkViewerPosition", float(column.around) + .5, height, float(column.across) + .5)
	explorer.call("ClearBenchmarkOverride")
	explorer.call("_PhysicsProcess", 0.0)
	explorer.set_physics_process(false)

func peak() -> float:
	await wait_real(.08)
	capture.clear_buffer()
	await wait_real(.22)
	var result := 0.0
	while capture.get_frames_available() > 0:
		for sample in capture.get_buffer(mini(capture.get_frames_available(), 2048)):
			if not check(is_finite(sample.x) and is_finite(sample.y), "nonfinite PCM"): return 0
			result = maxf(result, maxf(absf(sample.x), absf(sample.y)))
	return result

func frames(count: int) -> void:
	for _i in count: await process_frame

func wait_real(seconds: float) -> void:
	await create_timer(seconds, true, false, true).timeout

func check(condition: bool, message: String) -> bool:
	if condition: return true
	done = true
	paused = false
	push_error("RAIN_SURFACE_BEHAVIOR=FAIL: " + message + (" " + str(diag()) if is_instance_valid(ambience) else ""))
	quit(1)
	return false
