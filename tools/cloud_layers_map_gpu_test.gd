extends SceneTree
# S6 WO2: the macro weather map's cloud layer array layer vs the CPU twin RingCloudLayers.EvaluateMap.
# Run (windowed Vulkan, not --headless; no other Godot process running):
#   Godot --path . --resolution 1280x720 --script res://tools/cloud_layers_map_gpu_test.gd
# Prints CLOUD_LAYERS_MAP=PASS. Places a manual storm, waits for the macro map to build, reads back the front coverage and layer slices and
# compares low/mid/high coverage and weatherness at 64 texels (32 spread over the map, 32 around the storm) within 1e-3.

var world: Node
var helper: RefCounted
var probe: RefCounted
var finished := false

func _initialize():
	create_timer(600.0).timeout.connect(func(): _fail("600 second watchdog expired"))
	call_deferred("_run")

func _frames(n: int):
	for i in n:
		await process_frame

func _wait_builds(target: int, max_frames := 1200) -> bool:
	for i in max_frames:
		if int(probe.Builds(world)) >= target:
			return true
		await process_frame
	return false

func _readback() -> bool:
	probe.RequestReadback(world)
	for i in 120:
		await process_frame
		if probe.ReadbackReady:
			return true
	return false

func _run():
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice")
		return
	var far = load("res://tools/FarRingGameplayCapture.cs").new()
	if not far.ConfigureArguments():
		_fail(far.ErrorText)
		return
	helper = load("res://tools/StormAnvilCapture.cs").new()
	probe = load("res://tools/CloudLayersMapProbe.cs").new()
	var test_world = load("res://tools/testing/RingTestWorld.cs").new()
	world = test_world.BootCertified(self, far, "res://.godot/far-ring-gameplay/storm_anvils_prep", 5000000.0)
	if world == null:
		_fail(test_world.ErrorText)
		return
	await process_frame
	if not helper.Setup(world, 0.6, 1500.0):
		_fail(helper.ErrorText)
		return
	test_world.AwaitSettled(world, 40.0)
	if not await test_world.Settled:
		_fail("world never settled: " + test_world.LastReport)
		return
	print("WORLD_SETTLED ", test_world.LastReport)
	helper.SetLifecycle(1.6)
	helper.PlaceStorm(world, 0.0, 0.0, 0.0, 40000.0)
	helper.PlaceCamera(world, 1500.0, 0.0, 0.0)
	if not await _wait_builds(2):
		_fail("macro map never became valid: builds=" + str(probe.Builds(world)))
		return
	await _frames(30)
	if not await _readback():
		_fail("readback timed out")
		return
	var report: String = probe.Compare(world, 1e-3)
	print("CLOUD_LAYERS_MAP ", report)
	if not report.begins_with("PASS"):
		_fail(report)
		return
	print("CLOUD_LAYERS_MAP=PASS")
	finished = true
	quit(0)

func _fail(reason):
	if finished:
		return
	finished = true
	printerr("CLOUD_LAYERS_MAP=FAIL ", reason)
	quit(1)
