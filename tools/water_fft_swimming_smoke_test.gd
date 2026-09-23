extends SceneTree

# Real-player regression for the FFT ocean surface query. It keeps saved worlds
# and user water tuning out of the fixture, then releases the explorer into the
# production swim motor rather than calling private water-state methods.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const OUTPUT_PATHS := preload("res://tools/generated_output_paths.gd")
const WATER_PROBE := preload("res://scripts/Tools/WaterRenderingProbe.cs")
const SWIM_PROBE := preload("res://scripts/Tools/WaterFftSwimmingProbe.cs")
const TIMEOUT_FRAMES := 1800
const SWIM_FRAMES := 180
const SPAWN_SETTLE_GRACE_SECONDS := 0.25
const SHUTDOWN_DRAIN_FRAMES := 6

var _scene: Node
var _drawer: Node
var _fft: Node
var _water_probe: RefCounted
var _swim_probe: RefCounted
var _ocean: Dictionary
var _defaults_path := ""
var _frame := 0
var _phase := "boot"
var _phase_frame := 0
var _swim_begin_physics_frame := 0
var _underwater_observe_physics_frames := 1
var _start_vertical := 0.0
var _base_surface := 0.0
var _query_frames := 0
var _query_ready_frames := 0
var _tracked_frames := 0
var _nonzero_offset_frames := 0
var _initial_underwater := false
var _max_surface_error := 0.0
var _exit_code := -1

