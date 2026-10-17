extends SceneTree

## Phase E3 windowed capture: T1 zero-uniform parity (hub uniforms at 0, before/after the hub lighting change) and the "noon everywhere" shots.
## Modes (windowed, NO other Godot process running, one run at a time):
##   --mode=parity --out=<dir> [--ref=<dir of an earlier parity run>] [--ref2=<dir of a second run of the reference build>] [--only=pose_moment,...]
##     Shoots day, night and totality (pitch-black darkness ON) at five poses (ground horizon, ground up, 9 km altitude looking down, and the
##     main spire's Far and High emitter viewpoints, the M1 poses). Every state is shot TWICE (a, b) with the celestial and weather clocks
##     frozen. The a/b pair is the same-state noise baseline. Without --ref the run just records the frames and the clock times. With --ref
##     (a previous parity run, e.g. the pre-E3 build) the clock times are taken from <ref>/results.json, and every frame is compared
##     with the reference: a frame is "bit identical" when its RGBA bytes are equal; otherwise the frame-to-reference difference must stay
##     within PARITY_NOISE_FACTOR x the largest noise measure (small absolute floors). Noise measures: the a/b pair of this run, the a/b pair
##     of the reference, and with --ref2=<dir> (a second run of the SAME reference build, i.e. the cross-process noise) ref2 vs ref.
##     Any failure exits with FAIL. Keep a full run for the gate: the frames depend on the states shot before them (exposure and cloud
##     history), so a --only frame is not comparable with a full-run reference except for the very first state (ground_horizon_day).
##   --mode=noon --out=<dir> [--fixed-exposure]
##     Totality with the hub level override at 0 / 0.1 / 0.5 / 1.0, and the real sun at its highest elevation (day, dayhub = override 1.0 with the
##     real sun, dayhub_nohelper = override 1.0 with the per-fragment light helper switched off, day2 = the first state again, for the GPU
##     time A/B/A), at ground (horizon, up, far arch) and from altitude (9 km down, 150 km up at the arch). --fixed-exposure turns auto
##     exposure off so luminance compares light. Reports mean luminance, the far-arch strip evenness (ground_arch) and GPU time. The override
##     is EclipseEventDirector.SetHubLevelOverride (visual only). Each state also records the mean GPU / CPU render time of the root viewport
##     over PERF_FRAMES frames (RenderingServer measured render time), so hub-off against hub-on is the shader cost of the hub lighting.
## Uses an isolated world under res://.godot/eclipse-capture/ and a stub WorldSession; nothing is written to user:// settings.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PHASE_DARKNESS := 2
const EXPOSURE_SETTLE_S := 8.0
const SHOT_GAP_S := 1.0
const CHANGED_CHANNEL_THRESHOLD := 2.5 / 255.0
const PARITY_NOISE_FACTOR := 2.0
# Absolute floors only matter where the measured noise is exactly 0 (dark frames are often bit-stable): 0.1 % of sampled pixels more than
# 2.5/255 off, 0.0002 mean signed luma, 0.0003 mean absolute luma.
const PARITY_CHANGED_FRACTION_FLOOR := 0.001
const PARITY_MEAN_LUMA_FLOOR := 0.0002
const PARITY_MEAN_ABS_LUMA_FLOOR := 0.0003
const FIXED_CLOUD_TIME := 1234.5
const PERF_FRAMES := 240

var out_dir := ""
var mode := "parity"
var ref_dir := ""
var ref2_dir := ""
var fixed_exposure := false   # --fixed-exposure turns auto-exposure off so luminance compares light, not the eye adaptation
var only_states: Array = []   # e.g. --only=main_high_day,ground_up_night limits the parity run to those pose_moment states
var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var celestial: Node
var weather: Node
var atmosphere: Node
var director: Node
var drawer: Node
var base_pose := {}
var results := {"events": [], "info": {}, "luminance": {}, "compare": {}, "gates": {}, "perf": {}, "strip": {}}
var gate_failures: Array = []
var shot_count := 0
var finished := false


class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false


func _init() -> void:
	_run.call_deferred()
	create_timer(3000.0).timeout.connect(func(): _abort("harness watchdog"))


