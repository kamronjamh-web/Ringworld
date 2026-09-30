extends SceneTree

# Isolated rendered regression for MenuFlyoverSunshineLayer temporal sampling.
# No menu/default resource is loaded or saved by this fixture.
const OUTPUT := "res://.godot/menu_space"
const SIZE := Vector2i(640, 360)
const RADIUS := 5_000_000.0
const RING_WIDTH := 318_000
const START_AROUND := 17_219_375.0
const SPEED := 2500.0
const DT := 1.0 / 60.0
const HIGH_WARMUP := 108
const BACKGROUND_LUMA := .108
var viewport_3d: SubViewport
var copy_viewport: SubViewport
var copy_rect: TextureRect
var world: WorldEnvironment
var adapter: Node
var settings: Resource
var watchdog: SceneTreeTimer
var sunshine_source: CompositorEffect
var gameplay_driver: Node
var gameplay_previous_phase := 0.0
var gameplay_phase := 0.0
var gameplay_previous_uploaded_time := -1.0
var failed := false
var cleanup_started := false
var user_quality := false

func _initialize() -> void:
	watchdog = create_timer(120.0)
	watchdog.timeout.connect(func() -> void: _fail("120-second watchdog"))
	call_deferred("_run")

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	user_quality = "--user-quality" in OS.get_cmdline_user_args()
	_build_world()
	if "--gameplay-history" in OS.get_cmdline_user_args():
		gameplay_driver = (load("res://addons/SunshineClouds2/SunshineCloudsDriver.gd") as Script).new()
		gameplay_driver.set("update_continuously", false)
		gameplay_driver.set("clouds_resource", adapter.get("Effect"))
		(adapter.get("Effect") as Object).set("dither_speed", _gameplay_dither_speed())
		if _gameplay_dither_speed() > 0.0:
			var bins := {}
			for serial in 128:
				var phase := fposmod(float(serial) * 0.6180339887498949 + fposmod(float(serial) / 64.0, 1.0), 1.0)
				bins[int(floor(phase * 64.0))] = true
			_require(bins.size() >= 55, "gameplay golden-ratio dither phase did not cover 55/64 slices")
		if failed: return
		if _interior_mode():
			var gameplay_interior := await _run_interior_diagnostics()
			if failed: return
			if _interior_regression(): _require_interior_regression(gameplay_interior)
			if failed: return
			print("GAMEPLAY_CLOUD_INTERIOR_GPU_DIAGNOSTICS ", JSON.stringify(gameplay_interior))
			await _cleanup(0)
			return
		if "--history-diagnostics" in OS.get_cmdline_user_args():
			var gameplay_diagnostics := await _run_gameplay_history_diagnostics()
			if failed: return
			print("GAMEPLAY_CLOUD_HISTORY_GPU_DIAGNOSTICS ", JSON.stringify(gameplay_diagnostics))
			await _cleanup(0)
			return
		var gameplay_static := await _compare_history("gameplay_static", false)
		if failed: return
		var gameplay_moving := await _compare_history("gameplay_moving", true)
		if failed: return
		var far_depth := await _test_synthetic_far_depth_consumer()
		if failed: return
		_require(gameplay_static.high.color < gameplay_static.raw.color * .80 and gameplay_static.high.alpha < gameplay_static.raw.alpha * .80, "gameplay static high history did not converge")
		if failed: return
		_require(gameplay_moving.high.color < gameplay_moving.raw.color * .90 and gameplay_moving.high.alpha < gameplay_moving.raw.alpha * .90, "gameplay moving high history did not converge")
		if failed: return
		print("GAMEPLAY_CLOUD_HISTORY_GPU_TEST=PASS ", JSON.stringify({"static":gameplay_static,"moving":gameplay_moving,"far_depth":far_depth}))
		await _cleanup(0)
		return
	if _interior_mode():
		var interior := await _run_interior_diagnostics()
		if failed: return
		if _interior_regression(): _require_interior_regression(interior)
		if failed: return
		print("MENU_CLOUD_INTERIOR_GPU_DIAGNOSTICS ", JSON.stringify(interior))
		await _cleanup(0)
		return
	if "--history-diagnostics" in OS.get_cmdline_user_args():
		var diagnostics := await _run_history_diagnostics()
		if not failed:
			print("MENU_CLOUD_HISTORY_GPU_DIAGNOSTICS ", JSON.stringify(diagnostics))
			await _cleanup(0)
		return
	var sampler := await _test_callback_sampling()
	if failed: return
	var dither := await _test_dither_control()
	if failed: return
	var callback_state := await _test_callback_state_resets()
	if failed: return
	var static_result := await _compare_history("static", false)
	if failed: return
	_require(float(static_result.get("true_static", 0.0)) > .5, "settled paused history never asserted true-static confidence")
	if failed: return
	var moving_result := await _compare_history("moving", true)
	if failed: return
	_require(static_result.high.color < static_result.raw.color * .80 and static_result.high.alpha < static_result.raw.alpha * .80, "static high history did not materially reduce color/alpha error")
	if failed: return
	_require(moving_result.high.color < moving_result.raw.color * .80 and moving_result.high.alpha < moving_result.raw.alpha * .80, "moving high history did not materially reduce matched-pose color/alpha error")
	if failed: return
	print("MENU_CLOUD_HISTORY_GPU_TEST=PASS ", JSON.stringify({"sampler": sampler, "dither": dither, "callback_state": callback_state, "static": static_result, "moving": moving_result, "user_quality": user_quality}))
	await _cleanup(0)

