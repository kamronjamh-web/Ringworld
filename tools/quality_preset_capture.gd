extends SceneTree

## TEST ONLY. Quality-preset capture harness (docs/quality-presets-plan.md, WO-0). GPU / windowed, not --headless.
## Boots the isolated certified runtime world (tools/shadow_harness_common.gd) at a chosen graphics preset, records the boot / first-minute
## hitch trace (tools/WarmStallProbe.cs), then at each pose samples a baseline window and A-X-A ablation windows through existing runtime
## switches, plus a gameplay-speed flight. GPU power / P-state is logged for the whole run and summarised per window (report "power").
## Labels: see ALL_LABELS, plus proc_off:<game child> (process_mode DISABLED) and prop:<game child>:<property>=<int|float|bool>.
## Args (after --): --preset=3 --res=1920x1080 | --maximized  --poses=a,b  --labels=a,b  --first-pose-labels=a,b  --seconds=8 --warmup=2 --boot-seconds=60
##                  --flights=72@150,240@500 (speed m/s @ altitude m, 30 s each)  --flight-seconds=30  --out=<abs dir>  --idle-timeout=150
##                  --snap (PNG per window)  --user-prefs
##                  --a1000 (require the RTX A1000; windows whose power verdict is invalid get "invalid")
##                  --release-note=<text> (stored verbatim as report["build_note"], e.g. the C# build configuration)
##                  --hold-seconds=<s> (idle after QP_BASELINE=PASS before quitting, for an attached dotnet-trace rundown)
##                  --gc-pace=<gen>:<mb>[:<frames>] (or / separators) / --gc-latency=<GCLatencyMode> (GC A-B experiments in tools/WarmStallProbe.cs)

const COMMON := preload("res://tools/shadow_harness_common.gd")
const BRIDGE := preload("res://tools/ShadowTimingBridge.cs")
const PROBE := preload("res://tools/WarmStallProbe.cs")
const HITCH_MS := 25.0
const ALL_LABELS := ["sun_map_off", "planet_map_off", "cloud_shadow_gen_off", "macro_off", "tree_shadows_off", "hs_near", "sunshine_off", "far_vp_off",
	"far_process_off", "near_terrain_hidden", "far_terrain_hidden", "grass_hidden", "trees_hidden", "grass_process_off", "trees_process_off",
	"water_fft_off", "ssao_off", "haze_on", "baseline_repeat", "cloud_steps_128", "cloud_half_res", "cloud_interleave_4x4", "fsr2_quality", "fsr2_balanced",
	"far_vp_half", "atlas_4096", "sun_6km", "haze_physical_q4", "haze_physical_q8", "far_vp_every2", "tree_paused"]