func _log(text: String) -> void:
	print("HUB_CAPTURE ", text)
	results["events"].append([Time.get_ticks_msec(), text])


func _run() -> void:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--out="): out_dir = a.trim_prefix("--out=")
		elif a.begins_with("--ref="): ref_dir = a.trim_prefix("--ref=")
		elif a.begins_with("--ref2="): ref2_dir = a.trim_prefix("--ref2=")
		elif a == "--fixed-exposure": fixed_exposure = true
		elif a.begins_with("--only="): only_states = Array(a.trim_prefix("--only=").split(",", false))
		elif a.begins_with("--mode="): mode = a.trim_prefix("--mode=")
	if out_dir == "":
		_abort("--out=<dir> is required"); return
	DirAccess.make_dir_recursive_absolute(out_dir)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	results["info"]["adapter"] = RenderingServer.get_video_adapter_name()
	results["info"]["mode"] = mode
	fixture = "res://.godot/eclipse-capture/world-%d" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalHubCapture"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await _create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	celestial = game.get_node_or_null("RingDayNightCycle")
	weather = game.get_node_or_null("RingVolumetricClouds")
	atmosphere = game.get_node_or_null("DaySideAtmosphere")
	director = game.get_node_or_null("EclipseEvent")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	if explorer == null or celestial == null or atmosphere == null or director == null or drawer == null:
		_abort("missing game nodes"); return
	for m in ["set_process_input", "set_process_unhandled_input", "set_process_unhandled_key_input", "set_process_shortcut_input"]: explorer.call(m, false)
	var waited := 0
	while waited < 900:
		if int(drawer.call("FindEmitterSite", "main")) >= 0: break
		waited += 1
		await physics_frame
	await _wait_global()
	base_pose = _pose_now()
	celestial.set("Paused", true)
	if weather != null:
		weather.set("CloudSimulationPaused", true)
		weather.set("CloudSimulationTimeSeconds", FIXED_CLOUD_TIME)
	var gpu: Array = []
	OS.execute("nvidia-smi", ["--query-gpu=name,pstate,temperature.gpu,clocks.gr", "--format=csv,noheader"], gpu)
	results["info"]["gpu"] = gpu
	_set_darkness(true)
	if fixed_exposure: atmosphere.set("storm_auto_exposure_enabled", false)
	var times := await _moment_times()
	results["info"]["times"] = times
	if mode == "noon": await _run_noon(times)
	else: await _run_parity(times)
	_write()
	if gate_failures.is_empty(): _finish()
	else: _abort("gates failed: " + ", ".join(gate_failures))


## Clock times for day / night / totality. With --ref they are read from the reference run so both runs shoot the very same states.
func _moment_times() -> Dictionary:
	if ref_dir != "" and FileAccess.file_exists(ref_dir.path_join("results.json")):
		var ref = JSON.parse_string(FileAccess.get_file_as_string(ref_dir.path_join("results.json")))
		if ref is Dictionary and ref.get("info", {}).has("times"):
			var t: Dictionary = ref["info"]["times"]
			if t.has("totality"):
				director.call("JumpToPhase", PHASE_DARKNESS)
				await _frames(4)
				return t
	director.call("JumpToPhase", PHASE_DARKNESS)
	await _frames(4)
	var t_totality := float(celestial.call("GetSimulationSeconds"))
	return {"totality": t_totality, "day": _find_time(t_totality, true), "night": _find_time(t_totality, false)}


func _run_parity(times: Dictionary) -> void:
	var gate_count := 0
	for pose_name in ["ground_horizon", "ground_up", "alt_down", "main_far", "main_high"]:
		var wanted := only_states.is_empty()
		for moment in ["day", "night", "totality"]:
			if only_states.has("%s_%s" % [pose_name, moment]): wanted = true
		if not wanted: continue
		await _go_named(pose_name)
		for moment in ["day", "night", "totality"]:
			if not only_states.is_empty() and not only_states.has("%s_%s" % [pose_name, moment]): continue
			celestial.call("SetSimulationSeconds", float(times[moment]))
			await _settle(EXPOSURE_SETTLE_S)
			var key := "%s_%s" % [pose_name, moment]
			var a := await _shot_image()
			a.save_png(out_dir.path_join("%s_a.png" % key))
			await _settle(SHOT_GAP_S)
			var b := await _shot_image()
			b.save_png(out_dir.path_join("%s_b.png" % key))
			results["luminance"][key] = _mean_luma(a)
			_log("%s luma=%.5f weight=%s hub_weight=%s %s" % [key, results["luminance"][key], str(atmosphere.get("eclipse_darkness_weight")),
				str(director.get("HubLightWeight")), _far_sun_report()])
			results["compare"][key + "_noise"] = _diff_pixels(a, b)
			if ref_dir != "":
				gate_count += 1
				_gate_against_ref(key, a, b)
		_write()
	results["info"]["gate_count"] = gate_count


