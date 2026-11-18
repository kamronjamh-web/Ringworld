extends SceneTree
## Reusable real-world performance benchmark driver (docs/perf-bench.md). Run it through tools/perf_bench.ps1, which prepares the isolated profile,
## the frozen slot 3 snapshot, the optimized C# assembly, the GPU queue and the report. It boots the REAL start menu on the isolated copy of the
## snapshot and presses Continue, as the player does (tools/tree_visibility_world.gd), then runs the scenarios of tools/fixtures/perf_bench_scenarios.json:
## per scenario, A-A rounds of one window each (stand / yaw_sweep / fly), with per-frame wall, frame setup, main-thread process, render CPU and GPU,
## draw calls, primitives, VRAM, GC, per-viewport GPU, every RingPerformanceGpu detailed interval and the key CPU scopes.
## Args (after --): --out=<abs dir> --scenarios=<abs json> [--select=all|quick|a,b] [--expect=a,b] [--vsync-pass] [--stills] [--no-capture] [--tails] [--rounds=N] [--window-scale=F]
## --expect is the plan the runner resolved (tools/perf_bench.ps1: the names _select returns, in its order, which is the file order for all / quick and the --select order for a name list; the run order by kind comes later); a different list from _select is PERF_BENCH=FAIL "plan mismatch" before any boot work, and the list is recorded as report["expected"].
## Poses may be {"poi": "weather_lung"|"emitter", "viewpoint": N}, reached through the live tuning drawer's teleports. Scenarios of kind "ssr_fade" run last, after the stills, then those of kind "reflection_probe" (tools/perf_bench_reflection_probe.gd; results in report["reflection_probes"]).
## Scenarios of kind "ab_matrix" (tools/perf_bench_ab_matrix.gd; baseline plus named variants, interleaved A-X-A windows, optional stills) run right after the generic measured loop; results in report["ab_matrices"].
## Verdict line: PERF_BENCH=PASS when every selected scenario completed (stills and the VSync pass included), PERF_BENCH=FAIL <reason> otherwise.
## It refuses to run when user:// is not the isolated appdata_perf_bench profile, so it can never touch the player's save.

const PROBE := preload("res://tools/PerfBenchProbe.cs")
const COMMON := preload("res://tools/shadow_harness_common.gd")
const TVW := preload("res://tools/tree_visibility_world.gd")
const REFLECTION_PROBE := preload("res://tools/perf_bench_reflection_probe.gd")
const AB_MATRIX := preload("res://tools/perf_bench_ab_matrix.gd")
const ADAPTIVE := preload("res://tools/perf_bench_adaptive.gd")

var args := {}
var out_dir := ""
var report := {"scenarios": [], "stills": [], "reflection_probes": [], "ab_matrices": [], "events": [], "errors": [], "skipped": []}
var probe
var game: Node
var explorer: Node
var surface: Node
var monitor: Node
var graphics: Node
var trees: Node
var held := {}
var t_start := 0
var spec := {}
var key_scopes: PackedStringArray = []
var quiet_done := false


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
	var line := "PB %7.1fs %s" % [float(Time.get_ticks_msec() - t_start) / 1000.0, text]
	print(line)
	report["events"].append(line)


