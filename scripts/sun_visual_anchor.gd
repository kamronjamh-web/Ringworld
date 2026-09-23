extends Node3D

@export var player_path: NodePath = NodePath("../Player")
@export var sun_light_path: NodePath = NodePath("../StarLight")
@export var visual_distance: float = 500000.0
@export var follow_smoothing: float = 20.0

var player: Node3D = null
var sun_light: DirectionalLight3D = null


func _ready() -> void:
	top_level = true
	_resolve_references()


func _process(delta: float) -> void:
	_resolve_references()

	if player == null or sun_light == null:
		return

	# For a distant-star illusion, keep the visual at a fixed direction from the player.
	var to_sun: Vector3 = sun_light.global_transform.basis.z.normalized()
	var target_position: Vector3 = player.global_position + to_sun * maxf(visual_distance, 1.0)

	if follow_smoothing <= 0.0:
		global_position = target_position
	else:
		global_position = global_position.lerp(target_position, minf(1.0, follow_smoothing * delta))


func _resolve_references() -> void:
	if player == null or not is_instance_valid(player):
		player = get_node_or_null(player_path) as Node3D

	if sun_light == null or not is_instance_valid(sun_light):
		sun_light = get_node_or_null(sun_light_path) as DirectionalLight3D
