extends Node

@export var enabled: bool = true
@export var player_path: NodePath = NodePath("../Player")
@export var root_to_shift_path: NodePath = NodePath("..")
@export var shift_threshold: float = 10000.0
@export var max_shift_step: float = 100000000.0
@export var shift_grid_size: float = 1024.0
@export var canonical_shift_bridge_group: StringName = &"voxel_floating_origin_bridge"
@export var log_shifts: bool = false


func _physics_process(_delta: float) -> void:
	if not enabled:
		return

	var player := get_node_or_null(player_path) as Node3D
	var root_to_shift := get_node_or_null(root_to_shift_path) as Node3D

	if player == null or root_to_shift == null:
		return

	var player_position := player.global_position
	var horizontal_position := Vector3(player_position.x, 0.0, player_position.z)
	if horizontal_position.length() < shift_threshold:
		return

	# Shift the whole world so coordinates stay near zero.
	var shift := horizontal_position
	if shift.length() > max_shift_step:
		shift = shift.normalized() * max_shift_step

	# Grid-aligned shifts remain exactly representable in float transforms at
	# every certified-ring coordinate. The residual stays close to the camera.
	var grid := maxf(1.0, shift_grid_size)
	shift = Vector3(roundf(shift.x / grid) * grid, 0.0, roundf(shift.z / grid) * grid)
	if shift.is_zero_approx():
		return

	root_to_shift.global_position -= shift
	if not _is_grid_aligned(root_to_shift.global_position, grid):
		push_error("Floating-origin root left the exact shift grid.")
	if log_shifts or "--certified-smoke-test" in OS.get_cmdline_user_args():
		print("Floating-origin shift: delta=%s root=%s grid=%.0fm" % [shift, root_to_shift.global_position, grid])

	if canonical_shift_bridge_group != StringName():
		# Register in the same physics frame as the world shift. Deferring this
		# leaves canonical/world conversion disagreeing for one frame.
		get_tree().call_group(
			canonical_shift_bridge_group,
			"register_floating_origin_shift",
			shift)


func _is_grid_aligned(value: Vector3, grid: float) -> bool:
	return absf(value.x / grid - roundf(value.x / grid)) < 0.0001 \
		and absf(value.z / grid - roundf(value.z / grid)) < 0.0001
