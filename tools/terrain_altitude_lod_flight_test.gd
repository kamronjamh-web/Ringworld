extends SceneTree
## WO-E live flight invariant test for docs/terrain-altitude-lod-coverage-plan.md.
##
## Loads an ISOLATED COPY of a saved Standard world, flies a scripted noclip camera to a set of altitudes and pitches,
## and at every pose casts a ray grid onto the ground (tools/TerrainAltitudeLodProbe.cs) to check, against the live
## proxy chain, that
##   * no visible ground point lacks an installed sector of the tier that owns its slant distance (holes),
##   * every proxy tier stays within 2.5x TargetPixelError and the global backdrop within 5x (projected error).
## It also records frame time, draw calls, primitives and mesh memory, and optionally plain + tier-coloured screenshots.
## Exit code 0 = every stationary pose passed (the moving leg is reported, and fails above --move-hole-fraction).
##
## Run through tools/terrain_altitude_lod_flight_test.ps1 (isolated APPDATA). Arguments after `--`:
##   --out=<name>            results folder under .godot/terrain_altitude_lod/ (default manual)
##   --slot=1                saved world slot inside the isolated profile
##   --preset=2              coordinator quality preset: 0 Low, 1 Medium, 2 High, 3 Desktop High (set explicitly per run)
##   --alts=500,2000,8000,16000,23000,60000   --pitches=-15,-45,-89
##   --shots                 plain + tier-debug-colour screenshots at every pose (review only, not goldens)
##   --move=16000            moving leg altitude (omit for none); --move-seconds=30 --speed=1100
##   --move-hole-fraction=0.02   fail threshold for the moving leg
##   --rays=24x16            ray grid (columns x rows)
##   --quiet-only            skip the invariant checks (perf + screenshots only)

const WORLD_LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const PROBE := preload("res://tools/MenuAltitudePerfProbe.cs")
const LOD_PROBE := preload("res://tools/TerrainAltitudeLodProbe.cs")
const BAND := 350.0
const LEVEL_NAMES := ["Lod1Terrain2m", "Lod2Terrain4m", "Lod3Terrain8m", "Lod4Terrain16m", "Lod5Terrain32m",
	"Lod6Terrain64m", "Lod7Terrain128m", "Lod8Terrain256m", "Lod8BridgeTerrain512m", "Lod8BridgeTerrain1024m",
	"Lod9Terrain2km", "Lod9BridgeTerrain4096m", "Lod10Terrain8km", "Lod10BridgeTerrain16384m", "Lod11Terrain32km"]
const COLORS := [Color(1,0,0), Color(1,0.5,0), Color(1,1,0), Color(0.5,1,0), Color(0,1,0), Color(0,1,0.6),
	Color(0,1,1), Color(0,0.5,1), Color(0.3,0.2,1), Color(0.6,0,1), Color(1,0,1), Color(1,0.6,0.8),
	Color(0.6,0.4,0.2), Color(1,1,1), Color(0.4,0.4,0.4)]

var _args := {}
var _game: Node
var _failure := ""
var _probe
var _lod_probe
var _out := ""
var _results := {"poses": [], "failures": []}
var _held := {}
var _save_around := 0.0
var _save_across := 0.0
var _terrain_ref := 0.0
var _camera: Camera3D
var _camera_base := Vector3.ZERO
var _width := 318000.0
var _ray_cols := 24
var _ray_rows := 16

