extends SceneTree

## T2R-12: the far river line holds from 50 m to 200 km (docs/work-orders/terrain-vegetation/T2.md, Spec S11). Boots the landscape harness world, takes the river point of InlandSwimProbe.FindPoints
## (or, in a world without rivers, the pose the rivers run saved) and backs away from it and climbs in STEPS geometric altitude steps from 50 m to 200 km (horizontal distance 1.5 x altitude), looking at the river, at noon. Each step saves a
## 640 x 360 frame (frame%08d.png, so tools/media/ffmpeg_tools.ps1 reads the sequence) and records the mean luma change from the previous frame and the colour of the centre patch (the river).
## Two runs share a pose file and a series file each:
##   rivers:  --generation=1 --review-features=water.rivers   (the manifest arguments)  -> lod_flyout/rivers/, saves lod_flyout/pose.json
##   gen-1:   --generation=1                                                          -> lod_flyout/gen1/, reads the pose; the same fly-out without any river
## Whichever run finds the other's series compares: the largest single-step luma change of the rivers run must not exceed the gen-1 run's by more than SPIKE_FACTOR (no new pop or flash), and from
## 2 km up the centre patch must differ from the gen-1 centre patch at MIN_LINE_SHARE of the steps (the line is there). With no other series the run passes and says the comparison is pending.
## RUN: tools/run_tests.ps1 -Tests river_lod_consistency_gpu_test  (gen-1 run: --user-args "--generation=1"). Prints RIVER_LOD_CONSISTENCY=PASS|FAIL.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const PROBE := preload("res://tools/InlandSwimProbe.cs")
const MARKER := "RIVER_LOD_CONSISTENCY"
const STEPS := 70
const START_ALTITUDE_M := 50.0
const END_ALTITUDE_M := 200000.0
# The camera backs away from the river point along the ring and climbs with it, looking at the river: horizontal distance 1.5 x altitude, so the pitch is constant. The far tiers (which sit beyond
# the proxy levels) are only seen on such an oblique view; straight down shows proxies alone.
const DISTANCE_PER_ALTITUDE := 1.5
const PITCH_DEG := -33.7
# The one step saved at full size (full_stepN.png beside the frames): the minimap panel in the corner is only readable there.
const FULL_FRAME_STEP := 20
const FRAME_SIZE := Vector2i(640, 360)
const SPIKE_FACTOR := 1.25
const SPIKE_MAX_ALTITUDE_M := 25000.0
const LINE_FROM_ALTITUDE_M := 2000.0
const LINE_CENTRE_DELTA := 0.01
const MIN_LINE_SHARE := 0.5
const OUT_ROOT := "res://.godot/captures/landscape/t2r/lod_flyout"

var failures: Array = []
var finished := false


func _init() -> void:
	call_deferred("_run")


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	quit(1)


func _out_dir(tag: String) -> String:
	var path := ProjectSettings.globalize_path(OUT_ROOT).path_join(tag)
	DirAccess.make_dir_recursive_absolute(path)
	return path


