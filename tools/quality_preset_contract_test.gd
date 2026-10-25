extends SceneTree

## TEST ONLY. Graphics-preset contract test (docs/quality-presets-plan.md section 1, WO-1). Asserts the non-negotiable systems C1-C8 on all five
## menu presets (0 Low, 1 Medium, 2 High, 3 VeryHigh, 4 UltraVideo) in the isolated certified runtime world (tools/shadow_harness_common.gd),
## booted with factory settings (no user preferences). Also repeats C3 after a simulated world load (save, free, re-boot on the same fixture slot).
## Checks never abort the run: every failure is recorded and the groups that failed are listed at the end.
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   Godot --path . --resolution 1280x720 --script res://tools/quality_preset_contract_test.gd -- [--out=<dir>]
## Prints QP_CONTRACT=PASS, or QP_CONTRACT=FAIL <groups> on stderr (exit 1). --out writes <dir>/contract.json.
## Groups: setup, C1, C1-planet, C2, C3, C3-model, C3-load, C3-model-load, O12-load, C4, C5, C6, C7, C7-storm, C8, C8-natural, C8-preview.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const PROBE := preload("res://tools/QualityPresetContractProbe.cs")
const POI_SHADERS := ["res://materials/ring_poi_architecture.gdshader", "res://materials/ring_poi_architecture_double_sided.gdshader"]
const PRESET_NAMES := ["Low", "Medium", "High", "VeryHigh", "UltraVideo"]
const BOOT_PRESET := 3

