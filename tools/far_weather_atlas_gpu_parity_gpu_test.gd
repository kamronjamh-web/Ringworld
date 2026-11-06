extends SceneTree
## GPU-4: the GPU-built far weather / layer / fine atlases against the CPU build (per-channel max / mean error, cpu+gpu PNGs for ffmpeg compare).
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   Godot --path . --script res://tools/far_weather_atlas_gpu_parity_gpu_test.gd -- [--out=<dir>]
## Prints FAR_WEATHER_ATLAS_GPU_PARITY=PASS.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PARITY := preload("res://tools/FarWeatherAtlasGpuParityTest.cs")
const TIMES := [500040.0, 1234567.0, 86400.0]

var _scene: Node3D
var _far: Node
var _weather: Node
var _frame := 0
var _step := -1
var _probe
var _wait := 0
var _finish_frames := 0
var _out := ProjectSettings.globalize_path("res://.godot/captures/gpu4")

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+")
		return
	for a in OS.get_cmdline_user_args():
		if String(a).begins_with("--out="):
			_out = String(a).substr(6)
	create_timer(300.0).timeout.connect(func(): _fail("timeout"))
	_scene = TEST_SCENE.instantiate() as Node3D
	_weather = _scene.get_node("RingVolumetricClouds")
	_far = _scene.get_node("ScaledFarViewport")
	_far.set("Enabled", true)
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frame += 1
	if _finish_frames > 0:
		_finish_frames += 1
		if _finish_frames > 30:
			quit(0)
		return false
	if _frame < 5:
		return false
	if _step < 0:
		if int(_far.get("FarWeatherAtlasPublicationCount")) == 0 or bool(_far.get("FarWeatherAtlasRefreshInProgress")):
			if _frame > 600:
				return _fail("far atlas never published")
			return false
		if not bool(_far.get("FarWeatherAtlasUsesGpu")):
			return _fail("first atlas not GPU built: " + str(_far.get("FarAtlasGpuStatus")))
		_weather.set_process(false)
		_step = 0
		_start()
		return false
	if _probe.Poll():
		var label := "t%d" % int(TIMES[_step])
		if _probe.Compare(label) != 0:
			return _fail("parity " + label)
		_step += 1
		if _step >= TIMES.size():
			print("FAR_WEATHER_ATLAS_GPU_PARITY=PASS")
			_probe = null
			_far = null
			_weather = null
			_scene.queue_free()
			_finish_frames = 1
			return true
		_start()
	elif _frame > 4000:
		return _fail("readback timeout")
	return false

func _start() -> void:
	_probe = PARITY.new()
	_probe.OutputDirectory = _out
	var origin: float = float(_far.get("FarFineAtlasOriginForTest"))
	var texel: float = float(_far.get("FarFineAtlasTexelForTest"))
	if not _probe.Start(_far, TIMES[_step], origin, texel):
		_fail("GPU jobs not queued (ineligible)")

func _fail(message: String) -> bool:
	push_error("FAR_WEATHER_ATLAS_GPU_PARITY=FAIL " + message)
	quit(1)
	return true
