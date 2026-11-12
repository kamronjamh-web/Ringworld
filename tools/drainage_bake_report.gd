extends SceneTree

## T2R-4: drainage bake report and river map (see tools/DrainageBakeReportTool.cs). GPU source needs a windowed run.
## Manual: Godot_console.exe --path . --script res://tools/drainage_bake_report.gd -- --source=gpu --ring --seed=1337 [--noise=6 --lake-abundance=0.5 --label=x]
const TOOL := preload("res://tools/DrainageBakeReportTool.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TOOL.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("DRAINAGE_BAKE_REPORT_WRAPPER=PASS")
	else:
		push_error("DRAINAGE_BAKE_REPORT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