func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var t := String(raw)
		if t.begins_with("--"):
			var eq := t.find("=")
			if eq > 0:
				_args[t.substr(2, eq - 2)] = t.substr(eq + 1)
			else:
				_args[t.substr(2)] = "1"
	call_deferred("_run")

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("terrain_altitude_lod"):
		push_error("Refusing: user:// is not an isolated terrain_altitude_lod APPDATA: " + user_dir)
		quit(2)
		return
	_out = ProjectSettings.globalize_path("res://.godot/terrain_altitude_lod/" + String(_args.get("out", "manual")))
	DirAccess.make_dir_recursive_absolute(_out)
	if _args.has("rays"):
		var parts := String(_args["rays"]).split("x")
		_ray_cols = int(parts[0])
		_ray_rows = int(parts[1])
	var slot := int(_args.get("slot", 1))
	var session := root.get_node_or_null("WorldSession")
	if session == null or not session.select_slot(slot):
		push_error("slot unavailable")
		quit(3)
		return
	session.request_load_selected_world()
	var metadata: Dictionary = session.get_slot_metadata(slot)
	var settings: Dictionary = metadata.get("generation_settings", {})
	_width = float(settings.get("ring_width_km", 318.0)) * 1000.0
	var loader = WORLD_LOADING.ensure(root)
	loader.world_ready.connect(func(w: Node): _game = w)
	loader.loading_failed.connect(func(m: String): _failure = m)
	var t0 := Time.get_ticks_msec()
	loader.open_saved_world({"world_title": str(metadata.get("name", "World")),
		"seed": int(settings.get("terrain_seed", 1337)),
		"radius_m": float(settings.get("radius_km", 5000.0)) * 1000.0,
		"circumference_m": TAU * float(settings.get("radius_km", 5000.0)) * 1000.0,
		"width_m": _width, "is_existing": true})
	while _game == null and _failure.is_empty() and Time.get_ticks_msec() - t0 < 600000:
		await process_frame
	if _game == null:
		push_error("world load failed: " + _failure)
		quit(4)
		return
	print("FLIGHT ready in ", Time.get_ticks_msec() - t0, " ms")
	for i in 90:
		await process_frame
	_probe = PROBE.new()
	_probe.Bind(_game)
	_lod_probe = LOD_PROBE.new()
	_lod_probe.Bind(_game)
	_probe.SetBrightestCycleFraction()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var pose: Dictionary = _probe.GetPose()
	_save_around = pose.around
	_save_across = pose.across
	_terrain_ref = float(pose.vertical) - float(pose.altitude_computed)
	_camera = _game.get_node("Explorer/Head/Camera3D")
	_camera_base = _camera.rotation_degrees
	var coord := _game.get_node("TerrainLodCoordinator")
	if _args.has("preset"):
		coord.call("ApplyQualityPreset", int(_args["preset"]))
	_refresh_viewports()
	if _args.has("bridges-off"):
		coord.call("SetIntermediateLodTiersEnabled", false)
	if _args.has("global"):
		var gp := String(_args["global"]).split("x")
		var gl := _game.get_node("GlobalTerrainLod3")
		gl.call("ApplyRuntimeQuality", int(gp[0]), int(gp[1]), float(gl.get("FadeStartMeters")), float(gl.get("FadeEndMeters")))
	_results["adapter"] = RenderingServer.get_video_adapter_name()
	_results["preset"] = coord.get("CurrentPresetName")
	_results["args"] = _args
	_results["window"] = root.get_visible_rect().size
	_results["anchor_policy"] = _anchor_policy()
	print("FLIGHT preset=", _results["preset"], " adapter=", _results["adapter"], " anchor_policy=", _results["anchor_policy"])
	var alts := String(_args.get("alts", "500,2000,8000,16000,23000,60000")).split(",", false)
	var pitches := String(_args.get("pitches", "-15,-45,-89")).split(",", false)
	for alt_s in alts:
		for pitch_s in pitches:
			await _pose(float(alt_s), float(pitch_s))
			_write()
	if _args.has("move"):
		await _move(float(_args["move"]), float(_args.get("move-seconds", "30")))
		_write()
	var failed: Array = _results["failures"]
	print("FLIGHT_RESULT=", "FAIL" if failed.size() > 0 else "PASS", " failures=", failed.size())
	for line in failed:
		print("  FAIL ", line)
	_release()
	quit(1 if failed.size() > 0 else 0)

