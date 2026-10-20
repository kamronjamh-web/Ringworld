extends SceneTree

## Shadow revamp S0 - per-shadow-type GPU/CPU attribution matrix (docs/shadow-revamp-plan.md, S0). TEST ONLY: no production behaviour is changed; every label
## uses an existing runtime switch or a node property that the test restores afterwards.
##
## RUN (windowed Vulkan, no other Godot process, editor closed; kill stray Godot processes first):
##   Godot_console.exe --path . -- res://tools/shadow_attribution_test.gd [flags]
##   (or: godot --path . --script res://tools/shadow_attribution_test.gd -- [flags])
## Flags:
##   --quick                 short validation run: 1 round, 2 s windows, 0.75 s warmup, poses ground_horizon + alt_12km, 30 s idle timeout
##   --rounds=N              rounds per pose (default 3). One round = baseline, X1, baseline, X2, ..., baseline (A-X-A-X-...-A); odd rounds run the labels reversed.
##   --seconds=S             sampling window length (default 9 s; plan: 8-10 s)
##   --warmup=S              seconds between applying a label and opening the window (default 3)
##   --poses=a,b,c           subset of: ground_horizon, forest_noon, emitter_rim, coast(TODO), dawn_5deg, alt_1km, alt_12km, night_planet_lit, eclipse_hub (default: all but coast)
##   --labels=a,b            subset of: sun_map_off, planet_map_off, cloud_shadow_gen_off, cloud_receivers_off, macro_off, tree_shadows_off, casters_500m, pssm2_500m,
##                           atlas_2048, hs_on (heightfield + 6 km map), hs_receivers_off (producer only), hs_near (heightfield + 400 m PSSM2 near map), contact_on (S6 contact shadows, default settings), baseline_repeat (baseline is always the A window)
##   --out=<dir>             output root (default <project>/.godot/shadow_baseline/<machine>/); results go to <out>/attribution/<stamp>/ (report.json + power_log.csv)
##   --res=WxH               window size (default 1920x1080, the plan's reference resolution)
##   --idle-timeout=S        max wait per pose for zero builds in flight (default 120)
##   --no-engine             skip Godot engine timestamps (only RingPerformanceGpu markers + viewport timings)
##   --no-power-log          do not spawn nvidia-smi
##   --keep-vsync            keep the project's vsync/fps cap (default: vsync off, max_fps 0 for GPU timing)
## Outputs: report.json (windows with median/IQR/etc. per metric, per-window power verdict, per-label A-X-A deltas), power_log.csv, console table.
## Metrics per window: root/far/all viewport measured GPU+CPU ms, RenderingServer.get_frame_setup_time_cpu(), draw calls, the RingPerformanceGpu markers
## (CloudShadowNear/Far, TerrainCloudSunVisibility, ...) and Godot's own engine timestamps (ENG:<name> regions, see the report's engine_timestamp_names).
## The harness disables the scene's RingPerformanceMonitor (it re-configures the same timestamp bridge every frame and would drain its events).
##
## MACHINE RULE: the authoritative baseline is the A1000 laptop (P0 / 35 W / ~5500 MHz memory per sample, else the window is invalid and excluded from
## the medians). On any other GPU everything is recorded but stamped `machine: <tag> (non-authoritative)` and nothing is rejected.
## TODO poses (no fixture yet): coast. Labels with no effect at a pose (e.g. planet_map_off by day) are flagged `no_effect_at_pose` from the state snapshots.
## Full A1000 run time: see the console estimate printed at start (about poses x rounds x (2 x labels + 1) windows x (window + warmup) seconds).

const COMMON := preload("res://tools/shadow_harness_common.gd")
const BRIDGE := preload("res://tools/ShadowTimingBridge.cs")
const LABELS := ["sun_map_off", "planet_map_off", "cloud_shadow_gen_off", "cloud_receivers_off", "macro_off", "tree_shadows_off", "casters_500m", "pssm2_500m", "atlas_2048", "hs_on", "hs_receivers_off", "hs_near", "contact_on", "baseline_repeat"]
const DELTA_METRICS := ["root_gpu_ms", "far_gpu_ms", "root_cpu_ms", "frame_setup_cpu_ms"]

