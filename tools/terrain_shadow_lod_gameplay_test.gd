extends SceneTree
# Full-game, isolated contract capture for terrain shadow receivers/casters.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const AROUND := 16443476.0
const ACROSS := 38692.0
var fixture := ""
var game: Node
var surface: Node
var explorer: Node
var camera: Camera3D
var clock: Node
var coordinator: Node
var global_lod: Node
var planet: DirectionalLight3D
var placement: RefCounted
var report := {"status":"RUNNING", "captures":[]}

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/terrain_shadow_lod/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var old := root.get_node_or_null("WorldSession")
	if old != null: old.name = "WorldSessionOriginalTerrainShadows"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	surface = game.get_node_or_null("Terrain3DGameplay")
	explorer = game.get_node_or_null("Explorer")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	clock = game.get_node_or_null("RingDayNightCycle")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	global_lod = game.get_node_or_null("GlobalTerrainLod3")
	planet = game.get_node_or_null("PlanetLight") as DirectionalLight3D
	placement = FIXTURE_HELPER.new()
	if surface == null or explorer == null or camera == null or clock == null or coordinator == null or global_lod == null or planet == null: return fail("missing terrain-shadow gameplay nodes")
	var height := float(surface.call("SampleHeight", AROUND, ACROSS)) + 1.1
	if not is_finite(height): return fail("non-finite fixture terrain")
	explorer.call("SetCanonicalSpawnMeters", AROUND, height, ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	if not bool(placement.call("Place", explorer, AROUND, height, ACROSS)): return fail("could not place fixture")
	clock.set("Paused", true)
	var phases := await select_phases()
	clock.call("SetCycleFraction", phases.day); await wait_shadow_data("day")
	await capture("day")
	clock.call("SetCycleFraction", phases.night); await wait_shadow_data("night")
	await capture("night")
	clock.call("SetCycleFraction", phases.day); await wait_shadow_data("day_return")
	await capture("day_return")
	clock.call("SetCycleFraction", phases.night); await wait_shadow_data("night_revisit")
	await capture("night_revisit")
	if not validate_transitions(): return
	report.status = "PASS"; write_report()
	game.queue_free(); await process_frame
	print("TERRAIN_SHADOW_LOD_GAMEPLAY_TEST=PASS fixture=" + fixture)
	quit(0)

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: fail("missing terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)): fail("runtime generation preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); install_preferences(next)
	launcher.call("_configure_game", next, 5000000.0)
	root.add_child(next); current_scene = next; launcher.queue_free()
	for ignored in 360:
		await process_frame
		var candidate := next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	fail("runtime Terrain3D startup timed out")
	return null

func install_preferences(next: Node) -> void:
	var drawer := next.get_node_or_null("LiveTuningDrawer")
	if drawer == null: return
	for item in [["InstallGrassDefaults","grass.tres"], ["InstallVegetationDefaults","vegetation.tres"], ["InstallWaterDefaults","water.tres"], ["InstallAntiAliasingDefaults","anti_aliasing.tres"], ["InstallShadowDefaults","shadows.tres"], ["InstallSurfaceTerrainDefaults","surface.tres"], ["InstallAtmosphereDefaults","atmosphere.tres"], ["InstallWindDefaults","wind.tres"], ["InstallRainHazeDefaults","rain_haze.tres"]]: drawer.call(str(item[0]), fixture.path_join(str(item[1])))

func wait_ready() -> bool:
	var stable := 0; var deadline := Time.get_ticks_msec() + 60000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d: Dictionary = surface.call("GetSurfaceDiagnostics"); var stream: Dictionary = d.get("streaming", {}); var native: Dictionary = d.get("native", {})
		var prep: Dictionary = native.get("preparation", {})
		var ready := bool(d.get("ready", false)) and bool(d.get("visual_coverage", false)) and bool(prep.get("ready", false)) and int(stream.get("pending_jobs", 1)) == 0 and int(stream.get("active_jobs", 1)) == 0 and not bool(stream.get("packing", true))
		stable = stable + 1 if ready else 0
		if stable >= 8: return true
	fail("terrain did not settle")
	return false

func select_phases() -> Dictionary:
	var day := 0.0; var night := 0.0; var hi := -INF; var lo := INF
	for step in 48:
		var p := float(step) / 48.0; clock.call("SetCycleFraction", p)
		await process_frame
		var e := float(clock.get("SunElevationDegrees"))
		if e > hi: hi = e; day = p
		# The planet can be below its own horizon at the solar minimum.  Use the
		# darkest phase that actually activates its directional light.
		if e < lo and planet.visible and planet.light_energy > 0.001: lo = e; night = p
	if lo == INF: fail("no night phase with active PlanetLight")
	return {"day":day, "night":night}

func wait_shadow_data(label: String) -> void:
	# Let the atmosphere controller update PlanetLight, then the mask producer
	# consume that state before testing a day/night handoff.
	for ignored in 3: await process_frame
	var deadline := Time.get_ticks_msec() + 30000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var state := receiver_snapshot()
		var masks_ready := bool(state.native.get("macro_texture", false)) and bool(state.global.get("macro_texture", false))
		if label.begins_with("night"):
			masks_ready = masks_ready and bool(global_lod.get("PlanetMacroShadowMaskActive")) and bool(state.native.get("planet_texture", false)) and bool(state.global.get("planet_texture", false)) and direction_matches(state.native.get("planet_direction"), planet.global_transform.basis.z.normalized())
		for lod in lod_snapshot():
			if bool(lod.StreamingActive) and bool(lod.CastTerrainShadows) and int(lod.SampleSpacingMeters) == 2:
				masks_ready = masks_ready and bool(lod.ShadowIgnoresTerrainFade) and int(lod.visible_opaque_shadow_meshes) > 0
		if masks_ready: return
	fail("shadow masks did not settle for " + label + ": " + JSON.stringify(receiver_snapshot()))

func capture(label: String) -> void:
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	var path := fixture.path_join(label + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(path)) != OK: return fail("capture failed " + label)
	var receivers := receiver_snapshot()
	var snapshot := {"label":label, "image":path, "sun_elevation":clock.get("SunElevationDegrees"), "planet_direction":planet.global_transform.basis.z.normalized(), "planet_visible":planet.visible, "planet_energy":planet.light_energy, "native_cast_shadow":native_cast_shadow(), "lods":lod_snapshot(), "terrain_shadow_coverage_meters":coordinator.get("TerrainShadowCoverageMeters"), "global_cast_terrain_shadows":global_lod.get("CastTerrainShadows"), "planet_mask_active":global_lod.get("PlanetMacroShadowMaskActive"), "planet_mask_pending":global_lod.get("PlanetMacroShadowBuildPending"), "scaled_far_receiver_active":not (receivers.scaled_far as Array).is_empty(), "receivers":receivers}
	if int(snapshot.native_cast_shadow) != GeometryInstance3D.SHADOW_CASTING_SETTING_ON: return fail("native Terrain3D cast-shadow default is not On")
	snapshot["planet_shadow_distance_meters"] = planet.directional_shadow_max_distance
	var grid := game.get_node("ChunkGridManager") as Node3D
	snapshot["planet_far_direction"] = (grid.global_basis.orthonormalized().inverse() * planet.global_basis.z).normalized()
	var coverage := float(snapshot.terrain_shadow_coverage_meters)
	var active_lods := 0
	var casting_chain_end := 0.0
	var ordered_lods: Array = snapshot.lods.duplicate()
	ordered_lods.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return int(a.SampleSpacingMeters) < int(b.SampleSpacingMeters))
	for lod in ordered_lods:
		var extent := maxf(float(lod.get("FadeEndMeters", 0.0)), float(lod.get("FadeOutEndMeters", 0.0)))
		if bool(lod.get("StreamingActive", false)) and extent > 0.0: active_lods += 1
		if bool(lod.StreamingActive) and casting_chain_end < coverage:
			# Visual ownership changes at the midpoint, not the overlap start.
			# Persistent casters remove radial fades, but keep ownership atlas holes.
			var near_handoff := (float(lod.FadeStartMeters) + float(lod.FadeEndMeters)) * .5
			var far_handoff := (float(lod.FadeOutStartMeters) + float(lod.FadeOutEndMeters)) * .5
			var nearest := int(lod.SampleSpacingMeters) == 2
			if not bool(lod.CastTerrainShadows) or int(lod.visible_opaque_shadow_meshes) <= 0 or (not nearest and near_handoff > casting_chain_end + 1.0):
				return fail("terrain caster chain has a gap before required range: " + JSON.stringify({"covered_through":casting_chain_end,"required_range":coverage,"lod":lod}))
			casting_chain_end = maxf(casting_chain_end, far_handoff)
		if bool(lod.StreamingActive) and bool(lod.CastTerrainShadows) and int(lod.SampleSpacingMeters) == 2:
			if not bool(lod.ShadowIgnoresTerrainFade) or int(lod.visible_opaque_shadow_meshes) <= 0: return fail("nearest 2m proxy lacks persistent visible opaque shadow meshes: " + JSON.stringify(lod))
	if active_lods == 0: return fail("no active proxy LODs to audit")
	if casting_chain_end + 1.0 < coverage: return fail("terrain caster chain ends before required range: " + str(casting_chain_end) + " < " + str(coverage))
	snapshot["casting_chain_covered_through_meters"] = casting_chain_end
	report.captures.append(snapshot)

