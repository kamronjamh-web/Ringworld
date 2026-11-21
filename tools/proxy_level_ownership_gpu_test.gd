extends SceneTree
## Resident coverage (docs/proxy-coverage-culling.md, "Resident coverage"): a coarse proxy level must not draw where its finer level draws.
## Boots the certified gameplay world in an isolated fixture (shadow_harness_common.create_game; nothing is written to user:// settings or saves),
## finds the most rugged spot within 30 km of the fixture point, hovers 868 m above it at noon with the clocks frozen, and paints the proxy levels
## with diagnostic colours (finest active level red, every coarser level green).
##   1. Ownership at rest, four yaws: a frame with only the finest level drawn says where that level draws. Coarse (green) pixels there in the
##      full frame are coarse terrain in front of it ("poke-through"). With resident coverage on, at most MAX_POKE_ON of the finest level's pixels
##      at rest (before and after the fly-in). With it off (the former overlap) some sample must reach MIN_POKE_OFF, which proves the run exercises
##      the overlap (the certified world is smooth at rest; the fly-in, with its streaming lag, overlaps heavily).
##   2. Holes at rest: per yaw, three frames of one frozen view (resident on, off, every proxy level hidden). A pixel where "on" matches "hidden"
##      while "off" differs strongly is a hole (ProxyLevelOwnershipMetrics). At most MAX_HOLE_FRACTION_REST of the frame.
##   3. Fly-in: 60 km toward the spot at 11.1 km/s; every FLY_SAMPLE_FRAMES frames the view is frozen (time scale 0, explorer paused) for the
##      same triplet. At most MAX_HOLE_FRACTION_FLY per sample (streaming may change one frame to the next), then the at-rest checks after arrival.
## Options: --stills=<abs dir>   plain and level-colour stills of yaw 0 with resident coverage on and off, plus the per-sample report
##          --break=resident-off  measure "on" with resident coverage off (check 1 must then FAIL)
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   godot --path . --script res://tools/proxy_level_ownership_gpu_test.gd
## Prints PROXY_LEVEL_OWNERSHIP_GPU_TEST=PASS.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const TVW := preload("res://tools/tree_visibility_world.gd")
const PROBE := preload("res://tools/PerfBenchProbe.cs")
const METRICS := preload("res://tools/ProxyLevelOwnershipMetrics.cs")
const MARKER := "PROXY_LEVEL_OWNERSHIP_GPU_TEST"
const ALTITUDE := 868.0
const PITCH := -14.0
const FLY_SPEED := 11100.0
const FLY_DISTANCE := 60000.0
const FLY_SAMPLE_FRAMES := 12
const MAX_POKE_ON := 0.0001
const MIN_POKE_OFF := 0.01
const MAX_HOLE_FRACTION_REST := 0.0002
const MAX_HOLE_FRACTION_FLY := 0.001

var game: Node
var explorer: Node
var surface: Node
var probe
var metrics
var levels: Array = []
var hide_mode := ""  # "all" or "coarse" while a frame is taken without some levels
var finest_level: Node
var saved_visible := {}
var fixture := ""
var stills_dir := ""
var problems: PackedStringArray = []
var report := {"rest": [], "fly": [], "arrival": []}
var breaking := ""


func _initialize() -> void:
	create_timer(1500.0).timeout.connect(func(): _finish("timeout"))
	call_deferred("_run")


func _detail(text: String) -> void:
	print("PROXY_LEVEL_OWNERSHIP_DETAIL " + text)


