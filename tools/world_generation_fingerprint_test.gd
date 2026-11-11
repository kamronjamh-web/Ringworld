extends SceneTree

## Checks all frozen fingerprint rows and v9 generation 1 content parity against tools/fixtures/world_generation_fingerprint_golden.json.
## RUN: Godot_console.exe --headless --path . --script res://tools/world_generation_fingerprint_test.gd
## Optional args after --: --golden=<path>, --print [--generation=N] [--review-features=id[,id]], or --capture --generation=N.
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