func native_cast_shadow() -> int:
	var owner := surface.get_node_or_null("Terrain3DSurface")
	var terrain = owner.get("TerrainNode") if owner != null else null
	return int(terrain.get("cast_shadows")) if terrain != null else -1

func lod_snapshot() -> Array:
	var result: Array = []
	# Levels is a C# IReadOnlyList, which is not exposed as a Godot Variant.
	for level in all_nodes(game):
		var script = level.get_script()
		if script == null or not script.resource_path.ends_with("/RingTerrainProxySector.cs"): continue
		var visible_opaque_shadow_meshes := 0
		for candidate in all_nodes(level):
			if candidate is MeshInstance3D and candidate.name == "OpaqueShadowCaster" and candidate.is_visible_in_tree() and candidate.mesh != null and candidate.cast_shadow == GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY:
				visible_opaque_shadow_meshes += 1
		result.append({"LodLabel":level.get("LodLabel"), "SampleSpacingMeters":level.get("SampleSpacingMeters"), "StreamingActive":level.get("StreamingActive"), "Visible":level.get("Visible"), "CastTerrainShadows":level.get("CastTerrainShadows"), "ActiveSectorCount":level.get("ActiveSectorCount"), "FadeStartMeters":level.get("FadeStartMeters"), "FadeEndMeters":level.get("FadeEndMeters"), "FadeOutStartMeters":level.get("FadeOutStartMeters"), "FadeOutEndMeters":level.get("FadeOutEndMeters"), "ShadowIgnoresTerrainFade":level.get("ShadowIgnoresTerrainFade"), "visible_opaque_shadow_meshes":visible_opaque_shadow_meshes})
	return result

