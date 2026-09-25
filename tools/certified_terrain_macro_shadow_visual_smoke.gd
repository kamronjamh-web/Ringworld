extends SceneTree

## Forward+ Certified Ring evidence capture for the whole-ring terrain mask.
## This is intentionally a diagnostic, rather than an image-difference test:
## clouds, atmosphere, and LOD streaming can legitimately change RGB values.
const SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER: PackedScene = preload("res://scenes/WorldSizeLauncher.tscn")
const OUTPUT := "res://.godot/terrain_macro_shadow_visual"
const SHADOW_DEFAULTS := OUTPUT + "/shadow_defaults.tres"
const LIMIT_SECONDS := 175.0
const MASK_WAIT_FRAMES := 7200

var _scene: Node
var _started_usec := 0
var _failed := false

func _initialize() -> void:
	_started_usec = Time.get_ticks_usec()
	call_deferred("_boot")

func _process(_delta: float) -> bool:
	if float(Time.get_ticks_usec() - _started_usec) / 1000000.0 > LIMIT_SECONDS:
		_fail("timeout waiting for Certified Ring terrain macro-shadow evidence")
	return false

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required for terrain macro-shadow visual smoke")
		return
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	# Match the disposable launcher path used by the Certified performance smoke.
	# Removing this before the tree enters prevents production save data loading.
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_scene = SCENE.instantiate()
	var saves: Node = _scene.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := _scene.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		_fail("Certified scene is missing LiveTuningDrawer for isolated shadow preferences")
		return
	# Ensure this run cannot inherit the user's user:// shadow settings. The
	# drawer accepts this generated project-local path before it enters the tree.
	DirAccess.remove_absolute(ProjectSettings.globalize_path(SHADOW_DEFAULTS))
	drawer.call("InstallShadowDefaults", SHADOW_DEFAULTS)
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _scene, 5000000.0)
	launcher.free()
	root.add_child(_scene)

	var lod := _scene.get_node_or_null("GlobalTerrainLod3")
	var grid := _scene.get_node_or_null("ChunkGridManager")
	var camera := _scene.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	if lod == null or grid == null or camera == null:
		_fail("Certified scene is missing GlobalTerrainLod3, ChunkGridManager, or camera")
		return
	var mask: ImageTexture = await _wait_for_active_mask(lod)
	if mask == null:
		_fail("terrain macro-shadow texture did not become active")
		return
	var mask_image := mask.get_image()
	if mask_image == null or mask_image.is_empty():
		_fail("active terrain macro-shadow texture has no readable image")
		return
	if mask_image.save_png(ProjectSettings.globalize_path(OUTPUT + "/mask.png")) != OK:
		_fail("could not save terrain macro-shadow mask")
		return

	var selected := _select_interior_mask_receiver(mask_image)
	if not bool(selected.found):
		_write_inconclusive_report(mask_image, selected)
		print("CERTIFIED_TERRAIN_MACRO_SHADOW_VISUAL=INCONCLUSIVE no materially shadowed interior terrain receiver")
		_scene.queue_free()
		quit(0)
		return
	var ring_width := float(lod.get("RingWidthMeters"))
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters"))
	if ring_width <= 0.0 or circumference <= 0.0:
		_fail("Certified Ring did not expose usable canonical dimensions")
		return
	var selected_segment := await _prepare_selected_segment_for_diagnostic(lod, selected.uv.x)
	if selected_segment.is_empty():
		_write_inconclusive_report(mask_image, selected)
		print("CERTIFIED_TERRAIN_MACRO_SHADOW_VISUAL=INCONCLUSIVE selected global-terrain segment was unavailable")
		_scene.queue_free()
		quit(0)
		return
	var canonical := Vector3(selected.uv.x * circumference, 900.0, selected.uv.y * ring_width)
	var target: Vector3 = grid.call("CanonicalRingPositionToWorld", canonical)
	# Look down from the ring's local surface normal. This keeps the selected
	# interior receiver in-frame and avoids a structural-wall horizon view.
	var camera_canonical := canonical
	camera_canonical.y = 12000.0
	var camera_world: Vector3 = grid.call("CanonicalRingPositionToWorld", camera_canonical)
	var tangent_probe: Vector3 = grid.call("CanonicalRingPositionToWorld", Vector3(fposmod(canonical.x + 1000.0, circumference), canonical.y, canonical.z))
	var local_around_tangent := (tangent_probe - target).normalized()
	_freeze_camera_drivers(camera)
	camera.top_level = true
	camera.global_position = camera_world
	camera.look_at(target, local_around_tangent)
	for _frame in 12: await RenderingServer.frame_post_draw

	_freeze_pair_motion()
	var walls_enabled := bool(lod.get("EnableMacroTerrainWallShadows"))
	lod.call("ApplyRuntimeMacroShadowOptions", false, walls_enabled)
	var disabled := await _capture("macro_disabled")
	lod.call("ApplyRuntimeMacroShadowOptions", true, walls_enabled)
	# Re-enabling may schedule a new mask; the existing active texture remains
	# usable, but wait a few rendered frames before taking the paired view.
	for _frame in 12: await RenderingServer.frame_post_draw
	var enabled := await _capture("macro_enabled")
	# Leave the scene in its original enabled state before disposal.
	lod.call("ApplyRuntimeMacroShadowOptions", true, walls_enabled)
	if disabled == null or enabled == null:
		return
	var report := {
		"status": "inconclusive_visual_capture",
		"canonical_receiver_meters": {"around": canonical.x, "vertical_probe": canonical.y, "across": canonical.z},
		"camera_canonical_meters": {"around": camera_canonical.x, "vertical": camera_canonical.y, "across": camera_canonical.z},
		"mask": {"width": mask_image.get_width(), "height": mask_image.get_height(), "uv": selected.uv, "value": selected.value, "occluded_receiver_found": true, "interior_row_start": selected.interior_row_start, "interior_row_end": selected.interior_row_end, "interior_shadow_pixel_count": selected.interior_shadow_pixel_count, "interior_shadow_max": selected.interior_shadow_max},
		"diagnostic_override": selected_segment,
		"captures": {"disabled": _luminance_stats(disabled), "enabled": _luminance_stats(enabled), "receiver_roi": _receiver_roi_stats(disabled, enabled), "mean_absolute_luminance_delta": _mean_luminance_delta(disabled, enabled)},
		"limitation": "The selected global segment is force-visible and its terrain fade is disabled only inside this disposable diagnostic scene, after the mask builds. The automated view still lacks a clearly identifiable terrain receiver; even a darker central ROI does not verify mountain-shadow placement."
	}
	var file := FileAccess.open(OUTPUT + "/report.json", FileAccess.WRITE)
	if file == null:
		_fail("could not write visual smoke report")
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("CERTIFIED_TERRAIN_MACRO_SHADOW_VISUAL=INCONCLUSIVE ", JSON.stringify(report))
	_scene.queue_free()
	quit(0)