var game: Node
var explorer: Node
var surface: Node
var sun: DirectionalLight3D
var planet: DirectionalLight3D
var graphics: Node
var cloud_bridge: Node
var clouds_res: Object
var atmosphere: Node
var trees: Node
var height_shadow: Node
var coordinator: Node
var monitor: Node
var timing: RefCounted
var machine := {}
var failed := false
var measured_viewports: Array[Viewport] = []
var vp_paths := {}
var orig := {}
var restore_stack: Array[Callable] = []
var report := {}
var windows: Array = []
var power_pid := -1
var power_path := ""
var out_dir := ""
var quick := false
var window_seconds := 9.0
var warmup_seconds := 3.0
var rounds := 3
var idle_timeout := 120.0


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required"); return
	quick = COMMON.has_flag("--quick")
	rounds = int(COMMON.arg_value("--rounds=", "1" if quick else "3"))
	window_seconds = float(COMMON.arg_value("--seconds=", "2" if quick else "9"))
	warmup_seconds = float(COMMON.arg_value("--warmup=", "0.75" if quick else "3"))
	idle_timeout = float(COMMON.arg_value("--idle-timeout=", "30" if quick else "120"))
	machine = COMMON.machine_info()
	print("SHADOW_ATTRIBUTION ", machine["label"], " adapter=", machine["adapter"])
	var stamp := Time.get_datetime_string_from_system(false).replace(":", "-").replace("T", "_")
	out_dir = COMMON.output_dir(machine, "attribution/" + stamp)
	if not COMMON.has_flag("--keep-vsync"):
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		Engine.max_fps = 0
	var res := COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		root.size = Vector2i(int(res[0]), int(res[1]))

	var pose_names: PackedStringArray = COMMON.csv_arg("--poses=")
	if pose_names.is_empty():
		pose_names = PackedStringArray(["ground_horizon", "alt_12km"]) if quick else PackedStringArray(COMMON.ATTRIBUTION_POSES)
	var label_names: PackedStringArray = COMMON.csv_arg("--labels=")
	if label_names.is_empty():
		label_names = PackedStringArray(LABELS)
	for label in label_names:
		if not label in LABELS:
			_fail("unknown label " + label); return
	for pose in pose_names:
		if not COMMON.POSES.has(pose):
			_fail("unknown or TODO pose " + pose + " (coast has no fixture yet)"); return
	var per_pose_seconds := rounds * (2 * label_names.size() + 1) * (window_seconds + warmup_seconds + 0.3)
	print("SHADOW_ATTRIBUTION plan: %d poses x %d rounds x %d windows of %.1f s (+%.1f s warmup) = about %.0f min of sampling (+ pose setup)" % [pose_names.size(), rounds, 2 * label_names.size() + 1, window_seconds, warmup_seconds, per_pose_seconds * pose_names.size() / 60.0])

	report = {"status": "RUNNING", "tool": "shadow_attribution_test", "machine": machine["label"], "machine_info": machine, "authoritative": machine["authoritative"],
		"args": OS.get_cmdline_user_args(), "quick": quick, "rounds": rounds, "window_seconds": window_seconds, "warmup_seconds": warmup_seconds, "labels": label_names,
		"poses_requested": pose_names, "todo_poses": COMMON.TODO_POSES, "windows": [], "poses": {}, "skipped_poses": {}, "started_utc": Time.get_unix_time_from_system()}

	var fixture_root := "res://.godot/shadow-attribution/%d" % Time.get_ticks_usec()
	game = await COMMON.create_game(self, fixture_root)
	if game == null:
		_fail("game boot failed"); return
	if not _bind_nodes():
		return
	monitor.process_mode = Node.PROCESS_MODE_DISABLED
	COMMON.hold_world_clocks(game)
	atmosphere.set("storm_auto_exposure_enabled", false)
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await COMMON.wait_builds_idle(self, game, 180.0, 60):
		push_warning("initial settle timed out")
	_enable_viewport_timing()
	_capture_original()
	var base: Dictionary = COMMON.pose_now(game)
	report["quality"] = _quality_identity()
	report["base_pose"] = base

	timing = BRIDGE.new()
	timing.call("Begin", not COMMON.has_flag("--no-engine"))
	if not COMMON.has_flag("--no-power-log"):
		power_path = out_dir.path_join("power_log.csv")
		power_pid = COMMON.start_power_log(power_path)
		report["power_log"] = {"path": power_path, "query": COMMON.POWER_QUERY, "interval_ms": 500, "pid": power_pid}
		await COMMON.settle(self, 1.5)

	for pose_name in pose_names:
		var pose_info: Dictionary = await COMMON.go_pose(self, game, base, pose_name, 2.0 if quick else 6.0, idle_timeout)
		if pose_info.has("error"):
			report["skipped_poses"][pose_name] = pose_info
			print("SHADOW_ATTRIBUTION pose %s SKIPPED: %s" % [pose_name, str(pose_info["error"])])
			continue
		report["poses"][pose_name] = pose_info
		print("SHADOW_ATTRIBUTION pose %s ready: %s" % [pose_name, JSON.stringify(pose_info.get("time", {}))])
		if not await _run_pose(pose_name, label_names):
			return
		_write_report()

	await _finish()


