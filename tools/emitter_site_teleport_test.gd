extends SceneTree

## M1-9/10 full-game emitter site teleports (v5 world). Entry from far away, repeat stability, bore escape, stale-water regression,
## main entry, High and Far viewpoints, galleries. Optional `--capture=DIR` (run windowed, add `--full` to keep clouds and water
## controllers enabled) saves a view PNG for every viewpoint at a land site, an island site and the main site.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var fixture := ""
var deadline := 0
var game: Node
var explorer: Node
var surface: Node
var layer: Node
var streamer: Node
var drawer: Node
var mode_node: Node
var failed := false
var capture_dir := ""
var full := false
var frames_seen := 0
var summary: Array[String] = []

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(2400.0).timeout.connect(func(): _fail("teleport fixture timed out"))

func run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--capture="): capture_dir = argument.trim_prefix("--capture=")
		if argument == "--full": full = true
	fixture = "res://.godot/terrain-generation-tests/teleport-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalTeleport"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	deadline = Time.get_ticks_msec() + 2300000
	game = await _create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	layer = game.get_node_or_null("EmitterBores")
	streamer = game.get_node_or_null("EmitterSites")
	drawer = game.get_node_or_null("LiveTuningDrawer")
	mode_node = game.get_node_or_null("UndergroundMode")
	if explorer == null or surface == null or layer == null or streamer == null or drawer == null or mode_node == null: _fail("missing explorer, gameplay, bore layer, streamer, drawer or underground mode"); return
	explorer.set("EnableSwimming", true)
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	var waited := 0
	while waited < 900 and Time.get_ticks_msec() < deadline:
		if int(drawer.call("FindEmitterSite", "main")) >= 0 and not (layer.call("FindNearestBoreSite", 1000000.0, 100000.0) as Dictionary).is_empty(): break
		waited += 1
		await physics_frame
	var main_site := int(drawer.call("FindEmitterSite", "main"))
	if main_site < 0: _fail("emitter navigation never bound (needs a v5 world)"); return
	var start_around := float((surface.call("GetSurfaceDiagnostics") as Dictionary).get("player_around", 0.0))
	var land: Dictionary = _find_site("land", start_around + 5000000.0)
	var island: Dictionary = _find_site("island", start_around + 9000000.0)
	var main_info: Dictionary = _find_main()
	if land.is_empty() or island.is_empty() or main_info.is_empty(): _fail("could not find a land, island and main site (land=%s island=%s main=%s)" % [not land.is_empty(), not island.is_empty(), not main_info.is_empty()]); return
	print("EMITTER_TELEPORT_SITES land=%d island=%d main=%d" % [int(land["site"]), int(island["site"]), main_site])
	var far_m := absf(float(land["around"]) - start_around)
	far_m = minf(far_m, 31415936.0 - far_m)
	if far_m < 1000000.0: _fail("land site is not far enough from the start"); return

	# 1. Regular entry from more than 1,000 km away.
	if not await _teleport_entry("land entry", land, 3): return
	# 2. Repeat: away and back; same result, slot pool and orphan count stable.
	var orphans_before := int(Performance.get_monitor(Performance.OBJECT_ORPHAN_NODE_COUNT))
	if not await _teleport_entry("island entry", island, 3): return
	if not await _teleport_entry("land entry again", land, 3): return
	if int(streamer.get("SlotCount")) != 3: _fail("slot count %s != 3 after repeat" % streamer.get("SlotCount")); return
	var orphans_after := int(Performance.get_monitor(Performance.OBJECT_ORPHAN_NODE_COUNT))
	if orphans_after > orphans_before + 2: _fail("orphan nodes grew %d -> %d" % [orphans_before, orphans_after]); return
	summary.append("orphans=%d->%d" % [orphans_before, orphans_after])
	# 3. Bore escape.
	if not await _bore_escape(land): return
	# 4. Stale water.
	if not await _stale_water(island, land): return
	# 5. Main site entry.
	if not await _teleport_entry("main entry", main_info, 3): return
	# 6/7. Viewpoints at the land site, the island site and the main site.
	for entry in [["land", land], ["island", island], ["main", main_info]]:
		if not await _viewpoints(entry[0], entry[1]): return
	print("EMITTER_SITE_TELEPORT=PASS frames=%d %s" % [frames_seen, " ".join(summary)])
	game.queue_free(); await process_frame; quit(0)