func _gate_against_ref(key: String, a: Image, b: Image) -> void:
	var ra := Image.load_from_file(ref_dir.path_join("%s_a.png" % key))
	var rb := Image.load_from_file(ref_dir.path_join("%s_b.png" % key))
	if ra == null or rb == null:
		results["gates"][key] = false; gate_failures.append(key + "_missing_ref"); _log("PARITY %s FAIL missing reference frames" % key); return
	var ref_noise := _diff_pixels(ra, rb)
	var cur_noise := _diff_pixels(a, b)
	var exact_a := a.get_data() == ra.get_data()
	var exact_b := b.get_data() == rb.get_data()
	var effect_a := _diff_pixels(ra, a)
	var effect_b := _diff_pixels(rb, b)
	var effect_x := _diff_pixels(ra, b)
	var noise_changed := maxf(float(ref_noise["changed_fraction"]), float(cur_noise["changed_fraction"]))
	var noise_dl := maxf(absf(float(ref_noise["mean_luma_delta"])), absf(float(cur_noise["mean_luma_delta"])))
	var noise_abs := maxf(float(ref_noise["mean_abs_luma"]), float(cur_noise["mean_abs_luma"]))
	var cross_noise := {}
	if ref2_dir != "":
		var r2a := Image.load_from_file(ref2_dir.path_join("%s_a.png" % key))
		if r2a != null:
			cross_noise = _diff_pixels(ra, r2a)
			noise_changed = maxf(noise_changed, float(cross_noise["changed_fraction"]))
			noise_dl = maxf(noise_dl, absf(float(cross_noise["mean_luma_delta"])))
			noise_abs = maxf(noise_abs, float(cross_noise["mean_abs_luma"]))
	var changed_limit := maxf(PARITY_NOISE_FACTOR * noise_changed, PARITY_CHANGED_FRACTION_FLOOR)
	var luma_limit := maxf(PARITY_NOISE_FACTOR * noise_dl, PARITY_MEAN_LUMA_FLOOR)
	var abs_limit := maxf(PARITY_NOISE_FACTOR * noise_abs, PARITY_MEAN_ABS_LUMA_FLOOR)
	var worst_changed := maxf(float(effect_a["changed_fraction"]), maxf(float(effect_b["changed_fraction"]), float(effect_x["changed_fraction"])))
	var worst_dl := maxf(absf(float(effect_a["mean_luma_delta"])), maxf(absf(float(effect_b["mean_luma_delta"])), absf(float(effect_x["mean_luma_delta"]))))
	var worst_abs := maxf(float(effect_a["mean_abs_luma"]), maxf(float(effect_b["mean_abs_luma"]), float(effect_x["mean_abs_luma"])))
	var exact := exact_a and exact_b
	var passed := exact or (worst_changed <= changed_limit and worst_dl <= luma_limit and worst_abs <= abs_limit)
	results["gates"][key] = passed
	results["compare"][key] = {"bit_identical_a": exact_a, "bit_identical_b": exact_b, "ref_noise": ref_noise, "cur_noise": cur_noise, "cross_process_noise": cross_noise,
		"effect_a": effect_a, "effect_b": effect_b, "effect_cross": effect_x,
		"limits": {"changed_fraction": changed_limit, "mean_luma_delta": luma_limit, "mean_abs_luma": abs_limit}, "pass": passed}
	_log("PARITY %s %s exact=%s/%s diff_changed=%.5f (limit %.5f, noise %.5f) diff_dluma=%.6f (limit %.6f) diff_abs=%.6f (limit %.6f) max_ch=%.4f differing_px=%d/%d" % [
		key, "PASS" if passed else "FAIL", str(exact_a), str(exact_b), worst_changed, changed_limit, noise_changed, worst_dl, luma_limit, worst_abs, abs_limit,
		maxf(float(effect_a["max_channel_diff"]), float(effect_b["max_channel_diff"])), int(effect_a["differing"]), int(effect_a["sampled"])])
	if not passed: gate_failures.append(key)