func _bind_nodes() -> bool:
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	sun = game.get_node_or_null("StarLight") as DirectionalLight3D
	planet = game.get_node_or_null("PlanetLight") as DirectionalLight3D
	graphics = game.get_node_or_null("GraphicsQuality")
	cloud_bridge = game.get_node_or_null("SunshineCloudBridge")
	atmosphere = game.get_node_or_null("DaySideAtmosphere")
	trees = game.get_node_or_null("BiomeDecorations")
	height_shadow = game.get_node_or_null("TerrainHeightShadow")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	monitor = game.get_node_or_null("RingPerformanceMonitor")
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	clouds_res = driver.get("clouds_resource") if driver != null else null
	for pair in [["Explorer", explorer], ["Terrain3DGameplay", surface], ["StarLight", sun], ["GraphicsQuality", graphics], ["SunshineCloudBridge", cloud_bridge], ["DaySideAtmosphere", atmosphere], ["BiomeDecorations", trees], ["TerrainLodCoordinator", coordinator], ["RingPerformanceMonitor", monitor]]:
		if pair[1] == null:
			_fail("required node missing: " + str(pair[0])); return false
	return true


func _enable_viewport_timing() -> void:
	measured_viewports = [root]
	for node in _all_nodes(game):
		if node is SubViewport:
			measured_viewports.append(node)
	for viewport in measured_viewports:
		RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), true)
		vp_paths[str(viewport.get_viewport_rid().get_id())] = str(viewport.get_path())
	report["engine_timestamp_viewports"] = vp_paths


func _all_nodes(node: Node) -> Array[Node]:
	var result: Array[Node] = [node]
	for child in node.get_children():
		result.append_array(_all_nodes(child))
	return result


# ------------------------------------------------------------------ labels

func _capture_original() -> void:
	orig = {"sun_requested": bool(graphics.get("RequestedSunShadowsEnabled")), "atlas": int(graphics.get("EffectiveDirectionalShadowAtlasSize")),
		"macro": bool(graphics.get("EffectiveTerrainMacroShadowsEnabled")), "sun_mode": sun.directional_shadow_mode, "sun_max": sun.directional_shadow_max_distance,
		"split1": sun.directional_shadow_split_1, "split2": sun.directional_shadow_split_2, "split3": sun.directional_shadow_split_3, "fade": sun.directional_shadow_fade_start,
		"blend": sun.directional_shadow_blend_splits, "planet_cast": bool(atmosphere.get("planet_light_cast_shadows")),
		"cloud_gen": bool(cloud_bridge.get("GpuShadowGenerationEnabled")), "cloud_receivers": bool(cloud_bridge.get("CloudShadowReceiversEnabled")),
		"tree_dist": float(trees.get("TreeSunShadowDistanceMeters")), "tree_batches": int(trees.get("MaximumTreeSunShadowBatches")),
		"coverage": float(coordinator.get("TerrainShadowCoverageMeters")),
		"hs_enabled": bool(graphics.get("EffectiveHeightShadowEnabled")), "hs_near_enabled": bool(graphics.get("EffectiveHeightShadowNearMapEnabled")), "contact_enabled": bool(graphics.get("EffectiveContactShadowEnabled"))}
	report["original_state"] = orig.duplicate()