## --far-only: hides every proxy level, so only the far tiers (global LOD and far viewport, shader ring_terrain_global) draw the ground. A diagnostic of the far line itself; its series are tagged apart.
func _hide_proxies(game: Node) -> void:
	var stack: Array = [game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is Node3D and (node.has_method("HasActiveWaterSectorAt") or (node.name.begins_with("Lod") and node.name.contains("Terrain"))):
			(node as Node3D).visible = false
		for child in node.get_children():
			stack.append(child)


func _run() -> void:
	var watchdog := create_timer(1700.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (1700 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	var booted: Dictionary = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		quit(1)
		return
	var game: Node = booted["game"]
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	HARNESS.COMMON.hold_world_clocks(game)
	var probe: RefCounted = PROBE.new()
	var points: Dictionary = probe.call("FindPoints", game)
	var rivers := bool(points.get("ok", false))
	var far_only := _arg("far-only") == "1" or OS.get_cmdline_user_args().has("--far-only")
	var tag := ("rivers" if rivers else "gen1") + ("_far" if far_only else "")
	var pose_path := ProjectSettings.globalize_path(OUT_ROOT).path_join("pose.json")
	var pose := {}
	if rivers:
		var river: Dictionary = points["river"]
		pose = {"around": float(river["around"]), "across": float(river["across"]), "level": float(river["level"])}
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_ROOT))
		var file := FileAccess.open(pose_path, FileAccess.WRITE)
		file.store_string(JSON.stringify(pose))
		file.close()
	else:
		var file := FileAccess.open(pose_path, FileAccess.READ)
		if file == null:
			_fail("no river pose: run the rivers variant first (it saves %s)" % pose_path)
			return
		pose = JSON.parse_string(file.get_as_text())
		file.close()
		# A world without rivers has no water level at the point: the ground under it.
		pose["level"] = float(game.get_node("Terrain3DGameplay").call("SampleHeight", float(pose["around"]), float(pose["across"])))
	print("%s %s pose around=%.0f across=%.0f level=%.2f" % [MARKER, tag, float(pose["around"]), float(pose["across"]), float(pose["level"])])
	if far_only:
		_hide_proxies(game)
	var series: Dictionary = await _fly_out(game, pose, tag, far_only)
	if series.is_empty():
		return
	var series_path := ProjectSettings.globalize_path(OUT_ROOT).path_join("series_%s.json" % tag)
	var out := FileAccess.open(series_path, FileAccess.WRITE)
	out.store_string(JSON.stringify(series))
	out.close()
	var other_tag := ("gen1" if rivers else "rivers") + ("_far" if far_only else "")
	var other_path := ProjectSettings.globalize_path(OUT_ROOT).path_join("series_%s.json" % other_tag)
	if FileAccess.file_exists(other_path):
		var other_file := FileAccess.open(other_path, FileAccess.READ)
		var other: Dictionary = JSON.parse_string(other_file.get_as_text())
		other_file.close()
		_compare(series if rivers else other, other if rivers else series)
	else:
		print("%s comparison pending: no %s series yet (%s)" % [MARKER, other_tag, other_path])
	if not failures.is_empty():
		_fail("; ".join(failures))
		return
	finished = true
	print("%s=PASS" % MARKER)
	quit(0)


## The minimap's own render target (SubViewport TerrainMapViewport of RingTerrainMinimap): river polylines are checked by eye on it (T2R-12), whatever panel covers the corner of the screen.
func _save_minimap(path: String) -> void:
	var stack: Array = [root]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is SubViewport and node.name == "TerrainMapViewport":
			(node as SubViewport).render_target_update_mode = SubViewport.UPDATE_ONCE
			await RenderingServer.frame_post_draw
			await RenderingServer.frame_post_draw
			(node as SubViewport).get_texture().get_image().save_png(path)
			return
		for child in node.get_children():
			stack.append(child)


func _arg(name: String, fallback := "") -> String:
	for arg in OS.get_cmdline_user_args():
		if str(arg).begins_with("--%s=" % name):
			return str(arg).substr(name.length() + 3)
	return fallback


func _luma_small(image: Image) -> PackedFloat32Array:
	var small := image.duplicate() as Image
	small.resize(160, 90, Image.INTERPOLATE_BILINEAR)
	var values := PackedFloat32Array()
	for y in 90:
		for x in 160:
			var c := small.get_pixel(x, y)
			values.append(0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b)
	return values


func _fly_out(game: Node, pose: Dictionary, tag: String, far_only := false) -> Dictionary:
	var explorer := game.get_node_or_null("Explorer")
	if explorer == null:
		_fail("no Explorer")
		return {}
	var dir := _out_dir(tag)
	for old in DirAccess.get_files_at(dir):
		DirAccess.remove_absolute(dir.path_join(old))
	var altitudes := PackedFloat64Array()
	var deltas := PackedFloat64Array()
	var centres: Array = []
	var previous := PackedFloat32Array()
	var ratio := pow(END_ALTITUDE_M / START_ALTITUDE_M, 1.0 / float(STEPS - 1))
	var first: Dictionary = await HARNESS.go_pose(self, game, {"name": "flyout_start", "around_m": float(pose["around"]) - START_ALTITUDE_M * DISTANCE_PER_ALTITUDE, "across_m": pose["across"], "lift_m": float(pose["level"]) + START_ALTITUDE_M,
		"vertical_mode": "abs", "ground_m": float(pose["level"]), "yaw_deg": 0.0, "pitch_deg": PITCH_DEG}, "noon")
	if first.has("error"):
		_fail("start pose: %s" % str(first["error"]))
		return {}
	for step in STEPS:
		var altitude := START_ALTITUDE_M * pow(ratio, step)
		if far_only:
			_hide_proxies(game)
		explorer.call("TeleportToCanonicalMeters", float(pose["around"]) - altitude * DISTANCE_PER_ALTITUDE, float(pose["level"]) + altitude, float(pose["across"]), 0.0, PITCH_DEG, true)
		await HARNESS.COMMON.settle(self, 0.4)
		await HARNESS.COMMON.wait_builds_idle(self, game, 12.0, 10)
		await HARNESS.COMMON.settle(self, 0.6)
		await RenderingServer.frame_post_draw
		var image := root.get_texture().get_image()
		if image.get_format() != Image.FORMAT_RGBA8:
			image.convert(Image.FORMAT_RGBA8)
		if step == FULL_FRAME_STEP:
			image.save_png(dir.path_join("full_step%d.png" % step))
			await _save_minimap(dir.path_join("minimap_step%d.png" % step))
		image.resize(FRAME_SIZE.x, FRAME_SIZE.y, Image.INTERPOLATE_BILINEAR)
		image.save_png(dir.path_join("frame%08d.png" % step))
		var luma := _luma_small(image)
		var delta := 0.0
		if previous.size() == luma.size():
			for i in luma.size():
				delta += absf(luma[i] - previous[i])
			delta /= float(luma.size())
		previous = luma
		var patch := Vector3.ZERO
		var cx := FRAME_SIZE.x / 2
		var cy := FRAME_SIZE.y / 2
		for dy in range(-4, 5):
			for dx in range(-4, 5):
				var c := image.get_pixel(cx + dx, cy + dy)
				patch += Vector3(c.r, c.g, c.b)
		patch /= 81.0
		altitudes.append(altitude)
		deltas.append(delta)
		centres.append([patch.x, patch.y, patch.z])
		print("%s %s step %d altitude %.0f m: luma change %.4f, centre %.3f %.3f %.3f" % [MARKER, tag, step, altitude, delta, patch.x, patch.y, patch.z])
	return {"tag": tag, "altitudes": Array(altitudes), "deltas": Array(deltas), "centres": centres}


func _compare(rivers: Dictionary, gen1: Dictionary) -> void:
	var dr: Array = rivers["deltas"]
	var dg: Array = gen1["deltas"]
	var alt_r: Array = rivers["altitudes"]
	var max_r := 0.0
	var max_g := 0.0
	var at_r := 0
	var all_r := 0.0
	var all_g := 0.0
	for i in range(1, mini(dr.size(), dg.size())):
		all_r = maxf(all_r, float(dr[i]))
		all_g = maxf(all_g, float(dg[i]))
		if float(alt_r[i]) > SPIKE_MAX_ALTITUDE_M:
			continue
		if float(dr[i]) > max_r:
			max_r = float(dr[i])
			at_r = i
		max_g = maxf(max_g, float(dg[i]))
	print("%s largest single-step luma change up to %.0f m: rivers %.4f at step %d, gen-1 %.4f (limit %.2f x); over the whole fly-out rivers %.4f, gen-1 %.4f (not judged: above %.0f km the camera sees the ring edge and the planet and the change is dominated by them)" % [
		MARKER, SPIKE_MAX_ALTITUDE_M, max_r, at_r, max_g, SPIKE_FACTOR, all_r, all_g, SPIKE_MAX_ALTITUDE_M / 1000.0])
	if max_r > max_g * SPIKE_FACTOR:
		failures.append("the rivers fly-out has a spike of %.4f at step %d, above %.2f x the generation-1 level %.4f" % [max_r, at_r, SPIKE_FACTOR, max_g])
	var cr: Array = rivers["centres"]
	var cg: Array = gen1["centres"]
	var alt: Array = rivers["altitudes"]
	var far_steps := 0
	var different := 0
	var missing: Array = []
	for i in mini(cr.size(), cg.size()):
		if float(alt[i]) < LINE_FROM_ALTITUDE_M:
			continue
		far_steps += 1
		var d := absf(float(cr[i][0]) - float(cg[i][0])) + absf(float(cr[i][1]) - float(cg[i][1])) + absf(float(cr[i][2]) - float(cg[i][2]))
		if d >= LINE_CENTRE_DELTA:
			different += 1
		else:
			missing.append(i)
	var share := float(different) / maxf(1.0, float(far_steps))
	print("%s centre patch differs from gen-1 at %d of %d steps from %.0f m (%.0f %%, need %.0f %%); same at steps %s" % [MARKER, different, far_steps, LINE_FROM_ALTITUDE_M, 100.0 * share, 100.0 * MIN_LINE_SHARE, str(missing)])
	if share < MIN_LINE_SHARE:
		failures.append("the river line shows at the centre in only %.0f %% of the steps from %.0f m" % [100.0 * share, LINE_FROM_ALTITUDE_M])