var game: Node
var explorer: Node
var surface: Node
var sun: DirectionalLight3D
var planet: DirectionalLight3D
var graphics: Node
var cloud_bridge: Node
var clouds_res: Object
var atmosphere: Node
var trees: Node
var grass: Node
var height_shadow: Node
var coordinator: Node
var monitor: Node
var drawer: Node
var scaled_far: Node
var water_fft: Node
var ao: Node
var probe: Node
var timing: RefCounted
var measured_viewports: Array[Viewport] = []
var restore_stack: Array[Callable] = []
var report := {}
var out_dir := ""
var window_seconds := 8.0
var warmup_seconds := 2.0
var idle_timeout := 150.0
var failed := false
var orig := {}
var near_terrain_nodes: Array = []
var far_terrain_nodes: Array = []
var held := {}
var machine := {}
var power_pid := -1
var power_path := ""
var require_a1000 := false
var run_started := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	# Run once. The exported entry (tools/quality_preset_capture_entry.gd) attaches this script with SceneTree.set_script(), which runs
	# _init (one deferred _run) besides the entry's own deferred call; a second pass booted a second game world into the same root
	# viewport (2-3x draw calls, two Sunshine bridges, memory-pressure terrain fallback, both worlds saving into one fixture).
	if run_started:
		return
	run_started = true
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required"); return
	window_seconds = float(COMMON.arg_value("--seconds=", "8"))
	warmup_seconds = float(COMMON.arg_value("--warmup=", "2"))
	idle_timeout = float(COMMON.arg_value("--idle-timeout=", "150"))
	var preset := int(COMMON.arg_value("--preset=", "3"))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	if COMMON.has_flag("--maximized"):
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_MAXIMIZED)
	else:
		var res := COMMON.arg_value("--res=", "1920x1080").split("x")
		DisplayServer.window_set_size(Vector2i(int(res[0]), int(res[1])))
		root.size = Vector2i(int(res[0]), int(res[1]))
	out_dir = COMMON.arg_value("--out=", ProjectSettings.globalize_path("res://.godot/quality_presets/run_%d" % Time.get_ticks_usec()))
	DirAccess.make_dir_recursive_absolute(out_dir)
	machine = COMMON.machine_info()
	print("QP_BASELINE adapter=", machine["adapter"], " preset=", preset, " out=", out_dir)
	report = {"status": "RUNNING", "machine": machine, "args": OS.get_cmdline_user_args(), "preset_requested": preset, "window_seconds": window_seconds,
		"warmup_seconds": warmup_seconds, "hitch_ms": HITCH_MS, "poses": {}, "windows": [], "skipped": {}, "flights": [],
		"build_note": COMMON.arg_value("--release-note=", ""), "csharp_assembly": {"path": "", "debug_build": OS.is_debug_build()}}
	require_a1000 = COMMON.has_flag("--a1000")
	if require_a1000 and not bool(machine["authoritative"]):
		_fail("--a1000 requires the RTX A1000"); return
	power_path = out_dir.path_join("power_log.csv")
	power_pid = COMMON.start_power_log(power_path)
	report["power_log"] = {"path": power_path, "query": COMMON.POWER_QUERY, "interval_ms": 500, "pid": power_pid}

	probe = PROBE.new()
	probe.name = "QpStallProbe"
	probe.set("DetailThresholdMs", HITCH_MS)
	# GC A-B experiments (debug tooling only): --gc-pace=<gen>:<mb>[:<frames>]  --gc-latency=<GCLatencyMode name>
	var gc_pace := COMMON.arg_value("--gc-pace=", "")
	if not gc_pace.is_empty():
		var pace := gc_pace.replace("/", ":").split(":")
		probe.set("GcPaceGeneration", int(pace[0]))
		probe.set("GcPaceMegabytes", float(pace[1]) if pace.size() > 1 else 0.0)
		probe.set("GcPaceFrames", int(pace[2]) if pace.size() > 2 else 0)
	probe.set("GcLatencyMode", COMMON.arg_value("--gc-latency=", ""))
	root.add_child(probe)
	await process_frame
	probe.call("Begin")
	probe.call("SetContext", "boot")
	var boot_start := Time.get_ticks_msec()
	var fixture_root := "res://.godot/quality-presets-fixture/%d" % Time.get_ticks_usec()
	game = await _create_game(fixture_root, preset)
	if game == null:
		_fail("game boot failed"); return
	report["boot_ready_seconds"] = (Time.get_ticks_msec() - boot_start) / 1000.0
	if not _bind():
		return
	probe.call("InstrumentWorld", game)
	# Keep the user's mouse free and stop Escape from pausing the world mid-capture (the pause menu sets SceneTree.paused).
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	var pause_menu := game.get_node_or_null("LodPauseMenu")
	for blocked in [pause_menu, drawer]:
		if blocked != null:
			blocked.set_process_unhandled_input(false)
			blocked.set_process_unhandled_key_input(false)
			blocked.set_process_input(false)
			blocked.set_process_shortcut_input(false)
	monitor.process_mode = Node.PROCESS_MODE_DISABLED
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	_enable_viewport_timing()
	timing = BRIDGE.new()
	timing.call("Begin", false)
	report["quality"] = _quality_identity()
	print("QP_BASELINE quality ", JSON.stringify(report["quality"]))

	# Boot / first-minute trace with clocks running (what a player sees right after load).
	var boot_seconds := float(COMMON.arg_value("--boot-seconds=", "60"))
	probe.call("SetContext", "first_minute")
	var boot := await _sample_series("first_minute", boot_seconds, true)
	report["first_minute"] = boot
	print("QP_FIRST_MINUTE ", JSON.stringify(_brief(boot)))
	var idle_ok: bool = await COMMON.wait_builds_idle(self, game, 180.0, 60)
	report["initial_idle"] = idle_ok
	report["initial_idle_seconds"] = (Time.get_ticks_msec() - boot_start) / 1000.0
	_capture_original()
	var base: Dictionary = COMMON.pose_now(game)
	report["base_pose"] = base
	COMMON.hold_world_clocks(game)
	atmosphere.set("storm_auto_exposure_enabled", false)

	var pose_names: PackedStringArray = COMMON.csv_arg("--poses=")
	if pose_names.is_empty():
		pose_names = PackedStringArray(["forest_noon", "coast", "emitter_rim", "alt_1km", "alt_30km", "night_planet_lit", "storm"])
	var labels: PackedStringArray = COMMON.csv_arg("--labels=")
	for label in labels:
		if not label in ALL_LABELS and not label.begins_with("proc_off:") and not label.begins_with("prop:"):
			_fail("unknown label " + label); return
	var first_labels: PackedStringArray = COMMON.csv_arg("--first-pose-labels=")
	var first_pose_done := false
	for pose_name in pose_names:
		probe.call("SetContext", "pose_setup:" + pose_name)
		var info: Dictionary = await _go(pose_name, base)
		if info.has("error"):
			report["skipped"][pose_name] = info
			print("QP_POSE %s SKIPPED %s" % [pose_name, str(info["error"])])
			continue
		report["poses"][pose_name] = {"info": info, "windows": []}
		print("QP_POSE %s ready %s" % [pose_name, JSON.stringify(info.get("time", {}))])
		var sequence: Array = ["baseline"]
		var pose_labels: Array = Array(labels)
		if not first_pose_done:
			pose_labels.append_array(Array(first_labels))
			first_pose_done = true
		for label in pose_labels:
			if label == "planet_map_off" and not pose_name.begins_with("night"):
				continue
			sequence.append(label)
			sequence.append("baseline")
		for label in sequence:
			_restore()
			if not await _apply(label):
				_fail("label failed " + label); return
			probe.call("SetContext", "%s:%s" % [pose_name, label])
			var external_pause: bool = paused and label != "tree_paused"
			if external_pause:
				paused = false
			await COMMON.settle(self, warmup_seconds)
			var w := await _sample_series("%s:%s" % [pose_name, label], window_seconds, false)
			w["external_pause_cleared"] = external_pause
			if paused and label != "tree_paused":
				w["invalid"] = "tree paused during window"
			if require_a1000 and not w.has("invalid") and not bool(w.get("power", {}).get("valid", true)):
				w["invalid"] = "power: " + str(w["power"].get("verdict", ""))
			w["pose"] = pose_name
			w["label"] = label
			w["state"] = _state_dump()
			if COMMON.has_flag("--snap"):
				var img := root.get_texture().get_image()
				img.save_png(out_dir.path_join("%s_%02d_%s.png" % [pose_name, report["poses"][pose_name]["windows"].size(), label.replace(":", "_")]))
			report["poses"][pose_name]["windows"].append(w)
			print("QP_WINDOW ", JSON.stringify(_brief(w)))
		_restore()
		if pose_name == "storm":
			cloud_bridge.call("ClearSpawnedWeather")
			COMMON.hold_world_clocks(game)
		_summarize_pose(pose_name)
		_write()

	var flights: PackedStringArray = COMMON.csv_arg("--flights=")
	for spec in flights:
		var parts := spec.split("@")
		probe.call("SetContext", "flight:" + spec)
		var flight := await _flight(float(parts[0]), float(parts[1]) if parts.size() > 1 else 150.0, base)
		flight["state"] = _state_dump()
		report["flights"].append(flight)
		print("QP_FLIGHT ", JSON.stringify(_brief(flight)))
		_write()

	report["status"] = "PASS"
	probe.call("WriteReport", out_dir)
	probe.call("End")
	timing.call("End")
	COMMON.stop_power_log(power_pid)
	_write()
	print("QP_BASELINE=PASS out=", out_dir)
	# --hold-seconds=N keeps the process alive after the report so an attached dotnet-trace session can stop and write its rundown.
	var hold := float(COMMON.arg_value("--hold-seconds=", "0"))
	if hold > 0.0:
		await create_timer(hold).timeout
	quit(0)


