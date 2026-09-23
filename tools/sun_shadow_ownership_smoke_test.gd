extends SceneTree

const ATMOSPHERE_SCRIPT := preload("res://scripts/day_side_atmosphere.gd")
const GRAPHICS_QUALITY_SCRIPT := preload("res://scripts/Voxel/LOD/RingGraphicsQualityController.cs")


class MockPlayer extends Node3D:
	func GetCanonicalSurfaceUp() -> Vector3:
		return Vector3.UP

	func GetCanonicalAtmospherePositionMeters() -> Vector3:
		return Vector3(0.0, 0.0, 500.0)

	func GetCanonicalAtmosphereHeightMeters() -> float:
		return 0.0


class MockGrid extends Node:
	var RingAcrossWidthMeters := 1000.0

	func GetCanonicalRingRadiusMeters() -> float:
		return 10000.0

	func GetStructuralBottomGlobalY() -> float:
		return -100.0


class MockCelestialClock extends Node:
	var ToSun := Vector3(0.0, 1.0, 0.0)
	var DaylightFactor := 1.0
	var GiantEclipseCoverage := 0.0
	var GameplayEclipseDarkness := 0.0
	var GiantDirection := Vector3.DOWN
	var GiantAngularRadiusDegrees := 0.0


var _host: Node3D
var _sun: DirectionalLight3D
var _atmosphere: Node
var _quality: Node
var _clock: MockCelestialClock


func _initialize() -> void:
	_host = Node3D.new()
	root.add_child(_host)
	_sun = DirectionalLight3D.new()
	_sun.name = "StarLight"
	_sun.shadow_enabled = true
	_host.add_child(_sun)
	var player := MockPlayer.new()
	player.name = "Player"
	_host.add_child(player)
	var grid := MockGrid.new()
	grid.name = "ChunkGridManager"
	_host.add_child(grid)
	_clock = MockCelestialClock.new()
	_clock.name = "RingDayNightCycle"
	_host.add_child(_clock)
	var world_environment := WorldEnvironment.new()
	world_environment.name = "SpaceEnvironment"
	world_environment.environment = Environment.new()
	_host.add_child(world_environment)
	_atmosphere = ATMOSPHERE_SCRIPT.new()
	_atmosphere.name = "DaySideAtmosphere"
	_atmosphere.player_path = NodePath("../Player")
	_atmosphere.canonical_grid_path = NodePath("../ChunkGridManager")
	_atmosphere.sun_light_path = NodePath("../StarLight")
	_atmosphere.celestial_clock_path = NodePath("../RingDayNightCycle")
	_atmosphere.world_environment_path = NodePath("../SpaceEnvironment")
	_host.add_child(_atmosphere)
	_quality = GRAPHICS_QUALITY_SCRIPT.new()
	_quality.name = "GraphicsQuality"
	_quality.set("SunLightPath", NodePath("../StarLight"))
	_quality.set("DayNightCyclePath", NodePath("../RingDayNightCycle"))
	_host.add_child(_quality)
	call_deferred("_run")


