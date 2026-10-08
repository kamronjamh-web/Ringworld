extends "res://tools/cloud_history_gameplay_diagnostic.gd"

# Actual certified gameplay renderer. The portable helper test cannot prove
# transient light isolation or main/far opaque history integration.
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
var fixture_states: Array[Dictionary] = []

# Keep the render loop alive while this fixture's world and RD resources retire.
# Do not restore bridge/weather publishers during a failed paired capture.
func fail_capture(message: String) -> void:
	if failed: return
	failed = true
	report.status = "FAIL"; report.error = message
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report,"\t")); file.close()
	push_error("CLOUD_HISTORY_TARGETED_GAMEPLAY_GPU_TEST=FAIL " + message)
	call_deferred("finish_failed_fixture")

func finish_failed_fixture() -> void:
	print("TARGETED_HISTORY_CLEANUP=queue_world")
	if is_instance_valid(game): game.queue_free()
	for unused in 3:
		await process_frame
		await RenderingServer.frame_post_draw
	print("TARGETED_HISTORY_CLEANUP=world_drained")
	if clouds != null:
		var retiring_clouds := clouds
		RenderingServer.call_on_render_thread(func():
			print("TARGETED_HISTORY_CLEANUP=rd_clear_begin")
			retiring_clouds.call("clear_compute")
			print("TARGETED_HISTORY_CLEANUP=rd_clear_end"))
	for unused in 3:
		await process_frame
		await RenderingServer.frame_post_draw
	print("TARGETED_HISTORY_CLEANUP=rd_drained")
	clouds = null; bridge = null; driver = null; game = null
	print("TARGETED_HISTORY_CLEANUP=quit1")
	quit(1)

func install_isolated_preferences(next: Node) -> void:
	var isolation: Dictionary = ISOLATION.new().ExecuteFixture("isolation", next, fixture.path_join("preferences"))
	if not bool(isolation.get("pass", false)): fail_capture("pre-entry save/preference isolation failed")

