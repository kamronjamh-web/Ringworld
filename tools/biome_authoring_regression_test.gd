extends SceneTree

const DRIVER := preload("res://tools/BiomeAuthoringRegressionTest.cs")
const WATCHDOG_SECONDS := 30.0

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var watchdog := Timer.new()
	watchdog.one_shot = true
	root.add_child(watchdog)
	watchdog.timeout.connect(func() -> void: push_error("BIOME_AUTHORING_REGRESSION=FAIL watchdog"); quit(1))
	watchdog.start(WATCHDOG_SECONDS)
	var passed: bool = DRIVER.new().Run()
	quit(0 if passed else 1)
