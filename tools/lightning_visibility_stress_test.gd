extends SceneTree

## GPU-only Certified stress fixture: frozen OFF/ON/OFF controls for seam,
## ring-edge, and explicit far-viewport opaque-occlusion lightning cases.
const WORLD_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const OUTPUT := "res://.godot/lightning_visibility_stress"
var _world: Node3D

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		return _fail("RenderingDevice required; headless/Compatibility is not a GPU result")
	call_deferred("_run")

func _run() -> void:
	_world = WORLD_SCENE.instantiate() as Node3D
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	if not launcher.is_node_ready(): await launcher.ready
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _world, 5000000.0)
	launcher.free()
	root.add_child(_world)
	for frame in range(20): await process_frame
	var weather := _world.get_node("RingVolumetricClouds")
	var far := _world.get_node("ScaledFarViewport")
	var grid := _world.get_node("ChunkGridManager")
	var explorer := _world.get_node("Explorer")
	var camera := _world.get_node("Explorer/Head/Camera3D") as Camera3D
	var clock := _world.get_node("RingDayNightCycle")
	var day_side := _world.get_node_or_null("DaySideAtmosphere")
	var bridge := _world.get_node_or_null("SunshineCloudBridge")
	var driver := _world.get_node_or_null("SunshineCloudsDriver")
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var width := float(grid.get("RingAcrossWidthMeters"))
	var label := "edge_left"
	var target_around := circumference * 0.5
	var target_across := 1500.0
	var observer_around := 0.0
	if "--lightning-edge-right" in OS.get_cmdline_user_args():
		label = "edge_right"
		target_across = width - 1500.0
	elif "--lightning-seam" in OS.get_cmdline_user_args() or "--lightning-seam-diagnose" in OS.get_cmdline_user_args():
		label = "seam"
		target_around = circumference - 1000.0
		target_across = width * 0.5
		observer_around = circumference * 0.5
	elif "--lightning-occlusion" in OS.get_cmdline_user_args():
		label = "occlusion"
		target_across = width * 0.5
	elif "--lightning-cloud-occlusion" in OS.get_cmdline_user_args():
		label = "cloud_occlusion"
		target_across = width * 0.5
	clock.set("Paused", true)
	clock.call("set_simulation_seconds", 0.0)
	if bridge != null: bridge.call("SetBenchmarkViewerPosition", observer_around, 500.0, width * 0.5)
	for settle in range(20): await process_frame
	var target := grid.call("CanonicalRingPositionToWorld", Vector3(target_around, 600.0, target_across)) as Vector3
	camera.look_at(target, Vector3.UP)
	for settle in range(20): await process_frame
	if day_side != null:
		day_side.call("_process", 10.0)
		for settle in range(4): await process_frame
	explorer.set_process(false)
	explorer.set_physics_process(false)
	weather.set_process(false)
	if day_side != null: day_side.set_process(false)
	if bridge != null: bridge.set_process(false)
	if driver != null: driver.set_process(false)
	var environment := _world.get_node("WorldEnvironment") as WorldEnvironment
	var attributes := environment.camera_attributes as CameraAttributesPractical
	var auto_exposure_disabled := false
	if attributes != null and attributes.auto_exposure_enabled:
		attributes.auto_exposure_enabled = false
		auto_exposure_disabled = true
	for canvas: CanvasLayer in _world.find_children("*", "CanvasLayer", true, false):
		if canvas.layer > 0: canvas.hide()
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK: return _fail("could not create output directory")
	var far_viewport := far.get_node("FarRingViewport") as SubViewport
	weather.set("CloudSimulationTimeSeconds", 100.0)
	# A time jump starts an incremental atlas refresh. Publish that exact epoch
	# before controls so cloud-body changes cannot masquerade as lightning.
	var atlas_ready := false
	for atlas_retry in range(120):
		far.call("_Process", 0.0)
		if not bool(far.get("FarWeatherAtlasRefreshInProgress")) and absf(float(far.get("FarWeatherAtlasSnapshotTimeSeconds")) - 100.0) <= 0.0001:
			atlas_ready = true
			break
		await process_frame
	if not atlas_ready:
		return _fail("far weather atlas did not publish frozen epoch=100 snapshot=%.6f pending=%s publications=%s" % [float(far.get("FarWeatherAtlasSnapshotTimeSeconds")), far.get("FarWeatherAtlasRefreshInProgress"), far.get("FarWeatherAtlasPublicationCount")])
	far.set("FarStormLightningEnabled", false)
	far.call("_Process", 0.0)
	far.set_process(false)
	# Godot has no per-renderer shader-time API. Shader TIME follows engine time
	# scale, so freeze it after every explicit state update while frames continue
	# to render and settle existing temporal history.
	Engine.time_scale = 0.0
	for frozen in range(30): await process_frame
	var off_a_main := Image.new()
	var off_a_far := Image.new()
	var off_b_main := Image.new()
	var off_b_far := Image.new()
	var main_baseline := INF
	var far_baseline := INF
	var baseline_retry := 0
	for retry in range(120):
		var candidate_main_a := await _read(_world.get_viewport())
		var candidate_far_a := await _read(far_viewport)
		await process_frame
		var candidate_main_b := await _read(_world.get_viewport())
		var candidate_far_b := await _read(far_viewport)
		main_baseline = _roi_abs(candidate_main_a, candidate_main_b)
		far_baseline = _roi_abs(candidate_far_a, candidate_far_b)
		baseline_retry = retry + 1
		if main_baseline <= 0.005 and far_baseline <= 0.005:
			off_a_main = candidate_main_a
			off_a_far = candidate_far_a
			off_b_main = candidate_main_b
			off_b_far = candidate_far_b
			break
	if off_a_main.is_empty():
		return _fail("OFF/OFF target ROI unstable after 120 frozen frames main=%.6f far=%.6f" % [main_baseline, far_baseline])
	if not _save(off_a_main, "%s_main_off_a" % label) or not _save(off_a_far, "%s_far_off_a" % label) or not _save(off_b_main, "%s_main_off_b" % label) or not _save(off_b_far, "%s_far_off_b" % label):
		return _fail("could not save baseline captures")
	print("LIGHTNING_VISIBILITY_BASELINE mode=%s retries=%d main=%.6f far=%.6f shader_time_scale=%.1f" % [label, baseline_retry, main_baseline, far_baseline, Engine.time_scale])
	far.set("FarStormLightningEnabled", true)
	var source := weather.call("TriggerSharedLightningPreviewAt", target_around, target_across, -9401, 1.0, 80000.0) as Dictionary
	var source_id := int(source.get("event_id", 0))
	weather.set("CloudSimulationTimeSeconds", 100.035)
	far.call("_Process", 0.0)
	await process_frame
	var on_main := await _capture(_world.get_viewport(), "%s_main_on" % label)
	var on_far := await _capture(far_viewport, "%s_far_on" % label)
	var ids := (far.get("SelectedFarLightningEventIds") as Array).duplicate()
	var shell := far_viewport.get_node("ScaledCloudShell") as MeshInstance3D
	var raw_a := shell.material_override.get_shader_parameter("far_lightning_event_a") as Vector4
	var weights := shell.material_override.get_shader_parameter("far_lightning_event_weights") as Vector2
	var far_camera := far_viewport.get_node("FarRingCamera") as Camera3D
	var terrain := far_viewport.get_node_or_null("ScaledSeededRingSurface") as MeshInstance3D
	print("LIGHTNING_VISIBILITY_SOURCE_DIAG mode=%s source=%s selected_ids=%s event_a=%s weights=%s cloud_vertical=%s ground_vertical=%s shell_position=%s shell_aabb=%s far_camera=%s terrain_position=%s terrain_aabb=%s shell_mesh=%s" % [label, source, ids, raw_a, weights, source.get("cloud_vertical_meters", null), source.get("ground_vertical_meters", null), shell.global_position, shell.get_aabb(), far_camera.global_position if far_camera != null else Vector3.ZERO, terrain.global_position if terrain != null else Vector3.ZERO, terrain.get_aabb() if terrain != null else AABB(), _shell_mesh_diagnostics(shell, far_camera, raw_a)])
	if source_id <= 0 or not ids.has(source_id): return _fail("exact shared source ID was not selected source=%s ids=%s" % [source, ids])
	if raw_a.w <= 0.2 or weights.x <= 0.0: return _fail("source pulse/weight was not published raw=%s weights=%s" % [raw_a, weights])
	far.set("FarStormLightningEnabled", false)
	far.call("_Process", 0.0)
	await process_frame
	var off_main := await _capture(_world.get_viewport(), "%s_main_off" % label)
	var off_far := await _capture(far_viewport, "%s_far_off" % label)
	var main_positive := _positive(on_main, off_main)
	var far_positive := _positive(on_far, off_far)
	var main_control := _positive(on_main, off_a_main)
	var far_control := _positive(on_far, off_a_far)
	var main_post_drift := _roi_abs(off_a_main, off_main)
	var far_post_drift := _roi_abs(off_a_far, off_far)
	print("LIGHTNING_VISIBILITY_POST_BASELINE mode=%s main=%.6f far=%.6f" % [label, main_post_drift, far_post_drift])
	if main_post_drift > 0.005 or far_post_drift > 0.005:
		return _fail("OFF-pre/OFF-post target ROI drifted main=%.6f far=%.6f" % [main_post_drift, far_post_drift])
	if "--lightning-seam-diagnose" in OS.get_cmdline_user_args():
		# Diagnostic-only: these captures deliberately alter depth occluders and
		# never participate in acceptance. They separate hidden geometry from an
		# event UV/camera placement failure at the seam.
		print("LIGHTNING_SEAM_DIAG baseline=%s" % _difference_stats(on_far, off_far))
		for node_name in ["ScaledSeededRingSurface", "ScaledWaterSurface", "ScaledStructuralShell"]:
			var occluder := far_viewport.get_node_or_null(node_name) as VisualInstance3D
			if occluder != null:
				await _capture_occluder_diagnostic(far, far_viewport, occluder, "%s_%s_hidden" % [label, node_name])
		var shell_only: Array[VisualInstance3D] = []
		for node_name in ["ScaledSeededRingSurface", "ScaledWaterSurface", "ScaledStructuralShell"]:
			var geometry := far_viewport.get_node_or_null(node_name) as VisualInstance3D
			if geometry != null:
				geometry.hide()
				shell_only.append(geometry)
		await _capture_occluder_diagnostic(far, far_viewport, null, "%s_shell_only" % label)
		for geometry in shell_only: geometry.show()
	# Diagnostic captures above never convert a failed natural baseline into a
	# pass: preserve the normal unblocked visibility contract afterward.
	if main_positive.x < 4.0 or main_positive.y < 0.02 or far_positive.x < 4.0 or far_positive.y < 0.02 or main_control.x < 4.0 or main_control.y < 0.02 or far_control.x < 4.0 or far_control.y < 0.02:
		return _fail("unblocked target lacked localized positive flash main=%s/%s far=%s/%s" % [main_positive, main_control, far_positive, far_control])
	if label == "occlusion":
		var blocker := _add_far_blocker(far_viewport)
		far.set("FarStormLightningEnabled", true)
		far.call("_Process", 0.0)
		await process_frame
		var blocked_on := await _capture(far_viewport, "%s_far_blocked_on" % label)
		far.set("FarStormLightningEnabled", false)
		far.call("_Process", 0.0)
		await process_frame
		var blocked_off := await _capture(far_viewport, "%s_far_blocked_off" % label)
		if _roi_abs(blocked_on, blocked_off) > 0.005:
			return _fail("opaque far blocker did not occlude lightning ROI")
		blocker.queue_free()
	if "--lightning-cloud-occlusion" in OS.get_cmdline_user_args():
		# Synthetic compositor contract only: alpha=0 must expose the already
		# validated far texture; alpha=1 must hide it in the main canvas while the
		# FarRingViewport itself remains positively lit. This is not cloud-shape QA.
		far.set("FarStormLightningEnabled", true)
		far.call("_Process", 0.0)
		if not _set_synthetic_cloud_compositor(Color(0.0, 0.0, 0.0, 0.0)):
			return _fail("FarRingBackdrop compositor material is unavailable")
		await process_frame
		var clear_on_main := await _capture(_world.get_viewport(), "%s_clear_main_on" % label)
		var clear_on_far := await _capture(far_viewport, "%s_clear_far_on" % label)
		far.set("FarStormLightningEnabled", false)
		far.call("_Process", 0.0)
		_set_synthetic_cloud_compositor(Color(0.0, 0.0, 0.0, 0.0))
		await process_frame
		var clear_off_main := await _capture(_world.get_viewport(), "%s_clear_main_off" % label)
		var clear_off_far := await _capture(far_viewport, "%s_clear_far_off" % label)
		if _positive(clear_on_main, clear_off_main).x < 4.0 or _positive(clear_on_far, clear_off_far).x < 4.0:
			return _fail("clear synthetic compositor did not preserve main/far flash")
		far.set("FarStormLightningEnabled", true)
		far.call("_Process", 0.0)
		_set_synthetic_cloud_compositor(Color(0.0, 0.0, 0.0, 1.0))
		await process_frame
		var opaque_on_main := await _capture(_world.get_viewport(), "%s_opaque_main_on" % label)
		var opaque_on_far := await _capture(far_viewport, "%s_opaque_far_on" % label)
		far.set("FarStormLightningEnabled", false)
		far.call("_Process", 0.0)
		_set_synthetic_cloud_compositor(Color(0.0, 0.0, 0.0, 1.0))
		await process_frame
		var opaque_off_main := await _capture(_world.get_viewport(), "%s_opaque_main_off" % label)
		var opaque_off_far := await _capture(far_viewport, "%s_opaque_far_off" % label)
		if _roi_abs(opaque_on_main, opaque_off_main) > 0.005 or _positive(opaque_on_far, opaque_off_far).x < 4.0:
			return _fail("opaque synthetic compositor contract failed main=%.6f far=%s" % [_roi_abs(opaque_on_main, opaque_off_main), _positive(opaque_on_far, opaque_off_far)])
	print("LIGHTNING_VISIBILITY_STRESS_TEST=PASS mode=%s source=%s ids=%s raw=%s weights=%s auto_exposure_disabled=%s main=%s/%s far=%s/%s output=%s" % [label, source, ids, raw_a, weights, auto_exposure_disabled, main_positive, main_control, far_positive, far_control, OUTPUT])
	quit(0)

