extends SceneTree

# Production-gameplay temporal history under continuous physical camera motion.
# This is intentionally separate from the settled-pose menu fixture.
const OUTPUT := "res://.godot/cloud-motion-gpu"
const SIZE := Vector2i(640, 360)
const DT := 1.0 / 60.0
const TARGET_FRAMES := [48, 72, 96]
const REFERENCE_SAMPLES := 64
const CAMERA_HEIGHT := 20_112.0
const CAMERA_PITCH := -8.0
const START_AROUND := 17_219_375.0
const MARGIN := 16
var viewport_3d: SubViewport
var copy_viewport: SubViewport
var copy_rect: TextureRect
var world: WorldEnvironment
var camera: Camera3D
var adapter: Node
var settings: Resource
var driver: Node
var failed := false
var cleanup_started := false
var watchdog: SceneTreeTimer
var phase := 0.0
var previous_uploaded_time := -INF
var comparison_history := .85
var comparison_dither_speed := 60.0
var rebase_observations := []

func _initialize() -> void:
	watchdog = create_timer(120.0)
	watchdog.timeout.connect(func() -> void: _fail("120-second watchdog"))
	call_deferred("_run")

func _run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--history="):
			comparison_history = clampf(argument.trim_prefix("--history=").to_float(), 0.0, .95)
		if argument == "--dither-speed-zero": comparison_dither_speed = 0.0
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	_build_world()
	var cases := [{"label":"rotate15", "turn":15.0, "translate":0.0}, {"label":"rotate60", "turn":60.0, "translate":0.0}, {"label":"rotate120", "turn":120.0, "translate":0.0}, {"label":"translate6", "turn":0.0, "translate":6.0}, {"label":"combined60_translate6", "turn":60.0, "translate":6.0}, {"label":"combined60_translate6_rebase", "turn":60.0, "translate":6.0, "rebase":true}]
	var report := {"size":viewport_3d.size, "dt":DT, "targets":TARGET_FRAMES, "reference_samples":REFERENCE_SAMPLES, "sampled_overlap_margin_pixels":MARGIN, "sample_stride_pixels":12, "production_gameplay_sampler":true, "comparison_history":comparison_history, "dither_speed":comparison_dither_speed, "note":"High history is measured while the trajectory continues at target frames; it is not settled after stopping.", "cases":{}}
	for config: Dictionary in cases:
		print("CLOUD_MOTION_GPU_PROGRESS " + str(config.label))
		var result := await _run_case(config)
		if failed: return
		report.cases[config.label] = result
	var file := FileAccess.open(ProjectSettings.globalize_path(OUTPUT.path_join("report.json")), FileAccess.WRITE)
	if file == null: return _fail("could not write report")
	file.store_string(JSON.stringify(report, "\t")); file.close()
	print("CLOUD_HISTORY_MOTION_GPU_TEST=PASS ", JSON.stringify(report))
	await _cleanup(0)

func _build_world() -> void:
	viewport_3d = SubViewport.new(); viewport_3d.own_world_3d = true
	viewport_3d.size = Vector2i(1280,720) if "--render-720" in OS.get_cmdline_user_args() else SIZE
	viewport_3d.msaa_3d = Viewport.MSAA_4X; viewport_3d.render_target_update_mode = SubViewport.UPDATE_DISABLED
	root.add_child(viewport_3d)
	var environment := Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color(.08,.11,.17); environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_energy = .35
	world = WorldEnvironment.new(); world.environment = environment; viewport_3d.add_child(world)
	camera = Camera3D.new(); camera.current = true; camera.near = 50.0; camera.far = 12_000_000.0; camera.fov = 65.0; viewport_3d.add_child(camera)
	var light := DirectionalLight3D.new(); light.light_energy = 2.15; viewport_3d.add_child(light); light.look_at(Vector3(.4,-.75,.5), Vector3.UP)
	var ground := MeshInstance3D.new(); var plane := PlaneMesh.new(); plane.size = Vector2(600000,600000); ground.mesh = plane; viewport_3d.add_child(ground)
	settings = (load("res://scripts/ui/MenuFlyoverSettings.cs") as Script).new()
	adapter = (load("res://scripts/ui/MenuFlyoverSunshineLayer.cs") as Script).new(); root.add_child(adapter)
	adapter.call("Configure", world, 1337, 5_000_000.0, 318_000, Vector3(-.4,.75,-.5).normalized())
	driver = (load("res://addons/SunshineClouds2/SunshineCloudsDriver.gd") as Script).new(); driver.set("update_continuously", false); driver.set("clouds_resource", adapter.get("Effect"))
	copy_viewport = SubViewport.new(); copy_viewport.own_world_3d = true; copy_viewport.disable_3d = true; copy_viewport.transparent_bg = true; copy_viewport.use_hdr_2d = true; copy_viewport.size = viewport_3d.size; copy_viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED; root.add_child(copy_viewport)
	copy_rect = TextureRect.new(); copy_rect.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	var shader := Shader.new(); shader.code = "shader_type canvas_item; render_mode blend_disabled; void fragment(){ COLOR = texture(TEXTURE, UV); }"
	var material := ShaderMaterial.new(); material.shader = shader; copy_rect.material = material; copy_viewport.add_child(copy_rect)

