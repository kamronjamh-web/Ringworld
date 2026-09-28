extends "res://tools/atmosphere_visual_test.gd"
# Gameplay-only cloud temporal-history diagnostic.  This deliberately reuses
# the certified Terrain3D launcher/readiness path from atmosphere_visual_test.

const DRAWN_FRAMES := 120
const WARMUP_DRAWN_FRAMES := 16
const SKY_PITCH := 35.0
const HORIZON_PITCH := -8.0
const RUN_TIMEOUT_MSEC := 180000
var bridge: Node
var driver: Node
var clouds: Resource
var far_nodes: Array[GeometryInstance3D] = []
var far_layers := {}
var bridge_process_mode: Node.ProcessMode
var effect_enabled := true
var failed := false
var run_deadline_msec := 0
var raw_readback_serial := 0
var raw_readbacks := {}
var watchdog: SceneTreeTimer

func run() -> void:
	run_deadline_msec = Time.get_ticks_msec() + RUN_TIMEOUT_MSEC
	watchdog = create_timer(float(RUN_TIMEOUT_MSEC) / 1000.0)
	watchdog.timeout.connect(func() -> void: fail_capture("180-second fixture watchdog expired"))
	if RenderingServer.get_rendering_device() == null:
		return fail_capture("requires Vulkan/Forward+")
	fixture = "res://.godot/cloud-history-gameplay/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalCloudHistory"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	clock = game.get_node_or_null("RingDayNightCycle")
	graphics = game.get_node_or_null("GraphicsQuality")
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	bridge = game.get_node_or_null("SunshineCloudBridge")
	driver = game.get_node_or_null("SunshineCloudsDriver")
	if explorer == null or surface == null or clock == null or camera == null or bridge == null or driver == null:
		return fail_capture("missing certified gameplay cloud nodes")
	clouds = driver.get("clouds_resource") as Resource
	if clouds == null or not clouds.has_method("get_temporal_history_diagnostics"):
		return fail_capture("Sunshine cloud effect or temporal diagnostics unavailable")
	placement = FIXTURE_HELPER.new()
	var height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	if not is_finite(height): return fail_capture("non-finite dryland height")
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, SKY_PITCH)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	clock.set("Paused", true)
	clock.call("SetCycleFraction", noon_fraction())
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.call("ApplyCloudAppearanceTuning")
	await frames(20)
	collect_far_cloud_geometry()
	bridge_process_mode = bridge.process_mode
	effect_enabled = bool(clouds.get("enabled"))
	report = {"status":"RUNNING", "fixture":fixture, "captures":[], "backend":{"sunshine":"addons/SunshineClouds2/SunshineClouds.gd", "far":["materials/ring_far_cloud_clipmap.gdshader", "materials/ring_cloud_layer.gdshader"]}, "settings_persisted":false}
	if "--sampling-diagnostics" in OS.get_cmdline_user_args():
		await run_sampling_diagnostics(height)
		restore_renderers()
		if failed: return
		report.status = "PASS"
		var sampling_file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
		if sampling_file == null: return fail_capture("unable to write sampling report")
		sampling_file.store_string(JSON.stringify(report, "\t")); sampling_file.close()
		print("CLOUD_HISTORY_GAMEPLAY_SAMPLING_DIAGNOSTIC=PASS fixture=" + fixture)
		game.queue_free(); await process_frame; quit(0)
		return
	if "--continuous-motion" in OS.get_cmdline_user_args():
		await run_continuous_motion_diagnostic(height)
		restore_renderers()
		if failed: return
		report.status = "PASS"
		var motion_file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
		if motion_file == null: return fail_capture("unable to write continuous-motion report")
		motion_file.store_string(JSON.stringify(report, "\t")); motion_file.close()
		print("CLOUD_HISTORY_CONTINUOUS_MOTION=PASS fixture=" + fixture)
		game.queue_free(); await process_frame; quit(0)
		return
	await capture_case("history_095_both", 0.95, true, true, height)
	await capture_case("history_000_both", 0.0, true, true, height)
	await capture_case("history_095_sunshine_only", 0.95, true, false, height)
	await capture_case("history_095_far_only", 0.95, false, true, height)
	await capture_case("history_095_far_only_horizon", 0.95, false, true, height, HORIZON_PITCH)
	await capture_case("history_095_neither", 0.95, false, false, height)
	restore_renderers()
	if failed: return
	if report.captures.size() != 6: return fail_capture("incomplete capture set")
	report.status = "PASS"
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file == null: return fail_capture("unable to write report")
	file.store_string(JSON.stringify(report, "\t")); file.close()
	print("CLOUD_HISTORY_GAMEPLAY_DIAGNOSTIC=PASS fixture=" + fixture)
	game.queue_free(); await process_frame; quit(0)

