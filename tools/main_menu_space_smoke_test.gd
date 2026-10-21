extends SceneTree

const MENU := preload("res://scenes/StartMenu.tscn")
var failed: bool = false
var watchdog: Timer

class FakeWorldSession extends Node:
	var mutation_calls: int = 0
	func slot_exists(slot: int) -> bool: return slot == 1
	func get_most_recent_slot() -> int: return 1
	func get_slot_metadata(_slot: int) -> Dictionary:
		return {"name": "Preview fixture", "generation_settings": {"terrain_seed": 1337, "radius_km": 5000, "ring_width_km": 318}}
	func select_slot(_slot: int) -> bool: mutation_calls += 1; return false
	func copy_slot(_source: int, _target: int) -> bool: mutation_calls += 1; return false
	func delete_slot(_slot: int) -> bool: mutation_calls += 1; return false
	func request_load_selected_world() -> bool: mutation_calls += 1; return false
	func rename_slot(_slot: int, _name: String) -> bool: mutation_calls += 1; return false
	func create_world(_slot: int, _name: String) -> bool: mutation_calls += 1; return false
	func request_new_world(_request: Dictionary) -> void: mutation_calls += 1
	func get_slot_thumbnail_path(_slot: int) -> String: return ""
	func begin_play_session(_world: Node) -> void: pass
	func end_play_session() -> void: pass

func _initialize() -> void:
	watchdog = Timer.new()
	watchdog.one_shot = true
	watchdog.wait_time = 240.0
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout"))
	call_deferred("_run")

