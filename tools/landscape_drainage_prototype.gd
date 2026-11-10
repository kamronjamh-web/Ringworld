extends SceneTree
## T0-3 drainage-bake prototype and river map (docs/landscape-audit-t0-spec.md WO T0-3). Headless; writes drainage_<cell>.json/.md and river_map_<cell>[_crop].png to
## .godot/audit_baseline/common/t0/drainage/<stamp>/.
## RUN: tools/run_tests.ps1 -Tests landscape_drainage_prototype -Label landscape-t0-drainage -Cache none
## Manual: Godot_console.exe --headless --path . --script res://tools/landscape_drainage_prototype.gd [-- --strip-start=<m> --strip-km=2000 --cells=1000,500 --breach=5,15,40 --thresholds=25,50,100 --time-limit=180]
## Prints LANDSCAPE_DRAINAGE=PASS, or LANDSCAPE_DRAINAGE=FAIL <reason> through push_error (exit 1).
const DRIVER := preload("res://tools/LandscapeDrainagePrototype.cs")

func _initialize() -> void:
	var watchdog := create_timer(880.0)
	watchdog.timeout.connect(func() -> void:
		push_error("LANDSCAPE_DRAINAGE=FAIL watchdog timeout")
		quit(1))
	var driver: RefCounted = DRIVER.new()
	if not bool(driver.call("Run")):
		quit(1)
		return
	print("LANDSCAPE_DRAINAGE=PASS")
	quit(0)
