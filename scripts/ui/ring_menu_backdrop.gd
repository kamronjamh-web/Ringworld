extends Control
class_name RingMenuBackdrop

var _phase := 0.0


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	set_process(true)


func _process(delta: float) -> void:
	_phase = fmod(_phase + delta * 0.035, 1.0)
	queue_redraw()


func _draw() -> void:
	var viewport_size := size
	if viewport_size.x <= 1.0 or viewport_size.y <= 1.0:
		return

	# A quiet vertical gradient keeps text readable without requiring an image asset.
	var strips := 28
	for strip in range(strips):
		var t := float(strip) / float(strips - 1)
		var color := Color(0.012, 0.021, 0.038, 1.0).lerp(
			Color(0.003, 0.008, 0.017, 1.0), t)
		var y := viewport_size.y * float(strip) / float(strips)
		draw_rect(Rect2(0.0, y, viewport_size.x, viewport_size.y / strips + 1.0), color)

	# Deterministic star points: stable between launches and cheap enough for menus.
	for index in range(72):
		var hash_x := fmod(float(index * 47 + index * index * 13), 997.0) / 997.0
		var hash_y := fmod(float(index * 89 + index * index * 7), 991.0) / 991.0
		var pulse := 0.55 + 0.25 * sin(_phase * TAU + float(index) * 0.73)
		var point := Vector2(hash_x * viewport_size.x, hash_y * viewport_size.y * 0.78)
		draw_circle(point, 0.7 + float(index % 3) * 0.35, Color(0.55, 0.78, 0.9, pulse))

	# The ring is deliberately cropped; it reads as something much larger than the screen.
	var ring_center := Vector2(viewport_size.x * 0.76, viewport_size.y * 1.18)
	var ring_radius := maxf(viewport_size.x, viewport_size.y) * 0.64
	draw_arc(ring_center, ring_radius + 15.0, PI * 1.08, PI * 1.93, 180,
		Color(0.1, 0.95, 0.9, 0.035), 44.0, true)
	draw_arc(ring_center, ring_radius, PI * 1.08, PI * 1.93, 180,
		Color(0.31, 0.92, 0.91, 0.22), 2.0, true)
	draw_arc(ring_center, ring_radius - 7.0, PI * 1.08, PI * 1.93, 180,
		Color(0.52, 0.97, 0.9, 0.07), 1.0, true)

	# Sparse technical grid marks tie the menu to the in-game HUD language.
	var horizon_y := viewport_size.y * 0.76
	for index in range(7):
		var x := viewport_size.x * (0.08 + float(index) * 0.145)
		draw_line(Vector2(x, horizon_y), Vector2(viewport_size.x * 0.52, viewport_size.y),
			Color(0.28, 0.75, 0.78, 0.025), 1.0)
	draw_line(Vector2(0.0, horizon_y), Vector2(viewport_size.x, horizon_y),
		Color(0.28, 0.75, 0.78, 0.06), 1.0)
