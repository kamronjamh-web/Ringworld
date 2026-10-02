extends SceneTree

# Production scheduler regression: Godot --headless --path . --script res://tools/grass_modifier_streaming_test.gd
const DRIVER := preload("res://tools/GrassModifierStreamingTest.cs")

func _init() -> void:
	call_deferred("boot")

func boot() -> void:
	var driver := DRIVER.new()
	driver.connect("Completed", _on_completed.bind(driver))
	root.add_child(driver)

func _on_completed(passed: bool, driver: Node) -> void:
	driver.queue_free()
	await process_frame
	quit(0 if passed else 1)
