extends CharacterBody3D

const MODE_ON_FOOT: int = 0
const MODE_FAST_CRAFT: int = 1
const MODE_VERY_FAST_CRAFT: int = 2
const MODE_HYPER_CRAFT: int = 3

@export_group("Speed Tiers")
@export var on_foot_speed: float = 8.0
@export var fast_craft_speed: float = 500.0
@export var very_fast_craft_speed: float = 2000.0
@export var hyper_craft_speed: float = 10000.0
@export_enum("On Foot", "Fast Craft", "Very Fast Craft", "Hyper Craft") var mobility_mode: int = MODE_ON_FOOT

@export_group("Movement")
@export var player_speed: float = 8.0
@export var sprint_multiplier: float = 1.35
@export var acceleration: float = 7.0
@export var deceleration: float = 10.0
@export var fast_craft_accel_scale: float = 0.25
@export var very_fast_craft_accel_scale: float = 0.12
@export var hyper_craft_accel_scale: float = 0.08
@export var jump_speed: float = 14.0
@export var gravity_strength: float = 42.0
@export var up_align_speed: float = 8.0
@export var mouse_sensitivity: float = 0.003
@export var spawn_clearance: float = 3.0
@export var traversal_time_seconds: float = 300.0

@export_group("On-Foot Ground Probe")
@export var enable_ground_probe: bool = true
@export var ground_probe_start_up_offset: float = 1.2
@export var ground_probe_length: float = 4.0
@export var ground_probe_deadzone: float = 0.01
@export var ground_probe_push_gain: float = 10.0
@export var ground_probe_max_push_per_frame: float = 0.2
@export var ground_probe_calibrate_up_speed: float = 6.0
@export var ground_probe_relax_down_speed: float = 0.05
@export var on_foot_safe_margin: float = 0.08
@export var craft_safe_margin: float = 0.02

@export_group("Hyper Craft Flight")
@export var hyper_craft_vertical_ratio: float = 0.5

@export_group("Camera Clipping")
@export var on_foot_near_clip: float = 0.25
@export var craft_near_clip: float = 2.0
@export var very_fast_craft_near_clip: float = 8.0
@export var hyper_craft_near_clip: float = 20.0
@export var on_foot_far_clip_min: float = 300000.0
@export var fast_craft_far_clip_min: float = 340000.0
@export var very_fast_craft_far_clip_min: float = 420000.0
@export var hyper_craft_far_clip_min: float = 700000.0
@export var camera_far_padding: float = 50000.0
@export var maximum_far_clip: float = 1200000.0

@export var gravity_controller_path: NodePath = NodePath("../RingWorld/GravityController")
@export var debug_label_path: NodePath = NodePath("../DebugUI/DebugLabel")

@onready var head: Node3D = $Head
@onready var camera: Camera3D = $Head/Camera3D

var gravity_controller = null
var debug_label: Label
var smoothed_up: Vector3 = Vector3.UP
var yaw_angle: float = 0.0
var pitch_angle: float = 0.0
var previous_ring_angle: float = 0.0
var accumulated_ring_angle: float = 0.0
var total_distance_travelled: float = 0.0
var required_far_clip: float = 300000.0
var ground_probe_baseline_distance: float = -1.0


func _ready() -> void:
	_ensure_default_input_map()
	Input.set_mouse_mode(Input.MOUSE_MODE_CAPTURED)

	gravity_controller = get_node_or_null(gravity_controller_path)
	debug_label = get_node_or_null(debug_label_path) as Label
	floor_snap_length = 2.0

	if gravity_controller != null:
		gravity_strength = gravity_controller.gravity_strength
		_snap_to_start_height()
		smoothed_up = gravity_controller.get_surface_up(global_position)
		previous_ring_angle = gravity_controller.get_ring_angle(global_position)
		_refresh_required_far_clip(gravity_controller.ring_radius)
	else:
		_refresh_required_far_clip(0.0)

	_set_mobility_mode(mobility_mode)


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion and Input.get_mouse_mode() == Input.MOUSE_MODE_CAPTURED:
		yaw_angle -= event.relative.x * mouse_sensitivity
		pitch_angle = clamp(pitch_angle - event.relative.y * mouse_sensitivity, deg_to_rad(-85.0), deg_to_rad(85.0))

	if event.is_action_pressed("mode_on_foot"):
		_set_mobility_mode(MODE_ON_FOOT)

	if event.is_action_pressed("mode_fast_craft"):
		_set_mobility_mode(MODE_FAST_CRAFT)

	if event.is_action_pressed("mode_very_fast_craft"):
		_set_mobility_mode(MODE_VERY_FAST_CRAFT)

	if event.is_action_pressed("mode_hyper_craft"):
		_set_mobility_mode(MODE_HYPER_CRAFT)

	if event.is_action_pressed("ui_cancel") and not _live_tuning_drawer_owns_mouse():
		if Input.get_mouse_mode() == Input.MOUSE_MODE_CAPTURED:
			Input.set_mouse_mode(Input.MOUSE_MODE_VISIBLE)
		else:
			Input.set_mouse_mode(Input.MOUSE_MODE_CAPTURED)


