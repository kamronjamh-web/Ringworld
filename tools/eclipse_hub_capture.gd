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
##   --mode=visuals --out=<dir> [--shots=s4,s6,s7,storm]   (phase E4b: the spire to hub beams, driven by the director's scrub debug with the clock paused)
##     s4    = spec G.6 shot 4: main fire seen from the main spire's Far pose (climbing, full at the kindle, and the Sustain pillar).
##     s6    = shot 6: the nearest regular spire firing, seen from its Far viewpoint (about 5 km): climbing and complete.
##     s7    = shot 7: full noon with all 595 beams lit (Crescendo), looking up at the arch from the ground and from 150 km, with a no-beam
##             reference of the same pose for the difference.
##     storm = the T2 check on the real beams: a spawned storm front behind the main pillar, beams off / halo only / halo + depth core, no cloud
##             refresh while lit. Gate: the depth core recovers at least STORM_CORE_GAIN_MIN x the halo-only light, measured over the pixels the beam changes
##             above the spire crown and above the cloud deck base: the rows above the higher of the two screen points on the beam (a beam that starts at the
##             bore, BeamOrigin Base, is also visible below the crown, in and under the spire lattice, where the halo-only frame shows it too, which would
##             dilute the ratio; the Crown origin has no beam there).
##             The pose is the spire's 3 km Far viewpoint stepped --storm-side (default 1,500 m) across and re-aimed at the crown: on the plain Far
##             viewpoint the spire's plateau-lit relay link (E4d) runs right over the camera and projects onto the beam's screen column. The relay
##             grid is drawn (--hide-grid hides it, for diagnosis).
##   --mode=visuals --shots=s5,s7,s9,gates   (phase E4c: the hub sun body, the sky in-scatter, the halo and the far-sun colour mirror)
##     s5    = spec G.6 shot 5: kindle overhead (the hub level at the kindle, looking at the zenith from the ground).
##     s7    = shot 7 again (now with the hub body where the beams meet): ground / far arch / zenith / 150 km, with a no-beam reference.
##     s9    = shot 9: cloudy noon. A storm front overhead (the disc must be hidden) from the ground, and from 9 km looking down (hub-lit tops).
##     gates = the E4c numeric gates in final-output space (see _run_gates): disc centre >= 0.98 and >= 0.15 above the sky at 24 px at L = 1, the kindle
##             still a small white point, the hub-lit sky blue/red ratio >= 0.9 x real noon at the same poses, clouds and overhead geometry hide the disc,
##             and the far-viewport suns white at hub weight 0 / the hub colour at weight 1.
##   --mode=visuals --shots=s6,s8   (phase E4d: the grid ignition fronts, the suppressed normal pulse and the Sustain refuel flashes)
##     s6    = shot 6 now also includes the 12 ignition fronts climbing the arch both ways (s6_fronts_*: from 150 km and from the ground, at several
##             times after the ignition, plus the meeting at the antipode), next to the nearest-spire firing shots.
##     s8    = shot 8: Sustain row flash. The nearest regular spire seen from 5 km at its refuel flash (ArrivalTime on the grid clock): just before,
##             climbing, holding, fading, gone; the same instant from the ground looking up and from 150 km (the row of six).
##   --set=Key:value,... applies GameplayEclipseEventSettings overrides (e.g. HubZenithHaloIntensity:0.2,HubSkyInScatter:2) to every Configure.
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
const STORM_CORE_GAIN_MIN := 5.0
const STORM_MASK_THRESHOLD := 4.0 / 255.0
const STORM_NOISE_FLOOR := 0.005
const STORM_DIFFUSION_KEEP_MIN := 0.8   # phase E4e: the default (diffusion, no core cut) keeps this share of the old core light
# E4c gates (final image, 1600x900)
const DISC_CENTER_MIN := 0.98
const DISC_CONTRAST_MIN := 0.15
const DISC_KINDLE_CENTER_MIN := 0.9
const DISC_RING_PX := 24
const SKY_RATIO_MIN_FRACTION := 0.9
const OCCLUDED_CONTRIBUTION_MAX := 0.05
const SHOT_SIZE := Vector2(1600.0, 900.0)

var out_dir := ""
var mode := "parity"
var ref_dir := ""
var ref2_dir := ""
var fixed_exposure := false   # --fixed-exposure turns auto-exposure off so luminance compares light, not the eye adaptation
var only_states: Array = []   # e.g. --only=main_high_day,ground_up_night limits the parity run to those pose_moment states
var shots: Array = []         # --shots=s4,s6,s7,storm,s5,s9,gates for --mode=visuals (default s4,s6,s7,storm)
var sweep := ""               # --sweep="Key:v,Key:v|Key:v" (configs separated by |) for --shots=sky: the sky blue/red ratio per config
var hide_grid := false        # --hide-grid hides the near relay grid layer during the storm check (diagnostic; by default the grid is drawn)
var storm_side_offset := 1500.0  # --storm-side=<m>: sideways (across) step of the storm pose, so the plateau-lit relay link toward the camera does not project onto the hub beam's screen column (0 = the plain Far viewpoint)
var storm_distance := -1.0    # --storm-distance=<m> overrides bridge.SpawnDistanceMeters for the storm check (default: the bridge value, 12 km)
var overrides := {}           # --set=Key:value,... GameplayEclipseEventSettings overrides applied to every Configure
var bridge: Node
var beams: Node
var clouds_res: Object
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
		elif a.begins_with("--shots="): shots = Array(a.trim_prefix("--shots=").split(",", false))
		elif a.begins_with("--sweep="): sweep = a.trim_prefix("--sweep=")
		elif a == "--keep-grid": hide_grid = false
		elif a == "--hide-grid": hide_grid = true
		elif a.begins_with("--storm-side="): storm_side_offset = float(a.trim_prefix("--storm-side="))
		elif a.begins_with("--storm-distance="): storm_distance = float(a.trim_prefix("--storm-distance="))
		elif a.begins_with("--set="):
			for pair in a.trim_prefix("--set=").split(",", false):
				var kv := pair.split(":", false)
				if kv.size() == 2: overrides[kv[0]] = float(kv[1])
	if shots.is_empty(): shots = ["s4", "s6", "s7", "storm"]
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
	bridge = game.get_node_or_null("SunshineCloudBridge")
	beams = game.get_node_or_null("EclipseBeams")
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	clouds_res = driver.get("clouds_resource") if driver != null else null
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
	elif mode == "visuals": await _run_visuals()
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


# ------------------------------------------------------------------ phase E4b: spire to hub beams

func _mark(name: String) -> float:
	return float(director.call("TimelineMarkTau", name))


## Moves the (paused) celestial clock to the event time tau through the director's scrub debug and lets the hub light ease in.
func _scrub(tau: float, settle_s := 6.0) -> void:
	if not bool(director.call("ScrubToTau", tau)):
		_abort("scrub to tau %.2f refused" % tau); return
	await _frames(4)
	await _settle(settle_s)