func _wait_for_active_mask(lod: Node) -> ImageTexture:
	for _frame in MASK_WAIT_FRAMES:
		if _failed: return null
		var segment := _first_global_terrain_segment(lod)
		if segment != null and segment.mesh != null:
			for surface in segment.mesh.get_surface_count():
				var material := segment.mesh.surface_get_material(surface) as ShaderMaterial
				if material == null: continue
				if not bool(material.get_shader_parameter("terrain_macro_shadow_enabled")): continue
				var texture := material.get_shader_parameter("terrain_macro_shadow_texture") as ImageTexture
				if texture != null: return texture
		await RenderingServer.frame_post_draw
	return null

func _first_global_terrain_segment(lod: Node) -> MeshInstance3D:
	for child in lod.get_children():
		if child is MeshInstance3D and str(child.name).begins_with("GlobalTerrainSegment_"):
			return child as MeshInstance3D
	return null

func _prepare_selected_segment_for_diagnostic(lod: Node, around_uv: float) -> Dictionary:
	var segment_count := maxi(1, int(lod.get("GlobalSegmentCount")))
	var index := clampi(floori(fposmod(around_uv, 1.0) * segment_count), 0, segment_count - 1)
	var expected_name := "GlobalTerrainSegment_%03d" % index
	for _frame in 3600:
		var segment := lod.get_node_or_null(expected_name) as MeshInstance3D
		if segment != null and segment.mesh != null:
			segment.visible = true
			for surface in segment.mesh.get_surface_count():
				var material := segment.mesh.surface_get_material(surface) as ShaderMaterial
				if material == null: continue
				# Global terrain normally hands off near 472 km in Certified. This
				# disposable capture needs its receiver visible at 12 km.
				material.set_shader_parameter("fade_start_meters", 0.0)
				material.set_shader_parameter("fade_end_meters", 1.0)
				material.set_shader_parameter("far_fade_enabled", false)
			return {"enabled": true, "segment": expected_name, "segment_index": index, "terrain_fade_start_meters": 0.0, "terrain_fade_end_meters": 1.0, "far_fade_enabled": false}
		await RenderingServer.frame_post_draw
	return {}