func _run() -> void:
	root.add_child(watchdog)
	watchdog.start()
	var max_fps_before: int = Engine.max_fps
	var sunshine_source: Resource = load("res://resources/clouds/sunshine_ring_local.tres")
	var source_cloud_floor: float = sunshine_source.get("cloud_floor")
	var source_cloud_ceiling: float = sunshine_source.get("cloud_ceiling")
	var source_resolution: int = sunshine_source.get("resolution_scale")
	var source_fog: float = sunshine_source.get("use_environment_fog")
	var quality_keys := ["rendering/anti_aliasing/quality/screen_space_aa", "rendering/environment/screen_space_reflection/half_size", "rendering/lights_and_shadows/directional_shadow/size"]
	var quality_before: Array = []
	for key: String in quality_keys:
		quality_before.append(ProjectSettings.get_setting(key))
	var original_session: Node = root.get_node_or_null("WorldSession")
	if original_session != null:
		root.remove_child(original_session)
	var fake_session := FakeWorldSession.new()
	fake_session.name = "WorldSession"
	root.add_child(fake_session)
	var menu: Control = MENU.instantiate()
	menu.backdrop_seed = 1337
	menu.freeze_backdrop = true
	# User-edited project defaults must not affect deterministic visual fixtures,
	# and tuning regressions must never write the real menu preset.
	menu.use_saved_menu_defaults = false
	menu.menu_defaults_path = "res://.godot/menu_tuning_fixture.tres"
	root.add_child(menu)
	_require(menu.main_panel.visible, "main panel is not visible")
	_require(menu.continue_button != null and menu.continue_button.visible and menu.continue_subline.text.begins_with("Preview fixture"), "continue button missing for the most recent slot")
	_require(menu.continue_button.has_focus(), "continue button did not take initial focus")
	var enter_button: Button = _find_button(menu, "ENTER RINGWORLD")
	_require(enter_button != null, "missing enter button")
	if enter_button == null:
		return
	enter_button.pressed.emit()
	_require(menu.slot_buttons.size() == 3, "expected three slot buttons")
	if menu.slot_buttons.size() != 3:
		return
	_require(menu.worlds_panel.visible and not menu.main_panel.visible, "worlds panel not visible")
	_require(menu.slot_buttons[0].has_focus(), "most recent card header did not take focus")
	_require(menu.slot_buttons[0].get_node(menu.slot_buttons[0].focus_neighbor_bottom) == menu.slot_buttons[1], "card focus neighbor does not reach the next card")
	menu.slot_buttons[1].pressed.emit()
	_require(menu.new_world_panel.visible and not menu.worlds_panel.visible, "empty slot did not open the new world screen")
	var first_seed: String = menu.new_world_seed_edit.text
	_require(not first_seed.is_empty() and first_seed.is_valid_int(), "new world seed missing")
	menu.call("_reroll_seed")
	_require(menu.new_world_seed_edit.text != first_seed, "reroll did not change the seed")
	_cancel(menu)
	_require(menu.worlds_panel.visible and not menu.new_world_panel.visible, "cancel did not return from new world to worlds")
	menu.slot_buttons[0].pressed.emit()
	_require(menu.world_actions_panel.visible, "world actions not visible")
	_require(menu.world_actions_panel.get_parent() == menu.slot_buttons[0].get_parent(), "actions are not inside the selected card")
	var duplicate_button: Button = _find_button(menu, "DUPLICATE WORLD")
	_require(duplicate_button != null, "missing duplicate button")
	if duplicate_button == null:
		return
	duplicate_button.pressed.emit()
	_require(menu.copy_targets.visible and menu.copy_targets.get_child_count() == 2, "copy targets missing")
	_cancel(menu)
	_require(not menu.copy_targets.visible and menu.world_actions_panel.visible, "cancel did not close copy targets only")
	_find_button(menu, "RENAME").pressed.emit()
	_require(menu.rename_row.visible, "rename row not shown")
	_cancel(menu)
	_require(not menu.rename_row.visible, "cancel did not hide the rename row")
	_find_button(menu, "DELETE WORLD").pressed.emit()
	_require(menu.delete_modal.visible and menu.delete_cancel_button.has_focus(), "delete modal not shown with cancel focused")
	menu.call("_on_delete_hold_down")
	menu.call("_process", 0.75)
	_require(absf(menu.delete_hold_fill.scale.x - 0.5) < 0.01, "hold fill did not advance")
	menu.call("_on_delete_hold_up")
	_require(menu.delete_hold_fill.scale.x < 0.001, "releasing did not reset the hold")
	_cancel(menu)
	_require(not menu.delete_modal.visible, "cancel did not close the delete modal")
	_cancel(menu)
	_require(menu.worlds_panel.visible and not menu.world_actions_panel.visible, "cancel did not collapse the card")
	_cancel(menu)
	_require(menu.main_panel.visible and not menu.worlds_panel.visible, "cancel did not return to main")
	menu.call("_show_main_menu")
	_require(fake_session.mutation_calls == 0, "navigation mutated a world")
	var backdrop: Node = menu.get_node_or_null("SpaceBackdrop")
	if backdrop == null:
		_fail("missing space backdrop")
		return
	# Fixture speed checks measure full-speed travel; the ease-in ramp has its own check below.
	var fixture_settings: Resource = backdrop.call("GetMenuTuningSettings") as Resource
	fixture_settings.set("MotionEaseInSeconds", 0.0)
	backdrop.call("ApplyMenuTuningSettings", fixture_settings)
	var early_progress: float = backdrop.call("GetBackdropProgress")
	_require(early_progress >= 0.0 and early_progress <= 1.0, "backdrop progress out of range")
	if "--render-1440" in OS.get_cmdline_user_args():
		# Windows can shrink a requested window to its work area. Fix only the
		# test's isolated 3D target, so the measured workload is truly 1440p.
		await process_frame
		await process_frame
		(backdrop.get_node("MenuFlyoverViewport") as SubViewport).size = Vector2i(2560, 1440)
	var deadline: int = Time.get_ticks_msec() + 120000
	var next_progress: int = Time.get_ticks_msec() + 10000
	while not bool(backdrop.call("IsBackdropReady")) and Time.get_ticks_msec() < deadline:
		if Time.get_ticks_msec() >= next_progress:
			print("MAIN_MENU_SPACE_WAIT ", backdrop.call("GetBackdropDiagnostics"))
			next_progress += 10000
		await process_frame
	if not bool(backdrop.call("IsBackdropReady")):
		_fail("backdrop not ready")
		return
	_require(is_equal_approx(float(backdrop.call("GetBackdropProgress")), 1.0), "ready backdrop progress is not 1")
	var diagnostics: Dictionary = backdrop.call("GetBackdropDiagnostics")
	print("MAIN_MENU_SPACE_DIAGNOSTICS ", diagnostics)
	_require(int(diagnostics.get("seed", -1)) == 1337, "seed mismatch")
	_require(int(diagnostics.get("terrain_sample_count", 0)) == 3149313, "sample count mismatch")
	_require(int(diagnostics.get("terrain_cache_rows", 0)) == 2049 and int(diagnostics.get("terrain_cache_columns", 0)) == 1537 and int(diagnostics.get("terrain_spacing_meters", 0)) == 125, "cache dimensions mismatch")
	_require(int(diagnostics.get("resident_rows", 0)) == 2049, "initial cache not resident")
	_require(int(diagnostics.get("far_sample_count", 0)) == 133120 and bool(diagnostics.get("far_ready", false)), "far terrain not ready")
	_require(int(diagnostics.get("mesh_count", 0)) == 2 and int(diagnostics.get("near_triangles", 0)) == 6291456 and int(diagnostics.get("far_triangles", 0)) == 262144 and int(diagnostics.get("triangle_count", 0)) == 6553600, "terrain grid mismatch")
	_require(Engine.max_fps == max_fps_before, "menu changed max fps")
	for index: int in quality_keys.size():
		_require(ProjectSettings.get_setting(quality_keys[index]) == quality_before[index], "menu changed quality setting")
	var viewport: SubViewport = backdrop.get_node_or_null("MenuFlyoverViewport") as SubViewport
	_require(viewport != null and viewport.own_world_3d and viewport.msaa_3d == Viewport.MSAA_4X, "backdrop viewport contract mismatch")
	if viewport == null:
		return
	var frozen_camera: Variant = diagnostics.get("camera_position", Vector3.ZERO)
	var camera := viewport.get_camera_3d()
	_require(camera != null and (-camera.global_basis.z).z < 0.0 and (-camera.global_basis.z).y < 0.0, "flyover camera is not forward -Z/down")
	_require(abs(float(diagnostics.get("altitude_m", 0)) - 20000.0) < 100.0 and int(diagnostics.get("speed_mps", 0)) == 5000, "flyover camera contract mismatch")
	await create_timer(0.2).timeout
	var frozen_after: Dictionary = backdrop.call("GetBackdropDiagnostics")
	_require((frozen_camera as Vector3).distance_to(frozen_after.get("camera_position", Vector3.ZERO) as Vector3) < 0.001 and float(frozen_after.get("travel_m", -1.0)) == float(diagnostics.get("travel_m", -2.0)), "frozen flyover moved")
	_require(absf(float(frozen_after.get("effective_sun_elevation_degrees", 0.0)) - 49.51) < 0.001, "frozen flyover drifted the sun")
	var cloud_layer: Node = backdrop.get_node_or_null("MenuFlyoverSunshineLayer")
	_require(cloud_layer != null, "missing Sunshine menu adapter")
	var cloud_stats: Dictionary = diagnostics.get("cloud", {})
	_require(str(cloud_stats.get("provider", "")) == "SunshineClouds2" and int(cloud_stats.get("cloud_resolution_scale", -1)) == 1, "Sunshine provider contract mismatch")
	_require(cloud_stats.get("shadow_resolution", Vector2i.ZERO) == Vector2i(512, 512) and float(cloud_stats.get("cloud_floor_meters", 0)) == 2200.0 and float(cloud_stats.get("cloud_ceiling_meters", 0)) == 6000.0, "Sunshine shadow/cloud altitude mismatch")
	if RenderingServer.get_rendering_device() != null:
		_require(int(cloud_stats.get("actual_effect_callback_serial", 0)) > 0 and bool(cloud_stats.get("shadow_ready", false)), "Sunshine GPU shadow was not published")
	_require(source_cloud_floor == float(sunshine_source.get("cloud_floor")) and source_cloud_ceiling == float(sunshine_source.get("cloud_ceiling")) and source_resolution == int(sunshine_source.get("resolution_scale")) and source_fog == float(sunshine_source.get("use_environment_fog")), "menu mutated shared Sunshine resource")
	var world_environment := viewport.get_node_or_null("MenuFlyoverEnvironment") as WorldEnvironment
	_require(world_environment != null and world_environment.compositor != null and world_environment.compositor.compositor_effects.size() == 1, "menu compositor ownership mismatch")
	if world_environment != null and world_environment.compositor != null:
		_require(world_environment.compositor.compositor_effects[0].get_script().resource_path.ends_with("SunshineClouds.gd"), "menu compositor is not Sunshine")
		_require(world_environment.compositor.compositor_effects[0].effect_callback_type == CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_TRANSPARENT, "Sunshine must composite before the menu MSAA resolve")
	_require(int(diagnostics.get("route_selection_samples", 0)) == 160 and int(diagnostics.get("route_start_m", -1)) % 125 == 0, "route selection contract mismatch")
	var shadow_started_msec: int = Time.get_ticks_msec()
	backdrop.set("FreezeMotion", false)
	await create_timer(1.0).timeout
	var moving_after: Dictionary = backdrop.call("GetBackdropDiagnostics")
	var travel_delta: float = float(moving_after.get("travel_m", 0.0)) - float(frozen_after.get("travel_m", 0.0))
	_require(travel_delta > 4700.0 and travel_delta < 5300.0, "unfrozen flyover speed invalid")
	_require(int(moving_after.get("terrain_sample_count", 0)) - int(frozen_after.get("terrain_sample_count", 0)) < 160000, "motion rebuilt terrain")
	var render_updates: int = int(moving_after.get("render_update_count", 0)) - int(frozen_after.get("render_update_count", 0))
	_require(render_updates > 0 and render_updates <= 65, "backdrop exceeded its local render-update budget")
	var moving_cloud: Dictionary = moving_after.get("cloud", {})
	var shadow_elapsed_sec: float = (Time.get_ticks_msec() - shadow_started_msec) * .001
	_require(int(moving_cloud.get("published_dispatch_count", 0)) - int(cloud_stats.get("published_dispatch_count", 0)) <= int(ceil(shadow_elapsed_sec * 5.0)) + 1, "cloud shadow update rate is unbounded")
	backdrop.set("FreezeMotion", true)
	deadline = Time.get_ticks_msec() + 30000
	while int(backdrop.call("GetBackdropDiagnostics").get("queuecount", 1)) != 0 and Time.get_ticks_msec() < deadline:
		await process_frame
	_require(int(backdrop.call("GetBackdropDiagnostics").get("queuecount", 1)) == 0, "cache queue did not settle")
	# Screen camera poses: FreezeMotion snaps, diagnostics reflect the pose, and "main" restores fixture values.
	var pose_main: Dictionary = backdrop.call("GetBackdropDiagnostics")
	_require(str(backdrop.call("GetCameraPose")) == "main" and str(pose_main.get("camera_pose", "")) == "main" and float(pose_main.get("pose_blend", 1.0)) == 0.0, "default camera pose is not main")
	backdrop.call("SetCameraPose", "worlds")
	var pose_worlds: Dictionary = backdrop.call("GetBackdropDiagnostics")
	_require(str(pose_worlds.get("camera_pose", "")) == "worlds" and float(pose_worlds.get("pose_blend", 1.0)) == 0.0, "frozen worlds pose did not snap")
	_require(float(pose_worlds.get("pose_altitude_km", 0.0)) > float(pose_main.get("pose_altitude_km", 0.0)) and float(pose_worlds.get("pitch_degrees", 0.0)) < float(pose_main.get("pitch_degrees", 0.0)), "worlds pose is not higher and flatter than main")
	backdrop.call("SetCameraPose", "detail")
	var pose_detail: Dictionary = backdrop.call("GetBackdropDiagnostics")
	_require(float(pose_detail.get("fov_degrees", 99.0)) < float(pose_main.get("fov_degrees", 0.0)) and float(pose_detail.get("pose_altitude_km", 99.0)) < float(pose_main.get("pose_altitude_km", 0.0)), "detail pose is not lower and narrower")
	backdrop.call("SetCameraPose", "bogus")
	_require(str(backdrop.call("GetCameraPose")) == "main", "unknown pose did not fall back to main")
	var pose_restored: Dictionary = backdrop.call("GetBackdropDiagnostics")
	_require(is_equal_approx(float(pose_restored.get("pose_altitude_km", 0.0)), float(pose_main.get("pose_altitude_km", -1.0))) and is_equal_approx(float(pose_restored.get("pitch_degrees", 0.0)), float(pose_main.get("pitch_degrees", -1.0))) and is_equal_approx(float(pose_restored.get("fov_degrees", 0.0)), float(pose_main.get("fov_degrees", -1.0))), "main pose did not restore fixture values")
	if "--resize-check" in OS.get_cmdline_user_args() and RenderingServer.get_rendering_device() != null:
		var original_size: Vector2i = viewport.size
		for target_size: Vector2i in [original_size - Vector2i(16, 8), original_size]:
			var previous_wrapper: int = int(cloud_layer.call("GetCloudDiagnostics").get("shadow_wrapper_instance_id", 0))
			viewport.size = target_size
			await create_timer(0.4).timeout
			var resized_cloud: Dictionary = cloud_layer.call("GetCloudDiagnostics")
			_require(bool(resized_cloud.get("shadow_ready", false)) and int(resized_cloud.get("shadow_wrapper_instance_id", 0)) != previous_wrapper, "resized menu kept a retired cloud-shadow texture")
	if "--benchmark" in OS.get_cmdline_user_args():
		await _benchmark_viewports(backdrop, viewport)
	if "--extended" in OS.get_cmdline_user_args():
		backdrop.set("FreezeMotion", false)
		backdrop.call("AdvanceFlyoverForTest", 60.0)
		backdrop.set("FreezeMotion", true)
		for frame: int in 3:
			await process_frame
		var extended_deadline: int = Time.get_ticks_msec() + 120000
		while int(backdrop.call("GetBackdropDiagnostics").get("queuecount", 1)) != 0 and Time.get_ticks_msec() < extended_deadline:
			var queued: Dictionary = backdrop.call("GetBackdropDiagnostics")
			_require(int(queued.get("queuecount", 0)) <= 2049, "queue exceeded cache bounds")
			await process_frame
		var extended: Dictionary = backdrop.call("GetBackdropDiagnostics")
		_require(int(extended.get("queuecount", 1)) == 0 and int(extended.get("resident_rows", 0)) == 2049, "extended cache residency failed")
		_require(is_finite(float(extended.get("canonicalaround", NAN))), "extended canonical around is not finite")
		var circumference_m: float = float(extended.get("circumference_m", 0.0))
		var current_canonical: float = float(extended.get("canonicalaround", 0.0))
		var seam_seconds: float = (circumference_m - current_canonical + 2500.0) / 5000.0
		backdrop.set("FreezeMotion", false)
		backdrop.call("AdvanceFlyoverForTest", seam_seconds)
		backdrop.set("FreezeMotion", true)
		for frame: int in 3:
			await process_frame
		var seam_deadline: int = Time.get_ticks_msec() + 120000
		while int(backdrop.call("GetBackdropDiagnostics").get("queuecount", 1)) != 0 and Time.get_ticks_msec() < seam_deadline:
			var seam_queued: Dictionary = backdrop.call("GetBackdropDiagnostics")
			_require(int(seam_queued.get("queuecount", 0)) <= 2049, "seam queue exceeded cache bounds")
			await process_frame
		var seam: Dictionary = backdrop.call("GetBackdropDiagnostics")
		var seam_canonical: float = float(seam.get("canonicalaround", NAN))
		_require(seam_canonical >= 0.0 and seam_canonical < circumference_m and int(seam.get("resident_rows", 0)) == 2049, "circumference seam did not restore rows")
		_require(abs(seam_canonical - 2500.0) < 600.0, "seam route did not wrap near 2500m")
	_require(not _contains_runtime(menu), "menu created world runtime")
	if "--capture" in OS.get_cmdline_user_args():
		_require(RenderingServer.get_rendering_device() != null, "capture requires a real GPU")
		if RenderingServer.get_rendering_device() == null:
			return
		var directory_error: Error = DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/menu_space"))
		_require(directory_error == OK, "could not create capture directory")
		await RenderingServer.frame_post_draw
		var prefix := "res://.godot/menu_space/menu_%dx%d_seed1337" % [viewport.size.x, viewport.size.y]
		_require(root.get_texture().get_image().save_png(prefix + "_main.png") == OK, "main capture failed")
		_require(viewport.get_texture().get_image().save_png(prefix + "_backdrop.png") == OK, "raw capture failed")
		if cloud_layer != null:
			var effect: CompositorEffect = cloud_layer.get("Effect") as CompositorEffect
			var cloud_texture: Texture2D = cloud_layer.call("GetCloudTexture") as Texture2D
			if cloud_texture != null:
				await _capture_gpu_texture(cloud_texture, prefix + "_sunshine_clouds.png", 0)
				await _capture_gpu_texture(cloud_texture, prefix + "_sunshine_alpha.png", 1)
			if effect != null:
				var shadow_texture: Texture2D = effect.call("get_gpu_cloud_shadow_texture", 0) as Texture2D
				if shadow_texture != null:
					await _capture_gpu_texture(shadow_texture, prefix + "_sunshine_shadow.png", 2)
				if "--diagnostic-clouds" in OS.get_cmdline_user_args():
					for diagnostic: String in ["diagnostic_raw_density_matte", "diagnostic_depth_density_matte"]:
						effect.set(diagnostic, true)
						await create_timer(0.2).timeout
						await RenderingServer.frame_post_draw
						var matte: Image = viewport.get_texture().get_image()
						_require(matte.save_png(prefix + "_" + diagnostic + ".png") == OK, "cloud matte capture failed")
						_require_cloud_matte(matte)
						effect.set(diagnostic, false)
					await create_timer(0.2).timeout
		if "--capture-atlas" in OS.get_cmdline_user_args():
			var terrain_mesh: MeshInstance3D = viewport.get_node("MenuFlyoverTerrain") as MeshInstance3D
			var terrain_material: ShaderMaterial = terrain_mesh.material_override as ShaderMaterial
			var color_atlas: Image = (terrain_material.get_shader_parameter("color_texture") as Texture2D).get_image()
			color_atlas.convert(Image.FORMAT_RGB8)
			_require(color_atlas.save_png(prefix + "_terrain_color_atlas.png") == OK, "terrain color capture failed")
		menu.call("_show_worlds")
		# Let the panel motion settle so the capture shows the resting layout.
		await create_timer(0.6).timeout
		await RenderingServer.frame_post_draw
		_require(root.get_texture().get_image().save_png("res://.godot/menu_space/world_slots.png") == OK, "world slots capture failed")
	if "--tuning-check" in OS.get_cmdline_user_args():
		# Restore the default ease-in so the drawer's initial values match the backdrop.
		fixture_settings.set("MotionEaseInSeconds", 2.5)
		backdrop.call("ApplyMenuTuningSettings", fixture_settings)
		var tuning_checks: RefCounted = load("res://tools/menu_flyover_tuning_checks.gd").new()
		_require(await tuning_checks.run(menu, "--capture" in OS.get_cmdline_user_args()), "menu tuning regression failed")
	_require(fake_session.mutation_calls == 0, "menu modified the save fixture")
	menu.queue_free()
	await process_frame
	root.remove_child(fake_session)
	fake_session.queue_free()
	if original_session != null:
		root.add_child(original_session)
	var abort_backdrop: Control = load("res://scripts/ui/RingMenuSpaceBackdrop.cs").new()
	abort_backdrop.Seed = 7331
	abort_backdrop.UseSavedMenuDefaults = false
	root.add_child(abort_backdrop)
	await process_frame
	await process_frame
	_require(not bool(abort_backdrop.call("IsBackdropReady")), "abort fixture completed too early")
	abort_backdrop.queue_free()
	await process_frame
	await process_frame
	_require(not is_instance_valid(abort_backdrop), "aborted backdrop was not released")
	# The fixture itself loads an unused source CompositorEffect for the
	# immutability checks. The plugin initializes that resource's GPU pipelines
	# even without a viewport. Release only this fixture's source before exit.
	if RenderingServer.get_rendering_device() != null:
		RenderingServer.call_on_render_thread(sunshine_source.call.bind("clear_compute"))
		for frame: int in 8:
			await RenderingServer.frame_post_draw
	watchdog.stop()
	if failed:
		quit(1)
	else:
		print("MAIN_MENU_SPACE_SMOKE=PASS")
		quit(0)

