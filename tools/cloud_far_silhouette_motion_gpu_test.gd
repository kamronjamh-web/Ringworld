extends "res://tools/cloud_history_gameplay_diagnostic.gd"

# Guards the cloud history fix for fast pans across far silhouettes (the arch base, far ridges):
# history must not be dropped where the cloud lies in front of the opaque surface, and the
# arrival-frame cloud alpha must stay close to the settled value outside the leading-edge strip.
# Runs on the certified gameplay renderer over a generated fixture world.
# Run: tools/run_tests.ps1 -Tests cloud_far_silhouette_motion_gpu_test
# Thresholds are machine-indicative and were calibrated on the RTX 4070; they are not timing gates.
# Reads the Sunshine output data texture (accumulation_textures[0]) and the history-diagnostic colour
# (R weight, G reason, B mismatch, A alpha shown) of the cloud backdrop texture.

const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
const MARKER := "CLOUD_FAR_SILHOUETTE_MOTION_GPU_TEST"
const POSE_PITCH := 8.0
const POSE_YAWS: Array[float] = [0.0, 180.0]
const PAN_RATES: Array[float] = [3.0, 6.0]
const PAN_START_BEFORE := 60.0
const SETTLE_POSE_FRAMES := 150
const SETTLE_PAN_FRAMES := 90
const COVERED_ALPHA := 0.4
const SAMPLE_STEP := 2
const INNER_X_MIN := 0.2
const INNER_X_MAX := 0.8
const INNER_Y_MIN := 0.12
const INNER_DROP_ALPHA := 0.25
const FRONT_MARGIN_M := 2000.0
const WINDOW_STRIDE_PX := 4
const REASON_CODES: Array[float] = [0.0, 0.25, 0.5, 0.75, 0.9, 1.0]
# RTX 4070 calibration (2026-11-19): visibility_reject_in_front <= 0.0003, inner_drop <= 0.0044, rejected_drop <= 0.0096 with the fix;
# --reject-resolve-off gives rejected_drop up to 0.019 (the raw one-frame march on rejected pixels).
const MAX_VISIBILITY_REJECT_IN_FRONT := 0.002
const MAX_INNER_DROP := 0.015
const MAX_REJECTED_DROP := 0.013

var cases: Array[Dictionary] = []

func fail_capture(message: String) -> void:
	if failed: return
	failed = true
	report.status = "FAIL"; report.error = message
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report, "\t")); file.close()
	push_error(MARKER + "=FAIL " + message)
	call_deferred("finish_failed_fixture")

func finish_failed_fixture() -> void:
	if is_instance_valid(game): game.queue_free()
	for unused in 3:
		await process_frame
		await RenderingServer.frame_post_draw
	if clouds != null:
		var retiring_clouds := clouds
		RenderingServer.call_on_render_thread(func(): retiring_clouds.call("clear_compute"))
	for unused in 3:
		await process_frame
		await RenderingServer.frame_post_draw
	clouds = null; bridge = null; driver = null; game = null
	quit(1)

func install_isolated_preferences(next: Node) -> void:
	var isolation: Dictionary = ISOLATION.new().ExecuteFixture("isolation", next, fixture.path_join("preferences"))
	if not bool(isolation.get("pass", false)): fail_capture("pre-entry save/preference isolation failed")

func remove_fixture_dir() -> void:
	var absolute := ProjectSettings.globalize_path(fixture)
	_remove_recursive(absolute)

func _remove_recursive(path: String) -> void:
	var dir := DirAccess.open(path)
	if dir == null: return
	for entry in dir.get_directories(): _remove_recursive(path.path_join(entry))
	for entry in dir.get_files(): dir.remove(entry)
	DirAccess.remove_absolute(path)