func run() -> void:
	run_deadline_msec = Time.get_ticks_msec() + 240000
	create_timer(240.0).timeout.connect(func(): fail_capture("targeted gameplay watchdog expired"))
	if RenderingServer.get_rendering_device() == null: return fail_capture("Forward+ required")
	fixture = "res://.godot/cloud-history-targeted/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalTargetedHistory"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null or failed: return
	explorer = game.get_node("Explorer"); surface = game.get_node("Terrain3DGameplay")
	clock = game.get_node("RingDayNightCycle"); graphics = game.get_node("GraphicsQuality")
	grass = game.get_node("StandingGrass"); trees = game.get_node("BiomeDecorations")
	global_terrain = game.get_node("GlobalTerrainLod3")
	camera = game.get_node("Explorer/Head/Camera3D") as Camera3D
	bridge = game.get_node("SunshineCloudBridge"); driver = game.get_node("SunshineCloudsDriver")
	clouds = driver.get("clouds_resource") as Resource
	placement = FIXTURE_HELPER.new()
	var height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, SKY_PITCH)
	if not await wait_ready() or failed: return
	var weather := game.get_node("RingVolumetricClouds")
	weather.set("CloudSimulationPaused", true)
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	clock.set("Paused", true); clock.call("SetCycleFraction", noon_fraction())
	bridge.set("CloudWindMultiplier", 0.0); bridge.call("ApplyCloudAppearanceTuning")
	await drawn_frames(8, "publication")
	bridge_process_mode = bridge.process_mode; effect_enabled = true
	bridge.process_mode = Node.PROCESS_MODE_DISABLED; driver.process_mode = Node.PROCESS_MODE_DISABLED
	var producer := game.get_node_or_null("TerrainCloudSunVisibility")
	if producer != null: producer.process_mode = Node.PROCESS_MODE_DISABLED
	clock.process_mode = Node.PROCESS_MODE_DISABLED
	# DaySideAtmosphere eases its day factor independently of the paused clock
	# and calls bridge.SetLocalDayFactor even when the bridge is disabled.
	# Hold its already-published real noon lighting for fixed-radiance captures.
	var day_side_atmosphere := game.get_node("DaySideAtmosphere")
	day_side_atmosphere.process_mode = Node.PROCESS_MODE_DISABLED
	var local_effects := game.get_node_or_null("LocalWeatherEffects")
	if local_effects != null: local_effects.process_mode = Node.PROCESS_MODE_DISABLED
	# Isolate transient lighting from unfinished terrain-atlas illumination.
	# Its actual producer/receiver integration has a separate production fixture.
	clouds.set("terrain_cloud_sun_profile", [])
	# Raw cloud copy has no exposure; disable final haze only in this fixture so
	# the assertions measure persistent/transient RGB rather than atmosphere.
	clouds.set("ring_atmosphere_profile", [])
	clouds.set("ray_start_jitter_strength", 0.0)
	var dither_image := Image.create(1,1,false,Image.FORMAT_RGBA8); dither_image.fill(Color(0.5,0.5,0.5,1))
	var dither_texture := ImageTexture3D.new()
	if dither_texture.create(Image.FORMAT_RGBA8,1,1,1,false,[dither_image]) != OK: return fail_capture("constant dither creation failed")
	clouds.set("dither_noise", dither_texture)
	clouds.call("refresh_compute")
	clouds.set("clouds_coverage", 0.75)
	clouds.set("accumulation_decay", 0.95)
	clouds.set("depth_history_rejection_enabled", true)
	clouds.set("cloud_history_compatibility_enabled", true)
	clouds.set("lightning_history_isolation_enabled", true)
	clouds.set("diagnostic_temporal_readback", true)
	clouds.set("diagnostic_menu_history_weights", false)
	clouds.set("point_lights_data", []); clouds.set("lights_updated", true)
	# Establish fresh current RGB before retaining .95 history. Eight frames
	# leave .95^8 of any older illumination; warm a bounded 96 draws instead.
	clouds.set("accumulation_decay", 0.0); clouds.call("request_history_reset")
	await drawn_frames(2, "fresh baseline")
	clouds.set("accumulation_decay", 0.95)
	await drawn_frames(96, "converged baseline")
	var warm_pair := await capture_paired_copy("baseline_warm")
	if failed: return
	var baseline_warm: Image = warm_pair.output
	clouds.set("accumulation_decay", 0.0)
	await drawn_frames(2, "synchronized baseline reference")
	var fresh_pair := await capture_paired_copy("baseline_fresh")
	if failed: return
	var baseline_fresh: Image = fresh_pair.output
	clouds.set("accumulation_decay", 0.95)
	await drawn_frames(4, "retained baseline")
	report = {"status":"RUNNING", "fixture":fixture, "history_requested":0.95, "raw_no_haze":true, "terrain_sun_neutral_for_lightning":true, "captures":[],"fixture_states":fixture_states}
	var before_pair := await capture_paired_copy("before_flash")
	if failed: return
	var baseline: Image = before_pair.output
	if failed or baseline.is_empty(): return fail_capture("raw baseline unavailable")
	baseline.save_png(ProjectSettings.globalize_path(fixture.path_join("persistent_before.png")))
	var persistent_before: Image = before_pair.persistent
	if failed or persistent_before.is_empty(): return
	persistent_before.save_png(ProjectSettings.globalize_path(fixture.path_join("history_buffer_before.png")))
	baseline_warm.save_png(ProjectSettings.globalize_path(fixture.path_join("warm_baseline.png")))
	baseline_fresh.save_png(ProjectSettings.globalize_path(fixture.path_join("no_flash_fresh_before.png")))
	if not assert_fixed_radiance(warm_pair.state,fresh_pair.state): return
	var convergence := image_delta(baseline_warm, baseline_fresh)
	var persistent_convergence := image_delta(baseline_fresh, persistent_before)
	if float(convergence.mean_rgb) > 0.002 or float(convergence.changed_fraction) > 0.01:
		return fail_capture("no-flash baseline was not stable/converged: " + JSON.stringify(convergence))
	var light_position := camera.global_position - camera.global_basis.z * 3000.0
	clouds.set("point_lights_data", [Vector4(light_position.x, light_position.y, light_position.z, 16000.0), Vector4(1.0, 0.8, 0.6, 12.0)]); clouds.set("lights_updated", true)
	await drawn_frames(3, "flash")
	var flash_pair := await capture_paired_copy("during_flash")
	var flash: Image = flash_pair.output
	var persistent_flash: Image = flash_pair.persistent
	if failed or persistent_flash.is_empty(): return
	var flash_temporal: Dictionary = clouds.call("get_temporal_history_diagnostics")
	if absf(float(flash_temporal.get("last_packed_decay",0.0)) - 0.95) > 0.00001:
		return fail_capture("flash unnecessarily discarded coherent ambient/density history")
	if failed: return
	flash.save_png(ProjectSettings.globalize_path(fixture.path_join("flash.png")))
	persistent_flash.save_png(ProjectSettings.globalize_path(fixture.path_join("history_buffer_flash.png")))
	var flash_metrics := image_delta(baseline, flash)
	if float(flash_metrics.mean_rgb) < 0.005: return fail_capture("point light did not visibly illuminate production clouds")
	clouds.set("point_lights_data", []); clouds.set("lights_updated", true)
	await drawn_frames(2, "flash removed")
	var after_pair := await capture_paired_copy("after_flash")
	var recovered: Image = after_pair.output
	var persistent_after: Image = after_pair.persistent
	if failed or persistent_after.is_empty(): return
	if failed: return
	recovered.save_png(ProjectSettings.globalize_path(fixture.path_join("persistent_after.png")))
	persistent_after.save_png(ProjectSettings.globalize_path(fixture.path_join("history_buffer_after.png")))
	clouds.set("accumulation_decay", 0.0)
	await drawn_frames(2, "synchronized no-flash reference")
	var after_fresh_pair := await capture_paired_copy("fresh_after_flash")
	if failed: return
	var recovered_fresh: Image = after_fresh_pair.output
	recovered_fresh.save_png(ProjectSettings.globalize_path(fixture.path_join("no_flash_fresh_after.png")))
	clouds.set("accumulation_decay", 0.95)
	var recovered_metrics := image_delta(baseline, recovered)
	var flash_persistent_metrics := image_delta(persistent_before, persistent_flash)
	var after_persistent_metrics := image_delta(persistent_before, persistent_after)
	var fresh_after_metrics := image_delta(recovered_fresh, persistent_after)
	var state_drift_metrics := image_delta(baseline_fresh, recovered_fresh)
	var flash_display_delta := image_delta(persistent_flash, flash)
	report.captures.append({"case":"lightning", "convergence":convergence,"persistent_convergence":persistent_convergence,
		"flash_output":flash_metrics, "flash_minus_persistent":flash_display_delta,
		"persistent_during_flash":flash_persistent_metrics,"persistent_after_flash":after_persistent_metrics,
		"persistent_vs_fresh_after":fresh_after_metrics,"fresh_scene_drift":state_drift_metrics,
		"output_after":recovered_metrics})
	for metrics in [persistent_convergence, recovered_metrics, flash_persistent_metrics, after_persistent_metrics, fresh_after_metrics]:
		if float(metrics.mean_rgb) > 0.002 or float(metrics.changed_fraction) > 0.01:
			return fail_capture("flash/persistent current-state contract failed: " + JSON.stringify(report.captures[-1]))
	if float(flash_display_delta.mean_rgb) < 0.005:
		return fail_capture("flash output lacked isolated transient RGB")
	# A real depth-writing plane crosses the cloud body. It then moves aside;
	# retained history must converge immediately to the fresh ray march.
	var occluder := MeshInstance3D.new(); var mesh := QuadMesh.new(); mesh.size = Vector2(8000,8000)
	occluder.mesh = mesh
	var material := StandardMaterial3D.new(); material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = Color(0.1,0.1,0.1); occluder.material_override = material
	game.add_child(occluder)
	occluder.global_transform = camera.global_transform
	occluder.global_position = camera.global_position - camera.global_basis.z * 6000.0
	clouds.call("request_history_reset"); await drawn_frames(8, "opaque baseline")
	occluder.global_position += camera.global_basis.x * 4500.0
	await drawn_frames(2, "opaque crossing")
	var retained := await capture_cloud_copy()
	clouds.set("accumulation_decay", 0.0); clouds.call("request_history_reset")
	await drawn_frames(2, "opaque reference")
	var reference := await capture_cloud_copy()
	var opaque_metrics := image_delta(retained, reference)
	if float(opaque_metrics.mean_rgb) > 0.01 or float(opaque_metrics.mean_alpha) > 0.01:
		return fail_capture("moving opaque edge retained incompatible cloud history: " + JSON.stringify(opaque_metrics))
	retained.save_png(ProjectSettings.globalize_path(fixture.path_join("moving_opaque_history.png")))
	reference.save_png(ProjectSettings.globalize_path(fixture.path_join("moving_opaque_fresh.png")))
	report.captures.append({"case":"moving_opaque", "delta":opaque_metrics})
	occluder.queue_free(); await process_frame
	# These fullscene poses include the real far-ring opaque depth publisher.
	if clouds.get("far_opaque_depth_texture") == null:
		return fail_capture("actual far-ring opaque depth publisher unavailable")
	for pitch in [HORIZON_PITCH, 35.0]:
		explorer.call("SetPresentationView", 12.0, pitch)
		clouds.set("accumulation_decay", 0.95); clouds.call("request_history_reset")
		await drawn_frames(4, "far ring pose")
		var history_image := await capture_cloud_copy()
		clouds.set("accumulation_decay", 0.0); clouds.call("request_history_reset")
		await drawn_frames(2, "far ring fresh")
		var fresh_image := await capture_cloud_copy()
		history_image.save_png(ProjectSettings.globalize_path(fixture.path_join("far_ring_%s_history.png" % pitch)))
		fresh_image.save_png(ProjectSettings.globalize_path(fixture.path_join("far_ring_%s_fresh.png" % pitch)))
		var metrics := image_delta(history_image, fresh_image)
		if float(metrics.mean_alpha) > 0.01 or float(metrics.mean_rgb) > 0.01:
			return fail_capture("far-ring pose history incompatible: " + JSON.stringify(metrics))
		report.captures.append({"case":"far_ring_pose", "pitch":pitch, "delta":metrics, "temporal":clouds.call("get_temporal_history_diagnostics")})
	# Small physical translations plus yaw use previous-opaque-point reprojection
	# continuously, rather than resetting history for every camera pose.
	clouds.set("accumulation_decay", 0.95); clouds.call("request_history_reset")
	await drawn_frames(4, "motion warmup")
	for step in 4:
		camera.global_position += camera.global_basis.x * 0.75
		camera.rotate_y(deg_to_rad(0.2))
		await drawn_frames(2, "continuous camera motion")
		var moving_image := await capture_cloud_copy()
		var moving_temporal: Dictionary = clouds.call("get_temporal_history_diagnostics")
		if absf(float(moving_temporal.get("last_packed_decay",0.0))-0.95) > 0.00001:
			return fail_capture("continuous camera motion reset requested .95 history")
		clouds.set("accumulation_decay", 0.0)
		await drawn_frames(2, "motion fresh reference")
		var moving_fresh := await capture_cloud_copy()
		var motion_delta := image_delta(moving_image,moving_fresh)
		if float(motion_delta.mean_rgb) > 0.01 or float(motion_delta.mean_alpha) > 0.01:
			return fail_capture("continuous camera history disagrees with current rays: "+JSON.stringify(motion_delta))
		report.captures.append({"case":"continuous_motion", "step":step, "delta":motion_delta,"temporal":moving_temporal})
		clouds.set("accumulation_decay",0.95)
	if failed: return
	report.status = "PASS"
	var file := FileAccess.open(fixture.path_join("report.json"), FileAccess.WRITE)
	file.store_string(JSON.stringify(report,"\t")); file.close()
	print("CLOUD_HISTORY_TARGETED_GAMEPLAY_GPU_TEST=PASS fixture=" + fixture)
	game.queue_free(); await process_frame; await process_frame
	quit(0)

