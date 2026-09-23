extends Node3D
class_name GravityController

@export var ring_radius: float = 5000.0
@export var ring_width: float = 600.0
@export var gravity_strength: float = 42.0


func get_ring_center() -> Vector3:
	return global_transform.origin


func get_outward_direction(world_position: Vector3) -> Vector3:
	var center := get_ring_center()
	var radial := world_position - center
	radial.y = 0.0

	if radial.length_squared() < 0.000001:
		return Vector3.RIGHT

	return radial.normalized()


func get_surface_up(world_position: Vector3) -> Vector3:
	# "Up" points away from the inner surface (toward the ring center).
	return -get_outward_direction(world_position)


func get_gravity_direction(world_position: Vector3) -> Vector3:
	# Gravity pulls toward the inner surface.
	return get_outward_direction(world_position)


func get_ring_angle(world_position: Vector3) -> float:
	var center := get_ring_center()
	var radial := world_position - center
	return atan2(radial.z, radial.x)


func get_closest_point_on_inner_surface(world_position: Vector3) -> Vector3:
	var center := get_ring_center()
	var outward := get_outward_direction(world_position)
	var half_width := ring_width * 0.5
	var clamped_y: float = clampf(world_position.y, center.y - half_width, center.y + half_width)
	return Vector3(center.x, clamped_y, center.z) + outward * ring_radius
