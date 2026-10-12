extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MAX_SECONDS := 180.0

var _scene: Node3D
var _far: Node
var _started_at := 0
var _frames := 0
var _rendered := false
var _configured := false
var _lifecycle_pass := false
var _settled_frames := 0
var _settled_at := 0
var _global_pass := false
var _regional_pass := false
var _render_capture_phase := 0
var _render_phase_frame := 0
var _terrain_reference: Image
var _terrain_nodes: Array[MeshInstance3D] = []
const CAPTURE_PATH := "res://.godot/far_adaptive_geometry_rendered.png"

func _initialize() -> void:
	_started_at = Time.get_ticks_msec()
	_rendered = OS.get_cmdline_user_args().has("--rendered")
	_scene = TEST_SCENE.instantiate() as Node3D
	_far = _scene.get_node("ScaledFarViewport")
	_far.set("Enabled", true)
	_far.set("AroundSegments", 4096 if _rendered else 256)
	_far.set("AcrossSegments", 64 if _rendered else 8)
	_far.set("ResolutionScale", 1.0)
	_far.set("GeometrySectorCount", 16 if _rendered else 4)
	_far.set("AdaptiveGeometryEnabled", _rendered)
	_far.set("FarSurfaceTilesEnabled", _rendered)
	_far.set("RegionalFarLodsEnabled", not _rendered)
	_far.set("GeometryMaxVertices", 750000 if _rendered else 100000)
	_configure_fixture_cutout()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < 3:
		return false
	if not _configured:
		# Apply after scene preset initialization so this fixture owns its request.
		_far.call("ApplyRuntimeRenderQuality", 1.0, 4096 if _rendered else 256, 64 if _rendered else 8)
		_far.set("FarSurfaceTilesEnabled", _rendered)
		_far.set("GeometryMaxVertices", 750000 if _rendered else 100000)
		_configure_fixture_cutout()
		_far.call("ConfigureAdaptiveGeometry", _rendered, 2.0, 384, 16 if _rendered else 4, 0.25)
		_configured = true
	if _rendered:
		_far.call("AimGeometryContractCameraAtFarRing")
	if _lifecycle_pass:
		return _wait_rendered_capture()
	var result := "PASS" if _global_pass else str(_far.call("RunAsyncGeometryLifecycleContract"))
	if result == "PASS":
		_global_pass = true
		if not _rendered and not _regional_pass:
			var regional_result := str(_far.call("RunAsyncRegionalGeometryLifecycleContract"))
			if regional_result != "PENDING" and regional_result != "PASS":
				push_error("FAR_ASYNC_REGIONAL_GEOMETRY_CONTRACT=FAIL %s" % regional_result)
				_scene.queue_free()
				quit(1)
				return false
			if regional_result == "PASS":
				_regional_pass = true
				print("FAR_ASYNC_REGIONAL_GEOMETRY_CONTRACT=PASS")
			else:
				result = "PENDING"
	if result == "PASS":
		var coverage := str(_far.call("RunShiftedAnchorCoverageContract"))
		if coverage != "PASS":
			push_error("FAR_ASYNC_GEOMETRY_LIFECYCLE_CONTRACT=FAIL %s" % coverage)
			_scene.queue_free()
			quit(1)
			return false
		print("FAR_SHIFTED_ANCHOR_COVERAGE_CONTRACT=PASS")
		print("FAR_ASYNC_GEOMETRY_LIFECYCLE_CONTRACT=PASS frames=%d global=%dx%d sectors=%d" % [_frames,
			int(_far.get("ActiveGlobalAroundSegments")), int(_far.get("ActiveGlobalAcrossSegments")),
			int(_far.get("ActiveTerrainSectorCount"))])
		if _rendered:
			_lifecycle_pass = true
			return _wait_rendered_capture()
		_scene.queue_free()
		quit(0)
	elif result != "PENDING":
		push_error("FAR_ASYNC_GEOMETRY_LIFECYCLE_CONTRACT=FAIL %s" % result)
		_scene.queue_free()
		quit(1)
	elif Time.get_ticks_msec() - _started_at > int(MAX_SECONDS * 1000.0):
		push_error("FAR_ASYNC_GEOMETRY_LIFECYCLE_CONTRACT=FAIL timeout frames=%d" % _frames)
		_scene.queue_free()
		quit(1)
	return false