# Plugin RD textures are sampleable but intentionally have no COPY_FROM usage.
# Render the diagnostic into an ordinary viewport instead of reading them back.
func _capture_gpu_texture(texture: Texture2D, path: String, channel: int) -> void:
	var capture := SubViewport.new()
	capture.size = Vector2i(texture.get_width(), texture.get_height())
	capture.disable_3d = true
	capture.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var rectangle := TextureRect.new()
	rectangle.texture = texture
	rectangle.size = Vector2(capture.size)
	var shader := Shader.new()
	shader.code = "shader_type canvas_item; render_mode blend_disabled; uniform int channel = 0; void fragment() { vec4 c = texture(TEXTURE, UV); vec3 v = c.rgb; if (channel == 1) { v = vec3(c.a); } else if (channel == 2) { v = vec3(1.0 - (1.0 - c.r) * (1.0 - c.g)); } COLOR = vec4(v, 1.0); }"
	var material := ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("channel", channel)
	rectangle.material = material
	capture.add_child(rectangle)
	root.add_child(capture)
	await process_frame
	await RenderingServer.frame_post_draw
	var captured: Image = capture.get_texture().get_image()
	_require(captured != null and not captured.is_empty(), "GPU diagnostic capture was empty")
	if captured != null and not captured.is_empty():
		_require(captured.save_png(path) == OK, "GPU diagnostic capture failed: " + path)
		if channel == 1:
			var max_alpha: float = 0.0
			var sum_alpha: float = 0.0
			var samples: int = 0
			for y: int in range(0, captured.get_height(), 8):
				for x: int in range(0, captured.get_width(), 8):
					var alpha: float = captured.get_pixel(x, y).r
					max_alpha = maxf(max_alpha, alpha)
					sum_alpha += alpha
					samples += 1
			print("MENU_SUNSHINE_ALPHA ", {"max": max_alpha, "mean": sum_alpha / max(1, samples)})
	capture.queue_free()
	await process_frame

