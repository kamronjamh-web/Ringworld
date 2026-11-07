extends SceneTree
# Pure catalog/format assertions: no model weights, process launch, or inference.
const TEST := preload("res://tools/PdaModelCatalogTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PDA_MODEL_CATALOG_WRAPPER=PASS")
	else:
		push_error("PDA_MODEL_CATALOG_WRAPPER=FAIL exit=%d" % code)
	quit(code)
