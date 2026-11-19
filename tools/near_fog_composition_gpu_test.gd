extends SceneTree

## NF-4 GPU test (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N5/N8): the fog-end pass (scripts/render/RingNearFogEndComposite.gd, materials/ring_near_fog_end.comp) fogs the pixels
## Godot's froxels cannot reach. Boots the pinned v8 fixture (tools/landscape_harness_common.gd) in still-capture mode (tools/tree_visibility_world.gd) with the wind held and FXAA. The checks
## read the HDR colour before and after the pass back from the render thread (RingNearFogEndComposite.request_state_readback) and use RingNearFog's test hooks (DebugUniformSigma, DebugMaskEmpty,
## DebugSkipEndApply, ForcedDrive). Every pass-against-oracle number is in the same HDR units.
##  1. calibration: uniform fog, far backdrop OFF so Godot fogs its own sky: the pass's T_end / S_end applied to the unfogged sky (sigma 0 frame) against Godot's fogged sky, per band (horizon
##     0-10 deg, 10-30 deg, zenith 30+) looking along the ring both ways at a 10 degree sun. The oracle is the pixels Godot actually fogs (see BLACK_SKY).
##  2. skyline: N2 fog (D = (1, 0.5, 0), layer height 150 m so the sky rays pass through it): the same comparison with the real density (terrain, basin, noise) on the sky above the skyline.
##  3. far boundary: uniform fog, backdrop ON: across the boundary between main terrain and what lies above it, the step with fog equals T_end times the step without.
##  4. cloud over distant terrain: pass B's correction against pre + d (S - (1 - T) C), d from Sunshine's depth-density matte, C from its accumulation image.
##  5. cost (root viewport GPU time, A-B-A), 6. identity: the pass is not attached and the frame is byte-identical (against the node removed) at amount 0 and after fading out.
## Also saves stills (forced N2 fog at hills_eye, sun 10 degrees, pass off and on) under .godot/audit_baseline/common/atmosphere/nf4/ (--stills=<dir>); NF4_ONLY=<check> runs one check.
## RUN (windowed Vulkan, no other Godot running): tools/run_tests.ps1 -Label nf4 -Tests near_fog_composition_gpu_test
## Prints NEAR_FOG_COMPOSITION=PASS (or =FAIL <reason>) and per-check tables. Test only; the harness fixture world is deleted at exit.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const MARKER := "NEAR_FOG_COMPOSITION"
const UNIFORM_SIGMA := 0.004
const N2_DRIVE := Vector3(1.0, 0.5, 0.0)
const NO_DRIVE := Vector3(-1.0, -1.0, -1.0)
const BANDS := [[-90.0, 10.0, "horizon"], [10.0, 30.0, "10-30"], [30.0, 90.0, "zenith"]]
## NF-5b: judged at 5 %. The engine mixes 0.3 isotropic + 0.7 anisotropic sky into the froxel ambient (the anisotropic sample is the sky radiance map in the view direction, with its mip depending on the
## density); the fog-end pass uses one mean sky (k x A_up) for both, so the ambient share of the in-scatter agrees to the anisotropic sky's directional variation, not exactly. The density and the
## fixed-point rounding are unbiased (stochastic rounding in the froxel shader). Was 3 % with the quantisation emulated.
const SKY_TOLERANCE := 0.05
## With the far backdrop composite off, the visible sky colour (the blue and the cloud look) is drawn by a pass that runs after Godot's fog and is never fogged (measured: fogged and
## unfogged pixels identical there); only the pixels that pass leaves black (unfogged HDR luma under this) receive Godot's sky fog, as c_fogged = in-scatter alone. Those pixels are the oracle.
const BLACK_SKY := 0.02
const STEP_TOLERANCE := 0.02
const CLOUD_TOLERANCE := 0.03

var game: Node
var nf: Node
var failures: Array[String] = []
var finished := false
var args := {}
var stills_dir := ""


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
		_set_far(true)
		_set_sunshine(true)
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


func _set_far(enabled: bool) -> void:
	for fx in _effects_of("RingFarBackdropComposite.gd"):
		(fx as CompositorEffect).enabled = enabled


func _set_sunshine(enabled: bool) -> void:
	var found := _effects_of("SunshineClouds2/SunshineClouds")
	for fx in found:
		(fx as CompositorEffect).enabled = enabled
	var names := []
	var env := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	for fx in env.compositor.compositor_effects:
		names.append("%s:%s" % [(fx.get_script() as Script).resource_path.get_file(), str(fx.enabled)])
	print("%s sunshine enabled=%s (%d found); compositor: %s" % [MARKER, str(enabled), found.size(), ", ".join(names)])


func _set_aa_fxaa() -> void:
	root.use_taa = false
	root.screen_space_aa = Viewport.SCREEN_SPACE_AA_FXAA
	root.msaa_3d = Viewport.MSAA_DISABLED