func _restore() -> void:
	while not restore_stack.is_empty():
		var undo: Callable = restore_stack.pop_back()
		undo.call()


## Applies one matrix label through existing switches / node properties and pushes the undo. Returns false for an unknown label.
func _apply(label: String) -> bool:
	match label:
		"baseline", "baseline_repeat":
			pass
		"sun_map_off":
			graphics.call("SetShadowTuningOverride", "Enabled", false)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "Enabled", orig["sun_requested"]))
		"planet_map_off":
			atmosphere.set("planet_light_cast_shadows", false)
			restore_stack.append(func(): atmosphere.set("planet_light_cast_shadows", orig["planet_cast"]))
		"cloud_shadow_gen_off":
			cloud_bridge.set("GpuShadowGenerationEnabled", false)
			if clouds_res != null and clouds_res.has_method("set_gpu_cloud_shadow_generation_enabled"):
				clouds_res.call("set_gpu_cloud_shadow_generation_enabled", false)
			restore_stack.append(func():
				cloud_bridge.set("GpuShadowGenerationEnabled", orig["cloud_gen"])
				if clouds_res != null and clouds_res.has_method("set_gpu_cloud_shadow_generation_enabled"):
					clouds_res.call("set_gpu_cloud_shadow_generation_enabled", orig["cloud_gen"]))
		"cloud_receivers_off":
			cloud_bridge.set("CloudShadowReceiversEnabled", false)
			restore_stack.append(func(): cloud_bridge.set("CloudShadowReceiversEnabled", orig["cloud_receivers"]))
		"macro_off":
			graphics.call("SetShadowTuningOverride", "TerrainMacroEnabled", false)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "TerrainMacroEnabled", orig["macro"]))
		"tree_shadows_off":
			trees.set("TreeSunShadowDistanceMeters", 0.0)
			trees.set("MaximumTreeSunShadowBatches", 0)
			restore_stack.append(func():
				trees.set("TreeSunShadowDistanceMeters", orig["tree_dist"])
				trees.set("MaximumTreeSunShadowBatches", orig["tree_batches"]))
		"casters_500m":
			# Terrain/proxy casters limited to 500 m while the sun map keeps its range. The controller re-derives the coverage from the sun range every
			# frame, so its _process is paused for the window (restored after).
			graphics.process_mode = Node.PROCESS_MODE_DISABLED
			coordinator.call("SetTerrainShadowCoverageMeters", 500.0)
			restore_stack.append(func():
				graphics.process_mode = Node.PROCESS_MODE_INHERIT
				coordinator.call("SetTerrainShadowCoverageMeters", orig["coverage"]))
		"pssm2_500m":
			# PSSM 2 to 500 m (first split 100 m). The controller derives terrain caster coverage from the sun's max distance, so it follows.
			sun.directional_shadow_mode = DirectionalLight3D.SHADOW_PARALLEL_2_SPLITS
			sun.directional_shadow_max_distance = 500.0
			sun.directional_shadow_split_1 = 0.2
			restore_stack.append(func():
				sun.directional_shadow_mode = orig["sun_mode"]
				sun.directional_shadow_max_distance = orig["sun_max"]
				sun.directional_shadow_split_1 = orig["split1"])
		"hs_on", "hs_receivers_off", "hs_near":
			# S5 heightfield labels. The producer runs (and publishes, see _wait_height_shadow) in all three; hs_on keeps the 6 km sun map (near map off),
			# hs_receivers_off measures the producer alone (receivers read nothing, 6 km map), hs_near is the shipped configuration (400 m PSSM2 near map).
			if height_shadow == null:
				_fail("TerrainHeightShadow node missing"); return false
			graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", true)
			graphics.call("SetShadowTuningOverride", "HeightShadowNearMapEnabled", label == "hs_near")
			height_shadow.set("ReceiversEnabled", label != "hs_receivers_off")
			restore_stack.append(func():
				height_shadow.set("ReceiversEnabled", true)
				graphics.call("SetShadowTuningOverride", "HeightShadowNearMapEnabled", orig["hs_near_enabled"])
				graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", orig["hs_enabled"]))
		"contact_on":
			# S6 screen-space contact shadows (RingContactShadows compositor effect + receiver term, default settings). GPU.ContactShadow marker = the compute pass.
			graphics.call("SetShadowTuningOverride", "ContactShadowEnabled", true)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "ContactShadowEnabled", orig["contact_enabled"]))
		"atlas_2048":
			graphics.call("SetShadowTuningOverride", "AtlasSize", 2048)
			restore_stack.append(func(): graphics.call("SetShadowTuningOverride", "AtlasSize", orig["atlas"]))
		_:
			return false
	return true


