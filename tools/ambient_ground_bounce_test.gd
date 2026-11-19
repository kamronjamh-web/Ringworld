extends SceneTree

## Ground bounce (ambient lead F1, 2026-11-19): headless contract for DaySideAtmosphere.ground_bounce_radiance, the radiance the sky shader's
## radiance-map pass shows below the horizon (ring_local_sky.gdshader ground_bounce_radiance), and the defaults that make it reach surfaces.
## Checks: clear noon / low sun values; zero at night, at eclipse totality, under a closed storm and underground; linear in albedo; the clear-weather
## sky contribution is 1 and the switch is on; the sky shader declares the uniform and gates it on AT_CUBEMAP_PASS.
## RUN: tools/run_tests.ps1 -Label <task> -Tests ambient_ground_bounce_test   (headless; prints AMBIENT_GROUND_BOUNCE=PASS)

const MARKER := "AMBIENT_GROUND_BOUNCE"
const ATMOSPHERE_SCRIPT := preload("res://scripts/day_side_atmosphere.gd")
const SKY_SHADER_PATH := "res://materials/ring_local_sky.gdshader"
const ALBEDO := 0.11
const SUN := Vector3(4.0, 4.0, 4.0)                 # StarLight white x day_sun_energy 4
const SKY_UP := Vector3(0.0306, 0.0739, 0.1836)     # haze L_up at noon on the default atmosphere (measured)

var failures: Array[String] = []


func _init() -> void:
	var noon := _bounce(SUN, sin(deg_to_rad(75.0)), SKY_UP, 1.0, 1.0, 1.0)
	var expected_noon := (SUN * sin(deg_to_rad(75.0)) + SKY_UP) * ALBEDO
	_near(noon, expected_noon, 0.0001, "clear noon")
	if noon.x < 0.40 or noon.x > 0.46:
		_fail("clear noon bounce %s is outside the 0.40-0.46 red band (rho 0.11 x (3.86 + sky))" % str(noon))
	var low_sun := _bounce(SUN, sin(deg_to_rad(8.0)), SKY_UP, 1.0, 1.0, 1.0)
	if not (low_sun.x > 0.05 and low_sun.x < noon.x * 0.25):
		_fail("8 degree sun bounce %s should be about a seventh of noon %s" % [str(low_sun), str(noon)])
	# Night: the sun is below the horizon and the day factor (sky visibility) is 0.
	_near(_bounce(SUN, sin(deg_to_rad(-20.0)), SKY_UP, 1.0, 0.0, 1.0), Vector3.ZERO, 0.0, "night")
	# Eclipse totality: the light energy is scaled to 0 and the day factor is 0.
	_near(_bounce(Vector3.ZERO, sin(deg_to_rad(75.0)), SKY_UP, 1.0, 0.0, 1.0), Vector3.ZERO, 0.0, "eclipse totality")
	# Closed storm: the cloud sun visibility and the storm ambient multiply both terms to 0.
	_near(_bounce(SUN, sin(deg_to_rad(75.0)), SKY_UP, 0.0, 0.0, 1.0), Vector3.ZERO, 0.0, "closed storm")
	var heavy := _bounce(SUN, sin(deg_to_rad(75.0)), SKY_UP, 0.02 * 0.2, 1.0 * 0.3 * 0.2, 1.0)
	if heavy.x > noon.x * 0.02:
		_fail("heavy storm bounce %s is more than 2 %% of clear noon %s" % [str(heavy), str(noon)])
	# Underground: no sky access.
	_near(_bounce(SUN, sin(deg_to_rad(75.0)), SKY_UP, 1.0, 1.0, 0.0), Vector3.ZERO, 0.0, "underground")
	var half_access := _bounce(SUN, sin(deg_to_rad(75.0)), SKY_UP, 1.0, 1.0, 0.5)
	_near(half_access, noon * 0.5, 0.0001, "half sky access")
	# Linear in albedo, and clamped inputs.
	_near(ATMOSPHERE_SCRIPT.ground_bounce_radiance(0.22, SUN, sin(deg_to_rad(75.0)), SKY_UP, 1.0, 1.0, 1.0), noon * 2.0, 0.0001, "albedo x2")
	_near(ATMOSPHERE_SCRIPT.ground_bounce_radiance(-1.0, SUN, 1.0, SKY_UP, 1.0, 1.0, 1.0), Vector3.ZERO, 0.0, "negative albedo")
	_near(_bounce(SUN, 1.0, SKY_UP, 4.0, 4.0, 4.0), _bounce(SUN, 1.0, SKY_UP, 1.0, 1.0, 1.0), 0.0001, "visibility clamp")
	# Defaults: clear weather takes the radiance map at full weight, the bounce is on, and its albedo is the haze ground albedo (0.11).
	var atmosphere: Node = ATMOSPHERE_SCRIPT.new()
	if not is_equal_approx(float(atmosphere.get("ambient_sky_contribution")), 1.0):
		_fail("ambient_sky_contribution default is %s, expected 1.0" % str(atmosphere.get("ambient_sky_contribution")))
	if not bool(atmosphere.get("ground_bounce_enabled")):
		_fail("ground_bounce_enabled default is off")
	if not is_equal_approx(float(atmosphere.get("haze_ground_albedo")), ALBEDO):
		_fail("haze_ground_albedo default is %s, expected %s" % [str(atmosphere.get("haze_ground_albedo")), str(ALBEDO)])
	atmosphere.free()
	# Sky shader: the uniform exists and is only used by the radiance-map pass.
	var code := FileAccess.get_file_as_string(SKY_SHADER_PATH)
	if code.find("uniform vec3 ground_bounce_radiance") < 0:
		_fail("ring_local_sky.gdshader does not declare ground_bounce_radiance")
	var gate := code.find("if (AT_CUBEMAP_PASS) {")
	var use := code.find("ground_bounce_radiance, below_horizon")
	if gate < 0 or use < 0 or use < gate or use - gate > 600:
		_fail("ground_bounce_radiance is not applied inside an AT_CUBEMAP_PASS block")
	if failures.is_empty():
		print("%s=PASS noon=%s low_sun=%s" % [MARKER, str(noon), str(low_sun)])
		quit(0)
	else:
		for failure in failures:
			push_error("%s=FAIL %s" % [MARKER, failure])
		quit(1)


func _bounce(direct: Vector3, sin_elevation: float, sky: Vector3, sun_visibility: float, sky_visibility: float, access: float) -> Vector3:
	return ATMOSPHERE_SCRIPT.ground_bounce_radiance(ALBEDO, direct, sin_elevation, sky, sun_visibility, sky_visibility, access)


func _near(value: Vector3, expected: Vector3, tolerance: float, label: String) -> void:
	if (value - expected).abs().x > tolerance or (value - expected).abs().y > tolerance or (value - expected).abs().z > tolerance:
		_fail("%s: %s, expected %s" % [label, str(value), str(expected)])


func _fail(message: String) -> void:
	failures.append(message)