## Fog on (amount 1, the given drive, gate 1, optional uniform sigma) and waits for the fade to complete, or off (amount 0) and waits for the owner to stand down.
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


## One captured frame of the current state (still-capture mode: clocks held).
func _shot() -> Image:
	await WORLD.capture_begin(self, game)
	var img := await _stable_grab()
	WORLD.capture_end(game)
	return img


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
	var pose := HARNESS.pose_by_name(pose_name)
	var info: Dictionary = await HARNESS.go_pose(self, game, pose, time_name, null, heading)
	if info.has("error"):
		failures.append("pose %s: %s" % [pose_name, str(info["error"])])
		return false
	WORLD.freeze_world(game)
	await _frames(60)
	if not await _await_fields_here():
		failures.append("pose %s: the terrain fields never covered the camera" % pose_name)
		return false
	return true


# ------------------------------------------------------------------ image helpers

func _luma(c: Color) -> float:
	return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b


func _is_mask(c: Color) -> bool:
	return c.r - c.g > 0.25 and c.b - c.g > 0.25 and absf(c.r - c.b) < 0.15   # (0.8, 0, 0.8) HDR comes out pink (0.87, 0.48, 0.83) after tonemapping


## Main-empty pixel with its 7x7 neighbourhood (corners and axes at 3 px) also main-empty, so edge filtering and FXAA cannot reach it.
func _mask_core(mask: Image, x: int, y: int) -> bool:
	var w := mask.get_width()
	var h := mask.get_height()
	for o in [Vector2i(0, 0), Vector2i(3, 0), Vector2i(-3, 0), Vector2i(0, 3), Vector2i(0, -3), Vector2i(3, 3), Vector2i(-3, -3), Vector2i(3, -3), Vector2i(-3, 3)]:
		var px: int = x + o.x
		var py: int = y + o.y
		if px < 0 or py < 0 or px >= w or py >= h or not _is_mask(mask.get_pixel(px, py)):
			return false
	return true


func _elevation_deg(x: int, y: int) -> float:
	var cam := root.get_camera_3d()
	var ray := cam.project_ray_normal(Vector2(x + 0.5, y + 0.5))
	var up := (game.get_node("Explorer") as Node3D).global_basis.y.normalized()
	return rad_to_deg(asin(clampf(ray.dot(up), -1.0, 1.0)))


## Skyline samples: per column (step 8 px) the first non-mask row below a main-empty run that starts at the top. Returns [{x, y}].
func _skyline(mask: Image) -> Array:
	var out := []
	for x in range(8, mask.get_width() - 8, 8):
		if not _is_mask(mask.get_pixel(x, 2)):
			continue
		var y := 2
		while y < mask.get_height() - 12 and _is_mask(mask.get_pixel(x, y)):
			y += 1
		if y >= mask.get_height() - 12 or y < 14:
			continue
		var clear := true
		for k in range(1, 9):   # a clean terrain side: not mask for 8 rows below (no thin features or holes)
			if _is_mask(mask.get_pixel(x, y + k)):
				clear = false
				break
		if clear:
			out.append({"x": x, "y": y})
	return out


func _median(values: Array) -> float:
	if values.is_empty():
		return NAN
	var sorted := values.duplicate()
	sorted.sort()
	return sorted[sorted.size() / 2]


# ------------------------------------------------------------------ HDR readback (RingNearFogEndComposite.request_state_readback)

## One still-capture arm: optionally the LDR frame, and the HDR colour before and after the pass plus pass A's image, read back from the render thread.
func _arm(want_ldr := true) -> Dictionary:
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
	var end: PackedFloat32Array = eff.call("get_last_end")
	WORLD.capture_end(game)
	return {"img": img, "frames": frames, "end": end}


func _px(bytes: PackedByteArray, width: int, x: int, y: int) -> Vector3:
	var o := (y * width + x) * 8
	return Vector3(bytes.decode_half(o), bytes.decode_half(o + 2), bytes.decode_half(o + 4))


func _hl(c: Vector3) -> float:
	return 0.2126 * c.x + 0.7152 * c.y + 0.0722 * c.z


## Pass A's (S rgb, T) at full-resolution pixel (x, y): bilinear over the 1/8 image, texel centres at 8 i + 4, clamped (what pass B samples).
func _end_at(arm: Dictionary, x: int, y: int) -> Vector4:
	var size: Vector2i = arm["frames"]["end_size"]
	var end: PackedFloat32Array = arm["end"]
	var fx := (x + 0.5) / 8.0 - 0.5
	var fy := (y + 0.5) / 8.0 - 0.5
	var x0 := floori(fx)
	var y0 := floori(fy)
	var tx := fx - x0
	var ty := fy - y0
	var out := Vector4()
	for k in 4:
		var v := 0.0
		for c in [[0, 0, (1.0 - tx) * (1.0 - ty)], [1, 0, tx * (1.0 - ty)], [0, 1, (1.0 - tx) * ty], [1, 1, tx * ty]]:
			var xi := clampi(x0 + c[0], 0, size.x - 1)
			var yi := clampi(y0 + c[1], 0, size.y - 1)
			v += end[(yi * size.x + xi) * 4 + k] * c[2]
		out[k] = v
	return out