var game: Node
var explorer: Node
var surface: Node
var sun: DirectionalLight3D
var planet: DirectionalLight3D
var graphics: Node
var cloud_bridge: Node
var coordinator: Node
var scaled_far: Node
var weather: Node
var save_manager: Node
var probe: RefCounted
var checks: Array = []
var info := {}
var out_dir := ""
var finished := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+"); return
	create_timer(1500.0).timeout.connect(func(): _fail("timeout"))
	# Like the capture harness: uncapped and no VSync. A VSync (FIFO) window is throttled to ~4 fps when the display is asleep
	# (measured 2026-10-25: an empty scene runs at 3.8 fps), which starves streaming and breaks the idle waits.
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	out_dir = COMMON.arg_value("--out=", "")
	if out_dir != "":
		DirAccess.make_dir_recursive_absolute(out_dir)
	probe = PROBE.new()
	info["machine"] = COMMON.machine_info()
	print("QP_CONTRACT adapter=", info["machine"]["adapter"])
	var fixture_root := "res://.godot/quality-preset-contract/%d" % Time.get_ticks_usec()

	# ---- a. boot check (preset 3)
	game = await _create_game(fixture_root, BOOT_PRESET, false)
	if game == null:
		_fail("game boot failed"); return
	if not _bind():
		return
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	_quiet_inputs()
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	var idle_ok: bool = await COMMON.wait_builds_idle(self, game, 180.0)
	info["initial_idle"] = idle_ok
	var base: Dictionary = COMMON.pose_now(game)
	info["base_pose"] = base
	COMMON.hold_world_clocks(game)
	info["noon"] = COMMON.apply_time(game, {"mode": "noon"})
	await COMMON.settle(self, 2.0)
	_rec("setup", BOOT_PRESET, "streaming_idle_after_spawn", idle_ok, "idle=%s" % idle_ok)
	_rec("setup", BOOT_PRESET, "boot_preset", int(graphics.get("CurrentPreset")) == BOOT_PRESET, "CurrentPreset=%d" % int(graphics.get("CurrentPreset")))
	_check_haze("C3", BOOT_PRESET)

	# ---- b. per preset at the ground pose, noon
	var curtains: Node = await _find_curtains()
	for p in 5:
		await _apply_preset(p, 2.0)
		_check_shadows_and_haze(p)
		_check_clouds_and_weather(p, curtains)

	# ---- c. planet shadows at night
	var night: Dictionary = COMMON.apply_time(game, {"mode": "night_planet"})
	info["night"] = night
	if night.has("error"):
		_rec("C1-planet", -1, "night_planet_time", false, str(night["error"]))
	else:
		for p in 5:
			await _apply_preset(p, 1.5)
			var planet_on: bool = planet != null and planet.shadow_enabled
			_rec("C1-planet", p, "planet_light_shadow_enabled", planet_on, "PlanetLight.shadow_enabled=%s" % planet_on)
			var planet_eff := bool(graphics.get("EffectivePlanetShadowsEnabled"))
			_rec("C1-planet", p, "effective_planet_shadows", planet_eff, "EffectivePlanetShadowsEnabled=%s" % planet_eff)
	COMMON.apply_time(game, {"mode": "noon"})

	# ---- d. POI at emitter_rim
	var poi_info: Dictionary = await COMMON.go_pose(self, game, base, "emitter_rim", 4.0, 150.0)
	info["poi_pose"] = poi_info
	if poi_info.has("error"):
		_rec("C4", -1, "emitter_rim_pose", false, str(poi_info["error"]))
	else:
		await _apply_preset(BOOT_PRESET, 1.5)
		var reference := int(probe.call("LargePoiShadowCount"))
		info["large_poi_shadow_reference"] = reference
		for p in 5:
			await _apply_preset(p, 1.5)
			_check_poi(p, reference)

	# ---- e. storm: rain and lightning
	await _apply_preset(BOOT_PRESET, 0.5)
	explorer.call("TeleportToCanonicalMeters", float(base["around"]), float(base["vertical"]) + 2.0, float(base["across"]), float(base["yaw"]), 5.0, true)
	await COMMON.settle(self, 0.2)
	await COMMON.wait_builds_idle(self, game, 150.0)
	COMMON.apply_time(game, {"mode": "noon"})
	cloud_bridge.set("SpawnDistanceMeters", 0.0)
	cloud_bridge.call("SpawnStormFrontAhead")
	var clouds := game.get_node_or_null("RingVolumetricClouds")
	if clouds != null:
		clouds.set("CloudSimulationPaused", false)
	await COMMON.settle(self, 20.0)
	var flashes_before := int(weather.get("LightningFlashCount"))
	var natural_start := Time.get_ticks_msec()
	var natural := false
	while Time.get_ticks_msec() - natural_start < 60000:
		if int(weather.get("LightningFlashCount")) > flashes_before:
			natural = true
			break
		await process_frame
	var natural_s := (Time.get_ticks_msec() - natural_start) / 1000.0
	info["lightning_natural_seconds"] = natural_s
	_rec("C8-natural", BOOT_PRESET, "lightning_flash_count_increases_within_60s", natural, "flashes %d -> %d after %.1fs" % [flashes_before, int(weather.get("LightningFlashCount")), natural_s])
	for p in 5:
		await _apply_preset(p, 3.0)
		var rain := float(weather.get("PrecipitationIntensity"))
		_rec("C7-storm", p, "precipitation_intensity_positive", rain > 0.0, "PrecipitationIntensity=%.3f" % rain)
		var n := int(weather.get("LightningFlashCount"))
		weather.call("TriggerLightningPreview")
		var preview_start := Time.get_ticks_msec()
		while Time.get_ticks_msec() - preview_start < 3000 and int(weather.get("LightningFlashCount")) <= n:
			await process_frame
		var n2 := int(weather.get("LightningFlashCount"))
		_rec("C8-preview", p, "trigger_lightning_preview_flash", n2 > n, "flashes %d -> %d" % [n, n2])
	cloud_bridge.call("ClearSpawnedWeather")
	COMMON.hold_world_clocks(game)

	# ---- f. simulated world load: save, free, re-boot on the same fixture slot
	await _apply_preset(BOOT_PRESET, 0.5)
	# Simulate an old save: saved Ultra with haze off. The load must restore Ultra everywhere and migrate haze to on.
	graphics.call("ApplyUserPreset", 4)
	graphics.call("SetAtmosphereEnabled", false)
	var save_code := int(save_manager.call("SaveForSceneTransition")) if save_manager != null else -1
	_rec("C3-load", BOOT_PRESET, "save_for_scene_transition_ok", save_code == OK, "SaveForSceneTransition=%d" % save_code)
	var graphics_before := int(graphics.get("CurrentPreset"))
	game.queue_free()
	game = null
	await process_frame
	await process_frame
	var reloaded: Node = await _create_game(fixture_root, BOOT_PRESET, true)
	info["reload_reused_session"] = reloaded != null
	if reloaded == null:
		print("QP_CONTRACT note: reload with the retained stub session failed; retrying with a fresh stub session")
		reloaded = await _create_game(fixture_root, BOOT_PRESET, false)
		info["reload_fresh_session_fallback"] = reloaded != null
	if reloaded == null:
		_rec("C3-load", BOOT_PRESET, "second_boot", false, "second launcher pass on the existing fixture slot failed")
		_finish(); return
	game = reloaded
	if not _bind():
		return
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	_quiet_inputs()
	var monitor2 := game.get_node_or_null("RingPerformanceMonitor")
	if monitor2 != null:
		monitor2.process_mode = Node.PROCESS_MODE_DISABLED
	var idle2: bool = await COMMON.wait_builds_idle(self, game, 120.0)
	await COMMON.settle(self, 2.0)
	_rec("C3-load", BOOT_PRESET, "streaming_idle_after_reload", idle2, "idle=%s" % idle2)
	_check_haze("C3-load", BOOT_PRESET)
	info["preset_before_save"] = graphics_before
	info["graphics_preset_after_load"] = int(graphics.get("CurrentPreset"))
	info["lod_preset_after_load"] = int(coordinator.get("CurrentPreset"))
	_rec("O12-load", 4, "graphics_preset_after_load", int(info["graphics_preset_after_load"]) == 4, "GraphicsQuality.CurrentPreset=%d (want 4)" % info["graphics_preset_after_load"])
	_rec("O12-load", 4, "lod_preset_after_load", int(info["lod_preset_after_load"]) == 4, "TerrainLodCoordinator.CurrentPreset=%d (want 4)" % info["lod_preset_after_load"])
	print("QP_CONTRACT_INFO preset after load: GraphicsQuality=%d TerrainLodCoordinator=%d (before save %d)"
		% [info["graphics_preset_after_load"], info["lod_preset_after_load"], graphics_before])
	_finish()


