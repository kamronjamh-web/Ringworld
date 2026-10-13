extends AudioStreamPlayer

## Exterior ambience belongs only to the gameplay scene.  Its level follows the
## player's terrain-relative canonical altitude, so it stays stable as the ring
## curves beneath the player.
@export var player_path: NodePath = NodePath("../Explorer")
@export var full_volume_through_height_m: float = 1200.0
@export var silent_at_height_m: float = 2000.0
@export var target_volume_db: float = 6.0
@export var fade_seconds: float = 2.5

var _player: Node = null
var _current_gain: float = 0.0


func _ready() -> void:
	_player = get_node_or_null(player_path)
	volume_db = -80.0

	if stream != null:
		_configure_looping()
		play()


func _configure_looping() -> void:
	if stream is AudioStreamOggVorbis:
		var loop_stream := stream.duplicate() as AudioStreamOggVorbis
		if loop_stream != null:
			loop_stream.loop = true
			stream = loop_stream
	elif stream != null and stream.has_method("set_loop"):
		stream.call("set_loop", true)


func _process(delta: float) -> void:
	if _player == null:
		_player = get_node_or_null(player_path)
		if _player == null:
			_fade_to_silence(delta)
			return

	# Water owns its own mix.  The explorer publishes this metadata when its
	# camera enters a water column, which keeps exterior wind out of the mix.
	if not _player.has_method("GetCanonicalAtmosphereHeightMeters"):
		_fade_to_silence(delta)
		return

	var underwater := bool(_player.get_meta("ring_camera_underwater", false))
	var altitude_m := float(_player.call("GetCanonicalAtmosphereHeightMeters"))
	var target_gain := 0.0 if underwater else _altitude_gain(altitude_m)
	# Keep some wind around shorelines, but leave headroom for the water layers.
	# This metadata is cached by the explorer, so the mix adds no terrain work.
	var water_proximity := clampf(
		float(_player.get_meta("ring_water_ambience_proximity", 0.0)),
		0.0,
		1.0)
	target_gain *= lerpf(1.0, 0.55, water_proximity)
	# Weather ambience publishes a multiplicative duck only; water ownership remains unchanged.
	var weather_duck := float(_player.get_meta("ring_weather_ambience_duck", 1.0))
	target_gain *= clampf(weather_duck, 0.0, 1.0) if is_finite(weather_duck) else 1.0
	# Underground mode publishes the enclosure blend; the stream keeps playing so resume never restarts it.
	var enclosure := float(_player.get_meta("ring_underground_ambience_blend", 0.0))
	target_gain *= 1.0 - clampf(enclosure, 0.0, 1.0) if is_finite(enclosure) else 1.0
	_fade_to_gain(target_gain, delta)


func _altitude_gain(altitude_m: float) -> float:
	if altitude_m <= full_volume_through_height_m:
		return 1.0

	if altitude_m >= silent_at_height_m:
		return 0.0

	var fade := smoothstep(full_volume_through_height_m, silent_at_height_m, altitude_m)
	return 1.0 - fade


func _fade_to_silence(delta: float) -> void:
	_fade_to_gain(0.0, delta)


func _fade_to_gain(target_gain: float, delta: float) -> void:
	_current_gain = move_toward(
		_current_gain,
		clampf(target_gain, 0.0, 1.0),
		delta / maxf(fade_seconds, 0.01))
	volume_db = -80.0 if _current_gain <= 0.0001 else linear_to_db(_current_gain) + target_volume_db