func _interior_mode() -> bool:
	return "--interior-diagnostics" in OS.get_cmdline_user_args() or "--interior-regression" in OS.get_cmdline_user_args()

func _interior_regression() -> bool:
	return "--interior-regression" in OS.get_cmdline_user_args()

func _gameplay_dither_speed() -> float:
	return 0.0 if "--dither-speed-zero" in OS.get_cmdline_user_args() else 60.0

func _build_world() -> void:
	viewport_3d = SubViewport.new()
	viewport_3d.own_world_3d = true
	var render_size := Vector2i(2560, 1440) if "--render-1440" in OS.get_cmdline_user_args() else SIZE
	viewport_3d.size = render_size
	viewport_3d.msaa_3d = Viewport.MSAA_4X
	viewport_3d.render_target_update_mode = SubViewport.UPDATE_DISABLED
	root.add_child(viewport_3d)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(.08, .11, .17)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_energy = .35
	world = WorldEnvironment.new()
	world.environment = environment
	viewport_3d.add_child(world)
	var camera := Camera3D.new()
	camera.current = true
	camera.near = 50.0
	camera.far = 12_000_000.0
	camera.fov = 65.0
	camera.position = Vector3(0, 20_112, 0)
	camera.rotation_degrees = Vector3(-8, 0, 0)
	viewport_3d.add_child(camera)
	var sun := Vector3(-.4, .75, -.5).normalized()
	var light := DirectionalLight3D.new()
	light.light_energy = 2.15
	viewport_3d.add_child(light)
	light.look_at(-sun, Vector3.UP)
	var ground := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(600_000, 600_000)
	ground.mesh = plane
	var material := StandardMaterial3D.new()
	material.albedo_color = Color(.35, .35, .35)
	ground.material_override = material
	viewport_3d.add_child(ground)
	settings = (load("res://scripts/ui/MenuFlyoverSettings.cs") as Script).new()
	sunshine_source = load("res://resources/clouds/sunshine_ring_local.tres") as CompositorEffect
	_reset_adapter()
	copy_viewport = SubViewport.new()
	copy_viewport.disable_3d = true
	copy_viewport.transparent_bg = true
	# Preserve HDR cloud output only for the opt-in diagnostic capture. Existing
	# regression metrics retain their historical RGBA8 capture path.
	copy_viewport.use_hdr_2d = _interior_mode()
	copy_viewport.size = render_size
	copy_viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
	root.add_child(copy_viewport)
	copy_rect = TextureRect.new()
	copy_rect.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	var shader := Shader.new()
	shader.code = "shader_type canvas_item; render_mode blend_disabled; void fragment(){ COLOR = texture(TEXTURE, UV); }"
	var copy_material := ShaderMaterial.new()
	copy_material.shader = shader
	copy_rect.material = copy_material
	copy_viewport.add_child(copy_rect)

func _reset_adapter() -> void:
	if adapter == null:
		adapter = (load("res://scripts/ui/MenuFlyoverSunshineLayer.cs") as Script).new()
		root.add_child(adapter)
	else:
		if copy_rect != null:
			copy_rect.texture = null
		adapter.call("Detach")
	adapter.call("Configure", world, 1337, RADIUS, RING_WIDTH, Vector3(-.4, .75, -.5).normalized())
	if "--gameplay-history" in OS.get_cmdline_user_args():
		gameplay_phase = 0.0; gameplay_previous_phase = -1.0; gameplay_previous_uploaded_time = -1.0

func _apply(history: float, dither_speed: float) -> void:
	var values := {
		"CloudsEnabled": true, "CloudShadowsEnabled": false, "ShadowGenerationEnabled": false,
		"AltitudeKm": 20.0, "PitchDegrees": 8.0, "FieldOfView": 65.0,
		"CloudFloorKm": 2.0, "CloudCeilingKm": 8.0, "CloudCoverage": .71,
		"CloudDensity": 3.0, "CloudAmbientStrength": 2.0, "CloudSharpness": 1.0,
		"CloudResolution": 1, "CloudViewSteps": 256, "CloudMinStepMeters": 10.0,
		"CloudMaxStepMeters": 4000.0 if user_quality else 1600.0, "CloudLodBias": 2.0 if user_quality else .1, "CloudLightingSteps": 8 if user_quality else 5, "CloudRayStartJitterStrength": 1.0,
		"CloudBlurPower": 0.0, "CloudDriftMps": 0.0, "CloudDitherSpeed": dither_speed,
		"CloudStationaryHistoryWeight": history, "CloudMovingHistoryWeight": history
	}
	for key: String in values:
		settings.set(key, values[key])
	adapter.call("ApplyTuning", settings, Vector3(-.4, .75, -.5).normalized())

