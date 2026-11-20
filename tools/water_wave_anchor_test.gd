extends SceneTree
const SMOKE := preload("res://scripts/Tools/WaterWaveAnchorSmoke.cs")
func _initialize() -> void:
    var result: Dictionary = SMOKE.new().Run()
    if not bool(result.get("pass", false)):
        push_error("WATER_WAVE_ANCHOR_TEST=FAIL " + str(result.get("detail", "unknown")))
        quit(1)
        return
    print("WATER_WAVE_ANCHOR_TEST=PASS " + str(result.get("detail", "")))
    quit(0)