func _same_size(arm: Dictionary, img: Image) -> bool:
	var size: Vector2i = arm["frames"].get("size", Vector2i.ZERO)
	return size == Vector2i(img.get_width(), img.get_height())


# ------------------------------------------------------------------ checks

## Fog on with the given drive and uniform sigma (sigma -1 = the real density), backdrop OFF (Godot fogs its own sky), then the same view with sigma 0 (the unfogged HDR of the same base):
## returns {u, f, mask} arms; the caller applies the pass's T_end / S_end to u and compares with Godot's own fogged f.
func _godot_pair(drive: Vector3, sigma: float) -> Dictionary:
	if not await _fog(true, drive, 0.0):
		failures.append("the fog never turned on")
		return {}
	nf.set("DebugMaskEmpty", true)
	await _frames(5)
	var mask: Image = (await _arm())["img"]
	nf.set("DebugMaskEmpty", false)
	_set_far(false)
	_set_sunshine(false)   # Sunshine composites unfogged cloud over Godot's fogged sky; the calibration is of the fog integral alone
	await _frames(90)
	var u := await _arm(false)
	nf.set("DebugUniformSigma", sigma)
	await _frames(90)
	var f := await _arm(true)
	_set_far(true)
	_set_sunshine(true)
	if u.is_empty() or f.is_empty() or u["frames"].is_empty() or f["frames"].is_empty():
		failures.append("the pass produced no readback (not attached or not drawing)")
		return {}
	if not _same_size(f, mask):
		failures.append("internal size %s differs from the viewport image" % str(f["frames"].get("size")))
		return {}
	return {"u": u, "f": f, "mask": mask}


## Diagnostic: Godot's in-scatter S_G = c_fogged - T c_unfogged (T from the pass) against the pass's S, binned by the cosine between the view ray and the direction to the sun.
func _print_angular(label: String, mask: Image, u: Dictionary, f: Dictionary) -> void:
	var sun := (game.get_node("StarLight") as Node3D).global_basis.z.normalized()
	var cam := root.get_camera_3d()
	var w := mask.get_width()
	var bins := {}
	for y in range(0, mask.get_height(), 8):
		for x in range(0, w, 8):
			if not _mask_core(mask, x, y):
				continue
			var cos_t := cam.project_ray_normal(Vector2(x + 0.5, y + 0.5)).dot(sun)
			var bin := clampi(int((cos_t + 1.0) * 5.0), 0, 9)
			var te := _end_at(f, x, y)
			var sg := _hl(_px(f["frames"]["pre"], w, x, y)) - te.w * _hl(_px(u["frames"]["pre"], w, x, y))
			var sp := 0.2126 * te.x + 0.7152 * te.y + 0.0722 * te.z
			var a: Array = bins.get(bin, [0.0, 0.0, 0.0, 0])
			a[0] += sg; a[1] += sp; a[2] += cos_t; a[3] += 1
			bins[bin] = a
	var maps := []
	for k in 3:
		maps.append(Image.create(w, mask.get_height(), false, Image.FORMAT_RGB8))
	for y in range(0, mask.get_height(), 2):
		for x in range(0, w, 2):
			if not _mask_core(mask, x, y):
				continue
			var te := _end_at(f, x, y)
			var sg := _hl(_px(f["frames"]["pre"], w, x, y)) - te.w * _hl(_px(u["frames"]["pre"], w, x, y))
			var sp := 0.2126 * te.x + 0.7152 * te.y + 0.0722 * te.z
			var ratio := sg / maxf(sp, 1e-5)
			for dy in 2:
				for dx in 2:
					maps[0].set_pixel(x + dx, y + dy, Color(sg, sg, sg) * 0.8)
					maps[1].set_pixel(x + dx, y + dy, Color(sp, sp, sp) * 0.8)
					maps[2].set_pixel(x + dx, y + dy, Color(clampf(ratio / 2.0, 0.0, 1.0), 0.2, clampf(1.0 - ratio / 2.0, 0.0, 1.0)))
	maps[0].save_png(stills_dir.path_join("calibration_%s_S_godot.png" % label))
	maps[1].save_png(stills_dir.path_join("calibration_%s_S_pass.png" % label))
	maps[2].save_png(stills_dir.path_join("calibration_%s_S_ratio.png" % label))
	var keys := bins.keys()
	keys.sort()
	for k in keys:
		var a: Array = bins[k]
		var n: int = a[3]
		print("%s angular %s cos %+.2f n=%5d  S_godot %.4f  S_pass %.4f  ratio godot/pass %.3f" % [MARKER, label, a[2] / n, n, a[0] / n, a[1] / n, (a[0] / n) / maxf(a[1] / n, 1e-6)])
	var env := (game.get_node("WorldEnvironment") as WorldEnvironment).environment
	print("%s env: aniso %.2f length %.1f detail %.1f temporal %s %.2f gi %.2f ambient %.2f sky_affect %.2f density %.4f" % [MARKER, env.volumetric_fog_anisotropy, env.volumetric_fog_length,
		env.volumetric_fog_detail_spread, str(env.volumetric_fog_temporal_reprojection_enabled), env.volumetric_fog_temporal_reprojection_amount, env.volumetric_fog_gi_inject,
		env.volumetric_fog_ambient_inject, env.volumetric_fog_sky_affect, env.volumetric_fog_density])


