extends SceneTree

## Full-scene terrain-contact check. It uses real terrain samples to find a
## wet/dry transition, then validates the published shoreline field near it.
const TEST_SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE: PackedScene = preload("res://scenes/WorldSizeLauncher.tscn")
const PREFERENCE_ISOLATION = preload("res://scripts/Tools/MinimapWindContractTest.cs")
const WATER_PROBE = preload("res://scripts/Tools/WaterRenderingProbe.cs")
const TIMEOUT_FRAMES: int = 1800
const SETTLE_FRAMES: int = 120
var _scene: Node
var _drawer: Node
var _fft: Node
var _probe: RefCounted
var _shore: Dictionary
var _frame: int = 0
var _phase: String = "boot"
var _phase_frame: int = 0
var _defaults_path: String = ""
var _capture_path: String = ""

func _initialize() -> void:
	_boot.call_deferred()

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	var output_directory := "res://.godot/shared-wind-water/shore_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
	_defaults_path = output_directory + "/waterprefs.tres"
	_capture_path = output_directory + "/capture.png"
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_directory)) != OK:
		_fail("could not allocate isolated fixture outputs")
		return
	DirAccess.remove_absolute(_defaults_path)
	_scene = TEST_SCENE.instantiate()
	var saves: Node = _scene.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	_drawer = _scene.get_node_or_null("LiveTuningDrawer")
	if _drawer == null: _fail("missing LiveTuningDrawer"); return
	var launcher: Node = LAUNCHER_SCENE.instantiate(); root.add_child(launcher); launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", _scene, 5000000.0); launcher.free()
	# Install every preference path before scene Ready, including shared wind
	# and legacy grass migration. Preserve this fixture's existing water path.
	var preferences_path := "res://.godot/water_fft_terrain_shore_preferences_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
	var isolation: Dictionary = PREFERENCE_ISOLATION.new().ExecuteFixture("isolation", _scene, preferences_path)
	if not bool(isolation.get("pass", false)):
		_fail("preference isolation failed: " + str(isolation.get("detail", "")))
		return
	_drawer.call("InstallWaterDefaults", _defaults_path)
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frame += 1
	if _frame > TIMEOUT_FRAMES: return _fail("timed out in phase " + _phase)
	if _phase == "boot" and _frame >= 8:
		_fft = _scene.get_node_or_null("WaterFftController")
		_probe = WATER_PROBE.new()
		if _fft == null: return _fail("missing WaterFftController")
		if str(_drawer.call("GetWaterTuningDiagnostics").get("path", "")) != _defaults_path: return _fail("fixture did not retain isolated defaults")
		_fft.set("Enabled", true); _fft.set("Resolution", 128); _fft.set("CascadeCount", 2); _fft.set("UpdatesPerSecond", 24)
		_shore = _probe.call("FindOceanShore", _scene)
		if not bool(_shore.get("valid", false)): return _fail("bounded terrain search found no ocean-to-dry transition")
		if not bool(_probe.call("PlaceShoreCamera", _scene, _shore, 8.0)): return _fail("could not place explorer offshore")
		var explorer: Node = _scene.get_node_or_null("Explorer")
		if explorer != null:
			explorer.set_physics_process(false)
			explorer.set_process_unhandled_input(false)
			explorer.call("SetPresentationView", 18.0, -38.0)
		_phase = "wait_field"; _phase_frame = _frame
		return false
	if _phase == "wait_field":
		var binding: Dictionary = _probe.call("GetFftBindingStatus", _scene)
		var near: Dictionary = _probe.call("GetFftShorelineStatus", _scene, float(_shore.get("around", 0.0)), int(_shore.get("offshore_across", 0)))
		if not bool(binding.get("controller_ready", false)) or int(binding.get("geometry_meshes", 0)) <= 0 or not bool(near.get("sample_valid", false)): return false
		if _frame < _phase_frame + SETTLE_FRAMES: return false
		var water_across: int = int(_shore.get("offshore_across", 0)); var dry_across: int = int(_shore.get("dry_across", 0)); var direction: int = signi(water_across - dry_across)
		var far: Dictionary = _probe.call("GetFftShorelineStatus", _scene, float(_shore.get("around", 0.0)), water_across + direction * 24)
		if not bool(far.get("sample_valid", false)): return _fail("shoreline field did not cover the offshore comparison point")
		var near_depth: float = float(near.get("signed_depth", -INF)); var near_distance: float = float(near.get("shore_distance", INF)); var far_depth: float = float(far.get("signed_depth", -INF)); var far_distance: float = float(far.get("shore_distance", -INF))
		# Bilinear contact samples can straddle a dry triangle vertex. Signed
		# clearance may be negative there; zero shore distance must still pin it.
		if not is_finite(near_depth) or absf(near_distance) > 4.0: return _fail("transition was not a pinned near-shore field sample: " + str(near))
		if far_depth <= 0.0 or far_distance <= near_distance + 4.0: return _fail("offshore field distance did not exceed shore attenuation distance near=%s far=%s" % [near, far])
		await RenderingServer.frame_post_draw
		var image: Image = root.get_texture().get_image()
		if image.is_empty() or image.save_png(_capture_path) != OK: return _fail("could not capture terrain/water contact")
		print("WATER_FFT_TERRAIN_SHORE_SMOKE=PASS shore=%s near=%s far=%s binding=%s capture=%s" % [_shore, near, far, binding, _capture_path])
		_cleanup(); quit(0); return true
	return false

func _cleanup() -> void:
	if not _defaults_path.is_empty(): DirAccess.remove_absolute(_defaults_path)

func _fail(reason: String) -> bool:
	push_error("WATER_FFT_TERRAIN_SHORE_SMOKE=FAIL " + reason)
	_cleanup(); quit(1); return true
