extends "res://tools/atmosphere_visual_test.gd"

## R3a-1: the cloud reflection publication must not change a byte of the cloud image (docs/work-orders/reflections/R3a-design.md 4.2).
## Boots the certified gameplay scene (as tools/compositor_temporal_stability_test.gd does), freezes the explorer, wind, rain and the day clock,
## runs native without FSR2 / TAA, and holds Sunshine still (dither_speed 0, no resolution jitter, diagnostic_temporal_readback on). For two cases
##   C1 Native, interleave off      C2 Half, cloud_interleave 1 (2x2, exercises the fill)
## it runs off, on, off: reset (render_callback_serial 1000, refresh_compute) and draw K = 12 frames, then reads the colour image, the data image
## and the final frame back. off == on is the acceptance, off == off2 the control (a differing control is INCONCLUSIVE, never loosened).
## Also: C3 lightning glow (published at callbacks 4-5, gone from callback 6 on), C4 capture hold (the content stays), C5 another view
## (SubViewport sharing the world: reason other_view, weight 0, then back). Prints CLOUD_PUBLISH_DATA_RANGE (effective precision of .b).
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   godot --path . --script res://tools/cloud_publish_identity_gpu_test.gd
const K := 12
const BASE_SERIAL := 1000
const HOLD_FRAMES := 6
const WARM_MIN_FRAMES := 60
var bridge
var effect
var clouds_driver
var pause_menu
var problems: PackedStringArray = []
var inconclusive: PackedStringArray = []
var data_range_done := false
var case_name := ""

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	fixture = "res://.godot/cloud_publish_identity/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalCloudPublish"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	clock = game.get_node_or_null("RingDayNightCycle")
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	pause_menu = game.get_node_or_null("LodPauseMenu")
	clouds_driver = game.get_node_or_null("SunshineCloudsDriver")
	bridge = game.get_node_or_null("SunshineCloudBridge")
	placement = FIXTURE_HELPER.new()
	if explorer == null or surface == null or camera == null or grass == null or trees == null or clock == null or pause_menu == null or clouds_driver == null or bridge == null:
		return fail("missing gameplay nodes")
	effect = clouds_driver.get("clouds_resource")
	if effect == null or not effect.has_method("get_cloud_reflection_publication"): return fail("Sunshine effect has no cloud reflection publication")
	var ground_height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	if not is_finite(ground_height): return fail("dryland sample height was non-finite")
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground_height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	pause_menu.call("InstallRenderResolutionDefaults", fixture.path_join("render_resolution.tres"))
	# Freeze everything that moves on its own: sun, local wind / wind field, rain and Sunshine cloud wind.
	clock.set("Paused", true)
	clock.call("SetCycleFraction", float(select_visual_phases()["noon"]))
	var local_weather := game.get_node_or_null("LocalWeatherEffects")
	if local_weather != null: local_weather.set("Enabled", false)
	bridge.set("CloudWindMultiplier", 0.0)
	# Native, no FSR2, no TAA: no engine jitter.
	pause_menu.call("SetCompositorJitterCompensation", true)
	pause_menu.call("SetRenderResolutionPreset", 0)
	root.use_taa = false
	root.msaa_3d = Viewport.MSAA_DISABLED
	root.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED
	root.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
	root.scaling_3d_scale = 1.0
	await frames(2)
	await reset_pose(ground_height)
	explorer.call("SetPresentationView", 0.0, -10.0)
	clock.call("SetCycleFraction", float(select_visual_phases()["noon"]))

	for config in [{"name": "C1", "half": false}, {"name": "C2", "half": true}]:
		case_name = config["name"]
		await configure_case(bool(config["half"]))
		if not await warm(): return
		await identity_case()
		await lightning_case()
		await hold_case()
		if not bool(config["half"]):
			await other_view_case()

	Engine.time_scale = 1.0
	explorer.call("SetPresentationView", 0.0, -10.0)
	bridge.set("CloudReflectionPublicationEnabled", true)
	var verdict := "PASS"
	if not problems.is_empty(): verdict = "FAIL"
	elif not inconclusive.is_empty(): verdict = "INCONCLUSIVE"
	for message in problems: push_error("CLOUD_PUBLISH_IDENTITY_GPU_TEST=FAIL " + message)
	for message in inconclusive: push_error("CLOUD_PUBLISH_IDENTITY_GPU_TEST=INCONCLUSIVE " + message)
	print("CLOUD_PUBLISH_IDENTITY_GPU_TEST=%s" % verdict)
	game.queue_free()
	await process_frame
	quit(0 if verdict == "PASS" else 1)