## Defaults + the --set overrides + extra, as one Configure (Configure replaces the whole settings object).
func _configure(extra := {}) -> void:
	var script: Script = load("res://scripts/ui/GameplayEclipseEventSettings.cs")
	var gameplay: Resource = script.new()
	for k in overrides: gameplay.set(k, overrides[k])
	for k in extra: gameplay.set(k, extra[k])
	director.call("Configure", gameplay)


func _beam_settings(enabled: bool, core: bool) -> void:
	_configure({"BeamsEnabled": enabled, "BeamDepthWriteCore": core})


## Far viewpoint distance of the emitter teleport (a regular spire; the main spire uses twice this): saved into the isolated emitter defaults.
func _set_far_viewpoint_km(km: float) -> void:
	var script: Script = load("res://scripts/ui/GameplayEmitterSiteSettings.cs")
	var res: Resource = script.new()
	res.set("FarViewpointDistanceKm", km)
	var path := fixture.path_join("emitter_tuning.tres")
	ResourceSaver.save(res, path)
	drawer.call("InstallEmitterSiteDefaults", path)
	_log("far viewpoint distance set to %.1f km" % km)


func _beam_status() -> String:
	return str(beams.call("DescribeStatus")) if beams != null else "no beam layer"


func _save_shot(label: String, img: Image) -> void:
	img.save_png(out_dir.path_join("%s.png" % label))
	var regions := _region_luma(img)
	results["luminance"][label] = regions
	_log("%s luma=%.5f top=%.5f bottom=%.5f hub_weight=%s | %s" % [label, regions["mean"], regions["top"], regions["bottom"], str(director.get("HubLightWeight")), _beam_status()])


func _run_visuals() -> void:
	if beams == null or not director.has_method("TimelineMarkTau"):
		_abort("no EclipseBeams layer or director hooks"); return
	_beam_settings(true, true)
	director.call("JumpToPhase", PHASE_DARKNESS)
	await _frames(4)
	results["info"]["marks"] = {"MainFire": _mark("MainFire"), "Kindled": _mark("Kindled"), "Ignition": _mark("Ignition"), "Crescendo": _mark("Crescendo"),
		"Handoff": _mark("Handoff"), "Sustain": _mark("Sustain"), "Implosion": _mark("Implosion"), "MainClimb": _mark("MainClimb")}
	_log("marks " + JSON.stringify(results["info"]["marks"]))
	if shots.has("s4"): await _shot4()
	if shots.has("s6"):
		await _shot6()
		await _shot6_fronts()
	if shots.has("s8"): await _shot8()
	if shots.has("s7"): await _shot7()
	if shots.has("storm"): await _shot_storm()
	if shots.has("s5"): await _shot5()
	if shots.has("s9"):
		await _shot9()
		await _shot9_beams()
		await _shot9_deck()
	if shots.has("gates"): await _run_gates()
	if shots.has("sky"): await _run_sky_sweep()
	_write()


## Shot 4: main fire from the main spire's Far pose.
func _shot4() -> void:
	await _go_named("main_far")
	var fire := _mark("MainFire")
	var climb := _mark("MainClimb")
	for step in [["s4_main_climb_mid", fire + climb * 0.6], ["s4_main_full_dark", _mark("Kindled") + 0.3], ["s4_main_sustain", _mark("Sustain") + 6.0]]:
		await _scrub(float(step[1]), 6.0)
		_save_shot(str(step[0]), await _shot_image())
		results["info"][str(step[0])] = {"tau": float(step[1]), "beams": _beam_status()}
	_write()


## Shot 6: the nearest regular spire firing, seen from its Far viewpoint.
func _shot6() -> void:
	await _go_named("main_far")
	var site := int(drawer.call("FindEmitterSite", "nearest"))
	if site < 0:
		_abort("no nearest regular emitter site"); return
	var fire := float(director.call("SiteFireTauSeconds", site))
	results["info"]["s6_site"] = {"site": site, "fire_tau": fire, "class": str(drawer.call("EmitterSiteClassName", site))}
	_log("s6 nearest regular site %d fires at tau %.2f" % [site, fire])
	if is_nan(fire):
		_abort("site %d has no fire time" % site); return
	_set_far_viewpoint_km(5.0)
	await _teleport_site(site, 2)
	_log("s6 teleport: %s" % str(drawer.get("EmitterTeleportMessage")))
	# Before the fire the spire is dark (only the main beam is up), then the beam climbs for RegularClimbSeconds, then it stands.
	for step in [["s6_before", fire - 2.0], ["s6_climb_mid", fire + 0.75], ["s6_complete", fire + 3.0], ["s6_complete_late", fire + 12.0]]:
		await _scrub(float(step[1]), 6.0)
		_save_shot(str(step[0]), await _shot_image())
		results["info"][str(step[0])] = {"tau": float(step[1]), "beams": _beam_status()}
	_write()


## Grid state for the E4d log lines: clock override, normal pulse weight, event fronts, head sprites.
func _grid_status() -> String:
	var grid := game.get_node_or_null("EmitterGrid")
	if grid == null: return "no grid"
	return "grid override=%s time=%.2f weight=%.2f events=%d fronts=%d heads=%d look=%.2f" % [str(grid.get("GridClockOverridden")), float(grid.get("PulseTimeSeconds")),
		float(grid.get("NormalPulseWeight")), int(grid.get("EventFrontCount")), int(grid.get("LiveFrontCount")), int(grid.get("HeadSpriteCount")), float(grid.get("EventWeight"))]


## Shot 6 (phase E4d): the ignition fronts climbing the arch both ways. The grid runs on the celestial clock inside the event, so a scrub to tau
## puts the 12 heads at v x (tau - Ignition) from the main spire, forward and backward; 150 km altitude and the ground, then the meeting.
func _shot6_fronts() -> void:
	var ign := _mark("Ignition")
	var met := _mark("FrontsMet")
	# alt_wide is 4,500 km above the main spire looking straight down: the two chains run left and right along the ring with the heads on them.
	for pose_name in ["ground_up", "alt_wide", "alt_arch"]:
		await _go(_pose(pose_name), 5.0)
		var steps := [["t25", ign + 25.0], ["t60", ign + 60.0], ["t110", ign + 110.0], ["met", met + 0.4]]
		if pose_name == "alt_arch": steps = [["t60", ign + 60.0]]
		if pose_name == "alt_wide": steps = [["t12", ign + 12.0], ["t25", ign + 25.0], ["t35", ign + 35.0]]
		for step in steps:
			var label := "s6_fronts_%s_%s" % [pose_name, str(step[0])]
			await _scrub(float(step[1]), 3.0)
			_save_shot(label, await _shot_image())
			results["info"][label] = {"tau": float(step[1]), "grid": _grid_status()}
			_log("%s %s" % [label, _grid_status()])
			if pose_name == "alt_wide":
				# The same instant without the hub beams, so the fronts read on their own.
				_beam_settings(false, true)
				await _settle(2.0)
				_save_shot(label + "_nobeams", await _shot_image())
				_beam_settings(true, true)
		_write()