func _create_game() -> Node:
	var launcher := LAUNCHER.instantiate(); root.add_child(launcher); await _frames(2)
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null: _fail("launcher lacks terrain backend selector"); return null
	selector.select(2); selector.emit_signal("item_selected", 2)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("runtime surface definition preparation failed"); return null
	launcher.call("_save_selected_slot_settings")
	var next := GAME.instantiate(); launcher.call("_configure_game", next, 5000000.0)
	var tuning: Node = next.get_node_or_null("LiveTuningDrawer")
	if tuning != null and tuning.has_method("InstallSurfaceTerrainDefaults"):
		tuning.call("InstallSurfaceTerrainDefaults", fixture.path_join("surface_tuning.tres"))
	if tuning != null and tuning.has_method("InstallEmitterSiteDefaults"):
		tuning.call("InstallEmitterSiteDefaults", fixture.path_join("emitter_tuning.tres"))
	var disabled := ["BiomeDecorations", "StandingGrass", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "TerrainMinimap", "CertificationMonitor"]
	if not full: disabled.append_array(["RingVolumetricClouds", "LocalVolumetricCloudRenderer", "WaterFftController"])
	for name in disabled:
		var node: Node = next.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_fail("startup readiness deadline")
	return null

## A site of the wanted class near the scan point (information from the bore layer, class from the drawer hook).
func _find_site(class_name_wanted: String, scan_start: float) -> Dictionary:
	var scan := scan_start
	for i in 400:
		var info: Dictionary = layer.call("FindNearestBoreSite", fposmod(scan, 31415936.0), 30000.0 + float(i % 7) * 40000.0)
		if not info.is_empty() and not bool(info["is_main"]) and str(drawer.call("EmitterSiteClassName", int(info["site"]))) == class_name_wanted:
			if class_name_wanted == "island" or float(info["pad"]) >= 3.0:
				return info
		scan += 61000.0
	return {}

func _find_main() -> Dictionary:
	var scan := 0.0
	while scan < 31415936.0:
		var info: Dictionary = layer.call("FindNearestBoreSite", scan, 159000.0)
		if not info.is_empty() and bool(info["is_main"]): return info
		scan += 20000.0
	return {}

func _diag() -> Dictionary: return surface.call("GetSurfaceDiagnostics")

func _wait_arrival(label: String, timeout_s: float = 120.0) -> bool:
	var start := Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < int(timeout_s * 1000.0) and Time.get_ticks_msec() < deadline:
		if not await _step(): return false
		var state := str(drawer.get("EmitterTeleportState"))
		if state == "Arrived":
			print("EMITTER_TELEPORT %s: %s (wall %.1f s)" % [label, drawer.get("EmitterTeleportMessage"), (Time.get_ticks_msec() - start) / 1000.0])
			return true
		if state == "Failed" or state == "TimedOut":
			_fail("%s: %s: %s" % [label, state, drawer.get("EmitterTeleportMessage")]); return false
	_fail("%s: no arrival within %d s (%s)" % [label, int(timeout_s), drawer.get("EmitterTeleportMessage")])
	return false

func _teleport_entry(label: String, info: Dictionary, repeat_frames: int) -> bool:
	var site := int(info["site"])
	if not bool(drawer.call("RequestEmitterTeleport", site, 0)): _fail("%s: request refused: %s" % [label, drawer.get("EmitterTeleportMessage")]); return false
	if not await _wait_arrival(label): return false
	return await _check_on_pad(label, info, 40)

func _check_on_pad(label: String, info: Dictionary, frames: int) -> bool:
	var site := int(info["site"])
	var pad := float(info["pad"])
	var worst := 0.0
	var worst_align := 1.0
	var landing := 0
	while landing < 180 and not bool(explorer.call("is_on_floor")):
		landing += 1
		if not await _step(): return false
	for i in frames:
		if not await _step(): return false
		var diag := _diag()
		var h := float(diag.get("player_height", NAN))
		var ground := float(surface.call("SampleCollisionHeight", float(diag["player_around"]), float(diag["player_across"])))
		worst = maxf(worst, absf(h - ground))
		if not bool(explorer.call("is_on_floor")): _fail("%s: not on the floor at frame %d (h=%.2f ground=%.2f)" % [label, i, h, ground]); return false
		if absf(h - ground) > 0.25: _fail("%s: height %.3f vs collision %.3f" % [label, h, ground]); return false
		if h < pad - 1.0: _fail("%s: height %.2f below pad %.2f" % [label, h, pad]); return false
		var slot := int(streamer.call("FindSlotForSite", site))
		if slot < 0 or not bool(streamer.call("IsSlotCollisionEnabled", slot)): _fail("%s: site %d slot %d / collision missing" % [label, site, slot]); return false
		if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")): _fail("%s: underwater or swimming" % label); return false
		if int(mode_node.get("Mode")) != 0: _fail("%s: underground mode left Surface" % label); return false
		if bool(explorer.get("NoclipEnabled")): _fail("%s: noclip is on" % label); return false
		var align := float(drawer.call("MeasureEmitterViewAlignment", true))
		worst_align = minf(worst_align, align)
		if not (align > 0.99): _fail("%s: horizontal alignment %.4f" % [label, align]); return false
	summary.append("%s_err=%.3f/align=%.4f" % [label.replace(" ", "_"), worst, worst_align])
	if capture_dir != "": await _capture("%s_entry" % label.replace(" ", "_"))
	return true