func _check_calibration() -> void:
	print("%s calibration: uniform sigma %.4f /m (T_end about %.2f); the pass applied to Godot's own unfogged sky against Godot's fogged sky (backdrop OFF, HDR)" % [MARKER, UNIFORM_SIGMA, exp(-UNIFORM_SIGMA * 217.0)])
	var cases := [["yaw0", {"yaw": 0.0, "pitch": 20.0}], ["yaw180", {"yaw": 180.0, "pitch": 20.0}]]
	for c in cases:
		if not await _go("hills_eye", "sun_10_set", c[1]):
			return
		_set_aa_fxaa()
		var pair := await _godot_pair(Vector3(1, 0, 0), UNIFORM_SIGMA)
		if pair.is_empty():
			return
		var mask: Image = pair["mask"]
		var u: Dictionary = pair["u"]
		var f: Dictionary = pair["f"]
		var w := mask.get_width()
		var sums := {}
		for b in BANDS:
			sums[b[2]] = [0.0, 0.0, 0.0, 0.0, 0]   # predicted, godot, unfogged, T
		var errs := []
		for y in range(0, mask.get_height(), 6):
			for x in range(0, w, 6):
				if not _mask_core(mask, x, y):
					continue
				var e := _elevation_deg(x, y)
				var cu := _px(u["frames"]["pre"], w, x, y)
				if _hl(cu) >= BLACK_SKY:
					continue   # see the constant: only the pixels Godot's froxels fog
				var cf := _px(f["frames"]["pre"], w, x, y)
				var te := _end_at(f, x, y)
				var pred := cu * te.w + Vector3(te.x, te.y, te.z)
				for b in BANDS:
					if e >= b[0] and e < b[1]:
						var a: Array = sums[b[2]]
						a[0] += _hl(pred); a[1] += _hl(cf); a[2] += _hl(cu); a[3] += te.w; a[4] += 1
				errs.append(absf(_hl(pred) - _hl(cf)) / maxf(_hl(cf), 1e-4))
		_print_angular(c[0], mask, u, f)
		errs.sort()
		for b in BANDS:
			var a: Array = sums[b[2]]
			var n: int = a[4]
			if n < 200:
				print("%s calibration %s band %s: only %d sky samples, not judged" % [MARKER, c[0], b[2], n])
				continue
			var err: float = (a[0] - a[1]) / a[1]
			print("%s calibration %s band %-8s n=%5d  pass-applied %.4f  godot %.4f  rel err %+.2f %%   (unfogged %.4f, mean T_end %.3f, theory %.3f)" % [MARKER, c[0], b[2], n, a[0] / n, a[1] / n,
				err * 100.0, a[2] / n, a[3] / n, exp(-UNIFORM_SIGMA * 217.0)])
			if absf(err) > SKY_TOLERANCE:
				failures.append("calibration %s band %s: pass %.4f vs Godot %.4f = %+.2f %%" % [c[0], b[2], a[0] / n, a[1] / n, err * 100.0])
		print("%s calibration %s per-pixel |err| / godot luma: median %.2f %%, p90 %.2f %%" % [MARKER, c[0], errs[errs.size() / 2] * 100.0, errs[int(errs.size() * 0.9)] * 100.0])
		# Transmittance as Godot applies it: distant terrain just below the skyline (beyond the window) c_f = T c_u + S, with S = the pass's S (matched above).
		var tg := []
		for sl in _skyline(mask):
			var tx: int = sl["x"]
			var ty: int = sl["y"] + 8
			var tcu := _hl(_px(u["frames"]["pre"], w, tx, ty))
			if tcu < 0.03:
				continue
			var tte := _end_at(f, tx, ty)
			tg.append((_hl(_px(f["frames"]["pre"], w, tx, ty)) - (0.2126 * tte.x + 0.7152 * tte.y + 0.0722 * tte.z)) / tcu)
		tg.sort()
		if tg.size() >= 10:
			print("%s calibration %s transmittance of distant terrain under Godot's froxels: median %.3f (n=%d, p10 %.3f p90 %.3f); theory exp(-sigma Leff) = %.3f" % [MARKER, c[0], tg[tg.size() / 2], tg.size(), tg[tg.size() / 10],
				tg[int(tg.size() * 0.9)], exp(-UNIFORM_SIGMA * 220.0 * pow(1.0 - 0.5 / 80.0, 2.0))])
		await _fog(false)


