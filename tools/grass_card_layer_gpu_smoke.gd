extends SceneTree
# Windowed smoke of the production Cards layer. Run without --headless:
#   godot --path . --resolution 960x540 --script res://tools/grass_card_layer_gpu_smoke.gd -- out=<dir>
const DRIVER := preload("res://tools/GrassCardLayerGpuSmoke.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var driver := DRIVER.new()
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("out="):
			driver.set("OutputDirectory", arg.substr(4))
	driver.connect("Completed", _on_completed.bind(driver))
	root.add_child(driver)

func _on_completed(ok: bool, driver: Node) -> void:
	driver.queue_free()
	await process_frame
	quit(0 if ok else 1)
