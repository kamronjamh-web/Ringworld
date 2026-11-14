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
const FULL_FRAME_STEPS := [20, 45, 60, 69]
# T2R-12b glint poses: 150 km above the river point, sun 35 degrees up; the river is under a pixel wide at this range and the aim is a few pixels off, so the measure is the peak 5 x 5 mean luma gain of the line (same pose, line on minus line off) within +-GLINT_SEARCH_HALF pixels of the frame centre.
# The mirror pose must gain at least GLINT_MIN_DELTA and the pose 90 degrees off in azimuth under GLINT_OFF_FRACTION of that: the tint of the line shows in both, only the glint belongs to the mirror pose.
const GLINT_ALTITUDE_M := 150000.0
const GLINT_SUN_ELEVATION := 35.0
const GLINT_SUN_RISING := true
const GLINT_RING_WIDTH_M := 318000.0
const GLINT_ACROSS_MIN_M := 76000.0
const GLINT_ACROSS_MAX_M := 100000.0
const GLINT_SEARCH_HALF := 100
const GLINT_PEAK_HALF := 2
const GLINT_OFF_FRACTION := 0.6
const GLINT_MIN_DELTA := 0.05
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
	var no_line := OS.get_cmdline_user_args().has("--no-proxy-line")
	var tag := ("rivers" if rivers else "gen1") + ("_far" if far_only else "") + ("_noline" if no_line else "")
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
	var series: Dictionary = await _fly_out(game, pose, tag, far_only, no_line)
	if series.is_empty():
		return
	if rivers and not far_only:
		# The glint poses sit 230 km off along the sun azimuth (about 70 km of it across the ring, towards the ring edge or away from it with the sun side; the pose 90 degrees off turns to the side that stays on the ring, up to about 205 km across): the river must be 76 to 100 km from the ring edge, so both poses stay on the 318 km ring. The widest such reach, so the glint has coverage to show.
		var glint_point: Dictionary = probe.call("FindRiverAcross", game, GLINT_ACROSS_MIN_M, GLINT_ACROSS_MAX_M, float(pose["around"]), 400000.0)
		if not bool(glint_point.get("ok", false)):
			_fail("no glint river point: %s" % str(glint_point.get("error", "")))
			return
		print("%s glint river point around=%.0f across=%.0f level=%.2f width=%.0f m" % [MARKER, float(glint_point["around"]), float(glint_point["across"]), float(glint_point["level"]), float(glint_point["width"])])
		var glint: Dictionary = await _glint(game, glint_point, _out_dir(tag), no_line)
		if glint.is_empty():
			return
		series["glint"] = glint
		if glint.has("mirror_noline") and not no_line:
			_compare_glint(glint)
	var series_path := ProjectSettings.globalize_path(OUT_ROOT).path_join("series_%s.json" % tag)
	var out := FileAccess.open(series_path, FileAccess.WRITE)
	out.store_string(JSON.stringify(series))
	out.close()
	var other_tag := ("gen1" if rivers else "rivers") + ("_far" if far_only else "")
	var other_path := ProjectSettings.globalize_path(OUT_ROOT).path_join("series_%s.json" % other_tag)
	if no_line:
		pass
	elif FileAccess.file_exists(other_path):
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


func _fly_out(game: Node, pose: Dictionary, tag: String, far_only := false, no_line := false) -> Dictionary:
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
		if no_line:
			_apply_no_line(game)
		explorer.call("TeleportToCanonicalMeters", float(pose["around"]) - altitude * DISTANCE_PER_ALTITUDE, float(pose["level"]) + altitude, float(pose["across"]), 0.0, PITCH_DEG, true)
		await HARNESS.COMMON.settle(self, 0.4)
		await HARNESS.COMMON.wait_builds_idle(self, game, 12.0, 10)
		await HARNESS.COMMON.settle(self, 0.6)
		if no_line:
			_apply_no_line(game)
			await HARNESS.COMMON.settle(self, 0.1)
		await RenderingServer.frame_post_draw
		var image := root.get_texture().get_image()
		if image.get_format() != Image.FORMAT_RGBA8:
			image.convert(Image.FORMAT_RGBA8)
		if FULL_FRAME_STEPS.has(step):
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