func run() -> void:
	run_deadline_msec = Time.get_ticks_msec() + 600000
	create_timer(600.0).timeout.connect(func(): fail_capture("far silhouette motion watchdog expired"))
	if RenderingServer.get_rendering_device() == null: return fail_capture("Forward+ required")
	fixture = "res://.godot/cloud-far-silhouette/%s_%s" % [OS.get_process_id(), Time.get_ticks_usec()]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalFarSilhouette"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null or failed: return
	explorer = game.get_node("Explorer"); surface = game.get_node("Terrain3DGameplay")
	clock = game.get_node("RingDayNightCycle"); graphics = game.get_node("GraphicsQuality")
	grass = game.get_node("StandingGrass"); trees = game.get_node("BiomeDecorations")
	global_terrain = game.get_node("GlobalTerrainLod3")
	camera =game.get_node("Explorer/Head/Camera3D") as Camera3D
	bridge = game.get_node("SunshineCloudBridge"); driver = game.get_node("SunshineCloudsDriver")
	clouds = driver.get("clouds_resource") as Resource
	placement = FIXTURE_HELPER.new()
	var height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, POSE_PITCH)
	if not await wait_ready() or failed: return
	var weather := game.get_node("RingVolumetricClouds")
	weather.set("CloudSimulationPaused", true)
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	clock.set("Paused", true); clock.call("SetCycleFraction", noon_fraction())
	var prior_wind: Variant = bridge.get("CloudWindMultiplier")
	var prior_bias: Variant = bridge.get("CloudCoverageBias")
	bridge.set("CloudWindMultiplier", 0.0)
	bridge.set("CloudCoverageBias", 0.35)
	bridge.call("ApplyCloudAppearanceTuning")
	await drawn_frames(8, "publication")
	bridge_process_mode = bridge.process_mode; effect_enabled = true
	bridge.process_mode = Node.PROCESS_MODE_DISABLED; driver.process_mode = Node.PROCESS_MODE_DISABLED
	var producer := game.get_node_or_null("TerrainCloudSunVisibility")
	if producer != null: producer.process_mode = Node.PROCESS_MODE_DISABLED
	clock.process_mode = Node.PROCESS_MODE_DISABLED
	var day_side_atmosphere := game.get_node("DaySideAtmosphere")
	day_side_atmosphere.process_mode = Node.PROCESS_MODE_DISABLED
	var local_effects := game.get_node_or_null("LocalWeatherEffects")
	if local_effects != null: local_effects.process_mode = Node.PROCESS_MODE_DISABLED
	# Fixture isolation as in the targeted gameplay test (raw cloud copy, no haze), but with the production ray-start jitter and dither:
	# the raw one-frame march must stay noisy so the rejected-pixel resolve is exercised. Interleave stays at the shipped setting.
	clouds.set("terrain_cloud_sun_profile", [])
	clouds.set("ring_atmosphere_profile", [])
	# Debug A/B (calibration only): --reject-resolve-off runs the pans without the rejected-pixel spatial resolve.
	if "--reject-resolve-off" in OS.get_cmdline_user_args(): clouds.set("cloud_reject_resolve", false)
	clouds.call("refresh_compute")
	clouds.set("accumulation_decay", 0.95)
	clouds.set("depth_history_rejection_enabled", true)
	clouds.set("cloud_history_compatibility_enabled", true)
	clouds.set("diagnostic_temporal_readback", true)
	clouds.set("diagnostic_menu_history_weights", false)
	clouds.set("point_lights_data", []); clouds.set("lights_updated", true)
	clouds.call("request_history_reset")
	await drawn_frames(8, "fixture settle")
	if failed: return
	report = {"status":"RUNNING", "fixture":fixture, "cases":[]}
	var callbacks_before := int((clouds.call("get_temporal_history_diagnostics") as Dictionary).get("reject_resolve_callbacks", 0))
	for yaw in POSE_YAWS:
		for rate in PAN_RATES:
			var result := await run_case(yaw, rate)
			if failed: return
			cases.append(result)
			report.cases.append(result)
			print("CLOUD_FAR_SILHOUETTE_CASE yaw=%s rate=%s covered=%s in_front=%s visibility_reject_in_front=%.5f inner_drop=%.5f rejected=%s rejected_drop=%.4f" % [
				yaw, rate, result.covered, result.in_front, result.visibility_reject_in_front, result.inner_drop, result.rejected, result.rejected_drop])
	var temporal: Dictionary = clouds.call("get_temporal_history_diagnostics")
	var callbacks_after := int(temporal.get("reject_resolve_callbacks", 0))
	report["reject_resolve_ready"] = temporal.get("reject_resolve_ready", false)
	report["reject_resolve_callbacks_delta"] = callbacks_after - callbacks_before
	clouds.set("diagnostic_menu_history_weights", false)
	clouds.set("diagnostic_temporal_readback", false)
	bridge.set("CloudWindMultiplier", prior_wind)
	bridge.set("CloudCoverageBias", prior_bias)
	var reason := ""
	for result in cases:
		if int(result.covered) <= 0: reason = "no covered cloud pixels at yaw %s rate %s" % [result.yaw, result.rate]; break
		if float(result.visibility_reject_in_front) > MAX_VISIBILITY_REJECT_IN_FRONT:
			reason = "visibility reject in front %.5f > %.5f at yaw %s rate %s" % [result.visibility_reject_in_front, MAX_VISIBILITY_REJECT_IN_FRONT, result.yaw, result.rate]; break
		if float(result.rejected_drop) > MAX_REJECTED_DROP:
			reason = "rejected-pixel drop %.5f > %.5f at yaw %s rate %s" % [result.rejected_drop, MAX_REJECTED_DROP, result.yaw, result.rate]; break
		if float(result.inner_drop) > MAX_INNER_DROP:
			reason = "inner drop %.5f > %.5f at yaw %s rate %s" % [result.inner_drop, MAX_INNER_DROP, result.yaw, result.rate]; break
	if reason == "" and callbacks_after <= callbacks_before:
		reason = "reject_resolve_callbacks did not increase (%d -> %d); fix not live" % [callbacks_before, callbacks_after]
	if reason != "": return fail_capture(reason)
	report.status = "PASS"
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report, "\t")); file.close()
	print(MARKER + "=PASS")
	game.queue_free(); await process_frame; await process_frame
	remove_fixture_dir()
	quit(0)

