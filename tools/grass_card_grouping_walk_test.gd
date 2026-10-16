extends SceneTree
# Windowed walk test of Cards adaptive grouping (grass-cards S6 plan, C.5-2 walk + oscillation). Run without --headless:
#   godot --path . --resolution 960x540 --script res://tools/grass_card_grouping_walk_test.gd -- [level4x4=0] [speed=0.1] [gate_tier_handoff=0]
const DRIVER := preload("res://tools/GrassCardGroupingWalkTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var driver := DRIVER.new()
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("level4x4="):
			driver.set("Exercise4x4", arg.substr(9) != "0")
		elif arg.begins_with("speed="):
			driver.set("MetersPerFrame", float(arg.substr(6)))
		elif arg.begins_with("gate_tier_handoff="):
			driver.set("GateTierHandoff", arg.substr(18) != "0")
	driver.connect("Completed", _on_completed.bind(driver))
	root.add_child(driver)

func _on_completed(ok: bool, driver: Node) -> void:
	driver.queue_free()
	await process_frame
	quit(0 if ok else 1)
