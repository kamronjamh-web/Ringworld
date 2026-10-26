extends SceneTree
# Run: godot --headless --path . --script res://tools/upper_atmosphere_lightning_test.gd   (prints UPPER_ATMOSPHERE_LIGHTNING=PASS)
# Pure sprite/policy checks (C#), then the production scheduler: sprite replay under rewind and the frequency setting.

const PURE := preload("res://tools/UpperAtmosphereLightningTest.cs")
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var scene: Node
var weather: Node
var pure: Node
var frame := 0

func _initialize() -> void:
	pure = PURE.new()
	root.add_child(pure)
	scene = TEST_SCENE.instantiate()
	root.add_child(scene)

func _count_events(cells: Array, times: Array) -> int:
	var n := 0
	for c in cells:
		for t in times:
			if not (weather.call("query_lightning_cell_at_time", c.x, c.y, float(t)) as Dictionary).is_empty(): n += 1
	return n

func _process(_delta: float) -> bool:
	frame += 1
	if frame != 8: return false
	if pure.get("Result") != "PASS": return fail("pure logic " + str(pure.get("Result")))
	weather = scene.get_node_or_null("RingVolumetricClouds")
	if weather == null: return fail("weather source unavailable")
	weather.set("EnableSharedLightning", true)
	pure.call("ResetPolicy")
	var found: Dictionary = {}
	var cell := Vector2i.ZERO
	var when := 0.0
	for a in range(32):
		for z in range(-3, 4):
			for t in range(1, 240):
				var e: Dictionary = weather.call("query_lightning_cell_at_time", a, z, float(t))
				if not e.is_empty():
					found = e; cell = Vector2i(a, z); when = float(t); break
			if not found.is_empty(): break
		if not found.is_empty(): break
	if found.is_empty(): return fail("no natural event found")
	if int(found["system_kind"]) < 3: return fail("event did not carry its system kind")
	# Rewind: same event and same upper-atmosphere derivation after the clock visits another time and returns.
	var kind_a: int = int(found["upper_atmosphere_kind"])
	weather.call("query_lightning_cell_at_time", cell.x, cell.y, when + 500.0)
	var replay: Dictionary = weather.call("query_lightning_cell_at_time", cell.x, cell.y, when)
	if int(replay["event_id"]) != int(found["event_id"]) or int(replay["upper_atmosphere_kind"]) != kind_a: return fail("sprite replay changed under rewind")
	# Scheduler path: set clock, process, step ahead and back, compare active event diagnostics.
	weather.set_process(false)
	var bridge := scene.get_node_or_null("SunshineCloudBridge")
	if bridge != null: bridge.call("SetBenchmarkViewerPosition", float(found["ground_around_meters"]), 120.0, float(found["ground_across_meters"]))
	weather.set("CloudSimulationTimeSeconds", when)
	weather.call("_process", 0.0)
	var first: Array = weather.call("get_active_lightning_event_diagnostics")
	weather.set("CloudSimulationTimeSeconds", when + 300.0)
	weather.call("_process", 0.0)
	weather.set("CloudSimulationTimeSeconds", when)
	weather.call("_process", 0.0)
	var second: Array = weather.call("get_active_lightning_event_diagnostics")
	if first.is_empty() or second.is_empty() or int(first[0]["event_id"]) != int(second[0]["event_id"]) or int(first[0]["upper_atmosphere_kind"]) != int(second[0]["upper_atmosphere_kind"]):
		return fail("scheduler rewind changed the sprite event")
	# Lightning frequency applies to the natural scheduler: 0 none, 0.4 fewer than 1, 2 more than 1 (shorter cadence), reduced flashes fewer than 1.
	var cells: Array = []
	for a in range(32):
		for z in range(-3, 4): cells.append(Vector2i(a, z))
	var times: Array = []
	for t in range(2, 720): times.append(float(t) * 0.5)
	pure.call("SetPolicy", 1.0, 1.0, false)
	var n1 := _count_events(cells, times)
	pure.call("SetPolicy", 0.0, 1.0, false)
	var n0 := _count_events(cells, times)
	pure.call("SetPolicy", 0.4, 1.0, false)
	var nlow := _count_events(cells, times)
	pure.call("SetPolicy", 2.0, 1.0, false)
	var n2 := _count_events(cells, times)
	pure.call("SetPolicy", 1.0, 1.0, true)
	var nr := _count_events(cells, times)
	pure.call("ResetPolicy")
	print("lightning frequency events: freq0=%d freq0.4=%d freq1=%d freq2=%d reduced=%d" % [n0, nlow, n1, n2, nr])
	if n1 < 10: return fail("too few baseline events to judge (%d)" % n1)
	if n0 != 0: return fail("frequency 0 still scheduled strikes")
	if nlow >= n1: return fail("frequency 0.4 did not thin strikes")
	if n2 <= n1: return fail("frequency 2 did not increase strikes")
	if nr >= n1: return fail("reduced flashes did not thin strikes")
	print("UPPER_ATMOSPHERE_LIGHTNING=PASS")
	quit(0)
	return false

func fail(message: String) -> bool:
	push_error("UPPER_ATMOSPHERE_LIGHTNING=FAIL " + message)
	quit(1)
	return true