func _bore_escape(info: Dictionary) -> bool:
	var around := float(info["around"]); var across := float(info["across"])
	var pad := float(info["pad"]); var floor_h := float(info["floor"])
	explorer.call("SetCanonicalSpawnMeters", around + 20.0, pad + 50.0, across)
	explorer.call("SetPresentationView", 0.0, 0.0)
	var settled := 0
	var start := Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < 180000 and settled < 40:
		if not await _step(): return false
		var h := float(_diag().get("player_height", NAN))
		settled = settled + 1 if bool(explorer.call("is_on_floor")) and absf(h - floor_h) <= 0.25 else 0
	if settled < 40: _fail("bore escape: never settled on the plug"); return false
	if not bool(drawer.call("RequestEmitterTeleport", int(info["site"]), 0)): _fail("bore escape: request refused"); return false
	if not await _wait_arrival("bore escape"): return false
	return await _check_on_pad("bore escape", info, 40)

func _stale_water(island: Dictionary, land: Dictionary) -> bool:
	var site_around := float(island["around"]); var site_across := float(island["across"])
	var found := false
	var water_a := 0.0; var water_x := 0.0
	for radius in [1500.0, 2500.0, 3500.0, 5000.0, 7000.0]:
		for k in 8:
			var angle := float(k) * TAU / 8.0
			var a: float = site_around + radius * cos(angle); var x: float = site_across + radius * sin(angle)
			if x < 100.0 or x > 317000.0: continue
			if float(surface.call("SampleHeight", a, x)) < -30.0: found = true; water_a = a; water_x = x; break
		if found: break
	if not found: _fail("stale water: no deep water near the island site"); return false
	if not bool(explorer.call("TeleportToCanonicalMeters", water_a, -10.0, water_x, 0.0, 0.0, true)): _fail("stale water: flight teleport failed"); return false
	var saw_underwater := false
	for i in 120:
		if not await _step(): return false
		if bool(explorer.get("IsCameraUnderwater")): saw_underwater = true; break
	if not saw_underwater: _fail("stale water: never became underwater 10 m below sea at %.0f/%.0f" % [water_a, water_x]); return false
	if not bool(drawer.call("RequestEmitterTeleport", int(land["site"]), 0)): _fail("stale water: entry request refused"); return false
	if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")): _fail("stale water: still underwater immediately after the teleport request"); return false
	# The very next frame and the whole hold must be dry.
	var start := Time.get_ticks_msec()
	var frames := 0
	while Time.get_ticks_msec() - start < 120000:
		await physics_frame
		frames += 1
		if failed: return false
		if bool(explorer.get("IsCameraUnderwater")) or bool(explorer.get("IsSwimming")):
			_fail("stale water: still underwater %d frames after the teleport" % frames); return false
		var state := str(drawer.get("EmitterTeleportState"))
		if state == "Arrived": break
		if state == "Failed" or state == "TimedOut": _fail("stale water: %s: %s" % [state, drawer.get("EmitterTeleportMessage")]); return false
	if str(drawer.get("EmitterTeleportState")) != "Arrived": _fail("stale water: entry never arrived"); return false
	summary.append("stale_water_dry_frames=%d" % frames)
	return await _check_on_pad("stale water entry", land, 20)