func _write() -> void:
	var file := FileAccess.open(out_dir.path_join("driver_report.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(report))
		file.close()


func _finish(code: int, reason: String) -> void:
	_release_keys()
	_write()
	if code == 0:
		print("PERF_BENCH=PASS")
	else:
		print("PERF_BENCH=FAIL %s" % reason)
		report["errors"].append(reason)
		_write()
	quit(code)


func _display_on() -> bool:
	var output := []
	if OS.execute("nvidia-smi", ["--query-gpu=display_active", "--format=csv,noheader"], output, true) != 0 or output.is_empty():
		return true
	return not String(output[0]).strip_edges().begins_with("Disabled")


func _run() -> void:
	t_start = Time.get_ticks_msec()
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("/.godot/appdata_perf_bench/"):
		push_error("Refusing to run: user:// (%s) is not the isolated appdata_perf_bench profile." % user_dir)
		quit(2)
		return
	out_dir = String(args.get("out", ""))
	DirAccess.make_dir_recursive_absolute(out_dir)
	spec = JSON.parse_string(FileAccess.get_file_as_string(String(args["scenarios"])))
	if typeof(spec) != TYPE_DICTIONARY:
		_finish(2, "cannot read the scenario file")
		return
	key_scopes = PackedStringArray(spec.get("key_scopes", []))
	var chosen := _select(String(args.get("select", "all")))
	if chosen.is_empty():
		_finish(2, "no scenario selected by --select=%s" % String(args.get("select", "all")))
		return
	var expected: Array = Array(String(args.get("expect", "")).split(",", false))
	if not expected.is_empty():  # an absent or empty --expect is no check (an older runner, or one that resolved nothing)
		report["expected"] = expected
		var picked: Array = chosen.map(func(s): return String(s["name"]))
		if expected != picked:
			_finish(2, "plan mismatch: runner expected [%s], driver selected [%s]" % [",".join(expected), ",".join(picked)])
			return
	probe = PROBE.new()
	var display_on := _display_on()
	report["machine"] = {"adapter": RenderingServer.get_video_adapter_name(), "api": RenderingServer.get_video_adapter_api_version(),
		"engine": Engine.get_version_info().get("string", ""), "user_dir": user_dir, "vsync_at_boot": DisplayServer.window_get_vsync_mode(),
		"max_fps": Engine.max_fps, "refresh_hz": DisplayServer.screen_get_refresh_rate(), "screen": DisplayServer.screen_get_size(),
		"display_active": display_on, "common": COMMON.machine_info()}
	report["selected"] = chosen.map(func(s): return String(s["name"]))
	_log("adapter=%s display_on=%s scenarios=%s" % [report["machine"]["adapter"], str(display_on), ",".join(report["selected"])])
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	# ---------------------------------------------------------------- the real start menu and Continue
	var boot_start := Time.get_ticks_msec()
	var err := []
	game = await TVW.boot_game(self, err)
	if game == null:
		_finish(3, "boot failed: %s" % (err[0] if not err.is_empty() else "unknown"))
		return
	report["boot_s"] = float(Time.get_ticks_msec() - boot_start) / 1000.0
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	monitor = game.get_node_or_null("RingPerformanceMonitor")
	graphics = game.get_node_or_null("GraphicsQuality")
	trees = game.get_node_or_null("BiomeDecorations")
	if explorer == null or surface == null or monitor == null:
		_finish(3, "game nodes missing (Explorer/Terrain3DGameplay/RingPerformanceMonitor)")
		return
	monitor.set("Enabled", true)
	monitor.set("DetailedGpuEnabled", true)
	# A/B of debug switches on the tree layer: PERF_BENCH_TREE_PROPS="TreeDropFaintInstances=1,TreeShadowCulling=0" (environment, applied once after boot).
	var tree_props := OS.get_environment("PERF_BENCH_TREE_PROPS")
	if tree_props != "" and trees != null:
		for pair in tree_props.split(",", false):
			var kv := pair.split("=")
			if kv.size() == 2:
				trees.set(kv[0].strip_edges(), kv[1].strip_edges() == "1")
				_log("tree prop %s = %s" % [kv[0], kv[1]])
		report["tree_props"] = tree_props
	# A/B of node properties anywhere in the game: PERF_BENCH_NODE_PROPS="WetWorld:SsrPolicyTargetOverride=0;Node/Path:Prop=1.5" (numbers, true/false).
	var node_props := OS.get_environment("PERF_BENCH_NODE_PROPS")
	if node_props != "":
		for item in node_props.split(";", false):
			var colon := item.find(":")
			var target := game.get_node_or_null(item.substr(0, colon).strip_edges()) if colon > 0 else null
			var rest := item.substr(colon + 1).strip_edges() if colon > 0 else ""
			if target != null and rest.ends_with(")") and rest.find("(") > 0:
				# Method call with numeric arguments: WetWorld:ForceUniform(0.02,0.02,0,0)
				var open := rest.find("(")
				var call_args: Array = []
				for a in rest.substr(open + 1, rest.length() - open - 2).split(",", false):
					call_args.append(float(a.strip_edges()))
				target.callv(rest.substr(0, open), call_args)
				_log("node call %s.%s%s" % [item.substr(0, colon), rest.substr(0, open), str(call_args)])
				continue
			var kv := rest.split("=") if colon > 0 else PackedStringArray()
			if target == null or kv.size() != 2:
				report["errors"].append("PERF_BENCH_NODE_PROPS: cannot apply '%s'" % item)
				continue
			var text := kv[1].strip_edges()
			var value: Variant = (text == "true") if text in ["true", "false"] else float(text)
			target.set(kv[0].strip_edges(), value)
			_log("node prop %s = %s" % [item.substr(0, colon), str(value)])
		report["node_props"] = node_props
	report["state"] = _state()
	report["spawn_pose"] = COMMON.pose_now(game)
	_log("booted in %.1f s; spawn pose %s" % [float(report["boot_s"]), str(report["spawn_pose"])])
	await COMMON.settle(self, 2.0)
	_write()
	# ---------------------------------------------------------------- scenarios
	var failed := 0
	var ssr_scenarios := chosen.filter(func(s): return String(s["kind"]) == "ssr_fade")
	var probe_scenarios := chosen.filter(func(s): return String(s["kind"]) == "reflection_probe")
	var ab_scenarios := chosen.filter(func(s): return String(s["kind"]) == "ab_matrix")
	chosen = chosen.filter(func(s): return String(s["kind"]) != "ssr_fade" and String(s["kind"]) != "reflection_probe" and String(s["kind"]) != "ab_matrix")
	for sc in chosen:
		var result := await _scenario(sc, false)
		report["scenarios"].append(result)
		failed += 1 if result.has("error") else 0
		_write()
	for sc in ab_scenarios:
		var result: Dictionary = await AB_MATRIX.measure(self, sc)
		report["ab_matrices"].append(result)
		failed += 1 if result.has("error") else 0
		_write()
	if args.has("vsync-pass"):
		if not display_on:
			report["skipped"].append("vsync pass: the display is off (VSync would run at a fixed ~4 fps)")
			_log("vsync pass skipped: display off")
		else:
			for sc in chosen:
				if bool(sc.get("vsync_pass", false)):
					var result := await _scenario(sc, true)
					report["scenarios"].append(result)
					failed += 1 if result.has("error") else 0
					_write()
	if args.has("stills"):
		await _stills(chosen)
		for s in report["stills"]:
			failed += 1 if s.has("error") else 0
	for sc in ssr_scenarios:
		var result := await _ssr_fade(sc)
		report["scenarios"].append(result)
		failed += 1 if result.has("error") else 0
		_write()
	for sc in ab_scenarios:
		if bool(sc.get("stills", false)):
			var still_error: String = await AB_MATRIX.stills(self, sc)
			failed += 1 if still_error != "" else 0
	for sc in probe_scenarios:
		var result: Dictionary = await REFLECTION_PROBE.run(self, sc)
		report["reflection_probes"].append(result)
		failed += 1 if result.has("error") else 0
		_write()
	if failed > 0:
		_finish(1, "%d scenario(s) or stills failed: see driver_report.json" % failed)
		return
	_finish(0, "")


func _select(what: String) -> Array:
	var all: Array = spec["scenarios"]
	if what == "all":
		return all
	if what == "quick":
		return all.filter(func(s): return bool(s.get("quick", false)))
	var names := what.split(",", false)
	var result: Array = []
	for n in names:
		for s in all:
			if String(s["name"]) == n:
				result.append(s)
	return result


# ------------------------------------------------------------------ scenarios

func _opt(sc: Dictionary, key: String) -> Variant:
	return sc.get(key, spec["defaults"].get(key))


func _scenario(sc: Dictionary, vsync_on: bool) -> Dictionary:
	var name := String(sc["name"]) + ("@vsync" if vsync_on else "")
	var pose: Dictionary = spec["poses"][String(sc["pose"])]
	var kind := String(sc["kind"])
	var rounds := int(args.get("rounds", _opt(sc, "rounds")))
	var seconds := float(_opt(sc, "window_s")) * float(args.get("window-scale", "1"))
	var adaptive := kind == "stand" and not args.has("tails")  # stand windows may stop early once settled; flights and sweeps keep fixed windows; --tails = full windows
	var result := {"name": name, "kind": kind, "pose_name": String(sc["pose"]), "pose": pose, "vsync": vsync_on, "window_s": seconds, "rounds": [], "setup": {}, "adaptive": adaptive}
	_log("scenario %s (%s, %d x %.0f s%s)" % [name, kind, rounds, seconds, ", adaptive" if adaptive else ""])
	_release_keys()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var speed := float(sc.get("speed_mps", 0.0))
	var began := Time.get_ticks_msec()
	var place_error: String = await _place(pose, sc, speed)
	if place_error != "":
		result["error"] = "placement: %s" % place_error
		_log("scenario %s FAILED: %s" % [name, result["error"]])
		return result
	await COMMON.settle(self, 0.4)
	var idle: bool = await COMMON.wait_builds_idle(self, game, 180.0)
	var time_info := _apply_time(_opt(sc, "time"), bool(_opt(sc, "hold_clocks")))
	if time_info.has("error"):
		result["error"] = "time of day: %s" % String(time_info["error"])
		_log("scenario %s FAILED: %s" % [name, result["error"]])
		return result
	await COMMON.settle(self, float(_opt(sc, "settle_s")))
	idle = idle and await COMMON.wait_builds_idle(self, game, 90.0)
	var reached: Dictionary = COMMON.pose_now(game)
	var ground_at := Vector2(float(pose["around_m"]), float(pose["across_m"])) if not pose.has("poi") else Vector2(float(reached["around"]), float(reached["across"]))
	result["setup"] = {"idle": idle, "settle_ms": Time.get_ticks_msec() - began, "reached": reached, "time": time_info,
		"ground_m": float(surface.call("SampleHeight", ground_at.x, ground_at.y))}
	# Wet world and engine SSR state at the start of the windows (the SSR-only-while-wet policy depends on both).
	var wet := game.get_node_or_null("WetWorld")
	var env_node := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	result["setup"]["wet_valid"] = wet.get("Valid") if wet != null else null
	result["setup"]["ssr_enabled"] = env_node.environment.ssr_enabled if env_node != null and env_node.environment != null else null
	result["setup"]["ssr_policy_weight"] = wet.get("SsrPolicyWeight") if wet != null else null
	result["setup"]["ssr_gate_wet_signal"] = wet.get("SsrGateWetSignal") if wet != null else null
	result["setup"]["ssr_gate_wet"] = wet.get("SsrGateWet") if wet != null else null
	result["setup"]["ssr_gate_poi_near"] = wet.get("SsrGatePoiNear") if wet != null else null
	_log("  placed, idle=%s after %d ms, sun %.1f deg, wet_valid=%s ssr=%s weight=%s gate wet=%s (signal %s) poi=%s" % [str(idle), int(result["setup"]["settle_ms"]), float(time_info.get("sun_elevation_deg", NAN)),
		str(result["setup"]["wet_valid"]), str(result["setup"]["ssr_enabled"]), str(result["setup"]["ssr_policy_weight"]), str(result["setup"]["ssr_gate_wet"]),
		str(result["setup"]["ssr_gate_wet_signal"]), str(result["setup"]["ssr_gate_poi_near"])])
	for r in rounds:
		var tag := "%s_r%d" % [name, r]
		var w: Dictionary
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED if vsync_on else DisplayServer.VSYNC_DISABLED)
		if kind == "fly":
			if r > 0:
				DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
				var replace_error: String = await _place(pose, sc, speed)
				if replace_error != "":
					result["error"] = "re-placement: %s" % replace_error
					DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
					return result
				await COMMON.settle(self, 0.4)
				await COMMON.wait_builds_idle(self, game, 120.0)
				DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED if vsync_on else DisplayServer.VSYNC_DISABLED)
			var start_pose: Dictionary = COMMON.pose_now(game)
			_key(KEY_W, true)
			await COMMON.settle(self, 1.5)
			w = await _window(tag, seconds, {})
			_key(KEY_W, false)
			var end_pose: Dictionary = COMMON.pose_now(game)
			w["distance_m"] = Vector2(float(end_pose["around"]) - float(start_pose["around"]), float(end_pose["across"]) - float(start_pose["across"])).length()
			w["m"]["distance_m"] = w["distance_m"]
			await COMMON.settle(self, 1.0)
		elif kind == "yaw_sweep":
			await COMMON.settle(self, 1.0)
			w = await _window(tag, seconds, {"yaw_rate": float(sc.get("deg_per_s", 360.0)), "yaw0": float(pose.get("yaw_deg", 0.0)), "pitch": float(pose.get("pitch_deg", 0.0))})
		else:
			await COMMON.settle(self, 1.5 if r > 0 else 2.0)
			w = await _window(tag, seconds, {"adaptive": true} if adaptive else {})
		result["rounds"].append(w)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	return result


