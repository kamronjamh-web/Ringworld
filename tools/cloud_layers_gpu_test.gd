extends SceneTree
# S6 WO3b: vertical density columns through the real Sunshine compute (SamplePoints probes) at fixed poses.
# Run (windowed Vulkan, not --headless; no other Godot process running):
#   Godot --path . --resolution 1280x720 --script res://tools/cloud_layers_gpu_test.gd
# Prints CLOUD_LAYERS=PASS. Checks, per scenario (probes every 250 m from 0 to 20 km):
#   fair   (macro-map weatherness 0, picked from the map readback): density 0 below FairWeatherCloudBase - 50 (1950 m) and above 7 km;
#   weather (a manual Nimbostratus system over the probe column): density reaches down to the weather base (<= 1500 m) and is 0 below 450 m;
#   ordering: the weather column starts below every fair column;
#   storm  (manual anvil storm, mature): density 0 above 18000 m.
# S6 WO5 (cirrus shell at SHELL = 9500 m, the High layer's base; a probe exactly at that altitude reads the shell's vertical opacity):
#   fair:   the above-7050 check ignores the probe at SHELL (still 0 everywhere else); each pick prints the shell value and the map's
#           high coverage, and where the map high coverage <= 0.0005 the shell value must be 0;
#   cirrus  (manual Cirrostratus over the probe column, after weather): core high coverage > 0.6; the shell value at the base > 0.05 and
#           the probes at 9250 / 9750 m (no volumetric cirrus) are 0; with CameraIndependentDebugMask 128 (cirrus shell off) the shell value is 0.

const STEP := 250.0
const TOP := 20000.0
const THRESHOLD := 0.01
const SHELL := 9500.0

var world: Node
var helper: RefCounted
var probe: RefCounted
var finished := false

func _initialize():
	create_timer(900.0).timeout.connect(func(): _fail("900 second watchdog expired"))
	call_deferred("_run")

func _frames(n: int):
	for i in n:
		await process_frame

func _wait_builds(target: int, max_frames := 1500) -> bool:
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

func _altitudes() -> Array:
	var altitudes := []
	var a := 0.0
	while a <= TOP:
		altitudes.append(a)
		a += STEP
	return altitudes

# Density column at a canonical position; returns the result per altitude.
func _column(around: float, across: float) -> PackedFloat32Array:
	var alts := _altitudes()
	var out := PackedFloat32Array()
	if not probe.RequestColumn(world, around, across, alts):
		return out
	for i in 600:
		await process_frame
		if probe.ColumnReady:
			break
	if not probe.ColumnReady:
		return out
	for i in alts.size():
		out.append(probe.Result(i))
	return out

# Lowest / highest altitude whose volumetric density exceeds THRESHOLD (-1 when none); the cirrus shell probe at SHELL is not volume.
func _extent(column: PackedFloat32Array) -> Vector2:
	var low := -1.0
	var high := -1.0
	for i in column.size():
		if column[i] > THRESHOLD and not is_equal_approx(float(i) * STEP, SHELL):
			if low < 0.0:
				low = float(i) * STEP
			high = float(i) * STEP
	return Vector2(low, high)

func _max_in(column: PackedFloat32Array, from_m: float, to_m: float) -> float:
	var m := 0.0
	for i in column.size():
		var a := float(i) * STEP
		if a >= from_m and a <= to_m:
			m = maxf(m, column[i])
	return m

# Max over [from_m, to_m] ignoring the probe at `skip_m` (the cirrus shell reads its vertical opacity there).
func _max_in_skipping(column: PackedFloat32Array, from_m: float, to_m: float, skip_m: float) -> float:
	var m := 0.0
	for i in column.size():
		var a := float(i) * STEP
		if a >= from_m and a <= to_m and not is_equal_approx(a, skip_m):
			m = maxf(m, column[i])
	return m

# The probe value exactly at the shell altitude (0 when the column is too short).
func _shell_value(column: PackedFloat32Array) -> float:
	var index := int(round(SHELL / STEP))
	return column[index] if index < column.size() else 0.0

