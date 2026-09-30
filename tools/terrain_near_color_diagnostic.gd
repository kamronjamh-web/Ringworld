extends SceneTree
# Isolated Forward+ diagnostic for a native Terrain3D colour mismatch.  This
# deliberately changes only the active runtime material and restores each
# value, so a run cannot alter player preferences or saved world content.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const FIXTURE_AROUND := 16443476.0
const FIXTURE_ACROSS := 38692.0
const SETTLE_FRAMES := 12

var fixture := ""
var game: Node
var surface: Node
var explorer: Node
var camera: Camera3D
var clock: Node
var placement: RefCounted
var report := {"status":"RUNNING", "material":{}, "captures":[]}

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary:
		return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void:
		settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool:
		return false

func _init() -> void:
	call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null:
		return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/terrain_near_color/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var old_session := root.get_node_or_null("WorldSession")
	if old_session != null:
		old_session.name = "WorldSessionOriginalNearColor"
	var session := SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	game = await create_game()
	if game == null:
		return
	surface = game.get_node_or_null("Terrain3DGameplay")
	explorer = game.get_node_or_null("Explorer")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	clock = game.get_node_or_null("RingDayNightCycle")
	placement = FIXTURE_HELPER.new()
	if surface == null or explorer == null or camera == null or clock == null:
		return fail("missing Terrain3D gameplay nodes")
	var height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	if not is_finite(height):
		return fail("fixture height is non-finite")
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready():
		return
	if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")):
		return fail("fixture remained underwater before capture")
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	if not bool(placement.call("Place", explorer, FIXTURE_AROUND, height, FIXTURE_ACROSS)):
		return fail("could not set canonical fixture pose")
	await frames(SETTLE_FRAMES)
	var material = active_material()
	if material == null:
		return fail("active native Terrain3D material unavailable")
	report.material = material_snapshot(material)
	report["visible_proxy_materials"] = proxy_material_snapshots()
	var phases := select_visual_phases()
	clock.set("Paused", true)
	clock.call("SetCycleFraction", phases.noon)
	await capture("normal_noon", material, false, 30)
	clock.call("SetCycleFraction", phases.night)
	await capture("normal_night", material, false, 30)
	await capture("baseline_haze_disabled", material)
	var original_biomes = material.call("get_shader_param", "biome_texture_weights_enabled")
	material.call("set_shader_param", "biome_texture_weights_enabled", false)
	await capture("biome_weights_disabled", material)
	material.call("set_shader_param", "biome_texture_weights_enabled", original_biomes)
	var original_wetness = material.call("get_shader_param", "surface_wetness")
	var original_strength = material.call("get_shader_param", "surface_wetness_strength")
	material.call("set_shader_param", "surface_wetness", 0.0)
	material.call("set_shader_param", "surface_wetness_strength", 0.0)
	await capture("dry_surface", material)
	material.call("set_shader_param", "surface_wetness", original_wetness)
	material.call("set_shader_param", "surface_wetness_strength", original_strength)
	var file := FileAccess.open(ProjectSettings.globalize_path(fixture.path_join("report.json")), FileAccess.WRITE)
	if file == null:
		return fail("could not write report")
	report.status = "PASS"
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	game.queue_free()
	await process_frame
	print("TERRAIN_NEAR_COLOR_DIAGNOSTIC=PASS fixture=" + fixture)
	quit(0)

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null:
		fail("missing terrain backend selector")
		return null
	selector.select(2)
	selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		fail("runtime generation preparation failed")
		return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate()
	install_isolated_preferences(next)
	launcher.call("_configure_game", next, 5000000.0)
	root.add_child(next)
	current_scene = next
	launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			return next
	fail("runtime Terrain3D startup timed out")
	return null

func install_isolated_preferences(next: Node) -> void:
	var drawer := next.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		return
	for item in [["InstallGrassDefaults", "grass.tres"], ["InstallVegetationDefaults", "vegetation.tres"], ["InstallWaterDefaults", "water.tres"], ["InstallAntiAliasingDefaults", "anti_aliasing.tres"], ["InstallShadowDefaults", "shadows.tres"], ["InstallSurfaceTerrainDefaults", "surface.tres"], ["InstallAtmosphereDefaults", "atmosphere.tres"]]:
		drawer.call(str(item[0]), fixture.path_join(str(item[1])))

func wait_ready() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 55000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
		var stream: Dictionary = diagnostics.get("streaming", {})
		var native: Dictionary = diagnostics.get("native", {})
		var prep: Dictionary = native.get("preparation", {})
		var placed := absf(float(diagnostics.get("player_around", NAN)) - FIXTURE_AROUND) < 1.0 and absf(float(diagnostics.get("player_across", NAN)) - FIXTURE_ACROSS) < 1.0
		var ready := bool(diagnostics.get("ready", false)) and bool(diagnostics.get("visual_coverage", false)) and bool(prep.get("ready", false)) and int(stream.get("pending_jobs", 1)) == 0 and int(stream.get("active_jobs", 1)) == 0 and not bool(stream.get("packing", true))
		stable = stable + 1 if placed and ready else 0
		if stable >= 8:
			return true
	fail("Terrain3D workload did not settle")
	return false