# ------------------------------------------------------------------ boot

func _create_game(fixture_root: String, preset: int) -> Node:
	var prior := root.get_node_or_null("WorldSession")
	if prior != null:
		prior.name = "WorldSessionOriginalQp"
	var session := COMMON.SessionStub.new()
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
		return null
	launcher.call("_save_selected_slot_settings")
	var g := COMMON.GAME.instantiate()
	launcher.call("_configure_game", g, 5000000.0)
	report["launcher_default_preset"] = int(g.get_node("GraphicsQuality").get("DefaultPreset"))
	g.get_node("GraphicsQuality").set("DefaultPreset", preset)
	g.get_node("TerrainLodCoordinator").set("DefaultPreset", preset)
	if COMMON.has_flag("--user-prefs"):
		COMMON._install_isolated_preferences(g, fixture_root)
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
	atmosphere = game.get_node_or_null("DaySideAtmosphere")
	trees = game.get_node_or_null("BiomeDecorations")
	grass = game.get_node_or_null("StandingGrass")
	height_shadow = game.get_node_or_null("TerrainHeightShadow")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	monitor = game.get_node_or_null("RingPerformanceMonitor")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	scaled_far = game.get_node_or_null("ScaledFarViewport")
	water_fft = game.get_node_or_null("WaterFftController")
	ao = game.get_node_or_null("AmbientOcclusion")
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	clouds_res = driver.get("clouds_resource") if driver != null else null
	for pair in [["Explorer", explorer], ["Terrain3DGameplay", surface], ["StarLight", sun], ["GraphicsQuality", graphics], ["SunshineCloudBridge", cloud_bridge],
			["DaySideAtmosphere", atmosphere], ["BiomeDecorations", trees], ["StandingGrass", grass], ["TerrainLodCoordinator", coordinator], ["RingPerformanceMonitor", monitor],
			["LiveTuningDrawer", drawer], ["ScaledFarViewport", scaled_far], ["WaterFftController", water_fft], ["AmbientOcclusion", ao]]:
		if pair[1] == null:
			_fail("required node missing: " + str(pair[0])); return false
	return true


func _enable_viewport_timing() -> void:
	measured_viewports = [root]
	for node in _all_nodes(game):
		if node is SubViewport:
			measured_viewports.append(node)
	for viewport in measured_viewports:
		RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), true)


func _all_nodes(node: Node) -> Array[Node]:
	var result: Array[Node] = [node]
	for child in node.get_children(true):
		result.append_array(_all_nodes(child))
	return result


func _geometry_nodes(owner: Node) -> Array:
	var result: Array = []
	for node in _all_nodes(owner):
		if node is GeometryInstance3D:
			result.append(node)
	return result


func _quality_identity() -> Dictionary:
	var env: Environment = (game.get_node("WorldEnvironment") as WorldEnvironment).environment
	var haze: Dictionary = graphics.call("GetAtmosphereTuningDiagnostics")
	return {"preset": graphics.get("CurrentPresetName"), "preset_enum": int(graphics.get("CurrentPreset")), "lod_preset": coordinator.get("CurrentPresetName"),
		"window": DisplayServer.window_get_size(), "root_size": root.size, "render_scale": root.scaling_3d_scale, "scaling_mode": root.scaling_3d_mode,
		"msaa": root.msaa_3d, "taa": root.use_taa, "screen_aa": root.screen_space_aa, "atlas": int(graphics.get("EffectiveDirectionalShadowAtlasSize")),
		"sun_max": sun.directional_shadow_max_distance, "sun_mode": sun.directional_shadow_mode, "planet_max": planet.directional_shadow_max_distance if planet else 0.0,
		"ssao": env.ssao_enabled, "ssil": env.ssil_enabled, "ssr": env.ssr_enabled, "glow": env.glow_enabled, "sdfgi": env.sdfgi_enabled, "volumetric_fog": env.volumetric_fog_enabled,
		"haze": haze, "sunshine_rendering": bool(cloud_bridge.get("CloudRenderingEnabled")), "raymarch_steps": int(cloud_bridge.get("RaymarchSteps")),
		"cloud_resolution_scale": int(cloud_bridge.get("CloudResolutionScale")), "cloud_shadow_gen": bool(cloud_bridge.get("GpuShadowGenerationEnabled")),
		"terrain_cloud_shadow": bool(cloud_bridge.get("TerrainCloudShadowEnabled")), "hs_enabled": bool(graphics.get("EffectiveHeightShadowEnabled")),
		"water_fft": bool(water_fft.get("Enabled")), "fsr_sharpness": root.fsr_sharpness, "viewports": measured_viewports.size()}