func _run():
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice")
		return
	var far = load("res://tools/FarRingGameplayCapture.cs").new()
	if not far.ConfigureArguments():
		_fail(far.ErrorText)
		return
	helper = load("res://tools/StormAnvilCapture.cs").new()
	probe = load("res://tools/CloudLayersProbe.cs").new()
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
	helper.RainOff(world)
	helper.PlaceCamera(world, 1500.0, 0.0, 0.0)
	if not await _wait_builds(2):
		_fail("macro map never became valid: builds=" + str(probe.Builds(world)))
		return
	await _frames(60)

	# ---- fair: columns where the map says weatherness 0 ----
	if not await _readback():
		_fail("readback timed out")
		return
	var base_around: float = probe.BaseAroundMeters
	var base_across: float = probe.BaseAcrossMeters
	var candidates := []
	for da in range(-90000, 90001, 15000):
		for dc in range(-60000, 60001, 15000):
			var layers: Vector4 = probe.LayersAt(base_around + da, base_across + dc)
			if layers.w < 0.0 or layers.w > 0.0005:
				continue
			candidates.append({"da": da, "dc": dc, "low": layers.x, "mid": layers.y, "high": layers.z})
	if candidates.size() < 3:
		_fail("fewer than 3 weatherness-0 columns on the map (" + str(candidates.size()) + ")")
		return
	var picks := []
	candidates.sort_custom(func(a, b): return a["low"] > b["low"])
	for i in mini(4, candidates.size()):
		picks.append(candidates[i])
	candidates.sort_custom(func(a, b): return a["mid"] > b["mid"])
	for c in candidates:
		if picks.size() >= 6:
			break
		if not picks.has(c):
			picks.append(c)
	var fair_low := 1e9
	var fair_high := -1.0
	var fair_nonzero := 0
	for pick in picks:
		var column := await _column(base_around + pick["da"], base_across + pick["dc"])
		if column.size() == 0:
			_fail("fair column timed out")
			return
		var extent := _extent(column)
		var below := _max_in(column, 0.0, 1950.0)
		var above := _max_in_skipping(column, 7050.0, TOP, SHELL)
		var shell := _shell_value(column)
		print("CLOUD_LAYERS fair da=", pick["da"], " dc=", pick["dc"], " low_cov=", snappedf(pick["low"], 0.01), " mid_cov=", snappedf(pick["mid"], 0.01),
			" high_cov=", snappedf(pick["high"], 0.0001), " extent=", extent, " max_below_1950=", below, " max_above_7050=", above, " shell=", shell)
		if below > 0.0 or above > 0.0:
			_fail("fair column has density outside 1950..7050 m: below=" + str(below) + " above=" + str(above))
			return
		if pick["high"] <= 0.0005 and shell != 0.0:
			_fail("fair column with map high coverage " + str(pick["high"]) + " has a nonzero cirrus shell value: " + str(shell))
			return
		if extent.x >= 0.0:
			fair_nonzero += 1
			fair_low = minf(fair_low, extent.x)
			fair_high = maxf(fair_high, extent.y)
	if fair_nonzero == 0:
		_fail("vacuous fair check: no density in any of " + str(picks.size()) + " columns")
		return

	# ---- weather: a Nimbostratus core over the probe column ----
	if not probe.PlaceWeather(world, 10, 0.0, 0.0, 80000.0, 7000.0, 4, 0.94, 0.97):
		_fail(probe.ErrorText)
		return
	var builds := int(probe.Builds(world))
	if not await _wait_builds(builds + 2):
		_fail("macro map did not rebuild with the weather system")
		return
	await _frames(60)
	if not await _readback():
		_fail("readback timed out")
		return
	var core: Vector4 = probe.LayersAt(base_around, base_across)
	print("CLOUD_LAYERS weather map layers at core ", core)
	if core.w < 0.5:
		_fail("weather core weatherness too low: " + str(core.w))
		return
	var weather_low := 1e9
	var weather_high := -1.0
	for offset in [Vector2(0, 0), Vector2(8000, 0), Vector2(0, 8000), Vector2(-8000, -4000)]:
		var column := await _column(base_around + offset.x, base_across + offset.y)
		if column.size() == 0:
			_fail("weather column timed out")
			return
		var extent := _extent(column)
		var below := _max_in(column, 0.0, 450.0)
		print("CLOUD_LAYERS weather off=", offset, " extent=", extent, " max_below_450=", below, " col=", str(column.slice(0, 40)))
		if below > 0.0:
			_fail("weather column has density below the 500 m base")
			return
		if extent.x >= 0.0:
			weather_low = minf(weather_low, extent.x)
			weather_high = maxf(weather_high, extent.y)
	if weather_low > 1500.0:
		_fail("weather density does not reach the weather base: lowest=" + str(weather_low))
		return
	print("CLOUD_LAYERS ordering weather_lowest=", weather_low, " fair_lowest=", fair_low, " weather_highest=", weather_high, " fair_highest=", fair_high)
	if not (weather_low < fair_low):
		_fail("layer ordering: the weather column does not start below the fair columns")
		return
	probe.ClearWeather(world)

	# ---- cirrus: a manual Cirrostratus over the probe column; the shell (not a volume) carries it ----
	if not probe.PlaceWeather(world, 5, 0.0, 0.0, 60000.0, 13000.0, 2, 0.0, 0.3):
		_fail(probe.ErrorText)
		return
	builds = int(probe.Builds(world))
	if not await _wait_builds(builds + 2):
		_fail("macro map did not rebuild with the cirrostratus system")
		return
	await _frames(60)
	if not await _readback():
		_fail("readback timed out")
		return
	var cirrus_core: Vector4 = probe.LayersAt(base_around, base_across)
	print("CLOUD_LAYERS cirrus map layers at core ", cirrus_core)
	if cirrus_core.z <= 0.6:
		_fail("cirrostratus core high coverage too low: " + str(cirrus_core.z))
		return
	var cirrus_column := await _column(base_around, base_across)
	if cirrus_column.size() == 0:
		_fail("cirrus column timed out")
		return
	var cirrus_shell := _shell_value(cirrus_column)
	var cirrus_below_shell: float = cirrus_column[int(round((SHELL - STEP) / STEP))]
	var cirrus_above_shell: float = cirrus_column[int(round((SHELL + STEP) / STEP))]
	print("CLOUD_LAYERS cirrus shell=", cirrus_shell, " at_9250=", cirrus_below_shell, " at_9750=", cirrus_above_shell)
	if cirrus_shell <= 0.05:
		_fail("cirrostratus shell value too low at the base: " + str(cirrus_shell))
		return
	if cirrus_below_shell > 0.0 or cirrus_above_shell > 0.0:
		_fail("volumetric density next to the cirrus shell: at_9250=" + str(cirrus_below_shell) + " at_9750=" + str(cirrus_above_shell))
		return
	var bridge_node: Node = world.get_node("SunshineCloudBridge")
	bridge_node.set("CameraIndependentDebugMask", 128)
	# The mask reaches the shader with the next weather upload (every WeatherRefreshSeconds, 0.25 s): 10 frames raced it at high frame rates.
	await create_timer(float(bridge_node.get("WeatherRefreshSeconds")) * 2.0 + 0.1).timeout
	await _frames(2)
	var off_column := await _column(base_around, base_across)
	if off_column.size() == 0:
		bridge_node.set("CameraIndependentDebugMask", 0)
		_fail("cirrus-off column timed out")
		return
	var off_shell := _shell_value(off_column)
	print("CLOUD_LAYERS cirrus shell with mask 128 = ", off_shell)
	bridge_node.set("CameraIndependentDebugMask", 0)
	if off_shell != 0.0:
		_fail("cirrus shell value is nonzero with debug mask 128 (cirrus off): " + str(off_shell))
		return
	await create_timer(float(bridge_node.get("WeatherRefreshSeconds")) * 2.0 + 0.1).timeout
	probe.ClearWeather(world)

	# ---- storm: nothing above 18 km ----
	helper.SetLifecycle(1.6)
	helper.PlaceStorm(world, 0.0, 0.0, 0.0, 40000.0)
	builds = int(probe.Builds(world))
	if not await _wait_builds(builds + 2):
		_fail("macro map did not rebuild with the storm")
		return
	await _frames(120)
	var storm_top := -1.0
	for offset in [Vector2(0, 0), Vector2(9000, 0), Vector2(-9000, 0), Vector2(0, 9000), Vector2(0, -9000), Vector2(14000, 6000), Vector2(-14000, -6000)]:
		var column := await _column(base_around + offset.x, base_across + offset.y)
		if column.size() == 0:
			_fail("storm column timed out")
			return
		var extent := _extent(column)
		var above := _max_in(column, 18001.0, TOP)
		print("CLOUD_LAYERS storm off=", offset, " extent=", extent, " max_above_18000=", above)
		if above > 0.0:
			_fail("storm column has density above 18000 m: " + str(above))
			return
		storm_top = maxf(storm_top, extent.y)
	if storm_top < 8000.0:
		print("CLOUD_LAYERS WARN storm probes found no density above 8 km (top=", storm_top, "): the nothing-above-18000 check is weak")
	print("CLOUD_LAYERS=PASS")
	finished = true
	quit(0)

func _fail(reason):
	if finished:
		return
	finished = true
	printerr("CLOUD_LAYERS=FAIL ", reason)
	quit(1)
