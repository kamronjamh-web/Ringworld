extends SceneTree

# Disposable production-scene state regression; GPU output is tested separately.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var world: Node
var weather: Node
var rain: Node
var bridge: Node
var grid: Node
var observer := Vector3.ZERO
var failed := false
var max_queries := 0

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	world = WORLD.instantiate()
	if "--rain-curtains-certified" in OS.get_cmdline_user_args():
		var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", world, 5000000.0)
		launcher.free()
	root.add_child(world)
	for i in range(10): await process_frame
	rain = world.get_node_or_null("DistantRainCurtains")
	if not require(rain != null, "default scene did not create distant rain renderer"): return
	weather = world.get_node("RingVolumetricClouds")
	bridge = world.get_node("SunshineCloudBridge")
	grid = world.get_node("ChunkGridManager")
	world.get_node("Explorer").set_physics_process(false)
	world.get_node("RingDayNightCycle").set("Paused", true)
	rain.set_process(false)
	weather.set_process(false)
	bridge.set_process(false)
	weather.set("CloudSimulationTimeSeconds", 300.0)
	weather.set("WeatherWindMetersPerSecond", Vector2.ZERO)
	weather.set("Coverage", -10.0)
	weather.set("WeatherClusteringStrength", 0.0)
	# Warm fixture control; actual per-cell cold rejection is checked below.
	rain.set("SnowTemperatureThreshold", -100.0)
	observer = Vector3(12000.0, 1000.0, float(grid.get("RingAcrossWidthMeters")) * .5)
	place(observer)
	step(140)
	if failed: return
	if not require(visible_count() == 0, "dry weather retained visible curtains"): return
	var target := select_target()
	if not require(not target.is_empty(), "no valid distant lowland test cell"): return
	weather.call("RegisterManualWeatherAt", "rain-curtain-fixture", float(target.around_meters), float(target.across_meters), 1200.0, -9491)
	step(140)
	if failed: return
	var local_sample: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", observer.x, observer.z, 300.0)
	if not require(float(local_sample.precipitation) < .18 and visible_count() > 0, "dry observer must see distant storm rain"): return
	if not field_agrees(): return
	var anchors := anchor_keys()
	place(observer + Vector3(10, 0, 0))
	step(60)
	if not require(anchor_keys() == anchors, "ten-metre walk moved canonical rain cells"): return
	if not field_agrees(): return
	# Cross an actual grid boundary while a shared keyed curtain remains wet.
	var circumference_for_step := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var spacing := circumference_for_step / roundf(circumference_for_step / 4000.0)
	var boundary := (floorf(observer.x / spacing) + 1.0) * spacing
	place(Vector3(boundary - 5, observer.y, observer.z))
	step(140)
	var before_boundary := cells()
	place(Vector3(boundary + 5, observer.y, observer.z))
	step(1)
	var retained := 0
	var after_boundary := cells()
	for index in range(before_boundary.size()):
		var a: Dictionary = before_boundary[index]
		var b: Dictionary = after_boundary[index]
		if bool(a.get("valid", false)) and float(a.intensity) > .1 and cell_key(a) == cell_key(b):
			if not require(absf(float(a.intensity) - float(b.intensity)) < .0001, "stencil crossing restarted a retained wet cell"): return
			retained += 1
	if not require(retained > 0, "stencil-crossing control had no retained wet cell"): return
	# Do not confuse the observer's high-altitude snow state with rain far below.
	place(Vector3(observer.x, 5000.0, observer.z))
	step(60)
	if not require(visible_count() > 0, "high observer incorrectly suppressed liquid rain below"): return
	place(observer)
	rain.set("SnowTemperatureThreshold", 100.0)
	step(140)
	if not require(visible_count() == 0, "cold ground retained liquid rain curtains"): return
	rain.set("SnowTemperatureThreshold", -100.0)
	step(140)
	if not require(visible_count() > 0, "warm ground did not recover rain"): return
	for owner in [rain, weather, world.get_node("LocalWeatherEffects"), bridge]:
		owner.set("Enabled", false)
		step(1)
		if not require(visible_count() == 0 and int(diag().get("samples_last_frame", -1)) == 0, "disabled publisher retained visible rain/work: " + str(owner.name)): return
		owner.set("Enabled", true)
		step(140)
		if not require(visible_count() > 0, "reenabled publisher failed to recover: " + str(owner.name)): return
	for epoch in [20.0, 500040.0]:
		weather.set("CloudSimulationTimeSeconds", epoch)
		step(1)
		if not require(visible_count() == 0, "time discontinuity kept old visible rain"): return
		step(140)
		if not require(visible_count() > 0, "time discontinuity never repopulated rain"): return
	weather.call("UnregisterManualWeather", "rain-curtain-fixture")
	step(160)
	if not require(visible_count() == 0, "removed storm did not clear rain"): return
	# Around seam and both physical width edges use the same bounded grid.
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var width := float(grid.get("RingAcrossWidthMeters"))
	for p in [Vector3(circumference - 10, 1000, width * .5), Vector3(10, 1000, width * .5), Vector3(12000, 1000, 10), Vector3(12000, 1000, width - 10)]:
		place(p)
		step(140)
		var seen := {}
		for cell: Dictionary in cells():
			if not bool(cell.get("valid", true)): continue
			var key := cell_key(cell)
			if not require(not seen.has(key), "duplicate canonical seam cell"): return
			seen[key] = true
			if not require(float(cell.around_meters) >= 0 and float(cell.around_meters) < circumference and float(cell.across_meters) >= 0 and float(cell.across_meters) <= width, "canonical cell outside ring"): return
	# Actual natural field, without the dry fixture override: compare every
	# sampled value rather than assuming a storm must happen at the spawn point.
	weather.set("Coverage", .46)
	weather.set("WeatherClusteringStrength", 1.0)
	weather.set("MacroSystemSpawnChance", .8)
	weather.set("CloudSimulationTimeSeconds", 300.0)
	place(Vector3(112000, 1000, width * .5))
	step(160)
	if not field_agrees(): return
	var natural_visible := false
	for epoch in [300.0, 900.0, 1500.0]:
		if natural_visible: break
		weather.set("CloudSimulationTimeSeconds", epoch)
		for a in range(0, 640000, 16000):
			if natural_visible: break
			for z in [4000.0, width * .5, width - 8000.0]:
				var candidate: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", float(a), z, epoch)
				if float(candidate.precipitation) < .3: continue
				place(Vector3(a, 350.0, z))
				step(140)
				if not field_agrees(): return
				if visible_count() > 0:
					natural_visible = true
					print("RAIN_CURTAINS_NATURAL epoch=", epoch, " around=", a, " across=", z, " visible=", visible_count())
					break
	if not require(natural_visible, "natural lifecycle field never produced visible rain in sampled storms"): return
	# Real Sunshine preview path (not only a manually registered test record).
	weather.set("Coverage", -10.0)
	weather.set("WeatherClusteringStrength", 0.0)
	place(observer)
	bridge.call("SpawnStormFrontAhead")
	step(160)
	if not require(visible_count() > 0, "Sunshine spawned front had no distant rain"): return
	var expected_base := float(bridge.get("FixedCloudAltitudeDatumMeters")) + float(bridge.get("StormFrontCloudBaseAltitudeMeters"))
	for c: Dictionary in cells():
		if bool(c.visible) and not require(absf(float(c.cloud_base_meters) - expected_base) < .001, "Sunshine cloud-base mismatch"): return
	bridge.call("ClearSpawnedWeather")
	step(160)
	if not require(visible_count() == 0, "cleared Sunshine preview retained rain"): return
	var old_mode := int(weather.process_mode)
	weather.process_mode = Node.PROCESS_MODE_DISABLED
	step(1)
	if not require(not bool(diag().provider_eligible), "process-disabled weather source remained eligible"): return
	weather.process_mode = old_mode
	var old_quality := int(weather.get("Quality"))
	weather.set("Quality", 0)
	step(1)
	if not require(not bool(diag().provider_eligible), "cloud quality Off left rain enabled"): return
	weather.set("Quality", old_quality)
	if failed: return
	print("DISTANT_RAIN_CURTAINS_STATE=PASS max_queries=", max_queries, " diagnostics=", diag())
	world.queue_free()
	for i in range(3): await process_frame
	quit(0)