func _run_noon(times: Dictionary) -> void:
	if not director.has_method("SetHubLevelOverride"):
		_abort("director lacks SetHubLevelOverride"); return
	# Real noon for the reference: the time of highest sun elevation within half a ring day of the first clear day found.
	var t_noon := float(times["day"])
	var best := -1000.0
	for i in 101:
		var t := float(times["day"]) - 2250.0 + 45.0 * i
		celestial.call("SetSimulationSeconds", t)
		if float(celestial.get("RawEclipseCoverage")) > 0.0: continue
		var elevation := float(celestial.get("SunElevationDegrees"))
		if elevation > best: best = elevation; t_noon = t
	times["noon"] = t_noon
	results["info"]["t_noon"] = t_noon
	results["info"]["noon_sun_elevation_deg"] = best
	for pose_name in ["ground_horizon", "ground_up", "ground_arch", "alt_down", "alt_arch"]:
		await _go_named(pose_name)
		# Real-sun noon reference first, then totality with the hub override at 0 / 0.1 / 0.5 / 1.0.
		# day / dayhub / day2: the real sun with the hub override off, on (full), off again. Same sun energy and shadow passes, so the GPU
		# time difference is the cost of the hub-mode light() path and the sky halo.
		var states := [["day", float(times["noon"]), -1.0], ["dayhub", float(times["noon"]), 1.0], ["dayhub_nohelper", float(times["noon"]), 1.0, true],
			["day2", float(times["noon"]), -1.0],
			["dark", float(times["totality"]), 0.0], ["hub010", float(times["totality"]), 0.1],
			["hub050", float(times["totality"]), 0.5], ["hub100", float(times["totality"]), 1.0]]
		for s in states:
			celestial.call("SetSimulationSeconds", float(s[1]))
			director.call("SetHubLevelOverride", float(s[2]))
			await _frames(3)
			# dayhub_nohelper: the light stays at the zenith and the hub day stays, but the per-fragment helper is switched off (weight global 0).
			if s.size() > 3 and bool(s[3]): RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", 0.0)
			await _settle(EXPOSURE_SETTLE_S)
			var key := "%s_%s" % [pose_name, s[0]]
			var img := await _shot_image()
			img.save_png(out_dir.path_join("%s.png" % key))
			var regions := _region_luma(img)
			results["luminance"][key] = regions
			results["perf"][key] = await _measure_perf()
			if pose_name == "ground_arch":
				results["strip"][key] = _strip_profile(img)
				_log("%s strip luma by band (top to bottom): %s" % [key, str(results["strip"][key])])
			_log("%s luma=%.5f top=%.5f bottom=%.5f ratio_top_bottom=%.3f gpu_ms=%.3f cpu_ms=%.3f" % [key, regions["mean"], regions["top"], regions["bottom"],
				float(regions["top"]) / maxf(float(regions["bottom"]), 0.000001), float(results["perf"][key]["gpu_ms"]), float(results["perf"][key]["cpu_ms"])])
		director.call("SetHubLevelOverride", -1.0)
		_write()


## Mean GPU and CPU render ms of the root viewport over PERF_FRAMES frames (Forward+ timestamp queries).
func _measure_perf() -> Dictionary:
	var vp := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp, true)
	await _frames(20)
	var gpu := 0.0
	var cpu := 0.0
	var worst_gpu := 0.0
	for i in PERF_FRAMES:
		await process_frame
		var g := RenderingServer.viewport_get_measured_render_time_gpu(vp)
		gpu += g
		worst_gpu = maxf(worst_gpu, g)
		cpu += RenderingServer.viewport_get_measured_render_time_cpu(vp)
	RenderingServer.viewport_set_measure_render_time(vp, false)
	return {"gpu_ms": gpu / PERF_FRAMES, "cpu_ms": cpu / PERF_FRAMES, "gpu_ms_max": worst_gpu}


