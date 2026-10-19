extends SceneTree
# Short, matched Vulkan visual capture. Every shot resets the exact canonical
# ground/high-altitude pose and presentation view before temporal settling.
# Run:  godot --path . --script res://tools/atmosphere_visual_test.gd [-- --model=physical] [--copy-to=<absolute dir>]
# --model=physical switches the P2a haze A/B toggle (drawer SetAtmosphereHazeModel) before any capture; the default is Legacy.
# --labels=a,b,c limits captures to labels ending with one of the suffixes (natural profile only).
# --copy-to also writes every capture PNG into the given directory (prefixed by the model) so runs can be compared.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const FIXTURE_AROUND := 16443476.0
const FIXTURE_ACROSS := 38692.0
const SETTLE_FRAMES := 18
var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var drawer: Node
var clock: Node
var graphics: Node
var camera: Camera3D
var grass: Node
var trees: Node
var global_terrain: Node
var placement: RefCounted
var report := {"status":"RUNNING", "captures":[]}
var haze_model_physical := false
var copy_dir := ""
var albedo_overrides := {}
var haze_brightness_override := -1.0
var quick := false
var only_label := ""
var only_labels: PackedStringArray = []

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	for argument in OS.get_cmdline_user_args():
		if argument == "--model=physical": haze_model_physical = true
		elif argument.begins_with("--copy-to="): copy_dir = argument.get_slice("=", 1)
		elif argument.begins_with("--terrain-albedo="): albedo_overrides["ringworld_terrain_albedo_scale"] = float(argument.get_slice("=", 1))
		elif argument.begins_with("--grass-albedo="): albedo_overrides["ringworld_grass_albedo_scale"] = float(argument.get_slice("=", 1))
		elif argument.begins_with("--haze-brightness="): haze_brightness_override = float(argument.get_slice("=", 1))
		elif argument == "--quick": quick = true
		elif argument.begins_with("--only="): only_label = argument.get_slice("=", 1)
		elif argument.begins_with("--labels="): only_labels = argument.get_slice("=", 1).split(",")
	if copy_dir != "": DirAccess.make_dir_recursive_absolute(copy_dir)
	fixture = "res://.godot/atmosphere_visual/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalAtmosphere"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	clock = game.get_node_or_null("RingDayNightCycle")
	graphics = game.get_node_or_null("GraphicsQuality")
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	placement = FIXTURE_HELPER.new()
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	if explorer == null or surface == null or drawer == null or clock == null or graphics == null or camera == null or grass == null or trees == null: return fail("missing gameplay nodes")
	drawer.call("SetAtmosphereEnabled", true)
	drawer.call("SetAtmosphereHazeModel", 1 if haze_model_physical else 0)
	# Tuning overrides: scratch comparisons without touching saved preferences (shader globals / atmosphere value only).
	for global_name in albedo_overrides: RenderingServer.global_shader_parameter_set(str(global_name), float(albedo_overrides[global_name]))
	if haze_brightness_override >= 0.0: drawer.call("SetAtmosphereTuningValue", "HazeBrightness", haze_brightness_override)
	var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	if not bool(diagnostics.get("runtime_surface_generation", false)) or str(diagnostics.get("surface_generation_contract_id", "")) != "surface_runtime_generation_v1": return fail("runtime Terrain3D generation is not active: " + JSON.stringify(diagnostics))
	var ground_height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	if not is_finite(ground_height): return fail("dryland sample height was non-finite")
	# Let normal physics settle the first destination and clear any swimming /
	# underwater state inherited from the launcher spawn before freezing pose.
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground_height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")):
		return fail("dryland fixture retained underwater state")
	# Presentation setters write the canonical transform directly. Disabling the
	# controller after readiness prevents walking/gravity from changing matched
	# ground and high-altitude captures during their temporal settling window.
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	var phases := select_visual_phases()
	for profile in ([[0, "natural"]] if quick or only_label != "" or not only_labels.is_empty() else [[0, "natural"], [1, "cinematic"]]):
		drawer.call("SetAtmospherePreset", int(profile[0]))
		await capture_visible_tuning(str(profile[1]))
		await matched_capture(str(profile[1]) + "_noon", ground_height, phases["noon"], false)
		await matched_capture(str(profile[1]) + "_dusk", ground_height, phases["dusk"], false)
		await matched_capture(str(profile[1]) + "_night", ground_height, phases["night"], false)
		await matched_capture(str(profile[1]) + "_high_alt", ground_height, phases["noon"], true)
		# Horizon rows (P2a): level view and a 2 degree upward view at noon, where distant terrain meets the haze band.
		await matched_capture(str(profile[1]) + "_noon_horizon", ground_height, phases["noon"], false, 0.0)
		await matched_capture(str(profile[1]) + "_noon_up2", ground_height, phases["noon"], false, 2.0)
		await matched_capture(str(profile[1]) + "_dusk_horizon", ground_height, phases["dusk"], false, 0.0)
		# Far arch (P10): local night, looking up the arch at 50 degrees where the far side is sunlit.
		await matched_capture(str(profile[1]) + "_night_arch", ground_height, phases["night"], false, 50.0)
		explorer.call("SetPresentationView", 0.0, -10.0)
	drawer.call("SetAtmospherePreset", 0)
	drawer.call("SetAtmosphereTuningValue", "Density", 0.0)
	await matched_capture("density_zero", ground_height, phases["noon"], false)
	drawer.call("SetAtmosphereEnabled", false)
	await matched_capture("disabled", ground_height, phases["noon"], false)
	drawer.call("SetAtmosphereEnabled", true)
	report.status = "PASS"
	var report_path := fixture.path_join("report.json")
	var file := FileAccess.open(report_path, FileAccess.WRITE)
	if file == null: return fail("could not write report")
	file.store_string(JSON.stringify(report, "\t")); file.close()
	game.queue_free()
	await process_frame
	print("ATMOSPHERE_VISUAL_TEST=PASS fixture=" + fixture)
	quit(0)

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: fail("missing terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)): fail("runtime generation preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate()
	install_isolated_preferences(next)
	launcher.call("_configure_game", next, 5000000.0)
	root.add_child(next); current_scene = next; launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	fail("runtime Terrain3D startup timed out")
	return null

func install_isolated_preferences(next: Node) -> void:
	var target := next.get_node_or_null("LiveTuningDrawer")
	if target == null: return
	for item in [["InstallGrassDefaults","grass.tres"], ["InstallVegetationDefaults","vegetation.tres"], ["InstallWaterDefaults","water.tres"], ["InstallAntiAliasingDefaults","anti_aliasing.tres"], ["InstallShadowDefaults","shadows.tres"], ["InstallSurfaceTerrainDefaults","surface.tres"], ["InstallAtmosphereDefaults","atmosphere.tres"]]:
		target.call(str(item[0]), fixture.path_join(str(item[1])))

func reset_pose(ground_height: float, high_altitude := false) -> void:
	var height := ground_height + (12000.0 if high_altitude else 0.0)
	if not bool(placement.call("Place", explorer, FIXTURE_AROUND, height, FIXTURE_ACROSS)): fail("benchmark canonical placement failed")
	await frames(2)

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = diagnostics.get("streaming", {})
		var native: Dictionary = diagnostics.get("native", {})
		var preparation: Dictionary = native.get("preparation", {})
		var placed: bool = absf(float(diagnostics.get("player_around", NAN)) - FIXTURE_AROUND) < 1.0 and absf(float(diagnostics.get("player_across", NAN)) - FIXTURE_ACROSS) < 1.0
		var grass_ready := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 and int(grass.get("ResidentGrassInstanceCount")) > 0
		var trees_ready := int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("VegetationPendingRenderUploads")) == 0 and int(trees.get("GpuTreeResidentRootCount")) > 0
		var global_ready: bool = global_terrain != null and not bool(global_terrain.get("GlobalSummaryBuildPending")) and int(global_terrain.get("PendingInitialSegmentCount")) == 0 and int(global_terrain.get("PendingSegmentRebuildCount")) == 0 and not bool(global_terrain.get("GlobalSegmentSamplingWorkerPending"))
		var ready := bool(diagnostics.get("ready", false)) and bool(diagnostics.get("visual_coverage", false)) and int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and not bool(streaming.get("packing", false)) and bool(preparation.get("ready", false)) and grass_ready and trees_ready and global_ready
		stable = stable + 1 if placed and ready else 0
		if stable >= 12: return true
	fail("Terrain3D workload did not settle at fixed pose")
	return false