# ------------------------------------------------------------------ labels

func _capture_original() -> void:
	orig = {"sun_requested": bool(graphics.get("RequestedSunShadowsEnabled")), "macro": bool(graphics.get("EffectiveTerrainMacroShadowsEnabled")),
		"planet_cast": bool(atmosphere.get("planet_light_cast_shadows")), "cloud_gen": bool(cloud_bridge.get("GpuShadowGenerationEnabled")),
		"tree_dist": float(trees.get("TreeSunShadowDistanceMeters")), "tree_batches": int(trees.get("MaximumTreeSunShadowBatches")),
		"hs_enabled": bool(graphics.get("EffectiveHeightShadowEnabled")), "hs_near_enabled": bool(graphics.get("EffectiveHeightShadowNearMapEnabled")),
		"sunshine": bool(cloud_bridge.get("CloudRenderingEnabled")), "water_fft": bool(water_fft.get("Enabled")),
		"ssao": (game.get_node("WorldEnvironment") as WorldEnvironment).environment.ssao_enabled,
		"haze": bool((graphics.call("GetAtmosphereTuningDiagnostics") as Dictionary).get("enabled", false))}
	report["original_state"] = orig.duplicate()


func _restore() -> void:
	while not restore_stack.is_empty():
		var undo: Callable = restore_stack.pop_back()
		undo.call()


func _refresh_terrain_nodes() -> void:
	near_terrain_nodes = [surface]
	for node in _all_nodes(game):
		if node is Node3D and node.get_script() != null and "RingTerrainProxySector" in node.get_script().resource_path:
			near_terrain_nodes.append(node)
	far_terrain_nodes = []
	for node in _all_nodes(scaled_far):
		if node is MeshInstance3D and (node.name == "ScaledSeededRingSurface" or node.name.begins_with("ScaledFarLod")):
			far_terrain_nodes.append(node)


