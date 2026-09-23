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
		_require(packed.size() == 720 and is_equal_approx(packed.decode_float(716), 1.0), "history diagnostic flag did not reach the GPU")
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
	var final_time := 50.0 + DT * (HIGH_WARMUP - 1)
	var final_around := START_AROUND + (SPEED * DT * (HIGH_WARMUP - 1) if moving else 0.0)
	# Raw reference at the exact pose/time at which the high-history run ends.
	_reset_adapter()
	_apply(0.0, 0.0)
	var reference := PackedFloat32Array()
	var raw_final := Image.new()
	var phase_matched_raw := Image.new()
	for sample in range(64):
		raw_final = await _frame(final_around, final_time, true)
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
		time += DT
		if moving:
			around += SPEED * DT
	var suffix := "_user_quality" if user_quality else ""
	phase_matched_raw.save_png("%s/menu_cloud_history_%s_raw%s.png" % [OUTPUT, label, suffix])
	high_final.save_png("%s/menu_cloud_history_%s_high%s.png" % [OUTPUT, label, suffix])
	var effect: Object = adapter.get("Effect") as Object
	var result := {"raw": _error_to_reference(phase_matched_raw, reference, 64), "high": _error_to_reference(high_final, reference, 64), "final_time": final_time, "final_around": final_around, "true_static": _controls(effect).get("true_static", 0.0)}
	print("MENU_CLOUD_HISTORY_GPU_METRICS ", label, " ", JSON.stringify(result))
	return result

func _frame(around: float, time: float, frozen: bool, capture: bool = true) -> Image:
	adapter.call("UpdateFrame", around, time, frozen)
	viewport_3d.render_target_update_mode = SubViewport.UPDATE_ONCE
	await RenderingServer.frame_post_draw
	if not capture:
		return Image.new()
	var cloud := adapter.call("GetCloudTexture") as Texture2D
	if cloud == null:
		_fail("cloud texture was unavailable")
		return Image.new()
	copy_rect.texture = cloud
	copy_viewport.render_target_update_mode = SubViewport.UPDATE_ONCE
	await RenderingServer.frame_post_draw
	return copy_viewport.get_texture().get_image()

func _controls(effect: Object) -> Dictionary:
	var bytes: PackedByteArray = effect.get("general_data") as PackedByteArray
	if bytes.size() < 720:
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