func noon_fraction() -> float:
	var best := 0.0
	var elevation := -INF
	for step in 48:
		var f := float(step) / 48.0
		clock.call("SetCycleFraction", f)
		var candidate := float(clock.get("SunElevationDegrees"))
		if candidate > elevation: elevation = candidate; best = f
	return best

func collect_far_cloud_geometry() -> void:
	for node in all_nodes(game):
		if node is GeometryInstance3D:
			var geometry := node as GeometryInstance3D
			var material = geometry.material_override
			if material == null and geometry is MeshInstance3D and (geometry as MeshInstance3D).mesh != null:
				var mesh := (geometry as MeshInstance3D).mesh
				if mesh.get_surface_count() > 0: material = mesh.surface_get_material(0)
			if material is ShaderMaterial:
				var shader := (material as ShaderMaterial).shader
				var path := shader.resource_path if shader != null else ""
				if path.ends_with("/ring_far_cloud_clipmap.gdshader") or path.ends_with("/ring_cloud_layer.gdshader"):
					far_nodes.append(geometry); far_layers[geometry.get_instance_id()] = geometry.layers
	if far_nodes.is_empty(): fail_capture("no far cloud geometry found")

func run_sampling_diagnostics(height: float) -> void:
	# All changes remain on the runtime bridge instance and are restored below;
	# this fixture never writes gameplay tuning or project defaults.
	var saved := {"jitter":bridge.get("RayStartJitterStrength"), "min_step":bridge.get("MinStepDistanceMeters"), "max_step":bridge.get("MaxStepDistanceMeters"), "stationary":bridge.get("StationaryHistoryWeight"), "moving":bridge.get("MovingHistoryWeight"), "diagnostic":clouds.get("diagnostic_menu_history_weights"), "readback":clouds.get("diagnostic_temporal_readback")}
	setup_diagnostic_copy()
	if failed: return
	report.sampling_diagnostics = {"mode":"--sampling-diagnostics", "settlement_drawn_frames":DRAWN_FRAMES, "diagnostic_frames":8, "static_wind":bridge.get("CloudWindMultiplier"), "reject_reason_levels":[0.0,0.25,0.5,0.75], "cases":[]}
	var cases := [
		{"label":"high_factory", "history":.95, "jitter":1.0, "min_step":500.0, "max_step":958.0},
		{"label":"raw_factory", "history":0.0, "jitter":1.0, "min_step":500.0, "max_step":958.0},
		{"label":"high_jitter_zero", "history":.95, "jitter":0.0, "min_step":500.0, "max_step":958.0},
		{"label":"high_quarter_steps", "history":.95, "jitter":1.0, "min_step":125.0, "max_step":240.0}
	]
	for config: Dictionary in cases:
		await capture_sampling_case(config, height)
		if failed: break
	if not failed:
		await capture_sampling_motion_visuals(height)
	clouds.set("diagnostic_menu_history_weights", saved.diagnostic)
	clouds.set("diagnostic_temporal_readback", saved.readback)
	clouds.call("refresh_compute")
	bridge.set("RayStartJitterStrength", saved.jitter); bridge.set("MinStepDistanceMeters", saved.min_step); bridge.set("MaxStepDistanceMeters", saved.max_step)
	bridge.call("SetTemporalHistoryWeights", saved.stationary, saved.moving)
	bridge.call("ApplyCloudAppearanceTuning")
	cleanup_diagnostic_copy()

