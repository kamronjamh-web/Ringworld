extends SceneTree
## OC-3 byte identity at rest (docs/occlusion-culling-plan.md section 7): the tree frustum filter must not change a single pixel of a still frame.
## On an isolated copy of slot 3 (real start menu, Continue), clocks held, wind frozen, HUD hidden: at each pose and yaw it captures the main viewport with
## BiomeDecorations.TreeFrustumCulling off (A), on (B) and off again (A2), and compares the raw RGBA8 bytes. A must equal A2 (otherwise the harness itself is
## not deterministic and the verdict is FAIL "baseline unstable") and B must equal A. The same triple is then repeated after a cloud move (the cloud
## simulation time is advanced, which exercises the per-tree term refresh and copy) and after an 8 m step (a fresh cull). Any difference blocks OC-2.
## Temporal noise: on the real world two captures of ONE state differ in 0.5 to 55 percent of the pixels (mostly 1 to 2 LSB sky banding and sparse dither, a few
## pixels up to ~170), so strict A == A2 does not hold today. Unless --strict is given, an unstable baseline is judged by 16x16 tiles instead: B vs A may not
## have a tile with more than 32 pixels differing by more than 2 LSB that also exceeds twice the densest tile of A vs A2 (a culled visible tree makes a dense blob; noise does not),
## and the verdict says "noise-limited". With a stable baseline the compare is the exact byte compare.
## --quiet-scene hides RingVolumetricClouds, SunshineCloudBridge, SunshineCloudsDriver, StandingGrass and the gust-front leaf debris (ForcedLevel 0); combine with --strict.
## Default (B7c): still-capture mode (tools/tree_visibility_world.gd capture_begin/capture_end, docs/test-speed-plan.md 3.8). freeze_noise and quiet_scene are not applied; per case the world change happens
## first, then capture_begin (time scale 0, cloud march held, byte-identical pair self-check), A and B are compared strictly and there is no A2. A case that is not deterministic falls back to the
## A-B-A2 logic and is marked capture_fallback. --no-capture restores the old path (--freeze, --quiet-scene and the noise logic above apply only there).
## SKIPs cleanly (TREE_FRUSTUM_IDENTITY=SKIP ...) while the TreeFrustumCulling property does not exist on the BiomeDecorations layer (lane T adds it in OC-2).
## Run (windowed Vulkan, not --headless; never on the player's save; takes the GPU queue):
##   tools/tree_visibility_run.ps1 -Test frustum_identity -Build      (or via tools/run_tests.ps1 -Tests tree_frustum_identity_gpu_test)
## Args (after --): --prop=<property> --sun-elevation=<deg> --out=<dir> [--names=forest_interior_eye,forest_edge_eye,plains_eye,spawn_ground] [--yaws=0,90,180,270] [--skip-moves] [--freeze=clouds,taa] [--strict] [--quiet-scene]
##   [--baseline-only] (no property needed: captures A twice to prove the harness is deterministic) [--settle=60]
## --freeze removes per-frame noise (cloud interleave and jitter, temporal AA) that makes two captures of one state differ; default none (measured: cloud interleave off made forest yaw 90 worse, 779k pixels differing; the game runs FXAA, no TAA).
## Prints TREE_FRUSTUM_IDENTITY=PASS | SKIP <why> | FAIL <why>. On a mismatch the A and B PNGs are saved under --out; compare them with
## tools/media/ffmpeg_tools.ps1 compare.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
var PROP := "TreeFrustumCulling"   # --prop=<BiomeDecorations bool property> A/Bs another switch the same way (off = A, on = B), e.g. TreeDropFaintInstances
const NOISE_LSB := 2   # +-1..2 LSB drift (sky banding, dither) is not counted toward the tile density

var args := {}
var out_dir := ""
var game: Node
var trees: Node
var failures: Array = []
var compared := 0
var noise_limited := 0
var report := {"cases": [], "events": []}
var settle_frames := 60
var capture_mode := true   # still-capture mode (docs/test-speed-plan.md 3.8); --no-capture restores the freeze_noise / A-B-A2 path
var capture_fallbacks := 0


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	PROP = String(args.get("prop", PROP))
	create_timer(3000.0).timeout.connect(func(): _finish("FAIL timeout"))
	call_deferred("_run")


func _log(text: String) -> void:
	var line := "TVH %s" % text
	print(line)
	report["events"].append(line)


func _finish(verdict: String) -> void:
	WORLD.capture_end(game)   # never leave time scale 0 behind (no-op when not in capture mode)
	if out_dir != "":
		var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
		if f != null:
			f.store_string(JSON.stringify(report, " "))
			f.close()
	if verdict.begins_with("PASS") or verdict.begins_with("SKIP"):
		print("TREE_FRUSTUM_IDENTITY=" + verdict)
		quit(0)
	else:
		push_error("TREE_FRUSTUM_IDENTITY=" + verdict)
		quit(1)


