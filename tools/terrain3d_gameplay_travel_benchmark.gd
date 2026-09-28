extends SceneTree

## Actual controller travel benchmark. It holds W through the explorer's normal
## physics path; teleporting is used only once to establish the route origin.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const SURFACE_RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const TERRAIN_GENERATION_SAMPLER := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const RUNTIME_FRAME_DIAGNOSTICS := preload("res://tools/RuntimeFrameDiagnostics.cs")
const CERTIFIED_CIRCUMFERENCE_METERS := 31415936.0
var fixture := ""
var frames: Array[float] = []
var raw_frame_profile: Array[Dictionary] = []
var phases: Array = []
var deadline := 0
var explorer: Node
var surface: Node
var game: Node
var session: SessionStub
var failed := false
var max_grounded_gap := 0.0
var grounded_gap := 0.0
var support_ray_samples := 0
var support_ray_misses := 0
var timing_enabled := false
var previous_frame_usec := 0
var measured_viewports: Array[Viewport] = []
var latest_diag: Dictionary = {}
var failure_context: Dictionary = {}
var initial_coverage_waits := 0
var cloud_resolution_override := -1
var telemetry_report := ""
var runtime_frame_diagnostics: RefCounted
var global_terrain_diagnostics_node: Node
class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings=next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	process_frame.connect(_stamp_render_frame)
	run.call_deferred()

func _stamp_render_frame() -> void:
	var now := Time.get_ticks_usec()
	if timing_enabled and previous_frame_usec > 0:
		var interval_ms: float = (now-previous_frame_usec)/1000.0
		frames.append(interval_ms)
		var viewport_cpu_ms := 0.0
		var viewport_gpu_ms := 0.0
		for viewport: Viewport in measured_viewports:
			if not is_instance_valid(viewport): continue
			var viewport_rid: RID = viewport.get_viewport_rid()
			viewport_cpu_ms += RenderingServer.viewport_get_measured_render_time_cpu(viewport_rid)
			viewport_gpu_ms += RenderingServer.viewport_get_measured_render_time_gpu(viewport_rid)
		var frame_profile := {"timestamp_usec":now, "interval_ms":interval_ms, "process_ms":Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0, "physics_ms":Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000.0, "render_setup_ms":RenderingServer.get_frame_setup_time_cpu(), "viewport_cpu_ms":viewport_cpu_ms, "viewport_gpu_ms":viewport_gpu_ms, "viewport_timings":_measured_viewport_timings(), "position":{"around":latest_diag.get("player_around", NAN), "across":latest_diag.get("player_across", NAN)}, "streaming":latest_diag.get("streaming", {}), "native_preparation":_native_preparation(latest_diag)}
		if runtime_frame_diagnostics != null:
			frame_profile["runtime_counters"] = runtime_frame_diagnostics.call("CaptureCounters")
			frame_profile["global_terrain"] = runtime_frame_diagnostics.call("CaptureGlobalTerrainCounters", global_terrain_diagnostics_node)
		raw_frame_profile.append(frame_profile)
	previous_frame_usec = now

func _native_preparation(diag: Dictionary) -> Dictionary:
	var native: Dictionary = diag.get("native", {})
	return native.get("preparation", {})

func _coverage_wait_count(diag: Dictionary) -> int:
	var streaming: Dictionary = diag.get("streaming", {})
	return int(streaming.get("coverage_waits", 0))

func _enable_viewport_profiling(game: Node) -> void:
	measured_viewports.clear()
	var root_rid: RID = root.get_viewport_rid()
	measured_viewports.append(root)
	RenderingServer.viewport_set_measure_render_time(root_rid, true)
	for node: Node in _all_nodes(game):
		if node is SubViewport:
			var subviewport: SubViewport = node
			var rid: RID = subviewport.get_viewport_rid()
			measured_viewports.append(subviewport)
			RenderingServer.viewport_set_measure_render_time(rid, true)

func _measured_viewport_timings() -> Array[Dictionary]:
	var timings: Array[Dictionary] = []
	for viewport: Viewport in measured_viewports:
		if not is_instance_valid(viewport): continue
		var viewport_rid: RID = viewport.get_viewport_rid()
		timings.append({"name":str(viewport.get_path()), "cpu_ms":RenderingServer.viewport_get_measured_render_time_cpu(viewport_rid), "gpu_ms":RenderingServer.viewport_get_measured_render_time_gpu(viewport_rid)})
	return timings

