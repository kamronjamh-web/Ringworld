extends SceneTree
const TEST := preload("res://tools/FarCloudPatchContractTest.cs")
func _init() -> void:
	var result: Dictionary = TEST.new().Run()
	print("FAR_CLOUD_PATCH_CONTRACT=" + JSON.stringify(result))
	quit(0 if bool(result.get("pass", false)) else 1)
