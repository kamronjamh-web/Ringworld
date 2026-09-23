extends SceneTree

const CLOCK_SCRIPT := preload("res://scripts/RingDayNightCycle.cs")
var _host: Node3D
var _axis: Node3D
var _sun: DirectionalLight3D
var _clock: Node
var _reference_sun: DirectionalLight3D
var _reference_clock: Node
var _finished := false

func _initialize() -> void:
	_host = Node3D.new(); root.add_child(_host)
	_sun = DirectionalLight3D.new(); _sun.name = "StarLight"; _host.add_child(_sun)
	_axis = Node3D.new(); _axis.name = "RingAxis"; _host.add_child(_axis)
	_clock = CLOCK_SCRIPT.new(); _clock.name = "RingDayNightCycle"
	_clock.set("SunLightPath", NodePath("../StarLight")); _clock.set("RingAxisSourcePath", NodePath("../RingAxis"))
	_clock.set("Paused", true); _host.add_child(_clock); call_deferred("_run")
	create_timer(10.0).timeout.connect(_timeout)
	_reference_sun = DirectionalLight3D.new(); _reference_sun.name = "ReferenceStarLight"; _host.add_child(_reference_sun)
	_reference_clock = CLOCK_SCRIPT.new(); _reference_clock.name = "ReferenceClock"
	_reference_clock.set("SunLightPath", NodePath("../ReferenceStarLight")); _reference_clock.set("RingAxisSourcePath", NodePath("../RingAxis"))
	_reference_clock.set("Paused", true); _reference_clock.set("SunShadowStabilizationEnabled", false); _host.add_child(_reference_clock)