func _test_callback_sampling() -> Dictionary:
	_reset_adapter()
	_apply(0.0, 0.0)
	var effect: Object = adapter.get("Effect") as Object
	adapter.call("UpdateFrame", START_AROUND, 10.0, true)
	var serial := int(effect.get("render_callback_serial"))
	var phase := float(effect.get("last_menu_sample_phase"))
	for ignored in range(5):
		adapter.call("UpdateFrame", START_AROUND, 10.0, true)
	_require(int(effect.get("render_callback_serial")) == serial and is_equal_approx(float(effect.get("last_menu_sample_phase")), phase), "CPU-only updates changed callback phase")
	if failed: return {}
	var first := await _frame(START_AROUND, 10.0, true)
	var controls_first := _controls(effect)
	var second := await _frame(START_AROUND, 10.0, true)
	var controls_second := _controls(effect)
	var changed := _image_error(first, second)
	_require(bool(effect.get("menu_temporal_sampling")), "adapter did not enable menu temporal sampling")
	if failed: return {}
	_require(is_equal_approx(controls_first.enabled, 1.0) and not is_equal_approx(controls_first.phase, controls_second.phase), "real paused callbacks did not advance UBO phase")
	if failed: return {}
	_require(changed.color > .000001 or changed.alpha > .000001, "real paused callbacks did not change raw sample")
	if failed: return {}
	# This delta is below the float ULP near the canonical coordinate. The precise
	# adapter metadata must still reach the callback-relative reprojection UBO.
	_apply(.95, 0.0)
	await _frame(START_AROUND, 11.0, false, false)
	await _frame(START_AROUND + .005, 11.0 + DT, false, false)
	var tiny_motion := _controls(effect)
	_require(absf(float(tiny_motion.get("arc_minus_bulk", 0.0))) > .001 and float(tiny_motion.get("true_static", 1.0)) < .5, "precise sub-ULP arc movement was not preserved")
	if failed: return {}
	return {"serial_before": serial, "phase_before": phase, "controls_first": controls_first, "controls_second": controls_second, "tiny_motion": tiny_motion, "raw_change": changed}

func _test_dither_control() -> Dictionary:
	_reset_adapter()
	_apply(0.0, 0.0)
	var effect: Object = adapter.get("Effect") as Object
	await _frame(START_AROUND, 20.0, true)
	var time_zero_a := _ubo_time(effect)
	var wind_zero: Vector3 = effect.get("large_scale_clouds_position") as Vector3
	await _frame(START_AROUND, 20.0 + DT, true)
	var time_zero_b := _ubo_time(effect)
	_reset_adapter()
	_apply(0.0, 60.0)
	effect = adapter.get("Effect") as Object
	await _frame(START_AROUND, 20.0, true)
	var time_sixty_a := _ubo_time(effect)
	var wind_sixty: Vector3 = effect.get("large_scale_clouds_position") as Vector3
	await _frame(START_AROUND, 20.0 + DT, true)
	var time_sixty_b := _ubo_time(effect)
	_require(wind_zero.is_equal_approx(wind_sixty), "dither speed changed zero-drift wind")
	if failed: return {}
	_require(not is_equal_approx(time_zero_b - time_zero_a, time_sixty_b - time_sixty_a), "dither speed did not independently alter UBO time trajectory")
	if failed: return {}
	return {"zero_time": [time_zero_a, time_zero_b], "sixty_time": [time_sixty_a, time_sixty_b], "wind": wind_zero}

func _test_callback_state_resets() -> Dictionary:
	_reset_adapter()
	_apply(.95, 0.0)
	var effect: Object = adapter.get("Effect") as Object
	await _frame(START_AROUND, 30.0, false, false)
	# Several CPU updates must not replace the prior callback pose; only their
	# net movement belongs in the next callback-relative history delta.
	for delta in [.001, .002, .003, .004]:
		adapter.call("UpdateFrame", START_AROUND + delta, 30.0, false)
	await _frame(START_AROUND + .005, 30.0 + DT, false, false)
	var net_motion := _controls(effect)
	_require(absf(float(net_motion.get("arc_minus_bulk", 0.0)) - .005) < .000001, "CPU updates replaced previous callback pose")
	if failed: return {}
	for frame in range(4):
		await _frame(START_AROUND, 31.0 + frame * DT, true, false)
	await _frame(START_AROUND + 50_000.0, 32.0, false, false)
	var teleport_decay := _packed_decay(effect)
	_require(is_zero_approx(teleport_decay) and bool(effect.get("last_menu_history_discontinuity")), "route teleport retained unrelated cloud history")
	if failed: return {}
	await _frame(START_AROUND + 50_000.0, 32.0 + DT, true, false)
	_require(is_equal_approx(_packed_decay(effect), .95) and not bool(effect.get("last_menu_history_discontinuity")), "history did not resume after route teleport")
	if failed: return {}
	# Wind metadata must follow the physical 30 m/s vector, and never claim a
	# true-static history when its bulk/detail anchors advance.
	settings.set("CloudDriftMps", 30.0)
	adapter.call("ApplyTuning", settings, Vector3(-.4, .75, -.5).normalized())
	await _frame(START_AROUND, 40.0, false, false)
	var before: Vector3 = effect.get("large_scale_clouds_position") as Vector3
	await _frame(START_AROUND, 40.0 + DT, false, false)
	var after: Vector3 = effect.get("large_scale_clouds_position") as Vector3
	var wind_controls := _controls(effect)
	_require(absf((after.x - before.x) - .5) < .01 and absf((after.z - before.z) - .175) < .01 and float(wind_controls.get("true_static", 1.0)) < .5, "wind callback metadata/static confidence mismatch")
	if failed: return {}
	# Both an explicit enable toggle and an empty-cloud appearance update must
	# hold packed history at zero while the adapter observes reset callbacks.
	settings.set("CloudDriftMps", 0.0)
	settings.set("CloudsEnabled", false)
	adapter.call("ApplyTuning", settings, Vector3(-.4, .75, -.5).normalized())
	var off_serial := int(effect.get("render_callback_serial"))
	await _frame(START_AROUND, 50.0, true, false)
	# Disabled effects do not upload any UBO. Check requested reset and lack of
	# callbacks here; only resumed rendering can publish the packed zero weight.
	var off_decay := float(effect.get("accumulation_decay"))
	_require(int(effect.get("render_callback_serial")) == off_serial, "disabled clouds still rendered")
	if failed: return {}
	settings.set("CloudsEnabled", true)
	adapter.call("ApplyTuning", settings, Vector3(-.4, .75, -.5).normalized())
	await _frame(START_AROUND, 50.0 + DT, true, false)
	var resume_decay := _packed_decay(effect)
	settings.set("CloudCoverage", 0.0)
	settings.set("CloudDensity", 0.0)
	adapter.call("ApplyTuning", settings, Vector3(-.4, .75, -.5).normalized())
	var empty := Image.new()
	var empty_decay := NAN
	for frame in range(3):
		empty = await _frame(START_AROUND, 51.0 + frame * DT, true, frame == 2)
		if frame == 0:
			empty_decay = _packed_decay(effect)
	print("MENU_CLOUD_HISTORY_RESET_METRICS ", {"off_decay": off_decay, "resume_decay": resume_decay, "empty_decay": empty_decay, "empty_alpha": _mean_alpha(empty)})
	_require(is_zero_approx(off_decay) and is_zero_approx(resume_decay) and is_zero_approx(empty_decay) and _mean_alpha(empty) < .02, "enable/empty-cloud reset retained temporal residue")
	if failed: return {}
	return {"net_motion": net_motion, "teleport_decay": teleport_decay, "wind_delta": after - before, "wind_controls": wind_controls, "off_decay": off_decay, "resume_decay": resume_decay, "empty_decay": empty_decay, "empty_alpha": _mean_alpha(empty)}