func _all_nodes(node: Node) -> Array[Node]:
	var result: Array[Node] = [node]
	for child: Node in node.get_children(): result.append_array(_all_nodes(child))
	return result

func _apply_cloud_resolution_override() -> void:
	var requested := arg_optional_int("--cloud-resolution=")
	if requested < 0: return
	cloud_resolution_override = clampi(requested, 0, 2)
	var bridge: Node = game.get_node_or_null("SunshineCloudBridge")
	if bridge == null:
		fail("--cloud-resolution requested but SunshineCloudBridge is missing")
		return
	bridge.set("CloudResolutionScale", cloud_resolution_override)
	bridge.call("ApplyTemporalQualityTuning", true)

func _cloud_resolution_report() -> Dictionary:
	var result: Dictionary = {"requested_override":cloud_resolution_override}
	var bridge: Node = game.get_node_or_null("SunshineCloudBridge")
	if bridge != null:
		result["bridge_resolution_scale"] = bridge.get("CloudResolutionScale")
	var driver: Node = game.get_node_or_null("SunshineCloudsDriver")
	if driver != null:
		var resource = driver.get("clouds_resource")
		if is_instance_valid(resource):
			result["resource_resolution_scale"] = resource.get("resolution_scale")
	return result

func _configure_benchmark_telemetry(next_game: Node) -> bool:
	if "--telemetry" not in OS.get_cmdline_user_args(): return true
	var monitor: Node = next_game.get_node_or_null("RingPerformanceMonitor")
	if monitor == null:
		fail("--telemetry requested but RingPerformanceMonitor is missing")
		return false
	monitor.call("InstallSettingsPathForTests", fixture.path_join("diagnostics-settings.tres"))
	monitor.call("InstallReportDirectoryForTests", fixture.path_join("diagnostics"))
	monitor.set("Enabled", true)
	monitor.set("DetailedGpuEnabled", "--detailed-gpu" in OS.get_cmdline_user_args())
	return true

func _capture_benchmark_telemetry() -> bool:
	if "--telemetry" not in OS.get_cmdline_user_args(): return true
	var monitor: Node = game.get_node_or_null("RingPerformanceMonitor")
	if monitor == null:
		fail("--telemetry requested but RingPerformanceMonitor is unavailable for export")
		return false
	telemetry_report = str(monitor.call("CaptureSlowdown"))
	if telemetry_report.is_empty() or telemetry_report.begins_with("Export failed") or telemetry_report.begins_with("Export refused") or not FileAccess.file_exists(telemetry_report):
		fail("--telemetry requested but diagnostics export failed: " + telemetry_report)
		return false
	return true

func run() -> void:
	fixture = "res://.godot/terrain-generation-tests/m6-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1" if "--legacy" not in OS.get_cmdline_user_args() else "terrain3d_surface_v1")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var actual := root.get_node_or_null("WorldSession"); if actual != null: actual.name="WorldSessionOriginalM6"
	session=SessionStub.new(); session.name="WorldSession"; root.add_child(session)
	deadline = Time.get_ticks_msec() + 240000
	game = await create_game()
	if game == null: return
	_apply_cloud_resolution_override()
	if failed: return
	explorer = game.get_node_or_null("Explorer"); surface = game.get_node_or_null("Terrain3DGameplay")
	if explorer == null or surface == null: fail("missing explorer/surface"); return
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	explorer.set("EnableSwimming", false)
	var speed := arg_float("--speed=", 12.0)
	var distance := arg_float("--distance=", 512.0)
	explorer.set("MoveSpeed", speed); explorer.set("SprintSpeed", speed)
	var d: Dictionary = surface.call("GetSurfaceDiagnostics")
	latest_diag = d.duplicate(true)
	# By default test the configured production spawn. The old fixed (32,
	# 159032) route lands in a v17 ocean basin and is not representative of
	# normal spawn movement. Explicit coordinates remain available for repro.
	var origin_a := arg_optional_float("--around=")
	var origin_z := arg_optional_float("--across=")
	var explicit_origin := not is_nan(origin_a) and not is_nan(origin_z)
	if explicit_origin:
		var height := float(surface.call("SampleHeight", origin_a, origin_z)) + 1.1
		explorer.call("SetCanonicalSpawnMeters", origin_a, height, origin_z)
	else:
		origin_a = float(d.get("player_around", 0.0))
		origin_z = float(d.get("player_across", 0.0))
	if not await settle_route_origin(): return
	# Preserve the M6 rendered benchmark warmup after the controller has reached
	# a stable Terrain3D floor. It is excluded from timing but lets render state
	# settle before route measurements begin.
	await ticks(120)
	if not explicit_origin:
		d = surface.call("GetSurfaceDiagnostics")
		latest_diag = d.duplicate(true)
		origin_a = float(d.get("player_around", 0.0))
		origin_z = float(d.get("player_across", 0.0))
	if not await capture_render("terrain-start.png"): return
	_enable_viewport_profiling(game)
	latest_diag = surface.call("GetSurfaceDiagnostics")
	initial_coverage_waits = _coverage_wait_count(latest_diag)
	if "--telemetry" in OS.get_cmdline_user_args():
		global_terrain_diagnostics_node = game.get_node_or_null("GlobalTerrainLod3")
		if global_terrain_diagnostics_node == null:
			fail("--telemetry requested but global terrain diagnostics are unavailable")
			return
		runtime_frame_diagnostics = RUNTIME_FRAME_DIAGNOSTICS.new()
	previous_frame_usec = 0
	timing_enabled = true
	if not await travel("out", origin_a + distance, origin_z): return
	if not await travel("back", origin_a, origin_z): return
	timing_enabled = false
	if not _capture_benchmark_telemetry(): return
	if not await capture_render("terrain-end.png"): return
	report(game, speed, distance, origin_a, origin_z)
	game.queue_free(); await ticks(2); quit(0)

