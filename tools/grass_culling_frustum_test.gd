extends SceneTree

const TEST := preload("res://tools/GrassCullingFrustumTest.cs")

func _init() -> void:
	var result: Dictionary = TEST.new().Run()
	var ok := bool(result.get("pass", false))
	print("GRASS_CULLING_FRUSTUM=%s %s" % ["PASS" if ok else "FAIL", JSON.stringify(result)])
	quit(0 if ok else 1)