func _run_history_diagnostics() -> Dictionary:
	_reset_adapter()
	_apply(.95, 0.0)
	var effect: Object = adapter.get("Effect") as Object
	var time := 80.0
	var around := START_AROUND
	for frame in range(HIGH_WARMUP):
		await _frame(around, time, false, false)
		time += DT
		around += SPEED * DT
	effect.set("diagnostic_menu_history_weights", true)
	var counts := PackedInt32Array([0, 0, 0, 0])
	var opaque_counts := PackedInt32Array([0, 0, 0, 0])
	var used_sum := 0.0
	var mismatch_sum := 0.0
	var pixels := 0
	var opaque_pixels := 0
	var last := Image.new()
	for frame in range(6):
		last = await _frame(around, time, false, true)
		var packed: PackedByteArray = effect.get("general_data") as PackedByteArray
		_require(packed.size() == 880 and is_equal_approx(packed.decode_float(716), 1.0), "history diagnostic flag did not reach the GPU")
		if failed: return {}
		var sampled := _aggregate_history_diagnostics(last, counts, opaque_counts)
		used_sum += float(sampled.used)
		mismatch_sum += float(sampled.mismatch)
		pixels += int(sampled.pixels)
		opaque_pixels += int(sampled.opaque_pixels)
		time += DT
		around += SPEED * DT
	var suffix := "_user_quality" if user_quality else ""
	last.save_png("%s/menu_cloud_history_moving_diagnostics%s.png" % [OUTPUT, suffix])
	effect.set("diagnostic_menu_history_weights", false)
	return {"frames": 6, "pixels": pixels, "raw_alpha_gt_005_pixels": opaque_pixels, "reason_counts": counts, "reason_counts_raw_alpha_gt_005": opaque_counts, "mean_used_history": used_sum / max(1, pixels), "mean_alpha_mismatch": mismatch_sum / max(1, pixels), "last_time": time, "last_around": around}

func _run_gameplay_history_diagnostics() -> Dictionary:
	var result := {}
	for moving in [false, true]:
		_reset_adapter(); _apply(.95, 60.0)
		var effect: Object = adapter.get("Effect") as Object
		var counts := PackedInt32Array([0,0,0,0]); var opaque := PackedInt32Array([0,0,0,0])
		var used := 0.0; var mismatch := 0.0; var pixels := 0; var opaque_pixels := 0; var around := START_AROUND; var time := 110.0
		for warmup in HIGH_WARMUP:
			await _frame(around,time,not moving,false); if failed: return {}
			time += DT; if moving: around += 60.0 * DT
		effect.set("diagnostic_menu_history_weights",true)
		var last := Image.new()
		for frame in 8:
			last=await _frame(around,time,not moving,true); if failed: return {}
			var sample:=_aggregate_history_diagnostics(last,counts,opaque); used+=float(sample.used); mismatch+=float(sample.mismatch); pixels+=int(sample.pixels); opaque_pixels+=int(sample.opaque_pixels)
			time+=DT; if moving: around+=60.0*DT
		last.save_png("%s/gameplay_history_diagnostics_%s.png" % [OUTPUT,"moving" if moving else "static"])
		effect.set("diagnostic_menu_history_weights",false)
		result["moving" if moving else "static"]={"frames":8,"pixels":pixels,"opaque_pixels":opaque_pixels,"reason_counts":counts,"opaque_reason_counts":opaque,"mean_used_history":used/max(1,pixels),"mean_alpha_mismatch":mismatch/max(1,pixels)}
	return result

