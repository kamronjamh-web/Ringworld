extends SceneTree
## OC-3 verify sweeps in motion (docs/occlusion-culling-plan.md section 7): with BiomeDecorations.TreeCullVerify on, no frame may contain a magenta pixel.
## TreeCullVerify (lane T, OC-2) makes anything the frustum filter culled but the real frustum still shows draw magenta, so the count must be 0 in every
## frame of every sweep. On an isolated copy of slot 3 (real start menu, Continue), clocks held, HUD hidden, TreeFrustumCulling on, it runs, one frame at a time
## (run with --fixed-fps 30 so a frame is 33 ms of simulation whatever the readback costs; the runner adds it):
##   yaw360       360 deg/s yaw sweep in the forest interior            pitch      pitch sweep up and down at 180 deg/s
##   walk         walk through the forest (W held, ground controller)    fly72, fly240   flights near the ground at 72 and 240 m/s
##   fly1200, fly36800   flights from the save pose at 1.2 and 36.8 km/s  teleport  jumps between poses, every frame counted after each
##   rebase       a 1.2 km/s ground-level flight long enough to cross floating-origin rebases (distance flown is reported; it is not asserted)
## A rest frame (forest interior, culling and verify on) is counted first and must be 0; then a canary proves the key can fire.
## Magenta = R >= --rb, B >= --rb and both at least --gdiff above G (defaults 232 / 35). The verify material is unshaded albedo (4,0,4); tonemapping and fog bring it to a light pink of about (240-255, 190-205, 240-255) in the canary frame (measured 2026-11-16), so the key is R and B above 232 and both at least 35 above G; white and sky pixels have R = G = B, the forest has natural (182,80,180) pixels below the R/B floor, and the rest frame and the cull-off walk check it from the other side.
## SKIPs cleanly (TREE_CULL_VERIFY_SWEEP=SKIP ...) while the TreeCullVerify property does not exist (OC-2 not landed).
## Run (windowed Vulkan, not --headless; never on the player's save; takes the GPU queue):
##   tools/tree_visibility_run.ps1 -Test cull_verify -Build      (or via tools/run_tests.ps1 -Tests tree_cull_verify_sweep)
## Args (after --): --out=<dir> [--sweeps=yaw360,pitch,walk,walk_cull_off,fly72,fly240,fly1200,fly36800,teleport,rebase] [--secs=6] [--rb=232] [--gdiff=35] [--keep-frames=3]
## Frames with magenta are saved (first --keep-frames per sweep) under --out. Prints TREE_CULL_VERIFY_SWEEP=PASS | SKIP <why> | FAIL <why>; report.json has the per-sweep frames, max and total.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const VERIFY := "TreeCullVerify"
const CULL := "TreeFrustumCulling"
const FRAME_DT := 1.0 / 30.0   # one frame of simulation under --fixed-fps 30 (the runner passes it); sweep lengths and rates are in this time

var args := {}
var out_dir := ""
var game: Node
var trees: Node
var explorer: Node
var surface: Node
var report := {"sweeps": {}, "events": []}
var failures: Array = []
var rb := 180
var gdiff := 35
var keep_frames := 3
var held := {}


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	create_timer(3300.0).timeout.connect(func(): _finish("FAIL timeout"))
	call_deferred("_run")


func _log(text: String) -> void:
	var line := "TVH %s" % text
	print(line)
	report["events"].append(line)


func _finish(verdict: String) -> void:
	_release_keys()
	if out_dir != "":
		var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
		if f != null:
			f.store_string(JSON.stringify(report, " "))
			f.close()
	if verdict.begins_with("PASS") or verdict.begins_with("SKIP"):
		print("TREE_CULL_VERIFY_SWEEP=" + verdict)
		quit(0)
	else:
		push_error("TREE_CULL_VERIFY_SWEEP=" + verdict)
		quit(1)


