extends SceneTree
## Rivers performance probe (docs/work-orders/terrain-vegetation/T2.md, "rivers perf investigation"). TEST ONLY; run it through tools/rivers_perf_probe.ps1
## (isolated APPDATA, GPU queue, summary table). It refuses to run on the real roaming profile.
## Loads a NEW certified world with a seed through the real loading screen (generation 1, with or without the water.rivers review feature), teleports to a pose,
## samples a 15 s "arrival_streaming" window, waits until proxy, tree, grass and global builds are idle (the settle time), stands still and samples timed windows.
## Every window records: frame times, viewport GPU times, the opaque / transparent pass bracket, GPU markers, every CPU telemetry scope (main thread and workers),
## GC counts and allocation, draw calls, primitives and a per-level water census. Before every window the Live Tuning drawer is forced closed and the pose is
## checked (and restored when it drifted), because stray keyboard or mouse input once opened the drawer and moved the view; each window carries "valid" and "invalid_reasons".
##   --rivers=0|1 (1)   --seed=N (1337)   --out=<absolute dir> (required; result.json and stills go here)
##   --around=m --across=m --lift=m --yaw=deg --pitch=deg   pose; the defaults are the river-mouth spot of the 2026-11-12 play test (ARC 23410.5 km, ACROSS 192.4 km, 600 m up)
##   --target-height=1440   3D render height. The window is asked for 2560x1440; when the desktop gives less, the 3D scale is raised so the 3D buffers have this height (0 = leave)
##   --still-seconds=60 --window=20   wait after settle, and the length of each timed window
##   --cases=base,base_repeat   any of: base, base_repeat, drawer_open, lod_hud_on, ssr_off, refraction_off, coarse_water_hidden (16 m and coarser), inland_replacement_off (T2R-12d A/B: every level draws its inland water under finer levels, as before), water_hidden, proxies_hidden
##   --stills=1          nine stills at the pose after the windows (eight headings at pitch -35, one at -85)
##   --level-stills=1    instead of the windows: top-down stills with the water of one proxy level at a time (lv_*.png, 1280x720)
##   --ablate=1          instead of the windows: hide one proxy level or node group at a time (GPU cost of each), then stop one top-level node at a time (allocation source)
## Prints RIVERS_PERF_PROBE_PROBE=PASS|FAIL.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const COMMON := preload("res://tools/shadow_harness_common.gd")
const TIMING := preload("res://tools/LandscapeTimingBridge.cs")
const PASS_EFFECT := preload("res://tools/landscape_pass_timestamp_effect.gd")
const FRAME_DIAG := preload("res://tools/RuntimeFrameDiagnostics.cs")
const LOAD_TIMEOUT_MS := 420000

var _args := {}
var _loader: Node
var _game: Node
var _failure := ""
var _timing: RefCounted
var _diag: RefCounted
var _hide_material: ShaderMaterial
var _hidden: Array = []
var _result := {}
var _out := ""
var _render_scale := 1.0


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


## Keeps the 3D buffers at the target height when the desktop could not give the window that size (bilinear supersampling; FSR modes are left alone).
func _apply_render_scale() -> void:
	if absf(_render_scale - 1.0) < 0.001 or root.scaling_3d_mode != Viewport.SCALING_3D_MODE_BILINEAR:
		return
	if absf(root.scaling_3d_scale - _render_scale) > 0.001:
		root.scaling_3d_scale = _render_scale


## Closes the Live Tuning drawer and restores the pose when it drifted. Returns what it found.
func _guard(explorer: Node, around: float, vertical: float, across: float, yaw: float, pitch: float, keep_drawer := false) -> Dictionary:
	var note := {}
	var drawer_node := _game.get_node_or_null("LiveTuningDrawer")
	if drawer_node != null and not keep_drawer:
		drawer_node.call("SetBenchmarkDrawerOpen", false)
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	_apply_render_scale()
	var now_pose: Dictionary = COMMON.pose_now(_game)
	note["pose_before"] = now_pose
	note["drift_m"] = Vector3(float(now_pose["around"]) - around, float(now_pose["vertical"]) - vertical, float(now_pose["across"]) - across).length()
	note["turn_deg"] = maxf(absf(wrapf(float(now_pose["yaw"]) - yaw, -180.0, 180.0)), absf(float(now_pose["pitch"]) - pitch))
	if float(note["drift_m"]) > 1.0 or float(note["turn_deg"]) > 0.5:
		explorer.call("TeleportToCanonicalMeters", around, vertical, across, yaw, pitch, true)
		note["repositioned"] = true
		await COMMON.settle(self, 3.0)
	return note