func _state() -> Dictionary:
	return {"sun_shadow_enabled": sun.shadow_enabled, "sun_energy": sun.light_energy, "sun_mode": sun.directional_shadow_mode, "sun_max_distance": sun.directional_shadow_max_distance,
		"sun_split1": sun.directional_shadow_split_1, "atlas": int(graphics.get("EffectiveDirectionalShadowAtlasSize")), "macro": bool(graphics.get("EffectiveTerrainMacroShadowsEnabled")),
		"planet_energy": planet.light_energy if planet != null else 0.0, "planet_shadow": planet.shadow_enabled if planet != null else false,
		"cloud_generation": bool(cloud_bridge.get("GpuShadowGenerationEnabled")), "cloud_receivers": bool(cloud_bridge.call("IsCloudShadowProjectorEnabled")),
		"tree_shadow_renderers": int(trees.get("ActiveGpuTreeSunShadowRendererCount")), "tree_shadows_suppressed": bool(trees.get("TreeSunShadowsSuppressed")), "tree_suppression_reason": str(trees.get("TreeSunShadowSuppressionReason")), "caster_coverage_m": float(coordinator.get("TerrainShadowCoverageMeters")),
		"contact_enabled": bool(graphics.get("EffectiveContactShadowEnabled")), "contact_status": str(graphics.get("EffectiveContactShadowStatus")), "hs_enabled": bool(graphics.get("EffectiveHeightShadowEnabled")), "near_map_active": bool(graphics.get("NearSunMapActive")), "hs_receivers": bool(height_shadow.get("ReceiversEnabled")) if height_shadow != null else true,
		"handoff_suspended": bool(graphics.get("LocalSunShadowSuspended")), "handoff_reason": str(graphics.get("LocalShadowHandoffReason"))}


# ------------------------------------------------------------------ sampling

func _run_pose(pose_name: String, label_names: PackedStringArray) -> bool:
	var pose_windows: Array = []
	for round_index in rounds:
		var order: Array = Array(label_names)
		if round_index % 2 == 1:
			order.reverse()
		var sequence: Array = ["baseline"]
		for label in order:
			sequence.append(label)
			sequence.append("baseline")
		for index in sequence.size():
			var label: String = sequence[index]
			_restore()
			if not _apply(label):
				_fail("unknown label " + label); return false
			var window: Dictionary = await _sample_window(pose_name, label, round_index, index)
			if failed:
				return false
			pose_windows.append(window)
			windows.append(window)
	_aggregate_pose(pose_name, pose_windows)
	_restore()
	return true


## The heightfield labels need the first publish (fill + build, ~3 s on the 4070) and, for hs_near, the near map before the window opens.
func _wait_height_shadow(label: String) -> bool:
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
		var d: Dictionary = height_shadow.call("GetDiagnostics")
		var published: bool = int(d.get("generation", 0)) > 0 and float(d.get("crossfade", 0.0)) >= 0.999 and str(d.get("phase", "")) == "Idle"
		var near_active := bool(graphics.get("NearSunMapActive"))
		if published and ((label == "hs_near" and near_active) or (label != "hs_near" and not near_active)):
			return true
	print("SHADOW_ATTRIBUTION warning: %s did not reach its steady state within 120 s: %s" % [label, str(height_shadow.call("GetStatusText"))])
	return false


