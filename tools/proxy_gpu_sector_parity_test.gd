extends SceneTree

## WO-G2 of docs/gpu-offload-plan.md: the GPU K2 sector build (slot textures and summary) against RingTerrainProxySector.BuildRawSector on the same
## sectors (see tools/ProxyGpuSectorParityTest.cs). Windowed Vulkan; no world.
const FIXTURE := preload("res://tools/ProxyGpuSectorParityTest.cs")
const TIMEOUT_MSEC := 600000

var fixture
var started_msec := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("PROXY_GPU_SECTOR_PARITY=FAIL RenderingDevice required"); quit(1); return
	fixture = FIXTURE.new()
	root.add_child(fixture)
	fixture.call_deferred("Start")
	started_msec = Time.get_ticks_msec()

func _process(_delta: float) -> bool:
	if fixture == null: return false
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("PROXY_GPU_SECTOR_PARITY=" + result); quit(0)
		else:
			push_error("PROXY_GPU_SECTOR_PARITY=" + result); quit(1)
		return true
	if Time.get_ticks_msec() - started_msec > TIMEOUT_MSEC:
		push_error("PROXY_GPU_SECTOR_PARITY=FAIL timeout"); quit(1)
	return false