## Marks a finished window: the pose must still be the target pose and the frame rate must be plausible (a blocked presentation gave 0.6 fps once).
func _validate(window: Dictionary, note: Dictionary, around: float, vertical: float, across: float, yaw: float, pitch: float) -> void:
	var after: Dictionary = COMMON.pose_now(_game)
	note["pose_after"] = after
	note["window_focused"] = DisplayServer.window_is_focused()
	var reasons: Array = []
	var drift := Vector3(float(after["around"]) - around, float(after["vertical"]) - vertical, float(after["across"]) - across).length()
	var turn := maxf(absf(wrapf(float(after["yaw"]) - yaw, -180.0, 180.0)), absf(float(after["pitch"]) - pitch))
	if drift > 1.0 or turn > 0.5:
		reasons.append("pose moved during the window (%.1f m, %.1f deg)" % [drift, turn])
	if float(window["fps"]) < 5.0:
		reasons.append("frame rate under 5 fps (presentation blocked or another process on the GPU)")
	var drawer_scope: Dictionary = (window["cpu_scopes"] as Dictionary).get("Diagnostics UI refresh", {})
	if int(drawer_scope.get("n", 0)) > 0 and str(window.get("case", "")) != "drawer_open":
		reasons.append("Live Tuning drawer diagnostics refreshed during the window")
	window["note"] = note
	window["valid"] = reasons.is_empty()
	window["invalid_reasons"] = reasons