func _sample_window(pose_name: String, label: String, round_index: int, index: int) -> Dictionary:
	if label.begins_with("hs_"):
		await _wait_height_shadow(label)
	await COMMON.settle(self, warmup_seconds)
	await RenderingServer.frame_post_draw
	var state := _state()
	var builds_start := COMMON.builds_idle(game)
	timing.call("ResetWindow")
	var series := {"root_gpu_ms": [], "root_cpu_ms": [], "far_gpu_ms": [], "far_cpu_ms": [], "frame_setup_cpu_ms": [], "wall_ms": [], "draw_calls": [], "primitives": []}
	var vp_gpu := {}
	var vp_cpu := {}
	var t0 := Time.get_unix_time_from_system()
	var start := Time.get_ticks_usec()
	var frame_start := start
	while (Time.get_ticks_usec() - start) * 0.000001 < window_seconds:
		await RenderingServer.frame_post_draw
		if failed:
			return {}
		var now := Time.get_ticks_usec()
		series["wall_ms"].append((now - frame_start) * 0.001)
		frame_start = now
		var far_gpu := 0.0
		var far_cpu := 0.0
		for viewport in measured_viewports:
			if not is_instance_valid(viewport):
				continue
			var rid := viewport.get_viewport_rid()
			var gpu := RenderingServer.viewport_get_measured_render_time_gpu(rid)
			var cpu := RenderingServer.viewport_get_measured_render_time_cpu(rid)
			var key := str(viewport.get_path()).get_file()
			if viewport == root:
				series["root_gpu_ms"].append(gpu)
				series["root_cpu_ms"].append(cpu)
			else:
				if key == "FarRingViewport":
					far_gpu = gpu
					far_cpu = cpu
				if not vp_gpu.has(key):
					vp_gpu[key] = []
					vp_cpu[key] = []
				vp_gpu[key].append(gpu)
				vp_cpu[key].append(cpu)
		series["far_gpu_ms"].append(far_gpu)
		series["far_cpu_ms"].append(far_cpu)
		series["frame_setup_cpu_ms"].append(RenderingServer.get_frame_setup_time_cpu())
		series["draw_calls"].append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		series["primitives"].append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		timing.call("Sample")
	var t1 := Time.get_unix_time_from_system()
	var builds_end := COMMON.builds_idle(game)
	var result := {"pose": pose_name, "label": label, "round": round_index, "index": index, "t0_utc": t0, "t1_utc": t1, "frames": series["wall_ms"].size(),
		"state": state, "streaming_clean": builds_start and builds_end, "metrics": {}, "viewport_gpu_ms": {}, "viewport_cpu_ms": {}, "markers_ms": {}, "engine_ms": {}}
	for key in series:
		result["metrics"][key] = COMMON.stats(series[key])
	for key in vp_gpu:
		result["viewport_gpu_ms"][key] = COMMON.stats(vp_gpu[key])
		result["viewport_cpu_ms"][key] = COMMON.stats(vp_cpu[key])
	var collected: Dictionary = timing.call("Window")
	for key in collected:
		if key == "_meta":
			result["timing_meta"] = collected[key]
		elif str(key).begins_with("ENG:"):
			var engine_key := str(key).trim_prefix("ENG:")
			if engine_key.begins_with("vp_") and vp_paths.has(engine_key.trim_prefix("vp_")):
				engine_key = "vp:" + str(vp_paths[engine_key.trim_prefix("vp_")])
			result["engine_ms"][engine_key] = COMMON.stats(Array(collected[key]))
		else:
			result["markers_ms"][str(key)] = COMMON.stats(Array(collected[key]))
	print("SHADOW_ATTRIBUTION_WINDOW ", JSON.stringify({"pose": pose_name, "round": round_index, "label": label, "frames": result["frames"], "root_gpu_median": result["metrics"]["root_gpu_ms"].get("median", NAN), "iqr": result["metrics"]["root_gpu_ms"].get("iqr", NAN), "far_gpu_median": result["metrics"]["far_gpu_ms"].get("median", NAN), "setup_cpu_median": result["metrics"]["frame_setup_cpu_ms"].get("median", NAN), "sun_shadow": state["sun_shadow_enabled"], "streaming_clean": result["streaming_clean"]}))
	return result


# ------------------------------------------------------------------ aggregation

func _metric_median(window: Dictionary, group: String, name: String) -> float:
	var entry: Dictionary = window[group].get(name, {})
	return float(entry.get("median", NAN))