## Mean luma of the far-arch strip (looking straight up from the ground the ring arch is a narrow vertical band at the screen centre),
## in 10 vertical bands. The crosshair rows are skipped.
func _strip_profile(img: Image) -> Array:
	var bands := []
	for b in 10:
		var sum := 0.0
		var count := 0
		for y in range(b * 90, b * 90 + 90):
			if y >= 435 and y < 465: continue
			for x in range(794, 807):
				var c := img.get_pixel(x, y)
				sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b
				count += 1
		bands.append(snappedf(sum / maxf(count, 1), 0.0001))
	return bands


## LightColor and energy of the far viewport suns (diagnostic for the hub colour mirroring).
func _far_sun_report() -> String:
	var parts := []
	for n in ["FarRingSun", "FarRingRegionalSun"]:
		var light := game.find_child(n, true, false) as DirectionalLight3D
		parts.append("%s=%s e=%.3f" % [n, str(light.light_color) if light != null else "none", light.light_energy if light != null else -1.0])
	return " ".join(parts)


func _region_luma(img: Image) -> Dictionary:
	var small := img.duplicate() as Image
	small.resize(160, 90, Image.INTERPOLATE_BILINEAR)
	var top := 0.0
	var bottom := 0.0
	var count := 0
	for y in small.get_height():
		for x in small.get_width():
			var c := small.get_pixel(x, y)
			var l := 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b
			if y < 45: top += l
			else: bottom += l
			count += 1
	var half := float(count) * 0.5
	return {"mean": (top + bottom) / float(count), "top": top / half, "bottom": bottom / half}


func _go_named(pose_name: String) -> void:
	if pose_name.begins_with("main_"):
		var site := int(drawer.call("FindEmitterSite", "main"))
		var vp := 2 if pose_name == "main_far" else 3
		if site < 0 or not bool(drawer.call("RequestEmitterTeleport", site, vp)):
			_abort("main spire teleport refused for " + pose_name); return
		var start := Time.get_ticks_msec()
		while Time.get_ticks_msec() - start < 180000:
			await physics_frame
			var state := str(drawer.get("EmitterTeleportState"))
			if state == "Arrived": break
			if state == "Failed" or state == "TimedOut":
				_abort("teleport %s: %s" % [state, str(drawer.get("EmitterTeleportMessage"))]); return
		var landing := 0
		while landing < 180 and not bool(explorer.get("NoclipEnabled")) and not bool(explorer.call("is_on_floor")):
			landing += 1
			await physics_frame
		await _wait_global()
		await _settle(5.0)
		return
	await _go(_pose(pose_name), 5.0)


func _pose(name: String) -> Dictionary:
	var p := base_pose.duplicate()
	p["flight"] = true
	match name:
		"ground_horizon": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 2.0
		"ground_up": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 28.0
		"ground_arch": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 70.0
		"alt_down": p["vertical"] = float(base_pose["vertical"]) + 9000.0; p["pitch"] = -25.0
		"alt_arch": p["vertical"] = float(base_pose["vertical"]) + 150000.0; p["pitch"] = 60.0
	return p


func _set_darkness(enabled: bool) -> void:
	var script: Script = load("res://scripts/ui/GameplayEclipseEventSettings.cs")
	var gameplay: Resource = script.new()
	gameplay.set("EclipseDarknessEnabled", enabled)
	director.call("Configure", gameplay)


func _find_time(from_t: float, want_day: bool) -> float:
	var t := from_t
	for i in 600:
		t += 450.0
		celestial.call("SetSimulationSeconds", t)
		var daylight := float(celestial.get("DaylightFactor"))
		var coverage := float(celestial.get("RawEclipseCoverage"))
		if coverage > 0.0: continue
		if want_day and daylight > 0.9: return t
		if (not want_day) and daylight < 0.02: return t
	return from_t


func _mean_luma(img: Image) -> float:
	return float(_region_luma(img)["mean"])