# ------------------------------------------------------------------ T2R-12b: glint poses and the no-line variant

## Every distinct proxy-terrain ShaderMaterial in the scene (the sector meshes carry them as surface materials).
func _proxy_materials(game: Node) -> Array:
	var found: Dictionary = {}
	var stack: Array = [game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
			var mesh := (node as MeshInstance3D).mesh
			for s in mesh.get_surface_count():
				var material := mesh.surface_get_material(s)
				if material is ShaderMaterial and (material as ShaderMaterial).shader != null and (material as ShaderMaterial).shader.resource_path.ends_with("ring_terrain_proxy.gdshader"):
					found[material.get_instance_id()] = material
		for child in node.get_children():
			stack.append(child)
	return found.values()


## --no-proxy-line: the proxy shader ignores the river coverage (test side only). The owner caches its own copy of the flag and writes only on a change, so this holds until a level is rebuilt.
func _apply_no_line(game: Node) -> void:
	for material in _proxy_materials(game):
		(material as ShaderMaterial).set_shader_parameter("river_line_enabled", false)


## Switches the Sunshine cloud compositor effects off (the cloud shell is drawn there): only the shader term is measured, and a river under one pixel wide at this range would drown in cloud noise.
func _disable_cloud_effects(game: Node) -> void:
	var stack: Array = [game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is WorldEnvironment and (node as WorldEnvironment).compositor != null:
			for effect in (node as WorldEnvironment).compositor.compositor_effects:
				if effect != null and effect.get_script() != null and (effect.get_script() as Script).resource_path.contains("Sunshine"):
					effect.enabled = false
		for child in node.get_children():
			stack.append(child)


func _flat(v: Vector3) -> Vector3:
	return Vector3(v.x, 0.0, v.z)


## The camera at a canonical pose, read back in world space.
func _camera_at(explorer: Node, around: float, vertical: float, across: float, yaw: float, pitch: float) -> Dictionary:
	explorer.call("TeleportToCanonicalMeters", around, vertical, across, yaw, pitch, true)
	await HARNESS.COMMON.settle(self, 0.25)
	var camera := root.get_camera_3d()
	return {"pos": camera.global_position, "forward": -camera.global_transform.basis.z}


## Takes the glint stills: at GLINT_ALTITUDE_M above the river point, once on the mirror ray of the sun and once 90 degrees off in azimuth at the same altitude and range. Returns the centre-patch luma of each.
func _glint(game: Node, pose: Dictionary, dir: String, no_line: bool) -> Dictionary:
	# The measurement is of the shader term: cloud meshes would cover a river that is under one pixel wide at this range.
	_disable_cloud_effects(game)
	var explorer := game.get_node_or_null("Explorer")
	var applied: Dictionary = HARNESS.COMMON.apply_time(game, {"mode": "elevation", "value": GLINT_SUN_ELEVATION, "rising": GLINT_SUN_RISING})
	if applied.has("error"):
		_fail("glint time: %s" % str(applied["error"]))
		return {}
	var around := float(pose["around"])
	var across := float(pose["across"])
	var level := float(pose["level"]) + 2.0
	# The camera sits at the world origin whatever its canonical pose (floating origin), so positions read after different teleports are not comparable. Everything is derived from one snapshot at the river point
	# (the ring centre and sun uniforms, the camera forward at yaw 0 and 90); the across direction comes from how the ring centre moves for a 1 km step across.
	var river: Dictionary = await _camera_at(explorer, around, level, across, 0.0, 0.0)
	await HARNESS.COMMON.wait_builds_idle(self, game, 60.0, 10)
	river = await _camera_at(explorer, around, level, across, 0.0, 0.0)
	var materials := _proxy_materials(game)
	if materials.is_empty():
		_fail("glint: no proxy material found")
		return {}
	var material := materials[0] as ShaderMaterial
	var centre: Vector3 = material.get_shader_parameter("ring_center")
	var sun := (material.get_shader_parameter("sun_direction") as Vector3).normalized()
	var r_pos: Vector3 = river["pos"]
	var turned: Dictionary = await _camera_at(explorer, around, level, across, 90.0, 0.0)
	var side: Dictionary = await _camera_at(explorer, around, level, across + 1000.0, 0.0, 0.0)
	var centre_side: Vector3 = (_proxy_materials(game)[0] as ShaderMaterial).get_shader_parameter("ring_center")
	var rel_r := _flat(r_pos - centre)
	var up := (-rel_r).normalized()
	var radius_datum := rel_r.length() + level
	var t_axis := _flat(river["forward"] as Vector3).normalized()
	var a_axis := (centre - centre_side).normalized()
	var yaw_sign := 1.0 if (turned["forward"] as Vector3).dot(a_axis) > 0.0 else -1.0
	print("%s glint calibration: up.t %.3f up.a %.3f t.a %.3f a.y %.3f yaw_sign %.0f sun.up %.3f radius_datum %.0f" % [MARKER, up.dot(t_axis), up.dot(a_axis), t_axis.dot(a_axis), a_axis.y, yaw_sign, sun.dot(up), radius_datum])
	# The mirror ray: the reflection of the sun direction about the local zenith.
	var mirror := (2.0 * sun.dot(up) * up - sun).normalized()
	var elevation := mirror.dot(up)
	if elevation < 0.1:
		_fail("glint: the sun is below the horizon at the river point (elevation sine %.2f), no mirror pose" % sun.dot(up))
		return {}
	var range_m := GLINT_ALTITUDE_M / elevation
	var horizontal := (mirror - up * elevation).normalized()
	var luma := {}
	# Both poses with the line first, then both again with it switched off (the owner writes the flag only on a change, so it stays off once set).
	for combo in [["mirror", false], ["off", false], ["mirror", true], ["off", true]]:
		var pose_name: String = combo[0]
		var line_off: bool = combo[1] or no_line
		var key: String = pose_name + ("_noline" if combo[1] else "")
		# The pose off the mirror ray turns 90 degrees either way, whichever side stays on the ring.
		var turn := 90.0
		if pose_name == "off" and not (across + horizontal.rotated(up, deg_to_rad(turn)).dot(a_axis) * range_m * sqrt(1.0 - elevation * elevation) > 0.0):
			turn = -90.0
		var h := horizontal if pose_name == "mirror" else horizontal.rotated(up, deg_to_rad(turn))
		var view_dir := (h * sqrt(maxf(0.0, 1.0 - elevation * elevation)) + up * elevation).normalized()
		var cam_world: Vector3 = r_pos + view_dir * range_m
		# Back to canonical: around by the angle about the ring centre, vertical by the radial difference, across along the ring axis.
		var rel_c := _flat(cam_world - centre)
		var angle := absf(rel_r.angle_to(rel_c))
		var can_around := around + (angle if (rel_c - rel_r).dot(t_axis) > 0.0 else -angle) * radius_datum
		var can_vertical := level + (rel_r.length() - rel_c.length())
		var can_across := across + (cam_world - r_pos).dot(a_axis)
		if can_across < 0.0 or can_across > GLINT_RING_WIDTH_M:
			_fail("glint %s: the camera would sit at across %.0f m, off the ring; pick another sun side or river point" % [pose_name, can_across])
			return {}
		# Aim at the river point from the camera: the heading frame is that of the camera position.
		var up_c := (-rel_c).normalized()
		var tangent_c := a_axis.cross(up_c).normalized() * (1.0 if a_axis.cross(up).dot(t_axis) > 0.0 else -1.0)
		var look := (r_pos - cam_world).normalized()
		var pitch := rad_to_deg(asin(clampf(look.dot(up_c), -1.0, 1.0)))
		var yaw := rad_to_deg(atan2(yaw_sign * look.dot(a_axis), look.dot(tangent_c)))
		print("%s glint %s target: around %.0f vertical %.0f across %.0f yaw %.1f pitch %.1f (view azimuth t %.2f a %.2f)" % [MARKER, key, can_around, can_vertical, can_across, yaw, pitch, h.dot(t_axis), h.dot(a_axis)])
		await _camera_at(explorer, can_around, can_vertical, can_across, yaw, pitch)
		await HARNESS.COMMON.wait_builds_idle(self, game, 12.0, 10)
		await HARNESS.COMMON.settle(self, 1.0)
		if line_off:
			_apply_no_line(game)
			await HARNESS.COMMON.settle(self, 0.3)
		await RenderingServer.frame_post_draw
		luma[key] = _patch_luma(dir.path_join("glint_%s.png" % key))
		print("%s glint %s: view elevation %.1f deg, range %.0f km, centre luma %.5f%s" % [MARKER, key, rad_to_deg(asin(elevation)), range_m / 1000.0, luma[key], ""])
	luma["mirror_peak"] = _peak_gain(dir.path_join("glint_mirror.png"), dir.path_join("glint_mirror_noline.png"))
	luma["off_peak"] = _peak_gain(dir.path_join("glint_off.png"), dir.path_join("glint_off_noline.png"))
	return luma


func _patch_luma(path: String) -> float:
	var image := root.get_texture().get_image()
	if image.get_format() != Image.FORMAT_RGBA8:
		image.convert(Image.FORMAT_RGBA8)
	image.save_png(path)
	var size := image.get_size()
	var c := image.get_pixel(size.x / 2, size.y / 2)
	return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b


## Peak 5 x 5 mean of (with - without) luma within GLINT_SEARCH_HALF pixels of the frame centre, from the two saved stills.
func _peak_gain(with_path: String, without_path: String) -> float:
	var a := Image.load_from_file(with_path)
	var b := Image.load_from_file(without_path)
	var w := a.get_width()
	var h := a.get_height()
	var x0 := w / 2 - GLINT_SEARCH_HALF - GLINT_PEAK_HALF
	var y0 := h / 2 - GLINT_SEARCH_HALF - GLINT_PEAK_HALF
	var n := 2 * (GLINT_SEARCH_HALF + GLINT_PEAK_HALF) + 1
	var gain := PackedFloat32Array()
	gain.resize(n * n)
	for y in n:
		for x in n:
			var ca := a.get_pixel(x0 + x, y0 + y)
			var cb := b.get_pixel(x0 + x, y0 + y)
			gain[y * n + x] = (0.2126 * ca.r + 0.7152 * ca.g + 0.0722 * ca.b) - (0.2126 * cb.r + 0.7152 * cb.g + 0.0722 * cb.b)
	var best := -1.0
	var k := 2 * GLINT_PEAK_HALF + 1
	for y in range(0, n - k + 1):
		for x in range(0, n - k + 1):
			var sum := 0.0
			for j in k:
				for i in k:
					sum += gain[(y + j) * n + x + i]
			best = maxf(best, sum / float(k * k))
	return best


func _compare_glint(glint: Dictionary) -> void:
	var mirror_gain := float(glint["mirror_peak"])
	var off_gain := float(glint["off_peak"])
	print("%s glint: peak 5 x 5 luma gain of the line, mirror pose %.4f (need at least %.4f), pose 90 degrees off %.4f (need under %.4f = %.1f x the mirror gain)" % [MARKER, mirror_gain, GLINT_MIN_DELTA, off_gain, GLINT_OFF_FRACTION * mirror_gain, GLINT_OFF_FRACTION])
	if mirror_gain < GLINT_MIN_DELTA:
		failures.append("the mirror pose gains only %.4f luma from the line (need %.4f)" % [mirror_gain, GLINT_MIN_DELTA])
	if off_gain >= GLINT_OFF_FRACTION * mirror_gain:
		failures.append("off the mirror ray the line gains %.4f luma, not under %.1f x the mirror gain %.4f" % [off_gain, GLINT_OFF_FRACTION, mirror_gain])
