extends SceneTree
## River and lake site stills (docs/work-orders/terrain-vegetation/T2.md, T2R-6c and T2R-17). TEST ONLY; run it through tools/river_site_capture.ps1 (isolated APPDATA, GPU queue).
## It refuses to run on the real roaming profile. Loads a NEW certified world with a seed through the real loading screen (generation 1 plus the water.rivers review feature), then visits
## the places of a JSON list and saves one still per view: noon at the place, clocks held, weather pinned dry, cloud quality Off (the stills document ground and water), HUD hidden,
## a caption with the coordinates drawn into the frame. Nothing in the world is changed.
##   --seed=N (876374626)   --rivers=0|1 (1)   --out=<absolute dir> (required)   --places=<absolute json file> (required)   --size=1600x900 (saved size)
##   --arrival-timeout=240 --view-timeout=150   seconds to wait for streaming to go idle at a place and after each view change (the still is taken either way; index.json records "idle")
##   --index=index.json   name of the index file (so several runs can share one folder)
##   --clouds=off|keep (off)   keep leaves the saved cloud quality on: the view a player has with default settings
##   --counters=0|1 (0)   prints, after each still, the proxy sector node counts per level and the geometry nodes under the world (the engine gives each such node a block of the
##                        shader instance-uniform buffer), and at an idle timeout what is still busy
## a view may carry "variants": extra stills <name>_<tag>_<variant>.png after the plain one. "clouds": the saved cloud quality; "watermask": the water SSR debug view, so water
## surfaces draw black; "nosunshadow": the sun shadow map off (the water surface casts into it); "levels": one flat colour per proxy level (2 m red, 4 m orange, 8 m yellow, 16 m green, 32 m cyan, 64 m blue, coarser magenta), seen through the water.
## a view may carry "time": {"mode": "noon"|"fraction"|"elevation", ...} (tools/shadow_harness_common.gd apply_time): applied right before that view's still, and noon is restored after the view and its variants;
## the sun elevation is recorded as "sun_elevation_deg" in the view's index entry.
## a view may carry "ambience": true: after its plain still the pose is held 4 s and the WaterAmbience node's diagnostics are stored in the view's index entry ("ambience": sounding_count, playing_count,
## masker_db, finder_ms, node_ms, source_count, proximity and the playing voices with layer, kind, distance, level_db, mix_gain, cutoff_hz) and printed as one "RIVER_SITE_CAPTURE ambience" line.
## places file: [{"name": "d1_stem9943", "caption": "...", "around_km": 23735.336, "across_km": 208.712, "ground_m": 398 (optional: probed when absent),
##                "views": [{"tag": "top600", "lift_m": 600, "yaw": 0, "pitch": -89}, ...]}]
## Prints RIVER_SITE_CAPTURE=PASS|FAIL and writes index.json beside the stills.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const COMMON := preload("res://tools/shadow_harness_common.gd")
const LANDSCAPE := preload("res://tools/landscape_harness_common.gd")
const LOAD_TIMEOUT_MS := 600000

var _args := {}
var _loader: Node
var _game: Node
var _failure := ""
var _out := ""
var _label: Label
var _saved_cloud_quality := 0
var _saved_sun_shadows := true
const LEVEL_COLORS := {"Lod1Terrain2m": Color(1, 0, 0), "Lod2Terrain4m": Color(1, 0.5, 0), "Lod3Terrain8m": Color(1, 1, 0), "Lod4Terrain16m": Color(0, 1, 0), "Lod5Terrain32m": Color(0, 1, 1), "Lod6Terrain64m": Color(0, 0, 1)}


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			if eq > 0:
				_args[text.substr(2, eq - 2)] = text.substr(eq + 1)
			else:
				_args[text.substr(2)] = "1"
	call_deferred("_run")


func _streaming_idle() -> bool:
	var coordinator := _game.get_node_or_null("TerrainLodCoordinator")
	if coordinator != null and not bool(coordinator.get("StreamingSettled")):
		return false
	return COMMON.builds_idle(_game)


