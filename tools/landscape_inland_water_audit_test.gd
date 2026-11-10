# Landscape audit T0-4: inland water (Lake, River) in gen-17 and gen-16, proxy sector water output, shader profiles.
# Run: Godot --headless --path . --script res://tools/landscape_inland_water_audit_test.gd
# Prints LANDSCAPE_INLAND_WATER=PASS; failures go through push_error as LANDSCAPE_INLAND_WATER=FAIL <reason>.
extends SceneTree

const LandscapeInlandWaterAuditTest = preload("res://tools/LandscapeInlandWaterAuditTest.cs")

func _init() -> void:
	var watchdog := create_timer(170.0)
	watchdog.timeout.connect(func() -> void:
		push_error("LANDSCAPE_INLAND_WATER=FAIL watchdog timeout")
		quit(1))
	_run.call_deferred()

func _run() -> void:
	var test := LandscapeInlandWaterAuditTest.new()
	quit(0 if test.Run() else 1)
