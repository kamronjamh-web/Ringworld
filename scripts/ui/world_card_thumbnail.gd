extends Control
# World card art: the captured thumbnail covered to the control's aspect, or a
# seed-tinted ring on a starfield when no capture exists (or for empty slots).

const FALLBACK_STAR_COUNT := 40

var _seed := 0
var _texture: ImageTexture
var _plus_glyph := false


func _init() -> void:
	clip_contents = true
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	resized.connect(queue_redraw)


func set_world(slot_seed: int, thumbnail_path: String) -> void:
	_seed = slot_seed
	_plus_glyph = false
	_texture = null
	if not thumbnail_path.is_empty():
		# Load from disk, not ResourceLoader, so a re-captured file is never served from cache.
		var image := Image.load_from_file(ProjectSettings.globalize_path(thumbnail_path))
		if image != null and not image.is_empty():
			_texture = ImageTexture.create_from_image(image)
	queue_redraw()


# Empty slot: the starfield with a "+" in the middle.
func set_empty() -> void:
	_seed = 0
	_texture = null
	_plus_glyph = true
	queue_redraw()


func _draw() -> void:
	if _texture != null:
		var texture_size := Vector2(_texture.get_size())
		var cover := maxf(size.x / texture_size.x, size.y / texture_size.y)
		var source_size := size / cover
		var source := Rect2((texture_size - source_size) * 0.5, source_size)
		draw_texture_rect_region(_texture, Rect2(Vector2.ZERO, size), source)
		return
	draw_rect(Rect2(Vector2.ZERO, size), Color(0.012, 0.025, 0.04, 1.0))
	var rng := RandomNumberGenerator.new()
	rng.seed = _seed
	for i in FALLBACK_STAR_COUNT:
		var star := Vector2(rng.randf() * size.x, rng.randf() * size.y)
		draw_circle(star, rng.randf_range(0.6, 1.3), Color(1, 1, 1, rng.randf_range(0.25, 0.8)))
	if _plus_glyph:
		var center := size * 0.5
		var arm := minf(size.x, size.y) * 0.16
		var color := Color(0.48, 0.63, 0.69, 0.8)
		draw_line(center - Vector2(arm, 0), center + Vector2(arm, 0), color, 3.0, true)
		draw_line(center - Vector2(0, arm), center + Vector2(0, arm), color, 3.0, true)
		return
	var tint := Color.from_hsv(fposmod(float(_seed) * 0.618034, 1.0), 0.45, 0.95)
	var radius := size.x * 0.42
	draw_set_transform(Vector2(size.x * 0.5, size.y * 0.72), 0.0, Vector2(1.0, 0.3))
	draw_arc(Vector2.ZERO, radius, PI * 1.04, TAU * 0.96, 96, tint, 5.0, true)
	draw_arc(Vector2.ZERO, radius * 0.9, PI * 1.04, TAU * 0.96, 96, Color(tint, 0.4), 2.0, true)
	draw_set_transform(Vector2.ZERO, 0.0, Vector2.ONE)