# ---- setup -------------------------------------------------------------------------------------------------------------------------

func freeze_wind() -> void:
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 0.0)

func configure_case(half: bool) -> void:
	bridge.set("CloudResolutionScale", 1 if half else 0)
	bridge.set("CloudInterleave", 1 if half else 0)
	bridge.set("CloudResolutionJitter", false)
	bridge.set("DitherSpeed", 0.0)
	# Wall-clock input (design 4.2): the cloud-shadow crossfade blends by real time after each shadow bake, so hold it off.
	bridge.set("GpuShadowCrossfadeEnabled", false)
	bridge.call("ApplyTemporalQualityTuning", true)
	# Hold the effect still. diagnostic_temporal_readback gives every texture CAN_COPY_FROM (read back in both modes, so it biases neither).
	effect.set("dither_speed", 0.0)
	effect.set("resolution_jitter_enabled", false)
	effect.set("diagnostic_temporal_readback", true)
	effect.call("refresh_compute")
	await frames(2)

func static_ok() -> bool:
	if not bool(effect.get("macro_map_valid")) or bool(effect.get("macro_map_build_active")): return false
	if not bool(effect.get("storm_profile_map").valid): return false
	var shadow: Dictionary = effect.call("get_gpu_cloud_shadow_status")
	return not bool(shadow.get("near_pending", false)) and not bool(shadow.get("far_pending", false))

func warm() -> bool:
	Engine.time_scale = 1.0
	var done := 0
	var deadline := Time.get_ticks_msec() + 180000
	while Time.get_ticks_msec() < deadline:
		freeze_wind()
		await process_frame
		done += 1
		if done >= WARM_MIN_FRAMES and static_ok(): return true
	problems.append("%s: clouds never settled (macro map / storm map / cloud shadows)" % case_name)
	fail("%s: clouds never settled" % case_name)
	return false

# ---- one sequence ------------------------------------------------------------------------------------------------------------------

## Reset (render thread: serial, jitter counter, refresh_compute) then K frames. Returns the images after the last frame, the status and
## publication read after it, and (per_callback) the colour / data bytes and publication after each callback.
func run_sequence(enabled: bool, flash := false, per_callback := false) -> Dictionary:
	# Hold every delta-driven input (SunshineCloudsDriver dither phase, advection clock, wind positions) and fix the dither phase.
	Engine.time_scale = 0.0
	effect.set("current_time", 0.0)
	bridge.set("CloudReflectionPublicationEnabled", enabled)
	await process_frame # the bridge applies the toggle to the effect in _Process
	set_flash(false)
	var effect_ref = effect
	RenderingServer.call_on_render_thread(func() -> void:
		effect_ref.set("render_callback_serial", BASE_SERIAL)
		effect_ref.set("resolution_jitter_counter", 0)
		effect_ref.call("refresh_compute"))
	var per: Array = []
	for index in range(1, K + 1):
		freeze_wind()
		if flash: set_flash(index == 4 or index == 5)
		await RenderingServer.frame_post_draw
		if per_callback: per.append(read_state(false))
	var result := read_state(true)
	result["per"] = per
	result["serial_end"] = int(effect.get("render_callback_serial"))
	if int(result["serial_end"]) != BASE_SERIAL + K:
		inconclusive.append("%s: expected render_callback_serial %d after %d frames, got %d (more than one callback per frame?)" % [case_name, BASE_SERIAL + K, K, int(result["serial_end"])])
	set_flash(false)
	return result