func _finish(error: String) -> void:
	Engine.time_scale = 1.0
	if stills_dir != "":
		var f := FileAccess.open(stills_dir.path_join("report.json"), FileAccess.WRITE)
		if f != null:
			f.store_string(JSON.stringify(report, "  "))
			f.close()
	if fixture != "":
		_remove_recursive(ProjectSettings.globalize_path(fixture))
	if error == "" and problems.is_empty():
		print("%s=PASS" % MARKER)
		quit(0)
	else:
		var reason := error if error != "" else " | ".join(problems)
		push_error("%s=FAIL %s" % [MARKER, reason])
		quit(1)


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		return _finish("requires Vulkan/Forward+")
	stills_dir = COMMON.arg_value("--stills=", "")
	breaking = COMMON.arg_value("--break=", "")
	if stills_dir != "":
		DirAccess.make_dir_recursive_absolute(stills_dir)
	fixture = "res://.godot/proxy_level_ownership_%d" % OS.get_process_id()
	game = await COMMON.create_game(self, fixture)
	if game == null:
		return _finish("gameplay world did not start")
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	if explorer == null or surface == null:
		return _finish("Explorer or Terrain3DGameplay missing")
	for child in game.get_children():
		if child.has_method("SetDebugColor") and child.get("LodLabel") != null and child.get("EnableResidentCoverage") != null:
			levels.append(child)
	if levels.is_empty():
		return _finish("no proxy levels with EnableResidentCoverage")
	probe = PROBE.new()
	metrics = METRICS.new()
	RenderingServer.frame_pre_draw.connect(_pre_draw)
	TVW.freeze_noise(self, game, PackedStringArray(["clouds", "taa"]))
	TVW.hide_overlays(game)

	var spot := _rugged_spot()
	_detail("spot around=%.0f across=%.0f ground=%.1f relief=%.1f" % [spot.x, spot.y, spot.z, spot.w])
	report["spot"] = [spot.x, spot.y, spot.z, spot.w]
	var target := Vector2(spot.x, spot.y)
	var vertical := spot.z + ALTITUDE

	# ---- at rest after a teleport
	probe.PlaceNoclip(explorer, target.x, vertical, target.y, 0.0, PITCH, 0.0)
	await COMMON.settle(self, 1.0)
	_noon()
	await _wait_proxies(240.0)
	_residency_stats("teleport and settle")
	await COMMON.settle(self, 5.0)
	_residency_stats("at rest (5 s)")
	for yaw in [0.0, 90.0, 180.0, 270.0]:
		report["rest"].append(await _measure_rest(target, vertical, yaw, "rest"))
	_check_rest(report["rest"], "rest")

	# ---- fly-in at 11.1 km/s
	var dir := await _w_direction(target, vertical)
	var start := target - dir * FLY_DISTANCE
	var fly_vertical := maxf(vertical, _max_ground_along(start, target) + 300.0)
	# Level flight (W follows the pitch); the samples look along the flight path.
	probe.PlaceNoclip(explorer, start.x, fly_vertical, start.y, 0.0, 0.0, FLY_SPEED)
	await COMMON.settle(self, 2.0)
	await _wait_proxies(120.0)
	_residency_stats("before the fly-in")
	_key(KEY_W, true)
	var frames := 0
	var begin := Time.get_ticks_msec()
	while Time.get_ticks_msec() - begin < 40000:
		await process_frame
		frames += 1
		var pose: Dictionary = COMMON.pose_now(game)
		var travelled := (Vector2(float(pose["around"]), float(pose["across"])) - start).dot(dir)
		if travelled >= FLY_DISTANCE:
			break
		if frames % FLY_SAMPLE_FRAMES == 0:
			var sample := await _triplet("fly_%03d" % frames, false)
			sample["travelled_m"] = travelled
			report["fly"].append(sample)
			if float(sample.get("hole_fraction", 1.0)) > MAX_HOLE_FRACTION_FLY:
				problems.append("fly-in sample at %.0f m: holes %.5f > %.5f" % [travelled, float(sample["hole_fraction"]), MAX_HOLE_FRACTION_FLY])
	_key(KEY_W, false)
	_residency_stats("fly-in at 11.1 km/s")
	var arrived: Dictionary = COMMON.pose_now(game)
	_detail("fly-in: %d samples, arrived at %s after %.1f s" % [report["fly"].size(), str(arrived), float(Time.get_ticks_msec() - begin) / 1000.0])
	var max_fly := 0.0
	for s in report["fly"]:
		max_fly = maxf(max_fly, float(s.get("hole_fraction", 0.0)))
	_detail("fly-in max hole fraction %.6f" % max_fly)
	report["fly_max_hole_fraction"] = max_fly
	if report["fly"].is_empty():
		problems.append("fly-in took no samples")
	# Stop over the arrival point and take samples while the chain catches up, then the at-rest checks there.
	var arrive := Vector2(float(arrived["around"]), float(arrived["across"]))
	var arrive_vertical := float(surface.call("SampleHeight", arrive.x, arrive.y)) + ALTITUDE
	probe.PlaceNoclip(explorer, arrive.x, arrive_vertical, arrive.y, 0.0, PITCH, 0.0)
	for wait_s in [0.0, 1.0, 3.0, 10.0]:
		await COMMON.settle(self, wait_s)
		var sample := await _triplet("arrive_%02d" % int(wait_s), false)
		report["arrival"].append(sample)
		if float(sample.get("hole_fraction", 1.0)) > MAX_HOLE_FRACTION_FLY:
			problems.append("arrival +%.0f s: holes %.5f > %.5f" % [wait_s, float(sample["hole_fraction"]), MAX_HOLE_FRACTION_FLY])
	await _wait_proxies(240.0)
	_residency_stats("arrival until settled")
	var settled: Array = []
	for yaw in [0.0, 180.0]:
		settled.append(await _measure_rest(arrive, arrive_vertical, yaw, "arrived"))
	report["arrived_rest"] = settled
	_check_rest(settled, "after the fly-in")
	var max_poke_off := 0.0
	for group in [report["rest"], report["fly"], report["arrival"], settled]:
		for r in group:
			max_poke_off = maxf(max_poke_off, float(r["off"].get("poke_fraction", 0.0)))
	_detail("largest poke-through without resident coverage: %.4f of the finest level's pixels" % max_poke_off)
	if max_poke_off < MIN_POKE_OFF:
		problems.append("poke-through without resident coverage never reached %.3f (largest %.4f): the run does not exercise the overlap" % [MIN_POKE_OFF, max_poke_off])
	_finish("")