func _viewpoints(label: String, info: Dictionary) -> bool:
	var site := int(info["site"])
	var pad := float(info["pad"])
	# Gallery: collision on, a downward ray hits within 2 m.
	if not bool(drawer.call("RequestEmitterTeleport", site, 1)): _fail("%s gallery: request refused" % label); return false
	if not await _wait_arrival("%s gallery" % label): return false
	await _frames(10)
	var up: Vector3 = (explorer as Node3D).global_basis.y.normalized()
	var origin: Vector3 = (explorer as Node3D).global_position
	var space: PhysicsDirectSpaceState3D = (explorer as Node3D).get_world_3d().direct_space_state
	var query := PhysicsRayQueryParameters3D.create(origin + up * 0.5, origin - up * 2.0)
	query.exclude = [(explorer as CollisionObject3D).get_rid()]
	var hit := space.intersect_ray(query)
	if hit.is_empty(): _fail("%s gallery: no floor within 2 m below the player" % label); return false
	var align_gallery := float(drawer.call("MeasureEmitterViewAlignment", false))
	if not (align_gallery > 0.99): _fail("%s gallery: alignment %.4f" % [label, align_gallery]); return false
	if not bool(explorer.get("NoclipEnabled")): _fail("%s gallery: expected flight mode" % label); return false
	summary.append("%s_gallery_floor=%.2f" % [label, origin.distance_to(hit["position"])])
	if capture_dir != "": await _capture("%s_gallery" % label)
	# High: vertical pinned at P + altitude for 120 frames, noclip, aligned.
	if not bool(drawer.call("RequestEmitterTeleport", site, 3)): _fail("%s high: request refused" % label); return false
	if not await _wait_arrival("%s high" % label): return false
	var worst := 0.0
	for i in 120:
		if not await _step(): return false
		var h := float(_diag().get("player_height", NAN))
		worst = maxf(worst, absf(h - (pad + 150000.0)))
		if worst > 1.0: _fail("%s high: vertical %.2f vs %.2f" % [label, h, pad + 150000.0]); return false
		if not bool(explorer.get("NoclipEnabled")): _fail("%s high: not in flight mode" % label); return false
	var align_high := float(drawer.call("MeasureEmitterViewAlignment", false))
	if not (align_high > 0.99): _fail("%s high: alignment %.4f" % [label, align_high]); return false
	summary.append("%s_high_err=%.3f" % [label, worst])
	if capture_dir != "": await _capture("%s_high" % label, 400)
	# Far: grounded on dry land or hovering, aligned.
	if not bool(drawer.call("RequestEmitterTeleport", site, 2)): _fail("%s far: request refused" % label); return false
	if not await _wait_arrival("%s far" % label): return false
	var hover := bool(explorer.get("NoclipEnabled"))
	var landing := 0
	while not hover and landing < 180 and not bool(explorer.call("is_on_floor")):
		landing += 1
		if not await _step(): return false
	var far_error := 0.0
	for i in 40:
		if not await _step(): return false
		var diag := _diag()
		var h := float(diag.get("player_height", NAN))
		var ground := float(surface.call("SampleCollisionHeight", float(diag["player_around"]), float(diag["player_across"])))
		if hover:
			if h < maxf(ground, 0.0) + 195.0: _fail("%s far hover: %.1f m is below ground %.1f + 200" % [label, h, ground]); return false
		else:
			far_error = maxf(far_error, absf(h - ground))
			if not bool(explorer.call("is_on_floor")) or absf(h - ground) > 0.25: _fail("%s far: not grounded (h=%.2f ground=%.2f)" % [label, h, ground]); return false
			if ground < 2.0: _fail("%s far: grounded on %.2f m ground (not dry land)" % [label, ground]); return false
	var align_far := float(drawer.call("MeasureEmitterViewAlignment", false))
	if not (align_far > 0.99): _fail("%s far: alignment %.4f" % [label, align_far]); return false
	summary.append("%s_far=%s/err=%.3f/align=%.4f" % [label, "hover" if hover else "grounded", far_error, align_far])
	if capture_dir != "": await _capture("%s_far" % label, 120)
	return true

func _capture(shot_name: String, settle_frames: int = 45) -> void:
	DirAccess.make_dir_recursive_absolute(capture_dir)
	await _frames(settle_frames)
	# Captures must not show the staged global terrain build (hull gating covers it, but wait anyway).
	var global_lod: Node = game.get_node_or_null("GlobalTerrainLod3") if is_instance_valid(game) else null
	if global_lod != null:
		var wait_start := Time.get_ticks_msec()
		while int(global_lod.get("PendingInitialSegmentCount")) > 0 and Time.get_ticks_msec() - wait_start < 180000:
			await process_frame
		await _frames(10)
	var image := root.get_texture().get_image()
	var path := capture_dir.path_join(shot_name + ".png")
	image.save_png(path)
	print("EMITTER_TELEPORT_CAPTURE ", path)

func _step() -> bool:
	await physics_frame
	frames_seen += 1
	return not failed

func _frames(count: int) -> void:
	for ignored in count: await process_frame

func _fail(message: String) -> void:
	if failed: return
	failed = true
	if is_instance_valid(surface): print("EMITTER_TELEPORT_FAILURE_STATE=", JSON.stringify(surface.call("GetSurfaceDiagnostics")))
	push_error("EMITTER_SITE_TELEPORT=FAIL " + message); quit(1)