func _configure(history: float) -> void:
	var values := {"CloudsEnabled":true,"CloudShadowsEnabled":false,"ShadowGenerationEnabled":false,"AltitudeKm":20.0,"PitchDegrees":8.0,"FieldOfView":65.0,"CloudFloorKm":2.0,"CloudCeilingKm":8.0,"CloudCoverage":.71,"CloudDensity":3.0,"CloudAmbientStrength":2.0,"CloudSharpness":1.0,"CloudResolution":1,"CloudViewSteps":256,"CloudMinStepMeters":10.0,"CloudMaxStepMeters":1600.0,"CloudLodBias":.1,"CloudLightingSteps":5,"CloudRayStartJitterStrength":1.0,"CloudBlurPower":0.0,"CloudDriftMps":0.0,"CloudDitherSpeed":60.0,"CloudStationaryHistoryWeight":history,"CloudMovingHistoryWeight":history}
	for key: String in values: settings.set(key, values[key])
	adapter.call("ApplyTuning", settings, Vector3(-.4,.75,-.5).normalized())

func _reset(history: float) -> void:
	copy_rect.texture = null; adapter.call("Detach")
	adapter.call("Configure", world, 1337, 5_000_000.0, 318_000, Vector3(-.4,.75,-.5).normalized())
	_configure(history); phase = 0.0; previous_uploaded_time = -INF

func _pose(config: Dictionary, frame: int) -> void:
	var seconds := float(frame) * DT
	# Start at 20 degrees to keep the cloud mass in the test framing.
	var yaw := 20.0 + float(config.turn) * seconds
	camera.position = Vector3(float(config.translate) * seconds, CAMERA_HEIGHT, 0.0)
	camera.rotation_degrees = Vector3(CAMERA_PITCH, yaw, 0.0)

