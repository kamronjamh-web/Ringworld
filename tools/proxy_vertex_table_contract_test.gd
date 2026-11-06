extends SceneTree

## WO-S0 contract: packed vertex-table slots read back from the GPU equal the worker's SectorData
## (positions, morph and chord bitwise, normals within oct16 tolerance, colours exact, UV derived, skirts equal).
const FIXTURE := preload("res://tools/ProxyVertexTableContractTest.cs")
const TIMEOUT_MSEC := 600000

var fixture
var started_msec := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("PROXY_VERTEX_TABLE_CONTRACT=FAIL RenderingDevice required"); quit(1); return
	fixture = FIXTURE.new()
	root.add_child(fixture)
	fixture.call_deferred("Start")
	started_msec = Time.get_ticks_msec()

func _process(_delta: float) -> bool:
	if fixture == null: return false
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("PROXY_VERTEX_TABLE_CONTRACT=" + result); quit(0)
		else:
			push_error("PROXY_VERTEX_TABLE_CONTRACT=" + result); quit(1)
		return true
	if Time.get_ticks_msec() - started_msec > TIMEOUT_MSEC:
		push_error("PROXY_VERTEX_TABLE_CONTRACT=FAIL timeout"); quit(1)
	return false