func create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await ticks(2)
	launcher.call("_on_certified_preset_pressed")
	var select: OptionButton = launcher.get("terrain_backend_selector")
	if select == null: fail("missing launcher backend selector"); return null
	select.select(1 if "--legacy" in OS.get_cmdline_user_args() else 2); select.emit_signal("item_selected", select.selected)
	if select.selected == 2 and not bool(launcher.call("_prepare_runtime_surface_generation", true)): fail("runtime definition preparation failed"); return null
	# The launcher prepares the normal runtime definition before a game exists. Replace
	# that pending definition here, still before _configure_game, for an isolated v1
	# comparison that retains the Terrain3D v17 backend and all controller settings.
	if select.selected == 2 and "--reference-v1" in OS.get_cmdline_user_args():
		var recipe: TerrainGenerationRecipe = SURFACE_RECIPE.new()
		recipe.generator_id = SURFACE_RECIPE.LEGACY_GENERATOR_ID
		var sampler := TERRAIN_GENERATION_SAMPLER.new()
		if not bool(sampler.Configure(recipe.to_normalized_dictionary())):
			fail("reference-v1 recipe preparation failed: %s" % str(sampler.get("LastError")))
			sampler.Release()
			return null
		launcher.set("pending_surface_generation_recipe_json", str(sampler.get("CanonicalJson")))
		sampler.Release()
	if select.selected == 2: launcher.call("_save_selected_slot_settings")
	var game := GAME.instantiate(); launcher.call("_configure_game", game, 5000000.0)
	if not _configure_benchmark_telemetry(game):
		launcher.queue_free()
		game.queue_free()
		return null
	var tuning: Node=game.get_node_or_null("LiveTuningDrawer"); if tuning!=null and tuning.has_method("InstallSurfaceTerrainDefaults"): tuning.call("InstallSurfaceTerrainDefaults",fixture.path_join("surface_tuning.tres"))
	if "--sync-baseline" in OS.get_cmdline_user_args():
		var gameplay: Node = game.get_node_or_null("Terrain3DGameplay")
		if gameplay == null or not bool(gameplay.call("EnableSynchronousBenchmarkMode")):
			fail("synchronous benchmark mode was rejected")
			launcher.queue_free()
			game.queue_free()
			return null
	if "--isolate" in OS.get_cmdline_user_args():
		for name in ["BiomeDecorations","StandingGrass","RingVolumetricClouds","LocalVolumetricCloudRenderer","LocalWeatherEffects","WeatherAudio","WeatherAmbience","WaterFftController","TerrainMinimap","CertificationMonitor"]:
			var n: Node = game.get_node_or_null(name)
			if n != null: n.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(game); current_scene = game; launcher.queue_free()
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var surface: Node = game.get_node_or_null("Terrain3DGameplay")
		if surface != null and bool(surface.call("EnsureInitialized")): return game
	fail("startup readiness deadline"); return null