func _live_tuning_drawer_owns_mouse() -> bool:
	return get_tree().root.get_meta("ringworld_live_tuning_mouse_open", false)


func _physics_process(delta: float) -> void:
	if gravity_controller == null:
		return

	gravity_strength = gravity_controller.gravity_strength

	var target_up: Vector3 = gravity_controller.get_surface_up(global_position)
	smoothed_up = smoothed_up.slerp(target_up, min(1.0, up_align_speed * delta)).normalized()
	up_direction = smoothed_up

	var outward: Vector3 = gravity_controller.get_outward_direction(global_position)
	var ring_tangent := Vector3(-outward.z, 0.0, outward.x).normalized()
	if ring_tangent.length_squared() < 0.000001:
		ring_tangent = -global_transform.basis.z

	var desired_forward := ring_tangent.rotated(smoothed_up, yaw_angle).normalized()
	var right_dir := desired_forward.cross(smoothed_up).normalized()
	desired_forward = smoothed_up.cross(right_dir).normalized()

	global_transform.basis = Basis(right_dir, smoothed_up, -desired_forward).orthonormalized()
	head.rotation = Vector3(pitch_angle, 0.0, 0.0)

	var input_vec := Input.get_vector("move_left", "move_right", "move_forward", "move_back")
	var move_dir := Vector3.ZERO
	if input_vec.length_squared() > 0.0:
		move_dir = (right_dir * input_vec.x - desired_forward * input_vec.y).normalized()

	var target_speed := player_speed
	if mobility_mode == MODE_ON_FOOT and Input.is_action_pressed("sprint"):
		target_speed *= sprint_multiplier

	var down_dir := -smoothed_up
	var vertical_speed := velocity.dot(down_dir)
	var horizontal_velocity := velocity - down_dir * vertical_speed

	var target_horizontal := move_dir * target_speed
	var blend_rate := acceleration
	if move_dir == Vector3.ZERO:
		blend_rate = deceleration

	var mode_accel_scale: float = 1.0
	if mobility_mode == MODE_FAST_CRAFT:
		mode_accel_scale = fast_craft_accel_scale
	elif mobility_mode == MODE_VERY_FAST_CRAFT:
		mode_accel_scale = very_fast_craft_accel_scale
	elif mobility_mode == MODE_HYPER_CRAFT:
		mode_accel_scale = hyper_craft_accel_scale

	if mobility_mode == MODE_HYPER_CRAFT:
		floor_snap_length = 0.0
		var vertical_input: float = Input.get_action_strength("fly_up") - Input.get_action_strength("fly_down")
		var target_vertical_speed: float = -vertical_input * target_speed * hyper_craft_vertical_ratio
		var target_velocity: Vector3 = target_horizontal + down_dir * target_vertical_speed
		velocity = velocity.move_toward(target_velocity, blend_rate * target_speed * mode_accel_scale * delta)
		move_and_slide()

		var ring_plane_velocity: Vector3 = velocity - down_dir * velocity.dot(down_dir)
		_update_progress_debug(ring_plane_velocity.length())
		return

	floor_snap_length = 2.0

	horizontal_velocity = horizontal_velocity.move_toward(target_horizontal, blend_rate * target_speed * mode_accel_scale * delta)

	vertical_speed += gravity_strength * delta
	if is_on_floor() and vertical_speed > 0.0:
		vertical_speed = 0.0

	if mobility_mode == MODE_ON_FOOT and Input.is_action_just_pressed("jump") and is_on_floor():
		vertical_speed = -jump_speed

	velocity = horizontal_velocity + down_dir * vertical_speed
	move_and_slide()
	_apply_ground_probe_anti_clip(delta, down_dir)

	_update_progress_debug(horizontal_velocity.length())