func publication() -> Dictionary:
	var snapshot: Dictionary = effect.get("gameplay_cloud_frame_snapshot")
	if snapshot.is_empty():
		return effect.call("get_cloud_reflection_publication", Transform3D.IDENTITY, 0.0, 0.0, 0.0, false)
	return effect.call("get_cloud_reflection_publication", snapshot["frame"], float(snapshot["around"]), float(snapshot["across"]), float(snapshot["radius"]), true)

func read_state(include_final: bool, include_images := true) -> Dictionary:
	var state := {}
	if not include_images:
		state["pub"] = publication()
		state["status"] = bridge.get("CloudReflectionStatus")
		state["params"] = (state["status"] as Dictionary).get("params", Vector4.ZERO)
		return state
	var color_texture = effect.call("get_cloud_backdrop_texture", 0)
	var color_image: Image = color_texture.get_image() if color_texture != null else null
	var rids: Array = effect.get("accumulation_textures")
	var data_image: Image = null
	if rids.size() > 1:
		var wrapper := Texture2DRD.new()
		wrapper.texture_rd_rid = rids[0]
		data_image = wrapper.get_image()
	state["color_image"] = color_image
	state["data_image"] = data_image
	state["color"] = color_image.get_data() if color_image != null else PackedByteArray()
	state["data"] = data_image.get_data() if data_image != null else PackedByteArray()
	if include_final: state["final"] = root.get_viewport().get_texture().get_image().get_data()
	state["pub"] = publication()
	state["status"] = bridge.get("CloudReflectionStatus")
	state["params"] = (state["status"] as Dictionary).get("params", Vector4.ZERO)
	return state

func set_flash(on: bool) -> void:
	if not on:
		effect.call("set_segment_lights", [], 0)
		return
	# One transient segment light (b.w = 0 so color.a < 0.5: the in-cloud glow path), in front of the camera inside the cloud layer.
	var up_vector: Vector4 = effect.get("cloud_frame_up")
	var up := Vector3(up_vector.x, up_vector.y, up_vector.z).normalized()
	var forward := -camera.global_basis.z
	forward = (forward - up * forward.dot(up)).normalized()
	var across := forward.cross(up).normalized()
	var start := camera.global_position + forward * 2500.0 + up * (float(effect.get("cloud_floor")) + 500.0)
	var finish := start + across * 600.0
	effect.call("set_segment_lights", [Vector4(start.x, start.y, start.z, 4000.0), Vector4(finish.x, finish.y, finish.z, 0.0), Vector4(80.0, 80.0, 110.0, 0.0)], 1)

# ---- comparison --------------------------------------------------------------------------------------------------------------------

## "" when equal, else which image differs and where.
func compare_states(a: Dictionary, b: Dictionary, keys: Array) -> String:
	for key in keys:
		var bytes_a: PackedByteArray = a[key]
		var bytes_b: PackedByteArray = b[key]
		if bytes_a.is_empty() or bytes_b.is_empty(): return "%s image missing" % key
		if bytes_a == bytes_b: continue
		if bytes_a.size() != bytes_b.size(): return "%s size %d vs %d" % [key, bytes_a.size(), bytes_b.size()]
		if key == "final":
			for index in bytes_a.size():
				if bytes_a[index] != bytes_b[index]: return "%s first differing byte %d (pixel %d, %d vs %d)" % [key, index, index / 4, bytes_a[index], bytes_b[index]]
		var floats_a := bytes_a.to_float32_array()
		var floats_b := bytes_b.to_float32_array()
		for index in floats_a.size():
			if floats_a[index] != floats_b[index] and not (is_nan(floats_a[index]) and is_nan(floats_b[index])):
				return "%s first differing pixel %d channel %d (%f vs %f)" % [key, index / 4, index % 4, floats_a[index], floats_b[index]]
		return "%s bytes differ" % key
	return ""

## RenderingServer.global_shader_parameter_get is editor-only at run time (returns null); the bridge reports the RID it bound to the global.
func global_color_rid() -> RID:
	return RID(bridge.get("CloudReflectionStatus").get("bound_color", RID()))