func select_visual_phases() -> Dictionary:
	var noon := 0.0
	var dusk := 0.0
	var night := 0.0
	var highest := -INF
	var lowest := INF
	var horizon_distance := INF
	for step in 48:
		var fraction := float(step) / 48.0
		clock.call("SetCycleFraction", fraction)
		var elevation := float(clock.get("SunElevationDegrees"))
		if elevation > highest: highest = elevation; noon = fraction
		if elevation < lowest: lowest = elevation; night = fraction
		if abs(elevation) < horizon_distance: horizon_distance = abs(elevation); dusk = fraction
	return {"noon":noon, "dusk":dusk, "night":night}

func matched_capture(label: String, ground_height: float, fraction: float, high_altitude: bool, pitch_degrees := -10.0) -> void:
	if only_label != "" and not label.ends_with(only_label): return
	if not only_labels.is_empty():
		var wanted := false
		for suffix in only_labels: wanted = wanted or label.ends_with(suffix)
		if not wanted: return
	elif quick and not (label.ends_with("noon") or label.ends_with("noon_horizon") or label.ends_with("noon_up2")): return
	await reset_pose(ground_height, high_altitude)
	if pitch_degrees != -10.0: explorer.call("SetPresentationView", 0.0, pitch_degrees)
	clock.set("Paused", true)
	clock.call("SetCycleFraction", fraction)
	await frames(SETTLE_FRAMES)
	var atmosphere: Dictionary = graphics.call("GetAtmosphereTuningDiagnostics")
	if not str(atmosphere.get("settings_path", "")).begins_with(fixture): return fail("atmosphere settings escaped fixture path")
	if label.begins_with("natural") and int(atmosphere.get("preset", -1)) != 0: return fail("natural profile was not applied")
	if label.begins_with("cinematic") and int(atmosphere.get("preset", -1)) != 1: return fail("cinematic profile was not applied")
	if label == "density_zero" and abs(float(atmosphere.get("density", NAN))) > 0.0001: return fail("density-zero profile was not applied")
	if label == "disabled" and bool(atmosphere.get("enabled", true)): return fail("runtime haze toggle was not applied")
	var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")):
		return fail("underwater state invalidates atmosphere capture " + label)
	if abs(float(diagnostics.get("player_around", NAN)) - FIXTURE_AROUND) >= 1.0 or abs(float(diagnostics.get("player_across", NAN)) - FIXTURE_ACROSS) >= 1.0: return fail("camera drifted before " + label)
	var expected_height := ground_height + (12000.0 if high_altitude else 0.0)
	if abs(float(diagnostics.get("player_height", NAN)) - expected_height) > 1.0: return fail("canonical height mismatch " + label)
	if high_altitude and abs(float(diagnostics.get("player_height", NAN)) - ground_height) < 11000.0: return fail("high-altitude view did not move 12 km")
	if not is_finite(float(diagnostics.get("player_height", NAN))) or not is_finite(camera.global_position.x) or not is_finite(camera.global_position.y) or not is_finite(camera.global_position.z): return fail("non-finite capture pose " + label)
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	var path := fixture.path_join(label + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(path)) != OK: return fail("capture failed " + label)
	if copy_dir != "": image.save_png(copy_dir.path_join(("physical_" if haze_model_physical else "legacy_") + label + ".png"))
	var cycle_seconds := float(clock.call("GetCycleTimeSeconds"))
	var cycle_full := maxf(1.0, float(clock.get("FullCycleSeconds")))
	var material_snapshot := atmosphere_material_snapshot()
	if not bool(material_snapshot.get("owned_valid", false)): return fail("active Terrain3D material lacks a valid atmosphere ray frame")
	var grid := game.get_node_or_null("ChunkGridManager")
	var eye_height := float(placement.call("ResolveCameraCanonicalHeight", grid, camera))
	if not is_finite(eye_height) or abs(float(material_snapshot["owned"].get("camera_height_meters", NAN)) - eye_height) > 1.0: return fail("Terrain3D ray-frame height differs from canonical eye height")
	if eye_height <= float(diagnostics.get("player_height", NAN)) + 0.5: return fail("camera eye is not above the player feet")
	for kind in ["proxy", "global", "far"]:
		var group: Dictionary = material_snapshot["groups"].get(kind, {})
		if int(group.get("count", 0)) == 0 or not bool(group.get("all_valid", false)): return fail("visible %s terrain material lacks a valid atmosphere ray frame" % kind)
	report.captures.append({"label":label, "path":path, "cycle_fraction":cycle_seconds / cycle_full, "sun_elevation_degrees":clock.get("SunElevationDegrees"), "daylight_factor":clock.get("DaylightFactor"), "pose":{"around":diagnostics.get("player_around"), "across":diagnostics.get("player_across"), "height":diagnostics.get("player_height"), "eye_height":eye_height, "high_altitude":high_altitude, "yaw":0.0, "pitch":pitch_degrees, "camera_world":camera.global_position, "camera_basis":camera.global_transform.basis}, "atmosphere":atmosphere, "atmosphere_materials":material_snapshot, "gpu_ms":RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()), "cpu_ms":RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid())})