func _run() -> void:
	if not WORLD.profile_is_isolated():
		_finish("FAIL user:// is not an isolated test profile; refusing to touch the player's save")
		return
	out_dir = String(args.get("out", ProjectSettings.globalize_path("res://.godot/tree_cull_verify")))
	DirAccess.make_dir_recursive_absolute(out_dir)
	rb = int(args.get("rb", "232"))
	gdiff = int(args.get("gdiff", "35"))
	keep_frames = int(args.get("keep-frames", "3"))
	if RenderingServer.get_rendering_device() == null:
		_finish("FAIL requires Vulkan/Forward+ (run windowed)")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var err: Array = []
	game = await WORLD.boot_game(self, err, {VERIFY: true, CULL: true})
	if game == null:
		_finish("FAIL " + ", ".join(err))
		return
	trees = game.get_node_or_null("BiomeDecorations")
	if not WORLD.has_property(trees, VERIFY):
		_finish("SKIP property %s does not exist on BiomeDecorations (OC-2 not landed)" % VERIFY)
		return
	explorer = game.get_node("Explorer")
	surface = game.get_node("Terrain3DGameplay")
	# TreeCullVerify was set at boot (WORLD.boot_game), before the layer made its first outputs; set again here in case the layer was found late.
	trees.set(CULL, true)
	trees.set(VERIFY, true)
	report["machine"] = {"adapter": RenderingServer.get_video_adapter_name(), "args": args, "fixed_fps": Engine.max_fps}
	report["save_pose"] = COMMON.pose_now(game)
	WORLD.freeze_world(game)
	report["viewport"] = WORLD.freeze_noise(self, game, String(args.get("freeze", "")).split(",", false))
	_log("viewport %s" % str(report["viewport"]))
	WORLD.hide_overlays(game)
	var poses: Dictionary = WORLD.load_poses()
	var secs := float(args.get("secs", "6"))
	var wanted := String(args.get("sweeps", "yaw360,pitch,walk,walk_cull_off,fly72,fly240,fly1200,fly36800,teleport,rebase")).split(",", false)
	# Outputs made before TreeCullVerify was set have no verify renderer: retire them (GPU tree path off while the world streams, then on) and let new ones be built.
	trees.set("UseGpuCulling", false)
	await WORLD.go_pose(self, game, poses["forest_interior_eye"], 0.0, true)
	await _frames(60)
	trees.set("UseGpuCulling", true)
	await _frames(60)
	await WORLD.go_pose(self, game, poses["forest_edge_eye"], 0.0, true)
	var rebuilt := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < rebuilt and not bool(trees.get("GpuTreeCullingSettled")):
		await process_frame
	_log("GPU tree path rebuilt with TreeCullVerify on; settled=%s verify renderers=%d" % [str(trees.get("GpuTreeCullingSettled")), _verify_renderers()])
	# Rest check: forest interior at rest with culling and verify on must already count 0. (A culling-off "detector floor" is not usable: with the filter off the
	# verify list keeps its last rows and draws them, so the pink stays. TreeCullVerify is never switched at run time because outputs created while it was
	# off would have no verify renderer.)
	await WORLD.go_pose(self, game, poses["forest_interior_eye"], 0.0, true)
	WORLD.freeze_world(game)
	await _frames(30)
	var floor_img := WORLD.grab(self)
	var floor_count := _magenta(floor_img)
	if floor_count > 0:
		floor_img.save_png(out_dir.path_join("floor.png"))
		report["floor_pixels"] = _magenta_positions(floor_img, 20)
	report["rest_count"] = floor_count
	_log("rest check (culling and verify on): %d magenta pixels" % floor_count)
	if floor_count != 0:
		_finish("FAIL rest frame already has %d magenta pixels (a visible row was culled, or the key is too loose: see floor.png)" % floor_count)
		return
	await _frames(30)
	var verify_nodes := _verify_renderers()
	report["verify_renderers"] = verify_nodes
	_log("verify renderers: %d" % verify_nodes)
	if verify_nodes == 0:
		_finish("FAIL no *_CullVerify renderers exist (the outputs were created before TreeCullVerify was set)")
		return
	_log("cull state: active=%s relatches=%s last=%s" % [str(trees.get("TreeCullActive")), str(trees.get("TreeCullRelatches")), str(trees.get("TreeCullLastRelatchReason"))])
	trees.call("StartTreeCameraAudit")
	var audit_deadline := Time.get_ticks_msec() + 30000
	await _frames(5)
	while String(trees.get("TreeCullAuditResult")) == "" and Time.get_ticks_msec() < audit_deadline:
		await process_frame
	report["camera_audit"] = String(trees.get("TreeCullAuditResult"))
	_log("camera list audit: %s" % report["camera_audit"])
	if args.has("widen-verify-aabb"):
		_widen_verify_aabbs()
	var canary := await _canary()
	report["canary"] = canary
	_log("canary (camera turned after the cull, before the draw): %s" % str(canary))
	if int(canary.get("max", 0)) <= 0:
		_finish("FAIL canary: verify mode never showed magenta when the camera was turned past the culled rows; a zero count would mean nothing")
		return
	for sweep in wanted:
		match sweep:
			"yaw360": await _sweep_view("yaw360", poses["forest_interior_eye"], secs, 360.0, 0.0)
			"pitch": await _sweep_view("pitch", poses["forest_interior_eye"], secs, 0.0, 180.0)
			"walk": await _sweep_walk("walk", poses["forest_edge_eye"], secs * 2.0)
			"walk_cull_off":
				# Key check from the other side: with the filter off nothing is dropped, so the count must be 0 (verify rows are not drawn then).
				trees.set(CULL, false)
				await _frames(60)
				await _sweep_walk("walk_cull_off", poses["forest_edge_eye"], secs * 2.0)
				trees.set(CULL, true)
				await _frames(60)
			"fly72": await _sweep_fly("fly72", poses["plains_eye"], 10.0, 1, 72.0, secs * 2.0)
			"fly240": await _sweep_fly("fly240", poses["plains_eye"], 10.0, 2, 240.0, secs * 2.0)
			"fly1200": await _sweep_fly("fly1200", {}, 0.0, 3, 1200.0, secs)
			"fly36800": await _sweep_fly("fly36800", {}, 0.0, 3, 36800.0, secs)
			"teleport": await _sweep_teleport("teleport", poses)
			"rebase": await _sweep_fly("rebase", poses["plains_eye"], 10.0, 3, 1200.0, secs * 2.0)
			_: _log("unknown sweep %s" % sweep)
		if args.has("stop-on-fail") and not failures.is_empty():
			break
	trees.set(VERIFY, false)
	if failures.is_empty():
		_finish("PASS %d sweeps, 0 magenta pixels in every frame" % wanted.size())
	else:
		_finish("FAIL " + " | ".join(failures.slice(0, 12)))