func _contains_runtime(node: Node) -> bool:
	var script: Script = node.get_script()
	var path: String = script.resource_path if script != null else ""
	if node is PhysicsBody3D or path.contains("VoxelChunkGridManager") or path.contains("Explorer") or path.contains("RingSunshineCloudBridge") or path.contains("SunshineCloudsDriver") or path.contains("Vegetation") or path.contains("WorldPhysics"):
		return true
	for child: Node in node.get_children():
		if _contains_runtime(child):
			return true
	return false

func _require_cloud_matte(matte: Image) -> void:
	# A populated internal cloud texture is insufficient: the MSAA display stage
	# must actually reach the final viewport, which this grayscale matte checks.
	var samples: int = 0
	var gray: int = 0
	var cloudy: int = 0
	var clear: int = 0
	for y: int in range(0, matte.get_height(), 16):
		for x: int in range(0, matte.get_width(), 16):
			var color: Color = matte.get_pixel(x, y)
			samples += 1
			if maxf(absf(color.r - color.g), absf(color.g - color.b)) < 0.02:
				gray += 1
			if color.r > 0.3: cloudy += 1
			if color.r < 0.1: clear += 1
	_require(gray > samples * 0.95 and cloudy > samples * 0.02 and clear > samples * 0.1, "Sunshine density matte did not reach final viewport")