# ---- C1 / C2 identity --------------------------------------------------------------------------------------------------------------

func identity_case() -> void:
	# One discarded reset first: the warm-up ran at time scale 1, so the first held sequence may still start from moving state.
	await run_sequence(false)
	var off1: Dictionary = await run_sequence(false)
	check_off(off1, true)
	var on: Dictionary = await run_sequence(true)
	check_on(on)
	var off2: Dictionary = await run_sequence(false)
	check_off(off2, false)
	var control := compare_states(off1, off2, ["color", "data", "final"])
	if control != "":
		inconclusive.append("%s: control (off vs off again) differs: %s. Find the remaining nondeterminism; do not loosen." % [case_name, control])
		return
	var difference := compare_states(off1, on, ["color", "data", "final"])
	if difference != "": problems.append("%s: publication on changed the output: %s" % [case_name, difference])
	else: print("CLOUD_PUBLISH_IDENTITY %s off == on == off2 (colour, data, final frame)" % case_name)
	if not data_range_done: report_data_range(on)

func check_off(state: Dictionary, _first: bool) -> void:
	var status: Dictionary = state["status"]
	if str(status.get("reason", "")) != "disabled": problems.append("%s off: status reason '%s', expected disabled" % [case_name, str(status.get("reason", ""))])
	var params: Vector4 = state["params"]
	if params.x != 0.0: problems.append("%s off: params.x %f, expected 0" % [case_name, params.x])
	# The publication is on from boot (warm-up included), so params.yzw keep the last published values; while off only x (the weight) must be 0.

func check_on(state: Dictionary) -> void:
	var status: Dictionary = state["status"]
	var pub: Dictionary = state["pub"]
	var rids: Array = effect.get("accumulation_textures")
	if float(status.get("weight", 0.0)) <= 0.0: problems.append("%s on: valid weight %f, expected > 0 (reason %s)" % [case_name, float(status.get("weight", 0.0)), str(status.get("reason", ""))])
	if int(pub["serial"]) != BASE_SERIAL + K: problems.append("%s on: snapshot serial %d, expected %d" % [case_name, int(pub["serial"]), BASE_SERIAL + K])
	if rids.size() < 2 or RID(pub["color"]) != rids[1] or RID(pub["data"]) != rids[0]: problems.append("%s on: snapshot RIDs are not accumulation_textures[1] / [0]" % case_name)
	if rids.size() < 2 or global_color_rid() != rids[1]: problems.append("%s on: global colour texture is not bound to accumulation_textures[1]" % case_name)

func report_data_range(state: Dictionary) -> void:
	var image: Image = state["data_image"]
	if image == null: return
	data_range_done = true
	var floats := image.get_data().to_float32_array()
	var scratch := PackedByteArray()
	scratch.resize(2)
	var max_b := 0.0
	var over := 0
	var exact := 0
	var counted := 0
	for index in range(2, floats.size(), 4 * 5): # every fifth pixel
		var value := floats[index]
		if not is_finite(value): continue
		max_b = maxf(max_b, value)
		if value > 65504.0: over += 1
		scratch.encode_half(0, value)
		if scratch.decode_half(0) == value: exact += 1
		counted += 1
	print("CLOUD_PUBLISH_DATA_RANGE max_b=%.1f over_65504=%d fp16_exact_fraction=%.4f" % [max_b, over, float(exact) / maxf(1.0, float(counted))])

# ---- C3 lightning ------------------------------------------------------------------------------------------------------------------