## Returns "" on success, an error text otherwise. POI poses go through the live tuning drawer's teleports (as shadow_harness_common.go_pose).
func _place(pose: Dictionary, sc: Dictionary, speed: float) -> String:
	if pose.has("poi"):
		return await _place_poi(pose)
	var a := float(pose["around_m"])
	var z := float(pose["across_m"])
	var yaw := float(sc.get("yaw_deg", pose.get("yaw_deg", 0.0)))
	var pitch := float(sc.get("pitch_deg", pose.get("pitch_deg", 0.0)))
	if pose.has("vertical_m") and not sc.has("lift_m"):
		probe.PlaceNoclip(explorer, a, float(pose["vertical_m"]), z, yaw, pitch, speed)
		return ""
	var ground := float(surface.call("SampleHeight", a, z))
	var lift := float(sc.get("lift_m", pose.get("lift_m", 1.7)))
	if speed > 0.0:
		probe.PlaceNoclip(explorer, a, ground + lift, z, yaw, pitch, speed)
	else:
		explorer.call("TeleportToCanonicalMeters", a, ground + lift, z, yaw, pitch, true)
	return ""


func _place_poi(pose: Dictionary) -> String:
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		return "LiveTuningDrawer missing"
	var viewpoint := int(pose.get("viewpoint", 0))
	var kind := String(pose["poi"])
	var state_prop := ""
	var message_prop := ""
	var streamer: Node = null
	if kind == "emitter":
		var site := int(drawer.call("FindEmitterSite", "main"))
		if site < 0 or not bool(drawer.call("RequestEmitterTeleport", site, viewpoint)):
			return "emitter teleport refused (site %d)" % site
		state_prop = "EmitterTeleportState"
		message_prop = "EmitterTeleportMessage"
	elif kind == "weather_lung":
		streamer = game.get_node_or_null("WeatherLung")
		if streamer == null or not bool(drawer.call("RequestWeatherLungTeleport", viewpoint)):
			return "weather lung teleport refused (streamer %s)" % str(streamer != null)
		state_prop = "WeatherLungTeleportState"
		message_prop = "WeatherLungTeleportMessage"
	else:
		return "unknown poi kind '%s'" % kind
	var start := Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < 180000:
		await physics_frame
		var state := str(drawer.get(state_prop))
		if state == "Arrived":
			break
		if state == "Failed" or state == "TimedOut":
			return "%s teleport %s: %s" % [kind, state, str(drawer.get(message_prop))]
	if str(drawer.get(state_prop)) != "Arrived":
		return "%s teleport did not arrive within 180 s" % kind
	if kind == "emitter":
		var landing := 0
		while landing < 180 and not bool(explorer.get("NoclipEnabled")) and not bool(explorer.call("is_on_floor")):
			landing += 1
			await physics_frame
	else:
		if viewpoint != 2:
			var attach_start := Time.get_ticks_msec()
			while int(streamer.get("DetailState")) != 2 and Time.get_ticks_msec() - attach_start < 120000:
				await physics_frame
			if int(streamer.get("DetailState")) != 2:
				return "weather lung detail never attached: " + str(streamer.call("DescribeStatus"))
		var controller: Node = streamer.get("Controller")
		if controller != null:
			controller.call("ScrubTo", COMMON.WEATHER_LUNG_FROZEN_PHASE)
			controller.set("FreezePhase", true)
	return ""


