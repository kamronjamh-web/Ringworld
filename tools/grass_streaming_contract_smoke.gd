extends SceneTree

const DRIVER := preload("res://tools/GrassStreamingTestDriver.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var driver := DRIVER.new()
	driver.connect("Completed", _on_completed.bind(driver))
	root.add_child(driver)

func _on_completed(ok: bool, driver: Node) -> void:
	driver.queue_free()
	await process_frame
	quit(0 if ok else 1)
