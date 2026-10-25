extends SceneTree
# Run: Godot --headless --path . --script res://tools/quality_preset_ownership_test.gd
# Proves the LOD profile owns terrain worker counts for menu presets 0..4 (docs/quality-presets-plan.md).
const TEST := preload("res://tools/QualityPresetOwnershipTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(120.0).timeout.connect(func(): push_error("QP_OWNERSHIP=FAIL timeout"); quit(1))
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("QP_OWNERSHIP_WRAPPER=PASS")
	else:
		push_error("QP_OWNERSHIP_WRAPPER=FAIL exit=%d" % code)
	quit(code)