func _add_far_blocker(viewport: SubViewport) -> MeshInstance3D:
	var camera := viewport.get_node("FarRingCamera") as Camera3D
	var mesh := MeshInstance3D.new()
	mesh.name = "LightningStressOpaqueBlocker"
	var box := BoxMesh.new()
	box.size = Vector3(200.0, 200.0, 1.0)
	mesh.mesh = box
	var material := StandardMaterial3D.new()
	material.albedo_color = Color(0.02, 0.02, 0.02, 1.0)
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mesh.material_override = material
	viewport.add_child(mesh)
	var forward := -camera.global_basis.z.normalized()
	mesh.global_position = camera.global_position + forward * maxf(camera.near * 4.0, 5.0)
	mesh.look_at(camera.global_position + forward * 100.0, camera.global_basis.y)
	return mesh

func _capture_occluder_diagnostic(far: Node, viewport: SubViewport, hidden: VisualInstance3D, label: String) -> void:
	if hidden != null: hidden.hide()
	far.set("FarStormLightningEnabled", true)
	far.call("_Process", 0.0)
	await process_frame
	var on_image := await _capture(viewport, "%s_on" % label)
	far.set("FarStormLightningEnabled", false)
	far.call("_Process", 0.0)
	await process_frame
	var off_image := await _capture(viewport, "%s_off" % label)
	print("LIGHTNING_SEAM_DIAG label=%s stats=%s" % [label, _difference_stats(on_image, off_image)])
	if hidden != null: hidden.show()