func _run() -> void:
	if not WORLD.profile_is_isolated():
		_finish("FAIL user:// is not an isolated test profile; refusing to touch the player's save")
		return
	out_dir = String(args.get("out", ProjectSettings.globalize_path("res://.godot/tree_frustum_identity")))
	DirAccess.make_dir_recursive_absolute(out_dir)
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
	trees = game.get_node_or_null("BiomeDecorations")
	var baseline_only := args.has("baseline-only")
	if not baseline_only and not WORLD.has_property(trees, PROP):
		_finish("SKIP property %s does not exist on BiomeDecorations (OC-2 not landed)" % PROP)
		return
	report["machine"] = {"adapter": RenderingServer.get_video_adapter_name(), "args": args}
	WORLD.freeze_world(game)
	capture_mode = not args.has("no-capture")
	report["capture_mode"] = capture_mode
	if capture_mode:
		report["viewport"] = {"skipped": "capture mode (freeze_noise not called)"}
		_log("capture mode: freeze_noise and quiet_scene skipped; each case is captured with time scale 0 and the cloud march held")
	else:
		report["viewport"] = WORLD.freeze_noise(self, game, String(args.get("freeze", "")).split(",", false))
		_log("viewport %s" % str(report["viewport"]))
	WORLD.hide_overlays(game)
	if args.has("quiet-scene") and not capture_mode:
		report["quiet_scene"] = WORLD.quiet_scene(game)
		_log("quiet scene: hidden %s" % str(report["quiet_scene"]))
	var poses: Dictionary = WORLD.load_poses()
	var names := String(args.get("names", "forest_interior_eye,forest_edge_eye,plains_eye,spawn_ground")).split(",", false)
	var yaw_offsets := String(args.get("yaws", "0,90,180,270")).split(",", false)
	for name in names:
		if not poses.has(name):
			_log("pose %s missing" % name)
			failures.append("pose %s missing" % name)
			continue
		var pose: Dictionary = poses[name]
		for yo in yaw_offsets:
			var yaw := fposmod(float(pose["yaw_deg"]) + float(yo), 360.0)
			var idle: bool = await WORLD.go_pose(self, game, pose, yaw, true)
			if args.has("sun-elevation"):
				# Low (or negative) sun: shadows come from outside the view. --sun-elevation=5 (degrees, rising); logs which lights cast.
				report["time"] = COMMON.apply_time(game, {"mode": "elevation", "value": float(args["sun-elevation"]), "rising": true})
				WORLD.freeze_world(game)
				await _settle(settle_frames * 2)
				for ln in ["StarLight", "PlanetLight"]:
					var light := game.get_node_or_null(ln) as DirectionalLight3D
					if light != null:
						_log("%s energy %.3f shadow %s visible %s" % [ln, light.light_energy, str(light.shadow_enabled), str(light.visible)])
			WORLD.freeze_world(game)
			var tag := "%s_yaw%d" % [name, int(round(yaw))]
			await _triple(tag + "_rest", baseline_only)
			if args.has("skip-moves") or baseline_only:
				continue
			# Cloud move: advance the held cloud simulation time (terms and cloud shadow refresh), then compare again.
			var weather := game.get_node_or_null("RingVolumetricClouds")
			if weather != null:
				weather.set("CloudSimulationTimeSeconds", float(weather.get("CloudSimulationTimeSeconds")) + 90.0)
			await _settle(settle_frames)
			await _triple(tag + "_cloudmove", false)
			# 8 m step: teleport 8 m along the ring from the pose, then compare again.
			var explorer: Node = game.get_node("Explorer")
			var surface: Node = game.get_node("Terrain3DGameplay")
			var a := float(pose["around_m"]) + 8.0
			var z := float(pose["across_m"])
			explorer.call("TeleportToCanonicalMeters", a, float(surface.call("SampleHeight", a, z)) + 1.7, z, yaw, float(pose.get("pitch_deg", 0.0)), true)
			await COMMON.settle(self, 0.3)
			await COMMON.wait_builds_idle(self, game, 60.0)
			WORLD.freeze_world(game)
			await _triple(tag + "_step8m", false)
	_log("compared %d cases, %d failures" % [compared, failures.size()])
	if failures.is_empty() and compared > 0:
		_finish("PASS %d comparisons, %d byte-identical%s%s" % [compared, compared - noise_limited, (", %d noise-limited (baseline captures differ by temporal noise)" % noise_limited) if noise_limited > 0 else "",
			(", %d capture fallbacks" % capture_fallbacks) if capture_mode else ""])
	else:
		_finish("FAIL " + " | ".join(failures.slice(0, 12)) if not failures.is_empty() else "FAIL nothing was compared")


func _settle(frames: int) -> void:
	for i in frames:
		WORLD.hold_wind()
		await process_frame
	await COMMON.wait_builds_idle(self, game, 60.0, 10)


func _capture(enabled: bool, baseline_only: bool) -> Image:
	if not baseline_only:
		trees.set(PROP, enabled)
	await _settle(settle_frames)
	WORLD.hold_wind()
	await RenderingServer.frame_post_draw
	return WORLD.grab(self)