var _viewports: Array = []

func _refresh_viewports() -> void:
	_viewports.clear()
	_viewports.append(root.get_viewport_rid())
	for node in _game.find_children("*", "SubViewport", true, false):
		var vp := node as SubViewport
		if vp.render_target_update_mode != SubViewport.UPDATE_DISABLED:
			_viewports.append(vp.get_viewport_rid())
	for rid in _viewports:
		RenderingServer.viewport_set_measure_render_time(rid, true)

func _mean(values: PackedFloat64Array) -> float:
	var t := 0.0
	for v in values:
		t += v
	return t / maxf(1.0, values.size())

func _anchor_policy() -> Dictionary:
	var policy := {}
	for name in LEVEL_NAMES:
		var n := _game.get_node_or_null(name)
		if n != null:
			policy[name] = n.get("RebuildExistingMeshesOnCanonicalAnchorChange")
	return policy

func _key(code: int, down: bool) -> void:
	if _held.get(code, false) == down:
		return
	_held[code] = down
	var e := InputEventKey.new()
	e.keycode = code
	e.physical_keycode = code
	e.pressed = down
	Input.parse_input_event(e)

func _release() -> void:
	for c in [KEY_W, KEY_E, KEY_Q]:
		_key(c, false)

func _drive(target: float, forward: bool, pitch: float) -> void:
	var p: Dictionary = _probe.GetPose()
	var err: float = float(p.altitude_cached) - target
	var band := maxf(BAND, target * 0.02)
	_key(KEY_E, err < -band)
	_key(KEY_Q, err > band)
	_key(KEY_W, forward and absf(err) < 3000.0)
	_camera.rotation_degrees = Vector3(_camera_base.x + pitch, _camera_base.y, _camera_base.z)

func _place(around: float, alt: float, pitch: float) -> void:
	_release()
	_probe.PlaceNoclip(around, _terrain_ref + alt, _save_across, 0.0, float(_args.get("speed", 1100.0)))
	_camera.rotation_degrees = Vector3(_camera_base.x + pitch, _camera_base.y, _camera_base.z)

func _busy(coord: Node) -> bool:
	return int(coord.get("PendingUploadCount")) > 0 or int(coord.get("ActiveWorkerCount")) > 0

func _pose(alt: float, pitch: float) -> void:
	var coord := _game.get_node("TerrainLodCoordinator")
	_place(_save_around, alt, pitch)
	var start := Time.get_ticks_msec()
	var quiet := 0
	while Time.get_ticks_msec() - start < 60000:
		_drive(alt, false, pitch)
		await process_frame
		quiet = quiet + 1 if not _busy(coord) else 0
		if quiet > 120 and Time.get_ticks_msec() - start > 6000:
			break
	var settle_ms := Time.get_ticks_msec() - start
	if _args.has("hide"):
		for n in String(_args["hide"]).split(",", false):
			var hn := _game.get_node_or_null(n)
			if hn != null:
				hn.visible = false
	var frames := PackedFloat64Array()
	var cpu_ms := PackedFloat64Array()
	var gpu_ms := PackedFloat64Array()
	var proc_ms := PackedFloat64Array()
	var last := Time.get_ticks_usec()
	for i in 120:
		_drive(alt, false, pitch)
		await process_frame
		var now := Time.get_ticks_usec()
		frames.append((now - last) / 1000.0)
		last = now
		var c := 0.0
		var g := 0.0
		for rid in _viewports:
			c += RenderingServer.viewport_get_measured_render_time_cpu(rid)
			g += RenderingServer.viewport_get_measured_render_time_gpu(rid)
		cpu_ms.append(c)
		gpu_ms.append(g)
		proc_ms.append(Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0)
	frames.sort()
	var label := "alt%d_p%d" % [int(alt), int(absf(pitch))]
	var state: Dictionary = _lod_probe.GetLodState()
	var entry := {"label": label, "altitude_target": alt, "pitch": pitch, "settle_ms": settle_ms,
		"pose": _probe.GetPose(), "frame_ms_p50": frames[60], "frame_ms_p95": frames[114],
		"draw_calls": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME),
		"primitives": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_PRIMITIVES_IN_FRAME),
		"mesh_mb": state.get("mesh_mb", 0.0), "lod": state,
		"frame_ms_mean": _mean(frames), "render_cpu_ms": _mean(cpu_ms), "render_gpu_ms": _mean(gpu_ms), "process_ms": _mean(proc_ms)}
	if not _args.has("quiet-only"):
		var check: Dictionary = _lod_probe.Evaluate(_terrain_ref, _ray_cols, _ray_rows)
		entry["check"] = check
		_judge(label, check, "stationary", 0.0)
	if _args.has("shots"):
		await _shot(label)
		_set_debug(true)
		for i in 3:
			await process_frame
		await _shot(label + "_dbg")
		_set_debug(false)
	_results["poses"].append(entry)
	print("FLIGHT ", label, " ", JSON.stringify(_summary(entry)))

