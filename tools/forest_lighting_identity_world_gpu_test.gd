extends SceneTree
## FL-4 (docs/forest-lighting-plan.md section 11): forest lighting is byte-identical when off, proven in the REAL world.
## On an isolated copy of slot 3 (real start menu, Continue, tools/tree_visibility_world.gd), at forest poses, at noon and at a low setting sun, two yaws each, still-capture mode:
##   A  = current shaders, forest lighting switched off (all three; translucency and crown occlusion default on since 2026-11-19)
##   B  = the pre-forest-lighting shader sources (commit 3ca696e, swapped into the cached Shader resources at run time), forest lighting off
##   A2 = current shaders again after the restore (determinism control: A must equal A2, else the case is "not deterministic" and not judged)
##   S  = current shaders, all three switches on (early look for FL-5, saved as PNG)
##   BS = old shaders with the switches on (positive control: the old shaders ignore the switches, so BS must equal A, while S differs from A where forest lighting acts;
##        this proves the swap really reached the pixels S changed)
## Pass: every deterministic case has B == A (byte for byte) and BS == A. Scratch copies of the old sources live in res://.godot/fl4_head_<pid>/ and are deleted at the end.
## Run (windowed Vulkan; never on the player's save; takes the GPU queue): tools/tree_visibility_run.ps1 -Test forest_lighting_identity -Label fl4 -Build
## Args (after --): --out=<dir> --stills=<dir> [--names=forest_interior_eye,forest_edge_eye,spawn_ground] [--times=noon,sunset] [--sun-elevation=8] [--settle=60]
## Prints FOREST_LIGHTING_IDENTITY_WORLD=PASS | FAIL <why> and a per-case table (identical, differing pixels, max channel difference).