func _initialize() -> void:
	_boot.call_deferred()

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_defaults_path = OUTPUT_PATHS.smoke_capture_path("water_fft_swimming_smoke.tres")
	if _defaults_path.is_empty():
		_fail("could not allocate isolated water defaults")
		return
	DirAccess.remove_absolute(_defaults_path)
	_scene = TEST_SCENE.instantiate()
	# Match water_rendering_smoke_test: never let this fixture load or save a world slot.
	var saves := _scene.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	_drawer = _scene.get_node_or_null("LiveTuningDrawer")
	if _drawer == null:
		_fail("missing LiveTuningDrawer before scene entry")
		return
	_drawer.call("InstallWaterDefaults", _defaults_path)
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _scene, 5000000.0)
	launcher.free()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frame += 1
	if _phase == "shutdown":
		# queue_free releases the controller/provider first. Keep the fixture
		# alive for several frames so render-thread callbacks can observe that
		# disposal before the process exits.
		if _frame - _phase_frame < SHUTDOWN_DRAIN_FRAMES: return false
		_cleanup()
		quit(_exit_code)
		return false
	if _frame > TIMEOUT_FRAMES:
		return _fail("timed out in phase " + _phase)
	if _phase == "boot":
		if _frame < 8: return false
		_fft = _scene.get_node_or_null("WaterFftController")
		_water_probe = WATER_PROBE.new()
		_swim_probe = SWIM_PROBE.new()
		if _fft == null: return _fail("missing WaterFftController")
		if str(_drawer.call("GetWaterTuningDiagnostics").get("path", "")) != _defaults_path:
			return _fail("water drawer did not retain isolated defaults")
		_fft.set("Enabled", true)
		_fft.set("Resolution", 128)
		_fft.set("CascadeCount", 2)
		_fft.set("UpdatesPerSecond", 24)
		_fft.set("GeometryHeightScale", 1.0)
		_fft.set("GeometryChoppiness", 0.75)
		_ocean = _water_probe.call("FindOcean", _scene)
		if not bool(_ocean.get("valid", false)) or int(_ocean.get("water_kind", 0)) != 1:
			return _fail("typed water probe found no ocean")
		_phase = "warm_fft_query"
		_phase_frame = _frame
		return false
	if _phase == "warm_fft_query":
		var binding: Dictionary = _water_probe.call("GetFftBindingStatus", _scene)
		var query_ready := bool(_swim_probe.call("PrimeOceanFftQuery", _scene, _ocean))
		var geometry_ready := bool(binding.get("controller_ready", false)) \
			and int(binding.get("active_cascades", 0)) == 2 \
			and int(binding.get("geometry_meshes", 0)) > 0
		if geometry_ready and query_ready:
			var started: Dictionary = _swim_probe.call("BeginSwimming", _scene, _ocean)
			if not bool(started.get("valid", false)): return _fail("could not release swimmer below ocean surface")
			_start_vertical = float(started.get("start_vertical", NAN))
			_base_surface = float(started.get("base_surface", NAN))
			if not is_finite(_start_vertical) or not is_finite(_base_surface): return _fail("non-finite swimming start")
			print("WATER_FFT_SWIMMING_SMOKE_TEST=PLACEMENT ocean=%s start=%s base_surface=%.3f sampler_depth=%.3f" % [_ocean, started, _base_surface, float(_ocean.get("depth", NAN))])
			_swim_begin_physics_frame = Engine.get_physics_frames()
			var physics_hz := float(ProjectSettings.get_setting("physics/common/physics_ticks_per_second", 60))
			var explorer := _scene.get_node_or_null("Explorer")
			var settle_timeout := float(explorer.get("SpawnTerrainSettleTimeoutSeconds")) if explorer != null else 3.0
			_underwater_observe_physics_frames = maxi(1, int(ceilf((maxf(0.1, settle_timeout) + SPAWN_SETTLE_GRACE_SECONDS) * physics_hz)))
			_phase = "swim"
			_phase_frame = _frame
		return false
	if _phase == "swim":
		var sample: Dictionary = _swim_probe.call("Capture", _scene)
		if not bool(sample.get("valid", false)): return _fail("non-finite live water state; sample=" + str(sample))
		var elapsed := _frame - _phase_frame
		var physics_elapsed := Engine.get_physics_frames() - _swim_begin_physics_frame
		# Benchmark mode bypasses the production spawn-terrain settle guard. Once
		# released, honor that guard and begin measurement only after the real swim
		# state becomes observable.
		if not _initial_underwater:
			if physics_elapsed > 0 and bool(sample.get("underwater", false)) and bool(sample.get("swimming", false)):
				_initial_underwater = true
				_start_vertical = float(sample.get("vertical", NAN))
				if not is_finite(_start_vertical): return _fail("non-finite post-settle swimmer position; sample=" + str(sample))
				_phase_frame = _frame
				_query_frames = 0; _query_ready_frames = 0; _tracked_frames = 0; _nonzero_offset_frames = 0; _max_surface_error = 0.0
				print("WATER_FFT_SWIMMING_SMOKE_TEST=SWIM_READY physics_elapsed=%d sample=%s" % [physics_elapsed, sample])
				return false
			if physics_elapsed >= _underwater_observe_physics_frames:
				return _fail("swimmer did not enter observable underwater swimming before spawn settle deadline; physics_elapsed=%d sample=%s" % [physics_elapsed, str(sample)])
			return false
		if not bool(sample.get("swimming", false)):
			return _fail("swimmer left the finite ocean column; physics_elapsed=%d sample=%s" % [physics_elapsed, str(sample)])
		_query_frames += 1
		if bool(sample.get("query_ready", false)):
			_query_ready_frames += 1
			var offset := float(sample.get("offset", NAN))
			var surface := float(sample.get("surface", NAN))
			if not is_finite(offset) or not is_finite(surface): return _fail("FFT query returned non-finite values")
			_max_surface_error = maxf(_max_surface_error, absf(surface - (_base_surface + offset)))
			_tracked_frames += 1
			if absf(offset) > 0.001: _nonzero_offset_frames += 1
		if elapsed < SWIM_FRAMES: return false
		var final_vertical := float(sample.get("vertical", NAN))
		var rise := final_vertical - _start_vertical
		if not is_finite(final_vertical) or rise < 0.25:
			return _fail("swim motor did not provide meaningful buoyant rise: %.3fm" % rise)
		if _query_ready_frames * 100 < _query_frames * 65:
			return _fail("FFT surface cache was unavailable too often: %d/%d" % [_query_ready_frames, _query_frames])
		if _tracked_frames == 0 or _nonzero_offset_frames == 0:
			return _fail("live water surface never tracked a nonzero FFT offset")
		if _max_surface_error > 0.08:
			return _fail("live water surface diverged from cached FFT offset: %.4fm" % _max_surface_error)
		print("WATER_FFT_SWIMMING_SMOKE_TEST=PASS rise=%.3fm query=%d/%d nonzero=%d max_surface_error=%.4fm" % [rise, _query_ready_frames, _query_frames, _nonzero_offset_frames, _max_surface_error])
		return _begin_shutdown(0)
	return false

func _cleanup() -> void:
	if not _defaults_path.is_empty(): DirAccess.remove_absolute(_defaults_path)

func _fail(reason: String) -> bool:
	push_error("WATER_FFT_SWIMMING_SMOKE_TEST=FAIL " + reason)
	return _begin_shutdown(1)

func _begin_shutdown(exit_code: int) -> bool:
	if _phase == "shutdown": return false
	_exit_code = exit_code
	_phase = "shutdown"
	_phase_frame = _frame
	# Let the complete scene-tree teardown call the production controller's
	# _ExitTree/dispose path rather than leaving RenderingDevice resources live.
	if _scene != null and is_instance_valid(_scene): _scene.queue_free()
	_scene = null
	_drawer = null
	_fft = null
	_water_probe = null
	_swim_probe = null
	return false