func atmosphere_material_snapshot() -> Dictionary:
	var owned := surface.get_node_or_null("Terrain3DSurface")
	var terrain_node = owned.get("TerrainNode") if owned != null else null
	var owned_material = terrain_node.call("get_material") if terrain_node != null else null
	var owned_frame := frame_values(owned_material)
	var discovered: Array = []
	var groups := {"proxy":{"count":0, "all_valid":true}, "global":{"count":0, "all_valid":true}, "far":{"count":0, "all_valid":true}}
	for node in all_nodes(game):
		if node is MeshInstance3D and node.is_visible_in_tree():
			var mesh_node := node as MeshInstance3D
			var material = mesh_node.material_override
			if material == null and mesh_node.mesh != null and mesh_node.mesh.get_surface_count() > 0: material = mesh_node.mesh.surface_get_material(0)
			if material is ShaderMaterial:
				var shader := (material as ShaderMaterial).shader
				var shader_path := shader.resource_path if shader != null else ""
				var kind := terrain_material_kind(shader_path, str(mesh_node.get_path()))
				if kind != "":
					var frame := frame_values(material)
					discovered.append({"path":str(mesh_node.get_path()), "name":mesh_node.name, "shader":shader_path, "kind":kind, "frame":frame})
					groups[kind]["count"] = int(groups[kind]["count"]) + 1
					groups[kind]["all_valid"] = bool(groups[kind]["all_valid"]) and bool(frame.get("valid", false))
	return {"owned_valid":bool(owned_frame.get("valid", false)), "owned":owned_frame, "discovered":discovered, "groups":groups}

