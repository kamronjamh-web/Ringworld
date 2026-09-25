extends SceneTree

var _finished := false

func _init() -> void:
	var timeout := create_timer(30.0)
	timeout.timeout.connect(func(): _fail("timed out waiting for Terrain3D pilot"))
	var fixture := "res://.godot/terrain3d_surface_pilot_smoke_%d" % Time.get_ticks_usec()
	var scene: PackedScene = load("res://scenes/Terrain3DSurfacePilot.tscn")
	var pilot := scene.instantiate()
	pilot.SurfaceSavePathOverride = fixture.path_join("surface_terrain3d_edits_v1.json")
	pilot.get_node("LiveTuning").InstallSurfaceTerrainDefaults(fixture.path_join("settings.tres"))
	root.add_child(pilot)
	await process_frame
	await physics_frame
	await process_frame
	var drawer: Node = pilot.get_node("LiveTuning")
	for cycle in range(2):
		drawer.call("SetBenchmarkDrawerOpen", true)
		await process_frame
		drawer.call("SetBenchmarkDrawerOpen", false)
		await process_frame
	if pilot == null or not pilot.has_method("ApplySurfaceBrushAtCanonical"):
		_fail("pilot scene did not load")
		return
	var before: Dictionary = pilot.GetSurfaceDiagnostics()
	if not bool(before.get("addon_ready", false)) or int(before.get("regions", 0)) != 9 or not str(before.get("error", "")).is_empty():
		_fail("Terrain3D renderer was not ready: %s" % before)
		return
	var native_origin := float(pilot.GetNativeHeightAtOrigin())
	if is_nan(native_origin) or absf(native_origin) > 0.35:
		_fail("Terrain3DData height at local field origin disagrees with the anchored field: %s" % native_origin)
		return
	var ray_before := _terrain_ray_y(pilot)
	if is_nan(ray_before):
		_fail("terrain collision ray missed before edit")
		return
	if not bool(pilot.ApplySurfaceBrushAtCanonical(0.0, 159000.0, 2.0)):
		_fail("raise brush was rejected")
		return
	await physics_frame
	await process_frame
	var ray_raised := _terrain_ray_y(pilot)
	if is_nan(ray_raised) or absf((ray_raised - ray_before) - 2.0) > 0.35:
		_fail("terrain collider did not rise by brush amount: %s -> %s" % [ray_before, ray_raised])
		return
	if not bool(pilot.ApplySurfaceBrushAtCanonical(0.0, 159000.0, -2.0)):
		_fail("lower brush was rejected")
		return
	await physics_frame
	await process_frame
	var ray_lowered := _terrain_ray_y(pilot)
	if is_nan(ray_lowered) or absf(ray_lowered - ray_before) > 0.35:
		_fail("terrain collider did not lower back to baseline: %s -> %s" % [ray_raised, ray_lowered])
		return
	pilot.ApplySurfaceBrushAtCanonical(0.0, 159000.0, 2.0)
	# Let ordinary gravity establish a playable ground contact before rebasing.
	await create_timer(3.0).timeout
	if not pilot.get_node("Player").is_on_floor():
		_fail("player did not establish Terrain3D surface contact: %s" % _player_state(pilot))
		return
	pilot.TeleportCanonicalSurface(256.0, 159000.0)
	pilot.get_node("Player").global_position += Vector3(0.375, 1.25, 0.625)
	var precise_before: Dictionary = pilot.GetSurfaceDiagnostics()
	pilot.RecenterSurfaceNow()
	var precise_after: Dictionary = pilot.GetSurfaceDiagnostics()
	if absf(float(precise_after.get("player_around", 0.0)) - float(precise_before.get("player_around", 0.0))) > 0.01 or absf(float(precise_after.get("player_across", 0.0)) - float(precise_before.get("player_across", 0.0))) > 0.01 or absf(float(precise_after.get("player_height", 0.0)) - float(precise_before.get("player_height", 0.0))) > 0.01:
		_fail("recenter lost fractional canonical player position")
		return
	# Cross the normal drift threshold without calling the explicit test hook.
	var frame_before := int(precise_after.get("frame_around", 0))
	pilot.get_node("Player").global_position.x += 160.0
	await process_frame
	await physics_frame
	var frame_after := int(pilot.GetSurfaceDiagnostics().get("frame_around", 0))
	if frame_after == frame_before:
		_fail("automatic Terrain3D window recenter did not occur")
		return
	if not bool(pilot.SaveSurfaceEdits()):
		_fail("surface save failed")
		return
	var after: Dictionary = pilot.GetSurfaceDiagnostics()
	if int(after.get("edits", 0)) <= int(before.get("edits", 0)):
		_fail("surface edit did not persist in field")
		return
	if OS.get_cmdline_user_args().has("--capture") and not OS.has_feature("headless"):
		var captured := await _capture_mound_and_drawer(pilot, fixture)
		if not captured:
			return
	pilot.queue_free()
	await process_frame
	var reloaded := scene.instantiate()
	reloaded.SurfaceSavePathOverride = fixture.path_join("surface_terrain3d_edits_v1.json")
	reloaded.get_node("LiveTuning").InstallSurfaceTerrainDefaults(fixture.path_join("settings.tres"))
	root.add_child(reloaded)
	await process_frame
	await physics_frame
	await process_frame
	var reload_diag: Dictionary = reloaded.GetSurfaceDiagnostics()
	if not bool(reload_diag.get("addon_ready", false)) or int(reload_diag.get("edits", 0)) <= 0:
		_fail("saved surface edit did not reload: %s" % reload_diag)
		return
	var reload_ray := _terrain_ray_y(reloaded)
	if is_nan(reload_ray) or absf(reload_ray - ray_lowered - 2.0) > 0.35:
		_fail("reloaded terrain collider height did not retain edited mound: %s" % reload_ray)
		return
	_finished = true
	print("Terrain3D surface pilot smoke: PASS ", after)
	quit(0)

