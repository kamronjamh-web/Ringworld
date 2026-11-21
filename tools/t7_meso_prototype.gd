extends SceneTree

## T7-1P prototype wrapper (headless). `-- --label=<name>` plus any --param=value override; see tools/T7MesoPrototype.cs.
const TEST := preload("res://tools/T7MesoPrototype.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code := int(runner.Run())
	runner = null
	if code == 0:
		print("T7_MESO_PROTOTYPE=PASS")
	else:
		push_error("T7_MESO_PROTOTYPE=FAIL")
	quit(code)
