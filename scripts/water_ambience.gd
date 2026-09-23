extends Node

## Bounded world-water mix.  This owns four looping layers total, regardless of
## water-sector count.  The explorer publishes inexpensive, cached hydrology
## probes so this node never has to inspect chunk meshes or spawn emitters.
@export var player_path: NodePath = NodePath("../Explorer")
@export var shore_path: NodePath = NodePath("Shore")
@export var open_water_path: NodePath = NodePath("OpenWater")
@export var river_path: NodePath = NodePath("River")
@export var underwater_path: NodePath = NodePath("Underwater")
@export var fade_seconds: float = 2.0
@export var shore_volume_db: float = -6.0
@export var open_water_volume_db: float = -10.0
@export var river_volume_db: float = -7.0
@export var underwater_volume_db: float = -12.0

var _player: Node
var _layers: Dictionary = {}
var _gains: Dictionary = {}


func _ready() -> void:
	_player = get_node_or_null(player_path)
	_layers = {
		"shore": get_node_or_null(shore_path),
		"open": get_node_or_null(open_water_path),
		"river": get_node_or_null(river_path),
		"underwater": get_node_or_null(underwater_path),
	}
	for key in _layers:
		_gains[key] = 0.0
		var layer: AudioStreamPlayer = _layers[key]
		if layer == null:
			continue
		layer.volume_db = -80.0
		_configure_looping(layer)
		if layer.stream != null:
			layer.play()


func _configure_looping(layer: AudioStreamPlayer) -> void:
	if layer.stream is AudioStreamOggVorbis:
		var loop_stream := layer.stream.duplicate() as AudioStreamOggVorbis
		if loop_stream != null:
			loop_stream.loop = true
			layer.stream = loop_stream


func _process(delta: float) -> void:
	if _player == null:
		_player = get_node_or_null(player_path)
	if _player == null:
		_apply_targets({})
		return

	var underwater := bool(_player.get_meta("ring_camera_underwater", false))
	var depth := maxf(0.0, float(_player.get_meta("ring_water_depth_meters", 0.0)))
	var kind := int(_player.get_meta("ring_water_ambience_kind", 0))
	var proximity := clampf(float(_player.get_meta("ring_water_ambience_proximity", 0.0)), 0.0, 1.0)
	var shore := clampf(float(_player.get_meta("ring_water_ambience_shore_proximity", 0.0)), 0.0, 1.0)
	var targets := {}
	if underwater:
		# The outside ambience already mutes itself underwater.  A depth ramp
		# avoids a sharp mix change at the surface.
		targets["underwater"] = lerpf(0.36, 1.0, clampf(depth / 4.0, 0.0, 1.0))
	else:
		targets["shore"] = shore * proximity
		if kind == 1: # SurfaceWaterKind.Ocean
			targets["open"] = proximity * (1.0 - shore * 0.55)
		elif kind == 3: # SurfaceWaterKind.River
			targets["river"] = proximity
		elif kind == 2: # Lake: quiet shore and a restrained open-water bed.
			targets["shore"] *= 0.45
			targets["open"] = proximity * 0.22
	_apply_targets(targets, delta)


func _apply_targets(targets: Dictionary, delta: float = 1.0) -> void:
	for key in _layers:
		var target := clampf(float(targets.get(key, 0.0)), 0.0, 1.0)
		var gain := move_toward(float(_gains[key]), target, delta / maxf(fade_seconds, 0.01))
		_gains[key] = gain
		var layer: AudioStreamPlayer = _layers[key]
		if layer != null:
			layer.volume_db = -80.0 if gain <= 0.0001 else linear_to_db(gain) + _layer_trim_db(key)


func _layer_trim_db(key: String) -> float:
	match key:
		"shore":
			return shore_volume_db
		"open":
			return open_water_volume_db
		"river":
			return river_volume_db
		"underwater":
			return underwater_volume_db
	return 0.0
