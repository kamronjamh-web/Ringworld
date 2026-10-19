extends "res://tools/atmosphere_visual_test.gd"

## FSR2 jitter probe. Boots the certified gameplay scene, enables FSR 2.2 at 67% through the pause-menu render-resolution path (with
## an isolated settings file), freezes the camera, then logs for 32 frames, from a CompositorEffect render callback:
##   * RenderSceneData.get_cam_projection() - all 16 elements; only column z (x, y) may vary (Godot writes the FSR2/TAA sub-pixel jitter
##     there). Also logs j_px = -z.xy * internal_size / 2 (the jitter in internal pixels) and checks |j_px| <= 0.5 and that the
##     sequence period equals the FSR2 phase count int(8 * (display / render width)^2) (17 at 67% of a 1600 px window).
##   * RenderSceneData.get_view_projection(0) column z (x, y)
##   * Camera3D.get_camera_projection() column z (x, y)       - the un-jittered projection, expected to be exactly zero
## and prints FSR_JITTER_PROBE varies=<true|false> max_delta=<value>. varies=true means the projection seen by compositor effects is
## jittered per frame.
## It then verifies the jitter-compensation plumbing: the Sunshine GenericData previous_projection z.xy equals the current z.xy
## (de-jittered), ring_jitter_controls xy = 0.5 * z.xy and z = 1, and the far backdrop effect's last_uv_offset = 0.5 * z.xy.
## --native keeps 100% bilinear and expects all of those values to be exactly zero. Prints FSR_JITTER_PROBE=PASS or exits 1.
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   godot --path . --script res://tools/fsr_jitter_probe.gd
##   godot --path . --script res://tools/fsr_jitter_probe.gd -- --native
const PROBE_FRAMES := 32
const WARMUP_FRAMES := 20
const VARIES_EPSILON := 1.0e-7