func _apply(label: String) -> bool:
	match label:
		"baseline", "baseline_repeat":
			pass
		"sun_map_off":
			graphics.call("SetShadowTuningOverride", "Enabled", false)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "Enabled", orig["sun_requested"]))
		"planet_map_off":
			atmosphere.set("planet_light_cast_shadows", false)
			restore_stack.append(func(): atmosphere.set("planet_light_cast_shadows", orig["planet_cast"]))
		"cloud_shadow_gen_off":
			cloud_bridge.set("GpuShadowGenerationEnabled", false)
			if clouds_res != null and clouds_res.has_method("set_gpu_cloud_shadow_generation_enabled"):
				clouds_res.call("set_gpu_cloud_shadow_generation_enabled", false)
			restore_stack.append(func():
				cloud_bridge.set("GpuShadowGenerationEnabled", orig["cloud_gen"])
				if clouds_res != null and clouds_res.has_method("set_gpu_cloud_shadow_generation_enabled"):
					clouds_res.call("set_gpu_cloud_shadow_generation_enabled", orig["cloud_gen"]))
		"macro_off":
			graphics.call("SetShadowTuningOverride", "TerrainMacroEnabled", false)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "TerrainMacroEnabled", orig["macro"]))
		"tree_shadows_off":
			trees.set("TreeSunShadowDistanceMeters", 0.0)
			trees.set("MaximumTreeSunShadowBatches", 0)
			restore_stack.append(func():
				trees.set("TreeSunShadowDistanceMeters", orig["tree_dist"])
				trees.set("MaximumTreeSunShadowBatches", orig["tree_batches"]))
		"hs_near":
			if height_shadow == null:
				return false
			graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", true)
			graphics.call("SetShadowTuningOverride", "HeightShadowNearMapEnabled", true)
			height_shadow.set("ReceiversEnabled", true)
			restore_stack.append(func():
				graphics.call("SetShadowTuningOverride", "HeightShadowNearMapEnabled", orig["hs_near_enabled"])
				graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", orig["hs_enabled"]))
			await _wait_height_shadow()
		"sunshine_off":
			cloud_bridge.set("CloudRenderingEnabled", false)
			restore_stack.append(func(): cloud_bridge.set("CloudRenderingEnabled", orig["sunshine"]))
		"far_vp_off":
			var vp: SubViewport = scaled_far.get("OuterViewportForDiagnostics")
			if vp == null:
				return false
			var prev_mode := vp.render_target_update_mode
			vp.render_target_update_mode = SubViewport.UPDATE_DISABLED
			restore_stack.append(func(): if is_instance_valid(vp): vp.render_target_update_mode = prev_mode)
		"far_process_off", "grass_process_off", "trees_process_off":
			var node: Node = {"far_process_off": scaled_far, "grass_process_off": grass, "trees_process_off": trees}[label]
			var was := node.is_processing()
			node.set_process(false)
			restore_stack.append(func(): if is_instance_valid(node): node.set_process(was))
		"near_terrain_hidden", "far_terrain_hidden":
			_refresh_terrain_nodes()
			var nodes: Array = near_terrain_nodes if label == "near_terrain_hidden" else far_terrain_nodes
			var vis: Array = []
			for n in nodes:
				vis.append(n.visible)
				n.visible = false
			restore_stack.append(func():
				for i in nodes.size():
					if is_instance_valid(nodes[i]): nodes[i].visible = vis[i])
		"grass_hidden", "trees_hidden":
			var geo := _geometry_nodes(grass if label == "grass_hidden" else trees)
			var layers: Array = []
			for g in geo:
				layers.append(g.layers)
				g.layers = 0
			restore_stack.append(func():
				for i in geo.size():
					if is_instance_valid(geo[i]): geo[i].layers = layers[i])
		"water_fft_off":
			water_fft.set("Enabled", false)
			restore_stack.append(func(): water_fft.set("Enabled", orig["water_fft"]))
		"ssao_off":
			ao.call("SetAmbientOcclusionEnabled", false)
			restore_stack.append(func(): ao.call("SetAmbientOcclusionEnabled", orig["ssao"]))
		"haze_on":
			drawer.call("SetAtmosphereEnabled", true)
			restore_stack.append(func(): drawer.call("SetAtmosphereEnabled", orig["haze"]))
		"cloud_steps_128", "cloud_half_res", "cloud_interleave_4x4":
			var prop: String = {"cloud_steps_128": "RaymarchSteps", "cloud_half_res": "CloudResolutionScale", "cloud_interleave_4x4": "CloudInterleave"}[label]
			var value: int = {"cloud_steps_128": 128, "cloud_half_res": 1, "cloud_interleave_4x4": 2}[label]
			var before = cloud_bridge.get(prop)
			cloud_bridge.set(prop, value)
			cloud_bridge.call("ApplyTemporalQualityTuning", true)
			restore_stack.append(func():
				cloud_bridge.set(prop, before)
				cloud_bridge.call("ApplyTemporalQualityTuning", true))
			await COMMON.settle(self, 2.0)
		"fsr2_quality", "fsr2_balanced":
			var mode := root.scaling_3d_mode
			var scale := root.scaling_3d_scale
			root.scaling_3d_mode = Viewport.SCALING_3D_MODE_FSR2
			root.scaling_3d_scale = 0.67 if label == "fsr2_quality" else 0.59
			restore_stack.append(func():
				root.scaling_3d_mode = mode
				root.scaling_3d_scale = scale)
			await COMMON.settle(self, 1.0)
		"far_vp_half":
			scaled_far.call("ApplyRuntimeRenderQuality", 0.5, int(scaled_far.get("AroundSegments")), int(scaled_far.get("AcrossSegments")))
			var around := int(scaled_far.get("AroundSegments"))
			var across := int(scaled_far.get("AcrossSegments"))
			restore_stack.append(func(): scaled_far.call("ApplyRuntimeRenderQuality", 1.0, around, across))
			await COMMON.settle(self, 1.0)
		"far_vp_every2":
			# Proposal probe: the far viewport renders every other frame (UPDATE_ONCE re-armed on even frames).
			var fvp: SubViewport = scaled_far.get("OuterViewportForDiagnostics")
			if fvp == null:
				return false
			var prev_update := fvp.render_target_update_mode
			var active := [true]
			var tick := func():
				if active[0] and is_instance_valid(fvp) and Engine.get_process_frames() % 2 == 0:
					fvp.render_target_update_mode = SubViewport.UPDATE_ONCE
			process_frame.connect(tick)
			fvp.render_target_update_mode = SubViewport.UPDATE_ONCE
			restore_stack.append(func():
				active[0] = false
				process_frame.disconnect(tick)
				if is_instance_valid(fvp): fvp.render_target_update_mode = prev_update)
		"tree_paused":
			# Reference: every node's process/physics stops (what the pause menu does); measures the total per-frame script-driven RS work.
			paused = true
			restore_stack.append(func(): paused = false)
		"atlas_4096":
			graphics.call("SetShadowTuningOverride", "AtlasSize", 4096)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "AtlasSize", int(report["quality"]["atlas"])))
		"sun_6km":
			var prev_max := sun.directional_shadow_max_distance
			graphics.call("SetShadowTuningOverride", "MaxDistanceMeters", 6000.0)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "MaxDistanceMeters", prev_max))
		"haze_physical_q4", "haze_physical_q8":
			drawer.call("SetAtmosphereHazeModel", 1)
			drawer.call("SetAtmosphereHazeQuality", 4 if label == "haze_physical_q4" else 8)
			drawer.call("SetAtmosphereEnabled", true)
			restore_stack.append(func():
				drawer.call("SetAtmosphereHazeModel", 0)
				drawer.call("SetAtmosphereHazeQuality", 8)
				drawer.call("SetAtmosphereEnabled", orig["haze"]))
			await COMMON.settle(self, 1.0)
		_:
			if label.begins_with("proc_off:"):
				var target := game.get_node_or_null(label.trim_prefix("proc_off:"))
				if target == null:
					return true
				var prev_mode := target.process_mode
				target.process_mode = Node.PROCESS_MODE_DISABLED
				restore_stack.append(func(): if is_instance_valid(target): target.process_mode = prev_mode)
				return true
			if label.begins_with("prop:"):
				# prop:<game child>:<property>=<value> sets one property for the window (int, float or bool), restored afterwards.
				var spec := label.trim_prefix("prop:")
				var colon := spec.find(":")
				var eq := spec.find("=")
				if colon < 0 or eq < colon:
					return false
				var node := game.get_node_or_null(spec.substr(0, colon))
				if node == null:
					return false
				var prop := spec.substr(colon + 1, eq - colon - 1)
				var raw := spec.substr(eq + 1)
				var value: Variant = (raw == "true") if raw in ["true", "false"] else (float(raw) if raw.contains(".") else int(raw))
				var before: Variant = node.get(prop)
				node.set(prop, value)
				restore_stack.append(func(): if is_instance_valid(node): node.set(prop, before))
				return true
			return false
	return true


