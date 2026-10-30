extends SceneTree
# Storm profile map contract (docs/storm-revamp-s4-sculpt-v2-design.md, WO-V1).
# Run (windowed Vulkan, not --headless; no other Godot process running):
#   Godot --path . --resolution 1280x720 --script res://tools/storm_profile_map_gpu_test.gd
# Prints STORM_PROFILE_MAP=PASS. Checks, on the front layers read back with texture_get_data:
#   - two bakes of one snapshot are byte-identical;
#   - heights are finite and inside the storm band, body tops <= 18000 m and canopy tops <= 16500 m (S6 ceiling);
#   - neighbouring texels with coverage > 0.05 differ by at most 1.5 km in base and top (continuity);
#   - the map is invalid when camera-independent clouds are off (and valid again when they are back on).

var world: Node
var helper: RefCounted
var probe: RefCounted
var finished := false

func _initialize():
	create_timer(600.0).timeout.connect(func(): _fail("600 second watchdog expired"))
	call_deferred("_run")

func _arg(name: String, default_value: String) -> String:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--" + name + "="):
			return a.substr(name.length() + 3)
	return default_value

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
	helper = load("res://tools/StormAnvilCapture.cs").new()
	probe = load("res://tools/StormProfileMapProbe.cs").new()
	world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
	var launcher = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", world, 5000000.0)
	launcher.free()
	if not far.Prepare(world, "res://.godot/far-ring-gameplay/storm_anvils_prep"):
		_fail(far.ErrorText)
		return
	root.add_child(world)
	await process_frame
	if not helper.Setup(world, 0.6, 1500.0):
		_fail(helper.ErrorText)
		return
	await create_timer(20.0).timeout
	helper.SetLifecycle(1.6)
	helper.PlaceStorm(world, 0.0, 0.0, 0.0, 40000.0)
	helper.PlaceCamera(world, 1500.0, 0.0, 0.0)
	var bridge = world.get_node("SunshineCloudBridge")
	if not await _wait_builds(2):
		_fail("storm profile map never became valid: " + str(probe.State(world)))
		return
	await _frames(30)
	var band: Vector2 = bridge.GetFirstWeatherSystemBand()
	print("STORM_PROFILE_MAP band=", band, " state=", probe.State(world))
	if band.y <= band.x:
		_fail("no uploaded weather system band")
		return
	if not await _readback():
		_fail("readback timed out")
		return
	if _arg("preview", "") != "":
		probe.SavePreviews(ProjectSettings.globalize_path(_arg("preview", "")))
	var first_hash: String = probe.HeightsHash()
	var report: String = probe.Analyze(band.x, band.y)
	print("STORM_PROFILE_MAP analyze ", report)
	if not report.begins_with("PASS"):
		_fail(report)
		return
	# Determinism: another bake of the same snapshot (clocks paused, camera fixed) is byte-identical.
	var target := _builds() + 2
	if not await _wait_builds(target):
		_fail("second bake never completed")
		return
	await _frames(2)
	if not await _readback():
		_fail("second readback timed out")
		return
	var second_hash: String = probe.HeightsHash()
	print("STORM_PROFILE_MAP hashes ", first_hash, " ", second_hash)
	if first_hash != second_hash:
		_fail("two bakes of one snapshot differ: " + first_hash + " vs " + second_hash)
		return
	# Invalid when camera-independent clouds are off, valid again afterwards.
	bridge.set("CameraIndependentClouds", false)
	await _frames(90)
	if bool(probe.State(world)["valid"]):
		_fail("map is still valid with camera-independent clouds off")
		return
	bridge.set("CameraIndependentClouds", true)
	if not await _wait_builds(_builds() + 1):
		_fail("map did not return after camera-independent clouds came back")
		return
	await _frames(30)
	if not bool(probe.State(world)["valid"]):
		_fail("map not valid after camera-independent clouds came back")
		return
	print("STORM_PROFILE_MAP=PASS")
	finished = true
	quit(0)

func _fail(reason):
	if finished:
		return
	finished = true
	printerr("STORM_PROFILE_MAP=FAIL ", reason)
	quit(1)