## Sets the celestial state at the explorer's spot; the clocks run again from there unless hold_clocks.
func _apply_time(time: Variant, hold_clocks: bool) -> Dictionary:
	var info: Dictionary = COMMON.apply_time(game, time)
	if info.has("error"):
		return info
	var cycle := game.get_node_or_null("RingDayNightCycle")
	if cycle != null:
		cycle.set("Paused", hold_clocks)
	if hold_clocks:
		COMMON.hold_world_clocks(game)
	return info


func _key(code: int, down: bool) -> void:
	if held.get(code, false) == down:
		return
	held[code] = down
	var e := InputEventKey.new()
	e.keycode = code
	e.physical_keycode = code
	e.pressed = down
	Input.parse_input_event(e)


func _release_keys() -> void:
	for code in held.keys():
		_key(code, false)


# ------------------------------------------------------------------ stills

func _stills(chosen: Array) -> void:
	var dir := out_dir.path_join("stills")
	DirAccess.make_dir_recursive_absolute(dir)
	var hidden := quiet_done
	for sc in chosen:
		if not bool(sc.get("still", false)):
			continue
		var name := String(sc["name"])
		var entry := {"name": name, "pose_name": String(sc["pose"])}
		var pose: Dictionary = spec["poses"][String(sc["pose"])]
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		var place_error: String = await _place(pose, sc, 0.0)
		if place_error != "":
			entry["error"] = "placement: %s" % place_error
			report["stills"].append(entry)
			continue
		await COMMON.settle(self, 0.4)
		await COMMON.wait_builds_idle(self, game, 180.0)
		var time_info := _apply_time(_opt(sc, "time"), true)
		if time_info.has("error"):
			entry["error"] = String(time_info["error"])
			report["stills"].append(entry)
			continue
		await COMMON.settle(self, 3.0)
		await COMMON.wait_builds_idle(self, game, 90.0)
		var img: Image
		if args.has("no-capture"):
			if not hidden:
				entry["hidden"] = TVW.quiet_scene(game)
				TVW.hide_overlays(game)
				hidden = true
				quiet_done = true
			TVW.freeze_world(game)
			report["still_noise"] = TVW.freeze_noise(self, game, PackedStringArray(["clouds", "taa"]))
			for _i in 20:
				TVW.hold_wind()
				await process_frame
			await RenderingServer.frame_post_draw
			img = TVW.grab(self)
		else:
			# Still-capture mode (docs/test-speed-plan.md 3.8): time scale 0 and the cloud march held, so clouds, grass and gust debris stay visible. Timers only before capture_begin.
			if not hidden:
				TVW.hide_overlays(game)
				hidden = true
			TVW.freeze_world(game)
			report["still_noise"] = TVW.freeze_noise(self, game, PackedStringArray(["taa"]))
			var cap: Dictionary = await TVW.capture_begin(self, game)
			entry["capture"] = cap
			if not bool(cap.get("deterministic", false)):
				entry["capture_fallback"] = true
				_log("still %s: capture not deterministic (%s px differ), saved anyway" % [name, str(cap.get("last_pair_differing_px", -1))])
			await RenderingServer.frame_post_draw
			img = TVW.grab(self)
			TVW.capture_end(game)
		var path := dir.path_join("%s.png" % name)
		var save_err := img.save_png(path)
		entry["path"] = path
		entry["size"] = [img.get_width(), img.get_height()]
		if save_err != OK:
			entry["error"] = "save_png failed (%d)" % save_err
		report["stills"].append(entry)
		_log("still %s -> %s" % [name, path])
		_write()