func _wait_idle(timeout_s: float, stable_frames: int) -> Dictionary:
	var start := Time.get_ticks_msec()
	var stable := 0
	while (Time.get_ticks_msec() - start) * 0.001 < timeout_s:
		await process_frame
		if _streaming_idle():
			stable += 1
			if stable >= stable_frames:
				return {"idle": true, "seconds": (Time.get_ticks_msec() - start) * 0.001}
		else:
			stable = 0
	return {"idle": false, "seconds": (Time.get_ticks_msec() - start) * 0.001}


func _proxy_levels() -> Array:
	var levels: Array = []
	for child in _game.get_children():
		if child.has_method("SetDebugColor") and child.get("CreatedSectorNodeCount") != null:
			levels.append(child)
	return levels


## Diagnostic variants of a still (see the header). `on` applies, false restores.
func _apply_variant(variant: String, on: bool) -> float:
	match variant:
		"clouds":
			var clouds := _game.get_node_or_null("RingVolumetricClouds")
			if clouds != null:
				clouds.call("SetQuality", _saved_cloud_quality if on else 0)
			return 12.0 if on else 3.0
		"watermask":
			var quality := _game.get_node_or_null("GraphicsQuality")
			if quality != null:
				quality.set("WaterSsrDebugMode", 2 if on else 0)
				quality.call("ApplyWaterTuning")
			return 1.5
		"levels":
			for level in _proxy_levels():
				level.call("SetDebugColor", on, LEVEL_COLORS.get(String(level.name), Color(1, 0, 1)))
			return 1.5
		"nosunshadow":
			var shadows := _game.get_node_or_null("GraphicsQuality")
			if shadows != null:
				if on:
					_saved_sun_shadows = bool(shadows.get("RequestedSunShadowsEnabled"))
				shadows.call("SetShadowTuningOverride", "Enabled", false if on else _saved_sun_shadows)
			return 3.0
	return 0.0


## Holds the pose 4 s, then reads the WaterAmbience diagnostics (see the header).
func _read_ambience(place_name: String, tag: String) -> Dictionary:
	for i in 240:
		await process_frame
	var node := _game.get_node_or_null("WaterAmbience")
	if node == null or not node.has_method("GetWaterAmbienceDiagnostics"):
		print("RIVER_SITE_CAPTURE ambience place=%s view=%s error=no WaterAmbience" % [place_name, tag])
		return {"error": "no WaterAmbience"}
	var diagnostics: Dictionary = node.call("GetWaterAmbienceDiagnostics")
	var voices: Array = []
	var texts: Array = []
	for voice_value in diagnostics.get("voices", []):
		var voice: Dictionary = voice_value
		if not bool(voice.get("playing", false)):
			continue
		voices.append({"layer": str(voice.get("layer", "")), "kind": str(voice.get("kind", "")), "distance": float(voice.get("distance", NAN)), "level_db": float(voice.get("level_db", NAN)),
			"mix_gain": float(voice.get("mix_gain", NAN)), "cutoff_hz": float(voice.get("cutoff_hz", NAN))})
		texts.append("%s:%.0f m:%.1f dB" % [str(voice.get("kind", "")), float(voice.get("distance", NAN)), float(voice.get("level_db", NAN))])
	var result := {"sounding_count": int(diagnostics.get("sounding_count", 0)), "playing_count": int(diagnostics.get("playing_count", 0)), "masker_db": float(diagnostics.get("masker_db", NAN)),
		"finder_ms": float(diagnostics.get("finder_ms", NAN)), "node_ms": float(diagnostics.get("node_ms", NAN)), "source_count": int(diagnostics.get("source_count", 0)),
		"proximity": float(diagnostics.get("proximity", NAN)), "voices": voices}
	print("RIVER_SITE_CAPTURE ambience place=%s view=%s sounding=%d voices=%s finder_ms=%.3f node_ms=%.3f" % [place_name, tag, int(result["sounding_count"]), ", ".join(texts), float(result["finder_ms"]), float(result["node_ms"])])
	return result