func _check_skyline() -> void:
	await _check_skyline_inner()
	nf.call("DebugSetLayerHeight", 40.0)


func _check_skyline_inner() -> void:
	if not await _go("hills_eye", "sun_10_set", {"yaw": 270.0, "pitch": 6.0}):
		return
	_set_aa_fxaa()
	# Real N2 density (D = (1, 0.5, 0)), backdrop OFF: the pass applied to Godot's own unfogged sky against Godot's fogged sky, on the sky pixels 5 px above the skyline.
	nf.call("DebugSetLayerHeight", 150.0)   # the pose stands in a basin; a deep layer puts the sky rays through the fog (restored below)
	var pair := await _godot_pair(N2_DRIVE, -1.0)
	if pair.is_empty():
		nf.call("DebugSetLayerHeight", 40.0)
		return
	var mask: Image = pair["mask"]
	var u: Dictionary = pair["u"]
	var f: Dictionary = pair["f"]
	var w := mask.get_width()
	var line := _skyline(mask)
	print("%s skyline: %d columns" % [MARKER, line.size()])
	if line.size() < 20:
		failures.append("skyline: only %d usable columns" % line.size())
		return
	var errs := []
	var thick := 0
	for s in line:
		var x: int = s["x"]
		var y: int = s["y"] - 5
		while y > s["y"] - 80 and _hl(_px(u["frames"]["pre"], w, x, y)) >= BLACK_SKY:
			y -= 4   # the first pixel above the skyline that Godot fogs (see BLACK_SKY)
		var te := _end_at(f, x, y)
		var cu := _px(u["frames"]["pre"], w, x, y)
		if _hl(cu) >= BLACK_SKY:
			continue
		var cf := _px(f["frames"]["pre"], w, x, y)
		var terrain := _px(f["frames"]["pre"], w, x, s["y"] + 5)
		var pred := cu * te.w + Vector3(te.x, te.y, te.z)
		var local := 0.5 * (_hl(cf) + _hl(terrain))
		errs.append(absf(_hl(pred) - _hl(cf)) / maxf(local, 1e-4))
		if te.w < 0.97:
			thick += 1
	if errs.size() < 10:
		failures.append("skyline: only %d columns with a Godot-fogged sky pixel above the skyline" % errs.size())
		return
	errs.sort()
	var median: float = errs[errs.size() / 2]
	print("%s skyline N2 fog: sky-above-skyline |pass-applied - Godot| / local luma: median %.2f %%, p90 %.2f %% (%d of %d judged columns have T_end < 0.97)" % [MARKER, median * 100.0, errs[int(errs.size() * 0.9)] * 100.0, thick, errs.size()])
	if thick < errs.size() / 4:
		print("%s skyline: little fog along this skyline; the check is weak here" % MARKER)
	if median > STEP_TOLERANCE:
		failures.append("skyline: pass differs from Godot's own sky fog by %.2f %% of the local luma (median)" % (median * 100.0))
	# Stills for review: pass off (NF-2b alone) / pass on, backdrop on, forced N2 fog at the production layer height (40 m) and at 150 m, LDR.
	nf.call("DebugSetLayerHeight", 40.0)
	nf.set("DebugUniformSigma", -1.0)
	for height in [40, 150]:
		nf.call("DebugSetLayerHeight", float(height))
		await _frames(60)
		nf.set("EndPassEnabled", false)
		await _frames(30)
		var pass_off := await _shot()
		nf.set("EndPassEnabled", true)
		await _frames(30)
		var with_pass := await _shot()
		pass_off.save_png(stills_dir.path_join("nf4_forced_fog_hills_eye_sun10_H%d_pass_off.png" % height))
		with_pass.save_png(stills_dir.path_join("nf4_forced_fog_hills_eye_sun10_H%d_pass_on.png" % height))
	await _fog(false)