## Shot 8 (phase E4d): the Sustain row flash. The nearest regular spire from 5 km at its refuel flash, and the row of six from the ground and 150 km.
func _shot8() -> void:
	# Tweaks T3: the Sustain refuel orb. The beam stays dark between orbs, so the orb is the only light on the beam. Launch, lift-off, mid-flight, arrival.
	await _go_named("main_far")
	var site := int(drawer.call("FindEmitterSite", "nearest"))
	if site < 0:
		_abort("no nearest regular emitter site"); return
	var sustain := _mark("Sustain")
	var launch := float(director.call("SiteRefuelTauSeconds", site, sustain + 120.0))
	var travel := 30.0
	results["info"]["s8_site"] = {"site": site, "launch_tau": launch, "sustain_tau": sustain, "travel_s": travel}
	_log("s8 nearest regular site %d orb launches at tau %.3f (Sustain %.2f), arrives at the hub %.1f s later" % [site, launch, sustain, travel])
	if is_nan(launch):
		_abort("site %d has no refuel time" % site); return
	_set_far_viewpoint_km(5.0)
	await _teleport_site(site, 2)
	var steps := [["s8_before", launch - 1.0], ["s8_launch", launch + 0.4], ["s8_lift1s", launch + 1.0], ["s8_lift3s", launch + 3.2], ["s8_lift6s", launch + 6.0], ["s8_gone", launch + 40.0]]
	for step in steps:
		await _scrub(float(step[1]), 2.5)
		_save_shot(str(step[0]), await _shot_image())
		results["info"][str(step[0])] = {"tau": float(step[1]), "beams": _beam_status()}
	# Mid-flight and arrival: from the ground looking up the beam, and from 150 km looking up at the arch (the orbs of a whole row converge on the hub).
	for pose_name in ["ground_up", "alt_arch"]:
		await _go(_pose(pose_name), 5.0)
		for step in [["age12", launch + 12.0], ["age22", launch + 22.0], ["arrive", launch + travel + 0.2], ["hold", launch + travel + 0.8]]:
			var label := "s8_%s_%s" % [pose_name, str(step[0])]
			await _scrub(float(step[1]), 2.5)
			_save_shot(label, await _shot_image())
			results["info"][label] = {"tau": float(step[1]), "beams": _beam_status(), "hub_weight": float(director.get("HubLightWeight"))}
	_write()


## Shot 7: full noon, all beams lit (Crescendo): the arch from the ground and from 150 km, with a no-beam reference.
func _shot7() -> void:
	var full := _mark("Crescendo") + 3.0
	for pose_name in ["ground_up", "ground_arch", "ground_zenith", "alt_arch", "alt_zenith"]:
		await _go(_pose(pose_name), 5.0)
		_beam_settings(true, true)
		await _scrub(full, 8.0)
		_save_shot("s7_%s_beams" % pose_name, await _shot_image())
		results["info"]["s7_%s" % pose_name] = {"tau": full, "beams": _beam_status()}
		_beam_settings(false, true)
		await _settle(3.0)
		_save_shot("s7_%s_nobeams" % pose_name, await _shot_image())
		_beam_settings(true, true)
		_write()


# ------------------------------------------------------------------ phase E4c: hub body, sky in-scatter, halo, far-sun colour

func _body() -> Node:
	return game.get_node_or_null("EclipseHubBody")


func _find_noon(times: Dictionary) -> float:
	if times.has("noon"): return float(times["noon"])
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
	return t_noon


## Screen position of the hub in image pixels (the body's HubDirection is the unit camera to hub direction of its last drawn frame).
func _hub_pixel(img: Image) -> Vector2i:
	var cam := root.get_camera_3d()
	var body := _body()
	var dir: Vector3 = body.get("HubDirection")
	var p := cam.unproject_position(cam.global_position + dir * 1000.0)
	var vs := root.get_visible_rect().size
	return Vector2i(int(p.x * img.get_width() / vs.x), int(p.y * img.get_height() / vs.y))


func _luma_px(c: Color) -> float:
	return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b


func _box_mean(img: Image, c: Vector2i, r: int) -> Color:
	var sr := 0.0; var sg := 0.0; var sb := 0.0; var n := 0
	for y in range(maxi(c.y - r, 0), mini(c.y + r + 1, img.get_height())):
		for x in range(maxi(c.x - r, 0), mini(c.x + r + 1, img.get_width())):
			var col := img.get_pixel(x, y)
			sr += col.r; sg += col.g; sb += col.b; n += 1
	return Color(sr / maxi(n, 1), sg / maxi(n, 1), sb / maxi(n, 1))


## Mean colour of the pixels on a circle of radius r around c (r = 0 is the centre pixel).
func _ring_mean(img: Image, c: Vector2i, r: int) -> Array:
	var w := img.get_width()
	var h := img.get_height()
	var sr := 0.0; var sg := 0.0; var sb := 0.0; var n := 0
	var steps := maxi(8, r * 6)
	for k in steps:
		var a := TAU * float(k) / float(steps)
		var x := clampi(c.x + int(round(cos(a) * r)), 0, w - 1)
		var y := clampi(c.y + int(round(sin(a) * r)), 0, h - 1)
		var col := img.get_pixel(x, y)
		sr += col.r; sg += col.g; sb += col.b; n += 1
	return [sr / n, sg / n, sb / n]


func _ring_luma(img: Image, c: Vector2i, r: int) -> float:
	var m: Array = _ring_mean(img, c, r)
	return _luma_px(Color(m[0], m[1], m[2]))


## Pixels (in image coordinates) from the centre outwards where the luma first falls below the level (the visible disc radius).
func _radius_below(img: Image, c: Vector2i, level: float) -> int:
	for r in range(0, 200):
		if _ring_luma(img, c, r) < level: return r
	return 200


func _patch_rgb(img: Image, rect: Rect2i) -> Color:
	var sr := 0.0; var sg := 0.0; var sb := 0.0; var n := 0
	for y in range(rect.position.y, rect.end.y):
		for x in range(rect.position.x, rect.end.x):
			var col := img.get_pixel(x, y)
			sr += col.r; sg += col.g; sb += col.b; n += 1
	return Color(sr / maxi(n, 1), sg / maxi(n, 1), sb / maxi(n, 1))


func _blue_red(c: Color) -> float:
	return c.b / maxf(c.r, 0.0001)