func capture_sampling_motion_visuals(start_height: float) -> void:
	# Deliberately separate from the fixed-pose metric cases: these captures are
	# for trail inspection after real motion, not matched-pose error scoring.
	print("CLOUD_HISTORY_SAMPLING_PROGRESS motion setup")
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.set("RayStartJitterStrength", 1.0); bridge.set("MinStepDistanceMeters", 500.0); bridge.set("MaxStepDistanceMeters", 958.0)
	bridge.call("SetTemporalHistoryWeights", .95, .95); bridge.call("ApplyCloudAppearanceTuning")
	set_renderer_isolation(true, false)
	var around := FIXTURE_AROUND
	var height := start_height
	var before: Dictionary = clouds.call("get_temporal_history_diagnostics")
	for frame in DRAWN_FRAMES:
		around += .1
		height = float(surface.call("SampleHeight", around, FIXTURE_ACROSS)) + 1.1
		if not is_finite(height): return fail_capture("non-finite motion safe height at frame " + str(frame))
		if not bool(placement.call("Place", explorer, around, height, FIXTURE_ACROSS)): return fail_capture("could not place motion frame " + str(frame))
		explorer.call("SetPresentationView", 8.0 * float(frame + 1) / DRAWN_FRAMES, SKY_PITCH)
		if not await drawn_frames(1, "motion frame " + str(frame + 1)): return
	var after_motion: Dictionary = clouds.call("get_temporal_history_diagnostics")
	var continuous := await save_motion_visual("motion_continuous_12m_yaw8", around, height, 8.0)
	if failed: return
	# This is a deliberate discontinuous orientation change. Capture after one
	# actual callback, then again thirty draws later for visual trail review.
	explorer.call("SetPresentationView", 38.0, SKY_PITCH)
	if not await drawn_frames(1, "motion instant yaw callback"): return
	var after_turn_callback: Dictionary = clouds.call("get_temporal_history_diagnostics")
	if int(after_turn_callback.get("callback_serial", 0)) <= int(after_motion.get("callback_serial", 0)):
		return fail_capture("instant yaw did not receive an actual render callback")
	var after_one := await save_motion_visual("motion_instant_yaw38_after_1_callback", around, height, 38.0)
	if failed: return
	if not await drawn_frames(30, "motion instant yaw trail review"): return
	var after_turn_30: Dictionary = clouds.call("get_temporal_history_diagnostics")
	var after_thirty := await save_motion_visual("motion_instant_yaw38_after_30_frames", around, height, 38.0)
	if failed: return
	report.sampling_diagnostics.motion_visual_checks = {"description":"Visual-only real-motion trail checks; no matched-pose temporal error is claimed.", "factory":{"history":.95,"jitter":1.0,"min_step":500.0,"max_step":958.0,"wind":0.0}, "continuous_motion":{"drawn_frames":DRAWN_FRAMES,"around_delta_meters":around - FIXTURE_AROUND,"yaw_degrees":{"start":0.0,"end":8.0},"before":before,"after":after_motion,"callback_delta":int(after_motion.get("callback_serial",0))-int(before.get("callback_serial",0)),"reset_count_deltas":temporal_reset_deltas(before,after_motion),"current_pose":continuous.pose,"captures":continuous}, "instant_turn":{"yaw_delta_degrees":30.0,"after_one_callback":{"temporal":after_turn_callback,"callback_delta":int(after_turn_callback.get("callback_serial",0))-int(after_motion.get("callback_serial",0)),"reset_count_deltas":temporal_reset_deltas(after_motion,after_turn_callback),"capture":after_one},"after_30_frames":{"temporal":after_turn_30,"callback_delta":int(after_turn_30.get("callback_serial",0))-int(after_turn_callback.get("callback_serial",0)),"reset_count_deltas":temporal_reset_deltas(after_turn_callback,after_turn_30),"capture":after_thirty}}}
	print("CLOUD_HISTORY_SAMPLING_PROGRESS motion complete")

func save_motion_visual(label: String, around: float, height: float, yaw: float) -> Dictionary:
	var screen_path := fixture.path_join(label + "_screen.png")
	var screen := root.get_viewport().get_texture().get_image()
	if screen.is_empty() or screen.save_png(ProjectSettings.globalize_path(screen_path)) != OK: fail_capture("motion screenshot failed for " + label); return {}
	var cloud_path := fixture.path_join(label + "_cloudcolor.png")
	if not await save_cloud_copy(cloud_path): fail_capture("motion cloud capture failed for " + label); return {}
	return {"screen":screen_path,"cloudcolor":cloud_path,"pose":{"around":around,"across":FIXTURE_ACROSS,"height":height,"yaw":yaw,"pitch":SKY_PITCH,"camera_world":camera.global_position,"camera_forward":-camera.global_transform.basis.z}}

func temporal_reset_deltas(before: Dictionary, after: Dictionary) -> Dictionary:
	var keys := ["explicit_refresh_request_count", "accumulation_resource_rebuild_count", "render_target_change_count", "zero_packed_history_callback_count"]
	var deltas := {}
	for key: String in keys: deltas[key] = int(after.get(key, 0)) - int(before.get(key, 0))
	return deltas

func setup_diagnostic_copy() -> void:
	# Do not sample through a CanvasItem: the full-game WorldEnvironment applies
	# tonemapping there. Read the published RGBA32F RD texture directly instead.
	raw_readbacks.clear()
	clouds.set("diagnostic_temporal_readback", true)
	clouds.call("refresh_compute")

func cleanup_diagnostic_copy() -> void:
	raw_readbacks.clear()