## Seam across the boundary between main terrain and what lies above it (far backdrop terrain or sky), uniform fog, backdrop ON, in HDR: the step with fog must equal T_end times the
## step without fog (both sides get the same transmittance and in-scatter beyond the window).
func _check_far_boundary() -> void:
	if not await _go("hills_300m", "sun_10_set", {"yaw": 270.0, "pitch": -2.0}):
		return
	_set_aa_fxaa()
	if not await _fog(true, Vector3(1, 0, 0), 0.0):
		failures.append("far boundary: the fog never turned on")
		return
	nf.set("DebugMaskEmpty", true)
	await _frames(5)
	var mask: Image = (await _arm())["img"]
	nf.set("DebugMaskEmpty", false)
	await _frames(90)
	var u := await _arm(false)
	nf.set("DebugUniformSigma", UNIFORM_SIGMA)
	await _frames(90)
	var f := await _arm(false)
	nf.set("DebugUniformSigma", -1.0)
	if u.is_empty() or f.is_empty() or u["frames"].is_empty() or f["frames"].is_empty() or not _same_size(f, mask):
		failures.append("far boundary: no readback")
		return
	var w := mask.get_width()
	var errs := []
	var seam_off := []
	var n := 0
	for x in range(8, w - 8, 8):
		# From the bottom: the first main-empty run above main terrain.
		var y := mask.get_height() - 12
		while y > 14 and not _is_mask(mask.get_pixel(x, y)):
			y -= 1
		if y <= 14 or not _mask_core(mask, x, y - 3):
			continue
		var above := Vector2i(x, y - 4)
		var below := Vector2i(x, y + 6)
		if _is_mask(mask.get_pixel(below.x, below.y)) or not _is_mask(mask.get_pixel(above.x, above.y)):
			continue
		var te := _end_at(f, x, y)
		var step_u := _hl(_px(u["frames"]["post"], w, above.x, above.y)) - _hl(_px(u["frames"]["post"], w, below.x, below.y))
		var post_a := _hl(_px(f["frames"]["post"], w, above.x, above.y))
		var post_b := _hl(_px(f["frames"]["post"], w, below.x, below.y))
		var pre_a := _hl(_px(f["frames"]["pre"], w, above.x, above.y))
		var local := 0.5 * (post_a + post_b)
		errs.append(absf((post_a - post_b) - te.w * step_u) / maxf(local, 1e-4))
		seam_off.append(absf((pre_a - post_b) - te.w * step_u) / maxf(local, 1e-4))
		n += 1
	if n < 20:
		failures.append("far boundary: only %d usable columns" % n)
		return
	errs.sort()
	seam_off.sort()
	var median: float = errs[errs.size() / 2]
	print("%s far boundary (main terrain to what lies above), %d columns: |step_fog - T_end step_nofog| / local luma median %.2f %%, p90 %.2f %%  (without the pass's fog on the upper side: median %.2f %%)" % [MARKER, n,
		median * 100.0, errs[int(errs.size() * 0.9)] * 100.0, seam_off[seam_off.size() / 2] * 100.0])
	if median > STEP_TOLERANCE:
		failures.append("far boundary: step differs from T_end x the unfogged step by %.2f %% of the local luma (median)" % (median * 100.0))
	await _fog(false)


## Cloud composited over distant terrain (backdrop ON, uniform fog): pass B's correction against the analytic result in HDR. With C the straight cloud colour and a the displayed cloud
## alpha, Sunshine leaves (1 - a) c_f + a C over Godot-fogged terrain c_f; fog applied after the cloud would give (1 - a) c_f + a (C T + S). a is recovered from the frames with and without
## Sunshine (a = (c_with - c_without) / (C - c_without)), C and the raw alpha from Sunshine's accumulation image.
func _check_cloud() -> void:
	await _check_cloud_inner()
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	if driver != null and driver.get("clouds_resource") != null:
		var clouds: Object = driver.get("clouds_resource")
		clouds.set("diagnostic_disable_temporal_history", false)
		clouds.set("diagnostic_depth_density_matte", false)