## Sky patches for the blue/red ratio: two side patches of the zenith view (left and right of the arch strip) or the top corners of the up-pose.
func _sky_patches(pose_name: String) -> Array:
	if pose_name == "ground_zenith":
		return [Rect2i(120, 330, 300, 240), Rect2i(1180, 330, 300, 240)]
	return [Rect2i(60, 20, 300, 160), Rect2i(1240, 20, 300, 160)]


func _far_sun_colors() -> Array:
	var out := []
	for n in ["FarRingSun", "FarRingRegionalSun"]:
		var light := game.find_child(n, true, false) as DirectionalLight3D
		out.append(light.light_color if light != null else Color(-1, -1, -1))
	return out


func _color_close(a: Color, b: Color, eps: float) -> bool:
	return absf(a.r - b.r) <= eps and absf(a.g - b.g) <= eps and absf(a.b - b.b) <= eps


func _gate(name: String, passed: bool, detail: String) -> void:
	results["gates"][name] = passed
	_log("GATE %s %s %s" % [name, "PASS" if passed else "FAIL", detail])
	if not passed: gate_failures.append(name)


## One shot of the hub at the given override level (the body on or off), after the exposure settles.
func _hub_frame(level: float, body_on: bool, settle_s := 8.0) -> Image:
	_configure({"HubBodyEnabled": body_on})
	director.call("SetHubLevelOverride", level)
	await _frames(3)
	await _settle(settle_s)
	return await _shot_image()


func _run_gates() -> void:
	if _body() == null:
		_abort("no EclipseHubBody node"); return
	var times: Dictionary = results["info"]["times"]
	var t_noon := _find_noon(times)
	_configure()
	var ratio_report := {}
	# 1. Sky blue/red: real noon (override off) against totality with the hub at L = 1, same poses.
	for pose_name in ["ground_zenith", "ground_up"]:
		await _go(_pose(pose_name), 5.0)
		celestial.call("SetSimulationSeconds", t_noon)
		var noon_img := await _hub_frame(-1.0, true, 8.0)
		noon_img.save_png(out_dir.path_join("gate_%s_realnoon.png" % pose_name))
		celestial.call("SetSimulationSeconds", float(times["totality"]))
		var hub_img := await _hub_frame(1.0, true, 8.0)
		hub_img.save_png(out_dir.path_join("gate_%s_hub100.png" % pose_name))
		var nobody_img := await _hub_frame(1.0, false, 4.0)
		nobody_img.save_png(out_dir.path_join("gate_%s_hub100_nobody.png" % pose_name))
		var noon_sum := Color(0, 0, 0)
		var hub_sum := Color(0, 0, 0)
		var patches := _sky_patches(pose_name)
		for rect in patches:
			var a := _patch_rgb(noon_img, rect)
			var b := _patch_rgb(hub_img, rect)
			noon_sum += a; hub_sum += b
		noon_sum = noon_sum / float(patches.size()); hub_sum = hub_sum / float(patches.size())
		var rn := _blue_red(noon_sum)
		var rh := _blue_red(hub_sum)
		ratio_report[pose_name] = {"noon_rgb": [noon_sum.r, noon_sum.g, noon_sum.b], "hub_rgb": [hub_sum.r, hub_sum.g, hub_sum.b], "noon_br": rn, "hub_br": rh, "fraction": rh / maxf(rn, 0.0001)}
		_gate("sky_ratio_" + pose_name, rh >= SKY_RATIO_MIN_FRACTION * rn,
			"hub b/r %.3f vs real noon b/r %.3f (%.1f%%, need %.0f%%) noon rgb=(%.3f %.3f %.3f) hub rgb=(%.3f %.3f %.3f)" % [rh, rn, 100.0 * rh / maxf(rn, 0.0001),
			100.0 * SKY_RATIO_MIN_FRACTION, noon_sum.r, noon_sum.g, noon_sum.b, hub_sum.r, hub_sum.g, hub_sum.b])
		if pose_name == "ground_zenith":
			# 2. The disc at L = 1 (still the zenith pose, hub image above).
			var c := _hub_pixel(hub_img)
			var center := _box_mean(hub_img, c, 1)
			var center_luma := _luma_px(center)
			var ring := _ring_luma(hub_img, c, DISC_RING_PX)
			var ring_nobody := _ring_luma(nobody_img, c, DISC_RING_PX)
			var radius := _radius_below(hub_img, c, 0.9)
			results["strip"]["disc_L100"] = {"pixel": [c.x, c.y], "center_rgb": [center.r, center.g, center.b], "center_luma": center_luma, "ring24": ring, "ring24_body_off": ring_nobody, "radius_px_above_0.9": radius,
				"body": str(_body().call("DescribeStatus"))}
			_gate("disc_center_L100", center_luma >= DISC_CENTER_MIN, "centre luma %.4f rgb=(%.3f %.3f %.3f) at %s (need %.2f); %s" % [center_luma, center.r, center.g, center.b, str(c), DISC_CENTER_MIN, str(_body().call("DescribeStatus"))])
			_gate("disc_contrast_L100", center_luma - ring >= DISC_CONTRAST_MIN, "centre %.4f - ring@%dpx %.4f = %.4f (need %.2f); ring without the body %.4f; white radius %d px" % [center_luma, DISC_RING_PX, ring, center_luma - ring, DISC_CONTRAST_MIN, ring_nobody, radius])
			# 3. The kindle (L = 0.1) is still a small white point.
			celestial.call("SetSimulationSeconds", float(times["totality"]))
			var k_img := await _hub_frame(0.1, true, 8.0)
			k_img.save_png(out_dir.path_join("gate_ground_zenith_hub010.png"))
			var kc := _hub_pixel(k_img)
			var k_center := _box_mean(k_img, kc, 1)
			var k_luma := _luma_px(k_center)
			var k_ring := _ring_luma(k_img, kc, DISC_RING_PX)
			var k_radius := _radius_below(k_img, kc, 0.9)
			results["strip"]["disc_L010"] = {"pixel": [kc.x, kc.y], "center_rgb": [k_center.r, k_center.g, k_center.b], "center_luma": k_luma, "ring24": k_ring, "radius_px_above_0.9": k_radius}
			_gate("disc_kindle_L010", k_luma >= DISC_KINDLE_CENTER_MIN and k_radius <= 12, "centre luma %.4f rgb=(%.3f %.3f %.3f) (need %.2f); ring@%dpx %.4f; radius above 0.9 = %d px (small point <= 12)" % [k_luma, k_center.r, k_center.g, k_center.b, DISC_KINDLE_CENTER_MIN, DISC_RING_PX, k_ring, k_radius])
	results["strip"]["sky_ratio"] = ratio_report
	# 4. Far suns: hub weight 0 exactly white, hub weight 1 the hub colour.
	director.call("SetHubLevelOverride", -1.0)
	celestial.call("SetSimulationSeconds", t_noon)
	await _frames(6)
	var off_colors := _far_sun_colors()
	director.call("SetHubLevelOverride", 1.0)
	await _frames(6)
	var on_colors := _far_sun_colors()
	var hub_color: Color = director.get("HubLightColor")
	var white_ok := true
	var mirror_ok := true
	var checked := 0
	for col in off_colors:
		if col.r < 0.0: continue   # the regional far sun does not exist in this world
		checked += 1
		white_ok = white_ok and col == Color(1, 1, 1, 1)
	for col in on_colors:
		if col.r < 0.0: continue
		mirror_ok = mirror_ok and _color_close(col, hub_color, 0.002)
	white_ok = white_ok and checked > 0
	_gate("far_sun_white_at_weight0", white_ok, "far suns %s (must be exactly (1, 1, 1, 1))" % str(off_colors))
	_gate("far_sun_hub_colour_at_weight1", mirror_ok, "far suns %s vs hub colour %s" % [str(on_colors), str(hub_color)])
	# 5. Occlusion at the zenith pose, hub L = 1 and totality: the body's contribution at the disc centre (body on minus body off, same scene).
	celestial.call("SetSimulationSeconds", float(times["totality"]))
	await _go(_pose("ground_zenith"), 5.0)
	var clear_on := await _hub_frame(1.0, true, 8.0)
	var clear_off := await _hub_frame(1.0, false, 4.0)
	var cc := _hub_pixel(clear_on)
	var clear_gain := _luma_px(_box_mean(clear_on, cc, 1)) - _luma_px(_box_mean(clear_off, cc, 1))
	_gate("occlusion_clear_sky_baseline", clear_gain >= 0.15, "body contribution at the disc centre in clear sky %.4f (must be visible: >= 0.15)" % clear_gain)
	# overhead geometry: a dark slab 150 m above the camera along the hub direction
	var cam := root.get_camera_3d()
	var dir: Vector3 = _body().get("HubDirection")
	var slab := MeshInstance3D.new()
	var quad := QuadMesh.new(); quad.size = Vector2(600.0, 600.0)
	slab.mesh = quad
	var slab_mat := StandardMaterial3D.new()
	slab_mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	slab_mat.albedo_color = Color(0.18, 0.18, 0.2)
	slab_mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	slab.material_override = slab_mat
	game.add_child(slab)
	slab.global_position = cam.global_position + dir * 150.0
	slab.look_at(cam.global_position, Vector3.UP if absf(dir.dot(Vector3.UP)) < 0.99 else Vector3.RIGHT)
	var slab_on := await _hub_frame(1.0, true, 6.0)
	var slab_off := await _hub_frame(1.0, false, 4.0)
	slab_on.save_png(out_dir.path_join("gate_occlusion_slab_on.png"))
	slab_off.save_png(out_dir.path_join("gate_occlusion_slab_off.png"))
	var sc := _hub_pixel(slab_on)
	var slab_gain := _luma_px(_box_mean(slab_on, sc, 1)) - _luma_px(_box_mean(slab_off, sc, 1))
	_gate("occlusion_geometry", slab_gain <= 0.02, "body contribution behind an overhead slab %.4f (must be <= 0.02); centre luma on %.4f off %.4f" % [slab_gain, _luma_px(_box_mean(slab_on, sc, 1)), _luma_px(_box_mean(slab_off, sc, 1))])
	slab.queue_free()
	await _frames(3)
	# clouds: a storm front centred on the camera
	if bridge != null:
		atmosphere.set("storm_auto_exposure_enabled", false)
		bridge.set("SpawnDistanceMeters", 0.0)
		bridge.call("SpawnStormFrontAhead")
		await _settle(14.0)
		var storm_on := await _hub_frame(1.0, true, 6.0)
		var storm_off := await _hub_frame(1.0, false, 4.0)
		storm_on.save_png(out_dir.path_join("gate_occlusion_storm_on.png"))
		storm_off.save_png(out_dir.path_join("gate_occlusion_storm_off.png"))
		var stc := _hub_pixel(storm_on)
		var storm_gain := _luma_px(_box_mean(storm_on, stc, 1)) - _luma_px(_box_mean(storm_off, stc, 1))
		_gate("occlusion_clouds", storm_gain <= OCCLUDED_CONTRIBUTION_MAX, "body contribution under a storm deck %.4f (must be <= %.2f); centre luma on %.4f off %.4f" % [storm_gain, OCCLUDED_CONTRIBUTION_MAX, _luma_px(_box_mean(storm_on, stc, 1)), _luma_px(_box_mean(storm_off, stc, 1))])
		bridge.call("ClearSpawnedWeather")
		atmosphere.set("storm_auto_exposure_enabled", true)
	director.call("SetHubLevelOverride", -1.0)
	_configure()
	_write()