func _run() -> void:
	await process_frame
	# This fixture drives the production writer explicitly so each assertion has
	# one known competing-writer order.
	_atmosphere.set_process(false)
	if not _sun.has_meta("ringworld_shadow_quality_owned"):
		_fail("real graphics-quality controller did not claim the sun")
		return
	# Exercise the real atmosphere writer after the controller's startup preset.
	_quality.call("SetShadowTuningOverride", "Enabled", false)
	_quality.call("SetShadowTuningOverride", "SunAngularDistance", 1.5)
	_quality.call("SetShadowTuningOverride", "FadeStart", 0.63)
	_sun.directional_shadow_max_distance = 12345.0
	_atmosphere.call("_process", 1.0)
	if not _assert_shadow_values(false, 1.5, 0.63, 12345.0, "first owned atmosphere tick"):
		return
	if _sun.light_energy <= 0.01:
		_fail("atmosphere no longer applies daytime energy while shadows are quality-owned")
		return

	# Preset reapplication changes its coordinated cascade profile. The next real
	# atmosphere tick must preserve that profile and the explicit player overrides.
	_quality.call("ApplyPreset", 2, false, false) # High
	var profile_distance := _sun.directional_shadow_max_distance
	var profile_split_1 := _sun.directional_shadow_split_1
	_atmosphere.call("_process", 1.0)
	if not _assert_shadow_values(false, 1.5, 0.63, profile_distance, "preset-owned atmosphere tick"):
		return
	if not is_equal_approx(_sun.directional_shadow_split_1, profile_split_1):
		_fail("atmosphere rewrote the graphics controller's cascade profile")
		return

	# Eclipse and local day/night remain atmosphere-owned even while static shadow
	# values are not.
	_atmosphere.keep_half_ring_lit_globally = false
	_clock.DaylightFactor = 1.0
	_clock.GiantEclipseCoverage = 0.0
	_clock.GameplayEclipseDarkness = 0.0
	_atmosphere.call("_process", 1.0)
	var daylight_energy := _sun.light_energy
	_clock.DaylightFactor = 0.0
	_atmosphere.call("_process", 1.0)
	if _sun.light_energy >= daylight_energy or _sun.light_energy > _atmosphere.night_sun_energy + 0.0001:
		_fail("quality ownership prevented local day/night energy from updating")
		return
	_clock.DaylightFactor = 1.0
	_clock.GiantEclipseCoverage = 1.0
	_clock.GameplayEclipseDarkness = 1.0
	_atmosphere.call("_process", 1.0)
	if _sun.light_energy > 0.0001:
		_fail("quality ownership prevented eclipse daylight energy from updating")
		return

	# A false marker remains an unclaimed legacy path.
	_sun.set_meta("ringworld_shadow_quality_owned", false)
	_sun.shadow_enabled = false
	_sun.light_angular_distance = 1.25
	_sun.directional_shadow_max_distance = 9999.0
	_sun.directional_shadow_fade_start = 0.51
	_clock.GiantEclipseCoverage = 0.0
	_clock.GameplayEclipseDarkness = 0.0
	_atmosphere.call("_process", 1.0)
	if not _assert_shadow_values(true, 0.0, _atmosphere.shadow_fade_ratio, _atmosphere.day_shadow_distance, "false-marker legacy atmosphere tick"):
		return

	# An absent marker must behave identically for scenes without the controller.
	_sun.remove_meta("ringworld_shadow_quality_owned")
	_sun.shadow_enabled = false
	_sun.light_angular_distance = 1.25
	_sun.directional_shadow_max_distance = 9999.0
	_sun.directional_shadow_fade_start = 0.51
	_atmosphere.call("_process", 1.0)
	if not _assert_shadow_values(true, 0.0, _atmosphere.shadow_fade_ratio, _atmosphere.day_shadow_distance, "absent-marker legacy atmosphere tick"):
		return
	print("SUN_SHADOW_OWNERSHIP_SMOKE_TEST=PASS")
	_finish(0)


func _assert_shadow_values(enabled: bool, softness: float, fade: float, distance: float, label: String) -> bool:
	if _sun.shadow_enabled != enabled \
		or not is_equal_approx(_sun.light_angular_distance, softness) \
		or not is_equal_approx(_sun.directional_shadow_fade_start, fade) \
		or not is_equal_approx(_sun.directional_shadow_max_distance, distance):
		_fail("%s expected enabled=%s softness=%.3f fade=%.3f distance=%.1f; got enabled=%s softness=%.3f fade=%.3f distance=%.1f" % [label, enabled, softness, fade, distance, _sun.shadow_enabled, _sun.light_angular_distance, _sun.directional_shadow_fade_start, _sun.directional_shadow_max_distance])
		return false
	return true


func _fail(message: String) -> void:
	push_error("SUN_SHADOW_OWNERSHIP_SMOKE_TEST=FAIL %s" % message)
	_finish(1)


func _finish(exit_code: int) -> void:
	if is_instance_valid(_host):
		_host.free()
	call_deferred("_quit_with_code", exit_code)


func _quit_with_code(exit_code: int) -> void:
	quit(exit_code)