func _difference_stats(on: Image, off: Image) -> Dictionary:
	if on.is_empty() or off.is_empty() or on.get_size() != off.get_size(): return {"valid": false}
	var whole_count := 0
	var roi_count := 0
	var peak := 0.0
	var peak_position := Vector2i(-1, -1)
	var center := Vector2i(on.get_width() / 2, on.get_height() / 2)
	for y in range(on.get_height()):
		for x in range(on.get_width()):
			var delta := _luma(on.get_pixel(x, y)) - _luma(off.get_pixel(x, y))
			if delta > 0.02:
				whole_count += 1
				if abs(x - center.x) < 32 and abs(y - center.y) < 32: roi_count += 1
			if delta > peak:
				peak = delta
				peak_position = Vector2i(x, y)
	return {"valid": true, "whole_positive": whole_count, "roi_positive": roi_count, "peak": peak, "peak_position": peak_position}

func _shell_mesh_diagnostics(shell: MeshInstance3D, camera: Camera3D, event_data: Vector4) -> Dictionary:
	if shell.mesh == null or shell.mesh.get_surface_count() < 1: return {"valid": false}
	var arrays := shell.mesh.surface_get_arrays(0)
	var vertices := arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array
	var uvs := arrays[Mesh.ARRAY_TEX_UV] as PackedVector2Array
	if vertices.is_empty() or uvs.is_empty(): return {"valid": false}
	var uv_min := Vector2(INF, INF)
	var uv_max := Vector2(-INF, -INF)
	var nearest := 0
	var nearest_distance := INF
	var seam_samples: Array = []
	var maximum_x := -INF
	for vertex in vertices: maximum_x = maxf(maximum_x, vertex.x)
	for index in range(mini(vertices.size(), uvs.size())):
		var uv := uvs[index]
		uv_min = Vector2(minf(uv_min.x, uv.x), minf(uv_min.y, uv.y))
		uv_max = Vector2(maxf(uv_max.x, uv.x), maxf(uv_max.y, uv.y))
		var distance := uv.distance_to(Vector2(event_data.x, event_data.y))
		if distance < nearest_distance:
			nearest_distance = distance
			nearest = index
		if vertices[index].x >= maximum_x - 0.01 and seam_samples.size() < 8:
			seam_samples.append({"vertex": vertices[index], "uv": uv})
	var target_world := shell.to_global(vertices[nearest])
	return {"valid": true, "vertex_count": vertices.size(), "uv_min": uv_min, "uv_max": uv_max, "event_nearest_vertex": vertices[nearest], "event_nearest_uv": uvs[nearest], "event_uv_distance": nearest_distance, "event_projected": camera.unproject_position(target_world) if camera != null else Vector2.ZERO, "seam_samples": seam_samples}