# ------------------------------------------------------------------ ssr fade

func _ssr_env() -> Environment:
	var env_node := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if env_node != null and env_node.environment != null:
		return env_node.environment
	return root.world_3d.environment if root.world_3d != null else null


## Runs frames until `until` is true (an empty Callable: none) plus extra_s more, at most max_s. Without grab_dir it records per frame wall ms,
## SsrPolicyWeight and env ssr_enabled into rec; with grab_dir every frame is saved as f%05d.png (rec["frame_index"] counts across calls) and only
## the ssr_enabled flips are noted. Returns the first and last recorded frame index.
func _ssr_frames(wet: Node, env: Environment, rec: Dictionary, until: Callable, extra_s: float, max_s: float, grab_dir: String) -> Vector2i:
	var first: int = rec["ms"].size() if grab_dir == "" else int(rec["frame_index"])
	var begin := Time.get_ticks_usec()
	if not rec.has("last_us"):
		rec["last_us"] = begin
	var reached_at := -1
	while true:
		TVW.hold_wind()
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		var enabled: bool = env.ssr_enabled if env != null else false
		var flipped: bool = rec.has("last_enabled") and bool(rec["last_enabled"]) != enabled
		rec["last_enabled"] = enabled
		if grab_dir != "":
			var idx := int(rec["frame_index"])
			var img := TVW.grab(self)
			img.save_png(grab_dir.path_join("f%05d.png" % idx))
			rec["frame_index"] = idx + 1
			if flipped:
				rec["flip_indices"].append(idx)
		else:
			rec["ms"].append(float(now - int(rec["last_us"])) * 0.001)
			rec["weight"].append(float(wet.get("SsrPolicyWeight")))
			rec["enabled"].append(enabled)
			if flipped:
				rec["flip_frames"].append(rec["ms"].size() - 1)
		rec["last_us"] = now
		if reached_at < 0 and (until.is_null() or bool(until.call())):
			reached_at = now
		if reached_at >= 0 and float(now - reached_at) * 0.000001 >= extra_s:
			break
		if float(now - begin) * 0.000001 > max_s:
			rec["timeouts"] += 1
			break
	var last: int = (rec["ms"].size() if grab_dir == "" else int(rec["frame_index"])) - 1
	return Vector2i(first, last)


