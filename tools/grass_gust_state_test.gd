extends SceneTree

# Runnable CPU contract driver: Godot --headless --path . --script res://tools/grass_gust_state_test.gd
const GrassGustStateTest := preload("res://tools/GrassGustStateTests.cs")

func _initialize() -> void:
	var test := GrassGustStateTest.new()
	quit(0 if test.Run() else 1)