func travel(phase: String, target_a: float, target_z: float) -> bool:
	var start_d: Dictionary = surface.call("GetSurfaceDiagnostics")
	var start_a := float(start_d.get("player_around",0.0))
	var target_delta := signed_around_delta(target_a, start_a)
	var direction := signf(target_delta)
	var prior_anchor := ""
	var phase_start := frames.size()
	var last_progress_msec := Time.get_ticks_msec()
	var last_progress_delta := 0.0
	if phase == "back": explorer.call("SetPresentationView", 180.0, 0.0)
	else: explorer.call("SetPresentationView", 0.0, 0.0)
	press_w(true)
	while Time.get_ticks_msec() < deadline:
		await physics_frame
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		latest_diag = diag.duplicate(true)
		var current_around := float(diag.get("player_around", 0.0))
		var current_delta := signed_around_delta(current_around, start_a)
		if absf(current_delta-last_progress_delta) > .5:
			last_progress_delta = current_delta; last_progress_msec = Time.get_ticks_msec()
		elif Time.get_ticks_msec()-last_progress_msec > 15000:
			failure_context = capture_failure_context(diag, phase)
			fail("movement stalled during "+phase); return false
		var anchor := "%s/%s" % [diag.get("frame_around", ""), diag.get("frame_across", "")]
		if anchor != prior_anchor: phases.append({"event":"anchor", "phase":phase, "frame":frames.size(), "anchor":anchor, "rebuild":diag.get("rebuild",{}), "native":diag.get("native",{}), "streaming":diag.get("streaming",{})}); prior_anchor=anchor
		var grounded := bool(explorer.call("is_on_floor")) if explorer.has_method("is_on_floor") else false
		var up: Vector3 = explorer.global_basis.y.normalized()
		var support_query := PhysicsRayQueryParameters3D.create(explorer.global_position + up * 2.0, explorer.global_position - up * 8.0, 1, [explorer.get_rid()])
		var support_hit: Dictionary = explorer.get_world_3d().direct_space_state.intersect_ray(support_query)
		support_ray_samples += 1
		if support_hit.is_empty(): support_ray_misses += 1
		if grounded: grounded_gap = 0.0
		else: grounded_gap += 1.0 / 60.0; max_grounded_gap=maxf(max_grounded_gap,grounded_gap)
		if current_delta * direction >= absf(target_delta): break
	press_w(false)
	var end_d: Dictionary = surface.call("GetSurfaceDiagnostics")
	latest_diag = end_d.duplicate(true)
	var phase_frames: Array[float] = []
	for index in range(phase_start, frames.size()): phase_frames.append(frames[index])
	phases.append({"event":"phase", "name":phase, "target_m":target_delta, "actual_m":signed_around_delta(float(end_d.get("player_around",0.0)), start_a), "frames":phase_frames.size(), "p50_ms":pct(phase_frames,.5), "p95_ms":pct(phase_frames,.95), "surface":end_d})
	if Time.get_ticks_msec() >= deadline: fail("wall deadline during "+phase); return false
	return not failed

func signed_around_delta(value: float, origin: float) -> float:
	return fposmod(value - origin + CERTIFIED_CIRCUMFERENCE_METERS * .5, CERTIFIED_CIRCUMFERENCE_METERS) - CERTIFIED_CIRCUMFERENCE_METERS * .5

func capture_render(filename: String) -> bool:
	if "--capture" not in OS.get_cmdline_user_args(): return true
	if DisplayServer.get_name().to_lower() == "headless":
		print("TERRAIN3D_TRAVEL_CAPTURE=SKIPPED_HEADLESS")
		return true
	await RenderingServer.frame_post_draw
	var texture := root.get_texture()
	if texture == null:
		fail("--capture requested but the rendered viewport texture is unavailable")
		return false
	var image := texture.get_image()
	if image == null or image.is_empty():
		fail("--capture requested but the rendered viewport image is unavailable")
		return false
	var path := ProjectSettings.globalize_path(fixture.path_join(filename))
	var result := image.save_png(path)
	if result != OK:
		fail("--capture could not save %s (error %s)" % [path, result])
		return false
	print("TERRAIN3D_TRAVEL_CAPTURE=", path)
	return true