func view_pose(yaw: float) -> void:
	explorer.call("SetPresentationView", yaw, POSE_PITCH)

func pan_to_pose(yaw: float, rate: float) -> bool:
	var steps := int(ceil(PAN_START_BEFORE / rate))
	view_pose(yaw - PAN_START_BEFORE)
	if not await drawn_frames(SETTLE_PAN_FRAMES, "pan settle"): return false
	for step in range(1, steps + 1):
		view_pose(yaw - PAN_START_BEFORE + minf(float(step) * rate, PAN_START_BEFORE))
		if not await drawn_frames(1, "pan step"): return false
	return true

func run_case(yaw: float, rate: float) -> Dictionary:
	view_pose(yaw)
	clouds.set("diagnostic_menu_history_weights", false)
	if not await drawn_frames(SETTLE_POSE_FRAMES, "settle pose"): return {}
	var settled_color := await capture_cloud_copy()
	var settled_data := await capture_output_data()
	if failed: return {}
	clouds.set("diagnostic_menu_history_weights", true)
	if not await pan_to_pose(yaw, rate): return {}
	var diagnostic_color := await capture_cloud_copy()
	if failed: return {}
	clouds.set("diagnostic_menu_history_weights", false)
	await drawn_frames(2, "diagnostic off")
	if not await pan_to_pose(yaw, rate): return {}
	var arrival_color := await capture_cloud_copy()
	if failed: return {}
	return measure_case(yaw, rate, settled_color, settled_data, diagnostic_color, arrival_color)

