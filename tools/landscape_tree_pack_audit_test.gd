extends SceneTree
# T0-5 tree pack audit (docs/landscape-audit-t0-spec.md section 3, WO T0-5). Headless; writes
# .godot/audit_baseline/common/t0/trees/<stamp>/tree_pack_audit.json and .md.
# Run: tools/run_tests.ps1 -Tests landscape_tree_pack_audit_test -Label landscape-t0-trees -Cache none
const DRIVER := preload("res://tools/LandscapeTreePackAuditTest.cs")

func _initialize() -> void:
	create_timer(140.0).timeout.connect(func():
		push_error("LANDSCAPE_TREE_PACK=FAIL watchdog timeout")
		quit(1))
	var passed: bool = DRIVER.new().Run()
	quit(0 if passed else 1)