## Tuning aid: the hub-lit sky blue/red ratio against real noon for each --sweep config (configs separated by |, each "Key:value,Key:value").
func _run_sky_sweep() -> void:
	var times: Dictionary = results["info"]["times"]
	var t_noon := _find_noon(times)
	var configs: Array = Array(sweep.split("|", false)) if sweep != "" else [""]
	for pose_name in ["ground_zenith", "ground_up"]:
		await _go(_pose(pose_name), 5.0)
		celestial.call("SetSimulationSeconds", t_noon)
		_configure()
		var noon_img := await _hub_frame(-1.0, true, 8.0)
		var patches := _sky_patches(pose_name)
		var noon_sum := Color(0, 0, 0)
		for rect in patches: noon_sum += _patch_rgb(noon_img, rect)
		noon_sum = noon_sum / float(patches.size())
		_log("SWEEP %s noon rgb=(%.3f %.3f %.3f) b/r=%.3f" % [pose_name, noon_sum.r, noon_sum.g, noon_sum.b, _blue_red(noon_sum)])
		celestial.call("SetSimulationSeconds", float(times["totality"]))
		var idx := 0
		for cfg in configs:
			var extra := {}
			for pair in String(cfg).split(",", false):
				var kv := pair.split(":", false)
				if kv.size() == 2: extra[kv[0]] = float(kv[1])
			_configure(extra)
			director.call("SetHubLevelOverride", 1.0)
			await _frames(3)
			await _settle(8.0)
			var img := await _shot_image()
			img.save_png(out_dir.path_join("sweep_%s_%d.png" % [pose_name, idx]))
			var hub_sum := Color(0, 0, 0)
			for rect in patches: hub_sum += _patch_rgb(img, rect)
			hub_sum = hub_sum / float(patches.size())
			var c := _hub_pixel(img)
			var center := _luma_px(_box_mean(img, c, 1))
			var ring := _ring_luma(img, c, DISC_RING_PX)
			_log("SWEEP %s cfg[%d]=%s hub rgb=(%.3f %.3f %.3f) b/r=%.3f fraction=%.1f%% centre=%.3f ring24=%.3f contrast=%.3f" % [pose_name, idx, cfg, hub_sum.r, hub_sum.g, hub_sum.b, _blue_red(hub_sum),
				100.0 * _blue_red(hub_sum) / maxf(_blue_red(noon_sum), 0.0001), center, ring, center - ring])
			idx += 1
	director.call("SetHubLevelOverride", -1.0)
	_configure()