func _wait_rendered_capture() -> bool:
	if Time.get_ticks_msec() - _started_at > int(MAX_SECONDS * 1000.0):
		push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL tile/geometry timeout")
		_scene.queue_free()
		quit(1)
		return false
	if RenderingServer.get_rendering_device() == null:
		push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL --rendered requires a native GPU rendering device")
		_scene.queue_free()
		quit(1)
		return false
	if int(_far.get("AroundSegments")) != 4096 or int(_far.get("AcrossSegments")) != 64 or not is_equal_approx(float(_far.get("ResolutionScale")), 1.0):
		push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL requested 4096x64 at100percent changed")
		_scene.queue_free()
		quit(1)
		return false
	var ready := bool(_far.get("GlobalGeometryReady"))
	var resident := int(_far.get("SurfaceTileResidentCount"))
	var target := int(_far.get("ActiveGlobalTerrainSectorCount"))
	var pending := int(_far.get("SurfaceTilePendingCount"))
	if not ready or not bool(_far.get("GlobalTerrainTilesReady")) or target <= 0 or resident < target or pending != 0:
		_settled_frames = 0
		_settled_at = 0
		return false
	if _settled_at == 0:
		_settled_at = Time.get_ticks_msec()
	_settled_frames += 1
	if _settled_frames < 3 or Time.get_ticks_msec() - _settled_at < 250:
		return false
	var viewport := _far.get_node("FarRingViewport") as SubViewport
	if _render_capture_phase == 0:
		# Freeze scene-state updates after publication and texture settling. The
		# real SubViewport keeps drawing the actual production ArrayMesh sectors.
		_far.set_process(false)
		var world_environment := viewport.get_node("FarWorldEnvironment") as WorldEnvironment
		world_environment.environment.background_mode = Environment.BG_COLOR
		world_environment.environment.background_color = Color.BLACK
		world_environment.environment.sky = null
		world_environment.environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
		world_environment.environment.ambient_light_color = Color(0.5, 0.5, 0.5)
		world_environment.environment.ambient_light_energy = 0.75
		_prepare_terrain_only(viewport)
		if _terrain_nodes.is_empty():
			push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL no semantic terrain sector meshes")
			_scene.queue_free()
			quit(1)
			return false
		_render_phase_frame = _frames
		_render_capture_phase = 1
		return false
	if _frames - _render_phase_frame < 3:
		return false
	if _render_capture_phase == 1:
		_terrain_reference = viewport.get_texture().get_image()
		if _terrain_reference == null or _terrain_reference.is_empty() or _terrain_reference.save_png(CAPTURE_PATH) != OK:
			push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL unable to save terrain-only actual viewport")
			_scene.queue_free()
			quit(1)
			return false
		for terrain in _terrain_nodes:
			terrain.visible = false
		_render_phase_frame = _frames
		_render_capture_phase = 2
		return false
	var hidden_image := viewport.get_texture().get_image()
	var image := _terrain_reference
	if image == null or image.is_empty() or image.save_png(CAPTURE_PATH) != OK:
		push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL unable to save actual far viewport image")
		_scene.queue_free()
		quit(1)
		return false
	var minimum := Color(1, 1, 1, 1)
	var maximum := Color(0, 0, 0, 0)
	var opaque_samples := 0
	for y in range(32):
		for x in range(32):
			var pixel := image.get_pixel(mini(image.get_width() - 1, int(x * image.get_width() / 32.0)), mini(image.get_height() - 1, int(y * image.get_height() / 32.0)))
			minimum = Color(minf(minimum.r, pixel.r), minf(minimum.g, pixel.g), minf(minimum.b, pixel.b), minf(minimum.a, pixel.a))
			maximum = Color(maxf(maximum.r, pixel.r), maxf(maximum.g, pixel.g), maxf(maximum.b, pixel.b), maxf(maximum.a, pixel.a))
			if pixel.a > 0.5:
				opaque_samples += 1
	var variance := maxf(maximum.r - minimum.r, maxf(maximum.g - minimum.g, maximum.b - minimum.b))
	if opaque_samples < 10 or variance < 0.001:
		push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL actualviewport blank/flat opaque_samples=%d variance=%f" % [opaque_samples, variance])
		_scene.queue_free()
		quit(1)
		return false
	var changed_samples := 0
	var maximum_delta := 0.0
	if hidden_image == null or hidden_image.is_empty():
		push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL terrain-hidden comparison image unavailable")
		_scene.queue_free()
		quit(1)
		return false
	for y in range(64):
		for x in range(64):
			var px := mini(image.get_width() - 1, int(x * image.get_width() / 64.0))
			var py := mini(image.get_height() - 1, int(y * image.get_height() / 64.0))
			var visible_pixel := image.get_pixel(px, py)
			var hidden_pixel := hidden_image.get_pixel(px, py)
			var delta := maxf(absf(visible_pixel.r - hidden_pixel.r), maxf(absf(visible_pixel.g - hidden_pixel.g), maxf(absf(visible_pixel.b - hidden_pixel.b), absf(visible_pixel.a - hidden_pixel.a))))
			maximum_delta = maxf(maximum_delta, delta)
			if delta > 0.001:
				changed_samples += 1
	if changed_samples < 41 or maximum_delta <= 0.02:
		push_error("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=FAIL hiding actual terrain did not change pixels changed=%d max_delta=%f" % [changed_samples, maximum_delta])
		_scene.queue_free()
		quit(1)
		return false
	print("FAR_GEOMETRY_GPU_SUBMISSION_CONTRACT=PASS requested=4096x64 resolution=1.0 actual=%dx%d tiles=%d terrain_changed_samples=%d terrain_max_delta=%f screenshot=%s visual_acceptance=pending" % [
		int(_far.get("ActiveGlobalAroundSegments")), int(_far.get("ActiveGlobalAcrossSegments")), resident, changed_samples, maximum_delta, ProjectSettings.globalize_path(CAPTURE_PATH)])
	_scene.queue_free()
	quit(0)
	return false

func _prepare_terrain_only(node: Node) -> void:
	if node is MeshInstance3D:
		var mesh := node as MeshInstance3D
		var terrain := bool(mesh.get_meta("far_global_terrain", false))
		mesh.visible = terrain
		if terrain:
			_terrain_nodes.append(mesh)
	for child in node.get_children():
		_prepare_terrain_only(child)

func _configure_fixture_cutout() -> void:
	if not _rendered:
		return
	# The development scene ring is only ~160km across. Production profile
	# cutouts of500km intentionally discard that entire test ring.
	_far.set("FadeStartMeters", 8000.0)
	_far.set("FadeEndMeters", 16000.0)
	_far.set("MinimumLocalTerrainCutoutMeters", 16000.0)
	_far.set("KeepLocalTerrainFallbackVisible", false)