# ------------------------------------------------------------------ boot

func _create_game(fixture_root: String, preset: int, reuse_session: bool) -> Node:
	var session: Node = root.get_node_or_null("WorldSession")
	if reuse_session and session is COMMON.SessionStub:
		pass
	else:
		if session != null:
			if session is COMMON.SessionStub:
				root.remove_child(session)
				session.free()
			else:
				session.name = "WorldSessionOriginalQpContract"
		session = COMMON.SessionStub.new()
		session.name = "WorldSession"
		root.add_child(session)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture_root)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	ProjectSettings.set_setting("ringworld/runtime/surface_weather_lung_v8", false)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture_root))
	var launcher := COMMON.LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	selector.select(2)
	selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		push_error("runtime-v2 generation preparation failed")
		launcher.queue_free()
		return null
	launcher.call("_save_selected_slot_settings")
	var g := COMMON.GAME.instantiate()
	launcher.call("_configure_game", g, 5000000.0)
	# Factory settings: nothing is installed from user:// (no _install_isolated_preferences), only the preset is forced.
	g.get_node("GraphicsQuality").set("DefaultPreset", preset)
	g.get_node("TerrainLodCoordinator").set("DefaultPreset", preset)
	var mon: Node = g.get_node_or_null("RingPerformanceMonitor")
	if mon != null:
		mon.call("InstallSettingsPathForTests", fixture_root.path_join("telemetry.tres"))
		var reports := fixture_root.path_join("reports")
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(reports))
		mon.call("InstallReportDirectoryForTests", reports)
	root.add_child(g)
	launcher.queue_free()
	var deadline := Time.get_ticks_msec() + 600000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate := g.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			return g
	push_error("gameplay startup did not become ready")
	return null


