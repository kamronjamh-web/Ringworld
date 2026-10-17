extends SceneTree

## Phase E2 windowed capture: normal day, normal night, partial eclipse and totality, each with the pitch-black darkness OFF (the old lighting)
## and ON, at three poses, plus a pixel-diff proof that day and night frames are unchanged by the darkness (neutral outside an eclipse).
## Usage (windowed, NO other Godot process running, one run at a time; check `nvidia-smi` is not P5):
##   Godot_console.exe --path <repo> --resolution 1600x900 --script res://tools/eclipse_darkness_capture.gd -- --out=<abs dir>
## Uses an isolated world under res://.godot/eclipse-capture/ and stub WorldSession; nothing is written to user:// settings (the eclipse
## director is configured in memory only, never saved).
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PHASE_DARKNESS := 2

var out_dir := ""
var fixture := ""
var game: Node
var explorer: Node
var surface: Node
var celestial: Node
var weather: Node
var atmosphere: Node
var director: Node
var base_pose := {}
var results := {"events": [], "info": {}, "luminance": {}, "diff": {}}
var shot_count := 0
var finished := false


class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false


func _init() -> void:
	_run.call_deferred()
	create_timer(2400.0).timeout.connect(func(): _abort("harness watchdog"))


func _log(text: String) -> void:
	print("ECLIPSE_CAPTURE ", text)
	results["events"].append([Time.get_ticks_msec(), text])


func _run() -> void:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--out="): out_dir = a.trim_prefix("--out=")
	if out_dir == "":
		_abort("--out=<dir> is required"); return
	DirAccess.make_dir_recursive_absolute(out_dir)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	results["info"]["adapter"] = RenderingServer.get_video_adapter_name()
	fixture = "res://.godot/eclipse-capture/world-%d" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalEclipseCapture"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await _create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	celestial = game.get_node_or_null("RingDayNightCycle")
	weather = game.get_node_or_null("RingVolumetricClouds")
	atmosphere = game.get_node_or_null("DaySideAtmosphere")
	director = game.get_node_or_null("EclipseEvent")
	var drawer: Node = game.get_node_or_null("LiveTuningDrawer")
	if explorer == null or celestial == null or atmosphere == null or director == null or drawer == null:
		_abort("missing game nodes"); return
	for m in ["set_process_input", "set_process_unhandled_input", "set_process_unhandled_key_input", "set_process_shortcut_input"]: explorer.call(m, false)
	var waited := 0
	while waited < 900:
		if int(drawer.call("FindEmitterSite", "main")) >= 0: break
		waited += 1
		await physics_frame
	await _wait_global()
	base_pose = _pose_now()
	celestial.set("Paused", true)
	if weather != null: weather.set("CloudSimulationPaused", true)
	var gpu: Array = []
	OS.execute("nvidia-smi", ["--query-gpu=pstate,temperature.gpu,clocks.gr", "--format=csv,noheader"], gpu)
	results["info"]["gpu"] = gpu
	var poses := {"ground_horizon": _pose("ground_horizon"), "ground_up": _pose("ground_up"), "alt_down": _pose("alt_down")}
	# Find whole-ring totality (phase Darkness) and a late partial moment before it.
	director.call("JumpToPhase", PHASE_DARKNESS)
	await _frames(4)
	var t_totality := float(celestial.call("GetSimulationSeconds"))
	var t_partial := t_totality
	for i in 200:
		celestial.call("SetSimulationSeconds", t_totality - 5.0 * i)
		await process_frame
		if float(celestial.get("RawEclipseCoverage")) < 0.6:
			t_partial = t_totality - 5.0 * i
			break
	results["info"]["t_totality"] = t_totality
	results["info"]["t_partial"] = t_partial
	results["info"]["partial_coverage"] = float(celestial.get("RawEclipseCoverage"))
	# A clear day and a deep night (non-eclipse): advance from the totality time by half a ring day steps until daylight / night.
	var t_day := _find_time(t_totality, true)
	var t_night := _find_time(t_totality, false)
	results["info"]["t_day"] = t_day
	results["info"]["t_night"] = t_night
	var moments := {"day": t_day, "night": t_night, "partial": t_partial, "totality": t_totality}
	for pose_name in poses:
		await _go(poses[pose_name], 5.0)
		var images := {}
		for moment in moments:
			celestial.call("SetSimulationSeconds", float(moments[moment]))
			for enabled in [false, true]:
				_set_darkness(enabled)
				await _settle(3.0)
				var key := "%s_%s_%s" % [pose_name, moment, "on" if enabled else "off"]
				var img := await _shot_image()
				images[key] = img
				img.save_png(out_dir.path_join("%02d_%s.png" % [_next(), key]))
				results["luminance"][key] = _mean_luma(img)
				_log("%s luma=%.5f weight=%s" % [key, results["luminance"][key], str(atmosphere.get("eclipse_darkness_weight"))])
		for moment in ["day", "night"]:
			var a: Image = images["%s_%s_off" % [pose_name, moment]]
			var b: Image = images["%s_%s_on" % [pose_name, moment]]
			results["diff"]["%s_%s" % [pose_name, moment]] = _diff_pixels(a, b)
		_write()
	_set_darkness(true)
	_finish()


