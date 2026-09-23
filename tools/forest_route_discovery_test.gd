extends SceneTree
func _initialize() -> void:
	call_deferred("run")
func run() -> void:
	var result: Dictionary = load("res://scripts/Tools/ForestRouteDiscovery.cs").new().call("Run")
	if not bool(result.get("route_found", false)):
		push_error("FOREST_ROUTE_DISCOVERY=NO_ROUTE " + str(result))
		quit(1)
		return
	print("FOREST_ROUTE_DISCOVERY=PASS ", result)
	quit(0)
