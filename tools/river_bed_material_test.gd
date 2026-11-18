extends SceneTree

## T4-7 (D11): river bed, bank and dry basin materials behind ground.river_beds (see tools/RiverBedMaterialTest.cs). Headless. Run with tools/run_tests.ps1 -Tests river_bed_material_test.
## Pass --cost for the CPU micro-cost numbers (printed, nothing checked). Pass --capture after the Godot -- separator only to write the pre-change identity golden once (it refuses to overwrite).
const TEST := preload("res://tools/RiverBedMaterialTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"), OS.get_cmdline_user_args().has("--cost"))
	runner = null
	if code == 0:
		print("RIVER_BED_MATERIAL_WRAPPER=PASS")
	else:
		push_error("RIVER_BED_MATERIAL_WRAPPER=FAIL exit=%d" % code)
	quit(code)
