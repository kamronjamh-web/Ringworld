extends SceneTree

## NF-5 GPU test (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N4, N8): the lighting of the near ground fog, in the froxels and in the fog-end pass, from one shared function
## (materials/ring_near_fog_light.gdshaderinc). Boots the pinned v8 fixture (tools/landscape_harness_common.gd) in still-capture mode (tools/tree_visibility_world.gd) with the wind held and FXAA.
## Every radiance below is the resolved HDR colour before the fog-end apply pass, read back from the render thread (RingNearFogEndComposite.request_state_readback); "thick fog" is
## RingNearFog.DebugUniformSigma 0.3 per metre, where the pixel is the fog's own radiance (about 3 m from the camera) and nothing behind it.
##  1. cloud shadow: a synthetic near cloud-shadow cascade (tools/NearFogShadowProbe.cs, installed through RingCloudShadowProjector like Sunshine's) shades the right half of the view (across > 0);
##     the fog and the ground there are darker than with a clear map, the left half is unchanged. Reports both ratios.
##  2. height shadow: at a low sun a terrain-shadowed pose is found by the ground (RingTerrainHeightShadow.ReceiversEnabled on against off); the fog there is darker too.
##  3. tree shafts at forest_edge_eye (sun 12 deg, looking sunward, fog on): the in-scatter (sun volumetric energy 1 minus 0) is not uniform; reports its contrast.
##  4. ambient calibration: Godot's own lit white Lambert card facing up, ambient only (sun off) against A_up (ringworld_fog_ambient), and thick fog (sun off) against k x card.
##  5. night (planet light): forced thick fog is not black and blue-grey (b > r).
##  6. cost: A-B-A on the root viewport GPU time of fog off, fog on, fog on without the fill light (RingNearFog.DebugFillOff) and the haze sun transmittance per froxel against one camera
##     value (RingNearFog.DebugHazeCameraValue); prints fog cost, fill light cost, haze cost and the noise floor (info only, no threshold).
##  6b. flicker: thin mist (sigma 0.002 per metre, real density) at hills_eye / sun_8_set, 60 consecutive LDR frames against a fog-off baseline: mean per-pixel luma step, largest
##     frame-mean step and p99 step; fails if the mist adds more than 1.0 (mean) or 0.5 (frame mean) levels, or if under 5 % of pixels differ (no mist seen). --flicker_frames=<dir>
##     saves the frames (<dir>/frame%08d.png, <dir>/off/ for the baseline) for tools/media/ffmpeg_tools.ps1 flicker.
## Stills (forced fog, amount 1) under .godot/audit_baseline/common/atmosphere/nf5/ (--stills=<dir>): NF5_ONLY=<check> runs one check (clouds, height, shafts, ambient, night, cost, flicker, stills).
## The river steam still needs inland water: run by hand with --generation=1 --review-features=water.rivers (tools/run_godot.ps1 -UserArgs) for a river point from tools/InlandSwimProbe.cs;
## the v8 fixture has none near its poses and reports so.
## RUN (windowed Vulkan, no other Godot running): tools/run_tests.ps1 -Label nf5 -Tests near_fog_shadow_gpu_test
## Prints NEAR_FOG_SHADOW=PASS (or =FAIL <reason>) and per-check numbers. Test only; the harness fixture world is deleted at exit.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const PROBE := preload("res://tools/NearFogShadowProbe.cs")
const SWIM := preload("res://tools/InlandSwimProbe.cs")
const MARKER := "NEAR_FOG_SHADOW"
const N2_DRIVE := Vector3(1.0, 0.5, 0.0)
const NO_DRIVE := Vector3(-1.0, -1.0, -1.0)
const THICK := 1.0   # Godot weights EMISSION by min(density, 1) per metre: above 1 the fog radiance falls as 1/sigma (measured), so the calibration uses exactly 1
const OMEGA := 0.99
const RHO_G := 0.11
const AMBIENT_TOLERANCE := 0.10   # per channel, thick fog against omega x A (the fill light's F = A)
const CLOUD_STRENGTH := 0.8

var game: Node
var nf: Node
var failures: Array[String] = []
var finished := false
var args := {}
var stills_dir := ""
var probe: RefCounted
var camera_default := Transform3D.IDENTITY
var extra_poses := {}   # run-time poses by name (the rivers world's river point)


func _init() -> void:
	call_deferred("_run")


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	_cleanup()
	quit(1)


func _cleanup() -> void:
	if game != null and is_instance_valid(game):
		WORLD.capture_end(game)
		var bridge := game.get_node_or_null("SunshineCloudBridge")
		if bridge != null:
			bridge.process_mode = Node.PROCESS_MODE_INHERIT
		var producer := game.get_node_or_null("TerrainHeightShadow")
		if producer != null:
			producer.set("ReceiversEnabled", true)
		var sun := game.get_node_or_null("StarLight") as Light3D
		if sun != null:
			sun.light_volumetric_fog_energy = 1.0
	if probe != null:
		probe.call("Remove")
	HARNESS.remove_dir_recursive(ProjectSettings.globalize_path("res://.godot/captures/landscape/fixture_world"))


func _frames(n: int) -> void:
	for i in n:
		WORLD.hold_wind()
		await process_frame


func _stable_grab() -> Image:
	await _frames(20)
	var prev: Image
	for i in 40:
		WORLD.hold_wind()
		await RenderingServer.frame_post_draw
		var img := WORLD.grab(self)
		if prev != null and prev.get_data() == img.get_data():
			return img
		prev = img
	return prev


func _set_aa_fxaa() -> void:
	root.use_taa = false
	root.screen_space_aa = Viewport.SCREEN_SPACE_AA_FXAA
	root.msaa_3d = Viewport.MSAA_DISABLED


## Fog on (amount 1, the given drive, gate 1, optional uniform sigma) and waits for the fade to complete, or off.
func _fog(on: bool, drive: Vector3 = N2_DRIVE, sigma := -1.0) -> bool:
	nf.set("DebugUniformSigma", sigma)
	if on:
		nf.set("ForcedGate", 1.0)
		nf.set("ForcedDrive", drive)
		nf.call("DebugSetAmount", 1.0)
		var deadline := Time.get_ticks_msec() + 10000
		while Time.get_ticks_msec() < deadline and not (bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999):
			await process_frame
		await _frames(10)
		return bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999
	nf.call("DebugSetAmount", 0.0)
	nf.set("ForcedDrive", NO_DRIVE)
	nf.set("ForcedGate", -1.0)
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline and bool(nf.get("FogEnabled")):
		await process_frame
	await _frames(10)
	return not bool(nf.get("FogEnabled"))


