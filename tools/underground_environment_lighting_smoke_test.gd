extends Node

const ATMOSPHERE_SCRIPT := preload("res://scripts/day_side_atmosphere.gd")


func _ready() -> void:
	var atmosphere := ATMOSPHERE_SCRIPT.new()
	var world_environment := WorldEnvironment.new()
	var environment := Environment.new()
	var attributes := CameraAttributesPractical.new()
	var sun := DirectionalLight3D.new()
	var planet := DirectionalLight3D.new()
	var local_light := OmniLight3D.new()
	var emissive := StandardMaterial3D.new()
	world_environment.environment = environment
	world_environment.camera_attributes = attributes
	atmosphere.world_environment = world_environment
	atmosphere.sun_light = sun
	atmosphere.planet_light = planet
	atmosphere.storm_auto_exposure_enabled = true
	atmosphere.auto_exposure_min_sensitivity = 50.0
	atmosphere.auto_exposure_max_sensitivity = 160.0
	atmosphere.auto_exposure_scale = 0.30
	local_light.light_energy = 2.5
	emissive.emission_enabled = true
	emissive.emission = Color(0.4, 0.7, 1.0)
	emissive.emission_energy_multiplier = 3.0
	# The atmosphere feather must begin inside the ribbon and never retain a
	# nonzero factor in either exterior/wall domain.
	const TEST_RING_WIDTH := 128.0
	const EDGE_INTERIOR := 1.0
	_assert_near(atmosphere._ring_interior_factor_for_across(-0.001, TEST_RING_WIDTH), 0.0, "left exterior atmosphere")
	_assert_near(atmosphere._ring_interior_factor_for_across(TEST_RING_WIDTH, TEST_RING_WIDTH), 0.0, "right wall atmosphere")
	_assert_near(atmosphere._ring_interior_factor_for_across(TEST_RING_WIDTH + 0.001, TEST_RING_WIDTH), 0.0, "right exterior atmosphere")
	if atmosphere._ring_interior_factor_for_across(EDGE_INTERIOR, TEST_RING_WIDTH) <= 0.0 \
		or atmosphere._ring_interior_factor_for_across(TEST_RING_WIDTH - EDGE_INTERIOR, TEST_RING_WIDTH) <= 0.0:
		_fail("interior-only atmosphere feather did not begin inside both walls")
	_assert_near(atmosphere._ring_interior_factor_for_across(TEST_RING_WIDTH * 0.5, TEST_RING_WIDTH), 1.0, "ring-center atmosphere")
	# The lower-hull plane remains in the inclusive shell interval. This direct
	# pure-function smoke covers the exact bottom, below it, a far-below point,
	# normal interior, and a simultaneous across/vertical out-of-range point.
	const STRUCTURAL_BOTTOM := -22304.0
	if atmosphere._ring_interior_factor_for_shell(TEST_RING_WIDTH * 0.5, TEST_RING_WIDTH, STRUCTURAL_BOTTOM, STRUCTURAL_BOTTOM) <= 0.0:
		_fail("structural-bottom shell atmosphere was rejected")
	_assert_near(atmosphere._ring_interior_factor_for_shell(TEST_RING_WIDTH * 0.5, TEST_RING_WIDTH, STRUCTURAL_BOTTOM - 0.001, STRUCTURAL_BOTTOM), 0.0, "below structural bottom atmosphere")
	_assert_near(atmosphere._ring_interior_factor_for_shell(TEST_RING_WIDTH * 0.5, TEST_RING_WIDTH, STRUCTURAL_BOTTOM - 1000.0, STRUCTURAL_BOTTOM), 0.0, "far below structural bottom atmosphere")
	if atmosphere._ring_interior_factor_for_shell(TEST_RING_WIDTH * 0.5, TEST_RING_WIDTH, STRUCTURAL_BOTTOM + 1.0, STRUCTURAL_BOTTOM) <= 0.0:
		_fail("interior shell atmosphere was rejected")
	_assert_near(atmosphere._ring_interior_factor_for_shell(TEST_RING_WIDTH, TEST_RING_WIDTH, STRUCTURAL_BOTTOM, STRUCTURAL_BOTTOM), 0.0, "combined wall and lower-hull atmosphere")

	environment.ambient_light_color = Color(0.5, 0.6, 0.7)
	environment.ambient_light_energy = 0.35
	environment.ambient_light_sky_contribution = 0.4
	environment.fog_sky_affect = 0.6
	environment.fog_sun_scatter = 0.2
	environment.fog_light_energy = 0.9
	environment.fog_aerial_perspective = 0.78
	sun.light_energy = 4.0
	planet.light_energy = 0.3
	atmosphere._configure_eye_adaptation()
	atmosphere._apply_underground_lighting(environment, 0.0)
	_assert_near(sun.light_energy, 0.0, "sealed-roof sunlight")
	_assert_near(planet.light_energy, 0.0, "sealed-roof reflected light")
	_assert_near(environment.ambient_light_energy, atmosphere.underground_ambient_energy, "cavity ambient")
	_assert_near(environment.ambient_light_sky_contribution, 0.0, "cavity sky ambient")
	_assert_near(environment.fog_sun_scatter, 0.0, "cavity in-scattering")
	_assert_near(environment.fog_light_energy, 0.0, "cavity fog light")
	_assert_near(environment.fog_aerial_perspective, 0.0, "cavity aerial perspective")
	if attributes.auto_exposure_enabled:
		_fail("sealed-cavity auto exposure remained enabled")
	_assert_near(local_light.light_energy, 2.5, "local-light preservation")
	_assert_near(emissive.emission_energy_multiplier, 3.0, "emissive preservation")

	# Cave fog coupling (deep caves only): weight 0 leaves surface fog alone; a fog-off baseline lerps from zero density and sky tint.
	const CAVE_DENSITY := 0.0045
	environment.fog_enabled = false
	environment.fog_density = 0.0
	environment.fog_sky_affect = 0.0
	environment.fog_light_energy = 0.9
	environment.fog_aerial_perspective = 0.78
	environment.fog_sun_scatter = 0.2
	atmosphere.underground_fog_sky_affect = 0.5
	atmosphere._apply_underground_fog_coupling(environment, 0.0, CAVE_DENSITY)
	if environment.fog_enabled:
		_fail("zero cave fog weight enabled the fog")
	atmosphere._apply_underground_fog_coupling(environment, 0.01, CAVE_DENSITY)
	if not environment.fog_enabled:
		_fail("small cave fog weight did not enable the fog")
	_assert_near(environment.fog_density, 0.01 * CAVE_DENSITY, "fog-off baseline density at weight 0.01")
	_assert_near(environment.fog_sky_affect, 0.01 * 0.5, "fog-off baseline sky affect at weight 0.01")
	environment.fog_enabled = false
	environment.fog_density = 0.0
	environment.fog_sky_affect = 0.0
	environment.fog_light_color = Color(0.9, 0.9, 0.9)
	atmosphere._apply_underground_fog_coupling(environment, 1.0, CAVE_DENSITY)
	_assert_near(environment.fog_density, CAVE_DENSITY, "full-weight cave fog density")
	_assert_near(environment.fog_sky_affect, 0.5, "full-weight cave fog sky affect")
	_assert_near(environment.fog_aerial_perspective, atmosphere.underground_fog_aerial_perspective, "full-weight cave fog aerial perspective")
	_assert_near(environment.fog_sun_scatter, 0.0, "full-weight cave fog sun scatter")
	_assert_near(environment.fog_light_energy, 1.0, "full-weight cave fog light energy")
	if not environment.fog_light_color.is_equal_approx(atmosphere._underground_cave_fog_color()):
		_fail("full-weight cave fog color was not the cave fog color")
	environment.fog_enabled = true
	environment.fog_density = 0.02
	atmosphere._apply_underground_fog_coupling(environment, 1.0, CAVE_DENSITY)
	if environment.fog_density < 0.02 - 0.000001:
		_fail("cave fog lowered a denser baseline fog")
	var previous_density := -1.0
	for step in range(11):
		environment.fog_enabled = false
		environment.fog_density = 0.0
		environment.fog_sky_affect = 0.0
		atmosphere._apply_underground_fog_coupling(environment, step / 10.0, CAVE_DENSITY)
		if environment.fog_density < previous_density - 0.0000001:
			_fail("cave fog density was not monotonic in weight")
		previous_density = environment.fog_density

	# Surface configuration must be restorable after the underground pass.
	atmosphere._configure_eye_adaptation()
	if not attributes.auto_exposure_enabled:
		_fail("surface auto exposure was not restored")
	_assert_near(attributes.auto_exposure_max_sensitivity, 160.0, "surface exposure ceiling")
	print("UNDERGROUND_LIGHTING_PROPERTY_SMOKE_TEST=PASS")
	atmosphere.free()
	world_environment.free()
	sun.free()
	planet.free()
	local_light.free()


func _assert_near(actual: float, expected: float, label: String) -> void:
	if not is_equal_approx(actual, expected):
		_fail("%s expected %.6f, got %.6f" % [label, expected, actual])


func _fail(message: String) -> void:
	push_error("UNDERGROUND_LIGHTING_PROPERTY_SMOKE_TEST=FAIL: %s" % message)
	get_tree().quit(1)