func _print_counters(where: String) -> void:
	var total_nodes := 0
	var lines: Array = []
	for level in _proxy_levels():
		var nodes := 0
		var stack: Array = [level]
		while not stack.is_empty():
			var node: Node = stack.pop_back()
			if node is GeometryInstance3D and node != level:
				nodes += 1
			for child in node.get_children():
				stack.append(child)
		total_nodes += nodes
		lines.append("%s active=%d warm=%d pooled=%d created=%d nodes=%d busy=%s" % [level.name, int(level.get("ActiveSectorCount")), int(level.get("WarmSectorCount")), int(level.get("PooledSectorCount")),
			int(level.get("CreatedSectorNodeCount")), nodes, str(bool(level.get("StreamingActive")) and (int(level.get("ActiveWorkerSlots")) > 0 or int(level.get("PendingUploadCount")) > 0 or bool(level.get("CoordinatorBuildRequested"))))])
	# A geometry node holds a block of the instance-uniform buffer from the first time it has an instance shader parameter until it is freed; the engine lists those parameters as properties.
	var by_owner := {}
	var all_geometry := 0
	var all_holders := 0
	for child in _game.get_children():
		var count := 0
		var holders := 0
		var stack: Array = [child]
		while not stack.is_empty():
			var node: Node = stack.pop_back()
			if node is GeometryInstance3D:
				count += 1
				for property in node.get_property_list():
					if String(property["name"]).begins_with("instance_shader_parameters/"):
						holders += 1
						break
			for grandchild in node.get_children():
				stack.append(grandchild)
		all_geometry += count
		all_holders += holders
		if count >= 20:
			by_owner[String(child.name)] = "%d/%d" % [holders, count]
	print("RIVER_SITE_CAPTURE counters at %s: proxy sector nodes=%d, geometry nodes under the world=%d, with instance shader parameters=%d (buffer holds %d blocks), by owner (with/all)=%s, builds_idle=%s, fps=%.0f" % [where, total_nodes, all_geometry, all_holders,
		int(ProjectSettings.get_setting("rendering/limits/global_shader_variables/buffer_size", 65536)) / 16, str(by_owner), str(COMMON.builds_idle(_game)), Performance.get_monitor(Performance.TIME_FPS)])
	for line in lines:
		print("RIVER_SITE_CAPTURE counters   " + line)
	var surface := _game.get_node_or_null("Terrain3DGameplay")
	var grass := _game.get_node_or_null("StandingGrass")
	var trees := _game.get_node_or_null("BiomeDecorations")
	var global_lod := _game.get_node_or_null("GlobalTerrainLod3")
	var coordinator := _game.get_node_or_null("TerrainLodCoordinator")
	print("RIVER_SITE_CAPTURE counters   pending: surface=%s grass builds=%s uploads=%s tree tiles=%s global segments=%s mesh memory pressure=%s" % [
		str((surface.call("GetSurfaceDiagnostics") as Dictionary).get("streaming", {})) if surface != null else "-",
		str(grass.get("PendingGrassTileBuildCount")) if grass != null else "-", str(grass.get("PendingGrassUploadCount")) if grass != null else "-",
		str(trees.get("VegetationPendingTiles")) if trees != null else "-", str(global_lod.get("PendingInitialSegmentCount")) if global_lod != null else "-",
		str(coordinator.get("MeshMemoryPressureActive")) if coordinator != null else "-"])