func _await_fields_here() -> bool:
	var producer := game.get_node_or_null("TerrainHeightShadow")
	var deadline := Time.get_ticks_msec() + 300000
	var stable := 0
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var values: Dictionary = producer.call("GetGlobalValues")
		var d: Dictionary = producer.call("GetDiagnostics")
		var params = values.get("ringworld_near_fog_fields_params")
		var bounds = values.get("ringworld_near_fog_t0_bounds")
		if not (params is Vector4 and (params as Vector4).z > 0.5 and bounds is Vector4) or str(d["phase"]) != "Idle":
			stable = 0
			continue
		var now: Dictionary = HARNESS.COMMON.pose_now(game)
		var circumference := float(game.get_node("ChunkGridManager").call("GetCanonicalRingCircumferenceMeters64"))
		var ca := fposmod(float(now["around"]) - float(d["anchor_around"]) + circumference * 0.5, circumference) - circumference * 0.5
		var cb := float(now["across"]) - float(d["anchor_across"])
		var b: Vector4 = bounds
		if ca - b.x > 300.0 and b.y - ca > 300.0 and cb - b.z > 300.0 and b.w - cb > 300.0:
			stable += 1
			if stable >= 30:
				return true
		else:
			stable = 0
	return false


func _go(pose_name: String, time_name: String, heading: Dictionary = {}) -> bool:
	var pose: Dictionary = extra_poses[pose_name] if extra_poses.has(pose_name) else HARNESS.pose_by_name(pose_name)
	var info: Dictionary = await HARNESS.go_pose(self, game, pose, time_name, null, heading)
	if info.has("error"):
		failures.append("pose %s: %s" % [pose_name, str(info["error"])])
		return false
	WORLD.freeze_world(game)
	root.get_camera_3d().transform = camera_default   # an earlier check may have turned the camera with look_at
	await _frames(60)
	if not await _await_fields_here():
		failures.append("pose %s: the terrain fields never covered the camera" % pose_name)
		return false
	return true


## The same pose with a different time of day (dawn needs a rising sun, which the fixture's time table has no name for).
func _set_time(mode: Dictionary) -> bool:
	var state: Dictionary = HARNESS.COMMON.apply_time(game, mode)
	if state.has("error"):
		failures.append("time %s: %s" % [str(mode), str(state["error"])])
		return false
	await HARNESS.COMMON.settle(self, 2.0)
	WORLD.freeze_world(game)
	await _frames(30)
	return true


## Goes to the pose facing the sun's azimuth: one visit at yaw 0 measures the sun in the camera's frame, the turn is then tried both ways (the explorer's yaw sign is not assumed).
func _face_sun(pose_name: String, time_name: String, pitch: float) -> bool:
	if not await _go(pose_name, time_name, {"yaw": 0.0, "pitch": pitch}):
		return false
	var cam := root.get_camera_3d()
	var up := (game.get_node("Explorer") as Node3D).global_basis.y.normalized()
	var sun := (game.get_node("StarLight") as Node3D).global_basis.z.normalized()
	var s := (sun - up * sun.dot(up)).normalized()
	var f0 := -cam.global_basis.z.normalized()
	f0 = (f0 - up * f0.dot(up)).normalized()
	var r0 := f0.cross(up).normalized()
	var turn := rad_to_deg(atan2(s.dot(r0), s.dot(f0)))
	for yaw in [turn, -turn]:
		if not await _go(pose_name, time_name, {"yaw": yaw, "pitch": pitch}):
			return false
		var f := -root.get_camera_3d().global_basis.z.normalized()
		f = (f - up * f.dot(up)).normalized()
		print("%s facing the sun: yaw %.1f gives cos %.3f to the sun azimuth" % [MARKER, yaw, f.dot(s)])
		if f.dot(s) > 0.95:
			return true
	failures.append("%s: could not face the sun" % pose_name)
	return false


# ------------------------------------------------------------------ HDR readback (RingNearFogEndComposite.request_state_readback)

## One still-capture arm: optionally the LDR frame, and the HDR colour before and after the pass read back from the render thread.
func _arm(want_ldr := false) -> Dictionary:
	await WORLD.capture_begin(self, game)
	var img: Image = null
	if want_ldr:
		img = await _stable_grab()
	var eff: Object = nf.get("EndEffect")
	if eff == null:
		WORLD.capture_end(game)
		return {}
	eff.call("request_state_readback")
	var started := Time.get_ticks_msec()
	var frames: Dictionary = {}
	while Time.get_ticks_msec() - started < 8000:
		await RenderingServer.frame_post_draw
		frames = eff.call("get_last_frames")
		if not frames.is_empty():
			break
	WORLD.capture_end(game)
	return {"img": img, "frames": frames}


func _px(bytes: PackedByteArray, width: int, x: int, y: int) -> Vector3:
	var o := (y * width + x) * 8
	return Vector3(bytes.decode_half(o), bytes.decode_half(o + 2), bytes.decode_half(o + 4))


func _hl(c: Vector3) -> float:
	return 0.2126 * c.x + 0.7152 * c.y + 0.0722 * c.z


## Mean HDR colour (before the apply pass) over a rectangle given in 0..1 of the frame: [x0, y0, x1, y1].
func _mean(arm: Dictionary, r: Array) -> Vector3:
	if arm.is_empty() or arm["frames"].is_empty():
		return Vector3.ZERO
	var size: Vector2i = arm["frames"]["size"]
	var bytes: PackedByteArray = arm["frames"]["pre"]
	var sum := Vector3.ZERO
	var n := 0
	for y in range(int(r[1] * size.y), int(r[3] * size.y), 2):
		for x in range(int(r[0] * size.x), int(r[2] * size.x), 2):
			sum += _px(bytes, size.x, x, y)
			n += 1
	return sum / maxf(float(n), 1.0)


func _ok(arm: Dictionary) -> bool:
	return not arm.is_empty() and not arm["frames"].is_empty()


func _node_float(node: Object, prop: String, fallback := 0.0) -> float:
	var v = node.get(prop)
	return float(v) if v is float else fallback


func _a_up() -> Vector3:
	var v = game.get_node("DaySideAtmosphere").get("published_fog_ambient")
	return v if v is Vector3 else Vector3.ZERO


func _sun() -> Light3D:
	return game.get_node("StarLight") as Light3D