func _bind() -> bool:
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	sun = game.get_node_or_null("StarLight") as DirectionalLight3D
	planet = game.get_node_or_null("PlanetLight") as DirectionalLight3D
	graphics = game.get_node_or_null("GraphicsQuality")
	cloud_bridge = game.get_node_or_null("SunshineCloudBridge")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	scaled_far = game.get_node_or_null("ScaledFarViewport")
	weather = game.get_node_or_null("LocalWeatherEffects")
	save_manager = game.get_node_or_null("WorldSaveManager")
	for pair in [["Explorer", explorer], ["Terrain3DGameplay", surface], ["StarLight", sun], ["PlanetLight", planet], ["GraphicsQuality", graphics],
			["SunshineCloudBridge", cloud_bridge], ["TerrainLodCoordinator", coordinator], ["ScaledFarViewport", scaled_far], ["LocalWeatherEffects", weather],
			["WorldSaveManager", save_manager]]:
		if pair[1] == null:
			_fail("required node missing: " + str(pair[0])); return false
	return true


## Escape (pause menu) and the grave key (tuning drawer) must not act during the run.
func _quiet_inputs() -> void:
	for blocked in [game.get_node_or_null("LodPauseMenu"), game.get_node_or_null("LiveTuningDrawer")]:
		if blocked != null:
			blocked.set_process_unhandled_input(false)
			blocked.set_process_unhandled_key_input(false)
			blocked.set_process_input(false)
			blocked.set_process_shortcut_input(false)


func _apply_preset(p: int, settle_seconds: float) -> void:
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	graphics.call("ApplyUserPreset", p)
	await COMMON.settle(self, settle_seconds)
	var now_preset := int(graphics.get("CurrentPreset"))
	if now_preset != p:
		_rec("setup", p, "apply_user_preset_took_effect", false, "CurrentPreset=%d" % now_preset)


func _find_curtains() -> Node:
	var frames := 0
	var node := game.get_node_or_null("DistantRainCurtains")
	while node == null and frames < 300:
		await process_frame
		frames += 1
		node = game.get_node_or_null("DistantRainCurtains")
	return node


# ------------------------------------------------------------------ checks

func _rec(group: String, preset: int, check_name: String, ok: bool, detail := "") -> void:
	checks.append({"group": group, "preset": preset, "name": check_name, "ok": ok, "detail": detail})
	if not ok:
		print("QP_CONTRACT_CHECK FAIL %s p=%d %s %s" % [group, preset, check_name, detail])


func _check_haze(group: String, p: int) -> void:
	var diag: Dictionary = graphics.call("GetAtmosphereTuningDiagnostics")
	var enabled := bool(diag.get("enabled", false))
	_rec(group, p, "haze_enabled", enabled, "GetAtmosphereTuningDiagnostics()[enabled]=%s" % enabled)
	var setting := bool(ProjectSettings.get_setting("ringworld/runtime/haze_enabled", true))
	_rec(group, p, "haze_enabled_project_setting", setting, "ringworld/runtime/haze_enabled=%s" % setting)
	var static_enabled := bool(probe.call("HazeEnabled"))
	_rec(group, p, "haze_enabled_static", static_enabled, "RingAtmosphereModel.Enabled=%s" % static_enabled)
	var model := int(diag.get("haze_model", -1))
	var model_group := "C3-model" if group == "C3" else "C3-model-load"
	_rec(model_group, p, "haze_model_physical", model == 1, "haze_model=%d (want 1 Physical), static=%d, quality=%d" % [model, int(probe.call("HazeModel")), int(probe.call("HazeQuality"))])