func _judge(label: String, check: Dictionary, kind: String, hole_fraction: float) -> void:
	if check.is_empty():
		return
	var hits := maxi(1, int(check.hits))
	var bad_holes := int(check.holes) > int(ceil(hole_fraction * hits))
	if bad_holes or int(check.proxy_eps_violations) > 0 or int(check.backdrop_violations) > 0:
		_results["failures"].append("%s (%s): holes=%d eps=%d backdrop=%d of %d hits; worst proxy %.1fpx (%s) backdrop %.1fpx; %s" % [
			label, kind, check.holes, check.proxy_eps_violations, check.backdrop_violations, check.hits,
			check.worst_proxy_eps, check.worst_proxy_tier, check.worst_backdrop_eps, str(check.samples)])

func _summary(entry: Dictionary) -> Dictionary:
	var act := []
	for l in entry.lod.levels:
		if l.streaming:
			act.append("%s:%d" % [l.spacing, l.active_sectors])
	var s := {"alt": snappedf(float(entry.pose.altitude_cached), 1.0), "active": act,
		"p50": snappedf(float(entry.frame_ms_p50), 0.01), "p95": snappedf(float(entry.frame_ms_p95), 0.01),
		"draws": entry.draw_calls, "prims": entry.primitives, "mesh_mb": snappedf(float(entry.mesh_mb), 0.1),
		"cpu": snappedf(float(entry.render_cpu_ms), 0.01), "gpu": snappedf(float(entry.render_gpu_ms), 0.01), "proc": snappedf(float(entry.process_ms), 0.01)}
	if entry.has("check") and not entry.check.is_empty():
		var c: Dictionary = entry.check
		s["hits"] = c.hits
		s["holes"] = c.holes
		s["eps_viol"] = c.proxy_eps_violations
		s["backdrop_viol"] = c.backdrop_violations
		s["worst_eps"] = snappedf(float(c.worst_proxy_eps), 0.1)
	return s

