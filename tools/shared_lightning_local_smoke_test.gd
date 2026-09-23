extends SceneTree

## Functional consumer smoke: a dry observer asks the shared source for one
## preview, then verifies the local renderer consumed that exact event once.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var scene: Node
var weather: Node
var local: Node
var bridge: Node
var frame := 0
var expected_id := 0
var expected_seed := 0
var expected_pulse := 0.0
var expected_ground := Vector2.ZERO
var first_flash_count := 0

func _initialize() -> void:
	scene = TEST_SCENE.instantiate()
	weather = scene.get_node("RingVolumetricClouds")
	local = scene.get_node("LocalWeatherEffects")
	bridge = scene.get_node("SunshineCloudBridge")
	root.add_child(scene)

func _process(_delta: float) -> bool:
	frame += 1
	if frame == 8:
		# Freeze scheduling after startup while keeping source previews authorized.
		weather.set_process(false)
		local.set("EnableLightning", true)
		# Place the observer just after the canonical seam and the source event
		# just before it: this must be considered a nearby event, despite dry air.
		bridge.call("SetBenchmarkViewerPosition", 10.0, 120.0, 10000.0)
		weather.call("trigger_shared_lightning_preview_at", 502646.0, 10000.0, -9001, 1.0, 2400.0)
		var events: Array = weather.call("get_active_lightning_event_diagnostics")
		if events.is_empty(): return fail("clear-weather preview did not create a shared event")
		var e: Dictionary = events[events.size() - 1]
		expected_id = int(e["event_id"])
		expected_seed = int(e["shape_seed"])
		expected_pulse = float(e["pulse"])
		expected_ground = Vector2(float(e["ground_around_meters"]), float(e["ground_across_meters"]))
	if frame == 12:
		var d: Dictionary = local.call("get_local_shared_lightning_diagnostics")
		if int(d["last_event_id"]) != expected_id or int(d["shape_seed"]) != expected_seed:
			return fail("local consumer did not retain the source event id and seed")
		var actual_ground := Vector2(float(d["ground_around_meters"]), float(d["ground_across_meters"]))
		var world_ground: Vector3 = d["world_ground"]
		var world_cloud: Vector3 = d["world_cloud"]
		if actual_ground.distance_to(expected_ground) > 0.01 or world_ground.distance_to(world_cloud) < 10.0:
			return fail("local consumer endpoints differ from the source event")
		if absf(float(d["pulse"]) - expected_pulse) > 0.001:
			return fail("local pulse differs from the source fixed-time pulse")
		if float(local.get("PrecipitationIntensity")) > 0.01:
			return fail("dry observer rendered precipitation while consuming a remote shared event")
		first_flash_count = int(d["flash_count"])
		# A basic floating-origin/grid-frame move must not regenerate the event.
		bridge.call("SetBenchmarkViewerPosition", 20.0, 120.0, 10000.0)
	if frame == 18:
		var d: Dictionary = local.call("get_local_shared_lightning_diagnostics")
		if int(d["flash_count"]) != first_flash_count or int(d["last_event_id"]) != expected_id:
			return fail("the same shared event was consumed more than once")
		local.set("EnableLightning", false)
	if frame == 20:
		var d: Dictionary = local.call("get_local_shared_lightning_diagnostics")
		if int(d["event_id"]) != 0:
			return fail("disabling local lightning did not immediately clear the active visual")
		weather.set("EnableSharedLightning", false)
		weather.set_process(true)
		print("Shared local lightning smoke passed: dry seam observer, exact shared id/seed, rebase, no duplicate, disable clear")
		quit(0)
	return false

func fail(message: String) -> bool:
	push_error("SHARED_LIGHTNING_LOCAL_SMOKE_FAIL: " + message)
	quit(1)
	return true