func settle_route_origin() -> bool:
	var stable := 0
	while Time.get_ticks_msec() < deadline:
		await physics_frame
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		latest_diag = diag.duplicate(true)
		var player_height := float(diag.get("player_height", NAN))
		var surface_height := float(diag.get("surface_height", NAN))
		var grounded := bool(explorer.call("is_on_floor")) if explorer.has_method("is_on_floor") else false
		stable = stable + 1 if grounded and not is_nan(player_height) and not is_nan(surface_height) and absf(player_height-surface_height) <= .25 else 0
		if stable >= 20: return true
	failure_context = capture_failure_context(latest_diag, "spawn_settle")
	fail("route origin did not reach stable grounded Terrain3D support")
	return false

func capture_failure_context(diag: Dictionary, phase: String) -> Dictionary:
	var up: Vector3 = explorer.global_basis.y.normalized()
	var support_query := PhysicsRayQueryParameters3D.create(explorer.global_position + up * 2.0, explorer.global_position - up * 8.0, 1, [explorer.get_rid()])
	var support: Dictionary = explorer.get_world_3d().direct_space_state.intersect_ray(support_query)
	var drawer: Node = game.get_node_or_null("LiveTuningDrawer") if game != null else null
	var drawer_visible := drawer is CanvasItem and (drawer as CanvasItem).visible
	return {"phase":phase, "input_w":Input.is_key_pressed(KEY_W), "input_w_physical":Input.is_physical_key_pressed(KEY_W), "mouse_mode":Input.mouse_mode, "live_tuning_mouse_open":root.get_meta("ringworld_live_tuning_mouse_open", false), "drawer_visible":drawer_visible, "explorer_process_mode":explorer.process_mode, "move_speed":explorer.get("MoveSpeed"), "sprint_speed":explorer.get("SprintSpeed"), "canonical_velocity":explorer.get("CanonicalVelocity"), "grounded":explorer.call("is_on_floor") if explorer.has_method("is_on_floor") else false, "swimming":explorer.get("IsSwimming"), "support_hit":not support.is_empty(), "support":support, "slides":capture_slide_contacts(), "local_slope":capture_local_slope(diag), "surface":diag, "latest_frame":raw_frame_profile.back() if not raw_frame_profile.is_empty() else {}}

func capture_slide_contacts() -> Array[Dictionary]:
	var contacts: Array[Dictionary] = []
	if explorer == null or not explorer.has_method("get_slide_collision_count"): return contacts
	var count := int(explorer.call("get_slide_collision_count"))
	for index in count:
		var collision: KinematicCollision3D = explorer.call("get_slide_collision", index)
		if collision == null: continue
		var collider: Object = collision.get_collider()
		contacts.append({"index":index, "normal":collision.get_normal(), "position":collision.get_position(), "travel":collision.get_travel(), "remainder":collision.get_remainder(), "collider_class":collider.get_class() if collider != null else "none", "collider_name":str(collider.get("name")) if collider != null else "none"})
	return contacts

func capture_local_slope(diag: Dictionary) -> Dictionary:
	if surface == null: return {}
	var around := float(diag.get("player_around", NAN))
	var across := float(diag.get("player_across", NAN))
	if is_nan(around) or is_nan(across): return {}
	return {"continuous":sample_slope("SampleHeight", around, across), "collision":sample_slope("SampleCollisionHeight", around, across)}

func sample_slope(method: String, around: float, across: float) -> Dictionary:
	if not surface.has_method(method): return {"available":false}
	var center := float(surface.call(method, around, across))
	var around_minus := float(surface.call(method, around - 1.0, across))
	var around_plus := float(surface.call(method, around + 1.0, across))
	var across_minus := float(surface.call(method, around, across - 1.0))
	var across_plus := float(surface.call(method, around, across + 1.0))
	var d_around := (around_plus - around_minus) * .5
	var d_across := (across_plus - across_minus) * .5
	var slope := sqrt(d_around * d_around + d_across * d_across)
	return {"available":is_finite(center) and is_finite(slope), "height":center, "d_around":d_around, "d_across":d_across, "slope":slope, "incline_degrees":rad_to_deg(atan(slope))}

func press_w(down: bool) -> void:
	var e := InputEventKey.new(); e.keycode = KEY_W; e.pressed = down; Input.parse_input_event(e)
func ticks(n: int) -> void: for ignored in n: await process_frame
func pct(a: Array[float], q: float) -> float:
	if a.is_empty(): return NAN
	var b := a.duplicate(); b.sort(); return b[int(clampf(q,0,1)*float(b.size()-1))]
func arg_float(prefix: String, fallback: float) -> float:
	for value in OS.get_cmdline_user_args(): if value.begins_with(prefix): return float(value.trim_prefix(prefix))
	return fallback
