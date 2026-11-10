extends SceneTree

## Landscape audit T0-2 relief spectrum (docs/landscape-audit-t0-spec.md WO T0-2): band RMS height, decade RMS, slope and relief per landscape class of the generation 1 world.
## RUN: Godot_console.exe --headless --path . --script res://tools/landscape_relief_audit.gd [-- --out=<dir>]
## Writes .godot/audit_baseline/common/t0/relief/<stamp>/relief_spectrum.json and .md. Prints LANDSCAPE_RELIEF=PASS, or LANDSCAPE_RELIEF=FAIL <reason> through push_error (exit 1).
const AUDIT := preload("res://tools/LandscapeReliefAudit.cs")

func _initialize() -> void:
	var watchdog := create_timer(280.0)
	watchdog.timeout.connect(func() -> void:
		push_error("LANDSCAPE_RELIEF=FAIL watchdog timeout")
		quit(1))
	var audit: RefCounted = AUDIT.new()
	if not bool(audit.call("Run")):
		quit(1)
		return
	print("LANDSCAPE_RELIEF=PASS")
	quit(0)
