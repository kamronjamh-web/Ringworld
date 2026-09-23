extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var scene: Node
var weather: Node
var bridge: Node
var frame := 0
var found: Dictionary = {}
var query_cell := Vector2i.ZERO
var query_time := 0.0

func _initialize() -> void:
	scene = TEST_SCENE.instantiate()
	root.add_child(scene)

func _process(_delta: float) -> bool:
	frame += 1
	if frame != 8: return false
	weather = scene.get_node_or_null("RingVolumetricClouds")
	bridge = scene.get_node_or_null("SunshineCloudBridge")
	if weather == null: return fail("weather source unavailable")
	weather.set("EnableSharedLightning", true)
	# Find a real production natural pulse, then demand immutable replay.
	for a in range(32):
		for z in range(-3, 4):
			for t in range(1, 240):
				var e: Dictionary = weather.call("query_lightning_cell_at_time", a, z, float(t))
				if not e.is_empty():
					found = e; query_cell = Vector2i(a, z); query_time = float(t); break
			if not found.is_empty(): break
		if not found.is_empty(): break
	if found.is_empty(): return fail("no production natural event found")
	if int(found["event_id"]) <= 0 or int(found["system_id"]) == 0 or float(found["energy"]) <= 0.0 \
		or float(found["pulse"]) <= 0.0 or float(found["cloud_vertical_meters"]) <= float(found["ground_vertical_meters"]):
		return fail("natural query returned a default or physically invalid event")
	if float(found["ground_across_meters"]) < 0.0 or float(found["ground_across_meters"]) > 20000.0 \
		or float(found["cloud_across_meters"]) < 0.0 or float(found["cloud_across_meters"]) > 20000.0:
		return fail("natural event endpoint escaped ring width")
	var replay: Dictionary = weather.call("query_lightning_cell_at_time", query_cell.x, query_cell.y, query_time)
	if int(replay["event_id"]) != int(found["event_id"]) or int(replay["shape_seed"]) != int(found["shape_seed"]): return fail("natural id/seed replay changed")
	for key in ["ground_around_meters", "ground_vertical_meters", "ground_across_meters", "cloud_around_meters", "cloud_vertical_meters", "cloud_across_meters", "plane_angle_radians", "energy", "flash_radius_meters"]:
		if absf(float(replay[key]) - float(found[key])) > 0.001: return fail("natural event field changed under replay: " + key)
	var quiet: Dictionary = weather.call("query_lightning_cell_at_time", query_cell.x, query_cell.y, query_time + 2.0)
	if not quiet.is_empty() and float(quiet["pulse"]) > 0.0: return fail("expired query retained pulse")
	# Exercise the production scheduler at the same frozen clock near the real
	# event. Local-priority scanning must schedule exactly that immutable event.
	if bridge == null: return fail("Sunshine bridge unavailable for scheduler positioning")
	bridge.call("SetBenchmarkViewerPosition", float(found["ground_around_meters"]), 120.0, float(found["ground_across_meters"]))
	weather.set_process(false)
	weather.set("CloudSimulationTimeSeconds", query_time)
	weather.call("_process", 0.0)
	var active: Array = weather.call("get_active_lightning_event_diagnostics")
	var scheduled := false
	for event in active:
		if int(event["event_id"]) == int(found["event_id"]): scheduled = true
	if not scheduled: return fail("production scheduler did not create the discovered natural event")
	weather.set("CloudSimulationTimeSeconds", query_time + 2.0)
	weather.call("_process", 0.0)
	if not (weather.call("get_active_lightning_event_diagnostics") as Array).is_empty(): return fail("scheduler retained expired event")
	weather.set("CloudSimulationTimeSeconds", query_time)
	weather.call("_process", 0.0)
	active = weather.call("get_active_lightning_event_diagnostics")
	if active.is_empty() or int(active[0]["event_id"]) != int(found["event_id"]): return fail("rewind did not regenerate the identical natural event")
	weather.set("EnableSharedLightning", false)
	weather.call("_process", 0.0)
	if not (weather.call("get_active_lightning_event_diagnostics") as Array).is_empty(): return fail("source disable did not clear active events")
	weather.set("EnableSharedLightning", true)
	# Registered stationary ellipses must affect only their canonical footprint,
	# including the wrapped seam, and fully restore the natural baseline on clear.
	var baseline: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", 100.0, 10000.0, 100.0)
	weather.call("register_manual_weather_at", "core_smoke_a", 100.0, 10000.0, 8000.0, -7301)
	var center: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", 100.0, 10000.0, 100.0)
	var seam: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", 502756.0, 10000.0, 100.0)
	var outside: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", 100000.0, 10000.0, 100.0)
	if int(center["system_id"]) != -7301 or float(center["precipitation"]) < 0.999 or float(center["cloud_coverage"]) < 0.999: return fail("manual center did not dominate weather")
	if int(seam["system_id"]) != -7301: return fail("manual ellipse did not wrap canonical seam")
	if int(outside["system_id"]) != int(baseline["system_id"]) or absf(float(outside["precipitation"]) - float(baseline["precipitation"])) > 0.001: return fail("manual weather leaked outside ellipse")
	weather.call("unregister_manual_weather", "core_smoke_a")
	var restored: Dictionary = weather.call("GetNaturalWeatherDiagnosticAtTime", 100.0, 10000.0, 100.0)
	if int(restored["system_id"]) != int(baseline["system_id"]) or absf(float(restored["cloud_coverage"]) - float(baseline["cloud_coverage"])) > 0.001: return fail("manual clear did not restore natural weather")
	weather.call("register_manual_weather_at", "core_smoke_a", 12000.0, 10000.0, 8000.0, -7301)
	weather.call("register_manual_weather_at", "core_smoke_b", 18000.0, 10000.0, 8000.0, -7302)
	var d: Dictionary = weather.call("get_shared_lightning_diagnostics")
	if int(d["manual_weather_count"]) != 2 or int(d["global_cells_scanned_last_update"]) > 64 or int(d["local_cells_scanned_last_update"]) > 81: return fail("manual registry or actual scan bound failed")
	weather.call("unregister_manual_weather", "core_smoke_a")
	weather.call("unregister_manual_weather", "core_smoke_b")
	if int((weather.call("get_shared_lightning_diagnostics") as Dictionary)["manual_weather_count"]) != 0: return fail("manual clear did not restore registry")
	print("Shared lightning core smoke passed: natural replay, expiry, registry, bounded scan")
	quit(0)
	return false

func fail(message: String) -> bool:
	push_error("SHARED_LIGHTNING_CORE_SMOKE_FAIL: " + message)
	quit(1)
	return true