func _source_frame(config: Dictionary, frame: int, capture := true) -> Image:
	_pose(config, frame)
	adapter.call("UpdateFrame", START_AROUND, float(frame) * DT, false)
	var effect: Object = adapter.get("Effect") as Object
	if bool(config.get("rebase", false)):
		var shift := Vector3(128.0, 0.0, 0.0) if frame >= 48 else Vector3.ZERO
		camera.position -= shift
		effect.set("cloud_frame_origin", -shift)
		var around_frame: Vector4 = effect.get("cloud_frame_around")
		var up_frame: Vector4 = effect.get("cloud_frame_up")
		var across_frame: Vector4 = effect.get("cloud_frame_across")
		var basis := Basis(Vector3(around_frame.x, around_frame.y, around_frame.z), Vector3(up_frame.x, up_frame.y, up_frame.z), Vector3(across_frame.x, across_frame.y, across_frame.z))
		# Canonical around/across stay constant; only presentation coordinates shift.
		effect.call("set_gameplay_cloud_frame", Transform3D(basis, -shift), START_AROUND, 0.0, 5_000_000.0)
	effect.set("menu_temporal_sampling", false); effect.set("menu_fast_reconstruction", false); effect.set("dither_speed", comparison_dither_speed); effect.set("current_time", phase)
	driver.set("clouds_resource", effect); driver.call("_process", DT); phase = float(effect.get("current_time"))
	viewport_3d.render_target_update_mode = SubViewport.UPDATE_ONCE; await RenderingServer.frame_post_draw
	var bytes: PackedByteArray = effect.get("general_data") as PackedByteArray
	if bytes.size() < 228: _fail("gameplay UBO missing")
	var uploaded := bytes.decode_float(224)
	_require(is_finite(uploaded) and not is_equal_approx(uploaded, previous_uploaded_time), "gameplay callback phase did not reach UBO")
	previous_uploaded_time = uploaded
	if bool(config.get("rebase", false)) and frame in TARGET_FRAMES:
		var packed_decay := bytes.decode_float(244)
		var expected_decay := float(effect.get("accumulation_decay"))
		_require(is_finite(packed_decay) and is_equal_approx(packed_decay, expected_decay), "rebase packed history differs from requested history at frame " + str(frame))
		var temporal: Dictionary = effect.call("get_temporal_history_diagnostics")
		_require(int(temporal.get("callback_serial", 0)) > 0, "rebase callback missing at frame " + str(frame))
		_require(is_equal_approx(float(temporal.get("last_packed_decay", NAN)), expected_decay), "rebase callback packed zero/unexpected history at frame " + str(frame))
		rebase_observations.append({"frame":frame,"capture":capture,"shift_x":128.0 if frame >= 48 else 0.0,"ubo_packed_decay":packed_decay,"expected_decay":expected_decay,"callback_serial":temporal.get("callback_serial"),"zero_packed_callbacks":temporal.get("zero_packed_history_callback_count")})
	if failed or not capture: return Image.new()
	var cloud := effect.call("get_cloud_backdrop_texture", 0) as Texture2D
	if cloud == null: _fail("published cloud texture missing"); return Image.new()
	copy_rect.texture = cloud; copy_viewport.render_target_update_mode = SubViewport.UPDATE_ONCE; await RenderingServer.frame_post_draw
	var image := copy_viewport.get_texture().get_image()
	_require(not image.is_empty() and (image.get_format() == Image.FORMAT_RGBAH or image.get_format() == Image.FORMAT_RGBAF), "invalid HDR cloud capture")
	return image

func _run_case(config: Dictionary) -> Dictionary:
	if bool(config.get("rebase", false)): rebase_observations = []
	var references := {}; var reference_visuals := {}; var raws := {}; var highs := {}
	for target in TARGET_FRAMES:
		_reset(0.0); _pose(config,target)
		var sum := PackedFloat32Array()
		var reference_sample := Image.new()
		for sample in REFERENCE_SAMPLES:
			reference_sample = await _source_frame(config,target,true); if failed: return {}
			_accumulate(sum,reference_sample)
		references[target] = sum; reference_visuals[target] = reference_sample.duplicate()
	# History-off and high-history traversals use exactly the same physical poses,
	# time sequence, wind, and gameplay dither progression.
	_reset(0.0)
	for frame in range(maxi(TARGET_FRAMES.back() + 1, 1)):
		var raw_capture := frame in TARGET_FRAMES
		var raw_image := await _source_frame(config,frame,raw_capture); if failed: return {}
		if raw_capture: raws[frame] = raw_image
	_reset(comparison_history)
	for frame in range(maxi(TARGET_FRAMES.back() + 1, 1)):
		var capture := frame in TARGET_FRAMES
		var image := await _source_frame(config,frame,capture); if failed: return {}
		if capture: highs[frame] = image
	var metrics := {}
	for target in TARGET_FRAMES:
		var target_metrics: Dictionary = _errors(raws[target], highs[target], references[target])
		metrics[str(target)] = target_metrics
		for region: String in ["interior", "edge"]:
			var region_metrics: Dictionary = target_metrics[region]
			_require(int(region_metrics.get("count", 0)) >= 16, "insufficient " + region + " samples at frame " + str(target) + " for " + str(config.label))
			if failed: return {}
			if "--regression" in OS.get_cmdline_user_args():
				# At .85, turning trades some smoothing for response. Require a
				# material gain at every pose; the old fast-motion cutoff fails
				# the edge bound. Straight travel should converge much further.
				var limit := .85 if region == "interior" else .75
				if float(config.turn) == 0.0: limit = .35
				_require(float(region_metrics.get("ratio", INF)) <= limit, "history ratio exceeded regression limit for " + str(config.label) + " frame " + str(target) + " " + region)
				if failed: return {}
	var last: int = int(TARGET_FRAMES.back())
	raws[last].save_png(ProjectSettings.globalize_path(OUTPUT.path_join("%s_frame%s_raw.png" % [config.label,last])))
	highs[last].save_png(ProjectSettings.globalize_path(OUTPUT.path_join("%s_frame%s_high.png" % [config.label,last])))
	# The full 64-sample reference is retained numerically for metrics. This
	# exported image is the actual final sample from that fixed-pose sequence.
	var reference_visual: Image = reference_visuals[last] as Image
	reference_visual.save_png(ProjectSettings.globalize_path(OUTPUT.path_join("%s_frame%s_reference_phase_sample.png" % [config.label,last])))
	return {"trajectory":config,"history_comparison":{"raw":0.0,"high":comparison_history},"targets":metrics,"reference":"64 fixed-pose raw samples (numeric); exported reference image is its final phase sample","rebase_observations":rebase_observations.duplicate(true) if bool(config.get("rebase", false)) else [],"last_images":{"raw":OUTPUT.path_join("%s_frame%s_raw.png" % [config.label,last]),"high":OUTPUT.path_join("%s_frame%s_high.png" % [config.label,last]),"reference_phase_sample":OUTPUT.path_join("%s_frame%s_reference_phase_sample.png" % [config.label,last])}}