func _rgb(v: Vector3) -> String:
	return "(%.4f, %.4f, %.4f)" % [v.x, v.y, v.z]


# ------------------------------------------------------------------ 1. cloud shadow

const LEFT := [0.04, 0.35, 0.28, 0.65]
const RIGHT := [0.72, 0.35, 0.96, 0.65]


func _install_cloud(shadow: bool) -> void:
	var cam := root.get_camera_3d()
	var explorer := game.get_node("Explorer") as Node3D
	var up := explorer.global_basis.y.normalized()
	var fwd := -cam.global_basis.z.normalized()
	fwd = (fwd - up * fwd.dot(up)).normalized()
	var right := fwd.cross(up).normalized()
	probe.call("Install", cam.global_position, fwd, right, up, 300.0, CLOUD_STRENGTH, shadow)
	# the camera's own right is not necessarily +across of this frame: verify against the screen below (RIGHT must be the shaded side)
	await _frames(20)


func _check_clouds() -> void:
	if not await _go("hills_eye", "noon", {"yaw": 0.0, "pitch": -25.0}):
		return
	_set_aa_fxaa()
	var bridge := game.get_node_or_null("SunshineCloudBridge")
	if bridge != null:
		bridge.process_mode = Node.PROCESS_MODE_DISABLED
	# the shaded side is the camera's right when the screen's right half darkens; the frame below uses across = camera right, so it must
	var rows := {}
	for label in ["ground", "fog"]:
		if not await _fog(true, N2_DRIVE, 0.0 if label == "ground" else THICK):
			failures.append("clouds: the forced fog never turned on")
			return
		await _install_cloud(false)
		var clear := await _arm()
		await _install_cloud(true)
		var shade := await _arm()
		if not (_ok(clear) and _ok(shade)):
			failures.append("clouds %s: no readback" % label)
			return
		var l_clear := _hl(_mean(clear, LEFT))
		var r_clear := _hl(_mean(clear, RIGHT))
		var l_shade := _hl(_mean(shade, LEFT))
		var r_shade := _hl(_mean(shade, RIGHT))
		rows[label] = [l_shade / maxf(l_clear, 1e-6), r_shade / maxf(r_clear, 1e-6)]
		print("%s clouds %s: luma left clear %.4f shade %.4f (ratio %.3f), right clear %.4f shade %.4f (ratio %.3f)" % [MARKER, label, l_clear, l_shade, rows[label][0], r_clear, r_shade, rows[label][1]])
		await _fog(false)
	probe.call("Remove")
	if bridge != null:
		bridge.process_mode = Node.PROCESS_MODE_INHERIT
	var g: Array = rows["ground"]
	var f: Array = rows["fog"]
	print("%s clouds: shaded-side ratio ground %.3f, fog %.3f (strength %.2f, day 1); unshaded side ground %.3f, fog %.3f" % [MARKER, g[1], f[1], CLOUD_STRENGTH, g[0], f[0]])
	if g[1] > 0.95:
		failures.append("clouds: the ground is not darker on the shaded side (ratio %.3f): the synthetic cascade did not reach the receivers" % g[1])
	if f[1] > 0.95:
		failures.append("clouds: the fog is not darker on the shaded side (ratio %.3f)" % f[1])
	if absf(f[0] - 1.0) > 0.03 or absf(g[0] - 1.0) > 0.03:
		failures.append("clouds: the unshaded side changed (ground %.3f, fog %.3f)" % [g[0], f[0]])


# ------------------------------------------------------------------ 2. height shadow

func _check_height() -> void:
	var producer := game.get_node_or_null("TerrainHeightShadow")
	if producer == null:
		failures.append("height: no TerrainHeightShadow node")
		return
	var found := false
	for cand in [["hills_eye", "sun_2_set", 270.0], ["hills_eye", "sun_5_set", 270.0], ["plains_eye", "sun_2_set", 90.0], ["coast_eye", "sun_2_set", 90.0], ["hills_eye", "sun_2_set", 90.0], ["forest_clearing_eye", "sun_2_set", 90.0]]:
		if not await _go(cand[0], cand[1], {"yaw": cand[2], "pitch": -15.0}):
			failures.append("height: pose %s unavailable" % cand[0])
			failures.clear()
			continue
		_set_aa_fxaa()
		var ratios := {}
		for label in ["ground", "fog"]:
			if not await _fog(true, N2_DRIVE, 0.0 if label == "ground" else THICK):
				failures.append("height: the forced fog never turned on")
				return
			producer.set("ReceiversEnabled", true)
			await _frames(30)
			var on := await _arm()
			producer.set("ReceiversEnabled", false)
			await _frames(30)
			var off := await _arm()
			producer.set("ReceiversEnabled", true)
			if not (_ok(on) and _ok(off)):
				failures.append("height %s: no readback" % label)
				return
			var a := _mean(on, [0.2, 0.3, 0.8, 0.7])
			var b := _mean(off, [0.2, 0.3, 0.8, 0.7])
			ratios[label] = _hl(a) / maxf(_hl(b), 1e-6)
			print("%s height %s/%s yaw %d %s: luma receivers on %.4f off %.4f, ratio %.3f" % [MARKER, cand[0], cand[1], int(cand[2]), label, _hl(a), _hl(b), ratios[label]])
			await _fog(false)
		print("%s height %s/%s: terrain-shadow ratio ground %.3f, fog %.3f" % [MARKER, cand[0], cand[1], ratios["ground"], ratios["fog"]])
		if ratios["ground"] < 0.9:
			found = true
			if ratios["fog"] > 0.95:
				failures.append("height: the ground is in terrain shadow (ratio %.3f) but the fog is not darker (ratio %.3f)" % [ratios["ground"], ratios["fog"]])
			break
	if not found:
		failures.append("height: no candidate pose is in the terrain shadow of a ridge at a low sun (ground ratio never below 0.9)")


# ------------------------------------------------------------------ 3. tree shafts

func _percentile(values: Array, q: float) -> float:
	var sorted := values.duplicate()
	sorted.sort()
	return sorted[clampi(int(q * (sorted.size() - 1)), 0, sorted.size() - 1)]


