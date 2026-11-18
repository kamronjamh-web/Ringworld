extends SceneTree

# Runs scenes/UndergroundEnvironmentRuntimeSmoke.tscn, which hosts tools/underground_environment_lighting_smoke_test.gd
# (UNDERGROUND_LIGHTING_PROPERTY_SMOKE_TEST) and quits when the scene's own smoke has finished.
func _init() -> void:
	var scene := load("res://scenes/UndergroundEnvironmentRuntimeSmoke.tscn") as PackedScene
	if scene == null:
		push_error("UNDERGROUND_LIGHTING_PROPERTY_SMOKE_TEST=FAIL: missing smoke scene")
		quit(1)
		return
	get_root().add_child(scene.instantiate())