func capture_sampling_case(config: Dictionary, height: float) -> void:
	var label := str(config.label)
	print("CLOUD_HISTORY_SAMPLING_PROGRESS case=" + label + " setup")
	if not bool(placement.call("Place", explorer, FIXTURE_AROUND, height, FIXTURE_ACROSS)): return fail_capture("could not restore sampling pose for " + label)
	explorer.call("SetPresentationView", 0.0, SKY_PITCH)
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.set("RayStartJitterStrength", config.jitter); bridge.set("MinStepDistanceMeters", config.min_step); bridge.set("MaxStepDistanceMeters", config.max_step)
	bridge.call("SetTemporalHistoryWeights", config.history, config.history); bridge.call("ApplyCloudAppearanceTuning")
	set_renderer_isolation(true, false)
	if not await drawn_frames(DRAWN_FRAMES, label + " settle"): return
	print("CLOUD_HISTORY_SAMPLING_PROGRESS case=" + label + " settled_drawn=" + str(DRAWN_FRAMES))
	clouds.set("diagnostic_menu_history_weights", false)
	await RenderingServer.frame_post_draw
	var screenshot_path := fixture.path_join(label + "_screen.png")
	var screenshot := root.get_viewport().get_texture().get_image()
	if screenshot.is_empty() or screenshot.save_png(ProjectSettings.globalize_path(screenshot_path)) != OK: return fail_capture("sampling screenshot failed for " + label)
	var color_path := fixture.path_join(label + "_cloudcolor.png")
	if not await save_cloud_copy(color_path):
		fail_capture("cloud color save failed for " + label)
		return
	clouds.set("diagnostic_menu_history_weights", true)
	var aggregate := {"frames":8, "raw_alpha_min":INF, "raw_alpha_max":-INF, "accepted_empty":{"pixels":0,"used_sum":0.0}, "alpha_gt_005":{"pixels":0,"used_sum":0.0,"rejects":[0,0,0,0],"alpha_mismatch_max":0.0}, "alpha_gt_085":{"pixels":0,"used_sum":0.0,"rejects":[0,0,0,0],"alpha_mismatch_max":0.0}}
	var diagnostic_path := fixture.path_join(label + "_diagnosticmap.png")
	for frame in 8:
		await RenderingServer.frame_post_draw
		var diagnostic_image := await capture_cloud_copy()
		if diagnostic_image.is_empty(): return fail_capture("diagnostic cloud copy failed for " + label)
		if frame == 7 and diagnostic_image.save_png(ProjectSettings.globalize_path(diagnostic_path)) != OK: return fail_capture("diagnostic map save failed for " + label)
		aggregate_sampling_map(diagnostic_image, aggregate)
	clouds.set("diagnostic_menu_history_weights", false)
	for key: String in ["alpha_gt_005", "alpha_gt_085"]:
		var region: Dictionary = aggregate[key]
		region.used_mean = float(region.used_sum) / max(1, int(region.pixels))
		region.meaningful = int(region.pixels) >= 16
		region.erase("used_sum")
	var accepted: Dictionary = aggregate.accepted_empty
	accepted.used_mean = float(accepted.used_sum) / max(1, int(accepted.pixels)); accepted.erase("used_sum")
	var bytes: PackedByteArray = clouds.get("general_data") as PackedByteArray
	var controls := {"bytes":bytes.size(), "fast":ubo_float(bytes,688), "enabled":ubo_float(bytes,692), "phase":ubo_float(bytes,704), "effective_history_packed":ubo_float(bytes,244), "true_static":ubo_float(bytes,712), "diagnostic_flag":ubo_float(bytes,716)}
	var packed_decay := float(controls.effective_history_packed)
	if int(accepted.pixels) == 0 or not is_finite(packed_decay) or absf(float(accepted.used_mean) - packed_decay) > .10: return fail_capture("diagnostic accepted-empty weight does not match packed decay for " + label + ": " + JSON.stringify({"accepted":accepted,"packed":packed_decay,"requested":config.history}))
	if float(aggregate.raw_alpha_min) > .001 or float(aggregate.raw_alpha_max) <= .05: return fail_capture("diagnostic raw alpha range is invalid for " + label + ": " + JSON.stringify({"min":aggregate.raw_alpha_min,"max":aggregate.raw_alpha_max}))
	for value in controls.values():
		if value is float and not is_finite(value): return fail_capture("non-finite UBO control for " + label)
	if float(bridge.get("MinStepDistanceMeters")) > float(bridge.get("MaxStepDistanceMeters")): return fail_capture("invalid min/max step order for " + label)
	report.sampling_diagnostics.cases.append({"label":label, "screen":screenshot_path, "cloudcolor":color_path, "diagnosticmap":diagnostic_path, "requested":config, "applied":{"history":bridge.get("AppliedHistoryWeight"), "jitter":bridge.get("RayStartJitterStrength"), "min_step":bridge.get("MinStepDistanceMeters"), "max_step":bridge.get("MaxStepDistanceMeters"), "wind":bridge.get("CloudWindMultiplier")}, "ubo_controls":controls, "history_map":aggregate})
	print("CLOUD_HISTORY_SAMPLING_PROGRESS case=" + label + " complete")