func _window_metric(window: Dictionary, metric: String) -> float:
	if window["metrics"].has(metric):
		return _metric_median(window, "metrics", metric)
	if metric.begins_with("marker:"):
		return _metric_median(window, "markers_ms", metric.trim_prefix("marker:"))
	if metric.begins_with("engine:"):
		return _metric_median(window, "engine_ms", metric.trim_prefix("engine:"))
	return NAN


## For each X window in a pose: delta of every metric against the mean of its two neighbouring baseline (A) windows. Written to report.poses[pose].summary.
func _aggregate_pose(pose_name: String, pose_windows: Array) -> void:
	var by_label := {}
	var metric_names: Array = DELTA_METRICS.duplicate()
	for window in pose_windows:
		for key in window["markers_ms"]:
			if not ("marker:" + key) in metric_names: metric_names.append("marker:" + key)
		for key in window["engine_ms"]:
			if not ("engine:" + key) in metric_names: metric_names.append("engine:" + key)
	for i in pose_windows.size():
		var window: Dictionary = pose_windows[i]
		var label: String = window["label"]
		if label == "baseline":
			continue
		var prev: Dictionary = pose_windows[i - 1]
		var next: Dictionary = pose_windows[i + 1]
		var entry: Dictionary = by_label.get(label, {"rounds": [], "no_effect_at_pose": null})
		var deltas := {}
		for metric in metric_names:
			var ref_a := _window_metric(prev, metric)
			var ref_b := _window_metric(next, metric)
			var value := _window_metric(window, metric)
			deltas[metric] = value - 0.5 * (ref_a + ref_b)
			deltas[metric + "@drift"] = ref_b - ref_a
		var round_entry := {"round": window["round"], "deltas": deltas, "windows": [i - 1, i, i + 1]}
		entry["rounds"].append(round_entry)
		var baseline_state: Dictionary = prev["state"]
		var same := true
		for key in window["state"]:
			# The tree renderer count moves with streaming, so only the tree label is judged by it.
			if str(window["state"][key]) != str(baseline_state.get(key)) and (not str(key).begins_with("tree_") or label == "tree_shadows_off"):
				same = false
		entry["no_effect_at_pose"] = same and label != "baseline_repeat"
		entry["state"] = window["state"]
		by_label[label] = entry
	report["poses"][pose_name]["window_indices"] = [windows.size() - pose_windows.size(), windows.size()]
	report["poses"][pose_name]["_pending_summary"] = by_label


func _finalize_summary() -> void:
	# Power verdicts need the finished nvidia-smi log; windows flagged invalid are excluded from the per-label medians (A1000 rule only).
	var rows: Array = COMMON.parse_power_log(power_path) if power_path != "" else []
	var offset := COMMON.local_offset_seconds()
	for window in windows:
		window["power"] = COMMON.power_window(rows, window["t0_utc"], window["t1_utc"], machine, offset) if power_path != "" else {"valid": true, "verdict": "power log disabled", "authoritative": machine["authoritative"]}
	report["windows"] = windows
	report["power_log_rows"] = rows.size()
	print("\nSHADOW_ATTRIBUTION_TABLE %s  (deltas vs neighbouring baseline windows, ms; median over rounds [min..max]; n = rounds used)" % machine["label"])
	print("%-18s %-22s %9s %9s %9s %9s %4s %s" % ["pose", "label", "d_root_gpu", "d_far_gpu", "d_root_cpu", "d_setup", "n", "notes"])
	for pose_name in report["poses"]:
		var pending: Dictionary = report["poses"][pose_name].get("_pending_summary", {})
		var summary := {}
		for label in pending:
			var entry: Dictionary = pending[label]
			var usable: Array = []
			for round_entry in entry["rounds"]:
				var ok := true
				for idx in round_entry["windows"]:
					if not bool(windows[int(report["poses"][pose_name]["window_indices"][0]) + int(idx)]["power"].get("valid", true)):
						ok = false
				round_entry["power_valid"] = ok
				if ok:
					usable.append(round_entry)
			var agg := {}
			for metric in entry["rounds"][0]["deltas"]:
				if str(metric).ends_with("@drift"):
					continue
				var values: Array = []
				for round_entry in usable:
					values.append(round_entry["deltas"][metric])
				agg[metric] = COMMON.stats(values)
			summary[label] = {"rounds_total": entry["rounds"].size(), "rounds_used": usable.size(), "no_effect_at_pose": entry["no_effect_at_pose"], "state": entry["state"], "delta_ms": agg, "rounds": entry["rounds"]}
			var d_root: Dictionary = agg.get("root_gpu_ms", {})
			var d_far: Dictionary = agg.get("far_gpu_ms", {})
			var d_cpu: Dictionary = agg.get("root_cpu_ms", {})
			var d_setup: Dictionary = agg.get("frame_setup_cpu_ms", {})
			var marker_note := ""
			for marker in ["marker:GPU.CloudShadowNear", "marker:GPU.CloudShadowFar", "marker:GPU.TerrainCloudSunVisibility"]:
				if agg.has(marker):
					marker_note += " %s=%.3f" % [marker.trim_prefix("marker:GPU."), float(agg[marker].get("median", NAN))]
			print("%-18s %-22s %9.3f %9.3f %9.3f %9.3f %4d %s%s" % [pose_name, label, float(d_root.get("median", NAN)), float(d_far.get("median", NAN)), float(d_cpu.get("median", NAN)), float(d_setup.get("median", NAN)), usable.size(), "NO EFFECT at pose" if entry["no_effect_at_pose"] else "", marker_note])
		report["poses"][pose_name]["summary"] = summary
		report["poses"][pose_name].erase("_pending_summary")


