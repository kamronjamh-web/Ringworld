extends SceneTree

## Landscape relief targets (docs/work-orders/terrain-vegetation/T7.md WO T7-2): the relief audit with CoastalBelt measured on land only, checked against a targets file.
## RUN: Godot_console.exe --headless --path . --script res://tools/landscape_relief_targets.gd [-- [--targets=<json>] [--baseline=<relief_spectrum.json>] [--out=<dir>] [--generation=N] [--review-features=a,b] [--seed=N]]
## Default targets: res://tools/fixtures/landscape_relief_targets_landforms.json. Default out: .godot/audit_baseline/common/t7/relief/<stamp>/.
## Prints one LANDSCAPE_RELIEF_TARGET line per row, then LANDSCAPE_RELIEF_TARGETS=PASS, or LANDSCAPE_RELIEF_TARGETS=FAIL <row ids> through push_error (exit 1).
const AUDIT := preload("res://tools/LandscapeReliefAudit.cs")
const DEFAULT_TARGETS := "res://tools/fixtures/landscape_relief_targets_landforms.json"

func _arg(prefix: String) -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(prefix):
			return arg.substr(prefix.length())
	return ""

func _initialize() -> void:
	var watchdog := create_timer(290.0)
	watchdog.timeout.connect(func() -> void:
		push_error("LANDSCAPE_RELIEF_TARGETS=FAIL watchdog timeout")
		quit(1))
	var targets := _arg("--targets=")
	if targets.is_empty():
		targets = ProjectSettings.globalize_path(DEFAULT_TARGETS)
	var out_dir := _arg("--out=")
	if out_dir.is_empty():
		var t := Time.get_datetime_dict_from_system()
		out_dir = ProjectSettings.globalize_path("res://.godot/audit_baseline/common/t7/relief/%04d%02d%02d_%02d%02d%02d" % [t.year, t.month, t.day, t.hour, t.minute, t.second])
	var audit: RefCounted = AUDIT.new()
	if not bool(audit.call("RunWith", true, targets, _arg("--baseline="), out_dir)):
		push_error("LANDSCAPE_RELIEF_TARGETS=FAIL audit failed")
		quit(1)
		return
	var failed := String(audit.call("TargetFailuresCsv"))
	if not failed.is_empty():
		push_error("LANDSCAPE_RELIEF_TARGETS=FAIL " + failed)
		quit(1)
		return
	print("LANDSCAPE_RELIEF_TARGETS=PASS")
	quit(0)