func _aggregate_history_diagnostics(image: Image, counts: PackedInt32Array, opaque_counts: PackedInt32Array) -> Dictionary:
	var used := 0.0
	var mismatch := 0.0
	var pixels := 0
	var opaque_pixels := 0
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			# The non-HDR canvas copy writes the compute channels unchanged; these
			# are numeric diagnostics, not an sRGB image requiring color conversion.
			var pixel := image.get_pixel(x, y)
			var linear := pixel
			var bucket := clampi(int(floor(linear.g * 4.0 + .5)), 0, 3)
			counts[bucket] += 1
			used += linear.r
			mismatch += linear.b
			pixels += 1
			if pixel.a > .05:
				opaque_counts[bucket] += 1
				opaque_pixels += 1
	return {"used": used, "mismatch": mismatch, "pixels": pixels, "opaque_pixels": opaque_pixels}

func _compare_history(label: String, moving: bool) -> Dictionary:
	var travel_speed := 60.0 if "--gameplay-history" in OS.get_cmdline_user_args() else SPEED
	var final_time := 50.0 + DT * (HIGH_WARMUP - 1)
	var final_around := START_AROUND + (travel_speed * DT * (HIGH_WARMUP - 1) if moving else 0.0)
	# Raw reference at the exact pose/time at which the high-history run ends.
	_reset_adapter()
	_apply(0.0, 0.0)
	var reference := PackedFloat32Array()
	var raw_final := Image.new()
	var phase_matched_raw := Image.new()
	for sample in range(64):
		raw_final = await _frame(final_around, final_time, true)
		if failed: return {}
		_accumulate(reference, raw_final)
		if sample == 43:
			phase_matched_raw = raw_final.duplicate()
	# Reconfigure before the high run: it cannot inherit raw history or a stale
	# reprojection state. It follows the same path and ends at the reference pose.
	_reset_adapter()
	_apply(.95, 0.0)
	var high_final := Image.new()
	var time := 50.0
	var around := START_AROUND
	for frame in range(HIGH_WARMUP):
		high_final = await _frame(around, time, not moving, frame == HIGH_WARMUP - 1)
		if failed: return {}
		time += DT
		if moving:
			around += travel_speed * DT
	var suffix := "_user_quality" if user_quality else ""
	phase_matched_raw.save_png("%s/menu_cloud_history_%s_raw%s.png" % [OUTPUT, label, suffix])
	high_final.save_png("%s/menu_cloud_history_%s_high%s.png" % [OUTPUT, label, suffix])
	var effect: Object = adapter.get("Effect") as Object
	var result := {"raw": _error_to_reference(phase_matched_raw, reference, 64), "high": _error_to_reference(high_final, reference, 64), "final_time": final_time, "final_around": final_around, "true_static": _controls(effect).get("true_static", 0.0)}
	print("MENU_CLOUD_HISTORY_GPU_METRICS ", label, " ", JSON.stringify(result))
	return result

# This is intentionally a diagnostic rather than a pass/fail regression: a
# thin cloud framing can leave too few alpha>=.85 samples to judge interiors.
func _run_interior_diagnostics() -> Dictionary:
	var results := {}
	for moving in [false, true]:
		var label := "moving" if moving else "static"
		results[label] = await _interior_diagnostic_case(label, moving)
		if failed: return {}
	return {"capture_hdr_2d": copy_viewport.use_hdr_2d, "capture_format": str(copy_viewport.get_texture().get_image().get_format()), "sample_stride": 12, "minimum_meaningful_region_count": 16, "static_wind": true, "note": "RGBA8 reference capture clips HDR color/alpha before averaging; this mode uses HDR 2D capture and reports raw values above one.", "cases": results}

func _require_interior_regression(result: Dictionary) -> void:
	for label: String in ["static", "moving"]:
		var case: Dictionary = result.get("cases", {}).get(label, {})
		_require(not case.is_empty(), "interior regression omitted " + label + " case")
		if failed: return
		for region: String in ["interior", "edge"]:
			var raw: Dictionary = case.get("raw", {}).get(region, {})
			var high: Dictionary = case.get("high", {}).get(region, {})
			_require(int(raw.get("count", 0)) >= 16 and int(high.get("count", 0)) >= 16, "interior regression has too few " + region + " samples for " + label)
			if failed: return
			_require(float(high.get("luma", INF)) <= float(raw.get("luma", 0.0)) * .25, "high history did not reduce " + region + " luma error to 25% for " + label)
			if failed: return
		# The series is fixed-pose after the moving path's endpoint; it measures
		# stationary temporal variation, not variation during continuous travel.
		for region: String in ["interior", "edge"]:
			var raw_variation: Dictionary = case.get("raw_frame_to_frame_luma_rms", {}).get(region, {})
			var high_variation: Dictionary = case.get("high_frame_to_frame_luma_rms", {}).get(region, {})
			_require(int(raw_variation.get("count", 0)) >= 16 and int(high_variation.get("count", 0)) >= 16, "interior regression has too few stationary-series " + region + " samples for " + label)
			if failed: return
			_require(float(high_variation.get("luma", INF)) <= float(raw_variation.get("luma", 0.0)) * .20, "high history did not reduce stationary " + region + " variation to 20% for " + label)
			if failed: return
		_require(float(case.get("raw_hdr_range", {}).get("max_raw_alpha", INF)) <= 1.001, "interior regression raw alpha exceeded 1 for " + label)
		if failed: return

