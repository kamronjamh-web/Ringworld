extends SceneTree

const BridgeTest := preload("res://tools/RingSurfaceLodBridgeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	quit(0 if BridgeTest.new().Run() else 1)