# Hold the compositor output stationary while both images are copied. A
# separate asynchronous copy can otherwise observe the next ping-pong frame.
func capture_paired_copy(label: String) -> Dictionary:
	await RenderingServer.frame_post_draw
	var was_enabled := bool(clouds.get("enabled"))
	clouds.set("enabled", false)
	await drawn_frames(2,"capture drain "+label)
	var before := capture_fixture_state(label+"_before")
	var output := await capture_cloud_copy()
	var persistent := await capture_persistent_copy()
	var after := capture_fixture_state(label+"_after")
	if not failed: clouds.set("enabled",was_enabled)
	if int(before.callback) != int(after.callback):
		fail_capture("paired readback crossed a cloud callback: "+label)
	return {"output":output,"persistent":persistent,"state":before}

func capture_fixture_state(label: String) -> Dictionary:
	var properties := {}
	for key in ["current_time","ray_start_jitter_strength","dither_speed","extra_large_scale_clouds_position",
		"large_scale_clouds_position","medium_scale_clouds_position","detail_clouds_position",
		"cloud_ambient_color","cloud_ambient_tint","clouds_coverage","clouds_density","lighting_density",
		"cloud_frame_origin","cloud_frame_around","cloud_frame_up","cloud_frame_across",
		"cloud_weather_params","cloud_weather_frame","directional_lights_data","point_lights_data",
		"terrain_cloud_sun_profile","terrain_cloud_sun_canonical_frame","accumulation_decay"]:
		properties[key] = str(clouds.get(key))
	var bytes: PackedByteArray = clouds.get("general_data") as PackedByteArray
	var light_bytes: PackedByteArray = clouds.get("light_data") as PackedByteArray
	var ubo: Array[float] = []; var light_ubo: Array[float] = []
	for offset in range(0,bytes.size(),4): ubo.append(bytes.decode_float(offset))
	for offset in range(0,light_bytes.size(),4): light_ubo.append(light_bytes.decode_float(offset))
	var temporal: Dictionary = clouds.call("get_temporal_history_diagnostics")
	var state := {"label":label,"callback":temporal.callback_serial,"temporal":temporal,
		"properties":properties,"ubo":ubo,"light_ubo":light_ubo,
		"camera":str(camera.global_transform),"cycle_seconds":clock.call("GetCycleTimeSeconds"),
		"viewport_aa":{"taa":root.get_viewport().use_taa,"scaling_mode":root.get_viewport().scaling_3d_mode,"msaa":root.get_viewport().msaa_3d},
		"sun_world":str(game.get_node("StarLight").global_transform),"sun_energy":game.get_node("StarLight").light_energy,
		"cloud_seconds":game.get_node("RingVolumetricClouds").get("CloudSimulationTimeSeconds")}
	if not fixture_states.is_empty(): state["delta_from_previous"] = fixture_state_delta(fixture_states[-1],state)
	fixture_states.append(state)
	var file := FileAccess.open(fixture.path_join("fixture-states.json"),FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(fixture_states,"\t")); file.close()
	return state