func _set_darkness(enabled: bool) -> void:
	# GameplayEclipseEventSettings is a C# Resource; build one through the script class and apply it to the director in memory only.
	var script: Script = load("res://scripts/ui/GameplayEclipseEventSettings.cs")
	var gameplay: Resource = script.new()
	gameplay.set("EclipseDarknessEnabled", enabled)
	director.call("Configure", gameplay)


func _find_time(from_t: float, want_day: bool) -> float:
	var t := from_t
	for i in 600:
		t += 450.0
		celestial.call("SetSimulationSeconds", t)
		var daylight := float(celestial.get("DaylightFactor"))
		var coverage := float(celestial.get("RawEclipseCoverage"))
		if coverage > 0.0: continue
		if want_day and daylight > 0.9: return t
		if (not want_day) and daylight < 0.02: return t
	return from_t


func _mean_luma(img: Image) -> float:
	var small := img.duplicate() as Image
	small.resize(160, 90, Image.INTERPOLATE_BILINEAR)
	var sum := 0.0
	for y in small.get_height():
		for x in small.get_width():
			var c := small.get_pixel(x, y)
			sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b
	return sum / float(small.get_width() * small.get_height())


func _diff_pixels(a: Image, b: Image) -> Dictionary:
	var width := mini(a.get_width(), b.get_width())
	var height := mini(a.get_height(), b.get_height())
	var differing := 0
	var max_channel := 0.0
	for y in range(0, height, 2):
		for x in range(0, width, 2):
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			var d := maxf(maxf(absf(ca.r - cb.r), absf(ca.g - cb.g)), absf(ca.b - cb.b))
			if d > 0.0: differing += 1
			max_channel = maxf(max_channel, d)
	return {"sampled": (width / 2) * (height / 2), "differing": differing, "max_channel_diff": max_channel}


func _next() -> int:
	shot_count += 1
	return shot_count


func _shot_image() -> Image:
	await RenderingServer.frame_post_draw
	return root.get_texture().get_image()


func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	selector.select(2); selector.emit_signal("item_selected", 2)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_abort("runtime surface preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", fixture.path_join("surface_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEmitterSiteDefaults"):
		tuning.call("InstallEmitterSiteDefaults", fixture.path_join("emitter_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEclipseDefaults"):
		tuning.call("InstallEclipseDefaults", fixture.path_join("eclipse_tuning.tres"))
	var mon: Node = next.get_node_or_null("RingPerformanceMonitor")
	if mon != null:
		var dst := fixture.path_join("telemetry.tres")
		mon.call("InstallSettingsPathForTests", dst)
		var d := fixture.path_join("reports")
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(d))
		mon.call("InstallReportDirectoryForTests", d)
	root.add_child(next); current_scene = next; launcher.queue_free()
	var deadline := Time.get_ticks_msec() + 600000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_abort("startup readiness deadline")
	return null


func _frames(n: int) -> void:
	for i in n: await process_frame


func _settle(seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end: await process_frame


func _wait_global() -> void:
	var global_lod: Node = game.get_node_or_null("GlobalTerrainLod3") if is_instance_valid(game) else null
	if global_lod == null: return
	var start := Time.get_ticks_msec()
	while int(global_lod.get("PendingInitialSegmentCount")) > 0 and Time.get_ticks_msec() - start < 180000:
		await process_frame


func _pose_now() -> Dictionary:
	var diag: Dictionary = surface.call("GetSurfaceDiagnostics") if surface != null else {}
	return {"around": float(diag.get("player_around", 0.0)), "vertical": float(diag.get("player_height", 0.0)),
		"across": float(diag.get("player_across", 0.0)), "yaw": rad_to_deg(float(explorer.get("SavedYaw"))),
		"pitch": float(explorer.get("SavedPitch")), "flight": bool(explorer.get("NoclipEnabled"))}


func _go(p: Dictionary, settle_s := 6.0) -> void:
	explorer.call("TeleportToCanonicalMeters", float(p["around"]), float(p["vertical"]), float(p["across"]), float(p["yaw"]), float(p["pitch"]), bool(p.get("flight", true)))
	await _frames(8)
	await _wait_global()
	await _settle(settle_s)


func _pose(name: String) -> Dictionary:
	var p := base_pose.duplicate()
	p["flight"] = true
	match name:
		"ground_horizon": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 2.0
		"ground_up": p["vertical"] = float(base_pose["vertical"]) + 2.0; p["pitch"] = 28.0
		"alt_down": p["vertical"] = float(base_pose["vertical"]) + 9000.0; p["pitch"] = -25.0
	return p


func _write() -> void:
	var f := FileAccess.open(out_dir.path_join("results.json"), FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(results, " "))
	f.close()


func _finish() -> void:
	if finished: return
	finished = true
	_write()
	print("ECLIPSE_CAPTURE=PASS")
	if is_instance_valid(game): game.queue_free()
	await process_frame
	await process_frame
	quit(0)


func _abort(reason: String) -> void:
	if finished: return
	finished = true
	_log("ABORT " + reason)
	_write()
	print("ECLIPSE_CAPTURE=FAIL ", reason)
	quit(1)