func _terrain_ray_y(pilot: Node) -> float:
	var player: Node3D = pilot.get_node("Player")
	var query := PhysicsRayQueryParameters3D.create(player.global_position + Vector3.UP * 20.0, player.global_position - Vector3.UP * 40.0, 1)
	query.exclude = [player.get_rid()]
	var hit: Dictionary = pilot.get_world_3d().direct_space_state.intersect_ray(query)
	return float(hit.get("position", Vector3(NAN, NAN, NAN)).y)

func _player_state(pilot: Node) -> Dictionary:
	var player: CharacterBody3D = pilot.get_node("Player")
	return { "position": player.global_position, "velocity": player.velocity, "on_floor": player.is_on_floor(), "mouse_mode": Input.mouse_mode, "diagnostics": pilot.GetSurfaceDiagnostics() }

func _capture_mound_and_drawer(pilot: Node, fixture: String) -> bool:
	pilot.TeleportCanonicalSurface(0.0, 159000.0)
	pilot.set_process(false)
	pilot.set_physics_process(false)
	var camera: Camera3D = pilot.get_node("Player/Head/Camera3D")
	camera.global_position = Vector3(30.0, 25.0, 40.0)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	await RenderingServer.frame_post_draw
	var terrain_path := ProjectSettings.globalize_path(fixture.path_join("terrain3d_surface_pilot.png"))
	if root.get_viewport().get_texture().get_image().save_png(terrain_path) != OK:
		_fail("terrain viewport capture failed")
		return false
	pilot.get_node("LiveTuning").call("SetBenchmarkDrawerOpen", true)
	var editing_section := pilot.get_node("LiveTuning").find_child("SectionSurfaceeditingandcamera", true, false)
	if editing_section == null:
		_fail("surface editing controls were not found in the drawer")
		return false
	editing_section.set("folded", false)
	await process_frame
	await RenderingServer.frame_post_draw
	var drawer_path := ProjectSettings.globalize_path(fixture.path_join("terrain3d_surface_pilot_drawer.png"))
	if root.get_viewport().get_texture().get_image().save_png(drawer_path) != OK:
		_fail("drawer viewport capture failed")
		return false
	print("Terrain3D surface captures: ", terrain_path, " | ", drawer_path)
	return true

func _fail(message: String) -> void:
	if _finished:
		return
	_finished = true
	push_error(message)
	quit(1)
