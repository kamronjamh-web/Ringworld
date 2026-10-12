extends SceneTree
const SMOKE := preload("res://scripts/Tools/WaterSharedWindSmoke.cs")
func _initialize() -> void:
    var result: Dictionary = SMOKE.new().Run()
    if not bool(result.get("pass", false)):
        push_error("WATER_SHARED_WIND_TEST_FAIL " + str(result.get("detail", "unknown")))
        quit(1)
        return
    print("WATER_SHARED_WIND_TEST_PASS " + str(result.get("detail", "")))
    quit(0)