func _move(alt: float, seconds: float) -> void:
	var coord := _game.get_node("TerrainLodCoordinator")
	var speed := float(_args.get("speed", 1100.0))
	var start_around := _save_around - speed * seconds * 0.55
	_place(start_around, alt, -25.0)
	var s := Time.get_ticks_msec()
	while Time.get_ticks_msec() - s < 20000:
		_drive(alt, false, -25.0)
		await process_frame
	var frames := PackedFloat64Array()
	var uploads := 0
	var starts := 0
	var max_upload_ms := 0.0
	var hit_total := 0
	var hole_total := 0
	var eps_total := 0
	var rebuilt_start: int = _lod_probe.GetTotalRebuiltSectorMeshes()
	var rebuilt_series := []
	var last_check := Time.get_ticks_msec()
	var last := Time.get_ticks_usec()
	s = Time.get_ticks_msec()
	while Time.get_ticks_msec() - s < int(seconds * 1000.0):
		_drive(alt, true, -25.0)
		await process_frame
		var now := Time.get_ticks_usec()
		frames.append((now - last) / 1000.0)
		last = now
		uploads += int(coord.get("LastFrameUploadCount"))
		starts += int(coord.get("LastFrameBuildStarts"))
		max_upload_ms = maxf(max_upload_ms, float(coord.get("LastFrameUploadMilliseconds")))
		if Time.get_ticks_msec() - last_check >= 1000:
			last_check = Time.get_ticks_msec()
			rebuilt_series.append(_lod_probe.GetTotalRebuiltSectorMeshes())
			if not _args.has("quiet-only"):
				var c: Dictionary = _lod_probe.Evaluate(_terrain_ref, 12, 8)
				if not c.is_empty():
					hit_total += int(c.hits)
					hole_total += int(c.holes)
					eps_total += int(c.proxy_eps_violations) + int(c.backdrop_violations)
	var sorted := frames.duplicate()
	sorted.sort()
	var total := 0.0
	for f in frames:
		total += f
	var state: Dictionary = _lod_probe.GetLodState()
	var m := {"alt": alt, "frames": frames.size(), "frame_mean": total / frames.size(), "p50": sorted[sorted.size() / 2],
		"p95": sorted[int(sorted.size() * 0.95)], "p99": sorted[int(sorted.size() * 0.99)], "max": sorted[-1],
		"draw_calls": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME),
		"primitives": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_PRIMITIVES_IN_FRAME),
		"mesh_mb": state.get("mesh_mb", 0.0),
		"uploads": uploads, "build_starts": starts, "max_upload_ms": max_upload_ms, "pose_end": _probe.GetPose(),
		"sampled_hits": hit_total, "sampled_holes": hole_total, "sampled_eps_violations": eps_total,
		"rebuilt_meshes_total": _lod_probe.GetTotalRebuiltSectorMeshes() - rebuilt_start, "rebuilt_series": rebuilt_series,
		"lod_end": state}
	_results["move"] = m
	if hit_total > 0:
		var fraction := float(hole_total) / float(hit_total)
		m["hole_fraction"] = fraction
		if fraction > float(_args.get("move-hole-fraction", 0.02)) or eps_total > 0:
			_results["failures"].append("moving leg: hole fraction %.4f eps_violations=%d" % [fraction, eps_total])
	if _args.has("shots"):
		await _shot("move_alt%d" % int(alt))
		_set_debug(true)
		for i in 3:
			await process_frame
		await _shot("move_alt%d_dbg" % int(alt))
		_set_debug(false)
	_release()
	print("FLIGHT MOVE ", JSON.stringify({"p50": m.p50, "p95": m.p95, "p99": m.p99, "max": m.max, "draws": m.draw_calls,
		"prims": m.primitives, "mesh_mb": m.mesh_mb, "uploads": uploads, "starts": starts, "holes": hole_total, "hits": hit_total,
		"rebuilt": m.rebuilt_meshes_total}))

func _set_debug(on: bool) -> void:
	for i in LEVEL_NAMES.size():
		var n := _game.get_node_or_null(LEVEL_NAMES[i])
		if n != null:
			n.call("SetDebugColor", on, COLORS[i])
	var g := _game.get_node_or_null("GlobalTerrainLod3")
	if g != null:
		g.call("SetDebugColor", on, Color(0.1, 0.1, 0.1))

func _shot(name: String) -> void:
	await RenderingServer.frame_post_draw
	var img := root.get_texture().get_image()
	if img != null:
		img.save_png(_out.path_join(name + ".png"))

func _write() -> void:
	var f := FileAccess.open(_out.path_join("results.json"), FileAccess.WRITE)
	f.store_string(JSON.stringify(_results, "  "))
	f.close()
