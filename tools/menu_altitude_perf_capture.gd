extends SceneTree
## Phase C P0 measurement: cold-start time and menu-altitude (16 km) cost of a saved Standard world.
##
## ALWAYS run with an isolated APPDATA holding a COPY of a saved slot (see tools/menu_altitude_perf_capture.ps1).
## The script refuses to run when user:// resolves into the real roaming profile.
##
##   Godot_v4.8-dev7_mono_win64_console.exe --path . --script res://tools/menu_altitude_perf_capture.gd -- \
##       --out=phase_c_p0/run1 [--slot=1] [--load-only] [--plan=a:s16,a:m,a:s23,b:s16,b:m,c:s16,c:m,c:s23]
##       [--altitude=16000] [--high-altitude=23000] [--pitch=-25] [--speed=1100] [--settle=20]
##       [--sample=20] [--move-seconds=30] [--cycle=0.6|keep] [--pause-clocks]
##
## Plan steps are VARIANT:KIND. Variants are cumulative levers applied at runtime:
##   a = quality as loaded, b = a + low-cost clouds (half-res raymarch, 64 steps, cloud shadow generation off),
##   c = b + LOD coordinator Low preset + Terrain3D GenerationWorkerCount 1 (grass/trees are only reported;
##       they suspend themselves above 350 m / 12 km AGL).
## Kinds: s16 = stationary at the save spot at --altitude, s23 = stationary at --high-altitude,
##   m = scripted noclip along the ring at --speed (W held, altitude held with E/Q bang-bang, camera tilted --pitch).
## The camera is tilted on the Camera3D node, not the Explorer head, so the noclip path stays level with the ring.
## Results: <out>/results.json and PNG screenshots (res://.godot/<out>/), plus WORLD_LOADING_REPORT on stdout.

const WORLD_LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const PROBE := preload("res://tools/MenuAltitudePerfProbe.cs")
const LOAD_TIMEOUT_MS := 600000
const BAND_METERS := 350.0

var _args := {}
var _loader: Node
var _game: Node
var _failure := ""
var _probe
var _out_dir := ""
var _results := {"steps": []}
var _viewports: Array = []
var _held := {}
var _yaw := 0.0
var _save_around := 0.0
var _save_across := 0.0
var _terrain_ref := 0.0
var _camera: Camera3D
var _camera_base := Vector3.ZERO

func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			if eq > 0:
				_args[text.substr(2, eq - 2)] = text.substr(eq + 1)
			else:
				_args[text.substr(2)] = "1"
	call_deferred("_run")

func _arg_f(key: String, fallback: float) -> float:
	return float(_args[key]) if _args.has(key) else fallback

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("phase_c_p0"):
		push_error("Refusing to run: user:// (%s) is not an isolated phase_c_p0 APPDATA. Use menu_altitude_perf_capture.ps1." % user_dir)
		quit(2)
		return
	var out_name := String(_args.get("out", "phase_c_p0/manual"))
	_out_dir = ProjectSettings.globalize_path("res://.godot/" + out_name)
	DirAccess.make_dir_recursive_absolute(_out_dir)
	_results["adapter"] = RenderingServer.get_video_adapter_name()
	_results["adapter_vendor"] = RenderingServer.get_video_adapter_vendor()
	_results["api"] = RenderingServer.get_video_adapter_api_version()
	_results["engine"] = Engine.get_version_info().get("string", "")
	_results["args"] = _args
	_results["user_dir"] = user_dir
	_results["window"] = DisplayServer.window_get_size()
	print("P0 adapter=", _results["adapter"], " user_dir=", user_dir)
	var slot := int(_arg_f("slot", 1))
	var session := root.get_node_or_null("WorldSession")
	if session == null or not session.select_slot(slot):
		push_error("slot unavailable")
		quit(3)
		return
	session.request_load_selected_world()
	var metadata: Dictionary = session.get_slot_metadata(slot)
	var settings: Dictionary = metadata.get("generation_settings", {})
	if str(settings.get("terrain_backend", "")) != "surface_runtime_generation_v1":
		push_error("slot %d is not a runtime-surface Standard world" % slot)
		quit(3)
		return
	_loader = WORLD_LOADING.ensure(root)
	_loader.world_ready.connect(func(world: Node): _game = world)
	_loader.loading_failed.connect(func(message: String): _failure = message)
	var boot_ms := Time.get_ticks_msec()
	_loader.open_saved_world({"world_title": str(metadata.get("name", "World")),
		"seed": int(settings.get("terrain_seed", 1337)),
		"radius_m": float(settings.get("radius_km", 5000.0)) * 1000.0,
		"circumference_m": TAU * float(settings.get("radius_km", 5000.0)) * 1000.0,
		"width_m": float(settings.get("ring_width_km", 318.0)) * 1000.0, "is_existing": true})
	var deadline := Time.get_ticks_msec() + LOAD_TIMEOUT_MS
	while _game == null and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	if _game == null:
		push_error("world did not become ready: " + _failure)
		_results["error"] = _failure
		_write_results()
		quit(4)
		return
	var ready_ms := Time.get_ticks_msec()
	_results["load"] = {"engine_ms_at_open": boot_ms, "engine_ms_at_ready": ready_ms, "open_to_ready_ms": ready_ms - boot_ms,
		"loader_report": _loader.get_diagnostics()}
	print("P0 world ready; open_to_ready_ms=", ready_ms - boot_ms)
	# Let the reveal fade finish and the world reach its normal running state.
	for i in 90:
		await process_frame
	_probe = PROBE.new()
	if not _probe.Bind(_game):
		push_error("probe bind failed")
		quit(5)
		return
	var pose: Dictionary = _probe.GetPose()
	_save_around = pose.around
	_save_across = pose.across
	_terrain_ref = float(pose.vertical) - float(pose.altitude_computed)
	_results["load"]["save_pose"] = pose
	_results["load"]["memory_at_ready"] = _memory_snapshot()
	_results["load"]["state_as_loaded"] = _probe.GetState()
	_prepare_runtime()
	if _args.has("load-only"):
		await _wait_seconds(float(_arg_f("idle", 8.0)))
		_results["load"]["memory_after_idle"] = _memory_snapshot()
		_write_results()
		_finish(0)
		return
	await _calibrate_yaw()
	for step in String(_args.get("plan", "a:s16,a:m,a:s23,b:s16,b:m,c:s16,c:m,c:s23")).split(",", false):
		var parts := step.split(":")
		await _run_step(parts[0], parts[1])
		_write_results()
	_finish(0)

