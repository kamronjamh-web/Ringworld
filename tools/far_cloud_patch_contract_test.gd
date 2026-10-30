extends SceneTree
const TEST := preload("res://tools/FarCloudPatchContractTest.cs")
func _init() -> void:
	var result: Dictionary = TEST.new().Run()
	var ok := bool(result.get("pass", false))
	print("FAR_CLOUD_PATCH_CONTRACT=%s %s" % ["PASS" if ok else "FAIL", JSON.stringify(result)])
	quit(0 if ok else 1)