func lightning_case() -> void:
	var reference: Dictionary = await run_sequence(true, false, true)
	var flash_off1: Dictionary = await run_sequence(false, true)
	var flash_on: Dictionary = await run_sequence(true, true, true)
	var flash_off2: Dictionary = await run_sequence(false, true)
	var control := compare_states(flash_off1, flash_off2, ["color", "data", "final"])
	if control != "":
		inconclusive.append("%s C3: flash control (off vs off again) differs: %s" % [case_name, control])
		return
	var difference := compare_states(flash_off1, flash_on, ["color", "data", "final"])
	if difference != "": problems.append("%s C3 (a): publication on changed the flash sequence: %s" % [case_name, difference])
	var ref_per: Array = reference["per"]
	var flash_per: Array = flash_on["per"]
	if ref_per.size() != K or flash_per.size() != K:
		problems.append("%s C3: expected %d per-callback readbacks" % [case_name, K])
		return
	# (b) the glow is published at callbacks 4 and 5.
	var glow_seen := false
	for index in [3, 4]:
		if compare_states(ref_per[index], flash_per[index], ["color"]) != "": glow_seen = true
	if not glow_seen: problems.append("%s C3 (b): the flash does not change the published colour at callbacks 4-5 (check the light placement)" % case_name)
	# (c) from callback 6 on the published colour and data equal the reference: transient light never enters the history.
	for index in range(5, K):
		var leak := compare_states(ref_per[index], flash_per[index], ["color", "data"])
		if leak != "":
			problems.append("%s C3 (c): callback %d differs from the reference after the flash: %s (lightning leaks into history; escalate)" % [case_name, index + 1, leak])
			break
	# (d) valid weight and content serial behave as in the reference.
	for index in K:
		var a: Dictionary = ref_per[index]["pub"]
		var b: Dictionary = flash_per[index]["pub"]
		if int(a["serial"]) != int(b["serial"]) or bool(a["valid"]) != bool(b["valid"]):
			problems.append("%s C3 (d): callback %d serial/valid differ (%d/%s vs %d/%s)" % [case_name, index + 1, int(a["serial"]), str(a["valid"]), int(b["serial"]), str(b["valid"])])
			break

# ---- C4 capture hold ---------------------------------------------------------------------------------------------------------------

## B7b (78b8425) marches normally on the first callback after capture_hold is set (it arms there) and on any callback whose camera, projection,
## size or target differs from the armed one. So the invariants are checked on the held callbacks after the arming one, and the camera turn is
## the unheld callback that must publish a complete image.
func hold_case() -> void:
	var settled: Dictionary = await run_sequence(true)
	if not bool(settled["pub"]["valid"]): problems.append("%s C4: not valid before the hold (reason %s)" % [case_name, str(settled["pub"]["reason"])])
	var incomplete_before := int(effect.get("incomplete_content_publications"))
	effect.set("capture_hold", true)
	await RenderingServer.frame_post_draw # the arming callback: marches normally
	var base := read_state(false)
	var base_pub: Dictionary = base["pub"]
	var last_frames := int(base_pub["frames_drawn"])
	var weight_before := float(base["status"].get("weight", 0.0))
	for index in HOLD_FRAMES:
		freeze_wind()
		await RenderingServer.frame_post_draw
		var held := read_state(false)
		var pub: Dictionary = held["pub"]
		if int(pub["serial"]) != int(base_pub["serial"]) or int(pub["generation"]) != int(base_pub["generation"]):
			problems.append("%s C4 hold %d: content serial/generation moved (%d/%d -> %d/%d)" % [case_name, index + 1, int(base_pub["serial"]), int(base_pub["generation"]), int(pub["serial"]), int(pub["generation"])])
			break
		if RID(pub["color"]) != RID(base_pub["color"]) or RID(pub["data"]) != RID(base_pub["data"]): problems.append("%s C4 hold %d: published RIDs changed" % [case_name, index + 1])
		if (pub["dir_to_clip"] as Projection) != (base_pub["dir_to_clip"] as Projection): problems.append("%s C4 hold %d: dir_to_clip changed" % [case_name, index + 1])
		var bytes_difference := compare_states(base, held, ["color", "data"])
		if bytes_difference != "": problems.append("%s C4 hold %d: image bytes changed while held: %s" % [case_name, index + 1, bytes_difference])
		if bool(pub["valid"]) != true: problems.append("%s C4 hold %d: valid dropped to false (%s)" % [case_name, index + 1, str(pub["reason"])])
		if float(held["status"].get("weight", 0.0)) < weight_before: problems.append("%s C4 hold %d: weight fell (%f < %f)" % [case_name, index + 1, float(held["status"].get("weight", 0.0)), weight_before])
		if int(pub["frames_drawn"]) <= last_frames: problems.append("%s C4 hold %d: liveness did not advance" % [case_name, index + 1])
		last_frames = int(pub["frames_drawn"])
	# Turn the camera 5 degrees: B7b ends the hold on that callback, which must publish a complete image (serial advances).
	explorer.call("SetPresentationView", 0.0, -5.0)
	var advanced := false
	for attempt in 3:
		freeze_wind()
		await RenderingServer.frame_post_draw
		if int(publication()["serial"]) > int(base_pub["serial"]): advanced = true; break
	if not advanced: problems.append("%s C4: the unheld callback after the camera turn did not publish" % case_name)
	var rearmed := publication()
	for index in 3:
		freeze_wind()
		await RenderingServer.frame_post_draw
	if int(publication()["serial"]) != int(rearmed["serial"]):
		problems.append("%s C4: content kept advancing after the hold re-armed (%d -> %d)" % [case_name, int(rearmed["serial"]), int(publication()["serial"])])
	effect.set("capture_hold", false)
	var before_release := int(publication()["serial"])
	for index in 3:
		freeze_wind()
		await RenderingServer.frame_post_draw
	if int(publication()["serial"]) <= before_release: problems.append("%s C4: content serial did not advance after release" % case_name)
	if int(effect.get("incomplete_content_publications")) != incomplete_before: problems.append("%s C4: incomplete_content_publications moved to %d" % [case_name, int(effect.get("incomplete_content_publications"))])
	explorer.call("SetPresentationView", 0.0, -10.0)
	await frames(2)