func _set_synthetic_cloud_compositor(color: Color) -> bool:
	var layer := _world.get_node_or_null("ScaledFarViewport/FarRingBackdrop") as CanvasLayer
	var rect := layer.get_node_or_null("FarRingTexture") as TextureRect if layer != null else null
	var material := rect.material as ShaderMaterial if rect != null else null
	if material == null: return false
	var image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
	image.fill(color)
	var texture := ImageTexture.create_from_image(image)
	material.set_shader_parameter("cloud_mask_enabled", true)
	material.set_shader_parameter("exact_sunshine_clouds_enabled", true)
	material.set_shader_parameter("sunshine_cloud_texture", texture)
	return true

func _capture(viewport: Viewport, label: String) -> Image:
	var image := await _read(viewport)
	if image.is_empty() or not _save(image, label): return Image.new()
	return image

func _read(viewport: Viewport) -> Image:
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	return image if image != null else Image.new()

func _save(image: Image, label: String) -> bool:
	return not image.is_empty() and image.save_png("%s/%s.png" % [OUTPUT, label]) == OK

func _positive(on: Image, off: Image) -> Vector2:
	if on.is_empty() or off.is_empty() or on.get_size() != off.get_size(): return Vector2.ZERO
	var count := 0
	var maximum := 0.0
	for y in range(on.get_height() / 2 - 32, on.get_height() / 2 + 32):
		for x in range(on.get_width() / 2 - 32, on.get_width() / 2 + 32):
			var delta := _luma(on.get_pixel(x, y)) - _luma(off.get_pixel(x, y))
			if delta > 0.02: count += 1
			maximum = maxf(maximum, delta)
	return Vector2(count, maximum)

func _roi_abs(a: Image, b: Image) -> float:
	if a.is_empty() or b.is_empty() or a.get_size() != b.get_size(): return INF
	var maximum := 0.0
	for y in range(a.get_height() / 2 - 32, a.get_height() / 2 + 32):
		for x in range(a.get_width() / 2 - 32, a.get_width() / 2 + 32):
			maximum = maxf(maximum, absf(_luma(a.get_pixel(x, y)) - _luma(b.get_pixel(x, y))))
	return maximum

func _luma(color: Color) -> float:
	return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722

func _fail(message: String) -> void:
	push_error("LIGHTNING_VISIBILITY_STRESS_TEST=FAIL " + message)
	quit(1)
