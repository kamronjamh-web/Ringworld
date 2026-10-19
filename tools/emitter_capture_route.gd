extends SceneTree

## M1-11 windowed capture route for the beam emitter sites. Run WINDOWED (a headless run has no framebuffer to save):
##   Godot_v4.8-dev7_mono_win64.exe --path . --script res://tools/emitter_capture_route.gd -- --out=DIR [--sites=land,island,mountain,main] [--quick]
## Per site: Entry (plus bore_down, bore_overview), Gallery, Far, High, then (unless --quick) an approach flight from 20 km (40 km main)
## into the entry distance, recording per-frame distance / frame time / streamer slot / stand-in presented / bore count (M1-5 crossover check).
## Writes <class>_<viewpoint>.png, <class>_approach_<km>.png, <class>_approach.csv, report.json and report.md. Exit 0 and EMITTER_CAPTURE_ROUTE=PASS when every teleport
## arrived and every file was written.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const CIRC := 31415936.0
const START_AROUND := 5000000.0
const START_ACROSS := 158000.0
const VIEWPOINTS := [[0, "entry"], [1, "gallery"], [2, "far"], [3, "high"]]
const REGULAR_SHOTS_KM := [16.0, 14.0, 13.0, 12.0, 11.5, 11.0, 10.5, 10.0, 6.0, 2.0, 0.5]
const MAIN_SHOTS_KM := [34.0, 32.0, 30.0, 28.0, 26.0, 25.0, 24.0, 20.0, 10.0, 2.0]
const SETTLE_FRAMES := 30

var out_dir := "user://emitter_capture_route"
var wanted_classes: Array[String] = ["land", "island", "mountain", "main"]
var quick := false
var fixture := ""
var deadline := 0
var game: Node
var explorer: Node
var surface: Node
var layer: Node
var streamer: Node
var standins: Node
var drawer: Node
var finished := false
var problems: Array[String] = []
var files: Array[String] = []
var sites := {}
var run_started := 0

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(3300.0).timeout.connect(func(): _abort("capture route timed out"))

func run() -> void:
	run_started = Time.get_ticks_msec()
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--out="): out_dir = argument.trim_prefix("--out=")
		elif argument.begins_with("--sites="):
			wanted_classes.clear()
			for part in argument.trim_prefix("--sites=").split(",", false): wanted_classes.append(part.strip_edges().to_lower())
		elif argument == "--quick": quick = true
	DirAccess.make_dir_recursive_absolute(out_dir)
	fixture = "res://.godot/terrain-generation-tests/capture-route-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalCaptureRoute"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	deadline = Time.get_ticks_msec() + 3200000
	game = await _create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	layer = game.get_node_or_null("EmitterBores")
	streamer = game.get_node_or_null("EmitterSites")
	standins = game.get_node_or_null("EmitterStandIns")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	if explorer == null or surface == null or layer == null or streamer == null or standins == null or drawer == null:
		_abort("missing explorer, gameplay, bore layer, streamer, stand-ins or drawer"); return
	explorer.set("EnableSwimming", true)
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	var waited := 0
	while waited < 900 and Time.get_ticks_msec() < deadline:
		if int(drawer.call("FindEmitterSite", "main")) >= 0 and bool(layer.get("IsBound")): break
		waited += 1
		await physics_frame
	if int(drawer.call("FindEmitterSite", "main")) < 0: _abort("emitter navigation never bound (needs a v5 world)"); return
	await _wait_global()

	# Site choice: from one fixed start point, so every run picks the same sites.
	explorer.call("TeleportToCanonicalMeters", START_AROUND, 1500.0, START_ACROSS, 0.0, 0.0, true)
	await _frames(60)
	var chosen := {}
	for cls in wanted_classes:
		var query := "main" if cls == "main" else "nearest " + cls
		var site := int(drawer.call("FindEmitterSite", query))
		if site < 0: problems.append("no %s site found" % cls); continue
		chosen[cls] = site
	for cls in wanted_classes:
		if not chosen.has(cls): continue
		await _site_route(cls, int(chosen[cls]))
		if finished: return
	await _finish()