class ProjectionProbe extends CompositorEffect:
	var mutex := Mutex.new()
	var capturing := false
	var samples: Array = []

	func _init() -> void:
		effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_TRANSPARENT
		enabled = true

	func _render_callback(_effect_callback_type: int, render_data: RenderData) -> void:
		mutex.lock()
		var active := capturing
		mutex.unlock()
		if not active: return
		var scene_data := render_data.get_render_scene_data()
		if scene_data == null: return
		var cam: Projection = scene_data.get_cam_projection()
		var view: Projection = scene_data.get_view_projection(0)
		mutex.lock()
		samples.append({"cam": Vector2(cam.z.x, cam.z.y), "view": Vector2(view.z.x, view.z.y), "full": cam})
		mutex.unlock()

	func begin_capture() -> void:
		mutex.lock(); samples.clear(); capturing = true; mutex.unlock()

	func end_capture() -> Array:
		mutex.lock(); capturing = false; var result := samples.duplicate(); mutex.unlock()
		return result

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/fsr_jitter_probe/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalFsrProbe"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	placement = FIXTURE_HELPER.new()
	if explorer == null or surface == null or camera == null or grass == null or trees == null: return fail("missing gameplay nodes")
	var ground_height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	if not is_finite(ground_height): return fail("dryland sample height was non-finite")
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground_height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	var native_mode := "--native" in OS.get_cmdline_user_args()
	# FSR 2.2 at 67% through the production path (Performance preset), using an isolated settings file. --native keeps 100% bilinear.
	var pause_menu := game.get_node_or_null("LodPauseMenu")
	if pause_menu != null:
		pause_menu.call("InstallRenderResolutionDefaults", fixture.path_join("render_resolution.tres"))
		pause_menu.call("SetRenderResolutionPreset", 0 if native_mode else 4)
	if not native_mode and root.scaling_3d_mode != Viewport.SCALING_3D_MODE_FSR2:
		root.scaling_3d_mode = Viewport.SCALING_3D_MODE_FSR2
		root.scaling_3d_scale = 0.67
	print("FSR_JITTER_PROBE mode=%d scale=%.3f native=%s" % [root.scaling_3d_mode, root.scaling_3d_scale, str(native_mode)])
	if not native_mode and root.scaling_3d_mode != Viewport.SCALING_3D_MODE_FSR2: return fail("FSR2 unavailable (needs Forward+)")
	# Append the probe to whichever Compositor the camera actually renders with (the camera's overrides the WorldEnvironment's).
	var probe := ProjectionProbe.new()
	var compositor: Compositor = camera.compositor
	if compositor == null:
		for node in all_nodes(game):
			if node is WorldEnvironment and (node as WorldEnvironment).compositor != null:
				compositor = (node as WorldEnvironment).compositor
				break
	if compositor == null:
		compositor = Compositor.new()
		camera.compositor = compositor
	var effects := compositor.compositor_effects.duplicate()
	effects.append(probe)
	compositor.compositor_effects = effects
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	var clouds: Resource = driver.get("clouds_resource") as Resource if driver != null else null
	var backdrop := find_backdrop_effect(compositor)
	await frames(WARMUP_FRAMES)
	probe.begin_capture()
	var cloud_records: Array = []
	for ignored in PROBE_FRAMES:
		await RenderingServer.frame_post_draw
		cloud_records.append(read_cloud_jitter(clouds, backdrop))
	var samples := probe.end_capture()
	if samples.is_empty(): return fail("compositor callback produced no projection samples")
	var camera_projection := camera.get_camera_projection()
	var internal_size := Vector2(root.size) * root.scaling_3d_scale
	var first: Dictionary = samples[0]
	var first_full: Projection = first["full"]
	var max_delta := 0.0
	var max_other_delta := 0.0 # any element other than z.x / z.y
	var max_j_px := 0.0
	var all_zero := true
	for index in samples.size():
		var sample: Dictionary = samples[index]
		var cam: Vector2 = sample["cam"]
		var view: Vector2 = sample["view"]
		var full: Projection = sample["full"]
		max_delta = maxf(max_delta, maxf(absf(cam.x - (first["cam"] as Vector2).x), absf(cam.y - (first["cam"] as Vector2).y)))
		var elements: PackedStringArray = []
		for column in 4:
			for row in 4:
				var value := float(full[column][row])
				elements.append("%.6f" % value)
				if column == 2 and row < 2: continue
				max_other_delta = maxf(max_other_delta, absf(value - float(first_full[column][row])))
		var j_px := -cam * internal_size * 0.5
		max_j_px = maxf(max_j_px, maxf(absf(j_px.x), absf(j_px.y)))
		all_zero = all_zero and cam == Vector2.ZERO
		print("FSR_JITTER_PROBE frame=%d cam_z=(%.9f, %.9f) view_z=(%.9f, %.9f) camera3d_z=(%.9f, %.9f) j_px=(%.4f, %.4f) elements=[%s]" % [
			index, cam.x, cam.y, view.x, view.y, camera_projection.z.x, camera_projection.z.y, j_px.x, j_px.y, ", ".join(elements)])
	var varies := max_delta > VARIES_EPSILON
	print("FSR_JITTER_PROBE varies=%s max_delta=%.9f samples=%d other_elements_max_delta=%.9f max_abs_j_px=%.4f" % [
		str(varies).to_lower(), max_delta, samples.size(), max_other_delta, max_j_px])
	var failures: PackedStringArray = []
	if max_other_delta > 1.0e-6: failures.append("projection elements other than z.x/z.y varied (%.9f)" % max_other_delta)
	if camera_projection.z.x != 0.0 or camera_projection.z.y != 0.0: failures.append("Camera3D projection z.xy is not zero")
	if native_mode:
		if varies or not all_zero: failures.append("native projection z.xy is not exactly zero")
	else:
		if not varies: failures.append("FSR2 projection z.xy did not vary")
		if max_j_px > 0.5 + 1.0e-3: failures.append("jitter exceeds half an internal pixel (%.4f)" % max_j_px)
		# FSR2 phase count = int(8 * (display_width / render_width)^2) (ffxFsr2GetJitterPhaseCount): 17 at 67% of 1600, 8 at 100%.
		var period := observed_period(samples)
		var expected_periods: Array[int] = []
		for render_width in [floori(root.size.x * root.scaling_3d_scale), roundi(root.size.x * root.scaling_3d_scale)]:
			var phases := int(8.0 * pow(float(root.size.x) / float(maxi(1, render_width)), 2.0))
			if not expected_periods.has(phases): expected_periods.append(phases)
		print("FSR_JITTER_PROBE period=%d expected=%s" % [period, str(expected_periods)])
		if not expected_periods.has(period): failures.append("jitter period %d is not the FSR2 phase count %s" % [period, str(expected_periods)])
	# Compensation plumbing: decoded general_data, the Sunshine bookkeeping and the far backdrop offset.
	var checked := 0
	var expected_flag := 0.0 if native_mode else 1.0
	for record in cloud_records:
		if record.has("valid") and record["valid"]:
			checked += 1
			var ndc: Vector2 = record["ndc"]
			var prev_z: Vector2 = record["prev_z"]
			var jitter_uv: Vector2 = record["jitter_uv"]
			if absf(float(record["prev_zw"]) + 1.0) > 1.0e-4:
				failures.append("decoded previous_projection.z.w %.6f is not -1 (GenericData offset is stale)" % float(record["prev_zw"])); break
			if prev_z.distance_to(ndc) > 1.0e-6:
				failures.append("packed previous_projection.z.xy %s != current z.xy %s" % [str(prev_z), str(ndc)]); break
			if jitter_uv.distance_to(ndc * 0.5) > 1.0e-6:
				failures.append("ring_jitter_controls.xy %s != 0.5 * z.xy %s" % [str(jitter_uv), str(ndc * 0.5)]); break
			if absf(float(record["flag"]) - expected_flag) > 1.0e-6:
				failures.append("ring_jitter_controls.z %.3f != %.1f" % [float(record["flag"]), expected_flag]); break
			if native_mode and (ndc != Vector2.ZERO or prev_z != Vector2.ZERO or jitter_uv != Vector2.ZERO):
				failures.append("native jitter values are not exactly zero"); break
	if checked == 0: failures.append("Sunshine general_data jitter terms unavailable (nothing to verify)")
	if backdrop == null:
		failures.append("far backdrop compositor effect not found")
	else:
		for record in cloud_records:
			var offset: Vector2 = record["backdrop_uv"]
			var matched := false
			for sample_item in samples:
				if offset.distance_to((sample_item["cam"] as Vector2) * 0.5) <= 1.0e-6: matched = true; break
			if not matched: failures.append("backdrop last_uv_offset %s matches no probe sample * 0.5" % str(offset)); break
	print("FSR_JITTER_PROBE compensation_checked=%d backdrop=%s" % [checked, str(backdrop != null)])
	if failures.is_empty():
		print("FSR_JITTER_PROBE=PASS")
	else:
		for message in failures: push_error("FSR_JITTER_PROBE=FAIL " + message)
	game.queue_free()
	await process_frame
	quit(0 if failures.is_empty() else 1)