func apply_ring_settings(new_radius: float, new_traversal_time: float, new_player_speed: float, new_gravity_strength: float) -> void:
	traversal_time_seconds = maxf(new_traversal_time, 1.0)
	on_foot_speed = maxf(new_player_speed, 0.1)
	gravity_strength = maxf(new_gravity_strength, 0.1)

	if gravity_controller != null:
		gravity_controller.ring_radius = maxf(new_radius, 1.0)

	_refresh_required_far_clip(new_radius)

	_set_mobility_mode(mobility_mode)


func _update_progress_debug(horizontal_speed: float) -> void:
	if gravity_controller == null:
		return

	var ring_angle: float = gravity_controller.get_ring_angle(global_position)
	var delta_angle := wrapf(ring_angle - previous_ring_angle, -PI, PI)
	accumulated_ring_angle += delta_angle
	previous_ring_angle = ring_angle

	var circumference: float = TAU * gravity_controller.ring_radius
	total_distance_travelled = abs(accumulated_ring_angle) * gravity_controller.ring_radius
	var wrapped_distance := fposmod(accumulated_ring_angle * gravity_controller.ring_radius, circumference)
	if wrapped_distance < 0.0:
		wrapped_distance += circumference

	var percent_complete: float = (wrapped_distance / circumference) * 100.0
	var estimated_loop_time: float = circumference / maxf(horizontal_speed, 0.01)
	var angle_deg := fposmod(rad_to_deg(ring_angle), 360.0)
	var mode_label: String = _get_mode_label()

	if debug_label != null:
		debug_label.text = "Mode: %s (1/2/3/4)\nSpeed: %.2f m/s\nDistance Around Ring: %.1f m\nRing Complete: %.2f %%\nEstimated Full Loop Time: %.1f s\nRing Angle: %.1f deg\nTotal Travelled: %.1f m" % [
			mode_label,
			horizontal_speed,
			wrapped_distance,
			percent_complete,
			estimated_loop_time,
			angle_deg,
			total_distance_travelled
		]


func _set_mobility_mode(new_mode: int) -> void:
	mobility_mode = clampi(new_mode, MODE_ON_FOOT, MODE_HYPER_CRAFT)
	var near_clip: float = on_foot_near_clip
	var far_clip: float = maxf(required_far_clip, on_foot_far_clip_min)

	if mobility_mode == MODE_ON_FOOT:
		player_speed = on_foot_speed
	elif mobility_mode == MODE_FAST_CRAFT:
		player_speed = fast_craft_speed
		near_clip = craft_near_clip
		far_clip = maxf(required_far_clip, fast_craft_far_clip_min)
	elif mobility_mode == MODE_VERY_FAST_CRAFT:
		player_speed = very_fast_craft_speed
		near_clip = very_fast_craft_near_clip
		far_clip = maxf(required_far_clip, very_fast_craft_far_clip_min)
	else:
		player_speed = hyper_craft_speed
		near_clip = hyper_craft_near_clip
		far_clip = maxf(required_far_clip, hyper_craft_far_clip_min)

	if camera != null:
		camera.near = near_clip
		camera.far = far_clip

	if mobility_mode == MODE_ON_FOOT:
		safe_margin = on_foot_safe_margin
	else:
		safe_margin = craft_safe_margin
		ground_probe_baseline_distance = -1.0


func _get_mode_label() -> String:
	if mobility_mode == MODE_ON_FOOT:
		return "On Foot"

	if mobility_mode == MODE_FAST_CRAFT:
		return "Fast Craft"

	if mobility_mode == MODE_VERY_FAST_CRAFT:
		return "Very Fast Craft"

	return "Hyper Craft"


func _refresh_required_far_clip(ring_radius_value: float) -> void:
	var safe_radius: float = maxf(ring_radius_value, 1000.0)
	# Rendering a full ring through the gameplay camera makes its frustum too
	# deep for Forward+ light culling. The scaled far viewport owns the distant
	# ring; keep this camera within its stable regional presentation range.
	required_far_clip = minf(
		safe_radius * 2.1 + camera_far_padding,
		maxf(maximum_far_clip, on_foot_far_clip_min))