func _check_shafts() -> void:
	if not await _face_sun("forest_edge_eye", "sun_12_set", 3.0):
		return
	_set_aa_fxaa()
	if not await _fog(true, Vector3(0.0, 1.0, 0.0)):
		failures.append("shafts: the forced fog never turned on")
		return
	var sun := _sun()
	sun.light_volumetric_fog_energy = 1.0
	await _frames(90)
	var with_sun := await _arm(true)
	sun.light_volumetric_fog_energy = 0.0
	await _frames(90)
	var without := await _arm(true)
	sun.light_volumetric_fog_energy = 1.0
	if not (_ok(with_sun) and _ok(without)):
		failures.append("shafts: no readback")
		return
	var size: Vector2i = with_sun["frames"]["size"]
	var a: PackedByteArray = with_sun["frames"]["pre"]
	var b: PackedByteArray = without["frames"]["pre"]
	# The sun's in-scatter I = (fog with the sun's volumetric energy 1) - (energy 0), per pixel, over the middle of the frame. A smooth glow (the forward lobe toward the sun) has no local contrast;
	# lit and shaded air columns (the shadow atlas, trees) show as pixels whose I differs from the mean of their 11 x 11 neighbourhood. Printed for the heatmap too.
	var step := 2
	var x0 := int(0.05 * size.x)
	var y0 := int(0.15 * size.y)
	var gw := (int(0.95 * size.x) - x0) / step
	var gh := (int(0.75 * size.y) - y0) / step
	var grid := PackedFloat32Array()
	grid.resize(gw * gh)
	var mean := 0.0
	for gy in gh:
		for gx in gw:
			var v := maxf(_hl(_px(a, size.x, x0 + gx * step, y0 + gy * step)) - _hl(_px(b, size.x, x0 + gx * step, y0 + gy * step)), 0.0)
			grid[gy * gw + gx] = v
			mean += v
	mean /= grid.size()
	var variance := 0.0
	for v in grid:
		variance += (v - mean) * (v - mean)
	var cv := sqrt(variance / grid.size()) / maxf(mean, 1e-6)
	var bright := 0
	var contrasty := 0
	for gy in range(5, gh - 5):
		for gx in range(5, gw - 5):
			var local := 0.0
			for oy in range(-5, 6):
				for ox in range(-5, 6):
					local += grid[(gy + oy) * gw + gx + ox]
			local /= 121.0
			if local > 0.02:
				bright += 1
				if absf(grid[gy * gw + gx] - local) > 0.25 * local:
					contrasty += 1
	var frac := float(contrasty) / maxf(float(bright), 1.0)
	print("%s shafts (sun 12 deg, facing the sun): sun in-scatter mean luma %.4f, coefficient of variation %.3f; of %d pixels with in-scatter > 0.02 in their 11 x 11 neighbourhood %.1f %% differ from the local mean by more than 25 %% (lit and shaded columns)" % [MARKER, mean, cv, bright, frac * 100.0])
	with_sun["img"].save_png(stills_dir.path_join("shafts_sun_on.png"))
	without["img"].save_png(stills_dir.path_join("shafts_sun_inscatter_off.png"))
	var p90 := 0.0
	var sorted_grid := Array(grid)
	sorted_grid.sort()
	p90 = sorted_grid[int(0.9 * (sorted_grid.size() - 1))]
	var heat := Image.create(size.x, size.y, false, Image.FORMAT_RGB8)
	for y in size.y:
		for x in size.x:
			var d := clampf((_hl(_px(a, size.x, x, y)) - _hl(_px(b, size.x, x, y))) / maxf(p90 * 1.5, 1e-4), 0.0, 1.0)
			heat.set_pixel(x, y, Color(d, d * 0.8, 1.0 - d))
	heat.save_png(stills_dir.path_join("shafts_sun_inscatter_heatmap.png"))
	if mean < 1e-4:
		failures.append("shafts: the sun adds no in-scatter at all (mean %.6f)" % mean)
	elif bright < 500 or frac < 0.02:
		failures.append("shafts: no lit and shaded columns in the sun in-scatter (%d bright pixels, %.1f %% with local contrast)" % [bright, frac * 100.0])
	await _fog(false)


# ------------------------------------------------------------------ 4. ambient calibration