func capture_cloud_copy() -> Image:
	var texture := clouds.call("get_cloud_backdrop_texture", 0) as Texture2D
	if texture == null: return Image.new()
	var rd_texture := texture as Texture2DRD
	if rd_texture == null or not rd_texture.texture_rd_rid.is_valid(): return Image.new()
	var request := raw_readback_serial; raw_readback_serial += 1
	RenderingServer.call_on_render_thread(_read_cloud_texture_on_render_thread.bind(request, rd_texture.texture_rd_rid, texture.get_width(), texture.get_height()))
	for ignored in 60:
		if raw_readbacks.has(request):
			var result: Dictionary = raw_readbacks[request]; raw_readbacks.erase(request)
			if not bool(result.get("ok", false)): fail_capture("raw cloud readback failed: " + str(result.get("error", "unknown"))); return Image.new()
			return Image.create_from_data(int(result.width), int(result.height), false, Image.FORMAT_RGBAF, result.bytes)
		if Time.get_ticks_msec() > run_deadline_msec: fail_capture("raw cloud readback timeout"); return Image.new()
		await RenderingServer.frame_post_draw
	fail_capture("raw cloud readback did not complete within 60 draws")
	return Image.new()

func _read_cloud_texture_on_render_thread(request: int, texture_rid: RID, width: int, height: int) -> void:
	var rd := RenderingServer.get_rendering_device()
	if rd == null or not texture_rid.is_valid():
		call_deferred("_complete_raw_cloud_readback", request, false, PackedByteArray(), width, height, "invalid render-device texture")
		return
	var format: RDTextureFormat = rd.texture_get_format(texture_rid)
	if format.format != RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT:
		call_deferred("_complete_raw_cloud_readback", request, false, PackedByteArray(), width, height, "unexpected RD format " + str(format.format))
		return
	var bytes := rd.texture_get_data(texture_rid, 0)
	if bytes.is_empty() or bytes.size() != width * height * 16:
		call_deferred("_complete_raw_cloud_readback", request, false, PackedByteArray(), width, height, "unexpected raw byte count " + str(bytes.size()))
		return
	call_deferred("_complete_raw_cloud_readback", request, true, bytes, width, height, "")

func _complete_raw_cloud_readback(request: int, ok: bool, bytes: PackedByteArray, width: int, height: int, error: String) -> void:
	raw_readbacks[request] = {"ok":ok, "bytes":bytes, "width":width, "height":height, "error":error}

func save_cloud_copy(path: String) -> bool:
	var image := await capture_cloud_copy()
	return not image.is_empty() and image.save_png(ProjectSettings.globalize_path(path)) == OK

func aggregate_sampling_map(image: Image, aggregate: Dictionary) -> void:
	for y in range(4, image.get_height(), 8):
		for x in range(4, image.get_width(), 8):
			var pixel := image.get_pixel(x, y)
			if not is_finite(pixel.r) or not is_finite(pixel.g) or not is_finite(pixel.b) or not is_finite(pixel.a): fail_capture("non-finite diagnostic map pixel"); return
			aggregate.raw_alpha_min = minf(float(aggregate.raw_alpha_min), pixel.a); aggregate.raw_alpha_max = maxf(float(aggregate.raw_alpha_max), pixel.a)
			var reason := clampi(int(floor(pixel.g * 4.0 + .5)), 0, 3)
			if pixel.a <= .05 and reason == 0:
				var accepted: Dictionary = aggregate.accepted_empty; accepted.pixels += 1; accepted.used_sum += pixel.r
			for threshold in [["alpha_gt_005", .05], ["alpha_gt_085", .85]]:
				if pixel.a > float(threshold[1]):
					var region: Dictionary = aggregate[threshold[0]]
					region.pixels += 1; region.used_sum += pixel.r; region.rejects[reason] += 1; region.alpha_mismatch_max = maxf(float(region.alpha_mismatch_max), pixel.b)

func ubo_float(bytes: PackedByteArray, offset: int) -> float:
	return bytes.decode_float(offset) if bytes.size() >= offset + 4 else NAN