func _cancel(menu: Control) -> void:
	var event := InputEventAction.new()
	event.action = "ui_cancel"
	event.pressed = true
	menu.call("_unhandled_input", event)

func _find_button(node: Node, prefix: String) -> Button:
	for child: Node in node.find_children("*", "Button", true, false):
		if child.text.begins_with(prefix):
			return child
	return null

func _benchmark_viewports(backdrop: Node, main_viewport: SubViewport) -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("benchmark requires a real GPU")
		return
	var viewports: Array[Viewport] = [root, main_viewport]
	for viewport: Viewport in viewports:
		RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), true)
	backdrop.set("FreezeMotion", false)
	var warmup_end := Time.get_ticks_msec() + 1000
	while Time.get_ticks_msec() < warmup_end: await process_frame
	var walls: Array[float] = []
	var gpu: Dictionary = {}
	for viewport: Viewport in viewports:
		var samples: Array[float] = []
		gpu[str(viewport.name)] = samples
	var benchmark_started: int = Time.get_ticks_msec()
	var before: Dictionary = backdrop.call("GetBackdropDiagnostics")
	var updates_before: int = int(before.get("render_update_count", 0))
	var callbacks_before: int = int(before.get("cloud", {}).get("actual_effect_callback_serial", 0))
	var end := Time.get_ticks_msec() + 15000
	while Time.get_ticks_msec() < end:
		var start := Time.get_ticks_usec()
		await process_frame
		walls.append((Time.get_ticks_usec() - start) * .001)
		for viewport: Viewport in viewports:
			var measured: float = RenderingServer.viewport_get_measured_render_time_gpu(viewport.get_viewport_rid())
			if measured > 0.0:
				(gpu[str(viewport.name)] as Array).append(measured)
	backdrop.set("FreezeMotion", true)
	for viewport: Viewport in viewports:
		RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), false)
	var gpu_stats := {}
	for key: String in gpu:
		var values: Array[float] = gpu[key]
		gpu_stats[key] = {"available": not values.is_empty(), "samples": values.size(), "median_ms": _percentile(values, .5) if not values.is_empty() else null, "p95_ms": _percentile(values, .95) if not values.is_empty() else null}
	var updates_after: int = int(backdrop.call("GetBackdropDiagnostics").get("render_update_count", 0))
	var final_diagnostics: Dictionary = backdrop.call("GetBackdropDiagnostics")
	print("MENU_FLYOVER_BENCHMARK ", JSON.stringify({"viewport_size": str(main_viewport.size), "wall_median_ms": _percentile(walls, .5), "wall_p95_ms": _percentile(walls, .95), "gpu_ms": gpu_stats, "render_updates": updates_after - updates_before, "actual_render_callbacks": int(final_diagnostics.get("cloud", {}).get("actual_effect_callback_serial", 0)) - callbacks_before, "queuecount": final_diagnostics.get("queuecount", -1), "resident_rows": final_diagnostics.get("resident_rows", -1), "elapsed_ms": Time.get_ticks_msec() - benchmark_started}))

func _percentile(values: Array[float], fraction: float) -> float:
	if values.is_empty(): return 0.0
	values.sort()
	return values[clamp(int(round((values.size() - 1) * fraction)), 0, values.size() - 1)]

func _require(condition: bool, message: String) -> void:
	if not condition:
		_fail(message)

func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	push_error("MAIN_MENU_SPACE_SMOKE=FAIL " + message)
	quit(1)