func arg_optional_float(prefix: String) -> float:
	for value in OS.get_cmdline_user_args(): if value.begins_with(prefix): return float(value.trim_prefix(prefix))
	return NAN
func arg_optional_int(prefix: String) -> int:
	for value in OS.get_cmdline_user_args():
		if value.begins_with(prefix): return int(value.trim_prefix(prefix))
	return -1
func report(game: Node, speed: float, distance: float, around: float, across: float) -> void:
	var max_ms := 0.0
	for value in frames: max_ms=maxf(max_ms,value)
	var final_surface: Dictionary = surface.call("GetSurfaceDiagnostics")
	var final_coverage_waits := _coverage_wait_count(final_surface)
	var data = {"backend":"legacy" if "--legacy" in OS.get_cmdline_user_args() else "runtime", "reference_v1":"--reference-v1" in OS.get_cmdline_user_args(), "sync_baseline":"--sync-baseline" in OS.get_cmdline_user_args(), "cloud_resolution":_cloud_resolution_report(),"speed_mps":speed,"distance_m":distance,"origin":{"around":around,"across":across},"frames":frames.size(),"p50_ms":pct(frames,.5),"p95_ms":pct(frames,.95),"p99_ms":pct(frames,.99),"max_frame_ms":max_ms,"raw_frame_ms":frames,"raw_frame_profile":raw_frame_profile,"timestamp_note":"raw_frame_profile.timestamp_usec is Godot process uptime. When --telemetry is enabled, raw_frame_profile.runtime_counters.stopwatch_seconds and performance telemetry events share RingPerformanceTelemetry's Stopwatch clock and can be correlated directly.","telemetry_report":telemetry_report,"profile_note":"Frame intervals are adjacent-monitor correlation, not a sum of CPU/GPU timings. Viewport timing covers measured viewports and can exclude custom RenderingDevice compositors or cloud compute.","events":phases,"coverage_waits":{"initial":initial_coverage_waits,"final":final_coverage_waits,"travel_delta":maxi(0, final_coverage_waits-initial_coverage_waits)},"support_probe":{"controller":"IsOnFloor diagnostic","ray_samples":support_ray_samples,"ray_misses":support_ray_misses,"ray":"physics ray, 2m above to 8m below explorer"},"max_ungrounded_diagnostic_seconds":max_grounded_gap,"hardware":{"os":OS.get_name(),"processor":OS.get_processor_name(),"gpu":RenderingServer.get_video_adapter_name()},"graphics":{"viewport_size":root.size,"rendering_method":ProjectSettings.get_setting("rendering/renderer/rendering_method",""),"vsync":DisplayServer.window_get_vsync_mode()},"world_settings":session.settings.duplicate(true),"failure_context":failure_context,"final_surface":final_surface}
	var path := ProjectSettings.globalize_path(fixture.path_join("travel-benchmark.json")); var f:=FileAccess.open(path,FileAccess.WRITE); f.store_string(JSON.stringify(data,"\t")); f.close(); print("TERRAIN3D_TRAVEL_BENCHMARK=",path)
func fail(message: String) -> void:
	failed=true
	if is_instance_valid(surface): print("TERRAIN3D_TRAVEL_FAILURE_STATE=", JSON.stringify({"surface":surface.call("GetSurfaceDiagnostics"),"context":failure_context}))
	write_failure_report(message)
	push_error("terrain3d_gameplay_travel_benchmark: "+message); press_w(false); quit(1)

func write_failure_report(message: String) -> void:
	if fixture.is_empty(): return
	var path := ProjectSettings.globalize_path(fixture.path_join("travel-benchmark-failure.json"))
	var data := {"failure":message, "backend":"legacy" if "--legacy" in OS.get_cmdline_user_args() else "runtime", "reference_v1":"--reference-v1" in OS.get_cmdline_user_args(), "speed_mps":arg_float("--speed=", 12.0), "distance_m":arg_float("--distance=", 512.0), "raw_frame_ms":frames, "raw_frame_profile":raw_frame_profile, "events":phases, "failure_context":failure_context, "latest_surface":latest_diag}
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_error("terrain3d_gameplay_travel_benchmark: could not persist failure report: "+path)
		return
	file.store_string(JSON.stringify(data, "\t"))
	file.close()
	print("TERRAIN3D_TRAVEL_FAILURE_REPORT=", path)