func _ssr_fade(sc: Dictionary) -> Dictionary:
	var name := String(sc["name"])
	var pose: Dictionary = spec["poses"][String(sc["pose"])]
	var result := {"name": name, "kind": "ssr_fade", "pose_name": String(sc["pose"]), "pose": pose, "vsync": false, "window_s": 0.0, "rounds": [], "setup": {}}
	_log("scenario %s (ssr_fade)" % name)
	_release_keys()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var wet := game.get_node_or_null("WetWorld")
	if wet == null or wet.get("SsrPolicyWeight") == null:
		result["error"] = "ssr policy not available"
		_log("scenario %s FAILED: %s" % [name, result["error"]])
		return result
	var began := Time.get_ticks_msec()
	var place_error: String = await _place(pose, sc, 0.0)
	if place_error != "":
		result["error"] = "placement: %s" % place_error
		_log("scenario %s FAILED: %s" % [name, result["error"]])
		return result
	await COMMON.settle(self, 0.4)
	var idle: bool = await COMMON.wait_builds_idle(self, game, 180.0)
	var time_info := _apply_time(_opt(sc, "time"), true)
	if time_info.has("error"):
		result["error"] = "time of day: %s" % String(time_info["error"])
		_log("scenario %s FAILED: %s" % [name, result["error"]])
		return result
	await COMMON.settle(self, float(_opt(sc, "settle_s")))
	idle = idle and await COMMON.wait_builds_idle(self, game, 90.0)
	if not quiet_done:
		report["ssr_quiet"] = TVW.quiet_scene(game)
		TVW.hide_overlays(game)
		quiet_done = true
	TVW.freeze_world(game)
	report["still_noise"] = TVW.freeze_noise(self, game, PackedStringArray(["clouds", "taa"]))
	result["setup"] = {"idle": idle, "settle_ms": Time.get_ticks_msec() - began, "reached": COMMON.pose_now(game), "time": time_info}
	var env := _ssr_env()
	var dir := out_dir.path_join("ssr_fade")
	DirAccess.make_dir_recursive_absolute(dir)
	var summary := {"stills": [], "frame_dir": "", "toggle_count_before": int(wet.get("SsrToggleCount"))}
	# a. weight stills
	for w in [1.0, 0.75, 0.5, 0.25, 0.1, 0.0]:
		wet.set("SsrPolicyWeightOverride", w)
		for _i in 20:
			TVW.hold_wind()
			await process_frame
		await RenderingServer.frame_post_draw
		var img := TVW.grab(self)
		var path := dir.path_join("%s_w%d.png" % [name, roundi(w * 100.0)])
		if img.save_png(path) != OK:
			result["error"] = "save_png failed (%s)" % path
		summary["stills"].append(path)
	wet.set("SsrPolicyWeightOverride", -1.0)
	# b. timing pass (no grabs)
	var at_full := func() -> bool: return float(wet.get("SsrPolicyWeight")) >= 0.999
	var at_zero := func() -> bool: return float(wet.get("SsrPolicyWeight")) <= 0.0001
	var rec := {"ms": [], "weight": [], "enabled": [], "flip_frames": [], "timeouts": 0}
	wet.set("SsrPolicyTargetOverride", 1.0)
	await _ssr_frames(wet, env, {"ms": [], "weight": [], "enabled": [], "flip_frames": [], "timeouts": 0}, at_full, 0.0, 10.0, "")
	var steady := await _ssr_frames(wet, env, rec, Callable(), 1.0, 5.0, "")
	wet.set("SsrPolicyTargetOverride", 0.0)
	var out_range := await _ssr_frames(wet, env, rec, at_zero, 2.0, 30.0, "")
	wet.set("SsrPolicyTargetOverride", 1.0)
	var in_range := await _ssr_frames(wet, env, rec, at_full, 2.0, 30.0, "")
	var ms: Array = rec["ms"]
	var sorted := ms.duplicate()
	sorted.sort()
	var flip_ms: Array = []
	for i in rec["flip_frames"]:
		flip_ms.append(ms[i])
	summary["max_frame_ms"] = sorted[sorted.size() - 1] if not sorted.is_empty() else 0.0
	summary["median_frame_ms"] = sorted[sorted.size() / 2] if not sorted.is_empty() else 0.0
	summary["flip_frame_ms"] = flip_ms
	summary["flip_frames"] = rec["flip_frames"]
	summary["timing_ranges"] = {"steady": [steady.x, steady.y], "fade_out": [out_range.x, out_range.y], "fade_in": [in_range.x, in_range.y]}
	var timeouts := int(rec["timeouts"])
	# c. frame pass (every frame saved)
	var frames_dir := dir.path_join("%s_frames" % name)
	DirAccess.make_dir_recursive_absolute(frames_dir)
	summary["frame_dir"] = frames_dir
	var frec := {"ms": [], "frame_index": 0, "flip_indices": [], "timeouts": 0}
	wet.set("SsrPolicyTargetOverride", 1.0)
	await _ssr_frames(wet, env, {"ms": [], "weight": [], "enabled": [], "flip_frames": [], "timeouts": 0}, at_full, 0.0, 10.0, "")
	await _ssr_frames(wet, env, frec, Callable(), 0.5, 5.0, frames_dir)
	var before_out := int(frec["frame_index"])
	wet.set("SsrPolicyTargetOverride", 0.0)
	var f_out := await _ssr_frames(wet, env, frec, at_zero, 1.0, 30.0, frames_dir)
	await _ssr_frames(wet, env, frec, Callable(), 0.5, 5.0, frames_dir)
	var before_in := int(frec["frame_index"])
	wet.set("SsrPolicyTargetOverride", 1.0)
	var f_in := await _ssr_frames(wet, env, frec, at_full, 1.0, 30.0, frames_dir)
	summary["frame_ranges"] = {"fade_out": [before_out, f_out.y], "fade_in": [before_in, f_in.y], "total": int(frec["frame_index"])}
	summary["frame_flip_indices"] = frec["flip_indices"]
	summary["timeouts"] = timeouts + int(frec["timeouts"])
	# d. release
	wet.set("SsrPolicyTargetOverride", -1.0)
	summary["toggle_count_after"] = int(wet.get("SsrToggleCount"))
	result["ssr_fade"] = {"summary": summary, "ms": rec["ms"], "weight": rec["weight"], "enabled": rec["enabled"]}
	_log("ssr_fade %s: max %.2f ms, median %.2f ms, flips %s ms, toggles %d -> %d, frames %d, timeouts %d" % [name, float(summary["max_frame_ms"]), float(summary["median_frame_ms"]),
		str(flip_ms), int(summary["toggle_count_before"]), int(summary["toggle_count_after"]), int(frec["frame_index"]), int(summary["timeouts"])])
	return result


# ------------------------------------------------------------------ measurement

func _all_nodes(node: Node) -> Array[Node]:
	var result: Array[Node] = [node]
	for child in node.get_children(true):
		result.append_array(_all_nodes(child))
	return result


func _viewports() -> Array:
	var result: Array = []
	for n in _all_nodes(root):
		if n is SubViewport:
			result.append(n)
	return result


