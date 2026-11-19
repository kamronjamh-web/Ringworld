extends SceneTree
# T6-4b: builds resources/trees/prepared/understory/ (foliage 1024 and bark 512 texture arrays, albedo and normal, plus understory_textures.manifest.json).
# Run: tools/run_tests.ps1 -Tests prepare_understory_textures -Label t6-4b
const TOOL := preload("res://tools/PrepareUnderstoryTextures.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var tool = TOOL.new()
	var code: int = tool.Run()
	tool = null
	quit(code)