func _check_cloud_inner() -> void:
	var driver := game.get_node_or_null("SunshineCloudsDriver")
	var clouds: Object = driver.get("clouds_resource") as Object if driver != null else null
	if clouds == null:
		failures.append("cloud: no Sunshine resource")
		return
	clouds.set("diagnostic_temporal_readback", true)   # allocates the accumulation images with the copy usage get_image needs
	clouds.set("diagnostic_disable_temporal_history", true)   # the matte frame and the normal frame must see the same accumulation
	clouds.call("refresh_compute")
	var candidates := [["hills_300m", 0.0, -2.0], ["hills_300m", 180.0, 4.0]]
	var best := {}
	for cand in candidates:
		var result := await _cloud_candidate(clouds, cand)
		if result.is_empty():
			continue
		print("%s cloud candidate %s yaw %d pitch %d: %d usable pixels" % [MARKER, cand[0], int(cand[1]), int(cand[2]), result["errs"].size()])
		if best.is_empty() or result["errs"].size() > best["errs"].size():
			best = result
			best["name"] = "%s yaw %d pitch %d (sun 10 deg)" % [cand[0], int(cand[1]), int(cand[2])]
		if best["errs"].size() >= 400:
			break
	if best.is_empty() or best["errs"].size() < 100:
		failures.append("cloud: no pose with a cloud displayed over terrain beyond the window (best %d pixels)" % (best["errs"].size() if not best.is_empty() else 0))
		return
	var errs: Array = best["errs"]
	var raw_errs: Array = best["raw_errs"]
	errs.sort()
	raw_errs.sort()
	var median: float = errs[errs.size() / 2]
	print("%s cloud pose: %s" % [MARKER, best["name"]])
	print("%s cloud over distant terrain, %d pixels: |post - analytic| / analytic median %.2f %%, p90 %.2f %%  (without the correction: median %.2f %%, p90 %.2f %%)" % [MARKER, errs.size(),
		median * 100.0, errs[int(errs.size() * 0.9)] * 100.0, raw_errs[raw_errs.size() / 2] * 100.0, raw_errs[int(raw_errs.size() * 0.9)] * 100.0])
	if median > CLOUD_TOLERANCE:
		failures.append("cloud: correction differs from the analytic fog-after-cloud by %.2f %% (median)" % (median * 100.0))


## One candidate pose: the normal frame (HDR before and after the pass, the cloud image) and a frame with Sunshine's depth-tested density matte (HDR luma = the displayed cloud alpha d);
## returns {errs, raw_errs} over the main-geometry pixels where d >= 0.2. The expected result is pre + d (S - (1 - T) C): the cloud over Godot-fogged terrain, fogged as a whole (see the header).
func _cloud_candidate(clouds: Object, cand: Array) -> Dictionary:
	if not await _go(cand[0], "sun_10_set", {"yaw": cand[1], "pitch": cand[2]}):
		return {}
	_set_aa_fxaa()
	if not await _fog(true, Vector3(1, 0, 0), UNIFORM_SIGMA):
		failures.append("cloud: the fog never turned on")
		return {}
	nf.set("DebugMaskEmpty", true)
	await _frames(5)
	var mask: Image = (await _arm())["img"]
	nf.set("DebugMaskEmpty", false)
	await _frames(60)
	var normal := await _arm(false)
	var backdrop: Texture2D = clouds.call("get_cloud_backdrop_texture", 0)
	var cloud_img: Image = backdrop.get_image() if backdrop != null else null
	clouds.set("diagnostic_depth_density_matte", true)
	await _frames(60)
	var matte := await _arm(false)
	clouds.set("diagnostic_depth_density_matte", false)
	await _fog(false)
	if cloud_img == null or cloud_img.is_empty() or normal.is_empty() or matte.is_empty() or normal["frames"].is_empty() or matte["frames"].is_empty() or not _same_size(normal, mask):
		return {}
	var w := mask.get_width()
	var h := mask.get_height()
	var errs := []
	var raw_errs := []
	var n_geo := 0
	var n_cloud := 0
	var dens := []
	for y in range(0, h, 4):
		for x in range(0, w, 4):
			if _is_mask(mask.get_pixel(x, y)):
				continue
			n_geo += 1
			var d := _hl(_px(matte["frames"]["pre"], w, x, y))
			if d < 0.2:
				continue
			n_cloud += 1
			var cp := cloud_img.get_pixel(clampi(int((x + 0.5) / w * cloud_img.get_width()), 0, cloud_img.get_width() - 1), clampi(int((y + 0.5) / h * cloud_img.get_height()), 0, cloud_img.get_height() - 1))
			var cl := _hl(Vector3(cp.r, cp.g, cp.b))
			var te := _end_at(normal, x, y)
			var s_l := 0.2126 * te.x + 0.7152 * te.y + 0.0722 * te.z
			var pre1 := _hl(_px(normal["frames"]["pre"], w, x, y))
			var post1 := _hl(_px(normal["frames"]["post"], w, x, y))
			var truth := pre1 + d * (s_l - (1.0 - te.w) * cl)
			errs.append(absf(post1 - truth) / maxf(truth, 1e-3))
			raw_errs.append(absf(pre1 - truth) / maxf(truth, 1e-3))
			dens.append(d)
	dens.sort()
	print("%s cloud candidate cells: geometry %d, displayed cloud (matte >= 0.2) %d%s" % [MARKER, n_geo, n_cloud, (", median displayed alpha %.2f" % dens[dens.size() / 2]) if not dens.is_empty() else ""])
	return {"errs": errs, "raw_errs": raw_errs}


