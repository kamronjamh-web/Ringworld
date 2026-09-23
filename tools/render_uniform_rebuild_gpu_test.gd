extends SceneTree

# Rebuild-focused reproduction fixture for RenderingDevice messages such as:
# "Uniforms were never supplied for set (3) at the time of drawing".
# It exercises the live game scene, but uses a disposable water preferences file
# and removes the save manager before the scene enters the tree.
# Run with --no-clouds to remove the Sunshine compositor path for a quick bisect.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PROBE := preload("res://scripts/Tools/WaterRenderingProbe.cs")

var game: Node
var probe: RefCounted
var ocean: Dictionary
var fft: Node
var graphics: Node
var phase := -1
var phase_frame := 0
var total_frames := 0
var started := false
var no_clouds := false
var paused_only := false
var phase_frames := 120

func _initialize() -> void:
	create_timer(240.0, true).timeout.connect(func():
		push_error("RENDER_UNIFORM_REBUILD=FAIL 240-second watchdog")
		quit(1))
	call_deferred("boot")

func boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("RENDER_UNIFORM_REBUILD=FAIL Forward+ renderer required")
		quit(1)
		return
	no_clouds = "--no-clouds" in OS.get_cmdline_user_args()
	paused_only = "--paused-only" in OS.get_cmdline_user_args()
	phase_frames = 40 if "--quick" in OS.get_cmdline_user_args() else 120
	Engine.max_fps = 60
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	if no_clouds:
		# These are the compositor driver and its live bridge; freeing before add_child
		# prevents their _Ready methods from allocating any cloud rendering resources.
		for path in ["SunshineCloudsDriver", "SunshineCloudBridge", "CloudProviderController"]:
			var node := game.get_node_or_null(path)
			if node != null: node.free()
	game.get_node("LiveTuningDrawer").call("InstallWaterDefaults", "res://artifacts/render_uniform_rebuild_isolated.tres")
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	root.add_child(game)
	for _i in range(60): await process_frame
	probe = PROBE.new()
	ocean = probe.call("FindOcean", game)
	if not bool(ocean.get("valid", false)):
		push_error("RENDER_UNIFORM_REBUILD=FAIL no ocean material")
		quit(1)
		return
	graphics = game.get_node_or_null("GraphicsQuality")
	fft = game.get_node_or_null("WaterFftController")
	if graphics == null or fft == null:
		push_error("RENDER_UNIFORM_REBUILD=FAIL required graphics or FFT controller missing")
		quit(1)
		return
	var explorer := game.get_node_or_null("Explorer")
	if explorer != null:
		explorer.set_physics_process(false)
		explorer.set_process_unhandled_input(false)
	fft.set("FollowWeatherWind", false)
	fft.set("WindSpeedMetersPerSecond", 6.0)
	fft.set("CascadeCount", 2)
	fft.set("Resolution", 128)
	fft.set("Enabled", true)
	probe.call("PlaceCamera", game, ocean)
	started = true
	begin_next_phase()