func _accumulate(total: PackedFloat32Array, image: Image) -> void:
	var i := 0
	for y in range(MARGIN,image.get_height()-MARGIN,12):
		for x in range(MARGIN,image.get_width()-MARGIN,12):
			var p := image.get_pixel(x,y); if total.size() <= i+1: total.resize(i+2)
			total[i] += (p.r*.2126+p.g*.7152+p.b*.0722)*p.a + .108*(1.0-p.a); total[i+1] += p.a; i += 2

func _errors(raw: Image, high: Image, reference: PackedFloat32Array) -> Dictionary:
	var result := {"interior":{"raw_luma":0.0,"high_luma":0.0,"count":0},"edge":{"raw_luma":0.0,"high_luma":0.0,"count":0}}
	var i := 0
	for y in range(MARGIN,raw.get_height()-MARGIN,12):
		for x in range(MARGIN,raw.get_width()-MARGIN,12):
			var alpha := reference[i+1]/REFERENCE_SAMPLES; var region := "interior" if alpha>=.85 else "edge" if alpha>.1 else ""
			if not region.is_empty():
				var target := reference[i]/REFERENCE_SAMPLES; var r := raw.get_pixel(x,y); var h := high.get_pixel(x,y)
				var rl := (r.r*.2126+r.g*.7152+r.b*.0722)*r.a+.108*(1-r.a); var hl := (h.r*.2126+h.g*.7152+h.b*.0722)*h.a+.108*(1-h.a)
				var entry: Dictionary=result[region]; entry.raw_luma+=pow(rl-target,2); entry.high_luma+=pow(hl-target,2); entry.count+=1
			i+=2
	for region: String in result:
		var entry: Dictionary=result[region]; var count:=int(entry.count); entry.raw_luma=sqrt(float(entry.raw_luma)/maxi(1,count)); entry.high_luma=sqrt(float(entry.high_luma)/maxi(1,count)); entry.ratio=float(entry.high_luma)/maxf(.000001,float(entry.raw_luma)); entry.meaningful=count>=16
	return result

func _require(ok: bool, why: String) -> void:
	if not ok: _fail(why)
func _fail(why: String) -> void:
	if failed: return
	failed=true; push_error("CLOUD_HISTORY_MOTION_GPU_TEST FAILED: "+why); call_deferred("_cleanup",1)
func _cleanup(code: int) -> void:
	if cleanup_started: return
	cleanup_started=true
	if copy_rect != null: copy_rect.texture=null
	if adapter != null: adapter.call("Detach")
	if driver != null: driver.free()
	if copy_viewport != null: copy_viewport.queue_free()
	if viewport_3d != null: viewport_3d.queue_free()
	if adapter != null: adapter.queue_free()
	quit(code)
