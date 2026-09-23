extends SceneTree

# State/continuity tests on the actual Normal/Certified weather publisher.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
var world: Node
var local: Node
var particles: GPUParticles3D
var driver: RefCounted
var failed := false
var ids := []
var rain_restarts := 0

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	world = WORLD.instantiate()
	if "--rain-profiles-certified" in OS.get_cmdline_user_args():
		var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", world, 5000000.0)
		launcher.free()
	root.add_child(world)
	for i in range(8): await process_frame
	world.get_node("Explorer").set_physics_process(false)
	world.get_node("RingVolumetricClouds").set_process(false)
	world.get_node("SunshineCloudBridge").set_process(false)
	world.get_node("RingDayNightCycle").set("Paused", true)
	local = world.get_node("LocalWeatherEffects")
	local.set_process(false)
	local.set("EnableLightning", false)
	particles = local.get_node("CameraLocalPrecipitation")
	driver = DRIVER.new()
	if not check(bool(driver.call("CheckProfileMath")), "pure rain profile sweep failed"): return
	target(0.0)
	step(200)
	ids = identities()
	rain_restarts = int(diag().restart_count)
	if not check(not particles.emitting and particles.amount == 4096 and particles.amount_ratio == 0.0, "clear/budget baseline"): return
	var samples := []
	for amount in [.15, .5, 1.0]:
		target(amount)
		step(200)
		var d := diag()
		if not invariant(): return
		if not check(absf(float(d.shared_intensity) - amount) < .0001, "shared rain intensity changed meaning"): return
		if not check(absf(float(d.scattered_weight) + float(d.steady_weight) + float(d.heavy_weight) - 1.0) < .000001, "profile weights do not conserve blend"): return
		if not check(particles.emitting and particles.lifetime == 2.2 and int(d.restart_count) == rain_restarts, "rain stage restarted/reconfigured lifecycle"): return
		samples.append(d)
		print("RAIN_PROFILE_SAMPLE intensity=", amount, " state=", d)
		var pushes := int(d.config_push_count)
		step(30)
		if not check(int(diag().config_push_count) == pushes, "unchanged rain keeps resending configuration"): return
	for index in range(1, 3):
		var a: Dictionary = samples[index - 1]
		var b: Dictionary = samples[index]
		if not check(float(b.effective_count) > float(a.effective_count) and Vector2(b.streak_size).y > Vector2(a.streak_size).y and Vector2(b.streak_size).x >= Vector2(a.streak_size).x and float(b.material_alpha) > float(a.material_alpha) and float(b.fall_velocity) > float(a.fall_velocity), "rain intensities are not visibly distinct"): return
	# Ramp across every blend threshold in both directions with one live emitter.
	for destination in [0.001, 1.0, 0.0]:
		target(destination)
		var previous := diag()
		for frame in range(220):
			step(1)
			var current := diag()
			if not invariant(): return
			if not check(absf(float(current.amount_ratio) - float(previous.amount_ratio)) < .05 and Vector2(current.streak_size).distance_to(Vector2(previous.streak_size)) < .1 and absf(float(current.material_alpha) - float(previous.material_alpha)) < .035 and absf(float(current.fall_velocity) - float(previous.fall_velocity)) < 1.0, "abrupt rain profile jump"): return
			if not check(int(current.restart_count) == rain_restarts, "rain ramp called Restart"): return
			previous = current
	if not check(not particles.emitting and particles.amount_ratio == 0, "rain stopped without returning to dry"): return
	# RGB edits must apply even when alpha/intensity stay unchanged.
	target(.5)
	step(200)
	local.set("RainColor", Color(.12, .3, .5, .42))
	step(1)
	var material := particles.draw_pass_1.material as StandardMaterial3D
	if not check(is_equal_approx(material.albedo_color.r, .12) and is_equal_approx(material.albedo_color.g, .3) and is_equal_approx(material.albedo_color.b, .5), "same-alpha RGB live edit ignored"): return
	var rain_before := diag()
	var lifetime_before := particles.lifetime
	# Rain -> snow -> IDENTICAL rain is the cache-invalidation regression.
	target(.5, true)
	step(200)
	if not check(bool(diag().snow) and Vector2(diag().streak_size).is_equal_approx(Vector2(.11, .11)) and particles.lifetime == 6.0, "snow appearance changed"): return
	target(.5)
	step(200)
	var rain_after := diag()
	if not check(not bool(rain_after.snow) and absf(float(rain_after.fall_velocity) - float(rain_before.fall_velocity)) < .001 and Vector2(rain_after.streak_size).is_equal_approx(Vector2(rain_before.streak_size)) and absf(float(rain_after.amount_ratio) - float(rain_before.amount_ratio)) < .0001 and particles.lifetime == lifetime_before, "same-intensity rain retained snow/base defaults"): return
	if not check(int(rain_after.restart_count) == rain_restarts + 2, "unexpected rain/snow restart count"): return
	# The visual profile must not alter the public wind consumed by audio.
	target(.5, false, Vector2(9, -4))
	step(200)
	if not check(Vector2(local.get("ResolvedWindMetersPerSecond")) == Vector2(9, -4), "shared wind changed"): return
	# No new resource or emitter allocation during ordinary tuning/transitions.
	if not invariant(): return
	local.set("Enabled", false)
	step(220)
	if not check(not particles.emitting and particles.amount_ratio == 0, "disable retained rain emission"): return
	local.set("Enabled", true)
	step(200)
	if not check(particles.emitting, "re-enable failed to recover rain"): return
	# Authored invalid speed/length/alpha must never reach the GPU as NaN/Inf.
	local.set("RainFallSpeedMetersPerSecond", NAN)
	local.set("RainStreakLengthMeters", INF)
	local.set("RainColor", Color(NAN, .3, .5, NAN))
	step(2)
	if not invariant(): return
	target(.5, true)
	step(1)
	target(.5)
	step(1)
	if not invariant(): return
	local.set("RainFallSpeedMetersPerSecond", 3.4028234e38)
	local.set("RainStreakLengthMeters", 3.4028234e38)
	target(1.0, false, Vector2(NAN, INF))
	step(200)
	if not invariant(): return
	if failed: return
	print("RAIN_PRECIPITATION_PROFILES_STATE=PASS rain_restarts=", rain_restarts, " final=", diag())
	world.queue_free()
	for i in range(3): await process_frame
	quit(0)