func _window(tag: String, seconds: float, opts: Dictionary) -> Dictionary:
	var subs := _viewports()
	for vp in subs:
		RenderingServer.viewport_set_measure_render_time(vp.get_viewport_rid(), true)
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	monitor.call("ClearHistory")
	var gc0: Dictionary = probe.Gc()
	var f := {"wall": PackedFloat64Array(), "gpu": PackedFloat64Array(), "cpu": PackedFloat64Array(), "setup": PackedFloat64Array(),
		"process": PackedFloat64Array(), "draws": PackedFloat64Array(), "prims": PackedFloat64Array(), "vram": PackedFloat64Array(), "speed": PackedFloat64Array()}
	var per_vp := {}
	var busy := 0
	var checks := 0
	var yaw: float = float(opts.get("yaw0", 0.0))
	await RenderingServer.frame_post_draw
	var start := Time.get_ticks_usec()
	var last := start
	var n := 0
	var adaptive := bool(opts.get("adaptive", false))
	var block_starts := PackedInt32Array([0])  # adaptive only: the frame index at each 2 s boundary
	var stop := "cap"
	var check := {}
	while (Time.get_ticks_usec() - start) * 0.000001 < seconds:
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		var dt := (now - last) * 0.000001
		f["wall"].append((now - last) * 0.001)
		last = now
		if opts.has("yaw_rate"):
			yaw = fposmod(yaw + float(opts["yaw_rate"]) * dt, 360.0)
			explorer.call("SetPresentationView", yaw, float(opts.get("pitch", 0.0)))
		var g := RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())
		var c := RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid())
		for vp in subs:
			if not is_instance_valid(vp):
				continue
			var vg := RenderingServer.viewport_get_measured_render_time_gpu(vp.get_viewport_rid())
			var vc := RenderingServer.viewport_get_measured_render_time_cpu(vp.get_viewport_rid())
			g += vg
			c += vc
			var key := str(root.get_path_to(vp))
			if not per_vp.has(key):
				per_vp[key] = {"gpu": PackedFloat64Array(), "cpu": PackedFloat64Array()}
			per_vp[key]["gpu"].append(vg)
			per_vp[key]["cpu"].append(vc)
		f["gpu"].append(g)
		f["cpu"].append(c)
		f["setup"].append(RenderingServer.get_frame_setup_time_cpu())
		f["process"].append(Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0)
		f["draws"].append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		f["prims"].append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		f["vram"].append(Performance.get_monitor(Performance.RENDER_VIDEO_MEM_USED) / 1048576.0)
		f["speed"].append(float(explorer.get("ActualTravelSpeedMetersPerSecond")))
		if n % 15 == 0:
			checks += 1
			if not COMMON.builds_idle(game):
				busy += 1
		n += 1
		if adaptive:
			var at_s := (now - start) * 0.000001
			if at_s >= ADAPTIVE.BLOCK_S * float(block_starts.size()):
				block_starts.append(n)
				if block_starts.size() >= 3:
					check = ADAPTIVE.check(f["wall"], f["gpu"], block_starts, at_s)
					if bool(check["settled"]):
						stop = "settled"
						break
	var elapsed := (Time.get_ticks_usec() - start) * 0.000001
	var gc1: Dictionary = probe.Gc()
	var frames: int = f["wall"].size()
	var d := {}
	for key in f:
		d[key] = _dist(f[key])
	var fps := float(frames) / maxf(elapsed, 0.001)
	var m := {"fps": fps, "frames": frames, "wall_p50": d["wall"]["p50"], "wall_p95": d["wall"]["p95"], "wall_p99": d["wall"]["p99"], "wall_max": d["wall"]["max"],
		"hitch33_count": d["wall"]["over33_count"], "hitch33_worst_ms": d["wall"]["max"] if int(d["wall"]["over33_count"]) > 0 else 0.0,
		"setup_p50": d["setup"]["p50"], "setup_p95": d["setup"]["p95"], "process_p50": d["process"]["p50"], "process_p95": d["process"]["p95"],
		"render_cpu_p50": d["cpu"]["p50"], "render_cpu_p95": d["cpu"]["p95"], "gpu_total_p50": d["gpu"]["p50"], "gpu_total_p95": d["gpu"]["p95"],
		"draws_p50": d["draws"]["p50"], "prims_p50": d["prims"]["p50"], "vram_mb": d["vram"]["p50"], "speed_mps": d["speed"]["mean"],
		"gc_gen1": int(gc1["gen1"]) - int(gc0["gen1"]), "gc_gen2": int(gc1["gen2"]) - int(gc0["gen2"]), "gc_pause_ms": float(gc1["pause_ms"]) - float(gc0["pause_ms"]),
		"alloc_mb_s": (float(gc1["allocated_mb"]) - float(gc0["allocated_mb"])) / maxf(elapsed, 0.001)}
	var viewports := {}
	for key in per_vp:
		var vgd := _dist(per_vp[key]["gpu"])
		var vcd := _dist(per_vp[key]["cpu"])
		viewports[key] = {"gpu_p50": vgd["p50"], "cpu_p50": vcd["p50"]}
		var short := String(key).replace("/", ".").replace(":", "_")
		m["vp.%s.gpu_p50" % short] = vgd["p50"]
		m["vp.%s.cpu_p50" % short] = vcd["p50"]
	var mon: Dictionary = probe.MonitorRows(monitor)
	var mon_rows: Array = mon.get("rows", [])
	var row_count := 0
	# Rows are sums over the events the monitor still retains; when a ring overflowed, divide by the frames those events span.
	var cpu_dropped := int(mon.get("cpu_dropped", 0))
	var gpu_dropped := int(mon.get("gpu_dropped", 0))
	var cpu_span := int(mon.get("cpu_span_frames", -1))
	var gpu_span := int(mon.get("gpu_span_frames", -1))
	var cpu_denom := float(frames) if (cpu_dropped <= 0 or cpu_span <= 0) else float(cpu_span)
	var gpu_denom := float(frames) if (gpu_dropped <= 0 or gpu_span <= 0) else float(gpu_span)
	for r in mon_rows:
		var name := String(r["name"])
		var per_frame := float(r["mean_ms"]) * float(r["count"]) / maxf(1.0, gpu_denom if bool(r["gpu"]) else cpu_denom)
		var is_key := key_scopes.has(name)
		if bool(r["gpu"]):
			m["gpu.%s" % name] = per_frame
			m["gpu_sample.%s" % name] = float(r["mean_ms"])
		elif bool(r["worker"]):
			if is_key or per_frame >= 0.05:
				m["worker.%s" % name] = per_frame
		elif is_key or per_frame >= 0.02:
			m["cpu.%s" % name] = per_frame
			row_count += 1
	for k in key_scopes:
		if not m.has("cpu.%s" % k) and not m.has("worker.%s" % k):
			m["cpu.%s" % k] = 0.0
	var result := {"tag": tag, "seconds": elapsed, "vsync": DisplayServer.window_get_vsync_mode(), "window": DisplayServer.window_get_size(), "busy_fraction": float(busy) / maxf(1.0, float(checks)),
		"m": m, "viewports": viewports, "monitor_dropped": mon.get("dropped", -1),
		"monitor_span": {"cpu_dropped": cpu_dropped, "gpu_dropped": gpu_dropped, "cpu_span_frames": cpu_span, "gpu_span_frames": gpu_span, "history_frames": int(mon.get("history_frames", -1))},
		"monitor_gpu_available": mon.get("gpu_available", false),
		"working_set_mb": mon.get("working_set_mb", -1), "managed_heap_mb": mon.get("managed_heap_mb", -1), "pose": COMMON.pose_now(game)}
	if adaptive:
		result["adaptive"] = {"cap_s": seconds, "stop": stop, "blocks": maxi(0, block_starts.size() - 1), "check": check}
		if stop == "settled":
			result["tails_info"] = true
	var cycle := game.get_node_or_null("RingDayNightCycle")
	if cycle != null:
		result["sun_elevation_deg"] = float(cycle.get("SunElevationDegrees"))
		result["lighting_phase"] = str(cycle.get("LightingPhaseName"))
	_log("WIN %-34s vs=%d fps=%6.1f wall p50=%6.2f p95=%6.2f p99=%6.2f max=%7.2f >33=%d gpu p50=%5.2f setup p50=%5.2f proc p50=%5.2f draws=%d busy=%.2f" % [
		tag, int(result["vsync"]), fps, float(m["wall_p50"]), float(m["wall_p95"]), float(m["wall_p99"]), float(m["wall_max"]), int(m["hitch33_count"]),
		float(m["gpu_total_p50"]), float(m["setup_p50"]), float(m["process_p50"]), int(m["draws_p50"]), float(result["busy_fraction"])])
	if gpu_dropped > 0 or cpu_dropped > 0:
		_log("  span gpu=%d/%d cpu=%d/%d (dropped gpu=%d cpu=%d)" % [gpu_span, frames, cpu_span, frames, gpu_dropped, cpu_dropped])
	return result


