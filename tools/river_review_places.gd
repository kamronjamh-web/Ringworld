extends SceneTree

## T3R-12: G2 review places and the 10 km benchmark route on the review world's river graph (see tools/RiverReviewPlaces.cs). Args: --seed=1337 --out=<absolute dir>. Prints RIVER_REVIEW_PLACES=PASS|FAIL.
const TEST := preload("res://tools/RiverReviewPlaces.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	quit(code)