const COMMON := preload("res://tools/shadow_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const BASE_COMMIT := "3ca696e"   # parent of the forest lighting commit 5457bb2
const SHADERS := [
	"imported_tree.gdshader", "imported_tree_impostor.gdshader", "terrain3d_ring_surface.gdshader", "terrain3d_ring_surface_ground.gdshader",
	"ring_terrain_proxy.gdshader", "ring_poi_architecture.gdshader", "ring_poi_architecture_double_sided.gdshader", "ring_grass_cards.gdshader"]
## Includes forest lighting changed (old = git show BASE_COMMIT) and the includes that pull one in without being changed (old = current text with the include rewritten).
const CHANGED_INCLUDES := ["ring_terrain_lighting.gdshaderinc", "ring_poi_architecture.gdshaderinc"]
const REWRITTEN_INCLUDES := ["ring_grass_card_common.gdshaderinc"]

var args := {}
var out_dir := ""
var stills_dir := ""
var scratch_res := ""
var scratch_abs := ""
var game: Node
var forest: Node
var swaps: Array = []   # {path, shader, cur, old}
var terrain_materials: Array = []
var settle_frames := 60
var rows: Array = []
var failures: Array = []
var report := {"cases": [], "events": []}


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	create_timer(3300.0, true, false, true).timeout.connect(func(): _finish("FAIL timeout"))
	call_deferred("_run")


func _log(text: String) -> void:
	var line := "FL4 %s" % text
	print(line)
	report["events"].append(line)


func _finish(verdict: String) -> void:
	WORLD.capture_end(game)
	_restore_all()
	_cleanup_scratch()
	if out_dir != "":
		var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
		if f != null:
			f.store_string(JSON.stringify(report, " "))
			f.close()
	if verdict.begins_with("PASS"):
		print("FOREST_LIGHTING_IDENTITY_WORLD=" + verdict)
		quit(0)
	else:
		push_error("FOREST_LIGHTING_IDENTITY_WORLD=" + verdict)
		quit(1)


func _frames(n: int) -> void:
	for i in n:
		WORLD.hold_wind()
		await process_frame


# ---------------------------------------------------------------- old sources
func _git_show(rel: String) -> String:
	var out: Array = []
	var project := ProjectSettings.globalize_path("res://").trim_suffix("/")
	var code := OS.execute("git", ["-C", project, "show", "%s:%s" % [BASE_COMMIT, rel]], out, true)
	if code != 0 or out.is_empty():
		return ""
	return String(out[0])


func _rewrite_includes(code: String, names: Array) -> String:
	for n in names:
		code = code.replace("#include \"res://materials/%s\"" % n, "#include \"%s/%s\"" % [scratch_res, n])
	return code


func _write_scratch(file_name: String, code: String) -> bool:
	var f := FileAccess.open(scratch_res.path_join(file_name), FileAccess.WRITE)
	if f == null:
		return false
	f.store_string(code)
	f.close()
	return true


func _prepare_old_sources() -> String:
	scratch_res = "res://.godot/fl4_head_%d" % OS.get_process_id()
	scratch_abs = ProjectSettings.globalize_path(scratch_res)
	DirAccess.make_dir_recursive_absolute(scratch_abs)
	var rewrite_names: Array = CHANGED_INCLUDES + REWRITTEN_INCLUDES
	for inc in CHANGED_INCLUDES:
		var old := _git_show("materials/" + inc)
		if old == "":
			return "git show failed for " + inc
		if not _write_scratch(inc, _rewrite_includes(old, rewrite_names)):
			return "cannot write scratch " + inc
	for inc in REWRITTEN_INCLUDES:
		var cur := FileAccess.get_file_as_string("res://materials/" + inc)
		if cur == "" or not _write_scratch(inc, _rewrite_includes(cur, rewrite_names)):
			return "cannot prepare include " + inc
	for sh in SHADERS:
		var path: String = "res://materials/" + sh
		var shader := load(path) as Shader
		if shader == null:
			return "cannot load " + path
		var cur_code := shader.code
		var old_code: String = _git_show("materials/" + sh)
		if old_code == "":
			old_code = cur_code   # not in the base commit: current text, includes rewritten
			_log("%s: not in %s, old = current text with includes rewritten" % [sh, BASE_COMMIT])
		old_code = _rewrite_includes(old_code, rewrite_names)
		swaps.append({"path": path, "shader": shader, "cur": cur_code, "old": old_code, "differs": old_code != cur_code})
		_log("%s: old source %d chars, current %d chars, differs=%s" % [sh, old_code.length(), cur_code.length(), str(old_code != cur_code)])
	return ""


func _find_terrain_materials() -> void:
	terrain_materials.clear()
	for n in game.find_children("*", "Terrain3D", true, false):
		var m: Object = n.get("material")
		if m != null:
			terrain_materials.append(m)
			var so: Object = m.get("shader_override")
			_log("Terrain3D %s material shader_override=%s enabled=%s" % [n.get_path(), str(so.resource_path) if so != null else "null", str(m.get("shader_override_enabled"))])


func _swap(to_old: bool, only := -1) -> void:
	for i in swaps.size():
		if only >= 0 and i != only:
			continue
		var s: Dictionary = swaps[i]
		(s["shader"] as Shader).code = s["old"] if to_old else s["cur"]
	# Terrain3D holds the Shader resource as shader_override; re-assign so it rebuilds even if the changed signal is not followed.
	for m in terrain_materials:
		var so: Object = m.get("shader_override")
		if so != null:
			m.set("shader_override", null)
			m.set("shader_override", so)
			m.set("shader_override_enabled", true)
	await _frames(settle_frames)


func _restore_all() -> void:
	for s in swaps:
		var sh := s["shader"] as Shader
		if sh != null and sh.code != s["cur"]:
			sh.code = s["cur"]
	for m in terrain_materials:
		if is_instance_valid(m):
			var so: Object = m.get("shader_override")
			if so != null:
				m.set("shader_override", null)
				m.set("shader_override", so)
				m.set("shader_override_enabled", true)


func _cleanup_scratch() -> void:
	if scratch_abs == "" or not DirAccess.dir_exists_absolute(scratch_abs):
		return
	for f in DirAccess.get_files_at(scratch_abs):
		DirAccess.remove_absolute(scratch_abs.path_join(f))
	DirAccess.remove_absolute(scratch_abs)


# ---------------------------------------------------------------- switches
## RenderingServer.global_shader_parameter_get returns null for a runtime-set global in this build, so the published state is judged from the node: the ForestLighting node
## publishes the switches every _Process (zeros while all three are off), so a few frames after the switch change they are applied.
func _switches_all_off() -> bool:
	return not bool(forest.get("TranslucencyEnabled")) and not bool(forest.get("CanopyShadeEnabled")) and not bool(forest.get("CrownOcclusionEnabled"))


func _switches(on: bool) -> bool:
	forest.set("TranslucencyEnabled", on)
	forest.set("CanopyShadeEnabled", on)
	forest.set("CrownOcclusionEnabled", on)
	if not on:
		await _frames(30)
		return _switches_all_off() and not bool(forest.get("RasterPublished"))
	for i in 900:
		await _frames(1)
		if bool(forest.get("RasterPublished")):
			await _frames(20)
			return true
	return false


# ---------------------------------------------------------------- capture
func _stable_grab() -> Image:
	await _frames(20)
	var prev: Image
	for i in 40:
		await RenderingServer.frame_post_draw
		var img := WORLD.grab(self)
		if prev != null and prev.get_data() == img.get_data():
			return img
		prev = img
	return prev


func _diff(a: Image, b: Image) -> Dictionary:
	var da := a.get_data()
	var db := b.get_data()
	if da.size() != db.size():
		return {"pixels": -1, "max_abs": -1}
	var pixels := 0
	var max_abs := 0
	var where: Array = []
	var w := a.get_width()
	for i in range(0, da.size(), 4):
		var d := maxi(maxi(absi(da[i] - db[i]), absi(da[i + 1] - db[i + 1])), maxi(absi(da[i + 2] - db[i + 2]), absi(da[i + 3] - db[i + 3])))
		if d > 0:
			pixels += 1
			max_abs = maxi(max_abs, d)
			if where.size() < 6:
				var p := i / 4
				where.append("(%d,%d) A=%d,%d,%d B=%d,%d,%d" % [p % w, p / w, da[i], da[i + 1], da[i + 2], db[i], db[i + 1], db[i + 2]])
	return {"pixels": pixels, "max_abs": max_abs, "where": where}


func _case(tag: String) -> void:
	var cap: Dictionary = await WORLD.capture_begin(self, game)
	var a: Image = await _stable_grab()
	await _swap(true)
	var b: Image = await _stable_grab()
	var on_ok: bool = await _switches(true)
	var bs: Image = await _stable_grab()
	await _swap(false)
	var s: Image = await _stable_grab()
	var off_ok: bool = await _switches(false)
	var a2: Image = await _stable_grab()
	if args.has("bisect") and int(_diff(a, b)["pixels"]) > 0:
		for i in swaps.size():
			if not bool(swaps[i]["differs"]):
				continue
			await _swap(true, i)
			var one: Image = await _stable_grab()
			await _swap(false, i)
			var d1 := _diff(a, one)
			_log("%s bisect %s alone old: px %d max %d %s" % [tag, String(swaps[i]["path"]).get_file(), int(d1["pixels"]), int(d1["max_abs"]), str(d1["where"])])
	if args.has("variants"):
		# Grass-only variants (--variant-dir=<abs dir> with grass_<name>.gdshader, --variants=V1,V2): only ring_grass_cards runs the variant, everything else is current. Compared against A and B.
		var gi := -1
		for i in swaps.size():
			if String(swaps[i]["path"]).ends_with("ring_grass_cards.gdshader"):
				gi = i
		for vn in String(args["variants"]).split(",", false):
			var vcode := FileAccess.get_file_as_string(String(args.get("variant-dir", "")).path_join("grass_%s.gdshader" % vn))
			if vcode == "" or gi < 0:
				_log("%s variant %s: cannot load" % [tag, vn])
				continue
			(swaps[gi]["shader"] as Shader).code = vcode
			await _frames(settle_frames)
			var vimg: Image = await _stable_grab()
			(swaps[gi]["shader"] as Shader).code = swaps[gi]["cur"]
			await _frames(settle_frames)
			var vd_a := _diff(a, vimg)
			var vd_b := _diff(b, vimg)
			_log("%s variant %s: vs A px %d max %d | vs B px %d max %d" % [tag, vn, int(vd_a["pixels"]), int(vd_a["max_abs"]), int(vd_b["pixels"]), int(vd_b["max_abs"])])
	WORLD.capture_end(game)
	var d_a2 := _diff(a, a2)
	var d_b := _diff(a, b)
	var d_bs := _diff(a, bs)
	var d_s := _diff(a, s)
	var det := int(d_a2["pixels"]) == 0 and bool(cap["deterministic"]) and on_ok and off_ok
	var b_same := int(d_b["pixels"]) == 0
	var bs_same := int(d_bs["pixels"]) == 0
	var entry := {"case": tag, "capture": cap, "switches_on_ok": on_ok, "switches_off_ok": off_ok, "deterministic": det, "A_vs_A2": d_a2, "A_vs_B": d_b, "A_vs_BS": d_bs, "A_vs_S": d_s}
	rows.append(entry)
	report["cases"].append(entry)
	a.save_png(stills_dir.path_join(tag + "_A_off.png"))
	s.save_png(stills_dir.path_join(tag + "_S_on.png"))
	if det and not (b_same and bs_same):
		failures.append("%s: B vs A %s, BS vs A %s" % [tag, str(d_b), str(d_bs)])
	if not det or not b_same or not bs_same:
		b.save_png(out_dir.path_join(tag + "_B.png"))
		bs.save_png(out_dir.path_join(tag + "_BS.png"))
		a.save_png(out_dir.path_join(tag + "_A.png"))
	if not b_same:
		_log("%s B vs A pixels %s" % [tag, str(d_b["where"])])
	_log("%s det=%s | B==A %s (px %d max %d) | BS==A %s (px %d max %d) | A2 px %d | S differs px %d max %d" % [tag, str(det), str(b_same), int(d_b["pixels"]), int(d_b["max_abs"]),
		str(bs_same), int(d_bs["pixels"]), int(d_bs["max_abs"]), int(d_a2["pixels"]), int(d_s["pixels"]), int(d_s["max_abs"])])


# ---------------------------------------------------------------- poses
## Yaw (degrees, in the pose's yaw convention) that faces the sun at the current time: probes the sign by teleporting.
func _sun_yaw(pose: Dictionary, yaw0: float) -> float:
	var light := game.get_node_or_null("StarLight") as DirectionalLight3D
	var cam := get_root().get_viewport().get_camera_3d()
	var explorer: Node3D = game.get_node("Explorer")
	if light == null or cam == null:
		return fposmod(yaw0 + 180.0, 360.0)
	var up := explorer.global_transform.basis.y.normalized()
	var to_sun := light.global_transform.basis.z.normalized()
	_log("sun elevation above explorer up: %.1f deg" % rad_to_deg(asin(clampf(to_sun.dot(up), -1.0, 1.0))))
	var sh := (to_sun - up * to_sun.dot(up)).normalized()
	var best_yaw := yaw0
	var best_dot := -2.0
	var f0 := cam.global_transform.basis.z * -1.0
	var f0h := (f0 - up * f0.dot(up)).normalized()
	var ang := rad_to_deg(f0h.signed_angle_to(sh, up))
	for sign_k in [1.0, -1.0]:
		var yaw := fposmod(yaw0 + sign_k * ang, 360.0)
		_teleport(pose, yaw)
		await _frames(4)
		var f := (cam.global_transform.basis.z * -1.0)
		var fh := (f - up * f.dot(up)).normalized()
		var dot := fh.dot(sh)
		if dot > best_dot:
			best_dot = dot
			best_yaw = yaw
	_log("sun-facing yaw %.1f (horizontal alignment %.3f)" % [best_yaw, best_dot])
	return best_yaw


func _teleport(pose: Dictionary, yaw: float) -> void:
	var explorer: Node = game.get_node("Explorer")
	var surface: Node = game.get_node("Terrain3DGameplay")
	var a := float(pose["around_m"])
	var z := float(pose["across_m"])
	explorer.call("TeleportToCanonicalMeters", a, float(surface.call("SampleHeight", a, z)) + 1.7, z, yaw, float(pose.get("pitch_deg", 0.0)), true)


func _set_time(spec: Dictionary) -> bool:
	var info: Dictionary = COMMON.apply_time(game, spec)
	if info.has("error"):
		_log("time error: %s" % str(info["error"]))
		return false
	WORLD.freeze_world(game)
	return true


func _apply_set(trans: bool, crown: bool, shade: bool) -> bool:
	forest.set("TranslucencyEnabled", trans)
	forest.set("CrownOcclusionEnabled", crown)
	forest.set("CanopyShadeEnabled", shade)
	if not (trans or crown or shade):
		await _frames(30)
		return _switches_all_off() and not bool(forest.get("RasterPublished"))
	if not shade:
		await _frames(40)
		return true
	for i in 900:
		await _frames(1)
		if bool(forest.get("RasterPublished")):
			await _frames(30)
			return true
	return false


func _floor_luma(img: Image) -> float:
	# Mean Rec.709 luminance (display values 0..1) of the lower middle of the frame: rows 62..92 %, columns 20..80 %.
	var w := img.get_width()
	var h := img.get_height()
	var sum := 0.0
	var n := 0
	for y in range(int(h * 0.62), int(h * 0.92), 2):
		for x in range(int(w * 0.2), int(w * 0.8), 2):
			var c := img.get_pixel(x, y)
			sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b
			n += 1
	return sum / maxf(float(n), 1.0)


## FL-5 (--stills-only): off / on(all three) stills per pose and time, plus translucency-only and shade-only at forest_interior_eye noon and low sun; prints raster CPU numbers and floor luminance.
func _stills_run() -> void:
	var poses: Dictionary = WORLD.load_poses()
	var names := String(args.get("names", "forest_interior_eye,forest_edge_eye,spawn_ground")).split(",", false)
	var times := String(args.get("times", "noon,sun_8_set,sun_12_low,night")).split(",", false)
	var specs := {"noon": {"mode": "noon"}, "sun_8_set": {"mode": "elevation", "value": 8.0, "rising": false},
		"sun_12_low": {"mode": "elevation", "value": 12.0, "rising": false}, "night": {"mode": "elevation", "value": -30.0, "rising": false}}
	var lumas: Array = []
	for name in names:
		if not poses.has(name):
			_log("pose %s missing" % name)
			continue
		var pose: Dictionary = poses[name]
		var label := "save_spot" if name == "spawn_ground" else String(name)
		var yaw0 := fposmod(float(pose["yaw_deg"]), 360.0)
		for t in times:
			if t == "night" and name != "forest_interior_eye":
				continue
			await WORLD.go_pose(self, game, pose, yaw0, true)
			if not _set_time(specs[t]):
				_log("%s %s: time not available" % [label, t])
				continue
			await _frames(settle_frames)
			var yaw := yaw0
			if t == "sun_8_set" or t == "sun_12_low":
				yaw = await _sun_yaw(pose, yaw0)
			_teleport(pose, yaw)
			_log("%s %s: sun elevation %.1f deg (cycle), yaw %.1f" % [label, t, float(game.get_node("RingDayNightCycle").get("SunElevationDegrees")), yaw])
			await _frames(settle_frames)
			await COMMON.wait_builds_idle(self, game, 60.0, 10)
			var sets: Array = [["off", false, false, false], ["all", true, true, true]]
			if name == "forest_interior_eye" and (t == "noon" or t == "sun_12_low"):
				sets = [["off", false, false, false], ["trans", true, false, false], ["shade", false, false, true], ["all", true, true, true]]
			if args.has("default-pair"):
				# The shipped default since 2026-11-19: translucency and crown occlusion on, canopy shade off.
				sets = [["off", false, false, false], ["default", true, true, false]]
			await WORLD.capture_begin(self, game)
			for st in sets:
				var ok: bool = await _apply_set(st[1], st[2], st[3])
				var img: Image = await _stable_grab()
				var tag := "%s_%s_%s" % [label, t, st[0]]
				img.save_png(stills_dir.path_join(tag + ".png"))
				var luma := _floor_luma(img)
				lumas.append([tag, luma])
				_log("STILL %s yaw %d ok=%s floor_luma %.5f | raster published=%s rebuilds=%d trees=%d copy_ms=%.3f build_ms=%.3f upload_ms=%.3f" % [tag, int(round(yaw)), str(ok), luma,
					str(forest.get("RasterPublished")), int(forest.get("RebuildCount")), int(forest.get("LastTreeCount")), float(forest.get("LastCopyMilliseconds")),
					float(forest.get("LastBuildMilliseconds")), float(forest.get("LastUploadMilliseconds"))])
			await _apply_set(false, false, false)
			WORLD.capture_end(game)
	for l in lumas:
		print("FL5 LUMA %s %.5f" % [l[0], l[1]])
	_finish("PASS stills written to " + stills_dir)


func _run() -> void:
	if not WORLD.profile_is_isolated():
		_finish("FAIL user:// is not an isolated test profile; refusing to touch the player's save")
		return
	out_dir = String(args.get("out", ProjectSettings.globalize_path("res://.godot/fl4_identity_world")))
	stills_dir = String(args.get("stills", ProjectSettings.globalize_path("res://.godot/audit_baseline/common/forest-lighting/fl4")))
	DirAccess.make_dir_recursive_absolute(out_dir)
	DirAccess.make_dir_recursive_absolute(stills_dir)
	settle_frames = int(args.get("settle", "60"))
	if RenderingServer.get_rendering_device() == null:
		_finish("FAIL requires Vulkan/Forward+ (run windowed)")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var err: Array = []
	game = await WORLD.boot_game(self, err)
	if game == null:
		_finish("FAIL " + ", ".join(err))
		return
	forest = game.get_node_or_null("ForestLighting")
	if forest == null:
		_finish("FAIL no ForestLighting node in the game scene")
		return
	report["machine"] = {"adapter": RenderingServer.get_video_adapter_name(), "args": args}
	WORLD.freeze_world(game)
	WORLD.hide_overlays(game)
	if args.has("stills-only"):
		await _frames(10)
		await _stills_run()
		return
	var prep := _prepare_old_sources()
	if prep != "":
		_finish("FAIL " + prep)
		return
	_find_terrain_materials()
	if terrain_materials.is_empty():
		_log("WARNING: no Terrain3D material found; the terrain swap is unproven")
	await _frames(10)
	# Translucency and crown occlusion default ON since 2026-11-19: switch all three off explicitly; A is the off picture.
	if not await _switches(false):
		_finish("FAIL the ForestLighting switches could not be switched off at start")
		return
	var poses: Dictionary = WORLD.load_poses()
	var names := String(args.get("names", "forest_interior_eye,forest_edge_eye,spawn_ground")).split(",", false)
	var times := String(args.get("times", "noon,sunset")).split(",", false)
	var sun_el := float(args.get("sun-elevation", "8"))
	var sunset := {"mode": "elevation", "value": sun_el, "rising": false}
	for name in names:
		if not poses.has(name):
			failures.append("pose %s missing" % name)
			continue
		var pose: Dictionary = poses[name]
		var yaw0 := fposmod(float(pose["yaw_deg"]), 360.0)
		var label := "save_spot" if name == "spawn_ground" else String(name)
		await WORLD.go_pose(self, game, pose, yaw0, true)
		if not _set_time(sunset):
			failures.append("%s: no sun elevation %.0f" % [label, sun_el])
			continue
		await _frames(settle_frames)
		var yaw_sun: float = await _sun_yaw(pose, yaw0)
		for yaw in [yaw0, yaw_sun]:
			var idle: bool = await WORLD.go_pose(self, game, pose, yaw, true)   # noon
			for t in times:
				if t == "sunset":
					if not _set_time(sunset):
						continue
					await _frames(settle_frames)
					await COMMON.wait_builds_idle(self, game, 60.0, 10)
				else:
					await _frames(settle_frames)
				await _case("%s_%s_yaw%d" % [label, t, int(round(yaw))])
	_restore_all()
	await _frames(30)
	# Table
	print("FL4 TABLE case | deterministic | B==A | A vs B px / max | BS==A | A vs BS px / max | A vs A2 px | S vs A px / max")
	var nondet := 0
	var judged := 0
	for r in rows:
		var db: Dictionary = r["A_vs_B"]
		var dbs: Dictionary = r["A_vs_BS"]
		var ds: Dictionary = r["A_vs_S"]
		if not bool(r["deterministic"]):
			nondet += 1
		else:
			judged += 1
		print("FL4 TABLE %s | det %s | B==A %s | %d / %d | BS==A %s | %d / %d | %d | %d / %d" % [r["case"], "yes" if bool(r["deterministic"]) else "NO",
			"yes" if int(db["pixels"]) == 0 else "no", int(db["pixels"]), int(db["max_abs"]), "yes" if int(dbs["pixels"]) == 0 else "no", int(dbs["pixels"]), int(dbs["max_abs"]),
			int(r["A_vs_A2"]["pixels"]), int(ds["pixels"]), int(ds["max_abs"])])
	_log("cases %d, judged (deterministic) %d, not deterministic %d, failures %d" % [rows.size(), judged, nondet, failures.size()])
	if rows.is_empty():
		_finish("FAIL nothing was compared")
	elif not failures.is_empty():
		_finish("FAIL " + " | ".join(failures.slice(0, 8)))
	elif judged * 2 < rows.size():
		_finish("FAIL %d of %d cases not deterministic; the verdict is meaningless" % [nondet, rows.size()])
	else:
		_finish("PASS %d cases judged byte-identical off vs pre-forest-lighting shaders (%d not deterministic)" % [judged, nondet])