func _prepare_runtime() -> void:
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var cycle := String(_args.get("cycle", "auto"))
	var day := _game.get_node_or_null("RingDayNightCycle")
	if cycle == "auto":
		_cycle_fraction = _probe.SetBrightestCycleFraction()
	elif cycle != "keep":
		_cycle_fraction = float(cycle)
		_probe.SetCycleFraction(_cycle_fraction)
	_results["daylight_factor"] = _probe.GetDaylightFactor()
	if _args.has("pause-clocks"):
		if day != null:
			day.set("Paused", true)
		var volumetric := _game.get_node_or_null("RingVolumetricClouds")
		if volumetric != null:
			volumetric.set("CloudSimulationPaused", true)
	_camera = _game.get_node("Explorer/Head/Camera3D")
	_camera_base = _camera.rotation_degrees
	_results["runtime"] = {"vsync": DisplayServer.window_get_vsync_mode(), "max_fps": Engine.max_fps,
		"clocks_paused": _args.has("pause-clocks"), "cycle": cycle, "cycle_fraction": _cycle_fraction}

func _wait_seconds(seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame

func _key(code: int, down: bool) -> void:
	if _held.get(code, false) == down:
		return
	_held[code] = down
	var event := InputEventKey.new()
	event.keycode = code
	event.physical_keycode = code
	event.pressed = down
	Input.parse_input_event(event)

func _release_all() -> void:
	for code in [KEY_W, KEY_E, KEY_Q]:
		_key(code, false)

## Holds the target altitude above terrain with E/Q and optionally advances with W.
func _drive(target_altitude: float, forward: bool) -> void:
	var pose: Dictionary = _probe.GetPose()
	var error: float = float(pose.altitude_cached) - target_altitude
	_key(KEY_E, error < -BAND_METERS)
	_key(KEY_Q, error > BAND_METERS)
	_key(KEY_W, forward and absf(error) < 3000.0)
	_camera.rotation_degrees = Vector3(_camera_base.x + float(_args.get("pitch", -25.0)), _camera_base.y, _camera_base.z)

func _place(around: float, altitude: float, yaw: float) -> void:
	_release_all()
	_probe.PlaceNoclip(around, _terrain_ref + altitude, _save_across, yaw, float(_arg_f("speed", 1100.0)))
	_camera.rotation_degrees = Vector3(_camera_base.x + float(_args.get("pitch", -25.0)), _camera_base.y, _camera_base.z)

## Finds the explorer yaw whose forward noclip motion advances +around.
func _calibrate_yaw() -> void:
	var best := 0.0
	var best_delta := -INF
	for k in 4:
		var yaw := k * PI * 0.5
		_place(_save_around, 16000.0, yaw)
		await _wait_seconds(0.4)
		var before: Dictionary = _probe.GetPose()
		_key(KEY_W, true)
		await _wait_seconds(0.5)
		_key(KEY_W, false)
		var after: Dictionary = _probe.GetPose()
		var delta := fposmod(float(after.around) - float(before.around) + 15707968.0, 31415936.0) - 15707968.0
		print("P0 yaw calibration yaw=%.2f delta_around=%.1f speed=%.1f" % [yaw, delta, float(after.noclip_speed)])
		if delta > best_delta:
			best_delta = delta
			best = yaw
	_yaw = best
	_results["yaw_radians"] = best
	_results["yaw_calibration_delta_around_m"] = best_delta
	_release_all()
	await _wait_seconds(0.3)

func _apply_variant(variant: String) -> void:
	match variant:
		"a":
			_probe.RestoreClouds()
			_probe.RestoreLod()
			_probe.RestoreTerrainWorkers()
		"b":
			_probe.RestoreLod()
			_probe.RestoreTerrainWorkers()
			_probe.ApplyLowCostClouds()
		"c":
			_probe.ApplyLowCostClouds()
			_probe.ApplyLodLow()
			_probe.SetTerrainWorkers(1)

var _current_variant := ""
var _cycle_fraction := -1.0

func _run_step(variant: String, kind: String) -> void:
	var changed := variant != _current_variant
	if changed:
		_apply_variant(variant)
		_current_variant = variant
	if _cycle_fraction >= 0.0:
		_probe.SetCycleFraction(_cycle_fraction)
	var target := float(_arg_f("high-altitude", 23000.0)) if kind == "s23" else float(_arg_f("altitude", 16000.0))
	var moving := kind == "m"
	var start_around := _save_around - (float(_arg_f("speed", 1100.0)) * float(_arg_f("move-seconds", 30.0)) * 0.55 if moving else 0.0)
	_place(start_around, target, _yaw)
	var settle := float(_arg_f("settle", 20.0)) + (float(_arg_f("variant-settle", 10.0)) if changed else 0.0)
	var end := Time.get_ticks_msec() + int(settle * 1000.0)
	while Time.get_ticks_msec() < end:
		_drive(target, false)
		await process_frame
	var seconds := float(_arg_f("move-seconds", 30.0)) if moving else float(_arg_f("sample", 20.0))
	var step := await _sample("%s_%s" % [variant, kind], seconds, target, moving)
	step["variant"] = variant
	step["kind"] = kind
	step["target_altitude"] = target
	_results["steps"].append(step)

func _refresh_viewports() -> void:
	_viewports.clear()
	_viewports.append({"name": "main", "rid": root.get_viewport_rid()})
	for node in _game.find_children("*", "SubViewport", true, false):
		var viewport := node as SubViewport
		if viewport.render_target_update_mode == SubViewport.UPDATE_DISABLED:
			continue
		_viewports.append({"name": str(_game.get_path_to(viewport)), "rid": viewport.get_viewport_rid()})
	for entry in _viewports:
		RenderingServer.viewport_set_measure_render_time(entry.rid, true)

func _sample(label: String, seconds: float, target: float, moving: bool) -> Dictionary:
	_refresh_viewports()
	await _wait_seconds(0.5)
	var wall := PackedFloat64Array()
	var process_ms := PackedFloat64Array()
	var physics_ms := PackedFloat64Array()
	var cpu_sum := PackedFloat64Array()
	var gpu_sum := PackedFloat64Array()
	var draws := PackedFloat64Array()
	var altitudes := PackedFloat64Array()
	var speeds := PackedFloat64Array()
	var per_vp_gpu := {}
	var per_vp_cpu := {}
	for entry in _viewports:
		per_vp_gpu[entry.name] = PackedFloat64Array()
		per_vp_cpu[entry.name] = PackedFloat64Array()
	_probe.ResetMonitor()
	var started := Time.get_ticks_usec()
	var last := started
	var frame := 0
	var pose_start: Dictionary = _probe.GetPose()
	while float(Time.get_ticks_usec() - started) / 1e6 < seconds:
		_drive(target, moving)
		await process_frame
		var now := Time.get_ticks_usec()
		frame += 1
		if frame > 3:
			wall.append(float(now - last) / 1000.0)
			process_ms.append(Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0)
			physics_ms.append(Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000.0)
			var cpu := 0.0
			var gpu := 0.0
			for entry in _viewports:
				var c := RenderingServer.viewport_get_measured_render_time_cpu(entry.rid)
				var g := RenderingServer.viewport_get_measured_render_time_gpu(entry.rid)
				per_vp_cpu[entry.name].append(c)
				per_vp_gpu[entry.name].append(g)
				cpu += c
				gpu += g
			cpu_sum.append(cpu)
			gpu_sum.append(gpu)
			draws.append(RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME))
			var pose: Dictionary = _probe.GetPose()
			altitudes.append(pose.altitude_cached)
			speeds.append(pose.travel_speed)
		last = now
	var pose_end: Dictionary = _probe.GetPose()
	var monitor_summary: Dictionary = _probe.GetMonitorSummary()
	var result := {"label": label, "frames": wall.size(), "seconds": seconds, "moving": moving,
		"wall_ms": _stats(wall), "process_ms": _stats(process_ms), "physics_ms": _stats(physics_ms),
		"viewport_cpu_sum_ms": _stats(cpu_sum), "viewport_gpu_sum_ms": _stats(gpu_sum), "draw_calls": _stats(draws),
		"altitude_agl_m": _stats(altitudes), "travel_speed_mps": _stats(speeds), "pose_start": pose_start, "pose_end": pose_end,
		"monitor": monitor_summary, "daylight_factor": _probe.GetDaylightFactor(), "viewports": {}}
	for entry in _viewports:
		result["viewports"][entry.name] = {"gpu_ms": _stats(per_vp_gpu[entry.name]), "cpu_ms": _stats(per_vp_cpu[entry.name])}
	# Screenshots stall the frame, so they are taken after the measured window while the drive continues.
	var shot_times := [1.0, 3.0, 5.0] if moving else [0.5]
	var photo_started := Time.get_ticks_msec()
	for i in shot_times.size():
		while float(Time.get_ticks_msec() - photo_started) / 1000.0 < float(shot_times[i]):
			_drive(target, moving)
			await process_frame
		await _screenshot("%s_t%d" % [label, i])
	_release_all()
	result["state"] = _probe.GetState()
	result["memory"] = _memory_snapshot()
	result["gpu_telemetry"] = _nvidia_smi()
	print("P0 STEP ", label, " wall_mean=%.2f p95=%.2f gpu_sum=%.2f cpu_sum=%.2f daylight=%.2f" % [
		result.wall_ms.mean, result.wall_ms.p95, result.viewport_gpu_sum_ms.mean, result.viewport_cpu_sum_ms.mean, result.daylight_factor])
	return result