func find_backdrop_effect(compositor: Compositor) -> CompositorEffect:
	var lists: Array = [compositor.compositor_effects]
	for node in all_nodes(game):
		if node is WorldEnvironment and (node as WorldEnvironment).compositor != null:
			lists.append((node as WorldEnvironment).compositor.compositor_effects)
	for list in lists:
		for effect in list:
			if effect != null and "last_uv_offset" in effect: return effect
	return null

## Smallest p such that the cam z.xy sequence repeats with period p over every available pair (0 when none does).
func observed_period(samples: Array) -> int:
	for p in range(2, samples.size()):
		var matches := true
		for index in range(0, samples.size() - p):
			var a: Vector2 = samples[index]["cam"]
			var b: Vector2 = samples[index + p]["cam"]
			if absf(a.x - b.x) > 1.0e-6 or absf(a.y - b.y) > 1.0e-6: matches = false; break
		if matches: return p
	return 0

## GenericData float index of previous_camera_projection (CloudsInc.comp): 4 noise vec4 (16) + 3 colour vec4 (12) + 6 scalar vec4 (24)
## + raster/time/lighting vec4 (12) + cloud frame 4 vec4 (16) + storm 5 vec4 (20) + temporal_controls (4) = 104, then
## current_camera_transform (16), current_camera_inv_projection (16) and previous_camera_view (16) -> 152. Column z starts at +8.
const PREVIOUS_PROJECTION_FLOAT := 152

## Decodes the Sunshine GenericData jitter terms: previous_camera_projection z.xy (floats 160 / 161) and ring_jitter_controls (the last vec4).
func read_cloud_jitter(clouds: Resource, backdrop: CompositorEffect) -> Dictionary:
	var record := {"valid": false, "backdrop_uv": Vector2.ZERO}
	if backdrop != null: record["backdrop_uv"] = backdrop.get("last_uv_offset")
	if clouds == null: return record
	var data: PackedByteArray = clouds.get("general_data")
	if data.size() < 416: return record
	record["valid"] = true
	record["ndc"] = clouds.get("last_engine_jitter_ndc")
	var z_column := (PREVIOUS_PROJECTION_FLOAT + 8) * 4
	record["prev_z"] = Vector2(data.decode_float(z_column), data.decode_float(z_column + 4))
	# Sanity: the previous projection's z.w must be the perspective -1 (catches a stale layout offset).
	record["prev_zw"] = data.decode_float(z_column + 12)
	var base := data.size() - 16
	record["jitter_uv"] = Vector2(data.decode_float(base), data.decode_float(base + 4))
	record["flag"] = data.decode_float(base + 8)
	return record

func fail(message: String) -> void:
	push_error("FSR_JITTER_PROBE=FAIL " + message)
	quit(1)