func measure_case(yaw: float, rate: float, settled_color: Image, settled_data: Image, diagnostic: Image, arrival: Image) -> Dictionary:
	if settled_color.is_empty() or settled_data.is_empty() or diagnostic.is_empty() or arrival.is_empty():
		fail_capture("missing readback image"); return {}
	var width := settled_color.get_width(); var height := settled_color.get_height()
	if diagnostic.get_width() != width or diagnostic.get_height() != height or arrival.get_width() != width or arrival.get_height() != height:
		fail_capture("colour readbacks differ in size"); return {}
	var data_width := settled_data.get_width(); var data_height := settled_data.get_height()
	var settled_floats: PackedFloat32Array = settled_color.get_data().to_float32_array()
	var arrival_floats: PackedFloat32Array = arrival.get_data().to_float32_array()
	var diagnostic_floats: PackedFloat32Array = diagnostic.get_data().to_float32_array()
	var data_floats: PackedFloat32Array = settled_data.get_data().to_float32_array()
	var covered := 0; var in_front := 0; var visibility_reject := 0
	var inner_covered := 0; var inner_dropped := 0; var rejected := 0; var rejected_dropped := 0
	var reasons := {}
	for code in REASON_CODES: reasons[str(code)] = 0
	var inner_x0 := int(float(width) * INNER_X_MIN); var inner_x1 := int(float(width) * INNER_X_MAX)
	var inner_y0 := int(float(height) * INNER_Y_MIN)
	# Per-frame pan motion in colour-readback pixels (horizontal field of view from the camera's vertical fov and the aspect).
	var hfov := 2.0 * rad_to_deg(atan(tan(deg_to_rad(camera.fov) * 0.5) * float(width) / float(height)))
	var window_px := int(ceil(rate / hfov * float(width))) + 4
	for y in range(0, height, SAMPLE_STEP):
		for x in range(0, width, SAMPLE_STEP):
			var index := (y * width + x) * 4
			var settled_alpha := settled_floats[index + 3]
			if settled_alpha < COVERED_ALPHA: continue
			covered += 1
			var dx := mini(data_width - 1, x * data_width / width)
			var dy := mini(data_height - 1, y * data_height / height)
			var data_index := (dy * data_width + dx) * 4
			var march_end := data_floats[data_index + 1]
			# In front: no opaque surface nearer than the cloud end + margin at this pixel or anywhere a pan step can reproject it from
			# (the same row within the per-frame motion). Sky behind a near ridge that moved is a real disocclusion and stays rejected.
			var front := march_end > 0.0
			var reach := march_end + FRONT_MARGIN_M
			var sx := -window_px
			while front and sx <= window_px:
				var nx := mini(data_width - 1, maxi(0, (x + sx) * data_width / width))
				var depth_log := data_floats[(dy * data_width + nx) * 4 + 3]
				if depth_log > -7.5 and exp(depth_log * log(2.0)) - 1.0 <= reach: front = false
				sx += WINDOW_STRIDE_PX
			var reason_value := diagnostic_floats[index + 1]
			var nearest := REASON_CODES[0]
			for code in REASON_CODES:
				if absf(reason_value - code) < absf(reason_value - nearest): nearest = code
			reasons[str(nearest)] = int(reasons[str(nearest)]) + 1
			# Rejected history (0.5 visibility, 0.75 offscreen); on full-march frames these pixels show the reject-resolve mean (A).
			if absf(reason_value - 0.5) <= 0.05 or absf(reason_value - 0.75) <= 0.05:
				rejected += 1
				# Alpha the rejected pixel shows on the arrival frame (diagnostic A) against its settled value.
				if settled_alpha - diagnostic_floats[index + 3] > INNER_DROP_ALPHA: rejected_dropped += 1
			if front:
				in_front += 1
				if absf(reason_value - 0.5) <= 0.05: visibility_reject += 1
			if x >= inner_x0 and x < inner_x1 and y >= inner_y0:
				inner_covered += 1
				if settled_alpha - arrival_floats[index + 3] > INNER_DROP_ALPHA: inner_dropped += 1
	var visibility_fraction := float(visibility_reject) / float(in_front) if in_front > 0 else 0.0
	var inner_fraction := float(inner_dropped) / float(inner_covered) if inner_covered > 0 else 0.0
	return {"yaw":yaw, "rate":rate, "covered":covered, "in_front":in_front,
		"visibility_reject_in_front":visibility_fraction, "inner_covered":inner_covered, "inner_drop":inner_fraction,
		"rejected":rejected, "rejected_drop":float(rejected_dropped) / float(rejected) if rejected > 0 else 0.0, "reason_counts":reasons}

# Output data texture of view 0 (accumulation_textures[0], RGBA32F). Size is read from the texture itself.
func capture_output_data() -> Image:
	var rids: Array[RID] = clouds.get("accumulation_textures") as Array[RID]
	if rids.is_empty() or not rids[0].is_valid():
		fail_capture("accumulation_textures[0] (output data) unavailable"); return Image.new()
	var request := raw_readback_serial; raw_readback_serial += 1
	RenderingServer.call_on_render_thread(_read_output_data_on_render_thread.bind(request, rids[0]))
	for ignored in 60:
		if raw_readbacks.has(request):
			var result: Dictionary = raw_readbacks[request]; raw_readbacks.erase(request)
			if not bool(result.get("ok", false)):
				fail_capture("output data readback failed: " + str(result.get("error", "unknown"))); return Image.new()
			return Image.create_from_data(int(result.width), int(result.height), false, Image.FORMAT_RGBAF, result.bytes)
		if failed or Time.get_ticks_msec() > run_deadline_msec: return Image.new()
		await RenderingServer.frame_post_draw
	fail_capture("output data readback did not complete"); return Image.new()

func _read_output_data_on_render_thread(request: int, texture_rid: RID) -> void:
	var rd := RenderingServer.get_rendering_device()
	if rd == null or not texture_rid.is_valid():
		call_deferred("_complete_raw_cloud_readback", request, false, PackedByteArray(), 0, 0, "invalid output data texture")
		return
	var format: RDTextureFormat = rd.texture_get_format(texture_rid)
	var width := int(format.width); var height := int(format.height)
	if format.format != RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT:
		call_deferred("_complete_raw_cloud_readback", request, false, PackedByteArray(), width, height, "unexpected RD format " + str(format.format))
		return
	var bytes := rd.texture_get_data(texture_rid, 0)
	if bytes.size() != width * height * 16:
		call_deferred("_complete_raw_cloud_readback", request, false, PackedByteArray(), width, height, "unexpected raw byte count " + str(bytes.size()))
		return
	call_deferred("_complete_raw_cloud_readback", request, true, bytes, width, height, "")
