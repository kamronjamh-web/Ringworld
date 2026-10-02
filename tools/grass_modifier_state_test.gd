extends SceneTree

# Runnable pure CPU modifier-data contract: Godot --headless --path . --script res://tools/grass_modifier_state_test.gd
const GrassModifierTests := preload("res://tools/GrassModifierTests.cs")

func _initialize() -> void:
	var test := GrassModifierTests.new()
	quit(0 if test.Run() else 1)