func _screenshot(name: String) -> void:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image != null:
		image.save_png(_out_dir.path_join(name + ".png"))

func _stats(values: PackedFloat64Array) -> Dictionary:
	if values.is_empty():
		return {"mean": 0.0, "p50": 0.0, "p95": 0.0, "p99": 0.0, "max": 0.0, "min": 0.0}
	var sorted := values.duplicate()
	sorted.sort()
	var total := 0.0
	for v in values:
		total += v
	var n := sorted.size()
	return {"mean": total / n, "min": sorted[0], "p50": sorted[n / 2], "p95": sorted[mini(n - 1, int(n * 0.95))],
		"p99": sorted[mini(n - 1, int(n * 0.99))], "max": sorted[n - 1]}

func _memory_snapshot() -> Dictionary:
	var process: Dictionary = _probe.GetProcessMemory() if _probe != null else {}
	var snapshot := {
		"process": process, "godot_static_mb": OS.get_static_memory_usage() / 1048576.0,
		"rs_video_mem_mb": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_VIDEO_MEM_USED) / 1048576.0,
		"rs_texture_mem_mb": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TEXTURE_MEM_USED) / 1048576.0,
		"rs_buffer_mem_mb": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_BUFFER_MEM_USED) / 1048576.0,
		"gpu_process_dedicated_mb": -1.0}
	var output := []
	var command := "(Get-Counter '\\GPU Process Memory(pid_%d*)\\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples | ForEach-Object { [int]($_.CookedValue/1MB) }" % OS.get_process_id()
	if OS.execute("powershell", ["-NoProfile", "-Command", command], output, true) == 0 and output.size() > 0:
		snapshot["gpu_process_dedicated_mb"] = float(String(output[0]).strip_edges().get_slice("\n", 0))
	return snapshot

func _nvidia_smi() -> Dictionary:
	var output := []
	if OS.execute("nvidia-smi", ["--query-gpu=name,utilization.gpu,memory.used,clocks.gr,temperature.gpu,power.draw,clocks_throttle_reasons.active", "--format=csv,noheader"], output, true) != 0:
		return {}
	return {"csv": String(output[0]).strip_edges() if output.size() > 0 else ""}

func _write_results() -> void:
	var file := FileAccess.open(_out_dir.path_join("results.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(_results, "  "))
		file.close()
	print("P0_RESULT_FILE=", _out_dir.path_join("results.json"))

func _finish(code: int) -> void:
	_release_all()
	quit(code)