func run_continuous_motion_diagnostic(height: float) -> void:
	var saved := {"wind":bridge.get("CloudWindMultiplier"), "jitter":bridge.get("RayStartJitterStrength"), "min_step":bridge.get("MinStepDistanceMeters"), "max_step":bridge.get("MaxStepDistanceMeters"), "stationary":bridge.get("StationaryHistoryWeight"), "moving":bridge.get("MovingHistoryWeight"), "diagnostic":clouds.get("diagnostic_menu_history_weights"), "readback":clouds.get("diagnostic_temporal_readback")}
	report.continuous_motion = {"mode":"--continuous-motion", "source_hz":60.0, "frames_per_route":96, "requested_history":{"stationary":.95,"moving":.85}, "wind":0.0, "factory":{"jitter":1.0,"min_step":500.0,"max_step":958.0}, "routes":[]}
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.set("RayStartJitterStrength", 1.0); bridge.set("MinStepDistanceMeters", 500.0); bridge.set("MaxStepDistanceMeters", 958.0)
	bridge.call("SetTemporalHistoryWeights", .95, .85); bridge.call("ApplyCloudAppearanceTuning")
	clouds.set("diagnostic_temporal_readback", true); clouds.call("refresh_compute")
	var routes := [
		{"label":"turn_15_dps", "turn_dps":15.0, "translate_mps":0.0, "far":false},
		{"label":"turn_60_dps", "turn_dps":60.0, "translate_mps":0.0, "far":false},
		{"label":"turn_120_dps", "turn_dps":120.0, "translate_mps":0.0, "far":false},
		{"label":"translate_6_mps", "turn_dps":0.0, "translate_mps":6.0, "far":false},
		{"label":"combined_6mps_60dps", "turn_dps":60.0, "translate_mps":6.0, "far":true}
	]
	for route: Dictionary in routes:
		await capture_continuous_route(route, height)
		if failed: break
	clouds.set("diagnostic_menu_history_weights", saved.diagnostic)
	clouds.set("diagnostic_temporal_readback", saved.readback); clouds.call("refresh_compute")
	bridge.set("CloudWindMultiplier", saved.wind); bridge.set("RayStartJitterStrength", saved.jitter); bridge.set("MinStepDistanceMeters", saved.min_step); bridge.set("MaxStepDistanceMeters", saved.max_step)
	bridge.call("SetTemporalHistoryWeights", saved.stationary, saved.moving); bridge.call("ApplyCloudAppearanceTuning")

