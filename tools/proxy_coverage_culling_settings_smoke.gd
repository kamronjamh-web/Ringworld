extends SceneTree

const SMOKE := preload("res://scripts/Tools/ProxyCoverageCullingSettingsSmoke.cs")
const PATH := "res://.godot/proxy-coverage-culling/settings-smoke.tres"

func _init() -> void:
	var result: Dictionary = SMOKE.new().call("Run", PATH)
	var ok := bool(result.get("pass", false))
	print("PROXY_COVERAGE_CULLING_SETTINGS_SMOKE=%s %s" % ["PASS" if ok else "FAIL", JSON.stringify(result)])
	quit(0 if ok else 1)