func step(count: int) -> void:
	for i in range(count):
		rain.call("_Process", .1)
		var d := diag()
		var queries := int(d.get("samples_last_frame", -1))
		max_queries = maxi(max_queries, queries)
		if not require(queries >= 0 and queries <= 3 and int(d.get("terrain_queries_last_frame", -1)) >= 0 and int(d.get("terrain_queries_last_frame", -1)) <= 3 and int(d.mesh_count) <= 25 and int(d.visible_count) <= int(d.mesh_count), "bounded frame/geometry contract"): return

func select_target() -> Dictionary:
	for cell: Dictionary in cells():
		var distance := Vector2(float(cell.around_meters) - observer.x, float(cell.across_meters) - observer.z).length()
		if distance > 3500 and distance < 6000 and float(cell.surface_meters) < 1400: return cell
	return {}

func field_agrees() -> bool:
	var epoch := float(weather.get("CloudSimulationTimeSeconds"))
	for cell: Dictionary in cells():
		if not bool(cell.get("valid", true)): continue
		var actual: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", float(cell.around_meters), float(cell.across_meters), epoch)
		if not require(absf(float(cell.precipitation) - float(actual.precipitation)) < .0001, "curtain sampled a different weather field"): return false
		if bool(cell.visible):
			if not require(float(actual.precipitation) > float(rain.get("PrecipitationStart")) and float(cell.temperature) > float(rain.get("SnowTemperatureThreshold")) and float(cell.cloud_base_meters) > float(cell.surface_meters), "rain drawn outside warm precipitating cloud-to-ground span"): return false
	return true

func place(p: Vector3) -> void:
	bridge.call("SetBenchmarkViewerPosition", float(p.x), float(p.y), float(p.z))

func diag() -> Dictionary:
	return rain.call("GetDiagnostics")
func cells() -> Array:
	return rain.call("GetCurtainDiagnostics")
func visible_count() -> int:
	return int(diag().visible_count)
func cell_key(c: Dictionary) -> String:
	return "%.3f/%.3f" % [float(c.around_meters), float(c.across_meters)]
func anchor_keys() -> Array:
	var result := []
	for c: Dictionary in cells():
		if bool(c.get("valid", true)): result.append(cell_key(c))
	result.sort()
	return result
func require(condition: bool, message: String) -> bool:
	if not condition and not failed:
		failed = true
		push_error("DISTANT_RAIN_CURTAINS_STATE=FAIL " + message)
		quit(1)
	return condition