# ------------------------------------------------------------------ measurements

func _measure_rest(target: Vector2, vertical: float, yaw: float, tag: String) -> Dictionary:
	probe.PlaceNoclip(explorer, target.x, vertical, target.y, yaw, PITCH, 0.0)
	await COMMON.settle(self, 0.5)
	await _wait_proxies(120.0)
	var result := await _triplet("%s_y%03d" % [tag, int(yaw)], stills_dir != "" and yaw == 0.0)
	result["yaw"] = yaw
	return result


## Freezes the view and takes "on", "off" and "hidden" frames with the diagnostic colours. Restores resident coverage on.
func _triplet(tag: String, save: bool) -> Dictionary:
	var previous_scale := Engine.time_scale
	Engine.time_scale = 0.0
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	_set_diag(true)
	_set_resident(breaking != "resident-off")
	await _frames(3)
	var on := TVW.grab(self)
	_set_resident(false)
	await _frames(3)
	var off := TVW.grab(self)
	hide_mode = "all"
	await _frames(3)
	var none := TVW.grab(self)
	_restore_levels()
	hide_mode = "coarse"
	await _frames(3)
	var finest := TVW.grab(self)
	_restore_levels()
	hide_mode = ""
	_set_resident(true)
	var result: Dictionary = metrics.Holes(on, off, none)
	result["tag"] = tag
	result["on"] = metrics.Poke(on, finest)
	result["off"] = metrics.Poke(off, finest)
	result["pose"] = COMMON.pose_now(game)
	result["resident_cells"] = _resident_cells()
	if stills_dir != "" and not save and float(result.get("hole_fraction", 0.0)) > 0.0005:
		on.save_png(stills_dir.path_join("%s_holes_levels_on.png" % tag))
		off.save_png(stills_dir.path_join("%s_holes_levels_off.png" % tag))
		none.save_png(stills_dir.path_join("%s_holes_levels_hidden.png" % tag))
	if save:
		on.save_png(stills_dir.path_join("%s_levels_on.png" % tag))
		off.save_png(stills_dir.path_join("%s_levels_off.png" % tag))
		none.save_png(stills_dir.path_join("%s_levels_hidden.png" % tag))
		finest.save_png(stills_dir.path_join("%s_levels_finest_only.png" % tag))
		_set_diag(false)
		await _frames(3)
		TVW.grab(self).save_png(stills_dir.path_join("%s_plain_on.png" % tag))
		_set_resident(false)
		await _frames(3)
		TVW.grab(self).save_png(stills_dir.path_join("%s_plain_off.png" % tag))
		_set_resident(true)
	_set_diag(false)
	explorer.process_mode = Node.PROCESS_MODE_INHERIT
	Engine.time_scale = previous_scale
	_detail("%s holes=%d (%.6f) poke_on=%.4f poke_off=%.4f finest_px=%d on_off_changed=%d resident_cells=%s" % [tag, int(result.get("holes", -1)),
		float(result.get("hole_fraction", -1.0)), float(result["on"].get("poke_fraction", -1.0)), float(result["off"].get("poke_fraction", -1.0)),
		int(result["on"].get("finest_pixels", -1)), int(result.get("on_off_changed", -1)), str(result["resident_cells"])])
	return result