func _wait_height_shadow() -> void:
	var deadline := Time.get_ticks_msec() + 60000
	while Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
		var d: Dictionary = height_shadow.call("GetDiagnostics")
		if int(d.get("generation", 0)) > 0 and float(d.get("crossfade", 0.0)) >= 0.999 and str(d.get("phase", "")) == "Idle" and (bool(graphics.get("NearSunMapActive")) or bool(graphics.get("NearPlanetMapActive"))):
			return
	print("QP warning: height shadow did not settle")


# ------------------------------------------------------------------ poses

func _go(pose_name: String, base: Dictionary) -> Dictionary:
	if COMMON.POSES.has(pose_name):
		return await COMMON.go_pose(self, game, base, pose_name, 4.0, idle_timeout)
	var info := {"pose": pose_name}
	match pose_name:
		"alt_30km":
			explorer.call("TeleportToCanonicalMeters", float(base["around"]), float(base["vertical"]) + 30000.0, float(base["across"]), float(base["yaw"]), -25.0, true)
			await COMMON.settle(self, 0.2)
			info["streaming_idle"] = await COMMON.wait_builds_idle(self, game, idle_timeout)
			info["time"] = COMMON.apply_time(game, {"mode": "noon"})
		"coast":
			var spot := _find_coast(float(base["around"]), float(base["across"]))
			if spot.is_empty():
				info["error"] = "no coast found within 40 km"
				return info
			info["coast"] = spot
			explorer.call("TeleportToCanonicalMeters", float(spot["around"]), float(spot["vertical"]), float(spot["across"]), float(base["yaw"]), -18.0, true)
			await COMMON.settle(self, 0.2)
			info["streaming_idle"] = await COMMON.wait_builds_idle(self, game, idle_timeout)
			info["time"] = COMMON.apply_time(game, {"mode": "noon"})
		"storm":
			explorer.call("TeleportToCanonicalMeters", float(base["around"]), float(base["vertical"]) + 2.0, float(base["across"]), float(base["yaw"]), 5.0, true)
			await COMMON.settle(self, 0.2)
			info["streaming_idle"] = await COMMON.wait_builds_idle(self, game, idle_timeout)
			info["time"] = COMMON.apply_time(game, {"mode": "noon"})
			cloud_bridge.set("SpawnDistanceMeters", 0.0)
			cloud_bridge.call("SpawnStormFrontAhead")
			var weather := game.get_node_or_null("RingVolumetricClouds")
			if weather != null:
				weather.set("CloudSimulationPaused", false)
			await COMMON.settle(self, 20.0)
			info["rain"] = _weather_state()
			if COMMON.has_flag("--snap"):
				# Proof that rain and a lightning flash still render: one frame right after a forced preview bolt.
				var lwe := game.get_node_or_null("LocalWeatherEffects")
				if lwe != null and lwe.has_method("TriggerLightningPreview"):
					lwe.call("TriggerLightningPreview")
					for _i in 3:
						await RenderingServer.frame_post_draw
					root.get_texture().get_image().save_png(out_dir.path_join("storm_lightning_preview.png"))
					info["lightning_preview"] = _weather_state()
		_:
			info["error"] = "unknown pose"
			return info
	await COMMON.settle(self, 4.0)
	return info


func _state_dump() -> Dictionary:
	var fvp: SubViewport = scaled_far.get("OuterViewportForDiagnostics")
	var d := {"paused": paused, "time_scale": Engine.time_scale, "explorer_processing": explorer.is_processing(), "explorer_physics": explorer.is_physics_processing(),
		"explorer_noclip": explorer.get("NoclipEnabled"), "bridge_enabled": cloud_bridge.get("Enabled"), "bridge_runtime_visible": cloud_bridge.get("RuntimeVisible"),
		"bridge_processing": cloud_bridge.is_processing(), "far_vp_update": fvp.render_target_update_mode if fvp else -1, "far_vp_size": fvp.size if fvp else Vector2i.ZERO,
		"far_processing": scaled_far.is_processing(), "pose": COMMON.pose_now(game)}
	var weather := game.get_node_or_null("RingVolumetricClouds")
	if weather != null:
		d["weather_sim_paused"] = weather.get("CloudSimulationPaused")
	# Effective terrain quality: the certification monitor's memory guard can drop Very High to High mid-run (working set > 90% of its limit).
	d["lod_preset"] = coordinator.get("CurrentPresetName")
	d["adaptive_fallbacks"] = coordinator.get("AdaptiveFallbackCount")
	var cert := game.get_node_or_null("CertificationMonitor")
	if cert != null:
		d["working_set_mb"] = int(cert.get("CurrentWorkingSetBytes")) / 1048576
		d["terrain_est_mb"] = int(cert.get("EstimatedTerrainBytes")) / 1048576
	return d


func _weather_state() -> Dictionary:
	var weather := game.get_node_or_null("LocalWeatherEffects")
	var result := {}
	if weather != null:
		for prop in ["Enabled", "RainIntensity", "CurrentRainIntensity", "PrecipitationIntensity", "LightningFlashCount", "SurfaceWetness"]:
			var v = weather.get(prop)
			if v != null:
				result[prop] = v
		if weather.has_method("GetRainHazeDiagnostics"):
			var haze: Dictionary = weather.call("GetRainHazeDiagnostics")
			result["rain_haze"] = {"visible": haze.get("visible"), "compositor_active": haze.get("compositor_active"), "compositor_dispatches": haze.get("compositor_dispatches"), "rain_intensity": haze.get("rain_intensity")}
	return result