func _dist(values: PackedFloat64Array) -> Dictionary:
	if values.is_empty():
		return {"n": 0, "mean": 0.0, "p50": 0.0, "p95": 0.0, "p99": 0.0, "max": 0.0, "min": 0.0, "over33_count": 0}
	var s := values.duplicate()
	s.sort()
	var total := 0.0
	var over33 := 0
	for v in s:
		total += v
		if v > 33.3:
			over33 += 1
	var k := s.size()
	return {"n": k, "mean": total / k, "p50": s[k / 2], "p95": s[mini(k - 1, int(k * 0.95))], "p99": s[mini(k - 1, int(k * 0.99))], "max": s[k - 1], "min": s[0], "over33_count": over33}


func _state() -> Dictionary:
	var env_node := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	var env: Environment = env_node.environment if env_node != null else null
	var d := {"window": DisplayServer.window_get_size(), "window_mode": DisplayServer.window_get_mode(), "root_size": root.size, "scale_3d": root.scaling_3d_scale,
		"scaling_mode": root.scaling_3d_mode, "msaa": root.msaa_3d, "taa": root.use_taa, "screen_aa": root.screen_space_aa, "vsync": DisplayServer.window_get_vsync_mode(),
		"max_fps": Engine.max_fps}
	if graphics != null:
		d["preset"] = graphics.get("CurrentPresetName")
		d["atlas"] = graphics.get("EffectiveDirectionalShadowAtlasSize")
	if env != null:
		d["ssao"] = env.ssao_enabled
		d["ssr"] = env.ssr_enabled
		d["glow"] = env.glow_enabled
		d["sdfgi"] = env.sdfgi_enabled
		d["volumetric_fog"] = env.volumetric_fog_enabled
	if monitor != null:
		d["monitor"] = {"enabled": monitor.get("Enabled"), "detailed_gpu": monitor.get("DetailedGpuEnabled"), "history_s": monitor.get("HistorySeconds"),
			"budget_mib": monitor.get("MemoryBudgetMiB"), "refresh_s": monitor.get("RefreshSeconds")}
	var bridge := game.get_node_or_null("SunshineCloudBridge")
	if bridge != null:
		d["clouds"] = {"res_scale": bridge.get("CloudResolutionScale"), "steps": bridge.get("RaymarchSteps"), "interleave": bridge.get("CloudInterleave")}
	if trees != null:
		d["trees"] = {"sun_shadow_m": trees.get("TreeSunShadowDistanceMeters"), "near_detail_start_m": trees.get("TreeNearDetailFadeStartMeters"),
			"near_detail_end_m": trees.get("TreeNearDetailFadeEndMeters"), "mid_detail_start_m": trees.get("TreeMidDetailFadeStartMeters"),
			"billboard_start_m": trees.get("TreeBillboardFadeStartMeters"), "billboard_end_m": trees.get("TreeBillboardFadeEndMeters")}
	var lod := game.get_node_or_null("TerrainLodCoordinator")
	if lod != null:
		d["lod_preset"] = lod.get("CurrentPresetName")
	return d
