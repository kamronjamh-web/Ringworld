extends SceneTree
## Reusable real-world performance benchmark driver (docs/perf-bench.md). Run it through tools/perf_bench.ps1, which prepares the isolated profile,
## the frozen slot 3 snapshot, the optimized C# assembly, the GPU queue and the report. It boots the REAL start menu on the isolated copy of the
## snapshot and presses Continue, as the player does (tools/tree_visibility_world.gd), then runs the scenarios of tools/fixtures/perf_bench_scenarios.json:
## per scenario, A-A rounds of one window each (stand / yaw_sweep / fly), with per-frame wall, frame setup, main-thread process, render CPU and GPU,
## draw calls, primitives, VRAM, GC, per-viewport GPU, every RingPerformanceGpu detailed interval and the key CPU scopes.
## Args (after --): --out=<abs dir> --scenarios=<abs json> [--select=all|quick|a,b] [--vsync-pass] [--stills] [--rounds=N] [--window-scale=F]
## Verdict line: PERF_BENCH=PASS when every selected scenario completed (stills and the VSync pass included), PERF_BENCH=FAIL <reason> otherwise.
## It refuses to run when user:// is not the isolated appdata_perf_bench profile, so it can never touch the player's save.

const PROBE := preload("res://tools/PerfBenchProbe.cs")
const COMMON := preload("res://tools/shadow_harness_common.gd")
const TVW := preload("res://tools/tree_visibility_world.gd")

var args := {}
var out_dir := ""
var report := {"scenarios": [], "stills": [], "events": [], "errors": [], "skipped": []}
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
	report["state"] = _state()
	report["spawn_pose"] = COMMON.pose_now(game)
	_log("booted in %.1f s; spawn pose %s" % [float(report["boot_s"]), str(report["spawn_pose"])])
	await COMMON.settle(self, 2.0)
	_write()
	# ---------------------------------------------------------------- scenarios
	var failed := 0
	for sc in chosen:
		var result := await _scenario(sc, false)
		report["scenarios"].append(result)
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
	var result := {"name": name, "kind": kind, "pose_name": String(sc["pose"]), "pose": pose, "vsync": vsync_on, "window_s": seconds, "rounds": [], "setup": {}}
	_log("scenario %s (%s, %d x %.0f s)" % [name, kind, rounds, seconds])
	_release_keys()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var speed := float(sc.get("speed_mps", 0.0))
	var began := Time.get_ticks_msec()
	_place(pose, sc, speed)
	await COMMON.settle(self, 0.4)
	var idle: bool = await COMMON.wait_builds_idle(self, game, 180.0)
	var time_info := _apply_time(_opt(sc, "time"), bool(_opt(sc, "hold_clocks")))
	if time_info.has("error"):
		result["error"] = "time of day: %s" % String(time_info["error"])
		_log("scenario %s FAILED: %s" % [name, result["error"]])
		return result
	await COMMON.settle(self, float(_opt(sc, "settle_s")))
	idle = idle and await COMMON.wait_builds_idle(self, game, 90.0)
	result["setup"] = {"idle": idle, "settle_ms": Time.get_ticks_msec() - began, "reached": COMMON.pose_now(game), "time": time_info,
		"ground_m": float(surface.call("SampleHeight", float(pose["around_m"]), float(pose["across_m"])))}
	_log("  placed, idle=%s after %d ms, sun %.1f deg" % [str(idle), int(result["setup"]["settle_ms"]), float(time_info.get("sun_elevation_deg", NAN))])
	for r in rounds:
		var tag := "%s_r%d" % [name, r]
		var w: Dictionary
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED if vsync_on else DisplayServer.VSYNC_DISABLED)
		if kind == "fly":
			if r > 0:
				DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
				_place(pose, sc, speed)
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
			w = await _window(tag, seconds, {})
		result["rounds"].append(w)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	return result


func _place(pose: Dictionary, sc: Dictionary, speed: float) -> void:
	var a := float(pose["around_m"])
	var z := float(pose["across_m"])
	var yaw := float(sc.get("yaw_deg", pose.get("yaw_deg", 0.0)))
	var pitch := float(sc.get("pitch_deg", pose.get("pitch_deg", 0.0)))
	if pose.has("vertical_m") and not sc.has("lift_m"):
		probe.PlaceNoclip(explorer, a, float(pose["vertical_m"]), z, yaw, pitch, speed)
		return
	var ground := float(surface.call("SampleHeight", a, z))
	var lift := float(sc.get("lift_m", pose.get("lift_m", 1.7)))
	if speed > 0.0:
		probe.PlaceNoclip(explorer, a, ground + lift, z, yaw, pitch, speed)
	else:
		explorer.call("TeleportToCanonicalMeters", a, ground + lift, z, yaw, pitch, true)


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
	var hidden := false
	for sc in chosen:
		if not bool(sc.get("still", false)):
			continue
		var name := String(sc["name"])
		var entry := {"name": name, "pose_name": String(sc["pose"])}
		var pose: Dictionary = spec["poses"][String(sc["pose"])]
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		_place(pose, sc, 0.0)
		await COMMON.settle(self, 0.4)
		await COMMON.wait_builds_idle(self, game, 180.0)
		var time_info := _apply_time(_opt(sc, "time"), true)
		if time_info.has("error"):
			entry["error"] = String(time_info["error"])
			report["stills"].append(entry)
			continue
		await COMMON.settle(self, 3.0)
		await COMMON.wait_builds_idle(self, game, 90.0)
		if not hidden:
			entry["hidden"] = TVW.quiet_scene(game)
			TVW.hide_overlays(game)
			hidden = true
		TVW.freeze_world(game)
		report["still_noise"] = TVW.freeze_noise(self, game, PackedStringArray(["clouds", "taa"]))
		for _i in 20:
			TVW.hold_wind()
			await process_frame
		await RenderingServer.frame_post_draw
		var img := TVW.grab(self)
		var path := dir.path_join("%s.png" % name)
		var save_err := img.save_png(path)
		entry["path"] = path
		entry["size"] = [img.get_width(), img.get_height()]
		if save_err != OK:
			entry["error"] = "save_png failed (%d)" % save_err
		report["stills"].append(entry)
		_log("still %s -> %s" % [name, path])
		_write()


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
	for r in mon_rows:
		var name := String(r["name"])
		var per_frame := float(r["mean_ms"]) * float(r["count"]) / maxf(1.0, float(frames))
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
		"m": m, "viewports": viewports, "monitor_dropped": mon.get("dropped", -1), "monitor_gpu_available": mon.get("gpu_available", false),
		"working_set_mb": mon.get("working_set_mb", -1), "managed_heap_mb": mon.get("managed_heap_mb", -1), "pose": COMMON.pose_now(game)}
	var cycle := game.get_node_or_null("RingDayNightCycle")
	if cycle != null:
		result["sun_elevation_deg"] = float(cycle.get("SunElevationDegrees"))
		result["lighting_phase"] = str(cycle.get("LightingPhaseName"))
	_log("WIN %-34s vs=%d fps=%6.1f wall p50=%6.2f p95=%6.2f p99=%6.2f max=%7.2f >33=%d gpu p50=%5.2f setup p50=%5.2f proc p50=%5.2f draws=%d busy=%.2f" % [
		tag, int(result["vsync"]), fps, float(m["wall_p50"]), float(m["wall_p95"]), float(m["wall_p99"]), float(m["wall_max"]), int(m["hitch33_count"]),
		float(m["gpu_total_p50"]), float(m["setup_p50"]), float(m["process_p50"]), int(m["draws_p50"]), float(result["busy_fraction"])])
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