func _interior_diagnostic_case(label: String, moving: bool) -> Dictionary:
	var travel_speed := 60.0 if "--gameplay-history" in OS.get_cmdline_user_args() else SPEED
	var final_time := 50.0 + DT * (HIGH_WARMUP - 1)
	var final_around := START_AROUND + (travel_speed * DT * (HIGH_WARMUP - 1) if moving else 0.0)
	_reset_adapter(); _apply(0.0, 0.0)
	var reference := PackedFloat32Array()
	var raw_series: Array[Image] = []
	for sample in range(64):
		var image := await _frame(final_around, final_time, true)
		if failed: return {}
		_require_hdr_diagnostic_image(image)
		if failed: return {}
		_accumulate_region_reference(reference, image)
		if sample >= 56:
			raw_series.append(image.duplicate())
	_reset_adapter(); _apply(.95, 0.0)
	var time := 50.0
	var around := START_AROUND
	for frame in range(HIGH_WARMUP):
		await _frame(around, time, not moving, false)
		if failed: return {}
		time += DT
		if moving: around += travel_speed * DT
	var high_series: Array[Image] = []
	for frame in range(8):
		high_series.append(await _frame(final_around, final_time, true))
		if failed: return {}
		_require_hdr_diagnostic_image(high_series.back())
		if failed: return {}
	var raw := _region_error_to_reference(raw_series[0], reference, 64)
	var high := _region_error_to_reference(high_series[7], reference, 64)
	var raw_variation := _region_luma_variation(raw_series, reference, 64)
	var high_variation := _region_luma_variation(high_series, reference, 64)
	var raw_hdr := _raw_hdr_range(raw_series)
	var high_hdr := _raw_hdr_range(high_series)
	high_series[7].save_png("%s/menu_cloud_history_%s_interior_high_hdr.png" % [OUTPUT, label])
	return {"reference_samples": 64, "series_frames": 8, "final_time": final_time, "final_around": final_around, "raw": raw, "high": high, "raw_frame_to_frame_luma_rms": raw_variation, "high_frame_to_frame_luma_rms": high_variation, "raw_hdr_range": raw_hdr, "high_hdr_range": high_hdr}

func _require_hdr_diagnostic_image(image: Image) -> void:
	_require(not image.is_empty() and image.get_width() > 0 and image.get_height() > 0, "interior diagnostic capture was empty")
	if failed: return
	_require(image.get_format() == Image.FORMAT_RGBAH or image.get_format() == Image.FORMAT_RGBAF, "interior diagnostic capture was not float/half HDR: " + str(image.get_format()))
	if failed: return
	for y in range(4, image.get_height(), 12):
		for x in range(4, image.get_width(), 12):
			var pixel := image.get_pixel(x, y)
			_require(is_finite(pixel.r) and is_finite(pixel.g) and is_finite(pixel.b) and is_finite(pixel.a), "interior diagnostic capture contained non-finite pixels")
			if failed: return

# Per sampled pixel: composited linear RGB followed by display alpha. Keeping
# RGB as well as luma distinguishes a stable brightness from a stable color.
func _accumulate_region_reference(total: PackedFloat32Array, image: Image) -> void:
	var index := 0
	for y in range(4, image.get_height(), 12):
		for x in range(4, image.get_width(), 12):
			var pixel := image.get_pixel(x, y)
			var alpha := clampf(pixel.a, 0.0, 1.0)
			if total.size() <= index + 3: total.resize(index + 4)
			total[index] += pixel.r * alpha + BACKGROUND_LUMA * (1.0 - alpha)
			total[index + 1] += pixel.g * alpha + BACKGROUND_LUMA * (1.0 - alpha)
			total[index + 2] += pixel.b * alpha + BACKGROUND_LUMA * (1.0 - alpha)
			total[index + 3] += alpha
			index += 4

func _region_error_to_reference(image: Image, reference: PackedFloat32Array, samples: int) -> Dictionary:
	var sums := {"interior": {"rgb": 0.0, "luma": 0.0, "alpha": 0.0, "count": 0}, "edge": {"rgb": 0.0, "luma": 0.0, "alpha": 0.0, "count": 0}}
	var index := 0
	for y in range(4, image.get_height(), 12):
		for x in range(4, image.get_width(), 12):
			var mean_alpha := reference[index + 3] / samples
			var region := "interior" if mean_alpha >= .85 else "edge" if mean_alpha > .1 else ""
			if not region.is_empty():
				var pixel := image.get_pixel(x, y); var alpha := clampf(pixel.a, 0.0, 1.0)
				var rgb := Vector3(pixel.r * alpha + BACKGROUND_LUMA * (1.0 - alpha), pixel.g * alpha + BACKGROUND_LUMA * (1.0 - alpha), pixel.b * alpha + BACKGROUND_LUMA * (1.0 - alpha))
				var target := Vector3(reference[index] / samples, reference[index + 1] / samples, reference[index + 2] / samples)
				var delta := rgb - target; var entry: Dictionary = sums[region]
				entry.rgb += delta.length_squared() / 3.0
				entry.luma += pow(rgb.dot(Vector3(.2126, .7152, .0722)) - target.dot(Vector3(.2126, .7152, .0722)), 2)
				entry.alpha += pow(alpha - mean_alpha, 2); entry.count += 1
			index += 4
	for region: String in sums:
		var entry: Dictionary = sums[region]; var count := int(entry.count)
		entry.rgb = sqrt(float(entry.rgb) / max(1, count)); entry.luma = sqrt(float(entry.luma) / max(1, count)); entry.alpha = sqrt(float(entry.alpha) / max(1, count)); entry.meaningful = count >= 16
	return sums