func _check_ambient() -> void:
	if not await _go("hills_eye", "noon", {"yaw": 0.0, "pitch": -35.0}):
		return
	_set_aa_fxaa()
	var sun := _sun()
	sun.visible = false
	var day_side_node := game.get_node("DaySideAtmosphere")
	var contribution_start: float = day_side_node.get("ambient_sky_contribution")
	print("%s ambient: run at the shipped sky contribution %.3f (day_side_atmosphere default, not forced)" % [MARKER, contribution_start])
	await _frames(60)
	var a_up := _a_up()
	if a_up.length() < 1e-4:
		failures.append("ambient: ringworld_fog_ambient is not published (%s)" % _rgb(a_up))
		sun.visible = true
		return
	# the card: 2 m square, facing up, 0.25 m above the ground, white diffuse
	var cam := root.get_camera_3d()
	var explorer := game.get_node("Explorer") as Node3D
	var up := explorer.global_basis.y.normalized()
	var fwd := -cam.global_basis.z.normalized()
	var card := MeshInstance3D.new()
	var quad := QuadMesh.new()
	quad.size = Vector2(2.0, 2.0)
	card.mesh = quad
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(1, 1, 1)
	mat.roughness = 1.0
	mat.metallic_specular = 0.0
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	card.material_override = mat
	game.add_child(card)
	# QuadMesh faces +Z: +Z is ring up
	var z := up
	var x := fwd.cross(z).normalized()
	var y := z.cross(x).normalized()
	card.global_basis = Basis(x, y, z)
	card.global_position = cam.global_position + fwd * 2.5
	if not await _fog(true, N2_DRIVE, 0.0):
		failures.append("ambient: the forced fog never turned on")
		card.queue_free()
		sun.visible = true
		return
	await _frames(60)
	var screen := cam.unproject_position(card.global_position)
	var vp := root.get_visible_rect().size
	var rect := [(screen.x - 24.0) / vp.x, (screen.y - 24.0) / vp.y, (screen.x + 24.0) / vp.x, (screen.y + 24.0) / vp.y]
	var arm_card := await _arm(true)
	if arm_card.get("img") != null:
		arm_card["img"].save_png(stills_dir.path_join("ambient_card.png"))
	var card_hdr := _mean(arm_card, rect)
	await _frames(60)
	card.queue_free()
	await _frames(30)
	var env := (game.get_node("WorldEnvironment") as WorldEnvironment).environment
	var day_side := game.get_node("DaySideAtmosphere")
	print("%s ambient inputs: colour %s energy %.4f sky contribution %.3f source %d; L_up day %s planet %s; L_up cos day %s; haze ambient day raw %s; day factor %.3f" % [MARKER, str(env.ambient_light_color), env.ambient_light_energy,
		env.ambient_light_sky_contribution, env.ambient_light_source, str(day_side.get("_haze_lup_day")), str(day_side.get("_haze_lup_planet")), str(day_side.get("_haze_lup_cos_day")), str(day_side.get("_haze_ambient_day_raw")), float(day_side.get("current_day_factor"))])
	var card_ratio := Vector3(card_hdr.x / a_up.x, card_hdr.y / a_up.y, card_hdr.z / a_up.z)
	print("%s ambient: A_up %s, white card (sun off) %s, card / A_up %s" % [MARKER, _rgb(a_up), _rgb(card_hdr), _rgb(card_ratio)])
	# thick fog, sun off, looking nearly level so that the terrain behind the fog is far (T about 0)
	cam.look_at(cam.global_position + fwd.slide(up).normalized() * 10.0 - up * 1.0, up)
	await _frames(30)
	if not await _fog(true, N2_DRIVE, THICK):
		failures.append("ambient: the thick fog never turned on")
		sun.visible = true
		return
	await _frames(60)
	var arm_fog := await _arm()
	var fog_hdr := _mean(arm_fog, [0.4, 0.4, 0.6, 0.6])
	var expected := OMEGA * a_up   # thick fog with the lights off = the fill light alone: ALBEDO_eff = omega, in-scatter omega x A
	print("%s ambient: local up vs world +Y angle %.2f deg (the engine's ambient inject samples world +Y; the fill light does not)" % [MARKER, rad_to_deg(Vector3.UP.angle_to(nf.get("LastRingUp")))])
	var fog_ratio := Vector3(fog_hdr.x / expected.x, fog_hdr.y / expected.y, fog_hdr.z / expected.z)
	print("%s ambient: thick fog (sun off) %s, omega x A %s, fog / expected %s" % [MARKER, _rgb(fog_hdr), _rgb(expected), _rgb(fog_ratio)])
	for sweep in [4.0, 16.0]:
		if await _fog(true, N2_DRIVE, sweep):
			await _frames(60)
			var arm_s := await _arm()
			var hdr_s := _mean(arm_s, [0.4, 0.4, 0.6, 0.6])
			print("%s ambient sweep sigma %.1f: thick fog %s, / expected %s" % [MARKER, sweep, _rgb(hdr_s), _rgb(Vector3(hdr_s.x / expected.x, hdr_s.y / expected.y, hdr_s.z / expected.z))])
	sun.visible = true
	await _fog(false)
	# The engine-convention check: thick fog with the sun and the planet light off is the fill light alone, omega x A in every channel (F = A).
	var card_luma := _hl(card_hdr) / _hl(a_up)
	var fog_luma := _hl(fog_hdr) / _hl(expected)
	print("%s ambient: info only: card / A luminance %.3f; thick fog / card luminance %.3f (fog is expected brighter than a shaded Lambert card: equal-area sky plus ground bounce); fog / (omega A) luminance %.3f" % [MARKER, card_luma, _hl(fog_hdr) / maxf(_hl(card_hdr), 1e-6), fog_luma])
	for c in 3:
		if absf(fog_ratio[c] - 1.0) > AMBIENT_TOLERANCE:
			failures.append("ambient: channel %d of the thick fog is %.1f %% off omega x A (fog %s, expected %s)" % [c, (fog_ratio[c] - 1.0) * 100.0, _rgb(fog_hdr), _rgb(expected)])


# ------------------------------------------------------------------ 5. night

func _check_night() -> void:
	if not await _go("hills_eye", "night_planet", {"yaw": 0.0, "pitch": 5.0}):
		return
	_set_aa_fxaa()
	if not await _fog(true, N2_DRIVE, THICK):
		failures.append("night: the forced fog never turned on")
		return
	await _frames(60)
	var arm := await _arm(true)
	if not _ok(arm):
		failures.append("night: no readback")
		return
	var hdr := _mean(arm, [0.3, 0.3, 0.7, 0.7])
	print("%s night (planet light): thick fog %s, A_up %s" % [MARKER, _rgb(hdr), _rgb(_a_up())])
	var planet := game.get_node_or_null("PlanetLight") as Light3D
	var cam_fwd := -root.get_camera_3d().global_basis.z
	print("%s night lights: sun visible %s energy %.3f vol %.2f cos(view, to sun) %.3f | planet %s energy %.3f vol %.2f colour %s cos(view, to planet) %.3f" % [MARKER, str(_sun().is_visible_in_tree()), _sun().light_energy, _sun().light_volumetric_fog_energy, cam_fwd.dot(_sun().global_basis.z),
		str(planet != null and planet.is_visible_in_tree()), planet.light_energy if planet != null else -1.0, planet.light_volumetric_fog_energy if planet != null else -1.0, str(planet.light_color) if planet != null else "-", cam_fwd.dot(planet.global_basis.z) if planet != null else 0.0])
	var sun_off_hdr := Vector3.ZERO
	for variant in ["planet vol 0", "sun off"]:
		if variant == "planet vol 0" and planet != null:
			planet.light_volumetric_fog_energy = 0.0
		elif variant == "sun off":
			_sun().visible = false
		await _frames(60)
		var arm_v := await _arm()
		var v_hdr := _mean(arm_v, [0.3, 0.3, 0.7, 0.7])
		if variant == "sun off":
			sun_off_hdr = v_hdr
		print("%s night %s: thick fog %s" % [MARKER, variant, _rgb(v_hdr)])
	if planet != null:
		planet.light_volumetric_fog_energy = 1.0
	_sun().visible = true
	if _hl(sun_off_hdr) > 0.0 and _hl(hdr) > 3.0 * _hl(sun_off_hdr):
		failures.append("night: the sun lights the night fog (luma %.4f with the sun, %.4f with it off): ALBEDO scales the StarLight that stays on below the horizon" % [_hl(hdr), _hl(sun_off_hdr)])
	if arm.get("img") != null:
		arm["img"].save_png(stills_dir.path_join("night_thick_fog.png"))
	if _hl(hdr) < 1e-4:
		failures.append("night: the forced fog is black (%s)" % _rgb(hdr))
	if not (hdr.z > hdr.x):
		failures.append("night: the forced fog is not blue-grey (r %.4f, b %.4f)" % [hdr.x, hdr.z])
	await _fog(false)