# ------------------------------------------------------------------ counting

func _verify_renderers() -> int:
	var n := 0
	var cameras := 0
	var sample: Array = []
	var stack: Array = [game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		var nm := String(node.name)
		if nm.ends_with("_CullVerify"):
			n += 1
			if n <= 4:
				var vm := node as MultiMeshInstance3D
				var cm := (node.get_parent().get_node_or_null(nm.replace("_CullVerify", "_Camera")) as MultiMeshInstance3D)
				_log("aabb verify=%s camera=%s extra_margin verify=%s camera=%s" % [str(vm.multimesh.custom_aabb), str(cm.multimesh.custom_aabb) if cm != null else "?", str(vm.extra_cull_margin), str(cm.extra_cull_margin) if cm != null else "?"])
				_log("verify %s: in_tree_visible=%s instance_count=%d visible_instance_count=%d | camera: visible=%s count=%d visible_count=%d" % [nm, str(vm.is_visible_in_tree()), vm.multimesh.instance_count, vm.multimesh.visible_instance_count,
					str(cm.is_visible_in_tree()) if cm != null else "?", cm.multimesh.instance_count if cm != null else -1, cm.multimesh.visible_instance_count if cm != null else -1])
		elif nm.ends_with("_Camera") and node is MultiMeshInstance3D:
			cameras += 1
			if sample.size() < 3:
				sample.append(str(game.get_path_to(node)))
				var sib: Array = []
				for c in node.get_parent().get_children():
					sib.append(String(c.name))
				_log("renderer %s children: %s" % [String(node.get_parent().name), str(sib)])
				var mat: Material = (node as MultiMeshInstance3D).material_override
				var code := ((mat as ShaderMaterial).shader.code if mat is ShaderMaterial and (mat as ShaderMaterial).shader != null else "")
				_log("camera renderer material: %s shader code %d chars, render_mode at %d, fragment at %d" % [mat.get_class() if mat != null else "null", code.length(), code.find("render_mode "), code.find("void fragment()")])
		stack.append_array(node.get_children())
	_log("TreeCullVerify=%s TreeFrustumCulling=%s camera renderers=%d verify renderers=%d sample=%s" % [str(trees.get(VERIFY)), str(trees.get(CULL)), cameras, n, str(sample)])
	return n


## Proves the key can fire: with the cull settled, the camera is turned in frame_pre_draw (after the cull ran for the old view), so rows the filter dropped fall into
## the drawn view and must show magenta. The camera is put back right after the draw. Returns {yaw90, yaw180, max}; the yaw180 frame is saved as canary.png.
func _canary() -> Dictionary:
	var cam := root.get_camera_3d()
	var result := {"max": 0}
	for deg in [90.0, 180.0, -90.0]:
		await _frames(40)
		var before := WORLD.grab(self).get_data()
		var saved := cam.global_transform
		var turned := Transform3D(saved.basis * Basis(Vector3.UP, deg_to_rad(deg)), saved.origin)
		RenderingServer.frame_pre_draw.connect(func():
			cam.global_transform = turned
			cam.force_update_transform(), CONNECT_ONE_SHOT)
		await RenderingServer.frame_post_draw
		var img := WORLD.grab(self)
		cam.global_transform = saved
		cam.force_update_transform()
		var n := _magenta(img)
		result["yaw%d" % int(deg)] = n
		var after := img.get_data()
		var changed := 0
		for k in range(0, before.size(), 4 * 97):
			if before[k] != after[k] or before[k + 1] != after[k + 1]:
				changed += 1
		result["view_changed_%d" % int(deg)] = float(changed) / float(before.size() / (4 * 97))
		result["max"] = maxi(int(result["max"]), n)
		if n > 0 and not result.has("sample"):
			result["sample"] = _magenta_positions(img, 3)
		if deg == 180.0:
			img.save_png(out_dir.path_join("canary.png"))
	return result


## Magenta pixels in an RGBA8 frame.
func _magenta(img: Image) -> int:
	var px := img.get_data().to_int32_array()
	var n := 0
	for i in px.size():
		var v := px[i]
		if (v & 0xFF) >= rb and ((v >> 16) & 0xFF) >= rb and (v & 0xFF) - ((v >> 8) & 0xFF) >= gdiff and ((v >> 16) & 0xFF) - ((v >> 8) & 0xFF) >= gdiff:
			n += 1
	return n


## Diagnostic (--widen-verify-aabb): gives every verify multimesh a huge custom AABB, to tell a verify list that is never drawn from one that Godot culls by bounds.
func _widen_verify_aabbs() -> void:
	var stack: Array = [game]
	var n := 0
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if String(node.name).ends_with("_CullVerify") and node is MultiMeshInstance3D:
			(node as MultiMeshInstance3D).multimesh.custom_aabb = AABB(Vector3(-1.0e6, -1.0e6, -1.0e6), Vector3(2.0e6, 2.0e6, 2.0e6))
			n += 1
		stack.append_array(node.get_children())
	_log("widened %d verify AABBs" % n)


func _magenta_positions(img: Image, limit: int) -> Array:
	var w := img.get_width()
	var px := img.get_data().to_int32_array()
	var out: Array = []
	for i in px.size():
		var v := px[i]
		if (v & 0xFF) >= rb and ((v >> 16) & 0xFF) >= rb and (v & 0xFF) - ((v >> 8) & 0xFF) >= gdiff and ((v >> 16) & 0xFF) - ((v >> 8) & 0xFF) >= gdiff:
			out.append([i % w, i / w, v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF])
			if out.size() >= limit:
				break
	return out


func _frames(count: int) -> void:
	for i in count:
		WORLD.hold_wind()
		await process_frame


## One frame: wait for the draw, count. Records into the sweep entry.
func _count(entry: Dictionary, frame: int) -> void:
	WORLD.hold_wind()
	await RenderingServer.frame_post_draw
	var img := WORLD.grab(self)
	var n := _magenta(img)
	entry["frames"] += 1
	entry["total"] += n
	if n > entry["max"]:
		entry["max"] = n
	if n > 0:
		entry["bad_frames"] += 1
		if entry["saved"] < keep_frames:
			entry["saved"] += 1
			img.save_png(out_dir.path_join("%s_frame%04d.png" % [entry["name"], frame]))


func _begin(name: String) -> Dictionary:
	var entry := {"name": name, "frames": 0, "total": 0, "max": 0, "bad_frames": 0, "saved": 0}
	report["sweeps"][name] = entry
	_log("sweep %s start" % name)
	return entry


func _end(entry: Dictionary) -> void:
	_release_keys()
	_log("sweep %s: %d frames, %d frames with magenta, max %d, total %d" % [entry["name"], entry["frames"], entry["bad_frames"], entry["max"], entry["total"]])
	if entry["bad_frames"] > 0:
		failures.append("%s: %d of %d frames have magenta (max %d px)" % [entry["name"], entry["bad_frames"], entry["frames"], entry["max"]])


# ------------------------------------------------------------------ sweeps

func _sweep_view(name: String, pose: Dictionary, secs: float, yaw_rate: float, pitch_rate: float) -> void:
	await WORLD.go_pose(self, game, pose, float(pose["yaw_deg"]), true)
	WORLD.freeze_world(game)
	var entry := _begin(name)
	var yaw := float(pose["yaw_deg"])
	var pitch := 0.0
	var dir := 1.0
	var t := 0.0
	var frame := 0
	while t < secs:
		var dt := FRAME_DT
		yaw += yaw_rate * dt
		if pitch_rate != 0.0:
			pitch += dir * pitch_rate * dt
			if absf(pitch) > 80.0:
				dir = -dir
				pitch = clampf(pitch, -80.0, 80.0)
		explorer.call("SetPresentationView", fposmod(yaw, 360.0), pitch)
		await process_frame
		await _count(entry, frame)
		t += dt
		frame += 1
	_end(entry)


func _sweep_walk(name: String, pose: Dictionary, secs: float) -> void:
	var a := float(pose["around_m"])
	var z := float(pose["across_m"])
	var ground := float(surface.call("SampleHeight", a, z))
	explorer.call("TeleportToCanonicalMeters", a, ground + 1.1, z, float(pose["yaw_deg"]), -5.0, false)
	await COMMON.settle(self, 0.3)
	await COMMON.wait_builds_idle(self, game, 120.0)
	var deadline := Time.get_ticks_msec() + 20000
	while not bool(explorer.call("is_on_floor")) and Time.get_ticks_msec() < deadline:
		await physics_frame
	WORLD.freeze_world(game)
	var entry := _begin(name)
	var start: Dictionary = COMMON.pose_now(game)
	_key(KEY_W, true)
	await _run_frames(entry, secs)
	_key(KEY_W, false)
	entry["distance_m"] = _dist(start, COMMON.pose_now(game))
	_end(entry)


## Flight: an optional ground pose (else the save pose at the save altitude), the speed tier, then the wheel to the exact speed.
func _sweep_fly(name: String, pose: Dictionary, agl: float, tier: int, speed: float, secs: float) -> void:
	if not pose.is_empty():
		var a := float(pose["around_m"])
		var z := float(pose["across_m"])
		explorer.call("TeleportToCanonicalMeters", a, float(surface.call("SampleHeight", a, z)) + agl, z, float(pose["yaw_deg"]), -2.0, true)
	else:
		var save: Dictionary = report["save_pose"]
		explorer.call("TeleportToCanonicalMeters", float(save["around"]), float(save["vertical"]), float(save["across"]), float(save["yaw"]), 0.0, true)
	await COMMON.settle(self, 0.5)
	await COMMON.wait_builds_idle(self, game, 120.0)
	explorer.call("SelectNoclipSpeedTier", tier)
	await _frames(5)
	_wheel_to_speed(speed)
	WORLD.freeze_world(game)
	_key(KEY_W, true)
	await _frames(30)   # the speed ramps to the target
	var entry := _begin(name)
	entry["target_speed"] = speed
	entry["speed_at_start"] = float(explorer.get("NoclipTargetSpeedMetersPerSecond"))
	var start: Dictionary = COMMON.pose_now(game)
	await _run_frames(entry, secs)
	_key(KEY_W, false)
	entry["distance_m"] = _dist(start, COMMON.pose_now(game))
	_end(entry)


func _sweep_teleport(name: String, poses: Dictionary) -> void:
	var entry := _begin(name)
	var order := ["forest_interior_eye", "plains_eye", "forest_edge_eye", "forest_interior_eye", "spawn_ground"]
	var frame := 0
	for pname in order:
		var pose: Dictionary = poses[pname]
		var a := float(pose["around_m"])
		var z := float(pose["across_m"])
		explorer.call("TeleportToCanonicalMeters", a, float(surface.call("SampleHeight", a, z)) + 1.7, z, float(pose["yaw_deg"]), float(pose.get("pitch_deg", 0.0)), true)
		# Count every frame from the jump until streaming is idle, then a few more.
		var settled := 0
		var guard := 0
		while settled < 20 and guard < 3000:
			await process_frame
			await _count(entry, frame)
			frame += 1
			guard += 1
			settled = settled + 1 if COMMON.builds_idle(game) else 0
	_end(entry)


func _run_frames(entry: Dictionary, secs: float) -> void:
	var t := 0.0
	var frame := 0
	while t < secs:
		await process_frame
		await _count(entry, frame)
		t += FRAME_DT
		frame += 1


func _dist(a: Dictionary, b: Dictionary) -> float:
	return Vector3(float(b["around"]) - float(a["around"]), float(b["vertical"]) - float(a["vertical"]), float(b["across"]) - float(a["across"])).length()


## Mouse-wheel steps, as the player changes noclip speed; the explorer multiplies by NoclipWheelMultiplier ^ factor per event.
func _wheel_to_speed(target: float) -> void:
	var current := float(explorer.get("NoclipTargetSpeedMetersPerSecond"))
	var mult := maxf(1.01, float(explorer.get("NoclipWheelMultiplier")))
	if current <= 0.0 or absf(current - target) < target * 0.02:
		return
	var steps := log(target / current) / log(mult)
	var e := InputEventMouseButton.new()
	e.button_index = MOUSE_BUTTON_WHEEL_UP if steps > 0.0 else MOUSE_BUTTON_WHEEL_DOWN
	e.pressed = true
	e.factor = absf(steps)
	e.position = Vector2(100, 100)
	Input.parse_input_event(e)


func _key(code: int, down: bool) -> void:
	if held.get(code, false) == down:
		return
	held[code] = down
	var e := InputEventKey.new()
	e.keycode = code
	e.physical_keycode = code
	e.pressed = down
	Input.parse_input_event(e)


func _release_keys() -> void:
	for code in held.keys():
		_key(code, false)
