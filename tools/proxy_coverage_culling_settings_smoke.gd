extends SceneTree

const SMOKE := preload("res://scripts/Tools/ProxyCoverageCullingSettingsSmoke.cs")
const PATH := "res://.godot/proxy-coverage-culling/settings-smoke.tres"

func _init() -> void:
	var result: Dictionary = SMOKE.new().call("Run", PATH)
	print("PROXY_COVERAGE_CULLING_SETTINGS_SMOKE=" + JSON.stringify(result))
	quit(0 if bool(result.get("pass", false)) else 1)