func _finish() -> void:
	timing.call("End")
	report["engine_timestamp_names"] = Array(timing.call("ListEngineTimestampNames"))
	print("SHADOW_ATTRIBUTION engine timestamp names (%d): %s" % [report["engine_timestamp_names"].size(), JSON.stringify(report["engine_timestamp_names"])])
	COMMON.stop_power_log(power_pid)
	await COMMON.settle(self, 0.5)
	_restore()
	_finalize_summary()
	report["status"] = "PASS"
	report["finished_utc"] = Time.get_unix_time_from_system()
	_write_report()
	for viewport in measured_viewports:
		if is_instance_valid(viewport):
			RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), false)
	print("SHADOW_ATTRIBUTION report: ", out_dir.path_join("report.json"))
	game.queue_free()
	await process_frame
	print("SHADOW_ATTRIBUTION=PASS ", machine["label"])
	quit(0)


func _write_report() -> void:
	var file := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	if file == null:
		return
	var snapshot := report.duplicate()
	if report["status"] == "RUNNING":
		snapshot["windows"] = windows
	file.store_string(JSON.stringify(snapshot, "\t"))
	file.close()


func _quality_identity() -> Dictionary:
	var cycle := game.get_node_or_null("RingDayNightCycle")
	return {"preset": graphics.get("CurrentPresetName"), "resolution": root.size, "render_scale": root.get("scaling_3d_scale"), "scaling_mode": root.get("scaling_3d_mode"), "msaa": root.get("msaa_3d"), "taa": root.get("use_taa"),
		"shadow_atlas": graphics.get("EffectiveDirectionalShadowAtlasSize"), "shadow_filter": graphics.get("EffectiveDirectionalShadowFilter"), "sun_max_distance": sun.directional_shadow_max_distance,
		"cloud_near_resolution": cloud_bridge.get("GpuNearShadowMapResolution"), "cloud_near_hz": cloud_bridge.get("GpuNearShadowUpdatesPerSecond"), "cloud_near_slices": cloud_bridge.get("GpuNearShadowSliceCount"),
		"cloud_far_hz": cloud_bridge.get("GpuFarShadowUpdatesPerSecond"), "sun_elevation": cycle.get("SunElevationDegrees") if cycle != null else NAN}


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	report["status"] = "FAIL"
	report["failure"] = message
	push_error("SHADOW_ATTRIBUTION=FAIL " + message)
	if timing != null:
		timing.call("End")
	COMMON.stop_power_log(power_pid)
	if out_dir != "":
		var snapshot := report.duplicate()
		snapshot["windows"] = windows
		var file := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
		if file != null:
			file.store_string(JSON.stringify(snapshot, "\t"))
			file.close()
	if is_instance_valid(game):
		game.queue_free()
	quit(1)