# ---- C5 another view ---------------------------------------------------------------------------------------------------------------

func other_view_case() -> void:
	var settled: Dictionary = await run_sequence(true)
	var recovered: bool = await wait_for_weight(120)
	if not recovered: problems.append("C5: weight did not reach 1 before the other view (reason %s)" % str(settled["status"].get("reason", "")))
	var sub := SubViewport.new()
	sub.size = root.size
	sub.world_3d = root.world_3d
	sub.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var sub_camera := Camera3D.new()
	sub.add_child(sub_camera)
	root.add_child(sub)
	sub_camera.global_transform = Transform3D(camera.global_basis.rotated(camera.global_basis.y, deg_to_rad(90.0)), camera.global_position)
	sub_camera.current = true
	var other_before := int(effect.get("last_other_view_frame"))
	var seen: Array = []
	for index in 3:
		freeze_wind()
		await RenderingServer.frame_post_draw
		seen.append(read_state(false, false))
	sub.queue_free()
	for index in 4:
		freeze_wind()
		await RenderingServer.frame_post_draw
		seen.append(read_state(false, false))
	if int(effect.get("last_other_view_frame")) == other_before:
		problems.append("C5: the SubViewport never ran the cloud effect (last_other_view_frame unchanged); the case was not exercised")
		return
	# The callable lags the callbacks by one frame: frames 2..4 (the view rendering and the frame after) must be invalid and at weight 0.
	var other_view_frames := 0
	for index in range(1, 4):
		var state: Dictionary = seen[index]
		var status: Dictionary = state["status"]
		if str(status.get("reason", "")) == "other_view": other_view_frames += 1
		if float(status.get("weight", 1.0)) != 0.0 or (state["params"] as Vector4).x != 0.0:
			problems.append("C5: weight/params.x not 0 while another view renders (frame %d, reason %s)" % [index, str(status.get("reason", ""))])
	if other_view_frames == 0: problems.append("C5: status reason never other_view while another view rendered")
	if not await wait_for_weight(180): problems.append("C5: weight did not ramp back to 1 after the other view was removed")

func wait_for_weight(max_frames: int) -> bool:
	for index in max_frames:
		freeze_wind()
		await RenderingServer.frame_post_draw
		if float(bridge.get("CloudReflectionStatus").get("weight", 0.0)) >= 1.0: return true
	return false