func capture_continuous_route(route: Dictionary, height: float) -> void:
	var label := str(route.label)
	if Time.get_ticks_msec() > run_deadline_msec: return fail_capture("continuous-motion watchdog expired before " + label)
	if not bool(placement.call("Place", explorer, FIXTURE_AROUND, height, FIXTURE_ACROSS)):
		return fail_capture("could not reset continuous-motion pose for " + label)
	explorer.call("SetPresentationView", 0.0, SKY_PITCH)
	set_renderer_isolation(true, bool(route.far))
	clouds.set("diagnostic_menu_history_weights", false)
	if not await drawn_frames(WARMUP_DRAWN_FRAMES, label + " warmup"): return
	var samples := []
	var turn_per_frame := float(route.turn_dps) / 60.0
	var travel_per_frame := float(route.translate_mps) / 60.0
	for frame in 96:
		if Time.get_ticks_msec() > run_deadline_msec: return fail_capture("continuous-motion watchdog expired during " + label)
		var yaw := turn_per_frame * float(frame + 1)
		var around := FIXTURE_AROUND + travel_per_frame * float(frame + 1)
		if travel_per_frame != 0.0 and not bool(placement.call("Place", explorer, around, height, FIXTURE_ACROSS)):
			return fail_capture("continuous-motion translation placement failed for " + label)
		explorer.call("SetPresentationView", yaw, SKY_PITCH)
		await RenderingServer.frame_post_draw
		var temporal: Dictionary = clouds.call("get_temporal_history_diagnostics")
		var bytes: PackedByteArray = clouds.get("general_data") as PackedByteArray
		var sample := {"frame":frame + 1, "yaw_degrees":yaw, "around":around, "turn_delta_degrees":turn_per_frame, "translation_delta_meters":travel_per_frame, "callback_serial":temporal.get("callback_serial"), "packed_decay":temporal.get("last_packed_decay"), "requested_decay":temporal.get("last_requested_decay"), "zero_packed_callbacks":temporal.get("zero_packed_history_callback_count"), "rebuilds":temporal.get("accumulation_resource_rebuild_count"), "bridge":{"effective":bridge.get("EffectiveHistoryWeight"), "applied":bridge.get("AppliedHistoryWeight"), "motion":bridge.get("CameraHistoryMotion")}, "ubo_packed_decay":ubo_float(bytes,244)}
		if not is_finite(float(sample.packed_decay)) or not is_finite(float(sample.bridge.applied)):
			return fail_capture("non-finite continuous-motion history state for " + label)
		samples.append(sample)
	var final_callback := int((samples.back() as Dictionary).callback_serial)
	# This read occurs immediately after the final moving post-draw callback; no
	# additional draw is issued before the production screenshot.
	var image := root.get_viewport().get_texture().get_image()
	var image_path := fixture.path_join(label + "_final_moving.png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(image_path)) != OK:
		return fail_capture("continuous-motion screenshot failed for " + label)
	var diagnostic_replay := await replay_continuous_diagnostic(route, height)
	if failed: return
	report.continuous_motion.routes.append({"label":label, "renderer":renderer_state(), "final_screenshot":image_path, "screenshot_callback_serial":final_callback, "samples":samples, "diagnostic_replay":diagnostic_replay})

func replay_continuous_diagnostic(route: Dictionary, height: float) -> Dictionary:
	var label := str(route.label)
	if not bool(placement.call("Place", explorer, FIXTURE_AROUND, height, FIXTURE_ACROSS)):
		fail_capture("could not reset diagnostic replay pose for " + label)
		return {}
	explorer.call("SetPresentationView", 0.0, SKY_PITCH)
	set_renderer_isolation(true, bool(route.far))
	clouds.set("diagnostic_menu_history_weights", true)
	if not await drawn_frames(WARMUP_DRAWN_FRAMES, label + " diagnostic warmup"): return {}
	var turn_per_frame := float(route.turn_dps) / 60.0
	var travel_per_frame := float(route.translate_mps) / 60.0
	for frame in 96:
		var yaw := turn_per_frame * float(frame + 1)
		var around := FIXTURE_AROUND + travel_per_frame * float(frame + 1)
		if travel_per_frame != 0.0 and not bool(placement.call("Place", explorer, around, height, FIXTURE_ACROSS)):
			fail_capture("diagnostic replay translation placement failed for " + label)
			return {}
		explorer.call("SetPresentationView", yaw, SKY_PITCH)
		await RenderingServer.frame_post_draw
	var target_temporal: Dictionary = clouds.call("get_temporal_history_diagnostics")
	var target_bytes: PackedByteArray = clouds.get("general_data") as PackedByteArray
	var target_callback := int(target_temporal.get("callback_serial", -1))
	var readback := await capture_cloud_copy()
	var readback_callback := int((clouds.call("get_temporal_history_diagnostics") as Dictionary).get("callback_serial", -1))
	clouds.set("diagnostic_menu_history_weights", false)
	if readback.is_empty():
		fail_capture("continuous-motion diagnostic readback failed for " + label)
		return {}
	var map := {"raw_alpha_min":INF,"raw_alpha_max":-INF}
	for y in range(4, readback.get_height(), 8):
		for x in range(4, readback.get_width(), 8):
			var pixel := readback.get_pixel(x, y)
			map.raw_alpha_min = minf(float(map.raw_alpha_min), pixel.a); map.raw_alpha_max = maxf(float(map.raw_alpha_max), pixel.a)
	return {"target_callback_serial":target_callback, "readback_callback_serial":readback_callback, "readback_extra_callbacks":readback_callback - target_callback, "target_packed_decay":target_temporal.get("last_packed_decay"), "target_requested_decay":target_temporal.get("last_requested_decay"), "target_ubo_packed_decay":ubo_float(target_bytes,244), "map":map}

func capture_case(label: String, history: float, sunshine_on: bool, far_on: bool, height: float, pitch := SKY_PITCH) -> void:
	if failed: return
	if Time.get_ticks_msec() > run_deadline_msec: return fail_capture("capture watchdog expired before " + label)
	if not bool(placement.call("Place", explorer, FIXTURE_AROUND, height, FIXTURE_ACROSS)):
		return fail_capture("could not restore fixed pose for " + label)
	explorer.call("SetPresentationView", 0.0, pitch)
	bridge.call("SetTemporalHistoryWeights", history, history)
	set_renderer_isolation(sunshine_on, far_on)
	if not await drawn_frames(WARMUP_DRAWN_FRAMES, label + " warmup"): return
	var temporal_before: Dictionary = clouds.call("get_temporal_history_diagnostics")
	if not await drawn_frames(DRAWN_FRAMES, label): return
	var temporal: Dictionary = clouds.call("get_temporal_history_diagnostics")
	var callback_delta := int(temporal.get("callback_serial", 0)) - int(temporal_before.get("callback_serial", 0))
	if (sunshine_on and callback_delta <= 0) or not is_finite(float(temporal.get("last_requested_decay", NAN))) or not is_finite(float(temporal.get("last_packed_decay", NAN))):
		return fail_capture("invalid temporal callback state for " + label + ": " + JSON.stringify(temporal))
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	var image_path := fixture.path_join(label + ".png")
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(image_path)) != OK:
		return fail_capture("screenshot failed for " + label)
	var state := renderer_state()
	if int(state.get("sunshine_effect_count", 0)) <= 0 or not bool(state.get("sunshine_effect_identity_present", false)) or bool(state.get("sunshine_effect_enabled")) != sunshine_on or int(state.get("far_geometry_with_layers", -1)) != (int(state.get("far_geometry", 0)) if far_on else 0):
		return fail_capture("renderer isolation state invalid for " + label)
	report.captures.append({"label":label, "image":image_path, "drawn_frames":DRAWN_FRAMES, "history_requested":{"stationary":history, "moving":history}, "history_bridge":{"stationary":bridge.get("StationaryHistoryWeight"), "moving":bridge.get("MovingHistoryWeight"), "effective":bridge.get("EffectiveHistoryWeight"), "applied":bridge.get("AppliedHistoryWeight"), "camera_motion":bridge.get("CameraHistoryMotion"), "sun_suppression":bridge.get("SunHistorySuppression"), "applied_count":bridge.get("TemporalHistoryApplyCount")}, "temporal_before_after":{"before":temporal_before, "after":temporal, "callback_delta":callback_delta}, "renderer":state, "pose":{"around":FIXTURE_AROUND, "across":FIXTURE_ACROSS, "height":height, "yaw":0.0, "pitch":pitch, "camera_world":camera.global_position, "camera_forward":-camera.global_transform.basis.z}, "cycle":{"seconds":clock.call("GetCycleTimeSeconds"), "full_seconds":clock.get("FullCycleSeconds"), "sun_elevation_degrees":clock.get("SunElevationDegrees")}})