func _hide_hud() -> void:
	var stack: Array = [_game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is CanvasLayer:
			(node as CanvasLayer).visible = false
			continue
		for child in node.get_children():
			stack.append(child)


func _make_label() -> void:
	var layer := CanvasLayer.new()
	layer.layer = 120
	root.add_child(layer)
	var panel := ColorRect.new()
	panel.color = Color(0, 0, 0, 0.55)
	panel.position = Vector2(0, 0)
	panel.size = Vector2(root.size.x, 58)
	layer.add_child(panel)
	_label = Label.new()
	_label.position = Vector2(18, 8)
	_label.add_theme_font_size_override("font_size", 30)
	_label.add_theme_color_override("font_color", Color(1, 1, 1))
	layer.add_child(_label)


func _run() -> void:
	_out = String(_args.get("out", ""))
	var places_path := String(_args.get("places", ""))
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if _out.is_empty() or places_path.is_empty() or user_dir.begins_with(real_roaming):
		push_error("RIVER_SITE_CAPTURE=FAIL needs --out, --places and an isolated APPDATA (run tools/river_site_capture.ps1)")
		quit(2)
		return
	var places_file := FileAccess.open(places_path, FileAccess.READ)
	var places: Variant = JSON.parse_string(places_file.get_as_text()) if places_file != null else null
	if not places is Array or (places as Array).is_empty():
		push_error("RIVER_SITE_CAPTURE=FAIL cannot read the places list %s" % places_path)
		quit(2)
		return
	DirAccess.make_dir_recursive_absolute(_out)
	var save_size := Vector2i(1600, 900)
	var size_parts := String(_args.get("size", "1600x900")).split("x")
	if size_parts.size() == 2:
		save_size = Vector2i(int(size_parts[0]), int(size_parts[1]))
	var rivers := String(_args.get("rivers", "1")) == "1"
	var seed_value := float(_args.get("seed", "876374626"))
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	DisplayServer.window_set_position(Vector2i(0, 0))
	DisplayServer.window_set_size(Vector2i(2560, 1440))
	# No focus: keyboard input typed elsewhere must not reach the game.
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, true)
	for i in 4:
		await process_frame
	var session := root.get_node_or_null("WorldSession")
	if session == null or not session.select_slot(1):
		push_error("RIVER_SITE_CAPTURE=FAIL isolated world slot unavailable")
		quit(3)
		return
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	current_scene = launcher
	launcher.call("_on_certified_preset_pressed")
	launcher.get("terrain_backend_selector").select(2)
	launcher.call("_on_terrain_backend_selected", 2)
	(launcher.get("terrain_seed_spin") as SpinBox).value = seed_value
	launcher.set("_selected_world_generation", 1)
	launcher.set("_selected_review_features", PackedStringArray(["water.rivers"]) if rivers else PackedStringArray())
	launcher.call("_start_game")
	_loader = root.get_node_or_null("RingWorldLoading")
	if _loader == null:
		push_error("RIVER_SITE_CAPTURE=FAIL loading screen did not appear")
		quit(1)
		return
	_loader.world_ready.connect(func(world: Node): _game = world)
	_loader.loading_failed.connect(func(message: String): _failure = message)
	var deadline := Time.get_ticks_msec() + LOAD_TIMEOUT_MS
	while _game == null and _failure.is_empty() and Time.get_ticks_msec() < deadline:
		await process_frame
	if _game == null:
		push_error("RIVER_SITE_CAPTURE=FAIL " + (_failure if not _failure.is_empty() else "load timed out"))
		quit(4)
		return
	print("RIVER_SITE_CAPTURE world ready, window %dx%d" % [root.size.x, root.size.y])
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	var explorer := _game.get_node("Explorer")
	var monitor := _game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	var drawer_node := _game.get_node_or_null("LiveTuningDrawer")
	if drawer_node != null and drawer_node.has_method("SetBenchmarkDrawerOpen"):
		drawer_node.call("SetBenchmarkDrawerOpen", false)
	COMMON.hold_world_clocks(_game)
	# Cloud quality Off: no volumetric clouds, no mesh cloud shell and no rain curtains, so a top-down still from above the cloud base shows the ground.
	# (Switching only the volumetric renderer off hands the sky to the far cloud shell, which then covers the view.)
	var weather_layer := _game.get_node_or_null("RingVolumetricClouds")
	var counters := String(_args.get("counters", "0")) == "1"
	if weather_layer != null and weather_layer.has_method("SetQuality"):
		_saved_cloud_quality = int(weather_layer.get("Quality"))
		if String(_args.get("clouds", "off")) != "keep":
			weather_layer.call("SetQuality", 0)
	_hide_hud()
	_make_label()

	var index: Array = []
	var failed := 0
	for place_variant in places:
		var place: Dictionary = place_variant
		var name := str(place["name"])
		var around := float(place["around_km"]) * 1000.0
		var across := float(place["across_km"]) * 1000.0
		var views: Array = place.get("views", [])
		var record := {"name": name, "caption": str(place.get("caption", "")), "around_km": place["around_km"], "across_km": place["across_km"], "views": []}
		_label.text = "moving to %s" % name
		var ground := float(place.get("ground_m", NAN))
		explorer.call("TeleportToCanonicalMeters", around, (ground if is_finite(ground) else 0.0) + 6000.0, across, 0.0, -89.0, true)
		await COMMON.settle(self, 3.0)
		if not is_finite(ground):
			var probe_alt := float(explorer.get("CurrentAltitudeAboveTerrainMeters"))
			ground = 6000.0 - probe_alt if is_finite(probe_alt) and probe_alt > 0.0 and probe_alt < 12000.0 else 0.0
			record["ground_probed"] = true
		record["ground_m"] = ground
		var first_lift := float((views[0] as Dictionary).get("lift_m", 600.0)) if not views.is_empty() else 600.0
		explorer.call("TeleportToCanonicalMeters", around, ground + first_lift, across, 0.0, -89.0, true)
		var idle: Dictionary = await _wait_idle(float(_args.get("arrival-timeout", "240")), 90)
		record["arrival_idle"] = idle
		var time_state: Dictionary = COMMON.apply_time(_game, {"mode": "noon"})
		record["sun_elevation_deg"] = time_state.get("sun_elevation_deg", NAN)
		var weather: Dictionary = LANDSCAPE.pin_weather(_game, [{"name": name, "around_m": around, "across_m": across}])
		record["weather"] = {"error": weather["error"]} if weather.has("error") else {"cloud_time": weather.get("cloud_time", NAN), "searched": weather.get("searched", 0)}
		print("RIVER_SITE_CAPTURE place=%s ground=%.1f idle=%s after %.0f s sun=%.1f weather=%s" % [name, ground, idle["idle"], idle["seconds"], float(record["sun_elevation_deg"]), str(record["weather"])])
		if counters:
			_print_counters("arrival at " + name)
		for view_variant in views:
			var view: Dictionary = view_variant
			var tag := str(view["tag"])
			var lift := float(view.get("lift_m", 600.0))
			var yaw := float(view.get("yaw", 0.0))
			var pitch := float(view.get("pitch", -89.0))
			var offset_around := float(view.get("offset_around_m", 0.0))
			var offset_across := float(view.get("offset_across_m", 0.0))
			explorer.call("TeleportToCanonicalMeters", around + offset_around, ground + lift, across + offset_across, yaw, pitch, true)
			var view_time: Dictionary = {}
			if view.has("time"):
				view_time = COMMON.apply_time(_game, view["time"])
			await COMMON.settle(self, 1.0)
			var view_idle: Dictionary = await _wait_idle(float(_args.get("view-timeout", "150")), 60)
			if drawer_node != null and drawer_node.has_method("SetBenchmarkDrawerOpen"):
				drawer_node.call("SetBenchmarkDrawerOpen", false)
			_label.text = "%s   ARC %.3f km  ACROSS %.3f km   %.0f m above %.0f m   yaw %.0f pitch %.0f   seed %d" % [str(place.get("caption", name)), (around + offset_around) / 1000.0, (across + offset_across) / 1000.0, lift, ground, yaw, pitch, int(seed_value)]
			if view.has("motion"):
				# "motion": {"seconds": 10, "fps": 30, "to_lift_m": 600, "to_offset_around_m": 0, "to_offset_across_m": 60}: the camera moves linearly from the view to this pose, one teleport per frame (a flicker or
				# fly-through review: every frame of the move is saved as <out>/<name>_<tag>_frames/frame%08d.png at 1280x720, the sequence tools/media/ffmpeg_tools.ps1 reads).
				var motion: Dictionary = view["motion"]
				var frames := maxi(2, int(float(motion.get("seconds", 10.0)) * float(motion.get("fps", 30.0))))
				var to_lift := float(motion.get("to_lift_m", lift))
				var to_around := float(motion.get("to_offset_around_m", offset_around))
				var to_across := float(motion.get("to_offset_across_m", offset_across))
				var frames_dir := _out.path_join("%s_%s_frames" % [name, tag])
				DirAccess.make_dir_recursive_absolute(frames_dir)
				print("RIVER_SITE_CAPTURE motion %s_%s: %d frames to %s" % [name, tag, frames + 1, frames_dir])
				for i in frames + 1:
					var u := float(i) / float(frames)
					explorer.call("TeleportToCanonicalMeters", around + lerpf(offset_around, to_around, u), ground + lerpf(lift, to_lift, u), across + lerpf(offset_across, to_across, u), yaw, pitch, true)
					await process_frame
					await RenderingServer.frame_post_draw
					var frame_image := root.get_texture().get_image()
					frame_image.resize(1280, 720, Image.INTERPOLATE_BILINEAR)
					frame_image.save_png(frames_dir.path_join("frame%08d.png" % i))
			await COMMON.settle(self, 2.5)
			await RenderingServer.frame_post_draw
			var image := root.get_texture().get_image()
			image.resize(save_size.x, save_size.y, Image.INTERPOLATE_LANCZOS)
			var file := "%s_%s.png" % [name, tag]
			var error := image.save_png(_out.path_join(file))
			if error != OK:
				failed += 1
			var view_entry := {"tag": tag, "file": file, "lift_m": lift, "yaw": yaw, "pitch": pitch, "idle": view_idle, "altitude_above_terrain_m": float(explorer.get("CurrentAltitudeAboveTerrainMeters"))}
			if view.has("time"):
				view_entry["sun_elevation_deg"] = view_time.get("sun_elevation_deg", NAN)
			(record["views"] as Array).append(view_entry)
			print("RIVER_SITE_CAPTURE still=%s idle=%s after %.0f s" % [file, view_idle["idle"], view_idle["seconds"]])
			if bool(view.get("ambience", false)):
				view_entry["ambience"] = await _read_ambience(name, tag)
			for variant_value in view.get("variants", []):
				var variant := str(variant_value)
				await COMMON.settle(self, _apply_variant(variant, true))
				await RenderingServer.frame_post_draw
				var variant_image := root.get_texture().get_image()
				variant_image.resize(save_size.x, save_size.y, Image.INTERPOLATE_LANCZOS)
				var variant_file := "%s_%s_%s.png" % [name, tag, variant]
				if variant_image.save_png(_out.path_join(variant_file)) != OK:
					failed += 1
				await COMMON.settle(self, _apply_variant(variant, false))
				print("RIVER_SITE_CAPTURE still=%s" % variant_file)
			if view.has("time"):
				COMMON.apply_time(_game, {"mode": "noon"})
			if counters:
				_print_counters(file)
		index.append(record)
		var out_file := FileAccess.open(_out.path_join(String(_args.get("index", "index.json"))), FileAccess.WRITE)
		if out_file != null:
			out_file.store_string(JSON.stringify(index, "  "))
			out_file.close()
	await _shutdown()
	if failed > 0:
		push_error("RIVER_SITE_CAPTURE=FAIL %d still(s) could not be saved" % failed)
		quit(1)
		return
	print("RIVER_SITE_CAPTURE=PASS places=%d" % index.size())
	quit(0)


func _shutdown() -> void:
	var surface: Node = _game.get_node_or_null("Terrain3DGameplay")
	var saves: Node = _game.get_node_or_null("WorldSaveManager")
	if saves != null:
		saves.call("CancelWorldLoad")
	if is_instance_valid(surface):
		surface.call("CancelStartup", "Capture complete.")
	var owners: Array[Node] = []
	for child: Node in _game.get_children():
		if child.has_method("BeginLoadingShutdown"):
			child.call("BeginLoadingShutdown")
			owners.append(child)
	_game.get_node("TerrainLodCoordinator").set_process(false)
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline:
		var drained := not is_instance_valid(surface) or bool(surface.get("StartupCancellationReady"))
		for owner: Node in owners:
			drained = drained and bool(owner.get("LoadingShutdownReady"))
		if drained:
			break
		await process_frame
	_game.queue_free()
	await process_frame
	await process_frame
