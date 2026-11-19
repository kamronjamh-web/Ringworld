extends SceneTree

## T6-9: ground-cover card kinds and grass that follows the habitat fields (see tools/GroundCoverCardsTest.cs). Headless.
## Run with tools/run_tests.ps1 -Tests ground_cover_cards_test. Optional user arg sheet=<png> writes the atlas sheet.
const TEST := preload("res://tools/GroundCoverCardsTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var sheet := ""
	for a in OS.get_cmdline_user_args():
		if a.begins_with("sheet="):
			sheet = a.substr(6)
	var runner = TEST.new()
	var code: int = runner.Run(sheet)
	runner = null
	if code == 0:
		print("GROUND_COVER_CARDS_WRAPPER=PASS")
	else:
		push_error("GROUND_COVER_CARDS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