func _region_luma_variation(series: Array[Image], reference: PackedFloat32Array, samples: int) -> Dictionary:
	var sums := {"interior": {"luma": 0.0, "count": 0}, "edge": {"luma": 0.0, "count": 0}}
	for frame in range(1, series.size()):
		var index := 0
		for y in range(4, series[frame].get_height(), 12):
			for x in range(4, series[frame].get_width(), 12):
				var mean_alpha := reference[index + 3] / samples; var region := "interior" if mean_alpha >= .85 else "edge" if mean_alpha > .1 else ""
				if not region.is_empty():
					var a := series[frame - 1].get_pixel(x, y); var b := series[frame].get_pixel(x, y)
					var al := clampf(a.a, 0.0, 1.0); var bl := clampf(b.a, 0.0, 1.0)
					var la := (a.r * .2126 + a.g * .7152 + a.b * .0722) * al + BACKGROUND_LUMA * (1.0 - al)
					var lb := (b.r * .2126 + b.g * .7152 + b.b * .0722) * bl + BACKGROUND_LUMA * (1.0 - bl)
					var entry: Dictionary = sums[region]; entry.luma += pow(lb - la, 2); entry.count += 1
				index += 4
	for region: String in sums:
		var entry: Dictionary = sums[region]; entry.luma = sqrt(float(entry.luma) / max(1, int(entry.count))); entry.meaningful = int(entry.count) >= 16
	return sums

func _raw_hdr_range(images: Array[Image]) -> Dictionary:
	var max_alpha := -INF; var max_color := -INF; var alpha_over_one := 0; var color_over_one := 0; var pixels := 0
	for image in images:
		for y in range(4, image.get_height(), 12):
			for x in range(4, image.get_width(), 12):
				var pixel := image.get_pixel(x, y); max_alpha = maxf(max_alpha, pixel.a); max_color = maxf(max_color, maxf(pixel.r, maxf(pixel.g, pixel.b)))
				if pixel.a > 1.0: alpha_over_one += 1
				if maxf(pixel.r, maxf(pixel.g, pixel.b)) > 1.0: color_over_one += 1
				pixels += 1
	return {"sampled_pixels": pixels, "max_raw_alpha": max_alpha, "max_raw_color": max_color, "raw_alpha_gt_one_count": alpha_over_one, "raw_color_gt_one_count": color_over_one}

func _frame(around: float, time: float, frozen: bool, capture: bool = true) -> Image:
	if failed: return Image.new()
	adapter.call("UpdateFrame", around, time, frozen)
	if "--gameplay-history" in OS.get_cmdline_user_args():
		var gameplay_effect: Object = adapter.get("Effect") as Object
		gameplay_effect.set("menu_temporal_sampling", false)
		gameplay_effect.set("menu_fast_reconstruction", false)
		gameplay_effect.set("dither_speed", _gameplay_dither_speed())
		gameplay_effect.set("current_time", gameplay_phase)
		gameplay_driver.set("clouds_resource", gameplay_effect)
		gameplay_driver.call("_process", DT)
		var phase := float(gameplay_effect.get("current_time"))
		if _gameplay_dither_speed() > 0.0:
			_require(!is_equal_approx(phase, gameplay_previous_phase), "production driver dither phase did not advance")
		if failed: return Image.new()
		gameplay_previous_phase = phase
		gameplay_phase = phase
	viewport_3d.render_target_update_mode = SubViewport.UPDATE_ONCE
	await RenderingServer.frame_post_draw
	if failed: return Image.new()
	if "--gameplay-history" in OS.get_cmdline_user_args():
		_require(is_zero_approx(float(_controls(adapter.get("Effect") as Object).get("enabled", 1.0))), "gameplay UBO still enabled menu temporal sampling")
		if failed: return Image.new()
		var uploaded_time := _ubo_time(adapter.get("Effect") as Object)
		_require(!is_equal_approx(uploaded_time, gameplay_previous_uploaded_time), "gameplay dither phase did not reach uploaded UBO time")
		if failed: return Image.new()
		gameplay_previous_uploaded_time = uploaded_time
	if not capture:
		return Image.new()
	var cloud := adapter.call("GetCloudTexture") as Texture2D
	if cloud == null:
		_fail("cloud texture was unavailable")
		return Image.new()
	copy_rect.texture = cloud
	copy_viewport.render_target_update_mode = SubViewport.UPDATE_ONCE
	await RenderingServer.frame_post_draw
	if failed: return Image.new()
	return copy_viewport.get_texture().get_image()

func _test_synthetic_far_depth_consumer() -> Dictionary:
	_reset_adapter(); _apply(0.0, 60.0)
	var baseline := await _frame(START_AROUND, 91.0, true)
	if failed: return {}
	var effect: Object = adapter.get("Effect") as Object
	var field := Image.create(SIZE.x, SIZE.y, false, Image.FORMAT_RF)
	field.fill(Color(0,0,0,1)); field.fill_rect(Rect2i(0,0,SIZE.x/2,SIZE.y), Color(80,0,0,1))
	var texture := ImageTexture.create_from_image(field)
	effect.set("far_opaque_depth_texture", texture); effect.call("refresh_compute")
	var clipped := Image.new()
	for frame in 10:
		clipped = await _frame(START_AROUND, 91.0 + frame * DT, true)
		if failed: return {}
	baseline.save_png("%s/gameplay_far_depth_baseline.png" % OUTPUT); clipped.save_png("%s/gameplay_far_depth_clipped.png" % OUTPUT)
	var baseline_left := _half_alpha(baseline, true); var clipped_left := _half_alpha(clipped, true); var clipped_right := _half_alpha(clipped, false)
	_require(baseline_left > .05, "synthetic far-depth baseline had no left cloud")
	_require(clipped_left < .03, "synthetic far depth did not clip left cloud")
	_require(clipped_right > .05, "synthetic far depth erased right cloud")
	if failed: return {}
	var post_clipped := viewport_3d.get_texture().get_image()
	effect.set("enabled", false); viewport_3d.render_target_update_mode=SubViewport.UPDATE_ONCE; await RenderingServer.frame_post_draw
	if failed: return {}
	var post_without_clouds := viewport_3d.get_texture().get_image(); effect.set("enabled", true)
	var post_left_error := _half_luma_error(post_clipped, post_without_clouds, true)
	_require(post_left_error < .03, "final post pixels retained clouds behind synthetic far depth")
	if failed: return {}
	# Keep the ImageTexture alive through all submitted captures, then detach it.
	effect.set("far_opaque_depth_texture", null); effect.call("refresh_compute")
	return {"baseline_left_alpha":baseline_left,"clipped_left_alpha":clipped_left,"clipped_right_alpha":clipped_right,"post_left_error":post_left_error}