func begin_next_phase() -> void:
	if paused_only and phase == 0:
		phase = 8
	phase += 1
	phase_frame = 0
	if phase >= 15:
		print("RENDER_UNIFORM_REBUILD=COMPLETE clouds_disabled=", no_clouds)
		started = false
		_finish.call_deferred()
		return
	var viewport := root.get_viewport()
	match phase:
		0:
			# Baseline: the production scene has already run for sixty frames.
			print("RENDER_UNIFORM_REBUILD_PHASE baseline")
		1:
			# Reapply the current real graphics preset, including cloud/far-view changes.
			graphics.call("ApplyPreset", graphics.get("CurrentPreset"), true, false)
			print("RENDER_UNIFORM_REBUILD_PHASE quality_reapply")
		2:
			viewport.scaling_3d_scale = 0.75
			print("RENDER_UNIFORM_REBUILD_PHASE scale_75")
		3:
			viewport.scaling_3d_scale = 1.0
			print("RENDER_UNIFORM_REBUILD_PHASE scale_100")
		4:
			viewport.msaa_3d = Viewport.MSAA_2X
			print("RENDER_UNIFORM_REBUILD_PHASE msaa_2x")
		5:
			viewport.msaa_3d = Viewport.MSAA_DISABLED
			print("RENDER_UNIFORM_REBUILD_PHASE msaa_off")
		6:
			viewport.scaling_3d_mode = Viewport.SCALING_3D_MODE_FSR2
			viewport.scaling_3d_scale = 1.0
			viewport.fsr_sharpness = 0.2
			print("RENDER_UNIFORM_REBUILD_PHASE fsr2_native")
		7:
			viewport.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
			viewport.scaling_3d_scale = 1.0
			print("RENDER_UNIFORM_REBUILD_PHASE native_bilinear")
		8:
			fft.set("Resolution", 256)
			fft.set("Enabled", true)
			print("RENDER_UNIFORM_REBUILD_PHASE fft_256_then_disable")
		9:
			# Reproduce presentation changes while normal scene _Process callbacks
			# are paused; compositor resources still retire on the render thread.
			paused = true
			viewport.scaling_3d_scale = 0.75
			print("RENDER_UNIFORM_REBUILD_PHASE paused_scale_75")
		10:
			viewport.scaling_3d_scale = 1.0
			print("RENDER_UNIFORM_REBUILD_PHASE paused_scale_100")
		11:
			viewport.msaa_3d = Viewport.MSAA_2X
			print("RENDER_UNIFORM_REBUILD_PHASE paused_msaa_2x")
		12:
			viewport.msaa_3d = Viewport.MSAA_DISABLED
			print("RENDER_UNIFORM_REBUILD_PHASE paused_msaa_off")
		13:
			viewport.scaling_3d_mode = Viewport.SCALING_3D_MODE_FSR2
			viewport.scaling_3d_scale = 1.0
			print("RENDER_UNIFORM_REBUILD_PHASE paused_fsr2_native")
		14:
			viewport.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
			viewport.scaling_3d_scale = 1.0
			paused = false
			print("RENDER_UNIFORM_REBUILD_PHASE paused_native_unpause")

func _process(_delta: float) -> bool:
	if not started: return false
	total_frames += 1
	if total_frames > 7200:
		push_error("RENDER_UNIFORM_REBUILD=FAIL timeout")
		quit(1)
		return true
	phase_frame += 1
	# Keep active water in view and let every render rebuild settle for one second.
	if not paused: probe.call("PlaceCamera", game, ocean)
	if phase == 8 and phase_frame == phase_frames / 2:
		fft.set("Enabled", false)
		print("RENDER_UNIFORM_REBUILD_PHASE fft_disabled")
	if phase_frame >= phase_frames:
		if phase == 0 and not no_clouds and not bool(game.get_node("ScaledFarViewport").get("ExactSunshineBackdropActive")):
			if phase_frame < 240: return false
			push_error("RENDER_UNIFORM_REBUILD=FAIL exact cloud backdrop was never bound")
			quit(1)
			return true
		if not no_clouds and phase >= 9:
			var clouds: Resource = game.get_node("SunshineCloudsDriver").get("clouds_resource")
			var status: Dictionary = clouds.call("get_gpu_cloud_shadow_status")
			var retired := int(status.get("retired_compositor_generations", -1))
			print("RENDER_UNIFORM_REBUILD_RETIREMENT phase=%d pending=%d" % [phase, retired])
			if retired != 0:
				push_error("RENDER_UNIFORM_REBUILD=FAIL retired cloud generations did not drain")
				quit(1)
				return true
			if phase == 14 and not bool(game.get_node("ScaledFarViewport").get("ExactSunshineBackdropActive")):
				push_error("RENDER_UNIFORM_REBUILD=FAIL exact cloud backdrop did not resume")
				quit(1)
				return true
		print("RENDER_UNIFORM_REBUILD_STATE phase=%d paused=%s exact_backdrop=%s fft_ready=%s" % [phase, paused, game.get_node("ScaledFarViewport").get("ExactSunshineBackdropActive"), fft.get("Ready")])
		begin_next_phase()
	return false

func _finish() -> void:
	paused = false
	game.queue_free()
	# Give deferred main-thread and render-thread resource releases a chance to
	# complete before shutting down the RenderingDevice itself.
	for _frame in range(12): await process_frame
	print("RENDER_UNIFORM_REBUILD=SHUTDOWN_READY")
	quit(0)