func terrain_material_kind(shader_path: String, node_path: String) -> String:
	if shader_path.ends_with("/ring_terrain_proxy.gdshader"): return "proxy"
	if shader_path.ends_with("/ring_terrain_global.gdshader"):
		return "far" if "/ScaledFarViewport/" in node_path else "global"
	return ""

func frame_values(material) -> Dictionary:
	if material == null: return {"valid":false, "reason":"no material"}
	if material is ShaderMaterial:
		var shader_material := material as ShaderMaterial
		return {"valid":bool(shader_material.get_shader_parameter("ring_atmosphere_ray_frame_valid")), "coordinate_scale":shader_material.get_shader_parameter("ring_atmosphere_coordinate_scale"), "camera_height_meters":shader_material.get_shader_parameter("ring_atmosphere_camera_height_meters"), "camera_world_position":shader_material.get_shader_parameter("ring_atmosphere_camera_world_position")}
	if material.has_method("get_shader_param"):
		return {"valid":bool(material.call("get_shader_param", "ring_atmosphere_ray_frame_valid")), "coordinate_scale":material.call("get_shader_param", "ring_atmosphere_coordinate_scale"), "camera_height_meters":material.call("get_shader_param", "ring_atmosphere_camera_height_meters"), "camera_world_position":material.call("get_shader_param", "ring_atmosphere_camera_world_position")}
	return {"valid":false, "reason":"unsupported material API"}

func all_nodes(node: Node) -> Array:
	var result: Array = [node]
	for child in node.get_children(): result.append_array(all_nodes(child))
	return result

func capture_visible_tuning(profile: String) -> void:
	drawer.call("SetBenchmarkDrawerOpen", true)
	await frames(2)
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	var atmosphere_tab := tabs.get_node_or_null("Atmosphere") as Control if tabs != null else null
	if tabs == null or atmosphere_tab == null: return fail("Atmosphere tuning tab is unavailable")
	tabs.current_tab = tabs.get_tab_idx_from_control(atmosphere_tab)
	await frames(2)
	var visibility := atmosphere_tab.find_child("AtmosphereVisibility", true, false) as Range
	if visibility == null or not visibility.visible: return fail("Atmosphere visibility control is not visible")
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	var path := fixture.path_join("ui_" + profile + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(path)) != OK: return fail("tuning screenshot failed " + profile)
	report["ui_" + profile] = {"path":path, "visibility_km":visibility.value}
	drawer.call("SetBenchmarkDrawerOpen", false)
	await frames(1)

func frames(count: int) -> void:
	for ignored in count: await process_frame

func fail(message: String) -> void:
	push_error("ATMOSPHERE_VISUAL_TEST=FAIL " + message)
	quit(1)
