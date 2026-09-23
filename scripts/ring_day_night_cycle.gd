extends Node

## Rotates the star direction around the ring's axis.  The terrain remains in
## canonical coordinates; rotating the light is the visually equivalent and
## avoids moving streamed terrain, physics, or floating-origin state.

@export var sun_light_path: NodePath = NodePath("../StarLight")
@export var ring_axis_source_path: NodePath = NodePath("../ChunkGridManager")
@export_range(60.0, 7200.0, 1.0) var full_cycle_seconds: float = 4500.0
@export_range(1.0, 100.0, 1.0) var time_scale: float = 1.0:
	set(value):
		time_scale = clampf(value, 1.0, 100.0)
		ProjectSettings.set_setting("ringworld/runtime/day_night_speed", time_scale)
@export_range(0.0, 1.0, 0.001) var initial_cycle_fraction: float = 0.0
@export var ring_axis: Vector3 = Vector3.UP

@export_group("Deterministic Celestial Time")
@export var initial_simulation_seconds: float = 0.0
@export var paused: bool = false

@export_group("Gas Giant Compatibility")
@export var giant_enabled: bool = true
@export_range(10000.0, 200000.0, 100.0) var giant_radius_km: float = 70000.0
@export_range(100000.0, 5000000.0, 1000.0) var giant_distance_km: float = 136610.390625
@export_range(60.0, 2000000.0, 1.0) var giant_orbit_seconds: float = 28187.36649295002
# The orbit plane is tilted so this is the closest approach on every orbit.
@export_range(-89.9, 89.9, 0.001) var giant_grazing_miss_degrees: float = 10.87182510946549
@export_range(0.05, 2.0, 0.001) var star_angular_radius_degrees: float = 0.265

var sun_light: DirectionalLight3D = null
var ring_axis_source: Node3D = null
var cycle_fraction: float = 0.0
var simulation_seconds: float = 0.0
var giant_direction: Vector3 = Vector3.ZERO
var giant_angular_radius_degrees: float = 0.0
var giant_eclipse_coverage: float = 0.0
var _base_sun_basis: Basis = Basis.IDENTITY


func _ready() -> void:
	_resolve_sun()
	cycle_fraction = fposmod(initial_cycle_fraction, 1.0)
	simulation_seconds = maxf(0.0, initial_simulation_seconds)
	if is_zero_approx(simulation_seconds):
		simulation_seconds = cycle_fraction * full_cycle_seconds
	var saved_speed: float = float(ProjectSettings.get_setting("ringworld/runtime/day_night_speed", time_scale))
	time_scale = clampf(saved_speed, 1.0, 100.0)
	if sun_light != null:
		_base_sun_basis = sun_light.global_basis
	_apply_sun_direction()
	if "--celestial-eclipse-smoke-test" in OS.get_cmdline_user_args():
		_run_eclipse_smoke_test()


func _process(delta: float) -> void:
	_resolve_sun()
	if sun_light == null:
		return
	if not paused:
		simulation_seconds += delta * time_scale
		cycle_fraction = fposmod(simulation_seconds / maxf(1.0, full_cycle_seconds), 1.0)
	_apply_sun_direction()


func set_speed_multiplier(multiplier: float) -> void:
	time_scale = multiplier


func set_cycle_fraction(fraction: float) -> void:
	cycle_fraction = fposmod(fraction, 1.0)
	# Preserve the absolute day/orbit count while changing only local clock time.
	var day_seconds := maxf(1.0, full_cycle_seconds)
	simulation_seconds = floor(simulation_seconds / day_seconds) * day_seconds + cycle_fraction * day_seconds
	_apply_sun_direction()


func get_speed_multiplier() -> float:
	return time_scale


func get_cycle_time_seconds() -> float:
	return cycle_fraction * full_cycle_seconds


func get_simulation_seconds() -> float:
	return simulation_seconds


func set_simulation_seconds(seconds: float) -> void:
	simulation_seconds = maxf(0.0, seconds)
	cycle_fraction = fposmod(simulation_seconds / maxf(1.0, full_cycle_seconds), 1.0)
	_apply_sun_direction()