# ------------------------------------------------------------------ 6. cost

func _check_cost() -> void:
	if not await _go("hills_eye", "sun_10_set", {"yaw": 270.0, "pitch": 6.0}):
		return
	_set_aa_fxaa()
	nf.call("DebugSetLayerHeight", 150.0)
	if not await _fog(true, N2_DRIVE):
		failures.append("cost: the forced fog never turned on")
		return
	var rid := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(rid, true)
	# [name, fog on, DebugFillOff, DebugHazeCameraValue]
	var off := ["fog off", false, false, false]
	var on := ["fog on", true, false, false]
	var nofill := ["fog on, no fill", true, true, false]
	var cam := ["camera value", true, false, true]
	var variants := [off, on, nofill, cam]
	var samples := {}
	for v in variants:
		samples[v[0]] = []
	# Each round brackets every variant; round 0 is A-B-A ordered, later rounds rotate the order.
	var orders := [[off, on, nofill, on, cam, on, off], [on, nofill, cam, off, on], [nofill, cam, off, on, nofill], [cam, off, on, nofill, cam]]
	var fog_state := true
	for round in 4:
		for v in orders[round]:
			if bool(v[1]) != fog_state:
				fog_state = bool(v[1])
				if not await _fog(fog_state, N2_DRIVE):
					failures.append("cost: the fog did not switch to %s" % ("on" if fog_state else "off"))
			nf.set("DebugFillOff", v[2])
			nf.set("DebugHazeCameraValue", v[3])
			await _frames(90)
			var window := []
			for i in 240:
				WORLD.hold_wind()
				await RenderingServer.frame_post_draw
				window.append(RenderingServer.viewport_get_measured_render_time_gpu(rid))
			window.sort()
			samples[v[0]].append(window[window.size() / 2])
	RenderingServer.viewport_set_measure_render_time(rid, false)
	nf.set("DebugHazeCameraValue", false)
	nf.set("DebugFillOff", false)
	var med := {}
	var spread := {}
	for v in variants:
		var arr: Array = samples[v[0]]
		print("%s cost %s: window medians %s ms" % [MARKER, v[0], ", ".join(PackedStringArray(arr.map(func(x): return "%.3f" % x)))])
		var sorted := arr.duplicate()
		sorted.sort()
		med[v[0]] = sorted[sorted.size() / 2]
		spread[v[0]] = sorted[sorted.size() - 1] - sorted[0]
		print("%s cost %s: median %.3f ms, spread %.3f ms" % [MARKER, v[0], med[v[0]], spread[v[0]]])
	print("%s cost (root viewport %dx%d, GPU): fog on - off %.3f ms, fill light %.3f ms (on - no fill), haze per froxel vs camera value %.3f ms (decision threshold 0.08 ms), noise floor %.3f ms" % [MARKER, root.size.x, root.size.y, med["fog on"] - med["fog off"], med["fog on"] - med["fog on, no fill"], med["fog on"] - med["camera value"], spread["fog on"]])
	nf.call("DebugSetLayerHeight", 40.0)
	await _fog(false)


# ------------------------------------------------------------------ 6b. thin mist flicker

const FLICKER_FRAMES := 60
const FLICKER_STEP := 2   # sample every 2nd pixel in x and y


func _luma_grid(img: Image) -> PackedFloat32Array:
	var w := img.get_width()
	var h := img.get_height()
	var bytes := img.get_data()   # RGBA8
	var out := PackedFloat32Array()
	for y in range(0, h, FLICKER_STEP):
		var row := y * w * 4
		for x in range(0, w, FLICKER_STEP):
			var o := row + x * 4
			out.append(0.2126 * bytes[o] + 0.7152 * bytes[o + 1] + 0.0722 * bytes[o + 2])
	return out


## Grabs FLICKER_FRAMES consecutive LDR frames; returns their luma grids and optionally saves them.
func _flicker_arm(save_dir: String) -> Array:
	await _frames(120)
	var grids := []
	if not save_dir.is_empty():
		DirAccess.make_dir_recursive_absolute(save_dir)
	for i in FLICKER_FRAMES:
		WORLD.hold_wind()
		await RenderingServer.frame_post_draw
		var img := WORLD.grab(self)
		grids.append(_luma_grid(img))
		if not save_dir.is_empty():
			img.save_png(save_dir.path_join("frame%08d.png" % i))
	return grids


## Returns {a: mean abs per-pixel luma step, b: largest step of the frame-mean luma, p99: 99th percentile of per-pixel abs step}.
func _flicker_metrics(grids: Array) -> Dictionary:
	var sum_a := 0.0
	var max_b := 0.0
	var diffs := PackedFloat32Array()
	var prev_mean := 0.0
	for k in grids.size():
		var g: PackedFloat32Array = grids[k]
		var mean := 0.0
		for v in g:
			mean += v
		mean /= maxf(float(g.size()), 1.0)
		if k > 0:
			var p: PackedFloat32Array = grids[k - 1]
			var acc := 0.0
			for i in g.size():
				var d := absf(g[i] - p[i])
				acc += d
				diffs.append(d)
			sum_a += acc / maxf(float(g.size()), 1.0)
			max_b = maxf(max_b, absf(mean - prev_mean))
		prev_mean = mean
	diffs.sort()
	var p99 := diffs[int(0.99 * float(diffs.size() - 1))] if not diffs.is_empty() else 0.0
	return {"a": sum_a / maxf(float(grids.size() - 1), 1.0), "b": max_b, "p99": p99}