## Shot 5: kindle overhead (the event, looking at the zenith from the ground).
func _shot5() -> void:
	await _go(_pose("ground_zenith"), 5.0)
	_configure()
	for step in [["s5_kindle_overhead", _mark("Kindled") + 0.3], ["s5_before_kindle", _mark("MainFire") + 1.0]]:
		await _scrub(float(step[1]), 6.0)
		_save_shot(str(step[0]), await _shot_image())
		results["info"][str(step[0])] = {"tau": float(step[1]), "body": str(_body().call("DescribeStatus"))}
	_write()


## Shot 9: cloudy noon. A storm front centred on the camera hides the disc from the ground; from 9 km the clouds are below (hub-lit tops).
func _shot9() -> void:
	if bridge == null:
		_abort("no SunshineCloudBridge"); return
	var full := _mark("Crescendo") + 3.0
	_configure()
	await _go(_pose("ground_zenith"), 5.0)
	atmosphere.set("storm_auto_exposure_enabled", false)
	bridge.set("SpawnDistanceMeters", 0.0)
	bridge.call("SpawnStormFrontAhead")
	await _settle(14.0)
	await _scrub(full, 8.0)
	_save_shot("s9_ground_under_deck", await _shot_image())
	bridge.set("SpawnDistanceMeters", 12000.0)
	bridge.call("SpawnStormFrontAhead")
	await _settle(12.0)
	await _go(_pose("alt_down"), 6.0)
	await _scrub(full, 8.0)
	_save_shot("s9_alt9km_tops", await _shot_image())
	bridge.call("ClearSpawnedWeather")
	atmosphere.set("storm_auto_exposure_enabled", true)
	_write()


## Shot 9 (phase E4e): the beams through the cloud. The nearest regular spire from 5 km with a storm front spawned over it: in-cloud diffusion on (the halo
## widens and brightens over the deck), diffusion off, the depth core cut inside dense cloud (CloudCoreCut 0.5), halo only and beams off; then the same from 10.5 km up (above the deck).
func _shot9_beams() -> void:
	if bridge == null:
		_abort("no SunshineCloudBridge"); return
	await _go_named("main_far")
	var site := int(drawer.call("FindEmitterSite", "nearest"))
	var fire := float(director.call("SiteFireTauSeconds", site))
	if site < 0 or is_nan(fire):
		_abort("s9 beams: no nearest regular emitter site"); return
	_set_far_viewpoint_km(5.0)
	await _teleport_site(site, 2)
	_configure()
	atmosphere.set("storm_auto_exposure_enabled", false)
	# The storm centre is placed 12 km and 6.5 km ahead of the camera (the spire is 5 km ahead): behind the spire and around it.
	for spawn in [["d12", 12000.0], ["d6", 6500.0]]:
		bridge.call("ClearSpawnedWeather")
		bridge.set("SpawnDistanceMeters", float(spawn[1]))
		bridge.call("SpawnStormFrontAhead")
		await _settle(14.0)
		await _scrub(fire + 4.0, 3.0)
		beams.call("ResetCloudQueries")
		await _settle(2.0)
		var beam := int(beams.call("BeamOfSite", site))
		var cloud: Color = beams.call("GetCloudMirror", beam)
		var info := {"site": site, "beam": beam, "fire_tau": fire, "cloud_k": cloud.r, "deck_low_above_crown": cloud.g, "deck_high_above_crown": cloud.b, "queried": cloud.a,
			"weather_queries": int(beams.get("WeatherQueryCount")), "colour_writes": int(beams.get("ColorWriteCount"))}
		results["info"]["s9_beam_cloud_" + str(spawn[0])] = info
		_log("s9 beams %s %s" % [str(spawn[0]), JSON.stringify(info)])
		for step in [["diffusion", {}], ["off", {"BeamCloudDiffusionEnabled": false}], ["cut", {"BeamCloudCoreCut": 0.5}], ["halo_only", {"BeamDepthWriteCore": false}],
				["beams_off", {"BeamsEnabled": false}]]:
			_configure(step[1])
			await _settle(3.0)
			_save_shot("s9_beam_cloud_%s_%s" % [str(spawn[0]), str(step[0])], await _shot_image())
		_configure()
	# From above the deck: the camera 10.5 km up, 5 km from the spire, looking down at it (cloud tops with the beam rising out of them).
	var here := _pose_now()
	here["vertical"] = float(here["vertical"]) + 10500.0
	here["pitch"] = -28.0
	await _go(here, 6.0)
	await _scrub(fire + 4.0, 3.0)
	for step in [["diffusion", {}], ["off", {"BeamCloudDiffusionEnabled": false}], ["cut", {"BeamCloudCoreCut": 0.5}], ["beams_off", {"BeamsEnabled": false}]]:
		_configure(step[1])
		await _settle(3.0)
		_save_shot("s9_beam_cloud_alt_%s" % str(step[0]), await _shot_image())
	_configure()
	bridge.call("ClearSpawnedWeather")
	bridge.set("SpawnDistanceMeters", 12000.0)
	atmosphere.set("storm_auto_exposure_enabled", true)
	_write()


## Shot 9 (phase E4e), clear sky: a forced cloud deck at the spire (coverage 0.9, altitudes 2.5-6 km) so the in-cloud widening is visible without
## Sunshine clouds in front of it. Diffusion on against off (and the cut at 0.5), the nearest spire from 5 km at the end of its climb.
func _shot9_deck() -> void:
	await _go_named("main_far")
	var site := int(drawer.call("FindEmitterSite", "nearest"))
	var fire := float(director.call("SiteFireTauSeconds", site))
	if site < 0 or is_nan(fire):
		_abort("s9 deck: no nearest regular emitter site"); return
	_set_far_viewpoint_km(5.0)
	await _teleport_site(site, 2)
	_configure()
	beams.call("SetForcedDeck", 0.9, 2500.0, 6000.0)
	await _scrub(fire + 4.0, 3.0)
	beams.call("ResetCloudQueries")
	await _settle(2.0)
	var cloud: Color = beams.call("GetCloudMirror", int(beams.call("BeamOfSite", site)))
	results["info"]["s9_deck"] = {"k": cloud.r, "low": cloud.g, "high": cloud.b, "queried": cloud.a}
	_log("s9 forced deck cloud data k=%.2f low=%.0f high=%.0f queried=%.0f" % [cloud.r, cloud.g, cloud.b, cloud.a])
	for step in [["diffusion", {}], ["off", {"BeamCloudDiffusionEnabled": false}], ["wide", {"BeamCloudWidening": 6.0, "BeamCloudHaloBoost": 2.0}], ["beams_off", {"BeamsEnabled": false}]]:
		_configure(step[1])
		await _settle(3.0)
		_save_shot("s9_deck_%s" % str(step[0]), await _shot_image())
	_configure()
	beams.call("SetForcedDeck", -1.0, 0.0, 0.0)
	beams.call("ResetCloudQueries")
	_write()


