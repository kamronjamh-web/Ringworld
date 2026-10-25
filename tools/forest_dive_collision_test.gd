extends SceneTree

## TEST ONLY. Forest dive collision test (WO-5c2): a fast vertical descent over imported forest must have trunk colliders resident before the explorer
## reaches the canopy band. Checks the altitude-predicted, hysteresis-held 3D collider reach (RingBiomeDecorationLayer.ImportedForest.cs).
## GPU / windowed, NOT --headless. Boots the isolated certified fixture world (tools/shadow_harness_common.gd), poses at forest_noon, holds the world
## clocks, keeps the mouse free and blocks Escape / grave. The run must use an isolated APPDATA (never the real profile), as tools/quality_preset_capture.ps1 does.
##
## Steps: forest_noon pose -> pick the nearest resident imported tree -> explorer (noclip) 500 m above the terrain next to it -> wait for streaming idle
## (max 20 s) -> dive straight down by speed * frame delta (a per-frame canonical teleport) until 5 m above the terrain. Every frame records altitude,
## predicted altitude, effective radius, collider count and whether the nearest tree (horizontal) has a resident collider.
## PASS: at every frame with altitude <= 40 m the effective radius equals the configured radius AND the nearest tree within it has a collider; the altitude
## where the first collider became resident must be > 40 m.
##
## Args (after --): --speed=<m/s | hyper | warp> (default: the explorer's NoclipHyperSpeed, at least 240; the warp / trans-ring tiers are space-travel speeds)
##                  --start-alt=500  --end-alt=5  --res=1920x1080  --idle-timeout=20  --out=<abs dir>
## Output: FOREST_DIVE_COLLISION=PASS|FAIL <details> on stdout, <project>/.godot/forest_dive_collision/<stamp>/report.json. Exit 0 on PASS, 1 on FAIL.
##
## RUN (PowerShell, repo root; no other Godot process; closes with exit code 0/1; the console.log holds the FOREST_DIVE_COLLISION line):
##   $iso = "$PWD\.godot\forest_dive_collision\appdata"; New-Item -ItemType Directory -Force "$iso\Godot\app_userdata\RingworldTechDemo" | Out-Null
##   $env:APPDATA = $iso
##   & .\artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe --path . --resolution 1920x1080 --script res://tools/forest_dive_collision_test.gd -- --res=1920x1080
##   (restore $env:APPDATA afterwards; C# must be built first: ./tools/use_godot_48.ps1 -Build)

const COMMON := preload("res://tools/shadow_harness_common.gd")
const CANOPY_BAND_METERS := 40.0
const TREE_SEARCH_METERS := 250.0
const COLUMN_OFFSET_METERS := 6.0
const COLUMN_TREE_MAX_METERS := 30.0