func target(intensity: float, snow := false, wind := Vector2.ZERO) -> void:
	driver.call("SetIntensity", local, intensity, snow, wind)
func step(count: int) -> void:
	for i in range(count): local.call("_Process", 1.0 / 60.0)
func diag() -> Dictionary:
	return local.call("GetPrecipitationDiagnostics")
func identities() -> Array:
	var d := diag()
	return [d.emitter_id, d.mesh_id, d.material_id, d.process_id]
func invariant() -> bool:
	var d := diag()
	if not check(identities() == ids and particles.amount == 4096, "particle resources reallocated"): return false
	for key in ["shared_intensity", "amount_ratio", "effective_count", "material_alpha", "fall_velocity", "initial_velocity_min", "initial_velocity_max", "spread"]:
		if not check(is_finite(float(d[key])) and float(d[key]) >= 0, "invalid GPU/profile value: " + key): return false
	return check(Vector2(d.streak_size).is_finite() and Vector3(d.gravity).is_finite() and float(d.amount_ratio) <= 1 and float(d.effective_count) <= 4096 and float(d.material_alpha) <= 1, "profile exceeds budget/range")
func check(ok: bool, message: String) -> bool:
	if not ok and not failed:
		failed = true
		push_error("RAIN_PRECIPITATION_PROFILES_STATE=FAIL " + message + " " + str(diag()))
		quit(1)
	return ok
