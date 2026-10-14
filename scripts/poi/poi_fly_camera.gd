extends Camera3D
## Free-fly debug camera for POI test scenes.
## Hold right mouse to look, WASD move, Q/E down/up, Shift = x10, Ctrl = x0.1,
## mouse wheel scales base speed. Number keys jump to bookmarks (Marker3D children of `bookmarks_path`).

@export var base_speed := 40.0
@export var mouse_sensitivity := 0.0025
@export var bookmarks_path: NodePath = ^"../Bookmarks"

var _yaw := 0.0
var _pitch := 0.0
var _looking := false

func _ready() -> void:
	_sync_angles()

func _sync_angles() -> void:
	var e := global_transform.basis.get_euler(EULER_ORDER_YXZ)
	_pitch = e.x
	_yaw = e.y

func goto_bookmark(index: int) -> bool:
	var bm := get_node_or_null(bookmarks_path)
	if bm == null or index < 0 or index >= bm.get_child_count():
		return false
	global_transform = (bm.get_child(index) as Node3D).global_transform
	_sync_angles()
	return true

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_RIGHT:
			_looking = mb.pressed
			Input.mouse_mode = Input.MOUSE_MODE_CAPTURED if _looking else Input.MOUSE_MODE_VISIBLE
		elif mb.pressed and mb.button_index == MOUSE_BUTTON_WHEEL_UP:
			base_speed = minf(base_speed * 1.25, 50000.0)
		elif mb.pressed and mb.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			base_speed = maxf(base_speed / 1.25, 0.5)
	elif event is InputEventMouseMotion and _looking:
		var mm := event as InputEventMouseMotion
		_yaw -= mm.relative.x * mouse_sensitivity
		_pitch = clampf(_pitch - mm.relative.y * mouse_sensitivity, -PI * 0.499, PI * 0.499)
		global_transform.basis = Basis.from_euler(Vector3(_pitch, _yaw, 0.0), EULER_ORDER_YXZ)
	elif event is InputEventKey and event.pressed and not event.echo:
		var k := event as InputEventKey
		if k.keycode >= KEY_1 and k.keycode <= KEY_9:
			goto_bookmark(k.keycode - KEY_1)

func _process(delta: float) -> void:
	var dir := Vector3.ZERO
	if Input.is_key_pressed(KEY_W): dir -= global_transform.basis.z
	if Input.is_key_pressed(KEY_S): dir += global_transform.basis.z
	if Input.is_key_pressed(KEY_A): dir -= global_transform.basis.x
	if Input.is_key_pressed(KEY_D): dir += global_transform.basis.x
	if Input.is_key_pressed(KEY_E): dir += Vector3.UP
	if Input.is_key_pressed(KEY_Q): dir -= Vector3.UP
	if dir == Vector3.ZERO:
		return
	var speed := base_speed
	if Input.is_key_pressed(KEY_SHIFT): speed *= 10.0
	if Input.is_key_pressed(KEY_CTRL): speed *= 0.1
	global_position += dir.normalized() * speed * delta