func _half_alpha(image: Image, left: bool) -> float:
	var total := 0.0; var count := 0; var begin := 0 if left else image.get_width()/2; var end := image.get_width()/2 if left else image.get_width()
	for y in range(4,image.get_height(),8):
		for x in range(begin+4,end,8): total += image.get_pixel(x,y).a; count += 1
	return total/max(1,count)

func _half_luma_error(a:Image,b:Image,left:bool)->float:
	var total:=0.; var count:=0; var begin:=0 if left else a.get_width()/2; var end:=a.get_width()/2 if left else a.get_width()
	for y in range(4,a.get_height(),8):
		for x in range(begin+4,end,8): total+=absf(a.get_pixel(x,y).get_luminance()-b.get_pixel(x,y).get_luminance()); count+=1
	return total/max(1,count)

func _controls(effect: Object) -> Dictionary:
	var bytes: PackedByteArray = effect.get("general_data") as PackedByteArray
	if bytes.size() < 880:
		return {}
	return {"fast": bytes.decode_float(688), "enabled": bytes.decode_float(692), "arc_minus_bulk": bytes.decode_float(696), "negative_bulk_z": bytes.decode_float(700), "phase": bytes.decode_float(704), "bulk_x": bytes.decode_float(708), "true_static": bytes.decode_float(712)}

func _ubo_time(effect: Object) -> float:
	var bytes: PackedByteArray = effect.get("general_data") as PackedByteArray
	return bytes.decode_float(224) if bytes.size() >= 228 else NAN

func _packed_decay(effect: Object) -> float:
	var bytes: PackedByteArray = effect.get("general_data") as PackedByteArray
	return bytes.decode_float(244) if bytes.size() >= 248 else NAN

func _mean_alpha(image: Image) -> float:
	if image.is_empty():
		return INF
	var total := 0.0
	var count := 0
	for y in range(4, image.get_height(), 12):
		for x in range(4, image.get_width(), 12):
			total += image.get_pixel(x, y).a
			count += 1
	return total / max(1, count)

func _accumulate(total: PackedFloat32Array, image: Image) -> void:
	var index := 0
	for y in range(4, image.get_height(), 12):
		for x in range(4, image.get_width(), 12):
			var color := image.get_pixel(x, y)
			if total.size() <= index + 1:
				total.resize(index + 2)
			var luma := color.r * .2126 + color.g * .7152 + color.b * .0722
			total[index] += luma * color.a + BACKGROUND_LUMA * (1.0 - color.a)
			total[index + 1] += color.a
			index += 2

func _error_to_reference(image: Image, reference: PackedFloat32Array, samples: int) -> Dictionary:
	var color_error := 0.0
	var alpha_error := 0.0
	var index := 0
	for y in range(4, image.get_height(), 12):
		for x in range(4, image.get_width(), 12):
			var color := image.get_pixel(x, y)
			var luma := color.r * .2126 + color.g * .7152 + color.b * .0722
			luma = luma * color.a + BACKGROUND_LUMA * (1.0 - color.a)
			color_error += pow(luma - reference[index] / samples, 2)
			alpha_error += pow(color.a - reference[index + 1] / samples, 2)
			index += 2
	return {"color": sqrt(color_error / max(1, index / 2)), "alpha": sqrt(alpha_error / max(1, index / 2))}

func _image_error(a: Image, b: Image) -> Dictionary:
	var reference := PackedFloat32Array()
	_accumulate(reference, a)
	return _error_to_reference(b, reference, 1)

func _require(condition: bool, reason: String) -> void:
	if not condition:
		_fail(reason)

func _fail(reason: String) -> void:
	if failed:
		return
	failed = true
	push_error("MENU_CLOUD_HISTORY_GPU_TEST FAILED: " + reason)
	call_deferred("_cleanup", 1)

func _cleanup(code: int) -> void:
	if gameplay_driver != null:
		gameplay_driver.free()
		gameplay_driver = null
	if cleanup_started:
		return
	cleanup_started = true
	if copy_rect != null:
		copy_rect.texture = null
	if adapter != null:
		adapter.call("Detach")
	if sunshine_source != null and RenderingServer.get_rendering_device() != null:
		RenderingServer.call_on_render_thread(sunshine_source.call.bind("clear_compute"))
	for frame in range(8):
		await RenderingServer.frame_post_draw
	if copy_viewport != null:
		copy_viewport.queue_free()
	if viewport_3d != null:
		viewport_3d.queue_free()
	if adapter != null:
		adapter.queue_free()
	quit(code)