func assert_fixed_radiance(a: Dictionary, b: Dictionary) -> bool:
	var delta := fixture_state_delta(a,b)
	var properties: Dictionary = delta.properties.duplicate()
	properties.erase("accumulation_decay")
	var unexpected_ubo: Array = []
	# The advancing dither phase is inert with jitter0. History weight and
	# ping-pong selector intentionally differ between retained/fresh captures.
	for item in delta.ubo:
		if int(item.byte) not in [224,244,248]: unexpected_ubo.append(item)
	if not properties.is_empty() or not unexpected_ubo.is_empty() or not delta.light_ubo.is_empty():
		fail_capture("fixed radiance inputs drifted: "+JSON.stringify({"properties":properties,
			"ubo":unexpected_ubo,"light_ubo":delta.light_ubo}))
		return false
	return true

func fixture_state_delta(a: Dictionary, b: Dictionary) -> Dictionary:
	var changed_properties := {}; var changed_ubo: Array = []; var changed_light: Array = []
	for key in a.properties:
		if a.properties[key] != b.properties[key]: changed_properties[key] = [a.properties[key],b.properties[key]]
	for index in mini(a.ubo.size(),b.ubo.size()):
		if a.ubo[index] != b.ubo[index]: changed_ubo.append({"byte":index*4,"before":a.ubo[index],"after":b.ubo[index]})
	for index in mini(a.light_ubo.size(),b.light_ubo.size()):
		if a.light_ubo[index] != b.light_ubo[index]: changed_light.append({"byte":index*4,"before":a.light_ubo[index],"after":b.light_ubo[index]})
	return {"properties":changed_properties,"ubo":changed_ubo,"light_ubo":changed_light,
		"callback_delta":int(b.callback)-int(a.callback)}