func _check_rest(rows: Array, label: String) -> void:
	for r in rows:
		if float(r.get("hole_fraction", 1.0)) > MAX_HOLE_FRACTION_REST:
			problems.append("%s yaw %d: holes %.6f > %.6f" % [label, int(r["yaw"]), float(r["hole_fraction"]), MAX_HOLE_FRACTION_REST])
		if float(r["on"].get("poke_fraction", 1.0)) > MAX_POKE_ON:
			problems.append("%s yaw %d: coarse terrain in front of the finest level on %.4f of its pixels > %.4f" % [label, int(r["yaw"]), float(r["on"]["poke_fraction"]), MAX_POKE_ON])



# ------------------------------------------------------------------ world helpers

## The most rugged point (largest height range over a 5 x 5 km block) within 30 km of the fixture point: (around, across, ground, relief).
func _rugged_spot() -> Vector4:
	var step := 1000.0
	var n := 61
	var heights := PackedFloat32Array()
	heights.resize(n * n)
	var a0 := COMMON.FIXTURE_AROUND - 30000.0
	var z0 := COMMON.FIXTURE_ACROSS - 30000.0
	for j in n:
		for i in n:
			heights[j * n + i] = float(surface.call("SampleHeight", a0 + i * step, maxf(z0 + j * step, 0.0)))
	var best := Vector4(COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS, 0.0, -1.0)
	for j in range(2, n - 2):
		for i in range(2, n - 2):
			var lo := INF
			var hi := -INF
			for dj in range(-2, 3):
				for di in range(-2, 3):
					var h := heights[(j + dj) * n + i + di]
					lo = minf(lo, h)
					hi = maxf(hi, h)
			if lo > 0.0 and hi - lo > best.w:
				best = Vector4(a0 + i * step, maxf(z0 + j * step, 0.0), heights[j * n + i], hi - lo)
	return best


func _max_ground_along(start: Vector2, target: Vector2) -> float:
	var top := -INF
	for k in 61:
		var p := start.lerp(target, float(k) / 60.0)
		top = maxf(top, float(surface.call("SampleHeight", p.x, p.y)))
	return top