func _find_coast(around0: float, across0: float) -> Dictionary:
	var best := {}
	var best_d := INF
	var step := 400.0
	for ix in range(-100, 101):
		for iy in range(-60, 61):
			var a := around0 + ix * step
			var c := across0 + iy * step
			var h := float(surface.call("SampleHeight", a, c))
			if h > -6.0:
				continue
			var d := Vector2(ix, iy).length() * step
			if d >= best_d:
				continue
			# water point: walk back toward the base until land, so the camera sits just offshore
			var t := 1.0
			var shore_h := h
			while t > 0.0:
				t -= 0.02
				var sh := float(surface.call("SampleHeight", lerpf(around0, a, t), lerpf(across0, c, t)))
				if sh > 1.0:
					break
				shore_h = sh
			var ofs_t := minf(1.0, t + 0.02 + 150.0 / maxf(d, 1.0))
			var wa := lerpf(around0, a, ofs_t)
			var wc := lerpf(across0, c, ofs_t)
			best = {"around": wa, "across": wc, "vertical": maxf(0.0, float(surface.call("SampleHeight", wa, wc))) + 18.0, "distance_m": d * ofs_t, "water_depth_m": h}
			best_d = d
	return best


# ------------------------------------------------------------------ flight

func _key(code: int, down: bool) -> void:
	if held.get(code, false) == down:
		return
	held[code] = down
	var e := InputEventKey.new()
	e.keycode = code
	e.physical_keycode = code
	e.pressed = down
	Input.parse_input_event(e)


func _flight(speed: float, alt: float, base: Dictionary) -> Dictionary:
	var weather := game.get_node_or_null("RingVolumetricClouds")
	var cycle := game.get_node_or_null("RingDayNightCycle")
	COMMON.apply_time(game, {"mode": "noon"})
	var ground := float(surface.call("SampleHeight", float(base["around"]), float(base["across"])))
	explorer.call("TeleportToCanonicalMeters", float(base["around"]), ground + alt, float(base["across"]), float(base["yaw"]), -8.0, true)
	await COMMON.settle(self, 0.3)
	await COMMON.wait_builds_idle(self, game, idle_timeout)
	explorer.call("SelectNoclipSpeedTier", 1 if speed < 150.0 else 2)
	if weather != null:
		weather.set("CloudSimulationPaused", false)
	if cycle != null:
		cycle.set("Paused", false)
	_key(KEY_W, true)
	await COMMON.settle(self, 2.0)
	var start_pose: Dictionary = COMMON.pose_now(game)
	var series := await _sample_series("flight_%d_%d" % [int(speed), int(alt)], float(COMMON.arg_value("--flight-seconds=", "30")), true)
	var end_pose: Dictionary = COMMON.pose_now(game)
	_key(KEY_W, false)
	COMMON.hold_world_clocks(game)
	series["requested_speed"] = speed
	series["altitude"] = alt
	series["distance_m"] = Vector2(float(end_pose["around"]) - float(start_pose["around"]), float(end_pose["across"]) - float(start_pose["across"])).length()
	series["start"] = start_pose
	series["end"] = end_pose
	return series


# ------------------------------------------------------------------ sampling

func _pct(sorted: Array, q: float) -> float:
	return COMMON.percentile(sorted, q)


func _dist(values: Array) -> Dictionary:
	var s: Array = []
	for v in values:
		if not is_nan(float(v)):
			s.append(float(v))
	if s.is_empty():
		return {"n": 0}
	s.sort()
	var total := 0.0
	for v in s:
		total += v
	return {"n": s.size(), "mean": total / s.size(), "median": _pct(s, 0.5), "p95": _pct(s, 0.95), "p99": _pct(s, 0.99), "max": s[-1]}