## World position of a hub beam's crown, read fresh each time (a teleport can move the floating origin).
func _beam_crown_world(beam: int) -> Vector3:
	return beams.global_transform * (beams.call("GetBeamTransform", beam) as Transform3D).origin


## Turns the camera (yaw only) until the beam's crown sits on the screen's centre column. The explorer's yaw sign is found by trial.
func _aim_at_beam_crown(beam: int) -> void:
	var sign := 1.0
	for i in 4:
		var world := _beam_crown_world(beam)
		var cam := root.get_viewport().get_camera_3d()
		if cam == null or cam.is_position_behind(world): return
		var size := root.get_viewport().get_visible_rect().size
		var focal := 0.5 * size.y / tan(deg_to_rad(cam.fov) * 0.5)
		var dx := cam.unproject_position(world).x - 0.5 * size.x
		if absf(dx) < 2.0: return
		var p := _pose_now()
		p["yaw"] = float(p["yaw"]) - sign * rad_to_deg(atan(dx / focal))
		p["flight"] = true
		await _go(p, 1.0)
		var after := root.get_viewport().get_camera_3d().unproject_position(_beam_crown_world(beam)).x - 0.5 * size.x
		if absf(after) > absf(dx): sign = -sign


func _refresh_count() -> int:
	if clouds_res == null: return -1
	return int(clouds_res.get("temporal_history_explicit_refresh_request_count"))


func _masked_mean(img: Image, mask: PackedByteArray) -> float:
	var w := img.get_width()
	var sum := 0.0
	var n := 0
	for i in mask.size():
		if mask[i] == 0: continue
		var c := img.get_pixel(i % w, i / w)
		sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b
		n += 1
	return sum / maxf(n, 1)


## Screen row of the higher of the spire crown and the cloud deck base on a spire's beam (the in-cloud data green channel is the base in metres above the
## beam origin; the beam is within 1.6 degrees of the vertical, so the point is taken along the beam). Rows above it (smaller y) are the beam inside the deck and above the crown.
func _deck_base_row(site: int, cloud: Color, image_height: int) -> int:
	var beam := int(beams.call("BeamOfSite", site))
	var t: Transform3D = beams.global_transform * (beams.call("GetBeamTransform", beam) as Transform3D)
	var above := maxf(maxf(cloud.g, 0.0), float(beams.call("CrownHeightAboveOrigin", beam)))
	var point := t.origin + t.basis.x.normalized() * above
	var cam := root.get_viewport().get_camera_3d()
	if cam.is_position_behind(point): return 0
	return clampi(int(cam.unproject_position(point).y), 0, image_height)