func GetCanonicalAtmosphereHeightMeters() -> float:
	# The inner surface is at the gravity controller's ring radius.  Moving
	# toward the ring centre is positive terrain-relative altitude.
	if gravity_controller == null:
		return 0.0

	var centre: Vector3 = gravity_controller.get_ring_center()
	var radial: Vector3 = global_position - centre
	radial.y = 0.0
	return maxf(0.0, gravity_controller.ring_radius - radial.length())


func _apply_ground_probe_anti_clip(delta: float, down_dir: Vector3) -> void:
	if not enable_ground_probe:
		return

	if mobility_mode != MODE_ON_FOOT:
		return

	if not is_on_floor():
		return

	var probe_origin: Vector3 = global_position + smoothed_up * ground_probe_start_up_offset
	var probe_target: Vector3 = probe_origin + down_dir * maxf(ground_probe_length, 0.5)

	var query: PhysicsRayQueryParameters3D = PhysicsRayQueryParameters3D.create(probe_origin, probe_target)
	query.exclude = [self]
	query.collide_with_areas = false

	var result: Dictionary = get_world_3d().direct_space_state.intersect_ray(query)
	if result.is_empty():
		return

	var hit_position: Vector3 = result["position"]
	var hit_distance: float = probe_origin.distance_to(hit_position)

	if ground_probe_baseline_distance < 0.0:
		ground_probe_baseline_distance = hit_distance
		return

	if hit_distance > ground_probe_baseline_distance:
		ground_probe_baseline_distance = lerpf(ground_probe_baseline_distance, hit_distance, minf(1.0, ground_probe_calibrate_up_speed * delta))
	else:
		ground_probe_baseline_distance = maxf(hit_distance, ground_probe_baseline_distance - ground_probe_relax_down_speed * delta)

	var penetration: float = ground_probe_baseline_distance - hit_distance
	if penetration > ground_probe_deadzone:
		var push_amount: float = minf(ground_probe_max_push_per_frame, penetration * ground_probe_push_gain)
		global_position += smoothed_up * push_amount


func _snap_to_start_height() -> void:
	var center: Vector3 = gravity_controller.get_ring_center()
	if global_position.distance_to(center) < 10.0:
		var outward := Vector3.RIGHT
		global_position = center + outward * (gravity_controller.ring_radius - spawn_clearance)


func _ensure_default_input_map() -> void:
	_add_key_binding("move_forward", KEY_W)
	_add_key_binding("move_forward", KEY_UP)
	_add_key_binding("move_back", KEY_S)
	_add_key_binding("move_back", KEY_DOWN)
	_add_key_binding("move_left", KEY_A)
	_add_key_binding("move_left", KEY_LEFT)
	_add_key_binding("move_right", KEY_D)
	_add_key_binding("move_right", KEY_RIGHT)
	_add_key_binding("jump", KEY_SPACE)
	_add_key_binding("sprint", KEY_SHIFT)
	_add_key_binding("mode_on_foot", KEY_1)
	_add_key_binding("mode_on_foot", KEY_KP_1)
	_add_key_binding("mode_fast_craft", KEY_2)
	_add_key_binding("mode_fast_craft", KEY_KP_2)
	_add_key_binding("mode_very_fast_craft", KEY_3)
	_add_key_binding("mode_very_fast_craft", KEY_KP_3)
	_add_key_binding("mode_hyper_craft", KEY_4)
	_add_key_binding("mode_hyper_craft", KEY_KP_4)
	_add_key_binding("fly_up", KEY_SPACE)
	_add_key_binding("fly_up", KEY_E)
	_add_key_binding("fly_down", KEY_Q)
	_add_key_binding("fly_down", KEY_C)


func _add_key_binding(action: StringName, keycode: int) -> void:
	if not InputMap.has_action(action):
		InputMap.add_action(action)

	for existing_event in InputMap.action_get_events(action):
		if existing_event is InputEventKey and (existing_event.physical_keycode == keycode or existing_event.keycode == keycode):
			return

	var key_event := InputEventKey.new()
	key_event.keycode = keycode
	key_event.physical_keycode = keycode
	InputMap.action_add_event(action, key_event)