var game: Node
var explorer: Node
var surface: Node
var trees: Node
var monitor: Node
var drawer: Node
var report := {}
var out_dir := ""
var finished := false
var started := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	if started:
		return
	started = true
	if RenderingServer.get_rendering_device() == null:
		_finish(false, "RenderingDevice is required"); return
	var res := COMMON.arg_value("--res=", "1920x1080").split("x")
	DisplayServer.window_set_size(Vector2i(int(res[0]), int(res[1])))
	root.size = Vector2i(int(res[0]), int(res[1]))
	var stamp := Time.get_datetime_string_from_system().replace(":", "-").replace("T", "_")
	out_dir = COMMON.arg_value("--out=", ProjectSettings.globalize_path("res://.godot/forest_dive_collision/%s" % stamp))
	DirAccess.make_dir_recursive_absolute(out_dir)
	report = {"status": "RUNNING", "adapter": RenderingServer.get_video_adapter_name(), "args": OS.get_cmdline_user_args(), "frames": []}

	game = await COMMON.create_game(self, "res://.godot/forest_dive_collision/fixture_%d" % Time.get_ticks_usec())
	if game == null:
		_finish(false, "game boot failed"); return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	trees = game.get_node_or_null("BiomeDecorations")
	monitor = game.get_node_or_null("RingPerformanceMonitor")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	if explorer == null or surface == null or trees == null:
		_finish(false, "required node missing (Explorer / Terrain3DGameplay / BiomeDecorations)"); return
	# Mouse free; Escape (pause menu) and grave (tuning drawer) must not interfere.
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	for blocked in [game.get_node_or_null("LodPauseMenu"), drawer]:
		if blocked != null:
			blocked.set_process_unhandled_input(false)
			blocked.set_process_unhandled_key_input(false)
			blocked.set_process_input(false)
			blocked.set_process_shortcut_input(false)
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	await COMMON.settle(self, 5.0)
	await COMMON.wait_builds_idle(self, game, 180.0, 60)
	var base: Dictionary = COMMON.pose_now(game)
	COMMON.hold_world_clocks(game)
	var info: Dictionary = await COMMON.go_pose(self, game, base, "forest_noon", 4.0, 120.0)
	if info.has("error"):
		_finish(false, "forest_noon pose failed: " + str(info["error"])); return
	COMMON.hold_world_clocks(game)
	var pose: Dictionary = COMMON.pose_now(game)

	# Dive speed: the highest ground-flight tier, at least 240 m/s.
	var tiers := {"noclip": float(explorer.get("NoclipSpeed")), "fast": float(explorer.get("NoclipFastSpeed")), "hyper": float(explorer.get("NoclipHyperSpeed")),
		"warp": float(explorer.get("NoclipWarpSpeed")), "trans_ring": float(explorer.get("NoclipTransRingSpeed"))}
	var speed_arg := COMMON.arg_value("--speed=", "hyper")
	var speed := float(tiers["hyper"]) if speed_arg == "hyper" else (float(tiers["warp"]) if speed_arg == "warp" else float(speed_arg))
	speed = maxf(speed, 240.0)
	var start_alt := float(COMMON.arg_value("--start-alt=", "500"))
	var end_alt := float(COMMON.arg_value("--end-alt=", "5"))
	var idle_timeout := float(COMMON.arg_value("--idle-timeout=", "20"))
	var configured := maxf(10.0, float(trees.get("ImportedForestCollisionRadiusMeters")))
	report["speed_tiers"] = tiers
	report["dive_speed"] = speed
	report["configured_radius"] = configured

	# Spot: the nearest resident imported tree to the pose; the dive column sits a few metres beside it.
	var tree: Dictionary = trees.call("ProbeImportedForestNearestTree", float(pose["around"]), float(pose["across"]), TREE_SEARCH_METERS)
	if not bool(tree.get("found", false)):
		_finish(false, "no resident imported tree within %d m of the forest_noon pose" % int(TREE_SEARCH_METERS)); return
	var col_around := float(tree["around"])
	var col_across := float(tree["across"]) + COLUMN_OFFSET_METERS
	var ground := float(surface.call("SampleHeight", col_around, col_across))
	report["column"] = {"around": col_around, "across": col_across, "ground": ground, "tree": tree}
	print("FOREST_DIVE column around=%.1f across=%.1f ground=%.1f tree_distance=%.1f ground_collider=%s" % [col_around, col_across, ground, float(tree["distance"]), str(tree["has_collider"])])

	# Hover at the start altitude until streaming idles (or the timeout).
	explorer.call("TeleportToCanonicalMeters", col_around, ground + start_alt, col_across, rad_to_deg(float(explorer.get("SavedYaw"))), -10.0, true)
	await COMMON.settle(self, 0.3)
	report["hover_idle"] = await COMMON.wait_builds_idle(self, game, idle_timeout, 30)
	COMMON.hold_world_clocks(game)
	explorer.call("RefreshAltitudeAboveTerrain")
	await process_frame
	report["hover"] = _sample(col_around, col_across)
	var column_tree: Dictionary = trees.call("ProbeImportedForestNearestTree", col_around, col_across, COLUMN_TREE_MAX_METERS)
	if not bool(column_tree.get("found", false)):
		_finish(false, "no resident imported tree within %d m of the dive column at %d m altitude" % [int(COLUMN_TREE_MAX_METERS), int(start_alt)]); return
	print("FOREST_DIVE hover ", JSON.stringify(report["hover"]))

	# Dive.
	var frames: Array = []
	var altitude_cmd := start_alt
	var last_us := Time.get_ticks_usec()
	var first_collider_alt := NAN
	var violations: Array = []
	var min_altitude := INF
	var deadline := Time.get_ticks_msec() + 90000
	while altitude_cmd > end_alt and Time.get_ticks_msec() < deadline:
		await process_frame
		var now_us := Time.get_ticks_usec()
		var delta := minf(float(now_us - last_us) * 1e-6, 0.25)
		last_us = now_us
		altitude_cmd = maxf(end_alt - 1.0, altitude_cmd - speed * delta)
		explorer.call("TeleportToCanonicalMeters", col_around, ground + altitude_cmd, col_across, rad_to_deg(float(explorer.get("SavedYaw"))), -10.0, true)
		explorer.call("RefreshAltitudeAboveTerrain")
		var s := _sample(col_around, col_across)
		s["dt"] = delta
		frames.append(s)
		var alt := float(s["altitude"])
		min_altitude = minf(min_altitude, alt)
		if is_nan(first_collider_alt) and bool(s["target_has_collider"]):
			first_collider_alt = alt
		if alt <= CANOPY_BAND_METERS:
			var problem := ""
			if not is_equal_approx(float(s["effective_radius"]), configured):
				problem = "radius %.1f != configured %.1f" % [float(s["effective_radius"]), configured]
			elif not bool(s["tree_found"]):
				problem = "no resident tree near the column"
			elif float(s["tree_distance"]) <= float(s["effective_radius"]) and not bool(s["target_has_collider"]):
				problem = "nearest tree (%.1f m) has no collider" % float(s["tree_distance"])
			if problem != "":
				violations.append({"frame": frames.size() - 1, "altitude": alt, "problem": problem})
	report["frames"] = frames
	report["frame_count"] = frames.size()
	report["min_altitude"] = min_altitude
	report["first_collider_altitude"] = first_collider_alt
	report["violations"] = violations
	report["violation_count"] = violations.size()
	var reached := min_altitude <= CANOPY_BAND_METERS
	var first_ok := not is_nan(first_collider_alt) and first_collider_alt > CANOPY_BAND_METERS
	var details := "speed=%.0f frames=%d min_alt=%.1f first_collider_alt=%.1f radius_final=%.0f/%.0f colliders_final=%d violations=%d" % [speed, frames.size(), min_altitude,
		first_collider_alt, float(frames[-1]["effective_radius"]) if not frames.is_empty() else -1.0, configured, int(frames[-1]["collider_count"]) if not frames.is_empty() else -1, violations.size()]
	if not reached:
		_finish(false, "dive never reached the canopy band: " + details); return
	if not first_ok:
		_finish(false, "first collider not resident above the canopy band: " + details); return
	_finish(violations.is_empty(), details)


## One per-frame record at the dive column.
func _sample(column_around: float, column_across: float) -> Dictionary:
	var nearest: Dictionary = trees.call("ProbeImportedForestNearestTree", column_around, column_across, 60.0)
	return {"altitude": float(explorer.get("CurrentAltitudeAboveTerrainMeters")), "predicted_altitude": float(trees.get("ImportedForestPredictedAltitudeMeters")),
		"effective_radius": float(trees.get("ImportedForestCollisionEffectiveRadiusMeters")), "collider_count": int(trees.get("ImportedForestColliderCount")),
		"tree_found": bool(nearest.get("found", false)), "tree_distance": float(nearest.get("distance", -1.0)), "target_has_collider": bool(nearest.get("has_collider", false))}


func _finish(passed: bool, details: String) -> void:
	if finished:
		return
	finished = true
	report["status"] = "PASS" if passed else "FAIL"
	report["details"] = details
	if out_dir != "":
		var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
		if f != null:
			f.store_string(JSON.stringify(report, "  "))
			f.close()
	print("FOREST_DIVE_COLLISION=%s %s out=%s" % ["PASS" if passed else "FAIL", details, out_dir])
	quit(0 if passed else 1)
