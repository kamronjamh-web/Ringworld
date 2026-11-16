extends SceneTree
## Diagnosis probe for the tree frustum identity blob (spawn_ground yaw 90 at rest: with TreeFrustumCulling on, a ~16 px yellow-green blob appears that culling off does not show).
## On the isolated slot 3 copy it captures that view in a series of states and reports, for the blob rectangle found from the first A/B pair, the mean colour and how
## many pixels differ from the first A capture: A (off), B (on), A2, B2 (repeat, to tell a deterministic difference from time-varying noise), A3 right after an on -> off toggle,
## B with the camera-list renderers hidden, B with the full-list renderers hidden, B with every tree renderer hidden, B with grass hidden, plus the camera-list audit (term compare).
## Run (windowed Vulkan; takes the GPU queue): tools/tree_visibility_run.ps1 -Test blob_probe [-ScriptArgs "--yaw=90 --pose=spawn_ground"]
## Prints TREE_FRUSTUM_BLOB=PASS (report only; the evidence is in report.json and the logged lines).

const COMMON := preload("res://tools/shadow_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const CULL := "TreeFrustumCulling"

var args := {}
var out_dir := ""
var game: Node
var trees: Node
var report := {"events": [], "states": {}}
var rect := Rect2i()


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	create_timer(1500.0).timeout.connect(func(): _finish("FAIL timeout"))
	call_deferred("_run")


func _log(text: String) -> void:
	var line := "TVH %s" % text
	print(line)
	report["events"].append(line)


func _finish(verdict: String) -> void:
	if out_dir != "":
		var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
		if f != null:
			f.store_string(JSON.stringify(report, " "))
			f.close()
	if verdict.begins_with("PASS"):
		print("TREE_FRUSTUM_BLOB=" + verdict)
		quit(0)
	else:
		push_error("TREE_FRUSTUM_BLOB=" + verdict)
		quit(1)


func _settle(frames: int) -> void:
	for i in frames:
		WORLD.hold_wind()
		await process_frame
	await COMMON.wait_builds_idle(self, game, 60.0, 10)


func _grab() -> Image:
	WORLD.hold_wind()
	await RenderingServer.frame_post_draw
	return WORLD.grab(self)


func _find_all(pred: Callable) -> Array:
	var out: Array = []
	var stack: Array = [trees]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if pred.call(node):
			out.append(node)
		stack.append_array(node.get_children())
	return out


## Pixels differing by more than 20 in any channel between a and b; also the bounding rectangle of the largest cluster (16x16 tile with the most).
func _diff_rect(a: Image, b: Image) -> Dictionary:
	var da := a.get_data()
	var db := b.get_data()
	var w := a.get_width()
	var tiles_x := (w + 15) / 16
	var tiles := PackedInt32Array()
	tiles.resize(tiles_x * ((a.get_height() + 15) / 16))
	var best := 0
	var best_t := 0
	for i in range(0, da.size(), 4):
		var d := maxi(maxi(absi(da[i] - db[i]), absi(da[i + 1] - db[i + 1])), absi(da[i + 2] - db[i + 2]))
		if d > 20:
			var p := i / 4
			var t := (p / w / 16) * tiles_x + (p % w) / 16
			tiles[t] += 1
			if tiles[t] > best:
				best = tiles[t]
				best_t = t
	var tx := best_t % tiles_x
	var ty := best_t / tiles_x
	return {"max_tile": best, "tile_x": tx * 16, "tile_y": ty * 16}


func _region(img: Image, r: Rect2i) -> Dictionary:
	var d := img.get_data()
	var w := img.get_width()
	var sum := Vector3.ZERO
	var n := 0
	for y in range(r.position.y, r.position.y + r.size.y):
		for x in range(r.position.x, r.position.x + r.size.x):
			var i := (y * w + x) * 4
			sum += Vector3(d[i], d[i + 1], d[i + 2])
			n += 1
	return {"mean": sum / maxf(1.0, float(n))}


func _count_diff(a: Image, b: Image, r: Rect2i) -> int:
	var da := a.get_data()
	var db := b.get_data()
	var w := a.get_width()
	var n := 0
	for y in range(r.position.y, r.position.y + r.size.y):
		for x in range(r.position.x, r.position.x + r.size.x):
			var i := (y * w + x) * 4
			if maxi(maxi(absi(da[i] - db[i]), absi(da[i + 1] - db[i + 1])), absi(da[i + 2] - db[i + 2])) > 20:
				n += 1
	return n


func _state(name: String, a: Image, img: Image) -> void:
	var entry := {"blob_pixels_vs_A": _count_diff(a, img, rect), "blob_mean": _region(img, rect)["mean"], "whole_vs_A": _diff_rect(a, img)}
	report["states"][name] = entry
	_log("%-28s blob px differing from A: %4d  blob mean colour %s  densest tile %s" % [name, entry["blob_pixels_vs_A"], str(entry["blob_mean"]), str(entry["whole_vs_A"])])
	img.save_png(out_dir.path_join(name + ".png"))


func _run() -> void:
	if not WORLD.profile_is_isolated():
		_finish("FAIL user:// is not an isolated test profile")
		return
	out_dir = String(args.get("out", ProjectSettings.globalize_path("res://.godot/tree_frustum_blob")))
	DirAccess.make_dir_recursive_absolute(out_dir)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var err: Array = []
	game = await WORLD.boot_game(self, err)
	if game == null:
		_finish("FAIL " + ", ".join(err))
		return
	trees = game.get_node_or_null("BiomeDecorations")
	if not WORLD.has_property(trees, CULL):
		_finish("FAIL TreeFrustumCulling missing")
		return
	WORLD.freeze_world(game)
	WORLD.hide_overlays(game)
	if not args.has("loud-scene"):
		WORLD.quiet_scene(game)
	var poses: Dictionary = WORLD.load_poses()
	var pose: Dictionary = poses[String(args.get("pose", "spawn_ground"))]
	var yaw := fposmod(float(pose["yaw_deg"]) + float(args.get("yaw", "90")), 360.0)
	await WORLD.go_pose(self, game, pose, yaw, true)
	WORLD.freeze_world(game)
	var s := 60
	# A / B pair, find the blob.
	trees.set(CULL, false)
	await _settle(s)
	var a: Image = await _grab()
	trees.set(CULL, true)
	await _settle(s)
	var b: Image = await _grab()
	var found := _diff_rect(a, b)
	_log("A vs B densest tile: %s" % str(found))
	rect = Rect2i(maxi(0, int(found["tile_x"]) - 24), maxi(0, int(found["tile_y"]) - 24), 64, 64)
	_log("blob rectangle %s" % str(rect))
	_state("A", a, a)
	_state("B", a, b)
	trees.set(CULL, false)
	await _settle(s)
	_state("A2", a, await _grab())
	trees.set(CULL, true)
	await _settle(s)
	_state("B2", a, await _grab())
	trees.set(CULL, false)
	await _settle(2)
	_state("A3_2frames_after_toggle", a, await _grab())
	await _settle(s)
	_state("A3_settled", a, await _grab())
	trees.set(CULL, true)
	await _settle(s)
	var cam_r := _find_all(func(n): return n is MultiMeshInstance3D and String(n.name).ends_with("_Camera"))
	var full_r := _find_all(func(n): return n is MultiMeshInstance3D and String(n.name).begins_with("GpuTree_") and not String(n.name).ends_with("_Camera") and not String(n.name).ends_with("_CullVerify"))
	_log("camera renderers %d, full-list renderers %d" % [cam_r.size(), full_r.size()])
	for n in cam_r:
		(n as Node3D).visible = false
	await _settle(10)
	_state("B_camera_lists_hidden", a, await _grab())
	for n in cam_r:
		(n as Node3D).visible = true
	await _settle(10)
	# Full-list renderers carry the camera lists as children, so hiding them hides both: report separately by hiding only their own draw (cast/geometry via layers).
	for n in full_r:
		(n as VisualInstance3D).layers = 0
	await _settle(10)
	_state("B_full_list_layers_off", a, await _grab())
	for n in full_r:
		(n as VisualInstance3D).layers = 1
	await _settle(10)
	trees.visible = false
	await _settle(10)
	_state("B_trees_layer_hidden", a, await _grab())
	trees.visible = true
	await _settle(10)
	var grass := game.get_node_or_null("StandingGrass")
	if grass != null and "visible" in grass:
		grass.set("visible", false)
		await _settle(10)
		_state("B_grass_hidden", a, await _grab())
		grass.set("visible", true)
		await _settle(10)
	trees.call("StartTreeCameraAudit")
	await _settle(5)
	var deadline := Time.get_ticks_msec() + 30000
	while String(trees.get("TreeCullAuditResult")) == "" and Time.get_ticks_msec() < deadline:
		await process_frame
	report["audit"] = String(trees.get("TreeCullAuditResult"))
	_log("camera list audit (culling on): %s" % report["audit"])
	_finish("PASS probe finished")
