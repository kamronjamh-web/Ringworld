extends Node

const WaterAmbience = preload("res://scripts/water_ambience.gd")
var _failures := 0


func _ready() -> void:
	var above_bus := AudioServer.get_bus_index(&"WaterAbove")
	var underwater_bus := AudioServer.get_bus_index(&"WaterUnderwater")
	_expect(above_bus >= 0, "WaterAbove audio bus must exist")
	_expect(underwater_bus >= 0, "WaterUnderwater audio bus must exist")
	_expect(
		underwater_bus >= 0 and AudioServer.get_bus_effect_count(underwater_bus) > 0,
		"underwater audio bus must provide its low-pass effect")
	var explorer := Node.new()
	explorer.name = "Explorer"
	add_child(explorer)
	var ambience := WaterAmbience.new()
	ambience.name = "WaterAmbience"
	for layer_name in [&"Shore", &"OpenWater", &"River", &"Underwater"]:
		var layer := AudioStreamPlayer.new()
		layer.name = layer_name
		ambience.add_child(layer)
	add_child(ambience)
	await get_tree().process_frame

	explorer.set_meta("ring_camera_underwater", false)
	explorer.set_meta("ring_water_ambience_kind", 1)
	explorer.set_meta("ring_water_ambience_proximity", 1.0)
	explorer.set_meta("ring_water_ambience_shore_proximity", 0.0)
	ambience._process(4.0)
	_expect(ambience.get_node("OpenWater").volume_db > -20.0, "ocean must own the open-water layer")
	_expect(ambience.get_node("Shore").volume_db <= -79.0, "open ocean must not force shore audio")

	explorer.set_meta("ring_water_ambience_kind", 3)
	ambience._process(4.0)
	_expect(ambience.get_node("River").volume_db > -20.0, "river kind must select the river layer")

	explorer.set_meta("ring_camera_underwater", true)
	explorer.set_meta("ring_water_depth_meters", 5.0)
	ambience._process(4.0)
	_expect(ambience.get_node("Underwater").volume_db > -20.0, "underwater state must own the underwater layer")
	_expect(ambience.get_node("OpenWater").volume_db <= -79.0, "underwater state must mute open-water audio")
	if _failures == 0:
		print("WATER_AMBIENCE_REGRESSION_SMOKE=PASS")
	get_tree().quit(0 if _failures == 0 else 1)


func _expect(condition: bool, message: String) -> void:
	if condition:
		return
	push_error("WATER_AMBIENCE_REGRESSION_SMOKE=FAIL: " + message)
	_failures += 1