func _check_shadows_and_haze(p: int) -> void:
	var sun_on := sun.shadow_enabled
	_rec("C1", p, "star_light_shadow_enabled", sun_on, "StarLight.shadow_enabled=%s" % sun_on)
	var requested := bool(graphics.get("RequestedSunShadowsEnabled"))
	_rec("C1", p, "requested_sun_shadows", requested, "RequestedSunShadowsEnabled=%s" % requested)
	var distance := float(graphics.get("EffectiveShadowDistanceMeters"))
	_rec("C1", p, "shadow_distance_ge_250", distance >= 250.0, "EffectiveShadowDistanceMeters=%.1f" % distance)
	var atlas := int(graphics.get("EffectiveDirectionalShadowAtlasSize"))
	_rec("C1", p, "atlas_ge_2048", atlas >= 2048, "EffectiveDirectionalShadowAtlasSize=%d" % atlas)
	var macro := bool(graphics.get("EffectiveTerrainMacroShadowsEnabled"))
	_rec("C2", p, "terrain_macro_shadows", macro, "EffectiveTerrainMacroShadowsEnabled=%s" % macro)
	var trace := float(graphics.get("EffectiveTerrainMacroShadowMaxTraceMeters"))
	_rec("C2", p, "macro_trace_ge_64000", trace >= 64000.0, "EffectiveTerrainMacroShadowMaxTraceMeters=%.0f" % trace)
	var coverage := float(coordinator.get("TerrainShadowCoverageMeters"))
	_rec("C2", p, "terrain_shadow_coverage", coverage > 0.0, "TerrainLodCoordinator.TerrainShadowCoverageMeters=%.0f" % coverage)
	_check_haze("C3", p)


func _check_clouds_and_weather(p: int, curtains: Node) -> void:
	var b_enabled := bool(cloud_bridge.get("Enabled"))
	var b_render := bool(cloud_bridge.get("CloudRenderingEnabled"))
	var b_visible := bool(cloud_bridge.get("RuntimeVisible"))
	var layer := game.get_node_or_null("RingCloudLayer") as Node3D
	var layer_visible := layer != null and layer.visible
	var shell := bool(scaled_far.get("FallbackCloudShellVisible"))
	var c5 := (b_enabled and b_render and b_visible) or (layer_visible and shell)
	_rec("C5", p, "clouds_active", c5, "bridge Enabled=%s CloudRenderingEnabled=%s RuntimeVisible=%s | RingCloudLayer.visible=%s FallbackCloudShellVisible=%s"
		% [b_enabled, b_render, b_visible, layer_visible, shell])
	var cast := bool(cloud_bridge.get("CastCloudShadows"))
	_rec("C6", p, "cast_cloud_shadows", cast, "CastCloudShadows=%s" % cast)
	var receivers_on := bool(cloud_bridge.get("CloudShadowReceiversEnabled"))
	_rec("C6", p, "cloud_shadow_receivers_enabled", receivers_on, "CloudShadowReceiversEnabled=%s" % receivers_on)
	var projector := bool(cloud_bridge.call("IsCloudShadowProjectorEnabled"))
	_rec("C6", p, "cloud_shadow_projector_enabled", projector, "IsCloudShadowProjectorEnabled()=%s" % projector)
	var receiver_count := int(cloud_bridge.call("GetCloudShadowReceiverCount"))
	_rec("C6", p, "cloud_shadow_receiver_count", receiver_count > 0, "GetCloudShadowReceiverCount()=%d" % receiver_count)
	var w_enabled := bool(weather.get("Enabled"))
	var particles := int(weather.get("MaximumParticleCount"))
	_rec("C7", p, "local_weather_enabled", w_enabled, "LocalWeatherEffects.Enabled=%s" % w_enabled)
	_rec("C7", p, "max_particles_ge_512", particles >= 512, "MaximumParticleCount=%d" % particles)
	var eligible := curtains != null and bool(curtains.get("ProviderEligible"))
	_rec("C7", p, "distant_rain_curtains_eligible", eligible, "DistantRainCurtains %s ProviderEligible=%s" % ["found" if curtains != null else "MISSING", eligible])
	var lightning := bool(weather.get("EnableLightning"))
	_rec("C8", p, "enable_lightning", lightning, "EnableLightning=%s" % lightning)


