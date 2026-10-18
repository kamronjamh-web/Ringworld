extends SceneTree

const TEST := preload("res://tools/GrassCullingFrustumTest.cs")

func _init() -> void:
	var result: Dictionary = TEST.new().Run()
	print("GRASS_CULLING_FRUSTUM=" + JSON.stringify(result))
	quit(0 if bool(result.get("pass", false)) else 1)
