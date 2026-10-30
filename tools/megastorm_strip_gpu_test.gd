extends SceneTree
# Megastorm strip map contract (docs/megastorm-design.md 3.2, WO4).
# Run (windowed Vulkan, not --headless; no other Godot process running):
#   Godot --path . --resolution 1280x720 --script res://tools/megastorm_strip_gpu_test.gd
# Prints MEGASTORM_STRIP=PASS. Checks:
#   - untriggered: the strip is not valid, no build ran and the texture is still the 1x1 placeholder (nothing allocated or dispatched);
#   - triggered (camera 10 km behind the edge, clocks paused): after two builds the front layers read back with texture_get_data are finite,
#     at most 18 km, and 64 check points match MegastormProfile.Column (heights within 50 m, aux within 0.02).

var world: Node
var probe: RefCounted
var finished := false

func _initialize():
	create_timer(600.0).timeout.connect(func(): _fail("600 second watchdog expired"))
	call_deferred("_run")

func _frames(n: int):
	for i in n:
		await process_frame

func _builds() -> int:
	return int(probe.State(world)["builds"])

func _wait_builds(target: int, max_frames := 900) -> bool:
	for i in max_frames:
		if _builds() >= target:
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
	var helper = load("res://tools/StormAnvilCapture.cs").new()
	probe = load("res://tools/MegastormStripProbe.cs").new()
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
	# Untriggered: nothing allocated, nothing dispatched.
	await _frames(30)
	var before: Dictionary = probe.State(world)
	print("MEGASTORM_STRIP untriggered state=", before)
	if bool(before["valid"]) or int(before["builds"]) != 0 or bool(before["real"]) or int(before["slices"]) != 0:
		_fail("untriggered strip is not idle: " + str(before))
		return
	if not probe.Trigger(world):
		_fail("trigger failed: " + probe.ErrorText)
		return
	if not await _wait_builds(2):
		_fail("megastorm strip never completed two builds: " + str(probe.State(world)))
		return
	await _frames(10)
	if not await _readback():
		_fail("readback timed out")
		return
	var report: String = probe.Analyze(world)
	print("MEGASTORM_STRIP analyze ", report)
	if not report.begins_with("PASS"):
		_fail(report)
		return
	print("MEGASTORM_STRIP=PASS")
	finished = true
	quit(0)

func _fail(reason):
	if finished:
		return
	finished = true
	printerr("MEGASTORM_STRIP=FAIL ", reason)
	quit(1)
