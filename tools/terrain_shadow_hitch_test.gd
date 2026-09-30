extends SceneTree

# Isolated GPU hitch attribution for terrain caster coverage.  It intentionally
# leaves the player's saved world and live-tuning resources untouched.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
const AROUND := 16443476.0
const ACROSS := 38692.0
const WARMUP_FRAMES := 90
const SAMPLE_FRAMES := 300 # roughly five seconds at 60 FPS per bounded phase.
const ROUTE_FRAMES := 600
const ROUTE_SPEED_MPS := 150.0

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
var report := {"status":"RUNNING", "windows":[]}
var production_casters := {}
var sweep_up := Vector3.UP
var sweep_forward := Vector3.FORWARD
var flight_height := 0.0
var telemetry: Node

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void: call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/terrain_shadow_hitch/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var old := root.get_node_or_null("WorldSession")
	if old != null: old.name = "WorldSessionOriginalTerrainShadowHitch"
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
	if surface == null or explorer == null or camera == null or clock == null or coordinator == null or global_lod == null or planet == null: return fail("missing gameplay nodes")
	var height := float(surface.call("SampleHeight", AROUND, ACROSS)) + 500.0
	if not is_finite(height): return fail("non-finite fixture terrain")
	flight_height = height
	explorer.call("SetCanonicalSpawnMeters", AROUND, height, ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	if not bool(placement.call("Place", explorer, AROUND, height, ACROSS)): return fail("could not place elevated fixture")
	# Keep the physical camera elevated after placement. The world/player remain
	# fixed; the sweep exercises the reported airborne pan path without streaming.
	camera.global_position += camera.global_basis.y.normalized() * 350.0
	sweep_up = camera.global_basis.y.normalized()
	sweep_forward = -camera.global_basis.z.normalized()
	clock.set("Paused", true)
	var phases := await select_phases()
	clock.call("SetCycleFraction", phases.night)
	if not await wait_shadow_data("night"): return
	telemetry = game.get_node_or_null("RingPerformanceMonitor")
	if not await wait_proxy_settled(): return
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	capture_production_casters()
	for policy in ["all_active", "former_sparse", "former_sparse", "all_active"]:
		apply_caster_policy(policy)
		var window := await capture_flight(policy)
		report.windows.append(window); write_report()
		print("TERRAIN_SHADOW_HITCH_WINDOW ", JSON.stringify(window))
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	if telemetry != null: report["telemetry_report"] = telemetry.call("CaptureSlowdown")
	report.status = "PASS"; write_report()
	print("TERRAIN_SHADOW_HITCH_TEST=PASS fixture=" + fixture)
	game.queue_free(); await process_frame; quit(0)

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
	fail("runtime Terrain3D startup timed out"); return null

func install_preferences(next: Node) -> void:
	var drawer := next.get_node_or_null("LiveTuningDrawer")
	if drawer == null: return
	for item in [["InstallGrassDefaults","grass.tres"], ["InstallVegetationDefaults","vegetation.tres"], ["InstallWaterDefaults","water.tres"], ["InstallAntiAliasingDefaults","anti_aliasing.tres"], ["InstallShadowDefaults","shadows.tres"], ["InstallSurfaceTerrainDefaults","surface.tres"], ["InstallAtmosphereDefaults","atmosphere.tres"]]: drawer.call(str(item[0]), fixture.path_join(str(item[1])))
	var monitor := next.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.call("InstallSettingsPathForTests", fixture.path_join("diagnostics-settings.tres"))
		monitor.call("InstallReportDirectoryForTests", fixture.path_join("diagnostics"))
		monitor.set("Enabled", true); monitor.set("DetailedGpuEnabled", true); monitor.set("HistorySeconds", 60)

func wait_ready() -> bool:
	var stable := 0; var deadline := Time.get_ticks_msec() + 60000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d: Dictionary = surface.call("GetSurfaceDiagnostics"); var stream: Dictionary = d.get("streaming", {}); var native: Dictionary = d.get("native", {}); var prep: Dictionary = native.get("preparation", {})
		var ready := bool(d.get("ready", false)) and bool(d.get("visual_coverage", false)) and bool(prep.get("ready", false)) and int(stream.get("pending_jobs", 1)) == 0 and int(stream.get("active_jobs", 1)) == 0 and not bool(stream.get("packing", true))
		stable = stable + 1 if ready else 0
		if stable >= 8: return true
	fail("terrain did not settle"); return false

func select_phases() -> Dictionary:
	var day := 0.0; var night := 0.0; var high := -INF; var low := INF
	for step in 48:
		var p := float(step) / 48.0; clock.call("SetCycleFraction", p); await process_frame
		var e := float(clock.get("SunElevationDegrees"))
		if e > high: high = e; day = p
		if e < low and planet.visible and planet.light_energy > 0.001: low = e; night = p
	if low == INF: fail("no night phase with active PlanetLight")
	return {"day":day, "night":night}

func wait_shadow_data(label: String) -> bool:
	for ignored in 3: await process_frame
	var deadline := Time.get_ticks_msec() + 30000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		if label == "night":
			if bool(global_lod.get("PlanetMacroShadowMaskActive")): return true
		else:
			if not bool(global_lod.get("PlanetMacroShadowBuildPending")): return true
	fail("shadow data did not settle for " + label); return false

func wait_proxy_settled() -> bool:
	var quiet := 0; var deadline := Time.get_ticks_msec() + 90000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var idle := int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0
		quiet = quiet + 1 if idle else 0
		if quiet >= 30: return true
	fail("proxy workers/uploads did not settle before flight: workers=%s uploads=%s" % [coordinator.get("ActiveWorkerCount"), coordinator.get("PendingUploadCount")]); return false

func proxies() -> Array:
	var result: Array = []
	for node in all_nodes(game):
		var script = node.get_script()
		if script != null and script.resource_path.ends_with("/RingTerrainProxySector.cs"): result.append(node)
	result.sort_custom(func(a, b): return lod_number(a) < lod_number(b))
	return result

func lod_number(level: Node) -> int:
	var first_token := str(level.get("LodLabel")).split(" ", false)[0]
	return first_token.trim_prefix("LOD").to_int()

func capture_production_casters() -> void:
	production_casters.clear()
	for level in proxies(): production_casters[level.get_instance_id()] = bool(level.get("CastTerrainShadows"))

func apply_caster_policy(policy: String) -> void:
	var levels := proxies(); var active: Array = []; var production_active: Array = []
	for level in levels:
		if bool(level.get("StreamingActive")):
			active.append(level)
			if bool(production_casters.get(level.get_instance_id(), false)): production_active.append(level)
	if active.is_empty(): fail("no active terrain proxy levels")
	var sparse: Dictionary = {}
	if policy == "former_sparse":
		if production_active.is_empty(): fail("production has no active terrain shadow casters"); return
		sparse[production_active.front().get_instance_id()] = true
		sparse[production_active.back().get_instance_id()] = true
	for level in levels:
		var should_cast := bool(production_casters.get(level.get_instance_id(), false)) if policy == "all_active" else sparse.has(level.get_instance_id())
		level.call("SetTerrainShadowCasting", should_cast)
	# Camera-only motion leaves the coordinator's coverage policy stable. Record
	# the property so reports reveal any unexpected controller rewrite.

func capture_sweep(phase: String, policy: String, macro_mode: String) -> Dictionary:
	# The first pass intentionally includes any material/pipeline first-use work.
	# Record first use separately from the warmed pass, including Godot's pipeline
	# compilation counters for correlation with any one-time pan spikes.
	var pipelines_before := pipeline_counts()
	var cold_wall: Array[float] = []
	var cold_pipeline_draw_delta: Array[float] = []
	var previous_pipeline_draw := float(pipelines_before.get("draw", 0))
	for frame in 30:
		var cold_start := Time.get_ticks_usec(); await sweep_frame(frame, 30)
		cold_wall.append((Time.get_ticks_usec() - cold_start) * .001)
		var next_pipeline_draw := float(pipeline_counts().get("draw", 0)); cold_pipeline_draw_delta.append(next_pipeline_draw - previous_pipeline_draw); previous_pipeline_draw = next_pipeline_draw
	for ignored in WARMUP_FRAMES: await sweep_frame(ignored, WARMUP_FRAMES)
	var wall: Array[float] = []; var gpu: Array[float] = []; var cpu: Array[float] = []; var setup: Array[float] = []; var draws: Array[float] = []; var primitives: Array[float] = []; var pipeline_draw_delta: Array[float] = []
	for frame in SAMPLE_FRAMES:
		var start := Time.get_ticks_usec(); await sweep_frame(frame, SAMPLE_FRAMES)
		wall.append((Time.get_ticks_usec() - start) * .001)
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
		cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
		setup.append(RenderingServer.get_frame_setup_time_cpu())
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		var next_pipeline_draw := float(pipeline_counts().get("draw", 0)); pipeline_draw_delta.append(next_pipeline_draw - previous_pipeline_draw); previous_pipeline_draw = next_pipeline_draw
	var casting := []
	for level in proxies(): if bool(level.get("StreamingActive")): casting.append({"lod":level.get("LodLabel"), "cast":level.get("CastTerrainShadows"), "active_sectors":level.get("ActiveSectorCount")})
	return {"phase":phase, "motion":"fixed-player airborne 360-degree camera pan", "caster_policy":policy, "macro_mode":macro_mode, "samples":SAMPLE_FRAMES, "warmup_frames":WARMUP_FRAMES, "cold_sweep_frames":cold_wall.size(), "cold_sweep_wall_ms":stats(cold_wall), "cold_sweep_pipeline_draw_delta":stats(cold_pipeline_draw_delta), "cold_sweep_over_50ms":count_over(cold_wall, 50.0), "cold_sweep_over_100ms":count_over(cold_wall, 100.0), "wall_ms":stats(wall), "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "render_setup_cpu_ms":stats(setup), "draw_calls":stats(draws), "primitives":stats(primitives), "pipeline_draw_delta_per_frame":stats(pipeline_draw_delta), "pipeline_compilations_before":pipelines_before, "pipeline_compilations_after":pipeline_counts(), "frames_over_50ms":count_over(wall, 50.0), "frames_over_100ms":count_over(wall, 100.0), "caster_snapshot":casting, "macro_pending":global_lod.get("PlanetMacroShadowBuildPending") if phase == "night" else global_lod.get("MacroShadowBuildPending")}

func capture_flight(policy: String) -> Dictionary:
	var wall: Array[float] = []; var gpu: Array[float] = []; var cpu: Array[float] = []; var setup: Array[float] = []; var draws: Array[float] = []; var pipeline_delta: Array[float] = []; var proxy_frames: Array = []
	var previous_pipeline_draw := float(pipeline_counts().get("draw", 0)); var pipeline_before := pipeline_counts()
	for frame in ROUTE_FRAMES:
		var started := Time.get_ticks_usec()
		var canonical_around := AROUND + ROUTE_SPEED_MPS * (float(frame) / 60.0)
		if not bool(placement.call("Move", explorer, canonical_around, flight_height, ACROSS, ROUTE_SPEED_MPS)):
			fail("could not advance flight fixture")
			return {}
		var t := TAU * float(frame) / float(ROUTE_FRAMES)
		var forward := (Basis(sweep_up, t) * sweep_forward + sweep_up * (0.08 * sin(t * 3.0))).normalized()
		camera.look_at(camera.global_position + forward * 1200.0, sweep_up, true)
		await RenderingServer.frame_post_draw
		wall.append((Time.get_ticks_usec() - started) * .001)
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
		cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
		setup.append(RenderingServer.get_frame_setup_time_cpu())
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		var next_pipeline_draw := float(pipeline_counts().get("draw", 0)); pipeline_delta.append(next_pipeline_draw - previous_pipeline_draw); previous_pipeline_draw = next_pipeline_draw
		proxy_frames.append({"workers":coordinator.get("ActiveWorkerCount"), "pending_uploads":coordinator.get("PendingUploadCount"), "uploads_this_frame":coordinator.get("LastFrameUploadCount"), "upload_ms":coordinator.get("LastFrameUploadMilliseconds")})
	var casting := []
	for level in proxies(): if bool(level.get("StreamingActive")): casting.append({"lod":level.get("LodLabel"), "cast":level.get("CastTerrainShadows"), "active_sectors":level.get("ActiveSectorCount")})
	return {"phase":"night", "motion":"benchmark noclip flight plus 360-degree camera pan", "route":{"altitude_above_start_surface_m":500.0, "speed_mps":ROUTE_SPEED_MPS, "simulated_seconds":ROUTE_FRAMES / 60.0, "distance_m":ROUTE_SPEED_MPS * ROUTE_FRAMES / 60.0}, "caster_policy":policy, "samples":ROUTE_FRAMES, "wall_ms":stats(wall), "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "render_setup_cpu_ms":stats(setup), "draw_calls":stats(draws), "pipeline_draw_delta_per_frame":stats(pipeline_delta), "pipeline_compilations_before":pipeline_before, "pipeline_compilations_after":pipeline_counts(), "frames_over_50ms":count_over(wall, 50.0), "frames_over_100ms":count_over(wall, 100.0), "proxy_frame_samples":proxy_frames, "caster_snapshot":casting}

func sweep_frame(frame: int, total: int) -> void:
	var t := TAU * float(frame % total) / float(total)
	var forward := (Basis(sweep_up, t) * sweep_forward + sweep_up * (0.08 * sin(t * 3.0))).normalized()
	camera.look_at(camera.global_position + forward * 1200.0, sweep_up, true)
	await RenderingServer.frame_post_draw

func pipeline_counts() -> Dictionary:
	return {"draw":RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_PIPELINE_COMPILATIONS_DRAW), "mesh":RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_PIPELINE_COMPILATIONS_MESH), "surface":RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_PIPELINE_COMPILATIONS_SURFACE), "specialization":RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_PIPELINE_COMPILATIONS_SPECIALIZATION), "canvas":RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_PIPELINE_COMPILATIONS_CANVAS)}

func stats(values: Array[float]) -> Dictionary:
	var sorted := values.duplicate(); sorted.sort(); var sum := 0.0
	for value in sorted: sum += value
	return {"mean":sum / sorted.size(), "median":sorted[sorted.size() / 2], "p95":sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .95)) - 1)], "p99":sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .99)) - 1)], "min":sorted[0], "max":sorted[-1]}

func count_over(values: Array[float], threshold: float) -> int:
	var count := 0
	for value in values:
		if value > threshold: count += 1
	return count

func all_nodes(node: Node) -> Array:
	var result: Array = [node]
	for child in node.get_children(): result.append_array(all_nodes(child))
	return result

func settle_frames(count: int) -> void:
	for ignored in count: await process_frame

func write_report() -> void:
	if fixture.is_empty(): return
	var file := FileAccess.open(ProjectSettings.globalize_path(fixture.path_join("report.json")), FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report, "\t")); file.close()

func fail(message: String) -> void:
	report.status = "FAIL"; report["error"] = message; write_report(); push_error("TERRAIN_SHADOW_HITCH_TEST=FAIL " + message); quit(1)