func _diff_pixels(a: Image, b: Image) -> Dictionary:
	var width := mini(a.get_width(), b.get_width())
	var height := mini(a.get_height(), b.get_height())
	if a.get_data() == b.get_data():
		return {"sampled": (width / 2) * (height / 2), "differing": 0, "changed": 0, "changed_fraction": 0.0,
			"mean_luma_delta": 0.0, "mean_abs_luma": 0.0, "max_channel_diff": 0.0}
	var differing := 0
	var changed := 0
	var max_channel := 0.0
	var luma_delta := 0.0
	var luma_abs := 0.0
	var sampled := 0
	for y in range(0, height, 2):
		for x in range(0, width, 2):
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			var d := maxf(maxf(absf(ca.r - cb.r), absf(ca.g - cb.g)), absf(ca.b - cb.b))
			if d > 0.0: differing += 1
			if d > CHANGED_CHANNEL_THRESHOLD: changed += 1
			max_channel = maxf(max_channel, d)
			var dl := 0.2126 * (cb.r - ca.r) + 0.7152 * (cb.g - ca.g) + 0.0722 * (cb.b - ca.b)
			luma_delta += dl
			luma_abs += absf(dl)
			sampled += 1
	var n := float(maxi(sampled, 1))
	return {"sampled": sampled, "differing": differing, "changed": changed, "changed_fraction": float(changed) / n,
		"mean_luma_delta": luma_delta / n, "mean_abs_luma": luma_abs / n, "max_channel_diff": max_channel}


func _shot_image() -> Image:
	await RenderingServer.frame_post_draw
	return root.get_texture().get_image()


func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	selector.select(2); selector.emit_signal("item_selected", 2)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_abort("runtime surface preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", fixture.path_join("surface_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEmitterSiteDefaults"):
		tuning.call("InstallEmitterSiteDefaults", fixture.path_join("emitter_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEclipseDefaults"):
		tuning.call("InstallEclipseDefaults", fixture.path_join("eclipse_tuning.tres"))
	var mon: Node = next.get_node_or_null("RingPerformanceMonitor")
	if mon != null:
		var dst := fixture.path_join("telemetry.tres")
		mon.call("InstallSettingsPathForTests", dst)
		var d := fixture.path_join("reports")
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(d))
		mon.call("InstallReportDirectoryForTests", d)
	root.add_child(next); current_scene = next; launcher.queue_free()
	var deadline := Time.get_ticks_msec() + 600000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_abort("startup readiness deadline")
	return null


func _frames(n: int) -> void:
	for i in n: await process_frame


func _settle(seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end: await process_frame


func _wait_global() -> void:
	var global_lod: Node = game.get_node_or_null("GlobalTerrainLod3") if is_instance_valid(game) else null
	if global_lod == null: return
	var start := Time.get_ticks_msec()
	while int(global_lod.get("PendingInitialSegmentCount")) > 0 and Time.get_ticks_msec() - start < 180000:
		await process_frame


func _pose_now() -> Dictionary:
	var diag: Dictionary = surface.call("GetSurfaceDiagnostics") if surface != null else {}
	return {"around": float(diag.get("player_around", 0.0)), "vertical": float(diag.get("player_height", 0.0)),
		"across": float(diag.get("player_across", 0.0)), "yaw": rad_to_deg(float(explorer.get("SavedYaw"))),
		"pitch": float(explorer.get("SavedPitch")), "flight": bool(explorer.get("NoclipEnabled"))}


func _go(p: Dictionary, settle_s := 6.0) -> void:
	explorer.call("TeleportToCanonicalMeters", float(p["around"]), float(p["vertical"]), float(p["across"]), float(p["yaw"]), float(p["pitch"]), bool(p.get("flight", true)))
	await _frames(8)
	await _wait_global()
	await _settle(settle_s)


func _write() -> void:
	var f := FileAccess.open(out_dir.path_join("results.json"), FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(results, " "))
	f.close()


func _finish() -> void:
	if finished: return
	finished = true
	_write()
	print("HUB_CAPTURE=PASS")
	if is_instance_valid(game): game.queue_free()
	await process_frame
	await process_frame
	quit(0)


func _abort(reason: String) -> void:
	if finished: return
	finished = true
	_log("ABORT " + reason)
	_write()
	print("HUB_CAPTURE=FAIL ", reason)
	quit(1)
