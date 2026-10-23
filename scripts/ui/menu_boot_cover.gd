extends Control
## Start-menu boot cover: sits where the flyover backdrop did (below the readability gradients, above the 3D world).
## Shows the last session's snapshot with a slow Ken Burns drift that converges on the live view, or a dark starfield when no snapshot
## exists. The menu then calls settle() and crossfade() once the live world is quiet behind it, and retire() to free the cover.

const STARFIELD_SHADER := preload("res://scripts/ui/menu_starfield.gdshader")
## The start offset is (-0.6, +0.25) * drift_amount of the screen.
const DRIFT_OFFSET := Vector2(-0.6, 0.25)
const DEFAULT_WARM_SECONDS := 25.0
## The drift decays exponentially with tau = warm time / TAU_DIVISOR, so it is nearly at rest when the world is ready.
const TAU_DIVISOR := 3.0
const SETTLE_SECONDS := 0.8

## Start zoom is 1 + drift_amount (a fraction of the screen); set from the menu's Live world drawer before the snapshot is shown.
var drift_amount := 0.08
## "snapshot", "starfield", or "" before one of the show_* calls.
var mode := ""
var is_retired := false
var snapshot_sidecar: Dictionary = {}
var frozen := false:
	set(value):
		frozen = value
		if _starfield != null:
			(_starfield.material as ShaderMaterial).set_shader_parameter("frozen", value)

var _picture: TextureRect
var _starfield: ColorRect
var _start_msec := 0
var _tau := DEFAULT_WARM_SECONDS / TAU_DIVISOR
var _settle_from := 0.0
var _settle_progress := 0.0
var _settling := false
var _end_scale := 1.0
var _tween: Tween
var _outgoing: Array[Node] = []


func _init() -> void:
	name = "BootCover"
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	clip_contents = true
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)


func show_snapshot(texture: Texture2D, sidecar: Dictionary) -> void:
	_clear_content()
	_make_picture(texture, sidecar)


## Dissolves from whatever the cover shows now (a snapshot or the starfield) to another picture; the drift restarts on the new one.
## Used when the last session's world cannot warm-load and the showcase picture takes over. Awaitable.
func fade_to_snapshot(texture: Texture2D, sidecar: Dictionary, seconds: float) -> void:
	if is_retired:
		return
	var outgoing: Array[Node] = []
	for node: Node in [_picture, _starfield]:
		if node != null:
			outgoing.append(node)
	_picture = null
	_starfield = null
	_outgoing.append_array(outgoing)
	_make_picture(texture, sidecar)
	_picture.modulate.a = 0.0
	_tween = create_tween()
	_tween.tween_property(_picture, "modulate:a", 1.0, maxf(0.01, seconds))
	await _tween.finished
	if is_retired:
		return
	for node in outgoing:
		if is_instance_valid(node):
			node.queue_free()
	_outgoing.clear()


func _make_picture(texture: Texture2D, sidecar: Dictionary) -> void:
	mode = "snapshot"
	snapshot_sidecar = sidecar.duplicate(true)
	var warm_ms := float(sidecar.get("last_warm_ms", 0.0))
	var warm_seconds := warm_ms / 1000.0 if warm_ms > 1000.0 else DEFAULT_WARM_SECONDS
	_tau = maxf(1.0, warm_seconds / TAU_DIVISOR)
	_picture = TextureRect.new()
	_picture.name = "SnapshotPicture"
	_picture.texture = texture
	_picture.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_picture.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_COVERED
	_picture.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_picture)
	_start_msec = Time.get_ticks_msec()
	_settling = false
	_settle_progress = 0.0
	_end_scale = 1.0
	_apply_transform()


func show_starfield() -> void:
	_clear_content()
	mode = "starfield"
	snapshot_sidecar = {}
	_starfield = ColorRect.new()
	_starfield.name = "Starfield"
	_starfield.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_starfield.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var material := ShaderMaterial.new()
	material.shader = STARFIELD_SHADER
	material.set_shader_parameter("frozen", frozen)
	_starfield.material = material
	add_child(_starfield)


## Eases the remaining drift to rest (and matches the live field of view) over SETTLE_SECONDS. Returns at once for a starfield.
func settle() -> void:
	if mode != "snapshot" or is_retired or _settling:
		return
	_settling = true
	_settle_from = _decay()
	_end_scale = _fov_scale()
	_tween = create_tween().set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	_tween.tween_property(self, "_settle_progress", 1.0, SETTLE_SECONDS)
	await _tween.finished


## Fades the cover out, revealing the live world underneath. Awaitable.
func crossfade(seconds: float) -> void:
	if is_retired:
		return
	_tween = create_tween()
	_tween.tween_property(self, "modulate:a", 0.0, maxf(0.01, seconds))
	await _tween.finished


## Stops any running settle or crossfade and makes the cover opaque again (the world behind it is going away).
func halt() -> void:
	if _tween != null and _tween.is_valid():
		_tween.kill()
	if not is_retired:
		modulate.a = 1.0


func retire() -> void:
	if is_retired:
		return
	is_retired = true
	if _tween != null and _tween.is_valid():
		_tween.kill()
	_clear_content()
	hide()
	set_process(false)


func _process(_delta: float) -> void:
	if mode == "snapshot":
		_apply_transform()


func _clear_content() -> void:
	if _picture != null:
		_picture.queue_free()
		_picture = null
	if _starfield != null:
		_starfield.queue_free()
		_starfield = null
	for node in _outgoing:
		if is_instance_valid(node):
			node.queue_free()
	_outgoing.clear()


func _decay() -> float:
	if frozen:
		return 1.0
	var seconds := float(Time.get_ticks_msec() - _start_msec) / 1000.0
	return exp(-seconds / _tau)


## Remaining drift fraction: exponential decay with time, scaled to zero by the settle tween.
func _drift_factor() -> float:
	if _settling:
		return _settle_from * (1.0 - _settle_progress)
	return _decay()


## Live camera zoom needed so the snapshot matches the live view when the field of view differs from the sidecar's.
func _fov_scale() -> float:
	var snapshot_fov := float((snapshot_sidecar.get("pose", {}) as Dictionary).get("fov_deg", 0.0))
	var camera := get_viewport().get_camera_3d() if is_inside_tree() else null
	if camera == null or snapshot_fov <= 1.0 or camera.fov <= 1.0:
		return 1.0
	var ratio := tan(deg_to_rad(snapshot_fov) * 0.5) / tan(deg_to_rad(camera.fov) * 0.5)
	return ratio if is_finite(ratio) and ratio > 0.1 else 1.0


func _apply_transform() -> void:
	if _picture == null:
		return
	var factor := _drift_factor()
	var zoom := 1.0 + drift_amount * factor
	if _settling:
		zoom *= lerpf(1.0, _end_scale, _settle_progress)
	var offset := DRIFT_OFFSET * drift_amount * factor * size
	# Never slide the picture far enough to expose the cover's edge.
	var limit := maxf(0.0, zoom - 1.0) * 0.5 * size
	offset = Vector2(clampf(offset.x, -limit.x, limit.x), clampf(offset.y, -limit.y, limit.y))
	_picture.size = size
	_picture.pivot_offset = size * 0.5
	_picture.scale = Vector2(zoom, zoom)
	_picture.position = offset