func _write() -> void:
	var file := FileAccess.open(_out.path_join("result.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(_result, "  "))
		file.close()


func _stats(values: Array) -> Dictionary:
	var clean: Array = []
	for v in values:
		if v != null and is_finite(float(v)):
			clean.append(float(v))
	if clean.is_empty():
		return {"n": 0}
	clean.sort()
	var n := clean.size()
	var total := 0.0
	for v in clean:
		total += v
	return {"n": n, "mean": total / n, "p50": clean[n / 2], "p95": clean[mini(n - 1, int(n * 0.95))], "max": clean[n - 1], "sum": total}


func _levels() -> Array:
	var found: Array = []
	var stack: Array = [_game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node.has_method("HasActiveWaterSectorAt"):
			found.append(node)
			continue
		for child in node.get_children():
			stack.append(child)
	found.sort_custom(func(a, b): return int(a.get("SampleSpacingMeters")) < int(b.get("SampleSpacingMeters")))
	return found


func _census() -> Array:
	var rows: Array = []
	for level in _levels():
		var sectors := 0
		var visible := 0
		var water_child_visible := 0
		var water_mesh_visible := 0
		var water_tris := 0
		for child in level.get_children():
			if not (child is MeshInstance3D) or not String(child.name).begins_with("Sector_"):
				continue
			sectors += 1
			var node := child as MeshInstance3D
			if node.visible:
				visible += 1
			var ocean := node.get_node_or_null("OceanSurface") as MeshInstance3D
			if ocean != null and ocean.visible and ocean.mesh != null and node.visible:
				water_child_visible += 1
				if ocean.mesh.get_surface_count() > 0:
					water_tris += int(ocean.mesh.surface_get_array_index_len(0) / 3)
			elif node.visible and node.mesh != null and node.mesh.get_surface_count() >= 2:
				water_mesh_visible += 1
				water_tris += int(node.mesh.surface_get_array_index_len(1) / 3)
		rows.append({"spacing": int(level.get("SampleSpacingMeters")), "label": str(level.get("LodLabel")), "streaming": bool(level.get("StreamingActive")), "sectors": sectors,
			"visible": visible, "water_child_visible": water_child_visible, "water_mesh_visible": water_mesh_visible, "water_tris": water_tris,
			"pending_uploads": int(level.get("PendingUploadCount")), "workers": int(level.get("ActiveWorkerSlots"))})
	return rows


func _hide_water(min_spacing: int) -> int:
	var count := 0
	for level in _levels():
		if int(level.get("SampleSpacingMeters")) < min_spacing:
			continue
		for child in level.get_children():
			if not (child is MeshInstance3D) or not String(child.name).begins_with("Sector_"):
				continue
			var node := child as MeshInstance3D
			var ocean := node.get_node_or_null("OceanSurface") as MeshInstance3D
			if ocean != null and ocean.visible:
				ocean.visible = false
				_hidden.append([ocean, "child"])
				count += 1
			elif node.mesh != null and node.mesh.get_surface_count() >= 2 and node.get_surface_override_material(1) == null:
				node.set_surface_override_material(1, _hide_material)
				_hidden.append([node, "surface"])
				count += 1
	return count


## T2R-12d: per level, the inland and ocean replacement uniforms of its water material (read from the first sector that has one).
func _replacement_report() -> Dictionary:
	var report := {}
	for level in _levels():
		for child in level.get_children():
			if not (child is MeshInstance3D) or not String(child.name).begins_with("Sector_"):
				continue
			var node := child as MeshInstance3D
			var ocean := node.get_node_or_null("OceanSurface") as MeshInstance3D
			var material: Material = null
			if ocean != null and ocean.material_override != null:
				material = ocean.material_override
			elif node.mesh != null and node.mesh.get_surface_count() >= 2:
				material = node.mesh.surface_get_material(1)
			if material is ShaderMaterial:
				var shader_material := material as ShaderMaterial
				report[str(level.get("SampleSpacingMeters"))] = {"inland": shader_material.get_shader_parameter("inland_replacement_enabled"), "ocean": shader_material.get_shader_parameter("ocean_replacement_coverage_enabled"),
					"distance": shader_material.get_shader_parameter("ocean_replacement_distance_meters")}
				break
	return report


func _ocean_materials() -> Array:
	var materials: Array = []
	for level in _levels():
		for child in level.get_children():
			if not (child is MeshInstance3D) or not String(child.name).begins_with("Sector_"):
				continue
			var node := child as MeshInstance3D
			var ocean := node.get_node_or_null("OceanSurface") as MeshInstance3D
			var material: Material = null
			if ocean != null and ocean.material_override != null:
				material = ocean.material_override
			elif node.mesh != null and node.mesh.get_surface_count() >= 2:
				material = node.mesh.surface_get_material(1)
			if material is ShaderMaterial and not materials.has(material):
				materials.append(material)
				break
	return materials


func _shot(name: String) -> void:
	await COMMON.settle(self, 1.0)
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	image.resize(1280, 720, Image.INTERPOLATE_LANCZOS)
	image.save_png(_out.path_join(name + ".png"))


func _level_stills(explorer: Node, around: float, vertical: float, across: float) -> void:
	var drawer := _game.get_node_or_null("LiveTuningDrawer") as CanvasLayer
	if drawer != null:
		drawer.visible = false
	explorer.call("TeleportToCanonicalMeters", around, vertical, across, 0.0, -85.0, true)
	await COMMON.settle(self, 3.0)
	_result["level_census"] = _census()
	await _shot("lv_all")
	_hide_water(0)
	await _shot("lv_no_water")
	_restore_water()
	for level in _levels():
		var spacing := int(level.get("SampleSpacingMeters"))
		if spacing > 256:
			continue
		# water of this level only
		for other in _levels():
			if int(other.get("SampleSpacingMeters")) == spacing:
				continue
			for child in other.get_children():
				if not (child is MeshInstance3D) or not String(child.name).begins_with("Sector_"):
					continue
				var node := child as MeshInstance3D
				var ocean := node.get_node_or_null("OceanSurface") as MeshInstance3D
				if ocean != null and ocean.visible:
					ocean.visible = false
					_hidden.append([ocean, "child"])
				elif node.mesh != null and node.mesh.get_surface_count() >= 2 and node.get_surface_override_material(1) == null:
					node.set_surface_override_material(1, _hide_material)
					_hidden.append([node, "surface"])
		await _shot("lv_water_only_%04dm" % spacing)
		_restore_water()
	var changed: Array = []
	for material in _ocean_materials():
		var shader_material := material as ShaderMaterial
		if bool(shader_material.get_shader_parameter("geomorph_enabled")):
			shader_material.set_shader_parameter("geomorph_enabled", false)
			changed.append(shader_material)
	_result["nomorph_materials"] = changed.size()
	await _shot("lv_water_nomorph")
	for material in changed:
		(material as ShaderMaterial).set_shader_parameter("geomorph_enabled", true)
	explorer.call("TeleportToCanonicalMeters", around, vertical, across, 45.0, -35.0, true)
	await _shot("lv_oblique_all")
	_write()


func _compact(window: Dictionary) -> Dictionary:
	var markers: Dictionary = window["gpu_markers"]
	var gc: Dictionary = window["gc_delta"]
	return {"fps": window["fps"], "frame_p50": window["frame_ms"].get("p50", NAN), "frame_p95": window["frame_ms"].get("p95", NAN), "frame_max": window["frame_ms"].get("max", NAN),
		"gpu_root_p50": window["viewport_gpu_ms"]["root"].get("p50", NAN), "opaque_p50": markers.get("BRACKET:opaque", {}).get("p50", NAN),
		"transparent_p50": markers.get("BRACKET:post_opaque_to_transparent_end", {}).get("p50", NAN), "draws": window["draw_calls"].get("p50", NAN), "prims": window["primitives"].get("p50", NAN),
		"alloc_mb_per_s": float(gc["allocated_bytes"]) / 1e6 / maxf(0.001, float(window["seconds"])), "gc0": gc["gen0_collections"], "gc_pause_ms": gc["total_gc_pause_ms"]}


func _alloc_rate(seconds: float) -> Dictionary:
	var before: Dictionary = _diag.call("CaptureCounters")
	var start := Time.get_ticks_usec()
	var frames := 0
	while (Time.get_ticks_usec() - start) * 0.000001 < seconds:
		await process_frame
		frames += 1
	var after: Dictionary = _diag.call("CaptureCounters")
	var elapsed := (Time.get_ticks_usec() - start) * 0.000001
	return {"alloc_mb_per_s": (float(after["allocated_bytes"]) - float(before["allocated_bytes"])) / 1e6 / elapsed, "gc0": float(after["gen0_collections"]) - float(before["gen0_collections"]),
		"gc_pause_ms": float(after["total_gc_pause_ms"]) - float(before["total_gc_pause_ms"]), "fps": frames / elapsed}


func _ablate(rids: Dictionary) -> void:
	var out := {}
	var far_meshes := 0
	var far_names := {}
	var stack: Array = [_game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is MeshInstance3D and String(node.name).begins_with("ScaledFarLod") and (node as MeshInstance3D).mesh != null:
			far_meshes += 1
			far_names[String(node.name).substr(0, 14)] = int(far_names.get(String(node.name).substr(0, 14), 0)) + 1
		if not node.has_method("HasActiveWaterSectorAt"):
			for child in node.get_children():
				stack.append(child)
	var far := _game.get_node_or_null("ScaledFarViewport")
	out["far"] = {"regional_meshes": far_meshes, "names": far_names, "global_geometry_ready": far.get("GlobalGeometryReady") if far != null else null,
		"pending_terrain_sectors": far.get("PendingTerrainSectorCount") if far != null else null}
	var cases: Array = []
	var base: Dictionary = await _sample(8.0, rids)
	var row := _compact(base)
	row["case"] = "base"
	cases.append(row)
	var targets: Array = []
	for level in _levels():
		if int(level.get("ActiveSectorCount")) > 0:
			targets.append(level)
	for name in ["BiomeDecorations", "StandingGrass", "Terrain3DGameplay", "SmoothTerrainGrid", "ChunkGridManager", "GlobalTerrainLod3", "AnalyticGlobalRing", "CoarseSdfLod1Pilot", "RingCloudLayer",
			"EmitterSites", "EmitterGrid", "ContainmentField", "AtmosphereProjectors", "WeatherLung", "ScaleAnchors", "LocalWeatherEffects"]:
		var node := _game.get_node_or_null(name)
		if node is Node3D:
			targets.append(node)
	for target in targets:
		var node3d := target as Node3D
		if not node3d.visible:
			continue
		node3d.visible = false
		var window: Dictionary = await _sample(5.0, rids)
		node3d.visible = true
		row = _compact(window)
		row["case"] = "hide:" + String(node3d.name)
		cases.append(row)
		print("RIVERS_PERF_PROBE ablate %s gpu=%.2f opaque=%.2f transparent=%.2f prims=%.0f draws=%.0f" % [row["case"], row["gpu_root_p50"], row["opaque_p50"], row["transparent_p50"], row["prims"], row["draws"]])
	var again: Dictionary = await _sample(8.0, rids)
	row = _compact(again)
	row["case"] = "base_repeat"
	cases.append(row)
	out["visibility"] = cases
	_result["ablation"] = out
	_write()
	# allocation sources: stop processing one node at a time
	var allocs: Array = []
	var a: Dictionary = await _alloc_rate(4.0)
	a["case"] = "base"
	allocs.append(a)
	var previous := _game.process_mode
	_game.process_mode = Node.PROCESS_MODE_DISABLED
	a = await _alloc_rate(4.0)
	a["case"] = "whole_world_not_processing"
	allocs.append(a)
	_game.process_mode = previous
	await COMMON.settle(self, 1.0)
	for child in _game.get_children():
		if child.process_mode == Node.PROCESS_MODE_DISABLED or child.has_method("HasActiveWaterSectorAt"):
			continue
		var mode := child.process_mode
		child.process_mode = Node.PROCESS_MODE_DISABLED
		a = await _alloc_rate(1.6)
		child.process_mode = mode
		a["case"] = "stop:" + String(child.name)
		allocs.append(a)
	a = await _alloc_rate(4.0)
	a["case"] = "base_repeat"
	allocs.append(a)
	out["alloc"] = allocs
	_result["ablation"] = out
	_write()


func _restore_water() -> void:
	for entry in _hidden:
		var node: Variant = entry[0]
		if not is_instance_valid(node):
			continue
		if str(entry[1]) == "child":
			(node as MeshInstance3D).visible = true
		else:
			(node as MeshInstance3D).set_surface_override_material(1, null)
	_hidden.clear()


func _streaming_idle() -> bool:
	var coordinator := _game.get_node_or_null("TerrainLodCoordinator")
	if coordinator != null and not bool(coordinator.get("StreamingSettled")):
		return false
	return COMMON.builds_idle(_game)


func _wait_idle(timeout_s: float, stable_frames: int) -> Dictionary:
	var start := Time.get_ticks_msec()
	var stable := 0
	var proxies_first := -1.0
	var coordinator := _game.get_node_or_null("TerrainLodCoordinator")
	while (Time.get_ticks_msec() - start) * 0.001 < timeout_s:
		await process_frame
		if proxies_first < 0.0 and coordinator != null and bool(coordinator.get("StreamingSettled")):
			proxies_first = (Time.get_ticks_msec() - start) * 0.001
		if _streaming_idle():
			stable += 1
			if stable >= stable_frames:
				return {"idle": true, "seconds": (Time.get_ticks_msec() - start) * 0.001, "proxies_first_settled_s": proxies_first}
		else:
			stable = 0
	return {"idle": false, "seconds": (Time.get_ticks_msec() - start) * 0.001, "proxies_first_settled_s": proxies_first}


func _sample(seconds: float, rids: Dictionary) -> Dictionary:
	for i in 30:
		await RenderingServer.frame_post_draw
		_timing.call("Sample")
	_timing.call("ResetWindow")
	var gc0: Dictionary = _diag.call("CaptureCounters")
	var frame_ms: Array = []
	var gpu := {}
	var cpu_render: Array = []
	var draws: Array = []
	var prims: Array = []
	var process_ms: Array = []
	for key in rids:
		gpu[key] = []
	var start := Time.get_ticks_usec()
	var last := start
	while (Time.get_ticks_usec() - start) * 0.000001 < seconds:
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		frame_ms.append((now - last) * 0.001)
		last = now
		for key in rids:
			(gpu[key] as Array).append(RenderingServer.viewport_get_measured_render_time_gpu(rids[key]))
		cpu_render.append(RenderingServer.viewport_get_measured_render_time_cpu(rids["root"]))
		draws.append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		prims.append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		process_ms.append(Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0)
		_timing.call("Sample")
	var elapsed := (Time.get_ticks_usec() - start) * 0.000001
	var gc1: Dictionary = _diag.call("CaptureCounters")
	var gpu_window: Dictionary = _timing.call("Window")
	var cpu_window: Dictionary = _timing.call("CpuWindow")
	var markers := {}
	for key in gpu_window:
		if str(key) != "_meta":
			markers[str(key)] = _stats(Array(gpu_window[key]))
	var scopes := {}
	for key in cpu_window:
		if str(key) != "_meta":
			var s := _stats(Array(cpu_window[key]))
			s["ms_per_frame"] = float(s.get("sum", 0.0)) / maxf(1.0, float(frame_ms.size()))
			scopes[str(key)] = s
	var gpu_stats := {}
	for key in gpu:
		gpu_stats[key] = _stats(gpu[key])
	var gc := {}
	for key in gc1:
		gc[key] = float(gc1[key]) - float(gc0[key])
	var explorer := _game.get_node_or_null("Explorer")
	return {"seconds": elapsed, "frames": frame_ms.size(), "fps": frame_ms.size() / elapsed, "frame_ms": _stats(frame_ms), "viewport_gpu_ms": gpu_stats, "root_cpu_render_ms": _stats(cpu_render),
		"process_ms": _stats(process_ms), "draw_calls": _stats(draws), "primitives": _stats(prims), "gpu_markers": markers, "gpu_meta": gpu_window.get("_meta", {}), "cpu_scopes": scopes,
		"cpu_meta": cpu_window.get("_meta", {}), "gc_delta": gc, "streaming_idle_at_end": _streaming_idle(), "census": _census(),
		"altitude_above_terrain_m": float(explorer.get("CurrentAltitudeAboveTerrainMeters")) if explorer != null else NAN,
		"scaling_3d_scale": root.scaling_3d_scale, "scaling_3d_mode": root.scaling_3d_mode, "window": [root.size.x, root.size.y], "msaa": root.msaa_3d, "taa": root.use_taa}


func _run() -> void:
	_out = String(_args.get("out", ""))
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if _out.is_empty() or user_dir.begins_with(real_roaming):
		push_error("RIVERS_PERF_PROBE=FAIL needs --out and an isolated APPDATA (run tools/rivers_perf_probe.ps1)")
		quit(2)
		return
	DirAccess.make_dir_recursive_absolute(_out)
	var rivers := String(_args.get("rivers", "1")) == "1"
	var seed_value := float(_args.get("seed", "1337"))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	DisplayServer.window_set_position(Vector2i(0, 0))
	DisplayServer.window_set_size(Vector2i(2560, 1440))
	# No focus: keyboard input typed elsewhere must not reach the game.
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, true)
	for i in 4:
		await process_frame
	var granted := DisplayServer.window_get_size()
	var target_height := float(_args.get("target-height", "1440"))
	if target_height > 0.0 and granted.y > 0 and absf(granted.y - target_height) > 2.0:
		_render_scale = target_height / float(granted.y)
	print("RIVERS_PERF_PROBE window=%dx%d render_scale=%.4f" % [granted.x, granted.y, _render_scale])
	var session := root.get_node_or_null("WorldSession")
	if session == null or not session.select_slot(1):
		push_error("RIVERS_PERF_PROBE=FAIL isolated world slot unavailable")
		quit(3)
		return
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	current_scene = launcher
	launcher.call("_on_certified_preset_pressed")
	launcher.get("terrain_backend_selector").select(2)
	launcher.call("_on_terrain_backend_selected", 2)
	(launcher.get("terrain_seed_spin") as SpinBox).value = seed_value
	launcher.set("_selected_world_generation", 1)
	launcher.set("_selected_review_features", PackedStringArray(["water.rivers"]) if rivers else PackedStringArray())
	print("RIVERS_PERF_PROBE launcher generation=%s review=%s seed=%s" % [launcher.get("_selected_world_generation"), launcher.get("_selected_review_features"), (launcher.get("terrain_seed_spin") as SpinBox).value])
	var open_msec := Time.get_ticks_msec()
	launcher.call("_start_game")
	_loader = root.get_node_or_null("RingWorldLoading")
	if _loader == null:
		push_error("RIVERS_PERF_PROBE=FAIL loading screen did not appear")
		quit(1)
		return
	_loader.world_ready.connect(func(world: Node): _game = world)
	_loader.loading_failed.connect(func(message: String): _failure = message)
	var deadline := Time.get_ticks_msec() + LOAD_TIMEOUT_MS
	while _game == null and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	_result = {"rivers": rivers, "seed": seed_value, "open_to_ready_ms": Time.get_ticks_msec() - open_msec, "adapter": RenderingServer.get_video_adapter_name(),
		"debug_build": OS.is_debug_build(), "pda_preload_env": OS.get_environment("RINGWORLD_PDA_DISABLE_PRELOAD")}
	var report: Dictionary = _loader.get_diagnostics()
	var marks: Array = []
	for mark in report.get("marks", []):
		marks.append({"name": mark.get("name", ""), "at_ms": mark.get("at_ms", 0)})
	_result["load_marks"] = marks
	_result["load_elapsed_ms"] = report.get("elapsed_ms", 0)
	if _game == null:
		_result["error"] = _failure if not _failure.is_empty() else "load timed out"
		_write()
		push_error("RIVERS_PERF_PROBE=FAIL " + str(_result["error"]))
		quit(4)
		return
	print("RIVERS_PERF_PROBE world ready after %d ms" % int(_result["open_to_ready_ms"]))
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	var explorer := _game.get_node("Explorer")
	var graphics := _game.get_node_or_null("GraphicsQuality")
	var monitor := _game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	_result["window_after_load"] = [root.size.x, root.size.y]
	_apply_render_scale()
	_result["render"] = {"window": [root.size.x, root.size.y], "scaling_3d_scale": root.scaling_3d_scale, "scaling_3d_mode": root.scaling_3d_mode,
		"render_height": root.size.y * root.scaling_3d_scale, "requested_scale": _render_scale}
	_result["graphics_preset"] = str(graphics.get("CurrentPresetName")) if graphics != null else "?"

	# ---- pose
	var around := float(_args.get("around", "23410500"))
	var across := float(_args.get("across", "192400"))
	var lift := float(_args.get("lift", "600"))
	var yaw := float(_args.get("yaw", "0"))
	var pitch := float(_args.get("pitch", "-15"))
	explorer.call("TeleportToCanonicalMeters", around, 3000.0, across, yaw, pitch, true)
	await COMMON.settle(self, 3.0)
	var probe_alt := float(explorer.get("CurrentAltitudeAboveTerrainMeters"))
	var ground := 3000.0 - probe_alt if is_finite(probe_alt) and probe_alt > 0.0 and probe_alt < 12000.0 else 0.0
	var vertical := ground + lift
	explorer.call("TeleportToCanonicalMeters", around, vertical, across, yaw, pitch, true)
	var teleport_msec := Time.get_ticks_msec()
	_result["pose"] = {"around_m": around, "across_m": across, "ground_m": ground, "vertical_m": vertical, "yaw": yaw, "pitch": pitch, "probe_altitude_m": probe_alt}
	print("RIVERS_PERF_PROBE pose ground=%.1f vertical=%.1f" % [ground, vertical])

	# ---- instrumentation
	_diag = FRAME_DIAG.new()
	_timing = TIMING.new()
	_timing.call("Begin", true)
	var rids := {"root": root.get_viewport_rid()}
	var stack: Array = [_game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is SubViewport:
			rids[str(_game.get_path_to(node))] = (node as SubViewport).get_viewport_rid()
		for child in node.get_children():
			stack.append(child)
	for key in rids:
		RenderingServer.viewport_set_measure_render_time(rids[key], true)
	var world_environment := _game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if world_environment != null:
		if world_environment.compositor == null:
			world_environment.compositor = Compositor.new()
		var effects := world_environment.compositor.compositor_effects.duplicate()
		for callback_type in [CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_OPAQUE, CompositorEffect.EFFECT_CALLBACK_TYPE_POST_OPAQUE, CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT]:
			effects.append(PASS_EFFECT.make(callback_type))
		world_environment.compositor.compositor_effects = effects
	var hide_shader := Shader.new()
	hide_shader.code = "shader_type spatial;\nrender_mode unshaded;\nvoid vertex() { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }\n"
	_hide_material = ShaderMaterial.new()
	_hide_material.shader = hide_shader

	if String(_args.get("level-stills", "0")) == "1":
		_result["windows"] = []
		var idle_first: Dictionary = await _wait_idle(240.0, 120)
		_result["settle"] = idle_first
		await _level_stills(explorer, around, vertical, across)
		_timing.call("End")
		await _shutdown()
		print("RIVERS_PERF_PROBE=PASS level stills")
		quit(0)
		return

	if String(_args.get("ablate", "0")) == "1":
		_result["windows"] = []
		var idle_ablate: Dictionary = await _wait_idle(240.0, 120)
		_result["settle"] = idle_ablate
		await COMMON.settle(self, float(_args.get("still-seconds", "30")))
		await _ablate(rids)
		_timing.call("End")
		await _shutdown()
		print("RIVERS_PERF_PROBE=PASS ablation")
		quit(0)
		return

	# ---- arrival: first seconds after the teleport (streaming), then settle, then stand still
	var arrival_note: Dictionary = await _guard(explorer, around, vertical, across, yaw, pitch)
	var arrival: Dictionary = await _sample(15.0, rids)
	arrival["case"] = "arrival_streaming"
	_validate(arrival, arrival_note, around, vertical, across, yaw, pitch)
	_result["windows"] = [arrival]
	print("RIVERS_PERF_PROBE case=arrival_streaming fps=%.1f frame_p95=%.2f max=%.2f valid=%s" % [arrival["fps"], arrival["frame_ms"]["p95"], arrival["frame_ms"]["max"], arrival["valid"]])
	_write()
	var idle: Dictionary = await _wait_idle(240.0, 120)
	idle["since_teleport_s"] = (Time.get_ticks_msec() - teleport_msec) * 0.001
	_result["settle"] = idle
	print("RIVERS_PERF_PROBE settle idle=%s after %.1f s since teleport" % [idle["idle"], idle["since_teleport_s"]])
	await COMMON.settle(self, float(_args.get("still-seconds", "60")))
	_result["time_of_day"] = {"sun_elevation_deg": _game.get_node("RingDayNightCycle").get("SunElevationDegrees") if _game.get_node_or_null("RingDayNightCycle") != null else null}

	# ---- timed windows
	var window_s := float(_args.get("window", "20"))
	var cases := String(_args.get("cases", "base,base_repeat")).split(",")
	var drawer_node := _game.get_node_or_null("LiveTuningDrawer")
	var lod_hud := _game.get_node_or_null("LodDebugHud") as CanvasLayer
	for label in cases:
		var note: Dictionary = await _guard(explorer, around, vertical, across, yaw, pitch, String(label) == "drawer_open")
		match String(label):
			"drawer_open":
				if drawer_node != null:
					drawer_node.call("SetBenchmarkDrawerOpen", true)
			"lod_hud_on":
				if lod_hud != null:
					lod_hud.visible = true
			"ssr_off":
				graphics.set("WaterSsrEnabled", false)
				graphics.call("ApplyWaterTuning")
			"refraction_off":
				note["refraction_before"] = graphics.get("WaterRefractionStrength")
				graphics.set("WaterRefractionStrength", 0.0)
				graphics.call("ApplyWaterTuning")
			"coarse_water_hidden":
				note["hidden_nodes"] = _hide_water(16)
			"water_hidden":
				note["hidden_nodes"] = _hide_water(0)
			"inland_replacement_off":
				for material in _ocean_materials():
					(material as ShaderMaterial).set_shader_parameter("inland_replacement_enabled", false)
			"proxies_hidden":
				for level in _levels():
					(level as Node3D).visible = false
		await COMMON.settle(self, 2.0)
		var window: Dictionary = await _sample(window_s, rids)
		window["case"] = String(label)
		window["replacement_uniforms"] = _replacement_report()
		if String(_args.get("case-stills", "0")) == "1":
			await RenderingServer.frame_post_draw
			root.get_texture().get_image().save_png(_out.path_join("case_%s_%d.png" % [String(label), (_result["windows"] as Array).size()]))
		_validate(window, note, around, vertical, across, yaw, pitch)
		(_result["windows"] as Array).append(window)
		print("RIVERS_PERF_PROBE case=%s fps=%.1f frame_p50=%.2f p95=%.2f max=%.2f gpu_root_p50=%.2f draws=%.0f valid=%s" % [label, window["fps"], window["frame_ms"]["p50"], window["frame_ms"]["p95"],
			window["frame_ms"]["max"], window["viewport_gpu_ms"]["root"].get("p50", NAN), window["draw_calls"].get("p50", NAN), window["valid"]])
		match String(label):
			"drawer_open":
				if drawer_node != null:
					drawer_node.call("SetBenchmarkDrawerOpen", false)
					Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
			"lod_hud_on":
				if lod_hud != null:
					lod_hud.visible = false
			"ssr_off":
				graphics.set("WaterSsrEnabled", true)
				graphics.call("ApplyWaterTuning")
			"refraction_off":
				graphics.set("WaterRefractionStrength", note["refraction_before"])
				graphics.call("ApplyWaterTuning")
			"coarse_water_hidden", "water_hidden":
				_restore_water()
			"inland_replacement_off":
				for material in _ocean_materials():
					(material as ShaderMaterial).set_shader_parameter("inland_replacement_enabled", true)
			"proxies_hidden":
				for level in _levels():
					(level as Node3D).visible = true
		_write()

	if monitor != null:
		_result["slowdown_report"] = str(monitor.call("CaptureSlowdown"))

	# ---- stills
	if String(_args.get("stills", "0")) == "1":
		var shots := [[0.0, -35.0], [90.0, -35.0], [180.0, -35.0], [270.0, -35.0], [45.0, -35.0], [135.0, -35.0], [225.0, -35.0], [315.0, -35.0], [0.0, -85.0]]
		for shot in shots:
			explorer.call("TeleportToCanonicalMeters", around, vertical, across, float(shot[0]), float(shot[1]), true)
			await COMMON.settle(self, 1.5)
			await RenderingServer.frame_post_draw
			var image := root.get_texture().get_image()
			image.save_png(_out.path_join("still_yaw%03d_pitch%d.png" % [int(shot[0]), int(absf(float(shot[1])))]))
	_timing.call("End")
	_write()
	await _shutdown()
	var invalid := 0
	for entry in _result["windows"]:
		if not bool(entry.get("valid", true)):
			invalid += 1
	if invalid > 0:
		push_error("RIVERS_PERF_PROBE=FAIL %d window(s) invalid; see invalid_reasons in result.json" % invalid)
		quit(1)
		return
	print("RIVERS_PERF_PROBE=PASS rivers=%s" % rivers)
	quit(0)


func _shutdown() -> void:
	var surface: Node = _game.get_node_or_null("Terrain3DGameplay")
	var saves: Node = _game.get_node_or_null("WorldSaveManager")
	if saves != null:
		saves.call("CancelWorldLoad")
	if is_instance_valid(surface):
		surface.call("CancelStartup", "Probe complete.")
	var owners: Array[Node] = []
	for child: Node in _game.get_children():
		if child.has_method("BeginLoadingShutdown"):
			child.call("BeginLoadingShutdown")
			owners.append(child)
	_game.get_node("TerrainLodCoordinator").set_process(false)
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline:
		var drained := not is_instance_valid(surface) or bool(surface.get("StartupCancellationReady"))
		for owner: Node in owners:
			drained = drained and bool(owner.get("LoadingShutdownReady"))
		if drained:
			break
		await process_frame
	_game.queue_free()
	await process_frame
	await process_frame