func _sample_series(tag: String, seconds: float, with_streaming: bool) -> Dictionary:
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE  # the explorer may recapture the mouse
	await RenderingServer.frame_post_draw
	timing.call("ResetWindow")
	var t0_utc := Time.get_unix_time_from_system()
	var series := {"wall_ms": [], "root_gpu_ms": [], "root_cpu_ms": [], "sub_gpu_ms": [], "sub_cpu_ms": [], "far_gpu_ms": [], "far_cpu_ms": [], "gpu_sum_ms": [],
		"cpu_render_ms": [], "frame_setup_ms": [], "process_ms": [], "physics_ms": [], "draw_calls": [], "primitives": [], "objects": []}
	var per_vp := {}
	var hitches: Array = []
	var busy_frames := 0
	var start := Time.get_ticks_usec()
	var last := start
	var far_vp: SubViewport = scaled_far.get("OuterViewportForDiagnostics")
	while (Time.get_ticks_usec() - start) * 0.000001 < seconds:
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		var wall := (now - last) * 0.001
		last = now
		var root_gpu := RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())
		var root_cpu := RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid())
		var sub_gpu := 0.0
		var sub_cpu := 0.0
		var far_gpu := 0.0
		var far_cpu := 0.0
		for vp in measured_viewports:
			if vp == root or not is_instance_valid(vp):
				continue
			var g := RenderingServer.viewport_get_measured_render_time_gpu(vp.get_viewport_rid())
			var c := RenderingServer.viewport_get_measured_render_time_cpu(vp.get_viewport_rid())
			sub_gpu += g
			sub_cpu += c
			var key := str(vp.name)
			if not per_vp.has(key):
				per_vp[key] = {"gpu": [], "cpu": []}
			per_vp[key]["gpu"].append(g)
			per_vp[key]["cpu"].append(c)
			if vp == far_vp:
				far_gpu = g
				far_cpu = c
		var setup := RenderingServer.get_frame_setup_time_cpu()
		series["wall_ms"].append(wall)
		series["root_gpu_ms"].append(root_gpu)
		series["root_cpu_ms"].append(root_cpu)
		series["sub_gpu_ms"].append(sub_gpu)
		series["sub_cpu_ms"].append(sub_cpu)
		series["far_gpu_ms"].append(far_gpu)
		series["far_cpu_ms"].append(far_cpu)
		series["gpu_sum_ms"].append(root_gpu + sub_gpu)
		series["cpu_render_ms"].append(root_cpu + sub_cpu + setup)
		series["frame_setup_ms"].append(setup)
		series["process_ms"].append(Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0)
		series["physics_ms"].append(Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000.0)
		series["draw_calls"].append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		series["primitives"].append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		series["objects"].append(Performance.get_monitor(Performance.RENDER_TOTAL_OBJECTS_IN_FRAME))
		var idle := COMMON.builds_idle(game) if with_streaming else true
		if not idle:
			busy_frames += 1
		if wall > HITCH_MS:
			hitches.append({"t_s": (now - start) * 0.000001, "wall_ms": wall, "gpu_ms": root_gpu + sub_gpu, "setup_ms": setup, "streaming": not idle})
		timing.call("Sample")
	var t1_utc := Time.get_unix_time_from_system()
	var result := {"tag": tag, "seconds": seconds, "frames": series["wall_ms"].size(), "hitch_count": hitches.size(), "hitches": hitches.slice(0, 80),
		"streaming_busy_frames": busy_frames, "metrics": {}, "viewports": {}, "markers": {}, "t0_utc": t0_utc, "t1_utc": t1_utc}
	result["power"] = COMMON.power_window(COMMON.parse_power_log(power_path), t0_utc, t1_utc, machine, COMMON.local_offset_seconds())
	for key in series:
		result["metrics"][key] = _dist(series[key])
	for key in per_vp:
		result["viewports"][key] = {"gpu": _dist(per_vp[key]["gpu"]), "cpu": _dist(per_vp[key]["cpu"])}
	var collected: Dictionary = timing.call("Window")
	for key in collected:
		if key != "_meta" and not str(key).begins_with("ENG:"):
			result["markers"][str(key)] = _dist(Array(collected[key]))
	# The first 30 s split, for the boot trace.
	if seconds >= 40.0:
		var w: Array = series["wall_ms"]
		var acc := 0.0
		var split := w.size()
		for i in w.size():
			acc += float(w[i])
			if acc >= 30000.0:
				split = i
				break
		result["first_30s"] = {"frames": split, "wall": _dist(w.slice(0, split)), "fps": split / maxf(acc * 0.001, 0.001)}
		result["after_30s"] = {"frames": w.size() - split, "wall": _dist(w.slice(split))}
	return result


func _m(w: Dictionary, metric: String, stat := "median") -> float:
	return float(w.get("metrics", {}).get(metric, {}).get(stat, NAN))


func _brief(w: Dictionary) -> Dictionary:
	return {"tag": w.get("tag", ""), "frames": w.get("frames", 0), "wall_med": _m(w, "wall_ms"), "wall_p95": _m(w, "wall_ms", "p95"), "wall_p99": _m(w, "wall_ms", "p99"),
		"gpu_med": _m(w, "gpu_sum_ms"), "gpu_p95": _m(w, "gpu_sum_ms", "p95"), "root_gpu": _m(w, "root_gpu_ms"), "far_gpu": _m(w, "far_gpu_ms"),
		"cpu_render": _m(w, "cpu_render_ms"), "setup": _m(w, "frame_setup_ms"), "process": _m(w, "process_ms"), "draws": _m(w, "draw_calls"),
		"hitches": w.get("hitch_count", 0), "busy": w.get("streaming_busy_frames", 0)}


func _summarize_pose(pose_name: String) -> void:
	var ws: Array = report["poses"][pose_name]["windows"]
	var summary := {}
	for i in ws.size():
		var w: Dictionary = ws[i]
		if w["label"] == "baseline" or i == 0 or i + 1 >= ws.size():
			continue
		var a: Dictionary = ws[i - 1]
		var b: Dictionary = ws[i + 1]
		var d := {}
		for metric in ["wall_ms", "gpu_sum_ms", "root_gpu_ms", "far_gpu_ms", "sub_gpu_ms", "cpu_render_ms", "frame_setup_ms", "process_ms", "root_cpu_ms", "sub_cpu_ms", "draw_calls"]:
			d[metric] = _m(w, metric) - 0.5 * (_m(a, metric) + _m(b, metric))
			d[metric + "@drift"] = _m(b, metric) - _m(a, metric)
		var markers := {}
		for key in w["markers"]:
			var ma := float(a["markers"].get(key, {}).get("median", 0.0))
			var mb := float(b["markers"].get(key, {}).get("median", 0.0))
			markers[key] = float(w["markers"][key].get("median", 0.0)) - 0.5 * (ma + mb)
		d["markers"] = markers
		summary[w["label"]] = d
	report["poses"][pose_name]["summary"] = summary
	var line := ""
	for label in summary:
		line += " %s:gpu%+.2f/cpu%+.2f/wall%+.2f" % [label, summary[label]["gpu_sum_ms"], summary[label]["cpu_render_ms"], summary[label]["wall_ms"]]
	print("QP_SUMMARY ", pose_name, line)


func _write() -> void:
	var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	if f != null:
		f.store_string(JSON.stringify(report, "  "))
		f.close()


func _fail(reason: String) -> void:
	if failed:
		return
	failed = true
	report["status"] = "FAIL"
	report["reason"] = reason
	COMMON.stop_power_log(power_pid)
	if out_dir != "":
		if probe != null:
			probe.call("WriteReport", out_dir)
		_write()
	push_error("QP_BASELINE=FAIL " + reason)
	quit(1)