func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: _abort("launcher lacks terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", 2)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_abort("runtime surface definition preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", fixture.path_join("surface_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEmitterSiteDefaults"):
		tuning.call("InstallEmitterSiteDefaults", fixture.path_join("emitter_tuning.tres"))
	# Same disabled set as emitter_site_teleport_test --full: clouds and water stay on so the captures look like the game.
	for name in ["BiomeDecorations", "StandingGrass", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "TerrainMinimap", "CertificationMonitor"]:
		var node: Node = next.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_abort("startup readiness deadline")
	return null

func _diag() -> Dictionary: return surface.call("GetSurfaceDiagnostics")

func _pose_now() -> Dictionary:
	var diag := _diag()
	return {
		"around": snappedf(float(diag.get("player_around", 0.0)), 0.01),
		"vertical": snappedf(float(diag.get("player_height", 0.0)), 0.01),
		"across": snappedf(float(diag.get("player_across", 0.0)), 0.01),
		"yaw": snappedf(rad_to_deg(float(explorer.get("SavedYaw"))), 0.01),
		"pitch": snappedf(float(explorer.get("SavedPitch")), 0.01),
		"flight": bool(explorer.get("NoclipEnabled")),
	}

func _site_route(cls: String, site: int) -> void:
	var rec := {"class": cls, "site": site, "arrivals": {}, "poses": {}, "images": []}
	sites[cls] = rec
	for entry in VIEWPOINTS:
		var vp: int = entry[0]
		var vp_name: String = entry[1]
		var arrived := await _teleport(site, vp, "%s %s" % [cls, vp_name], rec)
		if finished: return
		if not arrived: continue
		if vp == 0:
			# Landing on the pad, then the site facts (the entry point is within ~1 km of the spire, so the nearest bore site is this one).
			var landing := 0
			while landing < 180 and not bool(explorer.call("is_on_floor")):
				landing += 1
				await physics_frame
			var now := _pose_now()
			var info: Dictionary = layer.call("FindNearestBoreSite", float(now["around"]), float(now["across"]))
			if info.is_empty() or int(info["site"]) != site:
				problems.append("%s: nearest bore site %s is not the chosen site %d" % [cls, str(info.get("site", -1)), site]); continue
			rec["info"] = {"around": info["around"], "across": info["across"], "pad": info["pad"], "floor": info["floor"], "is_main": info["is_main"], "liner_radius": info["liner_radius"]}
			rec["on_floor"] = bool(explorer.call("is_on_floor"))
			rec["poses"]["entry"] = now
			await _shoot("%s_entry" % cls, rec)
			# Bore view, literal: pitch -60 from the entry point, grounded.
			explorer.call("TeleportToCanonicalMeters", float(now["around"]), float(now["vertical"]), float(now["across"]), float(now["yaw"]), -60.0, false)
			landing = 0
			await physics_frame
			while landing < 180 and not bool(explorer.call("is_on_floor")):
				landing += 1
				await physics_frame
			rec["poses"]["bore_down"] = _pose_now()
			await _shoot("%s_bore_down" % cls, rec)
			# Extra overview (flight): 100 m (200 m main) out on the entry bearing, above the pad, looking at the bore centre.
			var scale := 2.0 if cls == "main" else 1.0
			var pad := float(info["pad"])
			var da := _wrap(float(now["around"]) - float(info["around"])); var dx := float(now["across"]) - float(info["across"])
			var dl := maxf(sqrt(da * da + dx * dx), 1.0)
			var oa := fposmod(float(info["around"]) + da / dl * 100.0 * scale, CIRC); var ox := float(info["across"]) + dx / dl * 100.0 * scale
			var ov := pad + 60.0 * scale
			explorer.call("TeleportToCanonicalMeters", oa, ov, ox, float(now["yaw"]), -rad_to_deg(atan2(60.0, 100.0)), true)
			rec["poses"]["bore_overview"] = {"around": oa, "vertical": ov, "across": ox, "yaw": float(now["yaw"]), "pitch": -rad_to_deg(atan2(60.0, 100.0)), "flight": true}
			await _shoot("%s_bore_overview" % cls, rec)
			continue
		await _wait_flight_or_ground(vp)
		rec["poses"][vp_name] = _pose_now()
		await _shoot("%s_%s" % [cls, vp_name], rec)
	if not quick and rec.has("info") and rec["poses"].has("far"):
		await _approach(cls, rec)

func _wait_flight_or_ground(vp: int) -> void:
	# Grounded far poses must land before the capture; flight poses hold in noclip.
	if vp != 2 or bool(explorer.get("NoclipEnabled")): return
	var landing := 0
	while landing < 180 and not bool(explorer.call("is_on_floor")):
		landing += 1
		await physics_frame

func _teleport(site: int, vp: int, label: String, rec: Dictionary) -> bool:
	if not bool(drawer.call("RequestEmitterTeleport", site, vp)):
		problems.append("%s: request refused: %s" % [label, drawer.get("EmitterTeleportMessage")]); rec["arrivals"][label] = "refused"; return false
	var start := Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < 180000 and Time.get_ticks_msec() < deadline:
		await physics_frame
		var state := str(drawer.get("EmitterTeleportState"))
		if state == "Arrived":
			var seconds := (Time.get_ticks_msec() - start) / 1000.0
			rec["arrivals"][label] = snappedf(seconds, 0.1)
			print("EMITTER_CAPTURE_ROUTE arrived %s: %s (%.1f s)" % [label, drawer.get("EmitterTeleportMessage"), seconds])
			return true
		if state == "Failed" or state == "TimedOut":
			problems.append("%s: %s: %s" % [label, state, drawer.get("EmitterTeleportMessage")]); rec["arrivals"][label] = state; return false
	problems.append("%s: no arrival within 180 s" % label); rec["arrivals"][label] = "no_arrival"
	return false

func _wait_global() -> void:
	var global_lod: Node = game.get_node_or_null("GlobalTerrainLod3") if is_instance_valid(game) else null
	if global_lod == null: return
	var start := Time.get_ticks_msec()
	while int(global_lod.get("PendingInitialSegmentCount")) > 0 and Time.get_ticks_msec() - start < 180000:
		await process_frame

func _shoot(shot_name: String, rec: Dictionary) -> void:
	await _wait_global()
	await _frames(SETTLE_FRAMES)
	await _wait_global()
	_save(shot_name, rec)

func _save(shot_name: String, rec: Dictionary) -> void:
	var path := out_dir.path_join(shot_name + ".png")
	var image := root.get_texture().get_image()
	var err := image.save_png(path)
	if err != OK or not FileAccess.file_exists(path):
		problems.append("could not write %s (error %d)" % [path, err]); return
	rec["images"].append(shot_name + ".png")
	files.append(path)
	print("EMITTER_CAPTURE_ROUTE image ", path)

func _wrap(delta: float) -> float:
	var d := fposmod(delta, CIRC)
	return d - CIRC if d > CIRC * 0.5 else d

## One shot of a km list formatted like the file names: 11.5 -> "11.5", 16.0 -> "16".
func _km_name(km: float) -> String: return str(int(km)) if is_equal_approx(km, round(km)) else str(km)

func _approach(cls: String, rec: Dictionary) -> void:
	var info: Dictionary = rec["info"]
	var far: Dictionary = rec["poses"]["far"]
	var main := cls == "main"
	var sa := float(info["around"]); var sx := float(info["across"]); var pad := float(info["pad"])
	var start_d := 40000.0 if main else 20000.0
	var stop_d := 930.0 if main else 232.0
	var seconds := 60.0 if main else 40.0
	var speed := (start_d - stop_d) / seconds
	# Straight line along the Far pose's bearing; constant altitude 300 m above the pad (hover).
	var da := _wrap(float(far["around"]) - sa); var dx := float(far["across"]) - sx
	var dl := maxf(sqrt(da * da + dx * dx), 1.0)
	var ua := -da / dl; var ux := -dx / dl # unit vector from the start toward the site
	var hover := pad + 300.0
	var aim_v := float(far["vertical"]) + tan(deg_to_rad(float(far["pitch"]))) * dl
	var yaw := float(far["yaw"])
	var approach := {"start_km": start_d / 1000.0, "stop_km": stop_d / 1000.0, "speed_mps": snappedf(speed, 0.1), "hover_vertical": hover, "aim_vertical": snappedf(aim_v, 0.1), "yaw": yaw}
	rec["approach"] = approach
	var site := int(rec["site"])
	var shots: Array = (MAIN_SHOTS_KM if main else REGULAR_SHOTS_KM).duplicate()
	# Position at the start distance and let the terrain settle before the clock starts.
	_place(sa, sx, ua, ux, start_d, hover, aim_v, yaw)
	await _frames(20)
	await _wait_global()
	await _frames(60)
	var inbound: Array = await _fly(rec, site, sa, sx, ua, ux, start_d, stop_d, speed, hover, aim_v, yaw, shots)
	if finished: return
	approach["frames_in"] = inbound.size()
	var csv := PackedStringArray(["distance_m,frame_ms,slot,presented,bores"])
	for f in inbound: csv.append("%.1f,%.2f,%d,%d,%d" % [f[0], f[1], f[2], f[3], f[4]])
	var csv_path := out_dir.path_join("%s_approach.csv" % cls)
	var file := FileAccess.open(csv_path, FileAccess.WRITE)
	if file == null: problems.append("could not write " + csv_path)
	else:
		file.store_string("\n".join(csv)); file.close(); files.append(csv_path)
	_analyze_in(inbound, approach)
	# Outbound at 3x speed (no captures): the release distances.
	var outbound: Array = await _fly(rec, site, sa, sx, ua, ux, stop_d, start_d, speed * 3.0, hover, aim_v, yaw, [])
	if finished: return
	_analyze_out(outbound, approach)

func _place(sa: float, sx: float, ua: float, ux: float, d: float, hover: float, aim_v: float, yaw: float) -> void:
	var pitch := clampf(rad_to_deg(atan2(aim_v - hover, d)), -85.0, 85.0)
	explorer.call("TeleportToCanonicalMeters", fposmod(sa - ua * d, CIRC), hover, sx - ux * d, yaw, pitch, true)

## Flies the straight line between two distances at a constant speed (frame step capped at 50 ms so a hitch cannot skip a capture distance).
## Returns [distance_m, frame_ms (-1 when excluded), slot, presented 0/1, bores] per frame.
func _fly(rec: Dictionary, site: int, sa: float, sx: float, ua: float, ux: float, d_from: float, d_to: float, speed: float, hover: float, aim_v: float, yaw: float, shots: Array) -> Array:
	var frames: Array = []
	var d := d_from
	var direction := -1.0 if d_to < d_from else 1.0
	var next_shot := 0
	var exclude := 2
	var prev := Time.get_ticks_usec()
	while not finished and Time.get_ticks_msec() < deadline:
		_place(sa, sx, ua, ux, d, hover, aim_v, yaw)
		await process_frame
		var now := Time.get_ticks_usec()
		var ms := float(now - prev) / 1000.0
		prev = now
		var slot := int(streamer.call("FindSlotForSite", site))
		var presented := 1 if bool(standins.call("IsSitePresented", site)) else 0
		var bores := int(layer.get("ActiveBoreCount"))
		frames.append([d, -1.0 if exclude > 0 else ms, slot, presented, bores])
		if exclude > 0: exclude -= 1
		if next_shot < shots.size() and d <= float(shots[next_shot]) * 1000.0:
			_save("%s_approach_%s" % [str(rec["class"]), _km_name(float(shots[next_shot]))], rec)
			next_shot += 1
			exclude = 2
			prev = Time.get_ticks_usec()
		if (direction < 0.0 and d <= d_to) or (direction > 0.0 and d >= d_to): break
		d = d + direction * speed * minf(ms / 1000.0, 0.05)
		if direction < 0.0: d = maxf(d, d_to)
		else: d = minf(d, d_to)
	if next_shot < shots.size(): problems.append("%s approach: only %d of %d captures taken" % [str(rec["class"]), next_shot, shots.size()])
	return frames

func _ms_list(frames: Array, from: int, to: int) -> Array:
	var list: Array = []
	for i in range(maxi(from, 0), mini(to, frames.size())):
		if float(frames[i][1]) >= 0.0: list.append(float(frames[i][1]))
	return list

func _median(list: Array) -> float:
	if list.is_empty(): return 0.0
	var sorted := list.duplicate(); sorted.sort()
	return float(sorted[sorted.size() / 2])

func _analyze_in(frames: Array, approach: Dictionary) -> void:
	var assign_idx := -1
	var present_idx := -1
	var bore_idx := -1
	var slot_trans: Array = []; var present_trans: Array = []; var bore_trans: Array = []
	var worst := 0.0; var worst_d := 0.0
	for i in frames.size():
		var f: Array = frames[i]
		if assign_idx < 0 and int(f[2]) >= 0: assign_idx = i
		if present_idx < 0 and int(f[3]) == 1: present_idx = i
		if bore_idx < 0 and int(f[4]) > 0: bore_idx = i
		if i == 0 or (int(f[2]) >= 0) != (int(frames[i - 1][2]) >= 0): slot_trans.append({"distance_m": snappedf(float(f[0]), 1.0), "held": int(f[2]) >= 0})
		if i == 0 or int(f[3]) != int(frames[i - 1][3]): present_trans.append({"distance_m": snappedf(float(f[0]), 1.0), "presented": int(f[3]) == 1})
		if i == 0 or int(f[4]) != int(frames[i - 1][4]): bore_trans.append({"distance_m": snappedf(float(f[0]), 1.0), "bores": int(f[4])})
		if float(f[1]) > worst: worst = float(f[1]); worst_d = float(f[0])
	var all_ms := _ms_list(frames, 0, frames.size())
	var sorted := all_ms.duplicate(); sorted.sort()
	approach["frame_ms"] = {"median": snappedf(_median(all_ms), 0.01), "p99": snappedf(float(sorted[int(sorted.size() * 0.99)]) if not sorted.is_empty() else 0.0, 0.01),
		"worst": snappedf(worst, 0.01), "worst_at_distance_m": snappedf(worst_d, 1.0)}
	approach["slot_transitions"] = slot_trans
	approach["presented_transitions"] = present_trans
	approach["bore_transitions"] = bore_trans
	approach["assign_distance_m"] = snappedf(float(frames[assign_idx][0]), 1.0) if assign_idx >= 0 else null
	approach["presented_on_distance_m"] = snappedf(float(frames[present_idx][0]), 1.0) if present_idx >= 0 else null
	approach["bore_first_active_distance_m"] = snappedf(float(frames[bore_idx][0]), 1.0) if bore_idx >= 0 else null
	if assign_idx >= 0:
		var centre := float(frames[assign_idx][0])
		var near_worst := 0.0; var near_d := 0.0
		for f in frames:
			if absf(float(f[0]) - centre) <= 1000.0 and float(f[1]) > near_worst: near_worst = float(f[1]); near_d = float(f[0])
		approach["worst_ms_within_1km_of_assign"] = {"ms": snappedf(near_worst, 0.01), "at_distance_m": snappedf(near_d, 1.0)}
	# First-visible hitch: the frames around the first assign / first presented, against the median of the 120 frames before.
	var spikes := {}
	for pair in [["assign", assign_idx], ["presented_on", present_idx]]:
		var idx: int = pair[1]
		if idx < 0: continue
		var around := _ms_list(frames, idx - 1, idx + 4)
		var peak := 0.0
		for v in around: peak = maxf(peak, float(v))
		var base := _median(_ms_list(frames, idx - 121, idx - 1))
		spikes[pair[0]] = {"at_distance_m": snappedf(float(frames[idx][0]), 1.0), "peak_ms": snappedf(peak, 0.01), "baseline_median_ms": snappedf(base, 0.01), "spike_ms": snappedf(peak - base, 0.01), "frame_index": idx}
	approach["first_visible_spike"] = spikes

func _analyze_out(frames: Array, approach: Dictionary) -> void:
	var release := -1
	var present_off := -1
	for i in frames.size():
		if release < 0 and i > 0 and int(frames[i][2]) < 0 and int(frames[i - 1][2]) >= 0: release = i
		if present_off < 0 and i > 0 and int(frames[i][3]) == 0 and int(frames[i - 1][3]) == 1: present_off = i
	approach["release_distance_m"] = snappedf(float(frames[release][0]), 1.0) if release >= 0 else null
	approach["presented_off_distance_m"] = snappedf(float(frames[present_off][0]), 1.0) if present_off >= 0 else null
	approach["frames_out"] = frames.size()

func _finish() -> void:
	finished = true
	var expected_classes := wanted_classes.size()
	if sites.size() != expected_classes: problems.append("only %d of %d site classes were visited" % [sites.size(), expected_classes])
	var report := {
		"quick": quick, "out_dir": ProjectSettings.globalize_path(out_dir), "run_seconds": snappedf((Time.get_ticks_msec() - run_started) / 1000.0, 1.0),
		"sites": sites, "problems": problems,
		"log_errors": "not captured by the script; tools/run_emitter_capture_route.ps1 counts SHADER ERROR / ERROR: lines from the engine output and adds them here",
	}
	var json_path := out_dir.path_join("report.json")
	var file := FileAccess.open(json_path, FileAccess.WRITE)
	if file == null: problems.append("could not write report.json")
	else:
		file.store_string(JSON.stringify(report, "\t")); file.close(); files.append(json_path)
	var md := _markdown(report)
	var md_path := out_dir.path_join("report.md")
	file = FileAccess.open(md_path, FileAccess.WRITE)
	if file == null: problems.append("could not write report.md")
	else:
		file.store_string(md); file.close(); files.append(md_path)
	var arrived_all := true
	for cls in sites:
		for label in sites[cls]["arrivals"]:
			if typeof(sites[cls]["arrivals"][label]) == TYPE_STRING: arrived_all = false
	var ok := problems.is_empty() and arrived_all
	print("EMITTER_CAPTURE_ROUTE problems=%d files=%d" % [problems.size(), files.size()])
	for p in problems: printerr("EMITTER_CAPTURE_ROUTE problem: " + p)
	print("EMITTER_CAPTURE_ROUTE=PASS" if ok else "EMITTER_CAPTURE_ROUTE=FAIL")
	quit(0 if ok else 1)

func _markdown(report: Dictionary) -> String:
	var lines := PackedStringArray()
	lines.append("# Emitter capture route")
	lines.append("")
	lines.append("Run %.0f s, quick=%s, out `%s`." % [report["run_seconds"], str(quick), report["out_dir"]])
	lines.append("")
	lines.append("## Sites and poses")
	lines.append("")
	lines.append("| class | site | pad | viewpoint | around | vertical | across | yaw | pitch | flight | arrival s |")
	lines.append("|---|---|---|---|---|---|---|---|---|---|---|")
	for cls in sites:
		var rec: Dictionary = sites[cls]
		var pad: String = "%.1f" % float(rec["info"]["pad"]) if rec.has("info") else "?"
		for pose_name in rec["poses"]:
			var p: Dictionary = rec["poses"][pose_name]
			var arrival := "-"
			for label in rec["arrivals"]:
				if str(label).ends_with(" " + pose_name): arrival = str(rec["arrivals"][label])
			lines.append("| %s | %d | %s | %s | %.1f | %.1f | %.1f | %.1f | %.1f | %s | %s |" % [cls, int(rec["site"]), pad, pose_name,
				float(p["around"]), float(p["vertical"]), float(p["across"]), float(p["yaw"]), float(p["pitch"]), str(p["flight"]), arrival])
	lines.append("")
	lines.append("## Approach flights (M1-5 crossover)")
	for cls in sites:
		var rec: Dictionary = sites[cls]
		if not rec.has("approach") or not rec["approach"].has("frame_ms"): continue
		var a: Dictionary = rec["approach"]
		lines.append("")
		lines.append("### %s (site %d), %.0f km to %.2f km at %.0f m/s, %d frames in" % [cls, int(rec["site"]), a["start_km"], a["stop_km"], a["speed_mps"], int(a["frames_in"])])
		lines.append("")
		lines.append("- Slot assigned at: %s m; released at: %s m (outbound)" % [str(a.get("assign_distance_m")), str(a.get("release_distance_m"))])
		lines.append("- Stand-in presented on at: %s m; off at: %s m (outbound); first bore active: %s m" % [str(a.get("presented_on_distance_m")), str(a.get("presented_off_distance_m")), str(a.get("bore_first_active_distance_m"))])
		lines.append("- Frame ms: median %.2f, p99 %.2f, worst %.2f at %s m" % [a["frame_ms"]["median"], a["frame_ms"]["p99"], a["frame_ms"]["worst"], str(a["frame_ms"]["worst_at_distance_m"])])
		if a.has("worst_ms_within_1km_of_assign"): lines.append("- Worst frame within +-1 km of the assign distance: %s ms at %s m" % [str(a["worst_ms_within_1km_of_assign"]["ms"]), str(a["worst_ms_within_1km_of_assign"]["at_distance_m"])])
		for key in a["first_visible_spike"]:
			var s: Dictionary = a["first_visible_spike"][key]
			lines.append("- First-visible (%s) at %s m: peak %s ms vs baseline %s ms, spike %s ms" % [key, str(s["at_distance_m"]), str(s["peak_ms"]), str(s["baseline_median_ms"]), str(s["spike_ms"])])
		lines.append("- Slot transitions: %s" % JSON.stringify(a["slot_transitions"]))
		lines.append("- Presented transitions: %s" % JSON.stringify(a["presented_transitions"]))
		lines.append("- Bore transitions: %s" % JSON.stringify(a["bore_transitions"]))
	lines.append("")
	lines.append("## Problems")
	lines.append("")
	if problems.is_empty(): lines.append("None: every teleport arrived and every file was written.")
	for p in problems: lines.append("- " + p)
	lines.append("")
	lines.append("Log errors: " + str(report["log_errors"]))
	return "\n".join(lines) + "\n"

func _frames(count: int) -> void:
	for ignored in count: await process_frame

func _abort(message: String) -> void:
	if finished: return
	finished = true
	problems.append(message)
	push_error("EMITTER_CAPTURE_ROUTE abort: " + message)
	print("EMITTER_CAPTURE_ROUTE=FAIL")
	quit(1)
