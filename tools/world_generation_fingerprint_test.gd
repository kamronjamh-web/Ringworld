extends SceneTree

## Landscape audit T0-6: generation 1 fingerprint (docs/landscape-audit-t0-spec.md WO T0-6). Recomputes the row and compares it with tools/fixtures/world_generation_fingerprint_golden.json.
## RUN: Godot_console.exe --headless --path . --script res://tools/world_generation_fingerprint_test.gd [-- --capture | --print | --golden=<path>]
## Prints WORLD_GENERATION_FINGERPRINT=PASS, or WORLD_GENERATION_FINGERPRINT=FAIL <reason> through push_error (exit 1).
const TEST := preload("res://tools/WorldGenerationFingerprintTest.cs")

func _initialize() -> void:
	var watchdog := create_timer(140.0)
	watchdog.timeout.connect(func() -> void:
		push_error("WORLD_GENERATION_FINGERPRINT=FAIL watchdog timeout")
		quit(1))
	var test: RefCounted = TEST.new()
	if not bool(test.call("Run")):
		quit(1)
		return
	print("WORLD_GENERATION_FINGERPRINT=PASS")
	quit(0)