func receiver_snapshot() -> Dictionary:
	var native := native_material_receiver()
	var receivers := {"native":native, "proxy":[], "global":{}, "scaled_far":[], "structural_hulls":[]}
	var seen_materials := {}
	for node in all_nodes(game):
		if not node is MeshInstance3D or not node.is_visible_in_tree(): continue
		var mesh := node as MeshInstance3D; var material = mesh.material_override
		if material == null and mesh.mesh != null and mesh.mesh.get_surface_count() > 0: material = mesh.mesh.surface_get_material(0)
		if not material is ShaderMaterial: continue
		if seen_materials.has(material.get_instance_id()): continue
		seen_materials[material.get_instance_id()] = true
		var shader := (material as ShaderMaterial).shader; var path := shader.resource_path if shader != null else ""
		if not path.ends_with("/ring_terrain_proxy.gdshader") and not path.ends_with("/ring_terrain_global.gdshader"): continue
		var values := shader_receiver(material)
		values["node_path"] = str(mesh.get_path())
		if path.ends_with("/ring_terrain_proxy.gdshader"):
			# Hull closures reuse the terrain shader, but a ground-height mask
			# is not a valid receiver representation for a vertical wall face.
			if mesh.name == "StructuralHullLod": receivers.structural_hulls.append(values)
			else: receivers.proxy.append(values)
		elif path.ends_with("/ring_terrain_global.gdshader"):
			if "/ScaledFarViewport/" in str(mesh.get_path()): receivers.scaled_far.append(values)
			else: receivers.global = values
	return receivers

func native_material_receiver() -> Dictionary:
	var owner := surface.get_node_or_null("Terrain3DSurface"); var terrain = owner.get("TerrainNode") if owner != null else null
	var material = terrain.call("get_material") if terrain != null else null
	return receiver_values(material, true)

func shader_receiver(material: ShaderMaterial) -> Dictionary: return receiver_values(material, false)