## The canonical (around, across) direction W moves the explorer at yaw 0.
func _w_direction(target: Vector2, vertical: float) -> Vector2:
	probe.PlaceNoclip(explorer, target.x, vertical, target.y, 0.0, 0.0, 200.0)
	await COMMON.settle(self, 0.5)
	var p0: Dictionary = COMMON.pose_now(game)
	_key(KEY_W, true)
	await COMMON.settle(self, 0.8)
	_key(KEY_W, false)
	var p1: Dictionary = COMMON.pose_now(game)
	var d := Vector2(float(p1["around"]) - float(p0["around"]), float(p1["across"]) - float(p0["across"]))
	return d.normalized() if d.length() > 1.0 else Vector2(1.0, 0.0)


func _noon() -> void:
	var info: Dictionary = COMMON.apply_time(game, {"mode": "noon"})
	COMMON.hold_world_clocks(game)
	TVW.freeze_world(game)
	_detail("time %s" % str(info))


## Waits until every proxy level has no pending upload and an unchanged sector count for 60 frames.
func _wait_proxies(timeout_s: float) -> bool:
	await COMMON.wait_builds_idle(self, game, timeout_s)
	var stable := 0
	var last := ""
	var deadline := Time.get_ticks_msec() + int(timeout_s * 1000.0)
	while Time.get_ticks_msec() < deadline and stable < 60:
		await process_frame
		var state := ""
		var pending := 0
		for l in levels:
			pending += int(l.get("PendingUploadCount"))
			state += "%s:%d;" % [str(l.get("LodLabel")), int(l.get("ActiveSectorCount"))]
		stable = stable + 1 if pending == 0 and state == last else 0
		last = state
	return stable >= 60


## Residency refresh cost since the last call (RingTerrainProxySector.TakeResidencyRefreshStats, all levels).
func _residency_stats(label: String) -> void:
	var text := str(levels[0].call("TakeResidencyRefreshStats"))
	report["residency_stats_" + label.replace(" ", "_")] = text
	_detail("residency refresh, %s: %s" % [label, text])


func _set_resident(on: bool) -> void:
	for l in levels:
		l.set("EnableResidentCoverage", on)


func _resident_cells() -> Dictionary:
	var cells := {}
	for l in levels:
		if bool(l.get("visible")):
			cells[str(l.get("LodLabel"))] = int(l.get("ResidentCoverageCellCount"))
	return cells


func _set_diag(on: bool) -> void:
	finest_level = null
	for l in levels:
		if bool(l.get("visible")) and (finest_level == null or int(l.get("SampleSpacingMeters")) < int(finest_level.get("SampleSpacingMeters"))):
			finest_level = l
	# Every level as fine as the finest visible one is red (a finer level can become visible between the frames of a sample).
	var finest_spacing := int(finest_level.get("SampleSpacingMeters")) if finest_level != null else 0
	for l in levels:
		l.call("SetDebugColor", on, Color(1, 0, 0) if int(l.get("SampleSpacingMeters")) <= finest_spacing else Color(0, 1, 0))


func _pre_draw() -> void:
	if hide_mode == "":
		return
	for l in levels:
		if hide_mode == "coarse" and finest_level != null and int(l.get("SampleSpacingMeters")) <= int(finest_level.get("SampleSpacingMeters")):
			continue
		if not saved_visible.has(l):
			saved_visible[l] = l.visible
		l.visible = false


func _restore_levels() -> void:
	for l in saved_visible:
		if is_instance_valid(l):
			l.visible = saved_visible[l]
	saved_visible.clear()


func _frames(count: int) -> void:
	for _i in count:
		await RenderingServer.frame_post_draw


func _key(code: int, down: bool) -> void:
	var e := InputEventKey.new()
	e.keycode = code
	e.physical_keycode = code
	e.pressed = down
	Input.parse_input_event(e)


func _remove_recursive(path: String) -> void:
	var dir := DirAccess.open(path)
	if dir == null:
		return
	for entry in dir.get_files():
		DirAccess.remove_absolute(path.path_join(entry))
	for entry in dir.get_directories():
		_remove_recursive(path.path_join(entry))
	DirAccess.remove_absolute(path)
