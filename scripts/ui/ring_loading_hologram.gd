extends Control

## A procedural illustration: no world geometry, assets, viewport or GPU readback.
var animation_speed := 0.18
var motion_enabled := true:
	set(value):
		if motion_enabled == value:
			return
		motion_enabled = value
		set_process(value)
		queue_redraw()
var hologram_brightness := 1.0
var assembly_progress := 0.0
var beacon_turn := 0.08
var beacon_across := 0.5
var _angle := 0.0
var _clock := 0.0
var _half_width := 0.18
var _seed_phase := 0.0
var _circumference_m := 1.0
var _width_m := 1.0

func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	resized.connect(queue_redraw)
	set_process(motion_enabled)

func configure(info: Dictionary) -> void:
	var circumference := maxf(1.0, float(info.get("circumference_m", float(info.get("radius_m", 0.0)) * TAU)))
	var width := maxf(1.0, float(info.get("width_m", 1.0)))
	_circumference_m = circumference
	_width_m = width
	# Width is deliberately exaggerated so even enormous rings remain legible.
	_half_width = clampf(0.12 + sqrt(width / circumference) * 3.0, 0.12, 0.38)
	_seed_phase = float(posmod(int(info.get("seed", 1337)), 8192)) / 8192.0 * TAU
	set_arrival_position(float(info.get("spawn_around_m", 0.0)), float(info.get("spawn_across_m", width * 0.5)))
	queue_redraw()

func set_arrival_position(around_m: float, across_m: float) -> void:
	if not is_finite(around_m) or not is_finite(across_m):
		return
	var around := fposmod(around_m / _circumference_m, 1.0)
	var across := clampf(across_m / _width_m, 0.0, 1.0)
	if beacon_turn == around and beacon_across == across:
		return
	beacon_turn = around
	beacon_across = across
	queue_redraw()

func set_presentation(speed: float, motion: bool, brightness: float) -> void:
	animation_speed = clampf(speed, 0.0, 0.6)
	motion_enabled = motion
	hologram_brightness = clampf(brightness, 0.35, 1.5)
	queue_redraw()

func set_progress(value: float) -> void:
	var clamped := clampf(value, 0.0, 1.0)
	if assembly_progress == clamped:
		return
	assembly_progress = clamped
	queue_redraw()

func _process(delta: float) -> void:
	if not motion_enabled:
		return
	_clock += delta
	_angle = fposmod(_angle + delta * animation_speed, TAU)
	queue_redraw()

func _project(point: Vector3) -> Vector3:
	var yaw := _angle + 0.38
	var rotated := Vector3(point.x * cos(yaw) - point.z * sin(yaw), point.y, point.x * sin(yaw) + point.z * cos(yaw))
	var tilt := -0.68
	var raised := Vector3(rotated.x, rotated.y * cos(tilt) - rotated.z * sin(tilt), rotated.y * sin(tilt) + rotated.z * cos(tilt))
	var perspective := 1.0 / (1.0 + raised.z * 0.13)
	var scale := minf(size.x * 0.40, size.y * 0.42)
	return Vector3(size.x * 0.5 + raised.x * scale * perspective, size.y * 0.50 - raised.y * scale * perspective, raised.z)

func _point(angle: float, across: float, radius: float = 1.0) -> Vector3:
	# A subtle deterministic contour gives interior bands character without
	# pretending to be a generated terrain preview. Structural edges stay round.
	var relief := (sin(angle * 5.0 + _seed_phase) + sin(angle * 11.0 - _seed_phase) * 0.45) * 0.008 * (1.0 - absf(across))
	return Vector3(cos(angle) * (radius - relief), across * _half_width, sin(angle) * (radius - relief))

func _wire(a: Vector3, b: Vector3, strength: float, width: float = 1.0) -> void:
	var pa := _project(a)
	var pb := _project(b)
	var depth := clampf(0.78 - (pa.z + pb.z) * 0.19, 0.28, 1.0)
	var alpha := clampf(strength * depth * hologram_brightness, 0.0, 1.0)
	draw_line(Vector2(pa.x, pa.y), Vector2(pb.x, pb.y), Color(0.19, 0.91, 0.87, alpha), width, true)

func _draw() -> void:
	if size.x < 1.0 or size.y < 1.0:
		return
	var center := size * 0.5
	var radius := minf(size.x * 0.40, size.y * 0.42)
	# Quiet drafting guides give the ring a stable frame of reference.
	for index in range(1, 5):
		draw_arc(center, radius * (0.7 + index * 0.14), 0, TAU, 80, Color(0.13, 0.38, 0.43, 0.065), 1.0, true)
	draw_line(Vector2(center.x, 20), Vector2(center.x, size.y - 20), Color(0.2, 0.5, 0.55, 0.08), 1)
	draw_line(Vector2(20, center.y), Vector2(size.x - 20, center.y), Color(0.2, 0.5, 0.55, 0.08), 1)
	const SEGMENTS := 96
	const SECTORS := 24
	var assembled := clampi(int(ceil((0.12 + assembly_progress * 0.88) * SECTORS)), 1, SECTORS)
	var scan := fposmod(_clock * animation_speed * 0.8, 1.0) * TAU if motion_enabled else PI * 0.3
	for index in range(SEGMENTS):
		var a := TAU * index / SEGMENTS
		var b := TAU * (index + 1) / SEGMENTS
		var active := int(index / 4) < assembled
		var intensity := 0.68 if active else 0.11
		var distance := absf(wrapf(a - scan, -PI, PI))
		if active and motion_enabled:
			intensity += exp(-distance * distance * 16.0) * 0.28
		for band in [-1.0, -0.5, 0.0, 0.5, 1.0]:
			_wire(_point(a, band), _point(b, band), intensity * (1.0 if absf(band) == 1 else 0.45))
		if index % 4 == 0:
			_wire(_point(a, -1), _point(a, 1), intensity * 0.62)
		_wire(_point(a, -1, 1.025), _point(b, -1, 1.025), intensity * 0.20)
		_wire(_point(a, 1, 1.025), _point(b, 1, 1.025), intensity * 0.20)
	# Position beacon: it marks the actual configured around/across position.
	var beacon := _project(_point(beacon_turn * TAU, beacon_across * 2.0 - 1.0, 1.03))
	var screen := Vector2(beacon.x, beacon.y)
	var pulse := 0.75 + sin(_clock * 1.6) * 0.15 if motion_enabled else 0.85
	draw_circle(screen, 13.0 * pulse, Color(0.33, 0.97, 0.86, 0.06 * hologram_brightness))
	draw_arc(screen, 7, 0, TAU, 24, Color(0.55, 1.0, 0.94, 0.65 * hologram_brightness), 1, true)
	draw_circle(screen, 2.5, Color(0.72, 1.0, 0.95, clampf(hologram_brightness, 0, 1)))
	draw_line(screen + Vector2(0, -8), screen + Vector2(0, -30), Color(0.55, 1, 0.94, 0.45), 1, true)