# Returns raw geometry only. Presentation owners decide how it affects direct,
# ambient, cloud, and gameplay lighting independently.
func evaluate_giant(to_sun: Vector3, axis: Vector3) -> void:
	if not giant_enabled:
		giant_direction = -to_sun.normalized()
		giant_angular_radius_degrees = 0.0
		giant_eclipse_coverage = 0.0
		return
	var sun := to_sun.normalized()
	var phase := TAU * fposmod(simulation_seconds / maxf(1.0, giant_orbit_seconds), 1.0)
	var miss := deg_to_rad(giant_grazing_miss_degrees)
	# A great circle's closest angular distance to the star is asin(dot(sun,n)).
	# Tilt the plane normal by `miss`; rotating inside this plane can never align
	# the giant with the star, unlike an angular phase offset.
	var reference := axis.normalized() if axis.length_squared() > 0.000001 else Vector3.UP
	if absf(reference.dot(sun)) > 0.98:
		reference = Vector3.RIGHT
	var perpendicular := (reference - sun * reference.dot(sun)).normalized()
	var plane_normal := (sun * sin(miss) + perpendicular * cos(miss)).normalized()
	var closest := (sun - plane_normal * sun.dot(plane_normal)).normalized()
	var orbit_tangent := plane_normal.cross(closest).normalized()
	giant_direction = (closest * cos(phase) + orbit_tangent * sin(phase)).normalized()
	var giant_radius := asin(clampf(giant_radius_km / maxf(giant_distance_km, giant_radius_km + 0.001), 0.0, 0.999999))
	var star_radius := deg_to_rad(star_angular_radius_degrees)
	var separation := acos(clampf(sun.dot(giant_direction), -1.0, 1.0))
	giant_angular_radius_degrees = rad_to_deg(giant_radius)
	giant_eclipse_coverage = _disc_overlap_fraction(star_radius, giant_radius, separation)


func _disc_overlap_fraction(star_radius: float, giant_radius: float, separation: float) -> float:
	if separation >= star_radius + giant_radius:
		return 0.0
	if giant_radius >= separation + star_radius:
		return 1.0
	if star_radius >= separation + giant_radius:
		return clampf((giant_radius * giant_radius) / (star_radius * star_radius), 0.0, 1.0)
	var d := maxf(separation, 0.000001)
	var a1 := acos(clampf((d * d + star_radius * star_radius - giant_radius * giant_radius) / (2.0 * d * star_radius), -1.0, 1.0))
	var a2 := acos(clampf((d * d + giant_radius * giant_radius - star_radius * star_radius) / (2.0 * d * giant_radius), -1.0, 1.0))
	var area := star_radius * star_radius * a1 + giant_radius * giant_radius * a2 - 0.5 * sqrt(maxf(0.0, (-d + star_radius + giant_radius) * (d + star_radius - giant_radius) * (d - star_radius + giant_radius) * (d + star_radius + giant_radius)))
	return clampf(area / (PI * star_radius * star_radius), 0.0, 1.0)


func _run_eclipse_smoke_test() -> void:
	var saved_seconds := simulation_seconds
	var sun := Vector3.FORWARD
	# This retained clock uses conjunction at epoch zero (unlike the C# clock's
	# authored opposition start), so test the physical +/- 2,250-second contacts.
	simulation_seconds = 0.0
	evaluate_giant(sun, Vector3.UP)
	var center_coverage := giant_eclipse_coverage
	simulation_seconds = 2249.9
	evaluate_giant(sun, Vector3.UP)
	var inside_coverage := giant_eclipse_coverage
	simulation_seconds = 2250.1
	evaluate_giant(sun, Vector3.UP)
	var outside_coverage := giant_eclipse_coverage
	if center_coverage < 0.99999 or inside_coverage < 0.99999 or outside_coverage >= 1.0 \
			or absf(giant_angular_radius_degrees - 30.8242334) > 0.001:
		push_error("Celestial eclipse smoke test failed: center=%.4f inside=%.4f outside=%.4f giantRadius=%.3f" % [center_coverage, inside_coverage, outside_coverage, giant_angular_radius_degrees])
	else:
		print("CELESTIAL_ECLIPSE_SMOKE_TEST=PASS totalDuration=4500 center=%.4f outside=%.4f" % [center_coverage, outside_coverage])
	simulation_seconds = saved_seconds


func _resolve_sun() -> void:
	if sun_light == null or not is_instance_valid(sun_light):
		sun_light = get_node_or_null(sun_light_path) as DirectionalLight3D
		if sun_light != null:
			_base_sun_basis = sun_light.global_basis
	if ring_axis_source == null or not is_instance_valid(ring_axis_source):
		ring_axis_source = get_node_or_null(ring_axis_source_path) as Node3D


func _apply_sun_direction() -> void:
	var axis := Vector3(ring_axis_source.global_basis.y).normalized() if ring_axis_source != null else ring_axis.normalized()
	if axis.length_squared() < 0.000001:
		axis = Vector3.UP
	# One complete revolution is one ring-spin day.  Moving the distant sun in
	# this local frame makes each fixed point on the ring pass through daylight
	# and night without rotating the active voxel/physics world.
	sun_light.global_basis = Basis(axis, TAU * cycle_fraction) * _base_sun_basis
