extends SceneTree
## B7a capture determinism probe (docs/test-speed-plan.md 3.8). Windowed GPU tool, not --headless. One boot answers: with the existing freeze applied
## (tree_visibility_world freeze_world, freeze_noise clouds+taa, hold_wind every frame, clocks held) and NOTHING hidden, which layers make consecutive
## frames differ. Per pose it measures frame N against N+1 and N+30 for a baseline, then for one layer switched off or held at a time (restored after),
## then for a cumulative ladder of holds. Boots the REAL start menu on an isolated profile and presses Continue (tree_visibility_world.boot_game).
## In-engine it records byte equality and Image.compute_image_metrics (max / mean channel difference); frames that differ are written as raw RGBA8
## to --raw=<dir> for offline pixel counts and heatmaps. Writes <out>/probe_report.json. Read-only on game state except the switches it restores.
## Args (after --): --out=<abs dir> [--raw=<abs dir>] [--poses=save_spot,forest_interior_eye,plains_eye,coast_eye] [--warm=20] [--plan=clouds|residual]
## --plan=clouds runs the cloud-focused second pass (bridge-held cloud switches, fixed dither index, no history rejection, root-level nodes).
## Verdict: CAPTURE_DETERMINISM_PROBE=PASS when every pose was measured (it reports, it does not judge), =FAIL <reason> otherwise.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const TVW := preload("res://tools/tree_visibility_world.gd")
const PROBE := preload("res://tools/PerfBenchProbe.cs")
const MARKER := "CAPTURE_DETERMINISM_PROBE"
const WATCHDOG_S := 2700.0
const POSE_TIMES := {"coast_eye": {"mode": "elevation", "value": 8.0, "rising": false}}

var args := {}
var out_dir := ""
var raw_dir := ""
var warm := 20
var t_start := 0
var stage := "init"
var report := {"poses": [], "errors": [], "events": []}
var game: Node
var explorer: Node
var surface: Node
var probe
var clouds: Object
var bridge: Node
var far: Node
var env: Environment
var enforced: Array = []      # [object, property, value] re-applied every process frame (the game re-asserts some of them)
var saved: Array = []         # [object, property, original] restored in reverse order
var hold_wind_on := true
var finished := false


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			if eq > 0:
				args[text.substr(2, eq - 2)] = text.substr(eq + 1)
			else:
				args[text.substr(2)] = "1"
	call_deferred("_run")


func _log(text: String) -> void:
	var line := "B7A %7.1fs %s" % [float(Time.get_ticks_msec() - t_start) / 1000.0, text]
	print(line)
	report["events"].append(line)


func _heartbeat() -> void:
	if finished:
		return
	print("B7A %7.1fs heartbeat stage=%s frames=%d" % [float(Time.get_ticks_msec() - t_start) / 1000.0, stage, Engine.get_frames_drawn()])
	create_timer(20.0, true, false, true).timeout.connect(_heartbeat)