func _check_poi(p: int, reference: int) -> void:
	var shader_count := int(probe.call("PoiShaderCount"))
	_rec("C4", p, "poi_shaders_registered", shader_count > 0, "RingPoiHaze.RegisteredPoiShaderCount=%d (RegisteredCount=%d)" % [shader_count, int(probe.call("PoiRegisteredCount"))])
	var scope := "EmitterSites"
	var found := _poi_materials(game.get_node_or_null("EmitterSites"))
	if found.is_empty():
		scope = "game tree (EmitterSites had none)"
		found = _poi_materials(game)
	var frame_valid := 0
	var light_valid := 0
	for mat in found:
		if mat.get_shader_parameter("ring_atmosphere_ray_frame_valid") == true:
			frame_valid += 1
		if mat.get_shader_parameter("poi_ring_lighting_valid") == true:
			light_valid += 1
	var detail := "scope=%s found=%d ray_frame_valid=%d lighting_valid=%d" % [scope, found.size(), frame_valid, light_valid]
	_rec("C4", p, "poi_materials_haze_and_lighting_valid", found.size() > 0 and frame_valid == found.size() and light_valid == found.size(), detail)
	var extents := int(probe.call("LargePoiShadowCount"))
	_rec("C4", p, "large_poi_shadow_extents_unchanged", extents == reference, "RingLargePoiShadowExtents.Count=%d reference(p=3)=%d" % [extents, reference])


func _poi_materials(scope_root: Node) -> Array:
	var result: Array = []
	if scope_root == null:
		return result
	var seen := {}
	var stack: Array[Node] = [scope_root]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		stack.append_array(node.get_children(true))
		if not node is GeometryInstance3D:
			continue
		var candidates: Array = [(node as GeometryInstance3D).material_override]
		if node is MeshInstance3D:
			var mesh: Mesh = (node as MeshInstance3D).mesh
			if mesh != null:
				for i in mesh.get_surface_count():
					candidates.append((node as MeshInstance3D).get_surface_override_material(i))
					candidates.append(mesh.surface_get_material(i))
		for m in candidates:
			var sm := m as ShaderMaterial
			if sm == null or sm.shader == null or not sm.shader.resource_path in POI_SHADERS:
				continue
			if not seen.has(sm.get_instance_id()):
				seen[sm.get_instance_id()] = true
				result.append(sm)
	return result


# ------------------------------------------------------------------ result

func _finish() -> void:
	if finished:
		return
	finished = true
	var groups := {}
	var failed_groups := {}
	for c in checks:
		var g: String = c["group"]
		if not groups.has(g):
			groups[g] = {"pass": 0, "fail": 0}
		if c["ok"]:
			groups[g]["pass"] += 1
		else:
			groups[g]["fail"] += 1
			if not failed_groups.has(g):
				failed_groups[g] = []
			if not int(c["preset"]) in failed_groups[g]:
				failed_groups[g].append(int(c["preset"]))
	for g in groups:
		print("QP_CONTRACT_GROUP %s pass=%d fail=%d" % [g, groups[g]["pass"], groups[g]["fail"]])
	_write_json(failed_groups.is_empty(), failed_groups)
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	if failed_groups.is_empty():
		print("QP_CONTRACT=PASS checks=%d" % checks.size())
		quit(0)
	else:
		var parts: Array = []
		for g in failed_groups:
			parts.append("%s[p=%s]" % [g, ",".join(PackedStringArray((failed_groups[g] as Array).map(func(x): return str(x))))])
		push_error("QP_CONTRACT=FAIL " + " ".join(parts))
		quit(1)


func _write_json(passed: bool, failed_groups: Dictionary) -> void:
	if out_dir == "":
		return
	var f := FileAccess.open(out_dir.path_join("contract.json"), FileAccess.WRITE)
	if f != null:
		f.store_string(JSON.stringify({"status": "PASS" if passed else "FAIL", "failed_groups": failed_groups, "info": info, "checks": checks}, "  "))
		f.close()


func _fail(reason: String) -> void:
	if finished:
		return
	finished = true
	_write_json(false, {"fatal": [reason]})
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	push_error("QP_CONTRACT=FAIL " + reason)
	quit(1)
