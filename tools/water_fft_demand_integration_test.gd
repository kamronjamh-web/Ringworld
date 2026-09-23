extends SceneTree
const SMOKE := preload("res://scripts/Tools/WaterFftDemandIntegrationSmoke.cs")
var smoke: Node
var draining := 0
var exit_code := 0
func _initialize() -> void:
	smoke = SMOKE.new()
	root.add_child(smoke)
func _process(_delta: float) -> bool:
	if draining > 0:
		draining -= 1
		if draining == 0: quit(exit_code)
		return false
	if not bool(smoke.get("Completed")): return false
	if not bool(smoke.get("Passed")):
		push_error("WATER_FFT_DEMAND_INTEGRATION=FAIL " + str(smoke.get("LastFailure")))
		exit_code = 1
	smoke.queue_free()
	draining = 6
	return false