func _select_interior_mask_receiver(image: Image) -> Dictionary:
	# Structural walls dominate the first/last rows. Restrict the diagnostic to
	# the central 80% so a selected texel represents terrain/mountain occlusion.
	var row_start: int = max(1, ceili(float(image.get_height()) * 0.15))
	var row_end: int = min(image.get_height() - 1, floori(float(image.get_height()) * 0.85))
	var best_value := -1.0
	var best := Vector2i(image.get_width() / 2, image.get_height() / 2)
	var shadow_pixel_count := 0
	for y in range(row_start, row_end):
		for x in image.get_width():
			var value := image.get_pixel(x, y).r
			if value > 0.001: shadow_pixel_count += 1
			if value > best_value:
				best_value = value
				best = Vector2i(x, y)
	return {"found": best_value > 0.001, "uv": Vector2((float(best.x) + 0.5) / image.get_width(), (float(best.y) + 0.5) / image.get_height()), "value": best_value, "interior_row_start": row_start, "interior_row_end": row_end, "interior_shadow_pixel_count": shadow_pixel_count, "interior_shadow_max": best_value}

func _write_inconclusive_report(image: Image, selected: Dictionary) -> void:
	var file := FileAccess.open(OUTPUT + "/report.json", FileAccess.WRITE)
	if file == null: return
	file.store_string(JSON.stringify({
		"status": "inconclusive",
		"reason": "The active mask contained no materially shadowed receiver in its central terrain rows; no mountain-shadow visual claim was made.",
		"mask": {"width": image.get_width(), "height": image.get_height(), "interior_row_start": selected.interior_row_start, "interior_row_end": selected.interior_row_end, "interior_shadow_pixel_count": selected.interior_shadow_pixel_count, "interior_shadow_max": selected.interior_shadow_max}
	}, "\t"))
	file.close()

func _freeze_camera_drivers(camera: Camera3D) -> void:
	var explorer := _scene.get_node_or_null("Explorer")
	if explorer != null:
		explorer.set_process(false)
		explorer.set_physics_process(false)
	var day_night := _scene.get_node_or_null("RingDayNightCycle")
	if day_night != null: day_night.set_process(false)
	camera.set_process(false)

func _freeze_pair_motion() -> void:
	# Paired screenshots must not advance cloud/weather animation or LOD focus.
	for path in ["SunshineCloudsDriver", "CloudProviderController", "RingCloudLayer", "RingVolumetricClouds", "LocalVolumetricCloudRenderer", "LocalWeatherEffects", "ScaledFarViewport"]:
		var node := _scene.get_node_or_null(path)
		if node != null:
			node.set_process(false)
			node.set_physics_process(false)

func _capture(label: String) -> Image:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image == null or image.is_empty() or image.save_png(ProjectSettings.globalize_path(OUTPUT + "/" + label + ".png")) != OK:
		_fail("screenshot failed: " + label)
		return null
	return image

func _luminance_stats(image: Image) -> Dictionary:
	var total := 0.0
	var count := 0
	for y in range(0, image.get_height(), 8):
		for x in range(0, image.get_width(), 8):
			total += image.get_pixel(x, y).srgb_to_linear().get_luminance()
			count += 1
	return {"mean_luminance": total / max(1, count), "samples": count}

func _mean_luminance_delta(a: Image, b: Image) -> float:
	var total := 0.0
	var count := 0
	for y in range(0, min(a.get_height(), b.get_height()), 8):
		for x in range(0, min(a.get_width(), b.get_width()), 8):
			total += absf(a.get_pixel(x, y).srgb_to_linear().get_luminance() - b.get_pixel(x, y).srgb_to_linear().get_luminance())
			count += 1
	return total / max(1, count)

func _receiver_roi_stats(disabled: Image, enabled: Image) -> Dictionary:
	# The radial top-down diagnostic puts its intended receiver near screen
	# center. Use a compact middle ROI so the HUD and horizon do not dominate.
	var x0 := disabled.get_width() * 3 / 8
	var x1 := disabled.get_width() * 5 / 8
	var y0 := disabled.get_height() * 3 / 8
	var y1 := disabled.get_height() * 5 / 8
	var off_total := 0.0
	var on_total := 0.0
	var count := 0
	for y in range(y0, y1, 4):
		for x in range(x0, x1, 4):
			off_total += disabled.get_pixel(x, y).srgb_to_linear().get_luminance()
			on_total += enabled.get_pixel(x, y).srgb_to_linear().get_luminance()
			count += 1
	var off_mean: float = off_total / max(1, count)
	var on_mean: float = on_total / max(1, count)
	return {"disabled_mean_luminance": off_mean, "enabled_mean_luminance": on_mean, "delta": on_mean - off_mean, "enabled_is_darker": on_mean < off_mean - 0.0001, "samples": count, "rect": {"x": x0, "y": y0, "width": x1 - x0, "height": y1 - y0}}

func _fail(reason: String) -> void:
	if _failed: return
	_failed = true
	push_error("CERTIFIED_TERRAIN_MACRO_SHADOW_VISUAL=FAIL " + reason)
	if is_instance_valid(_scene): _scene.queue_free()
	quit(1)