func active_material():
	var owner := surface.get_node_or_null("Terrain3DSurface")
	var terrain = owner.get("TerrainNode") if owner != null else null
	return terrain.call("get_material") if terrain != null else null

func material_snapshot(material) -> Dictionary:
	var snapshot := {}
	for parameter in ["biome_texture_weights_enabled", "biome_texture_scale", "surface_wetness", "surface_wetness_strength", "surface_dry_roughness", "surface_wet_roughness", "ring_atmosphere_enabled", "smooth_terrain_shading", "flat_terrain_normals", "sun_direction", "day_start_dot", "day_full_dot", "night_tint", "night_direct_light_factor", "day_direct_light_factor", "shadow_floor", "local_cloud_shadow_enabled", "local_cloud_shadow_near_enabled", "local_cloud_shadow_far_enabled", "local_cloud_shadow_strength"]:
		snapshot[parameter] = material.call("get_shader_param", parameter)
	# These are Terrain3D material properties, not Terrain3D node properties.
	# Reading the owner node here would silently report the wrong debug state.
	for property in ["show_checkered", "shader_override_enabled", "shader_override", "world_background"]:
		snapshot[property] = material.get(property)
	var samplers := {}
	for parameter in ["biome_grass_albedo", "biome_dirt_albedo", "biome_desert_albedo", "biome_forest_albedo", "biome_tundra_albedo", "biome_alpine_albedo"]:
		var texture = material.call("get_shader_param", parameter)
		samplers[parameter] = {"class":texture.get_class() if texture != null else "null", "path":texture.resource_path if texture != null else ""}
	snapshot["samplers"] = samplers
	var cloud_texture = material.call("get_shader_param", "local_cloud_shadow_texture")
	snapshot["local_cloud_shadow_texture"] = {"class":cloud_texture.get_class() if cloud_texture != null else "null", "path":cloud_texture.resource_path if cloud_texture != null else ""}
	return snapshot

func proxy_material_snapshots() -> Array:
	var result: Array = []
	for node in all_nodes(game):
		if not node is MeshInstance3D:
			continue
		var mesh := node as MeshInstance3D
		if not mesh.is_visible_in_tree():
			continue
		var material = mesh.material_override
		if material == null and mesh.mesh != null and mesh.mesh.get_surface_count() > 0:
			material = mesh.mesh.surface_get_material(0)
		if not material is ShaderMaterial:
			continue
		var shader := (material as ShaderMaterial).shader
		if shader == null or not shader.resource_path.ends_with("/ring_terrain_proxy.gdshader"):
			continue
		var values := {}
		for parameter in ["biome_texture_weights_enabled", "biome_texture_scale", "night_tint", "night_direct_light_factor", "day_direct_light_factor", "shadow_floor", "analytic_cloud_shadow_enabled", "roughness_value", "ring_atmosphere_enabled"]:
			values[parameter] = material.get_shader_parameter(parameter)
		result.append({"path":str(mesh.get_path()), "parameters":values})
	return result

func all_nodes(node: Node) -> Array:
	var result: Array = [node]
	for child in node.get_children():
		result.append_array(all_nodes(child))
	return result

func select_visual_phases() -> Dictionary:
	var noon := 0.0
	var night := 0.0
	var highest := -INF
	var lowest := INF
	for step in 48:
		var fraction := float(step) / 48.0
		clock.call("SetCycleFraction", fraction)
		var elevation := float(clock.get("SunElevationDegrees"))
		if elevation > highest:
			highest = elevation
			noon = fraction
		if elevation < lowest:
			lowest = elevation
			night = fraction
	return {"noon":noon, "night":night}

func capture(label: String, material, disable_haze := true, settle_frames := SETTLE_FRAMES) -> void:
	# The baseline controls disable native haze per material; normal visual shots
	# preserve it and therefore exercise the actual daytime/nighttime path.
	var atmosphere_enabled = material.call("get_shader_param", "ring_atmosphere_enabled")
	if disable_haze:
		material.call("set_shader_param", "ring_atmosphere_enabled", false)
	await frames(settle_frames)
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	var path := fixture.path_join(label + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(path)) != OK:
		fail("capture failed " + label)
		return
	var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
	report.captures.append({"label":label, "path":path, "center_rgb":center_rgb(image), "parameters":material_snapshot(material), "player_height":diagnostics.get("player_height"), "surface_height":diagnostics.get("surface_height"), "sun_elevation_degrees":clock.get("SunElevationDegrees"), "haze_disabled_for_capture":disable_haze})
	if disable_haze:
		material.call("set_shader_param", "ring_atmosphere_enabled", atmosphere_enabled)

func center_rgb(image: Image) -> Vector3:
	var low_x := image.get_width() * 3 / 8
	var high_x := image.get_width() * 5 / 8
	var low_y := image.get_height() * 5 / 8
	var high_y := image.get_height() * 7 / 8
	var total := Vector3.ZERO
	var count := 0
	for y in range(low_y, high_y, 4):
		for x in range(low_x, high_x, 4):
			var color := image.get_pixel(x, y)
			total += Vector3(color.r, color.g, color.b)
			count += 1
	return total / max(1, count)

func frames(count: int) -> void:
	for ignored in count:
		await process_frame

func fail(message: String) -> void:
	push_error("TERRAIN_NEAR_COLOR_DIAGNOSTIC=FAIL " + message)
	quit(1)
