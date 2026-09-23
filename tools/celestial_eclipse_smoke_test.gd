extends SceneTree

const CLOCK_SCRIPT: Script = preload("res://scripts/RingDayNightCycle.cs")
const LEGACY_CLOCK_SCRIPT: Script = preload("res://scripts/ring_day_night_cycle.gd")
const CORE_RUNNER_SCRIPT: Script = preload("res://tools/CelestialCoreSmokeRunner.cs")
const ORBIT_SECONDS: float = 28187.36649295002
const DEFAULT_TOTAL_DURATION_SECONDS: float = 4500.0

var _clock: Node = null


func _initialize() -> void:
	var host := Node3D.new()
	host.name = "CelestialTestHost"
	root.add_child(host)

	var sun := DirectionalLight3D.new()
	sun.name = "StarLight"
	host.add_child(sun)

	var axis := Node3D.new()
	axis.name = "RingAxis"
	host.add_child(axis)

	_clock = CLOCK_SCRIPT.new() as Node
	_clock.name = "RingDayNightCycle"
	_clock.set("SunLightPath", NodePath("../StarLight"))
	_clock.set("RingAxisSourcePath", NodePath("../RingAxis"))
	_clock.set("InitialCycleFraction", 0.0)
	_clock.set("InitialSimulationSeconds", 0.0)
	_clock.set("InitialGiantOrbitalPhaseDegrees", 180.0)
	host.add_child(_clock)
	_clock.set_process(false)
	call_deferred("_run_checks")


func _run_checks() -> void:
	var legacy_error := _verify_retained_legacy_clock_defaults()
	if not legacy_error.is_empty():
		return _fail(legacy_error)
	var core_runner := CORE_RUNNER_SCRIPT.new() as Node
	var core_passed := core_runner != null and bool(core_runner.call("RunAll"))
	var detail := "C# runner did not instantiate"
	if core_runner != null:
		detail = str(core_runner.get("LastFailure"))
		core_runner.free()
	if not core_passed:
		return _fail("Celestial core math coverage failed: %s" % detail)
	if _clock == null:
		return _fail("C# celestial clock did not instantiate")
	if not _clock.has_method("evaluate_giant") \
			or not _clock.has_method("set_simulation_seconds") \
			or not _clock.has_method("get_simulation_seconds"):
		return _fail("Legacy StringName API compatibility is missing")
	if not bool(_clock.call("celestial_eclipse_self_test")):
		return _fail("C# default-total/partial-fixture geometry self-test failed")

	# A new world starts at opposition, so players never spawn into an eclipse.
	_set_and_evaluate(0.0)
	var starting_coverage := float(_clock.get("GiantEclipseCoverage"))
	if starting_coverage > 0.00001:
		return _fail("New-world giant phase starts in eclipse: %.6f" % starting_coverage)

	# With the 180-degree initial offset, half an orbit reaches conjunction.
	_set_and_evaluate(ORBIT_SECONDS * 0.5)
	var peak_coverage := float(_clock.get("GiantEclipseCoverage"))
	var giant_radius := float(_clock.get("GiantAngularRadiusDegrees"))
	if peak_coverage < 0.99999:
		return _fail("Default eclipse did not reach totality: %.7f" % peak_coverage)
	if absf(giant_radius - 30.8242334) > 0.001:
		return _fail("Unexpected giant angular radius: %.7f degrees" % giant_radius)
	_set_and_evaluate(ORBIT_SECONDS * 0.5 - DEFAULT_TOTAL_DURATION_SECONDS * 0.5 + 0.1)
	if float(_clock.get("RawEclipseCoverage")) < 0.99999:
		return _fail("Default total eclipse ended before its 4,500-second interior boundary")
	_set_and_evaluate(ORBIT_SECONDS * 0.5 + DEFAULT_TOTAL_DURATION_SECONDS * 0.5 + 0.1)
	if float(_clock.get("RawEclipseCoverage")) >= 1.0:
		return _fail("Default total eclipse extended beyond its 4,500-second exterior boundary")

	# Absolute time must reproduce the same direction and overlap after scrubbing.
	var saved_time := 123456.789
	_set_and_evaluate(saved_time)
	var saved_direction := _clock.get("GiantDirection") as Vector3
	var saved_coverage := float(_clock.get("GiantEclipseCoverage"))
	_set_and_evaluate(17.0)
	_set_and_evaluate(saved_time)
	var restored_direction := _clock.get("GiantDirection") as Vector3
	var restored_coverage := float(_clock.get("GiantEclipseCoverage"))
	var restored_time := float(_clock.call("get_simulation_seconds"))
	if saved_direction.distance_to(restored_direction) > 0.000001 \
			or absf(saved_coverage - restored_coverage) > 0.000001 \
			or absf(restored_time - saved_time) > 0.000001:
		return _fail("Absolute celestial time did not restore deterministically")

	print("CELESTIAL_ECLIPSE_RUNTIME_TEST=PASS peak=%.6f radius=%.6f" % [
		peak_coverage,
		giant_radius])
	quit(0)


func _verify_retained_legacy_clock_defaults() -> String:
	var legacy_clock := LEGACY_CLOCK_SCRIPT.new() as Node
	if legacy_clock == null:
		return "retained GDScript celestial clock did not instantiate"
	legacy_clock.set("simulation_seconds", 0.0)
	legacy_clock.call("evaluate_giant", Vector3.FORWARD, Vector3.UP)
	var error := ""
	if absf(float(legacy_clock.get("full_cycle_seconds")) - 4500.0) > 0.001 \
			or absf(float(legacy_clock.get("giant_distance_km")) - 136610.390625) > 0.01 \
			or absf(float(legacy_clock.get("giant_angular_radius_degrees")) - 30.8242334) > 0.001 \
			or float(legacy_clock.get("giant_eclipse_coverage")) < 0.99999:
		error = "retained GDScript celestial clock defaults diverged from the physical preset"
	legacy_clock.free()
	return error


func _set_and_evaluate(seconds: float) -> void:
	_clock.call("set_simulation_seconds", seconds)
	_clock.call("evaluate_giant", Vector3.FORWARD, Vector3.UP)


func _fail(message: String) -> void:
	push_error(message)
	quit(1)