func _check_flicker() -> void:
	if not await _go("hills_eye", "sun_8_set", {"yaw": 270.0, "pitch": 3.0}):
		return
	_set_aa_fxaa()
	var out_dir := String(args.get("flicker_frames", ""))
	if not await _fog(true, Vector3(0.0, 1.0, 0.0)):
		failures.append("flicker: the thin mist never turned on")
		return
	await WORLD.capture_begin(self, game)
	var on_grids: Array = await _flicker_arm(out_dir)
	WORLD.capture_end(game)   # the fog fade needs the clock
	if not await _fog(false):
		failures.append("flicker: the fog did not turn off")
	await WORLD.capture_begin(self, game)
	var off_grids: Array = await _flicker_arm(out_dir.path_join("off") if not out_dir.is_empty() else "")
	WORLD.capture_end(game)
	var on_m := _flicker_metrics(on_grids)
	var off_m := _flicker_metrics(off_grids)
	print("%s flicker fog on: mean abs luma step %.3f levels, largest frame-mean step %.3f levels, p99 pixel step %.1f levels" % [MARKER, on_m["a"], on_m["b"], on_m["p99"]])
	print("%s flicker fog off (baseline): mean abs luma step %.3f levels, largest frame-mean step %.3f levels, p99 pixel step %.1f levels" % [MARKER, off_m["a"], off_m["b"], off_m["p99"]])
	var changed := 0
	var total := 0
	var g_on: PackedFloat32Array = on_grids[on_grids.size() - 1]
	var g_off: PackedFloat32Array = off_grids[off_grids.size() - 1]
	for i in mini(g_on.size(), g_off.size()):
		total += 1
		if absf(g_on[i] - g_off[i]) > 0.5:
			changed += 1
	var frac := float(changed) / maxf(float(total), 1.0)
	print("%s flicker: %.1f %% of pixels differ in luma between fog on and fog off (last frames)" % [MARKER, frac * 100.0])
	if frac < 0.05:
		failures.append("flicker: only %.1f %% of pixels differ between fog on and off: the check saw no mist" % (frac * 100.0))
	if on_m["a"] > off_m["a"] + 1.0:
		failures.append("flicker: thin mist mean per-pixel step %.3f exceeds baseline %.3f + 1.0 levels" % [on_m["a"], off_m["a"]])
	if on_m["b"] > off_m["b"] + 0.5:
		failures.append("flicker: thin mist largest frame-mean step %.3f exceeds baseline %.3f + 0.5 levels" % [on_m["b"], off_m["b"]])


# ------------------------------------------------------------------ 7. pass A against Godot's own froxels (thin uniform fog, sky pixels Godot fogs itself)

func _effects_of(script_name: String) -> Array:
	var out := []
	var env := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if env == null or env.compositor == null:
		return out
	for fx in env.compositor.compositor_effects:
		var script := fx.get_script() as Script
		if script != null and script.resource_path.contains(script_name):
			out.append(fx)
	return out


func _set_effects(script_name: String, enabled: bool) -> void:
	for fx in _effects_of(script_name):
		(fx as CompositorEffect).enabled = enabled


func _arm_end() -> Dictionary:
	var arm := await _arm()
	if arm.is_empty() or arm["frames"].is_empty():
		return {}
	arm["end"] = (nf.get("EndEffect") as Object).call("get_last_end")
	return arm


## Pass A's (S rgb, T) at the 1/8 texel nearest to the middle of the frame's top band, averaged: returns Vector4(S, T).
func _end_mean(arm: Dictionary, rect: Array) -> Vector4:
	var size: Vector2i = arm["frames"]["end_size"]
	var end: PackedFloat32Array = arm["end"]
	var sum := Vector4.ZERO
	var n := 0
	for y in range(int(rect[1] * size.y), int(rect[3] * size.y)):
		for x in range(int(rect[0] * size.x), int(rect[2] * size.x)):
			var o := (y * size.x + x) * 4
			sum += Vector4(end[o], end[o + 1], end[o + 2], end[o + 3])
			n += 1
	return sum / maxf(float(n), 1.0)


func _check_passa() -> void:
	for time_name in ["noon", "sun_10_set"]:
		await _check_passa_at(time_name)


func _check_passa_at(time_name: String) -> void:
	print("%s passa time %s, A_up %s" % [MARKER, time_name, _rgb(_a_up())])
	if not await _go("hills_eye", time_name, {"yaw": 180.0, "pitch": 25.0}):
		return
	_set_aa_fxaa()
	_set_effects("RingFarBackdropComposite.gd", false)
	_set_effects("SunshineClouds2/SunshineClouds", false)
	var sun := _sun()
	for variant in ["sun off", "sun on"]:
		sun.visible = variant == "sun on"
		for sigma in [0.004, 0.03, 0.2]:
			if not await _fog(true, N2_DRIVE, 0.0):
				return
			await _frames(60)
			var u := await _arm_end()
			nf.set("DebugUniformSigma", sigma)
			await _frames(90)
			var f := await _arm_end()
			if u.is_empty() or f.is_empty():
				failures.append("passa: no readback")
				break
			var size: Vector2i = f["frames"]["size"]
			var ub: PackedByteArray = u["frames"]["pre"]
			var fb: PackedByteArray = f["frames"]["pre"]
			var s_godot := Vector3.ZERO
			var n := 0
			var t_pass := _end_mean(f, [0.3, 0.05, 0.7, 0.2]).w
			for y in range(int(0.05 * size.y), int(0.25 * size.y), 4):
				for x in range(int(0.3 * size.x), int(0.7 * size.x), 4):
					var cu := _px(ub, size.x, x, y)
					if _hl(cu) < 0.02:
						s_godot += _px(fb, size.x, x, y) - cu * t_pass
						n += 1
			var e := _end_mean(f, [0.3, 0.05, 0.7, 0.2])
			if n < 100:
				print("%s passa %s sigma %.3f: only %d black-sky pixels" % [MARKER, variant, sigma, n])
				continue
			s_godot /= float(n)
			print("%s passa %s %s sigma %.3f: A_up %s S godot %s, S pass %s, ratio godot/pass %s (T_end %.3f, n %d)" % [MARKER, time_name, variant, sigma, _rgb(_a_up()), _rgb(s_godot), _rgb(Vector3(e.x, e.y, e.z)), _rgb(Vector3(s_godot.x / e.x, s_godot.y / e.y, s_godot.z / e.z)), t_pass, n])
	sun.visible = true
	_set_effects("RingFarBackdropComposite.gd", true)
	_set_effects("SunshineClouds2/SunshineClouds", true)
	await _fog(false)


# ------------------------------------------------------------------ stills

func _still(name: String) -> void:
	var img := await _stable_grab()
	var path := stills_dir.path_join(name + ".png")
	img.save_png(path)
	print("%s still %s" % [MARKER, path])