func set_renderer_isolation(sunshine_on: bool, far_on: bool) -> void:
	# The bridge owns effect state during normal gameplay.  Freeze it before
	# directly toggling the compositor so ownership cannot undo this test state.
	if sunshine_on:
		bridge.process_mode = bridge_process_mode
		clouds.set("enabled", true)
	else:
		bridge.process_mode = Node.PROCESS_MODE_DISABLED
		clouds.set("enabled", false)
	for node in far_nodes: node.layers = int(far_layers[node.get_instance_id()]) if far_on else 0

func restore_renderers() -> void:
	if clouds != null: clouds.set("enabled", effect_enabled)
	for node in far_nodes:
		if is_instance_valid(node): node.layers = int(far_layers.get(node.get_instance_id(), 1))
	if bridge != null: bridge.process_mode = bridge_process_mode

func renderer_state() -> Dictionary:
	var enabled_layers := 0
	for node in far_nodes:
		if is_instance_valid(node) and node.layers != 0: enabled_layers += 1
	var effect_count := 0
	var effect_identity := false
	var world_environment := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if world_environment != null and world_environment.compositor != null:
		var effects := world_environment.compositor.compositor_effects
		effect_count = effects.size()
		for effect in effects:
			if effect == clouds: effect_identity = true
	return {"sunshine_effect_enabled":clouds.get("enabled"), "bridge_process_mode":bridge.process_mode, "far_geometry":far_nodes.size(), "far_geometry_with_layers":enabled_layers, "far_geometry_names":far_nodes.map(func(node): return str(node.get_path()) + ":" + node.name + ":visible=" + str(node.is_visible_in_tree())), "sunshine_effect_count":effect_count, "sunshine_effect_identity_present":effect_identity, "far_visible":not far_nodes.is_empty()}

func drawn_frames(count: int, label: String) -> bool:
	for ignored in count:
		if Time.get_ticks_msec() > run_deadline_msec:
			fail_capture("capture watchdog expired during " + label)
			return false
		await RenderingServer.frame_post_draw
	return not failed

func fail(message: String) -> void:
	fail_capture(message)

func fail_capture(message: String) -> void:
	if failed: return
	failed = true
	report.status = "FAIL"; report.error = message
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report, "\t")); file.close()
	restore_renderers()
	push_error("CLOUD_HISTORY_GAMEPLAY_DIAGNOSTIC=FAIL " + message)
	quit(1)