func receiver_values(material, native: bool) -> Dictionary:
	if material == null: return {"valid":false}
	var get_value = func(key: String): return material.call("get_shader_param", key) if native else material.get_shader_parameter(key)
	var macro_texture = get_value.call("terrain_macro_shadow_texture")
	var planet_texture = get_value.call("terrain_planet_shadow_texture")
	return {"valid":true, "macro_enabled":bool(get_value.call("terrain_macro_shadow_enabled")), "macro_texture":macro_texture != null, "planet_enabled":bool(get_value.call("terrain_planet_shadow_enabled")), "planet_texture":planet_texture != null, "planet_direction":get_value.call("terrain_planet_shadow_direction"), "planet_strength":get_value.call("terrain_planet_shadow_strength"), "planet_fade_start":get_value.call("terrain_planet_shadow_fade_start_meters"), "planet_fade_end":get_value.call("terrain_planet_shadow_fade_end_meters")}

func validate_transitions() -> bool:
	if report.captures.size() != 4:
		fail("missing day/night/day/night captures"); return false
	var day: Dictionary = report.captures[0]; var night: Dictionary = report.captures[1]; var back: Dictionary = report.captures[2]; var revisit: Dictionary = report.captures[3]
	var expected_direction: Vector3 = night.get("planet_direction", Vector3.ZERO)
	for key in ["native", "global"]:
		var d: Dictionary = day.receivers.get(key, {}); var n: Dictionary = night.receivers.get(key, {}); var b: Dictionary = back.receivers.get(key, {})
		if not bool(d.get("macro_enabled", false)) or not bool(b.get("macro_enabled", false)):
			fail("sun macro mask did not remain enabled across handoff: " + key); return false
		if not bool(n.get("planet_enabled", false)):
			fail("planet mask was not enabled at night: " + key); return false
		if float(n.get("planet_fade_end", INF)) > float(night.planet_shadow_distance_meters) + 1.0:
			fail("planet mask starts taking over beyond local atlas coverage: " + key); return false
		if not direction_matches(n.get("planet_direction"), expected_direction):
			fail("planet direction does not match PlanetLight: " + key); return false
		var r: Dictionary = revisit.receivers.get(key, {})
		if not bool(r.get("planet_enabled", false)) or not bool(r.get("planet_texture", false)):
			fail("planet mask did not re-enable on night revisit: " + key); return false
		if not direction_matches(r.get("planet_direction"), expected_direction):
			fail("planet direction changed on night revisit: " + key); return false
		if not bool(back.get("planet_visible", false)) or float(back.get("planet_energy", 0.0)) <= 0.001:
			if bool(b.get("planet_enabled", false)):
				fail("planet receiver remained enabled after daytime handoff: " + key); return false
	for group in ["proxy", "scaled_far"]:
		var values: Array = night.receivers.get(group, [])
		if values.is_empty():
			if group == "scaled_far": continue # Recorded explicitly as inactive in the report.
			fail("no visible %s terrain shadow receiver" % group); return false
		for value in values:
			if not bool(value.get("macro_enabled", false)) or not bool(value.get("macro_texture", false)):
				fail("sun macro receiver missing on " + group); return false
			if not bool(value.get("planet_enabled", false)) or not bool(value.get("planet_texture", false)):
				fail("planet receiver missing on " + group); return false
			if group == "proxy" and not direction_matches(value.get("planet_direction"), expected_direction):
				fail("planet direction does not match on " + group); return false
			if group == "scaled_far" and not direction_matches(value.get("planet_direction"), night.planet_far_direction):
				fail("scaled-far receiver has an incorrect transformed planet direction"); return false
		var revisit_values: Array = revisit.receivers.get(group, [])
		if group != "scaled_far" and revisit_values.is_empty():
			fail("no proxy receiver on night revisit"); return false
		for revisit_value in revisit_values:
			if not bool(revisit_value.get("planet_enabled", false)) or not bool(revisit_value.get("planet_texture", false)):
				fail("planet receiver did not re-enable on night revisit: " + group); return false
	return true

func direction_matches(value, expected: Vector3) -> bool:
	return value is Vector3 and (value as Vector3).normalized().dot(expected) > 0.999

func direction_is_nonzero(value) -> bool:
	return value is Vector3 and (value as Vector3).length_squared() > 0.5

func all_nodes(node: Node) -> Array:
	var result: Array = [node]
	for child in node.get_children(): result.append_array(all_nodes(child))
	return result

func write_report() -> void:
	if fixture.is_empty(): return
	var file := FileAccess.open(ProjectSettings.globalize_path(fixture.path_join("report.json")), FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report, "\t")); file.close()

func fail(message: String) -> void:
	report.status = "FAIL"; report["error"] = message; write_report(); push_error("TERRAIN_SHADOW_LOD_GAMEPLAY_TEST=FAIL " + message); quit(1)