func _check_stills() -> void:
	# (a) hills / valley at dawn, sun about 6 deg rising, D = (1, 0.5, 0), along the valley (two headings)
	for yaw in [270.0, 90.0]:
		if await _go("hills_eye", "sun_8_set", {"yaw": yaw, "pitch": 3.0}):
			_set_aa_fxaa()
			if await _set_time({"mode": "elevation", "value": 6.0, "rising": true}):
				await _fog(true, Vector3(1.0, 0.5, 0.0))
				await _frames(120)
				await WORLD.capture_begin(self, game)
				await _still("a_dawn_valley_yaw%d" % int(yaw))
				WORLD.capture_end(game)
				await _fog(false)
	# (b) forest_edge_eye, sun about 12 deg sunward, D = (0, 1, 0)
	if await _face_sun("forest_edge_eye", "sun_12_set", 3.0):
		_set_aa_fxaa()
		await _fog(true, Vector3(0.0, 1.0, 0.0))
		await _frames(120)
		await WORLD.capture_begin(self, game)
		await _still("b_forest_edge_sunward")
		WORLD.capture_end(game)
		await _fog(false)
	# (c) a river pose at dawn, D = (0, 0, 1): any fixture pose whose near-water field holds water
	# In a rivers world (--generation=1 --review-features=water.rivers) a river point from tools/InlandSwimProbe.cs FindPoints (as inland_water_render_gpu_test uses) is tried first:
	# the camera 4 m over the river's exact level, looking slightly down.
	var water_pose := ""
	var candidates := ["hills_eye", "plains_eye", "coast_eye", "forest_interior_eye", "forest_clearing_eye", "v0_centre_eye", "v0_wall_lo_eye", "v0_wall_hi_eye", "v0_ridge_eye"]
	if String(args.get("review-features", "")).contains("water.rivers"):
		var points: Dictionary = SWIM.new().call("FindPoints", game)
		print("%s river point: %s" % [MARKER, str(points.get("river", points.get("error", "none")))])
		if bool(points.get("ok", false)) and points.has("river"):
			var p: Dictionary = points["river"]
			var level := float(p["level"])
			extra_poses["river_level_eye"] = {"name": "river_level_eye", "around_m": p["around"], "across_m": p["across"], "lift_m": level + 4.0, "vertical_mode": "abs", "ground_m": level, "yaw_deg": 0.0, "pitch_deg": -5.0}
			candidates.push_front("river_level_eye")
	for name in candidates:
		var kept := failures.size()
		var heading := {} if extra_poses.has(name) else {"yaw": 0.0, "pitch": 3.0}
		if not await _go(name, "sun_8_set", heading):
			failures.resize(kept)   # a pose this world lacks is not a failure (and must not clear earlier ones)
			continue
		_set_aa_fxaa()
		await _fog(true, Vector3(0.0, 0.0, 1.0))
		await _frames(60)
		var texels := int(nf.call("DebugWaterTexelCount"))   # -1 while no field is built
		print("%s river scan: pose %s near-water texels %d" % [MARKER, name, texels])
		if texels > 20:
			water_pose = name
			print("%s river probe %s: centre mask, centre water height, camera height, lowest water %s" % [MARKER, name, str(nf.call("DebugWaterProbe"))])
			if await _set_time({"mode": "elevation", "value": 6.0, "rising": true}):
				await _frames(120)
				await WORLD.capture_begin(self, game)
				await _still("c_river_steam_" + name)
				WORLD.capture_end(game)
				await _fog(false)
				await _frames(60)
				await WORLD.capture_begin(self, game)
				await _still("c_river_nofog_" + name)
				WORLD.capture_end(game)
			await _fog(false)
			break
		await _fog(false)
	if water_pose.is_empty():
		print("%s river still: no fixture pose has inland water in the near-water field" % MARKER)
	# (d) clear dry noon, real drivers (amount 1, no forcing): byte-identical to amount 0
	if await _go("hills_eye", "noon", {"yaw": 0.0, "pitch": 5.0}):
		_set_aa_fxaa()
		await _fog(false)
		nf.set("DebugUniformSigma", -1.0)
		await _frames(120)
		# one capture (the clouds held) for both frames: amount 0, then amount 1 with the real drivers (clear and dry: no kind is driven, so the fog never activates)
		await WORLD.capture_begin(self, game)
		var img0 := await _stable_grab()
		nf.call("DebugSetAmount", 1.0)
		await _frames(150)
		var img1 := await _stable_grab()
		WORLD.capture_end(game)
		img0.save_png(stills_dir.path_join("d_clear_noon_amount0.png"))
		img1.save_png(stills_dir.path_join("d_clear_noon_amount1.png"))
		var differing := 0
		var da := img0.get_data()
		var db := img1.get_data()
		for i in range(0, da.size(), 4):
			if da[i] != db[i] or da[i + 1] != db[i + 1] or da[i + 2] != db[i + 2]:
				differing += 1
		print("%s clear noon amount 1 against amount 0 (one capture, real drivers): differing px %d; fog enabled=%s fade=%.3f drive %s rate %.6f" % [MARKER, differing, str(nf.get("FogEnabled")), float(nf.get("Fade")), str(nf.get("CurrentDrive")), float(nf.get("LastRate"))])
		if differing != 0:
			failures.append("stills (d): clear dry noon with amount 1 differs from amount 0 in %d px" % differing)
		nf.call("DebugSetAmount", 0.0)


func _run() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	var watchdog := create_timer(3000.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (3000 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	stills_dir = String(args.get("stills", ProjectSettings.globalize_path("res://.godot/audit_baseline/common/atmosphere/nf5")))
	DirAccess.make_dir_recursive_absolute(stills_dir)
	var booted: Dictionary = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		_fail("boot failed")
		return
	game = booted["game"]
	probe = PROBE.new()
	camera_default = root.get_camera_3d().transform
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	nf = game.get_node_or_null("NearFog")
	if nf == null:
		_fail("no NearFog node in the game scene")
		return
	WORLD.freeze_world(game)
	WORLD.hide_overlays(game)
	WORLD.quiet_scene(game)
	game.get_node("SunGlare").call("DebugSetStrength", 0.0)
	var only := String(args.get("only", OS.get_environment("NF5_ONLY")))
	if only.is_empty() or only == "ambient":
		await _check_ambient()
	if only.is_empty() or only == "clouds":
		await _check_clouds()
	if only.is_empty() or only == "height":
		await _check_height()
	if only.is_empty() or only == "shafts":
		await _check_shafts()
	if only.is_empty() or only == "night":
		await _check_night()
	if only.is_empty() or only == "cost":
		await _check_cost()
	if only.is_empty() or only == "flicker":
		await _check_flicker()
	if only == "passa":
		await _check_passa()
	if only.is_empty() or only == "stills":
		await _check_stills()
	if not failures.is_empty():
		_fail(" | ".join(failures.slice(0, 8)))
		return
	finished = true
	print("%s=PASS" % MARKER)
	_cleanup()
	quit(0)