## One case. Capture mode: the world change has already happened; capture_begin, A and B (strict byte compare, no A2), capture_end. A case whose capture_begin is
## not deterministic falls back to the A-B-A2 noise logic below (time running again, as in --no-capture) and is marked capture_fallback.
func _triple(tag: String, baseline_only: bool) -> void:
	var cap := {}
	if capture_mode and not baseline_only:
		cap = await WORLD.capture_begin(self, game)
		if bool(cap["deterministic"]):
			var ca: Image = await _capture(false, false)
			var cb: Image = await _capture(true, false)
			# Positive control: the filter must actually be on for B (it reads real ticks, but a skipped compute at time scale 0 would leave it off and make A == B vacuous).
			var engaged := bool(trees.get("TreeCullActive"))
			WORLD.capture_end(game)
			compared += 1
			var same := ca.get_data() == cb.get_data()
			var centry := {"case": tag, "baseline_stable": true, "identical": same, "capture": cap, "cull_active": engaged}
			if not engaged:
				failures.append("%s: culling did not engage in capture mode (TreeCullActive false); the compare would be vacuous" % tag)
			if not same:
				centry["diff"] = _diff(ca, cb)
				failures.append("%s: culling on differs from off (%s)" % [tag, str(centry["diff"])])
				ca.save_png(out_dir.path_join(tag + "_A.png"))
				cb.save_png(out_dir.path_join(tag + "_B.png"))
			_log("%s capture deterministic frames=%d identical=%s" % [tag, int(cap["frames_waited"]), str(same)])
			report["cases"].append(centry)
			return
		WORLD.capture_end(game)
		capture_fallbacks += 1
		_log("%s capture NOT deterministic (last pair differing px %d); falling back to A-B-A2" % [tag, int(cap["last_pair_differing_px"])])
	var a: Image = await _capture(false, baseline_only)
	var b: Image = a if baseline_only else await _capture(true, false)
	var a2: Image = await _capture(false, baseline_only)
	compared += 1
	var base_same := a.get_data() == a2.get_data()
	var b_same := baseline_only or a.get_data() == b.get_data()
	var entry := {"case": tag, "baseline_stable": base_same, "identical": b_same}
	if not cap.is_empty():
		entry["capture_fallback"] = true
		entry["capture"] = cap
		entry["last_pair_differing_px"] = cap["last_pair_differing_px"]
	if not base_same:
		entry["baseline_diff"] = _diff(a, a2)
		a.save_png(out_dir.path_join(tag + "_A.png"))
		a2.save_png(out_dir.path_join(tag + "_A2.png"))
		if args.has("strict"):
			failures.append("%s: baseline unstable (A vs A2 %s)" % [tag, str(entry["baseline_diff"])])
		else:
			noise_limited += 1
			if not b_same:
				var db: Dictionary = _diff(a, b)
				entry["diff"] = db
				var limit := maxi(32, 2 * int(entry["baseline_diff"]["max_tile"]))
				entry["tile_limit"] = limit
				if int(db["max_tile"]) > limit:
					failures.append("%s: culling on differs from off beyond noise (%s, tile limit %d)" % [tag, str(db), limit])
					b.save_png(out_dir.path_join(tag + "_B.png"))
				else:
					b_same = true   # noise only
	elif not b_same:
		entry["diff"] = _diff(a, b)
		failures.append("%s: culling on differs from off (%s)" % [tag, str(entry["diff"])])
		a.save_png(out_dir.path_join(tag + "_A.png"))
		b.save_png(out_dir.path_join(tag + "_B.png"))
	_log("%s baseline_stable=%s identical=%s %s" % [tag, str(base_same), str(b_same), str(entry.get("baseline_diff", ""))])
	report["cases"].append(entry)


## {pixels, max_abs, max_tile} between two RGBA8 images: differing pixels, the largest channel difference, and the most pixels differing by more than NOISE_LSB in one 16x16 tile.
func _diff(a: Image, b: Image) -> Dictionary:
	var da := a.get_data()
	var db := b.get_data()
	var w := a.get_width()
	var tiles_x := (w + 15) / 16
	var tiles := PackedInt32Array()
	tiles.resize(tiles_x * ((a.get_height() + 15) / 16))
	var pixels := 0
	var max_abs := 0
	var max_tile := 0
	var n := mini(da.size(), db.size())
	for i in range(0, n, 4):
		var d := maxi(maxi(absi(da[i] - db[i]), absi(da[i + 1] - db[i + 1])), absi(da[i + 2] - db[i + 2]))
		if d > 0:
			pixels += 1
			max_abs = maxi(max_abs, d)
			if d > NOISE_LSB:
				var p := i / 4
				var t := (p / w / 16) * tiles_x + (p % w) / 16
				tiles[t] += 1
				max_tile = maxi(max_tile, tiles[t])
	return {"pixels": pixels, "max_abs": max_abs, "max_tile": max_tile}