## The T2 check on the real hub beams (E4 plan): a beam in front of cloud recovers at least STORM_CORE_GAIN_MIN x the halo-only light with the depth core on,
## without any cloud refresh while lit.
func _shot_storm() -> void:
	if bridge == null:
		_abort("no SunshineCloudBridge"); return
	# A regular spire 3 km ahead (the T2 pillar was 3 km ahead): the storm front spawns 12 km ahead of the camera, so its clouds are mostly behind the beam.
	await _go_named("main_far")
	var site := int(drawer.call("FindEmitterSite", "nearest"))
	var fire := float(director.call("SiteFireTauSeconds", site))
	if site < 0 or is_nan(fire):
		_abort("storm: no nearest regular emitter site"); return
	_set_far_viewpoint_km(3.0)
	await _teleport_site(site, 2)
	_log("storm teleport: %s (site %d fires at tau %.2f)" % [str(drawer.get("EmitterTeleportMessage")), site, fire])
	if storm_side_offset != 0.0:
		# The Far viewpoint stands on the ring line through the spire, i.e. right under the spire's relay link toward the camera: during the
		# ignition that link is plateau-lit (E4d) and, seen end-on, projects as a vertical bright line from the crown up through the screen
		# column of the hub beam (with its own depth core). Step sideways so the link slants away from the beam, then aim at the crown again.
		await _scrub(_mark("Crescendo") + 3.0, 2.0)
		var beam := int(beams.call("BeamOfSite", site))
		if beam < 0:
			_abort("storm: site %d has no hub beam" % site); return
		var p := _pose_now()
		p["across"] = float(p["across"]) + storm_side_offset
		p["vertical"] = float(p["vertical"]) + 2.0
		p["flight"] = true
		await _go(p, 2.0)
		await _aim_at_beam_crown(beam)
		_log("storm pose stepped %.0f m sideways: crown at screen %s" % [storm_side_offset, str(root.get_viewport().get_camera_3d().unproject_position(_beam_crown_world(beam)))])
	atmosphere.set("storm_auto_exposure_enabled", false)
	if storm_distance >= 0.0: bridge.set("SpawnDistanceMeters", storm_distance)
	if hide_grid and game.get_node_or_null("EmitterGrid") != null: game.get_node("EmitterGrid").set("visible", false)
	bridge.call("SpawnStormFrontAhead")
	await _settle(30.0)
	# Phase E4e: the spire's weather was queried once, before the storm existed; ask again so the in-cloud diffusion and the depth core cut see the storm.
	await _scrub(_mark("Crescendo") + 3.0, 3.0)
	beams.call("ResetCloudQueries")
	await _settle(2.0)
	var storm_cloud: Color = beams.call("GetCloudMirror", int(beams.call("BeamOfSite", site)))
	_log("storm cloud data of the spire: k=%.3f low=%.0f high=%.0f queried=%.0f queries=%d" % [storm_cloud.r, storm_cloud.g, storm_cloud.b, storm_cloud.a, int(beams.get("WeatherQueryCount"))])
	results["info"]["storm_cloud"] = {"k": storm_cloud.r, "low": storm_cloud.g, "high": storm_cloud.b, "queried": storm_cloud.a}
	for state in [["noon", _mark("Crescendo") + 3.0], ["dark", fire + 3.0]]:
		var tag := str(state[0])
		await _scrub(float(state[1]), 3.0)
		_beam_settings(false, true)
		await _settle(2.5)
		var base := await _shot_image()
		base.save_png(out_dir.path_join("storm_%s_base.png" % tag))
		var refresh_before := _refresh_count()
		# Phase E4e: the T2 check runs with the in-cloud diffusion OFF (the original mechanism: a depth core recovers the light the clouds behind a beam
		# take). The default configuration (diffusion ON, the core cut inside dense cloud) replaces the core in the deck with the widened halo, so it is
		# judged by how much of that core light it keeps.
		_configure({"BeamsEnabled": true, "BeamDepthWriteCore": false, "BeamCloudDiffusionEnabled": false})
		await _settle(2.5)
		var nocore := await _shot_image()
		nocore.save_png(out_dir.path_join("storm_%s_halo_only.png" % tag))
		_configure({"BeamsEnabled": true, "BeamDepthWriteCore": true, "BeamCloudDiffusionEnabled": false})
		await _settle(2.5)
		var core := await _shot_image()
		core.save_png(out_dir.path_join("storm_%s_core.png" % tag))
		await _settle(2.0)
		var core2 := await _shot_image()
		core2.save_png(out_dir.path_join("storm_%s_core2.png" % tag))
		_configure({"BeamsEnabled": true, "BeamDepthWriteCore": true})
		await _settle(2.5)
		var diffuse := await _shot_image()
		diffuse.save_png(out_dir.path_join("storm_%s_diffusion_default.png" % tag))
		_configure({"BeamsEnabled": true, "BeamDepthWriteCore": false})
		await _settle(2.5)
		var diffuse_halo := await _shot_image()
		diffuse_halo.save_png(out_dir.path_join("storm_%s_diffusion_halo_only.png" % tag))
		_configure({"BeamsEnabled": true, "BeamDepthWriteCore": true, "BeamCloudCoreCut": 0.5})
		await _settle(2.5)
		var diffuse_nocut := await _shot_image()
		diffuse_nocut.save_png(out_dir.path_join("storm_%s_diffusion_cut05.png" % tag))
		var refresh_after := _refresh_count()
		var w := base.get_width()
		var h := base.get_height()
		var mask := PackedByteArray()
		mask.resize(w * h)
		mask.fill(0)
		var count := 0
		var others := [core, nocore, diffuse, diffuse_halo, diffuse_nocut]
		var deck_row := _deck_base_row(site, storm_cloud, h)
		_log("STORM %s in-deck rows: y < %d of %d (deck base %.0f m above the beam origin, crown %.0f m)" % [tag, deck_row, h, storm_cloud.g, float(beams.call("CrownHeightAboveOrigin", int(beams.call("BeamOfSite", site))))])
		for y in mini(deck_row, h):
			for x in w:
				var b := base.get_pixel(x, y)
				var d := 0.0
				for img in others:
					var a: Color = img.get_pixel(x, y)
					d = maxf(d, maxf(maxf(absf(a.r - b.r), absf(a.g - b.g)), absf(a.b - b.b)))
				if d > STORM_MASK_THRESHOLD:
					mask[y * w + x] = 1
					count += 1
		var m_base := _masked_mean(base, mask)
		var m_nocore := _masked_mean(nocore, mask)
		var m_core := _masked_mean(core, mask)
		var m_core2 := _masked_mean(core2, mask)
		var m_diffuse := _masked_mean(diffuse, mask)
		var m_diffuse_halo := _masked_mean(diffuse_halo, mask)
		var m_diffuse_nocut := _masked_mean(diffuse_nocut, mask)
		var gain_halo := m_nocore - m_base
		var gain_core := m_core - m_base
		var gain_diffuse := m_diffuse - m_base
		# The halo-only gain is about 0 when the clouds hide the beam completely (it can even be slightly negative from cloud noise), so the ratio is
		# taken against max(halo gain, STORM_NOISE_FLOOR): the core must recover at least STORM_CORE_GAIN_MIN x that.
		var ratio := gain_core / maxf(gain_halo, STORM_NOISE_FLOOR)
		# The default (diffusion on, no core cut) must keep at least STORM_DIFFUSION_KEEP_MIN of the old core light; the cut at 0.5 is reported for the decision.
		var keep := gain_diffuse / maxf(gain_core, STORM_NOISE_FLOOR)
		var rec := {"mask_pixels": count, "base": m_base, "halo_only": m_nocore, "core": m_core, "core2": m_core2, "recovery_ratio": ratio,
			"diffusion_default": m_diffuse, "diffusion_halo_only": m_diffuse_halo, "diffusion_cut05": m_diffuse_nocut, "diffusion_keep_of_core": keep,
			"gain_halo": gain_halo, "gain_core": gain_core, "gain_diffusion_default": gain_diffuse, "gain_diffusion_halo_only": m_diffuse_halo - m_base,
			"gain_diffusion_cut05": m_diffuse_nocut - m_base, "refresh_before": refresh_before, "refresh_after": refresh_after, "beams": _beam_status()}
		results["strip"]["storm_" + tag] = rec
		_log("STORM %s %s" % [tag, JSON.stringify(rec)])
		var passed := count > 200 and ratio >= STORM_CORE_GAIN_MIN and refresh_before == refresh_after
		results["gates"]["storm_" + tag] = passed
		_log("STORM %s %s core recovery %.2fx (min %.1fx, diffusion off) mask_px=%d refreshes %d->%d" % [tag, "PASS" if passed else "FAIL", ratio, STORM_CORE_GAIN_MIN, count, refresh_before, refresh_after])
		if not passed: gate_failures.append("storm_" + tag)
		var kept := keep >= STORM_DIFFUSION_KEEP_MIN
		results["gates"]["storm_diffusion_" + tag] = kept
		_log("STORM %s %s diffusion default keeps %.2f of the core light (min %.2f); halo gain %.4f core gain %.4f default gain %.4f" % [tag, "PASS" if kept else "FAIL", keep, STORM_DIFFUSION_KEEP_MIN, gain_halo, gain_core, gain_diffuse])
		if not kept: gate_failures.append("storm_diffusion_" + tag)
	_beam_settings(true, true)
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
		await _teleport_site(site, vp)
		return
	await _go(_pose(pose_name), 5.0)


## Emitter site teleport (viewpoint 0 entry, 1 gallery, 2 far, 3 high) and the landing wait.
func _teleport_site(site: int, vp: int) -> void:
	if site < 0 or not bool(drawer.call("RequestEmitterTeleport", site, vp)):
		_abort("emitter teleport refused for site %d viewpoint %d" % [site, vp]); return
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


func _pose(name: String) -> Dictionary:
	var p := base_pose.duplicate()
	p["flight"] = true
	match name:
		"ground_zenith": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 89.0
		"alt_zenith": p["vertical"] = float(base_pose["vertical"]) + 150000.0; p["pitch"] = 89.0
		"ground_horizon": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 2.0
		"ground_up": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 28.0
		"ground_arch": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 70.0
		"alt_down": p["vertical"] = float(base_pose["vertical"]) + 9000.0; p["pitch"] = -25.0
		"alt_arch": p["vertical"] = float(base_pose["vertical"]) + 150000.0; p["pitch"] = 60.0
		"alt_wide": p["vertical"] = float(base_pose["vertical"]) + 4500000.0; p["pitch"] = -89.0
	return p


func _set_darkness(enabled: bool) -> void:
	_configure({"EclipseDarknessEnabled": enabled})


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