## Cost of the fog end on the root viewport (GPU timestamps of the viewport): A-B-A-B-A-B with N2 fog forced, the pass off (NF-2b alone), pass A only and both passes; medians per window.
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
	var variants := [["off", false, false], ["A only", true, true], ["A + B", true, false]]
	var samples := {}
	for v in variants:
		samples[v[0]] = []
	for round in 3:
		for v in variants:
			nf.set("EndPassEnabled", v[1])
			nf.set("DebugSkipEndApply", v[2])
			await _frames(90)
			var window := []
			for i in 240:
				WORLD.hold_wind()
				await RenderingServer.frame_post_draw
				window.append(RenderingServer.viewport_get_measured_render_time_gpu(rid))
			window.sort()
			samples[v[0]].append(window[window.size() / 2])
	RenderingServer.viewport_set_measure_render_time(rid, false)
	nf.set("EndPassEnabled", true)
	nf.set("DebugSkipEndApply", false)
	var med := {}
	for v in variants:
		var arr: Array = samples[v[0]]
		print("%s cost %s: window medians %s ms" % [MARKER, v[0], ", ".join(PackedStringArray(arr.map(func(x): return "%.3f" % x)))])
		var sorted := arr.duplicate()
		sorted.sort()
		med[v[0]] = sorted[sorted.size() / 2]
	print("%s cost (root viewport %dx%d, GPU): pass A %.3f ms, pass B %.3f ms, both %.3f ms over the pass off" % [MARKER, root.size.x, root.size.y, med["A only"] - med["off"], med["A + B"] - med["A only"], med["A + B"] - med["off"]])
	nf.call("DebugSetLayerHeight", 40.0)
	await _fog(false)


func _identity_case(label: String) -> void:
	var cap: Dictionary = await WORLD.capture_begin(self, game)
	var x := await _stable_grab()
	var parent := nf.get_parent()
	parent.remove_child(nf)
	await _frames(10)
	var r := await _stable_grab()
	parent.add_child(nf)
	WORLD.capture_end(game)
	var differing := 0
	var da := x.get_data()
	var db := r.get_data()
	for i in range(0, da.size(), 4):
		if da[i] != db[i] or da[i + 1] != db[i + 1] or da[i + 2] != db[i + 2]:
			differing += 1
	print("%s identity %s: differing px vs feature absent %d (capture det=%s) attached=%s" % [MARKER, label, differing, str(cap["deterministic"]), str(nf.get("EndAttached"))])
	if differing != 0 and bool(cap["deterministic"]):
		failures.append("identity %s: %d px differ from the feature absent" % [label, differing])


func _check_identity() -> void:
	if not await _go("hills_eye", "noon", {"yaw": 0.0, "pitch": 5.0}):
		return
	_set_aa_fxaa()
	await _fog(false)
	nf.set("DebugUniformSigma", -1.0)
	await _frames(120)
	if bool(nf.get("EndAttached")):
		failures.append("identity: the pass is attached at amount 0")
	await _identity_case("amount0")
	# On then off: the pass detaches and the frame is the feature absent again.
	if not await _fog(true, N2_DRIVE):
		failures.append("identity: the forced fog never turned on")
		return
	if not bool(nf.get("EndAttached")):
		failures.append("identity: the pass is not attached while the fog is on")
	if not await _fog(false):
		failures.append("identity: the fog never turned off")
		return
	await _frames(30)
	if bool(nf.get("EndAttached")):
		failures.append("identity: the pass is still attached after the fog turned off")
	await _identity_case("after_off")


func _run() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	var watchdog := create_timer(2400.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (2400 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	stills_dir = String(args.get("stills", ProjectSettings.globalize_path("res://.godot/audit_baseline/common/atmosphere/nf4")))
	DirAccess.make_dir_recursive_absolute(stills_dir)
	var booted: Dictionary = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		_fail("boot failed")
		return
	game = booted["game"]
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
	game.get_node("SunGlare").call("DebugSetStrength", 0.0)   # the lens glare is added after the fog end and would corrupt the mask near the sun
	if _effects_of("RingFarBackdropComposite.gd").is_empty():
		_fail("no far backdrop composite in the compositor")
		return
	var only := String(args.get("only", OS.get_environment("NF4_ONLY")))   # development: run one check
	if only.is_empty() or only == "calibration":
		await _check_calibration()
	if only.is_empty() or only == "skyline":
		await _check_skyline()
	if only.is_empty() or only == "far_boundary":
		await _check_far_boundary()
	if only.is_empty() or only == "cloud":
		await _check_cloud()
	if only.is_empty() or only == "cost":
		await _check_cost()
	if only.is_empty() or only == "identity":
		await _check_identity()
	if not failures.is_empty():
		_fail(" | ".join(failures.slice(0, 8)))
		return
	finished = true
	print("%s=PASS" % MARKER)
	_cleanup()
	quit(0)
