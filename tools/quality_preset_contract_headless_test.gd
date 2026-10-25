extends SceneTree
# Headless half of the graphics-preset contract (docs/quality-presets-plan.md section 1): C1-C3 configuration for presets 0..4.
# Run: Godot --headless --path . --script res://tools/quality_preset_contract_headless_test.gd
const TEST := preload("res://tools/QualityPresetContractHeadlessTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("QP_CONTRACT_HEADLESS_WRAPPER=PASS")
	else:
		push_error("QP_CONTRACT_HEADLESS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
