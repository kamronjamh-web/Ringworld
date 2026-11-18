extends SceneTree
## Perf pass (trees): how often the tree compute re-runs while the camera stands still. On an isolated copy of slot 3 (real start menu, Continue), clocks RUNNING,
## at a ground pose, it reads BiomeDecorations.TreeComputeCounters before and after each window and prints the per-second rates, plus the frame time.
## Args (after --): --out=<dir> [--names=forest_interior_eye] [--seconds=10] [--props=Prop=1,Prop2=0] (bool properties applied to BiomeDecorations before the windows)
## Prints TREE_CADENCE=PASS. Run: tools/tree_visibility_run.ps1 -Test cadence
const COMMON := preload("res://tools/shadow_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
var args := {}
var game: Node
var trees: Node
var out_dir := ""
var report := {"windows": []}

func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	create_timer(1800.0).timeout.connect(func(): _finish("FAIL timeout"))
	call_deferred("_run")

func _finish(verdict: String) -> void:
	if out_dir != "":
		var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
		if f != null:
			f.store_string(JSON.stringify(report, " "))
			f.close()
	if verdict.begins_with("PASS"):
		print("TREE_CADENCE=" + verdict)
		quit(0)
	else:
		push_error("TREE_CADENCE=" + verdict)
		quit(1)

func _counters() -> Dictionary:
	var d := {}
	for part in String(trees.get("TreeComputeCounters")).split(" ", false):
		var kv := part.split("=")
		if kv.size() == 2:
			d[kv[0]] = int(kv[1])
	return d

func _run() -> void:
	if not WORLD.profile_is_isolated():
		_finish("FAIL user:// is not an isolated test profile")
		return
	out_dir = String(args.get("out", ProjectSettings.globalize_path("res://.godot/tree_cadence")))
	DirAccess.make_dir_recursive_absolute(out_dir)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var err: Array = []
	game = await WORLD.boot_game(self, err)
	if game == null:
		_finish("FAIL " + ", ".join(err))
		return
	trees = game.get_node_or_null("BiomeDecorations")
	for pair in String(args.get("props", "")).split(",", false):
		var kv := pair.split("=")
		if kv.size() == 2:
			trees.set(kv[0], kv[1] == "1")
			print("TVH prop %s=%s" % [kv[0], kv[1]])
	WORLD.hide_overlays(game)
	var poses: Dictionary = WORLD.load_poses()
	var seconds := float(args.get("seconds", "10"))
	for name in String(args.get("names", "forest_interior_eye")).split(",", false):
		var pose: Dictionary = poses[name]
		await WORLD.go_pose(self, game, pose, float(pose["yaw_deg"]), false)
		await COMMON.settle(self, 3.0)
		var before := _counters()
		var frames0 := Engine.get_process_frames()
		var t0 := Time.get_ticks_msec()
		await COMMON.settle(self, seconds)
		var after := _counters()
		var dt := float(Time.get_ticks_msec() - t0) / 1000.0
		var frames := Engine.get_process_frames() - frames0
		var row := {"pose": name, "seconds": dt, "frames": frames}
		for k in after:
			row[k + "_per_s"] = snappedf(float(after[k] - int(before.get(k, 0))) / dt, 0.1)
		report["windows"].append(row)
		print("TVH cadence %s" % str(row))
	_finish("PASS")