func capture_persistent_copy() -> Image:
	var rids: Array[RID] = clouds.get("accumulation_textures") as Array[RID]
	var index := 3 if bool(clouds.get("accumulation_is_a")) else 2
	var texture := clouds.call("get_cloud_backdrop_texture", 0) as Texture2D
	if rids.size() <= index or not rids[index].is_valid() or texture == null:
		fail_capture("persistent ping-pong buffer unavailable"); return Image.new()
	var request := raw_readback_serial; raw_readback_serial += 1
	RenderingServer.call_on_render_thread(_read_cloud_texture_on_render_thread.bind(request, rids[index], texture.get_width(), texture.get_height()))
	for ignored in 60:
		if raw_readbacks.has(request):
			var result: Dictionary = raw_readbacks[request]; raw_readbacks.erase(request)
			if not bool(result.get("ok",false)):
				fail_capture("persistent readback failed: "+str(result.get("error","unknown"))); return Image.new()
			return Image.create_from_data(int(result.width),int(result.height),false,Image.FORMAT_RGBAF,result.bytes)
		if failed or Time.get_ticks_msec() > run_deadline_msec: return Image.new()
		await RenderingServer.frame_post_draw
	fail_capture("persistent readback did not complete"); return Image.new()

func image_delta(a: Image, b: Image) -> Dictionary:
	if a.is_empty() or b.is_empty() or a.get_size() != b.get_size():
		fail_capture("incompatible raw readback images"); return {}
	var rgb_sum := 0.0; var alpha_sum := 0.0; var changed := 0; var maximum := 0.0
	var signed_r := 0.0; var signed_g := 0.0; var signed_b := 0.0
	var count := a.get_width() * a.get_height()
	for y in a.get_height():
		for x in a.get_width():
			var left := a.get_pixel(x,y); var right := b.get_pixel(x,y)
			var delta := absf(left.r-right.r)+absf(left.g-right.g)+absf(left.b-right.b)
			if not is_finite(delta): fail_capture("nonfinite raw cloud RGB"); return {}
			signed_r += right.r-left.r; signed_g += right.g-left.g; signed_b += right.b-left.b
			rgb_sum += delta; alpha_sum += absf(left.a-right.a); maximum = maxf(maximum,delta)
			if delta > 0.01: changed += 1
	return {"mean_rgb":rgb_sum/count, "mean_alpha":alpha_sum/count, "max_rgb":maximum, "changed_fraction":float(changed)/count,"signed_mean_r":signed_r/count,"signed_mean_g":signed_g/count,"signed_mean_b":signed_b/count}
