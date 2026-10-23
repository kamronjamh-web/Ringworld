extends SceneTree
## Phase C (C5) measurement: stationary cost of the live menu world at cruise altitude, and what the menu suspension set did.
##
## ALWAYS run through tools/menu_suspend_perf_test.ps1 (isolated APPDATA with a COPY of a saved slot). Refuses to run when user:// is not a
## phase_c_live profile. Run it windowed (GPU); it works against builds with and without the C5 suspension set so the same script gives the
## before and after numbers.
##
## Flow: warm the world behind the menu, wait for the loiter, hold the director (stationary at cruise), settle, then sample
## --seconds (default 30) of wall frame time and the root viewport's measured render times, save a screenshot, and write
## res://.godot/phase_c_live/c5/<label>/results.json with the numbers and the state of each lever.
##   -- --label=after --seconds=30 --settle=25

const MENU := preload("res://scenes/StartMenu.tscn")
const WARM_TIMEOUT_MS := 300000
const REVEAL_TIMEOUT_MS := 180000
const WATCHDOG_USEC := 900 * 1000000

var _args := {}
var _menu: Control
var _warm: Node

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

func _process(_delta: float) -> bool:
	if Time.get_ticks_usec() > WATCHDOG_USEC:
		push_error("watchdog: the suspend perf test exceeded its time budget")
		quit(3)
		return true
	return false

func _wait_until(condition: Callable, timeout_ms: int) -> bool:
	var deadline := Time.get_ticks_msec() + timeout_ms
	while Time.get_ticks_msec() < deadline:
		if condition.call():
			return true
		await process_frame
	return condition.call()

func _wait_seconds(seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame

func _percentile(sorted: Array, fraction: float) -> float:
	if sorted.is_empty():
		return 0.0
	return float(sorted[clampi(int(floor(fraction * (sorted.size() - 1))), 0, sorted.size() - 1)])

func _stats(values: Array) -> Dictionary:
	if values.is_empty():
		return {"mean": 0.0, "p50": 0.0, "p95": 0.0, "max": 0.0}
	var sorted := values.duplicate()
	sorted.sort()
	var sum := 0.0
	for v in values:
		sum += float(v)
	return {"mean": sum / values.size(), "p50": _percentile(sorted, 0.5), "p95": _percentile(sorted, 0.95), "max": float(sorted[sorted.size() - 1])}

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("phase_c_live"):
		push_error("Refusing to run: user:// (%s) is not an isolated phase_c_live APPDATA. Use menu_suspend_perf_test.ps1." % user_dir)
		quit(2)
		return
	var label := String(_args.get("label", "run"))
	var seconds := float(_args.get("seconds", "30"))
	var settle := float(_args.get("settle", "25"))
	var out_dir := ProjectSettings.globalize_path("res://.godot/phase_c_live/c5").path_join(label)
	DirAccess.make_dir_recursive_absolute(out_dir)
	var results := {"label": label, "adapter": RenderingServer.get_video_adapter_name(), "window": DisplayServer.window_get_size()}
	_menu = MENU.instantiate()
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	await process_frame
	_warm = root.get_node_or_null("RingWarmWorld")
	if _warm == null or not await _wait_until(func() -> bool: return int(_warm.state) == 2, WARM_TIMEOUT_MS):
		push_error("warm world did not become ready")
		quit(1)
		return
	if not await _wait_until(func() -> bool: return bool(_menu.get("_cover_retired")), REVEAL_TIMEOUT_MS):
		push_error("the cover was never retired")
		quit(1)
		return
	await process_frame
	var director: Node = _menu.flight_director
	var bridge: Node = _warm.bridge
	var world: Node = _warm.world
	if director == null:
		push_error("no flight director")
		quit(1)
		return
	# Stationary at cruise: freeze the loiter where it is.
	director.Hold()
	results["has_suspension_api"] = bridge.has_method("ApplyMenuSuspension")
	results["menu_suspension_active"] = bool(bridge.get("MenuSuspensionActive")) if bridge.has_method("ApplyMenuSuspension") else false
	await _wait_seconds(settle)
	var vp := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp, true)
	await _wait_seconds(1.0)
	var wall: Array = []
	var gpu: Array = []
	var cpu: Array = []
	var last := Time.get_ticks_usec()
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await process_frame
		var now := Time.get_ticks_usec()
		wall.append(float(now - last) / 1000.0)
		last = now
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(vp))
		cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(vp))
	results["wall_ms"] = _stats(wall)
	results["gpu_ms"] = _stats(gpu)
	results["render_cpu_ms"] = _stats(cpu)
	results["frames"] = wall.size()
	var telemetry: Dictionary = bridge.call("GetExplorerTelemetry")
	results["altitude_m"] = telemetry.get("altitude_m", -1.0)
	# What each lever is doing now
	var levers := {}
	var sun := world.get_node_or_null("StarLight") as DirectionalLight3D
	if sun != null:
		levers["sun_shadow_enabled"] = sun.shadow_enabled
		levers["sun_shadow_max_distance"] = sun.directional_shadow_max_distance
		levers["sun_shadow_fade_start"] = sun.directional_shadow_fade_start
	var graphics := world.get_node_or_null("GraphicsQuality")
	if graphics != null:
		levers["altitude_shadow_suspended"] = graphics.get("AltitudeShadowSuspended")
		levers["altitude_height_shadow_suspended"] = graphics.get("AltitudeHeightShadowSuspended")
		levers["local_shadow_reason"] = graphics.get("LocalShadowHandoffReason")
	var water := world.get_node_or_null("WaterFftController")
	if water != null:
		levers["water_external_dormant"] = water.get("ExternalDormant")
	var terrain := world.get_node_or_null("Terrain3DGameplay")
	if terrain != null:
		levers["terrain3d_focus_override"] = terrain.get("CoverageFocusOverrideActive")
		levers["terrain3d_chase_suspended"] = terrain.get("StreamingChaseSuspended")
	results["levers"] = levers
	var image := root.get_texture().get_image()
	image.save_png(out_dir.path_join("view.png"))
	var file := FileAccess.open(out_dir.path_join("results.json"), FileAccess.WRITE)
	file.store_string(JSON.stringify(results, "  "))
	file.close()
	print("MENU_SUSPEND_PERF ", JSON.stringify(results))
	await _menu._release_warm_world()
	print("MENU_SUSPEND_PERF=DONE")
	quit(0)