func _write() -> void:
	var file := FileAccess.open(out_dir.path_join("probe_report.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(report, " "))
		file.close()


func _finish(code: int, reason: String) -> void:
	if finished:
		return
	finished = true
	Engine.time_scale = 1.0
	if reason != "":
		report["errors"].append(reason)
	_write()
	if code == 0:
		print("%s=PASS" % MARKER)
	else:
		print("%s=FAIL %s" % [MARKER, reason])
	quit(code)


func _run() -> void:
	t_start = Time.get_ticks_msec()
	if not TVW.profile_is_isolated():
		push_error("Refusing to run: user:// (%s) is not an isolated test profile." % ProjectSettings.globalize_path("user://"))
		print("%s=FAIL not an isolated profile" % MARKER)
		quit(2)
		return
	out_dir = String(args.get("out", ""))
	if out_dir == "":
		print("%s=FAIL --out missing" % MARKER)
		quit(2)
		return
	raw_dir = String(args.get("raw", out_dir.path_join("raw")))
	warm = int(args.get("warm", "20"))
	DirAccess.make_dir_recursive_absolute(out_dir)
	DirAccess.make_dir_recursive_absolute(raw_dir)
	create_timer(WATCHDOG_S, true, false, true).timeout.connect(func(): _finish(4, "watchdog %d s at stage %s" % [int(WATCHDOG_S), stage]))
	_heartbeat()
	var spec: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://tools/fixtures/perf_bench_scenarios.json"))
	var pose_names := String(args.get("poses", "save_spot,forest_interior_eye,plains_eye,coast_eye")).split(",", false)
	for p in pose_names:
		if not spec["poses"].has(p):
			_finish(2, "unknown pose %s" % p)
			return
	probe = PROBE.new()
	report["machine"] = {"adapter": RenderingServer.get_video_adapter_name(), "engine": Engine.get_version_info().get("string", ""),
		"user_dir": ProjectSettings.globalize_path("user://")}
	_log("adapter=%s poses=%s" % [report["machine"]["adapter"], ",".join(pose_names)])
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	stage = "boot"
	var err := []
	game = await TVW.boot_game(self, err)
	if game == null:
		_finish(3, "boot failed: %s" % (err[0] if not err.is_empty() else "unknown"))
		return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	bridge = game.get_node_or_null("SunshineCloudBridge")
	far = game.get_node_or_null("ScaledFarViewport")
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	clouds = driver.get("clouds_resource") if driver != null else null
	var env_node := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	env = env_node.environment if env_node != null else null
	if explorer == null or surface == null:
		_finish(3, "Explorer or Terrain3DGameplay missing")
		return
	_log("booted; clouds=%s bridge=%s far=%s env=%s" % [str(clouds != null), str(bridge != null), str(far != null), str(env != null)])
	process_frame.connect(_on_process_frame)
	for p in pose_names:
		var pose: Dictionary = spec["poses"][p]
		var result: Dictionary = await _pose(p, pose)
		report["poses"].append(result)
		_write()
		if finished:
			return
	_finish(0, "")


func _on_process_frame() -> void:
	for e in enforced:
		var obj: Object = e[0]
		if obj != null and is_instance_valid(obj) and obj.get(e[1]) != e[2]:
			obj.set(e[1], e[2])
	if hold_wind_on:
		TVW.hold_wind()


# ------------------------------------------------------------------ switches

func _enforce(obj: Object, prop: String, value: Variant) -> void:
	if obj == null:
		return
	saved.append([obj, prop, obj.get(prop)])
	enforced.append([obj, prop, value])
	obj.set(prop, value)


func _restore_all() -> void:
	enforced.clear()
	for i in range(saved.size() - 1, -1, -1):
		var obj: Object = saved[i][0]
		if obj != null and is_instance_valid(obj):
			obj.set(saved[i][1], saved[i][2])
	saved.clear()
	hold_wind_on = true


func _n(path: String) -> Node:
	return game.get_node_or_null(path)


func _sub_viewports() -> Array:
	var list: Array = []
	for v in root.find_children("*", "SubViewport", true, false):
		var sv := v as SubViewport
		if sv != null and sv.render_target_update_mode != SubViewport.UPDATE_DISABLED:
			list.append(sv)
	return list


func _effects() -> Array:
	var seen := {}
	var list: Array = []
	var owners: Array = root.find_children("*", "WorldEnvironment", true, false) + root.find_children("*", "Camera3D", true, false)
	for o in owners:
		var comp: Compositor = o.get("compositor")
		if comp == null:
			continue
		for e in comp.compositor_effects:
			if e == null or seen.has(e.get_instance_id()):
				continue
			seen[e.get_instance_id()] = true
			list.append(e)
	return list


func _effect_name(e: Object) -> String:
	var s: Script = e.get_script()
	return (s.resource_path.get_file().get_basename() if s != null else e.get_class()) + "#" + str(e.get_instance_id() % 100000)


## Each entry: [tag, apply callable, warm frames]. Everything an apply sets goes through _enforce, so _restore_all() undoes it.
func _individual_configs() -> Array:
	var c: Array = []
	c.append(["base", _noop, warm])
	if clouds != null:
		c.append(["clouds_off", _enforce.bind(clouds, "enabled", false), warm])
		c.append(["clouds_hold_decay1", _enforce.bind(clouds, "accumulation_decay", 1.0), warm])
		c.append(["clouds_nojitter", _clouds_nojitter, warm * 3])
		c.append(["cloud_shadow_hold", _enforce.bind(clouds, "cloud_shadow_generation_enabled", false), warm])
	if bridge != null:
		c.append(["cloud_shadow_off", _enforce.bind(bridge, "CastCloudShadows", false), warm * 2])
	var svs := _sub_viewports()
	if not svs.is_empty():
		c.append(["subviewports_hold_all", _subviewports_hold, warm])
		for sv in svs:
			c.append(["subviewport_hold:" + String(sv.name), _enforce.bind(sv, "render_target_update_mode", SubViewport.UPDATE_DISABLED), warm])
	var debris := _n("GustFrontDebris")
	if debris != null:
		c.append(["debris_hold", _enforce.bind(debris, "process_mode", Node.PROCESS_MODE_DISABLED), warm])
		c.append(["debris_off", _debris_off, warm * 2])
	var grass := _n("StandingGrass")
	if grass != null:
		c.append(["grass_off", _enforce.bind(grass, "visible", false), warm])
		c.append(["grass_hold", _enforce.bind(grass, "process_mode", Node.PROCESS_MODE_DISABLED), warm])
	var trees := _n("BiomeDecorations")
	if trees != null:
		c.append(["trees_off", _enforce.bind(trees, "visible", false), warm])
		c.append(["trees_hold", _enforce.bind(trees, "process_mode", Node.PROCESS_MODE_DISABLED), warm])
	for pair in [["water_hold", "WaterFftController"], ["wet_world_hold", "WetWorld"], ["precip_occlusion_hold", "PrecipitationOcclusion"],
			["local_weather_hold", "LocalWeatherEffects"], ["volumetric_clouds_node_hold", "RingVolumetricClouds"], ["cloud_bridge_hold", "SunshineCloudBridge"],
			["cloud_driver_hold", "SunshineCloudsDriver"], ["far_viewport_node_hold", "ScaledFarViewport"], ["day_night_hold", "RingDayNightCycle"],
			["terrain_cloud_sun_hold", "TerrainCloudSunVisibility"], ["height_shadow_hold", "TerrainHeightShadow"], ["contact_shadows_hold", "ContactShadows"]]:
		var node := _n(String(pair[1]))
		if node != null:
			c.append([String(pair[0]), _enforce.bind(node, "process_mode", Node.PROCESS_MODE_DISABLED), warm])
	var droplets := _n("LensDroplets")
	if droplets != null:
		c.append(["lens_droplets_off", _enforce.bind(droplets, "visible", false), warm])
	if env != null:
		for flag in ["ssao_enabled", "ssr_enabled", "ssil_enabled", "glow_enabled", "volumetric_fog_enabled", "sdfgi_enabled"]:
			if bool(env.get(flag)):
				c.append(["env_off:" + String(flag), _enforce.bind(env, String(flag), false), warm])
	for e in _effects():
		if e != clouds:
			c.append(["effect_off:" + _effect_name(e), _enforce.bind(e, "enabled", false), warm])
	c.append(["wind_live", _wind_live, warm * 2])
	c.append(["game_process_off", _enforce.bind(game, "process_mode", Node.PROCESS_MODE_DISABLED), warm])
	c.append(["time_scale0", _enforce.bind(Engine, "time_scale", 0.0), warm])
	c.append(["base_end", _noop, warm * 3])
	return c


## Cumulative holds (nothing restored until the end): what is left after each step.
func _ladder() -> Array:
	return [["L1_debris_hold", _ladder_step.bind(1), warm], ["L2_+clouds_hold_decay1", _ladder_step.bind(2), warm],
		["L3_+cloud_shadow_hold", _ladder_step.bind(3), warm], ["L4_+subviewports_hold", _ladder_step.bind(4), warm],
		["L5_+game_process_off", _ladder_step.bind(5), warm], ["L6_+time_scale0", _ladder_step.bind(6), warm],
		["L7_+other_effects_off", _ladder_step.bind(7), warm], ["L8_+clouds_off", _ladder_step.bind(8), warm]]


## --plan=clouds: the second B7a pass. The bridge re-asserts the effect's `enabled` and the shadow generation switch every frame
## (RingSunshineCloudBridge.cs UpdateVisibility / shadow configure), so cloud switches are measured with the bridge held or through its own properties.
func _cloud_configs() -> Array:
	var c: Array = []
	c.append(["base", _noop, warm])
	if bridge != null:
		c.append(["bridge_hold", _enforce.bind(bridge, "process_mode", Node.PROCESS_MODE_DISABLED), warm])
		c.append(["clouds_off(bridge_held)", _clouds_off_bridge_held, warm * 2])
		c.append(["cloud_shadow_gen_off(bridge_prop)", _enforce.bind(bridge, "GpuShadowGenerationEnabled", false), warm * 2])
	if clouds != null:
		c.append(["clouds_decay1", _enforce.bind(clouds, "accumulation_decay", 1.0), warm])
		c.append(["clouds_decay1_nojitter", _cloud_steps.bind(2), warm * 2])
		c.append(["clouds_decay1_nojitter_norej", _cloud_steps.bind(3), warm * 2])
		c.append(["clouds_fixed_dither(decay_normal)", _clouds_fixed_dither, warm * 3])
		c.append(["clouds_fixed_dither+decay1", _clouds_fixed_dither_decay1, warm * 3])
	c.append(["root_others_hold", _root_others_hold, warm])
	c.append(["time_scale0", _enforce.bind(Engine, "time_scale", 0.0), warm])
	c.append(["game_process_off", _enforce.bind(game, "process_mode", Node.PROCESS_MODE_DISABLED), warm])
	c.append(["base_end", _noop, warm * 3])
	return c


func _cloud_ladder() -> Array:
	return [["M1_bridge+driver+debris_hold", _cloud_ladder_step.bind(1), warm], ["M2_+decay1_nojitter_norej", _cloud_ladder_step.bind(2), warm * 2],
		["M3_+fixed_dither", _cloud_ladder_step.bind(3), warm * 2], ["M4_+root_others_hold", _cloud_ladder_step.bind(4), warm],
		["M5_+game_process_off", _cloud_ladder_step.bind(5), warm], ["M6_+time_scale0", _cloud_ladder_step.bind(6), warm],
		["M7_+clouds_off", _cloud_ladder_step.bind(7), warm]]


func _cloud_step(level: int) -> void:
	if level == 1:
		_enforce(clouds, "accumulation_decay", 1.0)
	elif level == 2:
		_enforce(clouds, "ray_start_jitter_strength", 0.0)
		_enforce(clouds, "dither_speed", 0.0)
	elif level == 3:
		_enforce(clouds, "depth_history_rejection_enabled", false)
		_enforce(clouds, "cloud_history_compatibility_enabled", false)


func _cloud_steps(level: int) -> void:
	for i in range(1, level + 1):
		_cloud_step(i)


func _clouds_off_bridge_held() -> void:
	_enforce(bridge, "process_mode", Node.PROCESS_MODE_DISABLED)
	_enforce(clouds, "enabled", false)


## The render callback adds 1 to render_callback_serial; pinning it here every process frame fixes gameplay_dither_time (with dither_speed 0).
func _clouds_fixed_dither() -> void:
	_enforce(clouds, "dither_speed", 0.0)
	_enforce(clouds, "render_callback_serial", 100000)


func _clouds_fixed_dither_decay1() -> void:
	_clouds_fixed_dither()
	_enforce(clouds, "accumulation_decay", 1.0)


func _root_others_hold() -> void:
	for child in root.get_children():
		if child != game and not game.is_ancestor_of(child) and not child.is_ancestor_of(game):
			_enforce(child, "process_mode", Node.PROCESS_MODE_DISABLED)


func _cloud_ladder_step(step: int) -> void:
	match step:
		1:
			for n in ["SunshineCloudBridge", "SunshineCloudsDriver", "GustFrontDebris"]:
				if _n(n) != null:
					_enforce(_n(n), "process_mode", Node.PROCESS_MODE_DISABLED)
		2:
			_cloud_steps(3)
		3:
			_enforce(clouds, "render_callback_serial", 100000)
		4:
			_root_others_hold()
		5:
			_enforce(game, "process_mode", Node.PROCESS_MODE_DISABLED)
		6:
			_enforce(Engine, "time_scale", 0.0)
		7:
			_enforce(clouds, "enabled", false)


## --plan=residual: what moves once the clouds are really off (bridge held, effect disabled) and the debris is held; each extra switch on top of that.
func _residual_configs() -> Array:
	var c: Array = [["R0_clouds_off_debris_held", _residual_base, warm * 6]]
	var extras := [["ssr_off", env, "ssr_enabled", false], ["ssao_off", env, "ssao_enabled", false], ["time_scale0", Engine, "time_scale", 0.0],
		["game_process_off", game, "process_mode", Node.PROCESS_MODE_DISABLED], ["lens_droplets_off", _n("LensDroplets"), "visible", false],
		["grass_off", _n("StandingGrass"), "visible", false], ["trees_off", _n("BiomeDecorations"), "visible", false],
		["far_ring_viewport_hold", root.find_child("FarRingViewport", true, false), "render_target_update_mode", SubViewport.UPDATE_DISABLED]]
	for e in _effects():
		if e != clouds:
			extras.append(["effect_off:" + _effect_name(e), e, "enabled", false])
	for x in extras:
		if x[1] != null:
			c.append(["R+" + String(x[0]), _residual_with.bind(x[1], x[2], x[3]), warm * 6])
	c.append(["R0_again", _residual_base, warm * 6])
	return c


func _residual_base() -> void:
	_enforce(bridge, "process_mode", Node.PROCESS_MODE_DISABLED)
	_enforce(clouds, "enabled", false)
	if _n("GustFrontDebris") != null:
		_enforce(_n("GustFrontDebris"), "process_mode", Node.PROCESS_MODE_DISABLED)


func _residual_with(obj: Object, prop: String, value: Variant) -> void:
	_residual_base()
	_enforce(obj, prop, value)


func _noop() -> void:
	pass


func _clouds_nojitter() -> void:
	_enforce(clouds, "ray_start_jitter_strength", 0.0)
	_enforce(clouds, "dither_speed", 0.0)


func _subviewports_hold() -> void:
	for sv in _sub_viewports():
		_enforce(sv, "render_target_update_mode", SubViewport.UPDATE_DISABLED)


func _debris_off() -> void:
	_enforce(_n("GustFrontDebris"), "ForcedLevel", 0.0)
	_enforce(_n("GustFrontDebris"), "MegastormLevel", 0.0)


func _wind_live() -> void:
	hold_wind_on = false
	if _n("LocalWeatherEffects") != null:
		_enforce(_n("LocalWeatherEffects"), "Enabled", true)


func _ladder_step(step: int) -> void:
	match step:
		1:
			if _n("GustFrontDebris") != null:
				_enforce(_n("GustFrontDebris"), "process_mode", Node.PROCESS_MODE_DISABLED)
		2:
			if clouds != null:
				_enforce(clouds, "accumulation_decay", 1.0)
		3:
			if clouds != null:
				_enforce(clouds, "cloud_shadow_generation_enabled", false)
		4:
			_subviewports_hold()
		5:
			_enforce(game, "process_mode", Node.PROCESS_MODE_DISABLED)
		6:
			_enforce(Engine, "time_scale", 0.0)
		7:
			for e in _effects():
				if e != clouds:
					_enforce(e, "enabled", false)
		8:
			if clouds != null:
				_enforce(clouds, "enabled", false)


# ------------------------------------------------------------------ capture

func _frames(n: int) -> void:
	for i in n:
		await RenderingServer.frame_post_draw


func _grab() -> Dictionary:
	await RenderingServer.frame_post_draw
	var img := TVW.grab(self)
	var cam := root.get_camera_3d()
	return {"img": img, "frame": Engine.get_frames_drawn(), "cam": cam.global_transform if cam != null else Transform3D.IDENTITY, "counters": _counters()}


func _counters() -> Dictionary:
	var d := {}
	if clouds != null:
		d["cloud_callback_serial"] = clouds.get("render_callback_serial")
		d["cloud_accumulation_is_a"] = clouds.get("accumulation_is_a")
		d["cloud_shadow_next_cascade"] = clouds.get("cloud_shadow_next_cascade")
	if far != null:
		for p in ["FarWeatherAtlasPublicationCount", "FarWeatherAtlasContentHash", "FarFineAtlasPublicationCount", "FarFineAtlasContentHash", "FarWeatherCloudBakeCount", "CloudMaskUpdateCount"]:
			d[p] = str(far.get(p))
	return d


func _compare(a: Image, b: Image) -> Dictionary:
	if a.get_data() == b.get_data():
		return {"same": true, "max": 0.0, "mean": 0.0}
	var m := a.compute_image_metrics(b, false)
	return {"same": false, "max": float(m.get("max", -1.0)), "mean": float(m.get("mean", -1.0))}


func _save_raw(pose: String, tag: String, which: String, img: Image) -> String:
	var dir := raw_dir.path_join(pose)
	DirAccess.make_dir_recursive_absolute(dir)
	var path := dir.path_join("%s__%s.rgba" % [tag.replace(":", "_").replace("/", "_").replace("#", "_").replace("+", ""), which])
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f == null:
		return ""
	f.store_buffer(img.get_data())
	f.close()
	return path


func _measure(pose: String, tag: String, warm_frames: int, gap := 30, keep_png := false) -> Dictionary:
	stage = "%s/%s" % [pose, tag]
	await _frames(warm_frames)
	var a: Dictionary = await _grab()
	var b: Dictionary = await _grab()
	await _frames(gap - 1)
	var c: Dictionary = await _grab()
	var ia: Image = a["img"]
	var r := {"tag": tag, "frames": [a["frame"], b["frame"], c["frame"]], "size": [ia.get_width(), ia.get_height()],
		"cam_still": a["cam"].is_equal_approx(b["cam"]) and a["cam"].is_equal_approx(c["cam"]) and a["cam"] == c["cam"],
		"n_vs_n1": _compare(ia, b["img"]), "n_vs_n%d" % gap: _compare(ia, c["img"]), "counters": [a["counters"], b["counters"], c["counters"]], "raw": {}}
	if not r["n_vs_n1"]["same"] or not r["n_vs_n%d" % gap]["same"]:
		r["raw"]["n"] = _save_raw(pose, tag, "n", ia)
		if not r["n_vs_n1"]["same"]:
			r["raw"]["n1"] = _save_raw(pose, tag, "n1", b["img"])
		if not r["n_vs_n%d" % gap]["same"]:
			r["raw"]["n%d" % gap] = _save_raw(pose, tag, "n%d" % gap, c["img"])
	if keep_png:
		var png := out_dir.path_join("%s__%s.png" % [pose, tag.replace(":", "_").replace("+", "")])
		ia.save_png(png)
		r["png"] = png
		r["raw"]["png_n"] = _save_raw(pose, tag, "n", ia)
	_log("%s %s N/N+1 %s  N/N+%d %s  frames %s cam_still=%s" % [pose, tag, _fmt(r["n_vs_n1"]), gap, _fmt(r["n_vs_n%d" % gap]), str(r["frames"]), str(r["cam_still"])])
	return r


func _fmt(c: Dictionary) -> String:
	return "same" if c["same"] else "DIFF(max %.4f mean %.6f)" % [c["max"], c["mean"]]


func _wait_idle(timeout_s: float) -> bool:
	var deadline := Time.get_ticks_msec() + int(timeout_s * 1000.0)
	var stable := 0
	while Time.get_ticks_msec() < deadline:
		await process_frame
		stable = stable + 1 if COMMON.builds_idle(game) else 0
		if stable >= 30:
			return true
	return false


func _pose(name: String, pose: Dictionary) -> Dictionary:
	var result := {"pose": name, "configs": [], "ladder": []}
	stage = "%s/place" % name
	var a := float(pose["around_m"])
	var z := float(pose["across_m"])
	var vertical := float(pose["vertical_m"]) if pose.has("vertical_m") else float(surface.call("SampleHeight", a, z)) + float(pose.get("lift_m", 1.7))
	# Noclip hover with zero speed for every pose, so no physics settles the camera between frames.
	probe.PlaceNoclip(explorer, a, vertical, z, float(pose.get("yaw_deg", 0.0)), float(pose.get("pitch_deg", 0.0)), 0.0)
	await COMMON.settle(self, 0.5)
	result["idle_1"] = await _wait_idle(180.0)
	var time_spec: Dictionary = POSE_TIMES.get(name, {"mode": "noon"})
	result["time"] = COMMON.apply_time(game, time_spec)
	COMMON.hold_world_clocks(game)
	await COMMON.settle(self, 3.0)
	result["idle_2"] = await _wait_idle(90.0)
	TVW.freeze_world(game)
	result["freeze_noise"] = TVW.freeze_noise(self, game, PackedStringArray(["clouds", "taa"]))
	_hide_overlays_except_droplets()
	await _frames(60)
	result["state"] = _state()
	_log("%s placed (idle %s/%s), time %s; %d subviewports, %d effects" % [name, str(result["idle_1"]), str(result["idle_2"]), str(result["time"]), result["state"]["subviewports"].size(), result["state"]["effects"].size()])
	_write()
	var cloud_plan := String(args.get("plan", "")) == "clouds"
	var residual_plan := String(args.get("plan", "")) == "residual"
	if residual_plan:
		for cfg in _residual_configs():
			cfg[1].call()
			result["configs"].append(await _measure(name, cfg[0], cfg[2], 30, String(cfg[0]) == "R0_clouds_off_debris_held"))
			_restore_all()
			await _frames(warm)
		return result
	for cfg in (_cloud_configs() if cloud_plan else _individual_configs()):
		cfg[1].call()
		var keep := String(cfg[0]) == "base"
		result["configs"].append(await _measure(name, cfg[0], cfg[2], 30, keep))
		if String(cfg[0]) == "clouds_hold_decay1" and clouds != null and clouds.has_method("get_temporal_history_diagnostics"):
			result["cloud_hold_diagnostics"] = clouds.call("get_temporal_history_diagnostics")
		_restore_all()
		if finished:
			return result
	# Held clouds over a longer still (smear or decay would show), plus a PNG to compare against the base look.
	if clouds != null:
		if cloud_plan:
			_cloud_step(1); _cloud_step(2); _cloud_step(3)
		else:
			_enforce(clouds, "accumulation_decay", 1.0)
		result["configs"].append(await _measure(name, "clouds_hold_decay1_long150", warm, 150, true))
		_restore_all()
	await _frames(warm * 2)
	for cfg in (_cloud_ladder() if cloud_plan else _ladder()):
		cfg[1].call()
		result["ladder"].append(await _measure(name, cfg[0], warm))
		if finished:
			return result
	_restore_all()
	await _frames(warm * 3)
	result["ladder"].append(await _measure(name, "ladder_restored_base", warm))
	return result


func _hide_overlays_except_droplets() -> void:
	for parent in [game, root]:
		for child in parent.get_children():
			if child is CanvasLayer and String(child.name) != "LensDroplets":
				(child as CanvasLayer).visible = false


func _state() -> Dictionary:
	var vp := root
	var s := {"use_taa": vp.use_taa, "msaa_3d": vp.msaa_3d, "screen_space_aa": vp.screen_space_aa, "scaling_3d_mode": vp.scaling_3d_mode,
		"scaling_3d_scale": vp.scaling_3d_scale, "use_debanding": vp.use_debanding, "fsr_sharpness": vp.fsr_sharpness, "subviewports": [], "effects": []}
	if env != null:
		for flag in ["ssao_enabled", "ssr_enabled", "ssil_enabled", "glow_enabled", "volumetric_fog_enabled", "sdfgi_enabled", "fog_enabled", "tonemap_mode"]:
			s["env_" + flag] = env.get(flag)
	var cam := root.get_camera_3d()
	if cam != null and cam.attributes != null:
		s["camera_attributes"] = cam.attributes.get_class()
		s["auto_exposure"] = cam.attributes.get("auto_exposure_enabled")
	for sv in _sub_viewports():
		s["subviewports"].append({"path": str(sv.get_path()), "size": sv.size, "mode": sv.render_target_update_mode, "use_taa": sv.use_taa,
			"msaa_3d": sv.msaa_3d, "screen_space_aa": sv.screen_space_aa, "scaling_3d_mode": sv.scaling_3d_mode})
	for e in _effects():
		s["effects"].append({"name": _effect_name(e), "enabled": e.get("enabled"), "callback": e.get("effect_callback_type")})
	s["root_children"] = root.get_children().map(func(n): return "%s (%s)%s" % [n.name, n.get_class(), " [game]" if n == game or n.is_ancestor_of(game) else ""])
	if clouds != null:
		for p in ["cloud_interleave", "resolution_jitter_enabled", "accumulation_decay", "ray_start_jitter_strength", "dither_speed", "resolution_scale", "engine_jitter_compensation"]:
			s["clouds_" + p] = clouds.get(p)
	return s