func _run() -> void:
	await process_frame
	_clock.set_process(false); _reference_clock.set_process(false) # Manual calls below make timing deterministic.
	if absf(float(_clock.get("SunShadowStepDegrees")) - .01) > .000001: return _fail("default step is not 0.01 degrees")
	_clock.set("SunShadowStepDegrees", NAN)
	if float(_clock.get("SunShadowStepDegrees")) != 0.0: return _fail("NaN step was not sanitized")
	_clock.set("SunShadowStepDegrees", 1.0)
	if absf(float(_clock.get("SunShadowStepDegrees")) - .05) > .000001: return _fail("step clamp failed")
	_clock.set("SunShadowStepDegrees", .000001)
	_clock.set("SunShadowStepDegrees", 0.0)
	if float(_clock.get("SunShadowStepDegrees")) != 0.0: return _fail("tiny positive step prevented zero bypass")
	_clock.set("SunShadowStepDegrees", .000001)
	_clock.set("SunShadowStepDegrees", NAN)
	if float(_clock.get("SunShadowStepDegrees")) != 0.0: return _fail("tiny positive step prevented invalid-value sanitization")
	_clock.set("SunShadowStepDegrees", .01)
	var noon := float(_clock.get("FullCycleSeconds")) * .25
	_clock.call("set_simulation_seconds", noon)
	_reference_clock.call("set_simulation_seconds", noon)
	if (_reference_clock.get("ToSun") as Vector3).distance_to(_clock.get("ToSun") as Vector3) > .0000001: return _fail("seek changed authoritative frame output")
	if _basis_chord(_sun.global_basis, _desired_basis()) > .0000001: return _fail("seek did not exactly reach desired basis")
	if float(_clock.get("EffectiveSunShadowStepDegrees")) < .009: return _fail("noon effective step was unexpectedly tightened")

	# .0125 seconds is .001 degrees at 1x: exact output advances, render basis holds.
	_clock.set("Paused", false); _clock.set("TimeScale", 1.0)
	var held := _sun.global_basis
	var before_to_sun := _clock.get("ToSun") as Vector3
	_clock.call("_process", .0125)
	if before_to_sun.distance_to(_clock.get("ToSun") as Vector3) <= 0.0: return _fail("authoritative ToSun did not advance")
	if _sun.global_basis != held: return _fail("sub-threshold .001-degree rendered basis was not held")
	if float(_clock.get("SunShadowAngularLagDegrees")) < .0008: return _fail("tiny angular lag was rounded away")

	# Identical setting replay must not leave a force-update dirty bit.
	_clock.set("SunShadowStepDegrees", .01)
	held = _sun.global_basis
	_clock.call("_process", .0125)
	if _sun.global_basis != held: return _fail("no-op step setting replay forced an update step=%.9f effective=%.9f lag=%.9f" % [float(_clock.get("SunShadowStepDegrees")), float(_clock.get("EffectiveSunShadowStepDegrees")), float(_clock.get("SunShadowAngularLagDegrees"))])
	_clock.call("_process", .125)
	if _sun.global_basis == held or _basis_chord(_sun.global_basis, _desired_basis()) > .0000001: return _fail("cumulative threshold did not update exact desired basis")
	if float(_clock.get("SunShadowAngularLagDegrees")) != 0.0: return _fail("updated basis retained lag")

	_clock.set("SunShadowStepDegrees", 0.0)
	var continuous_before := _sun.global_basis
	_clock.call("_process", .005)
	if _sun.global_basis == continuous_before: return _fail("zero step did not bypass hold")
	_clock.set("SunShadowStepDegrees", .01); _clock.set("SunShadowStabilizationEnabled", false)
	continuous_before = _sun.global_basis
	_clock.call("_process", .005)
	if _sun.global_basis == continuous_before: return _fail("disabled stabilization did not bypass hold")
	_clock.set("SunShadowStabilizationEnabled", true)

	_clock.call("set_simulation_seconds", noon); _clock.set("TimeScale", 100.0)
	continuous_before = _sun.global_basis
	_clock.call("_process", .005)
	if _sun.global_basis == continuous_before: return _fail("high-speed update did not catch up")

	_clock.set("Paused", true); _clock.call("set_simulation_seconds", 0.0)
	if float(_clock.get("EffectiveSunShadowStepDegrees")) > .0001: return _fail("horizon step was not tightened")
	_clock.call("set_simulation_seconds", noon)
	var desired_before_replacement := _desired_basis()
	_sun.queue_free(); await process_frame
	_sun = DirectionalLight3D.new(); _sun.name = "StarLight"; _host.add_child(_sun)
	_clock.call("_process", 0.0)
	if _basis_chord(_sun.global_basis, desired_before_replacement) > .0000001: return _fail("replacement light missed desired full basis")

	# Roll the actual light around desired Z; no seek, so full-basis deadband repairs it.
	_sun.global_basis = _sun.global_basis * Basis(Vector3.FORWARD, .02)
	_clock.call("_process", 0.0)
	if _basis_chord(_sun.global_basis, desired_before_replacement) > .0000001: return _fail("full-basis roll was not repaired")

	# A 0.001-degree configured step must work without float-angle rounding.
	_clock.set("SunShadowStepDegrees", .001)
	_clock.set("TimeScale", 1.0)
	_clock.set("Paused", false)
	_clock.call("set_simulation_seconds", noon)
	held = _sun.global_basis
	_clock.call("_process", .001)
	if _sun.global_basis != held: return _fail("0.001-degree setting did not hold smaller motion")
	_clock.call("_process", .02)
	if _sun.global_basis == held: return _fail("0.001-degree setting did not cross its threshold")

	# Five seconds at 60 Hz must advance exact celestial state while reducing light
	# transform changes. This measures the mechanism, not visual shimmer or GPU time.
	_clock.set("SunShadowStepDegrees", .01)
	_clock.call("set_simulation_seconds", noon)
	var transform_changes := 0
	for frame in range(300):
		held = _sun.global_basis
		_clock.call("_process", 1.0 / 60.0)
		if _sun.global_basis != held: transform_changes += 1
		var expected_seconds := noon + (frame + 1) / 60.0
		if absf(float(_clock.call("get_simulation_seconds")) - expected_seconds) > .000001: return _fail("stabilization altered clock progression")
		_reference_clock.call("set_simulation_seconds", expected_seconds)
		if (_reference_clock.get("ToSun") as Vector3).distance_to(_clock.get("ToSun") as Vector3) > .000001: return _fail("stabilization altered authoritative sun direction")
		if (_reference_clock.get("GiantDirection") as Vector3).distance_to(_clock.get("GiantDirection") as Vector3) > .000001: return _fail("stabilization altered giant orbit")
		if absf(float(_reference_clock.get("RawEclipseCoverage")) - float(_clock.get("RawEclipseCoverage"))) > .000001: return _fail("stabilization altered eclipse coverage")
		if float(_clock.get("SunShadowAngularLagDegrees")) > float(_clock.get("EffectiveSunShadowStepDegrees")) + .00002: return _fail("held light exceeded angular bound")
	if transform_changes <= 0 or transform_changes >= 150: return _fail("stabilization did not reduce normal-speed transform updates")
	_clock.set("Paused", true)
	held = _sun.global_basis
	_clock.call("_process", 1.0)
	if _sun.global_basis != held: return _fail("paused sun direction moved")
	_clock.call("set_cycle_fraction", .3)
	if _basis_chord(_sun.global_basis, _desired_basis()) > .0000001: return _fail("fraction seek did not force the light")
	print("SUN_SHADOW_TRANSFORM_UPDATES=%d/300 at 60 Hz, 1x, near noon" % transform_changes)
	print("SUN_SHADOW_STABILITY_SMOKE_TEST=PASS")
	_finish(0)

func _desired_basis() -> Basis:
	var z := (_clock.get("ToSun") as Vector3).normalized()
	var x := _axis.global_basis.y.normalized().cross(z)
	if x.length_squared() <= .000001: x = Vector3.RIGHT - z * Vector3.RIGHT.dot(z)
	x = x.normalized()
	return Basis(x, z.cross(x).normalized(), z).orthonormalized()

func _basis_chord(a: Basis, b: Basis) -> float:
	return (a.x - b.x).length() + (a.y - b.y).length() + (a.z - b.z).length()

func _fail(message: String) -> void:
	push_error("SUN_SHADOW_STABILITY_SMOKE_TEST=FAIL %s" % message)
	_finish(1)

func _finish(exit_code: int) -> void:
	if _finished: return
	_finished = true
	if is_instance_valid(_host): _host.queue_free()
	call_deferred("quit", exit_code)

func _timeout() -> void:
	if not _finished: _fail("watchdog timeout")
