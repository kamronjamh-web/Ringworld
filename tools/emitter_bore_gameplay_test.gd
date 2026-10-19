extends SceneTree

## M1-8 / M2-3 full-game bore fixture (v5 world): the plug is an open iris, so the player spawns over the bore and falls down the
## shaft (no lift back to the plug or surface); a forced window swap and the cavern neither snap them up nor clamp them; recovery
## returns them to the arrival overlook; the apertures switch removes the underground. Optional --capture=DIR (run
## windowed) saves pose PNGs at a regular and the main site.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PROBE := preload("res://tools/EmitterBoreGameplayProbe.cs")
var fixture := ""
var deadline := 0
var game: Node
var explorer: Node
var surface: Node
var layer: Node
var mode_node: Node
var probe: RefCounted
var failed := false
var capture_dir := ""
var camera: Camera3D
var watch_holes := false
var watch_mode := true
var frames_seen := 0
var hole_frames := 0
var summary: Array[String] = []

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

func _init() -> void:
	run.call_deferred()
	create_timer(1500.0).timeout.connect(func(): _fail("bore gameplay fixture timed out"))

func run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--capture="): capture_dir = argument.trim_prefix("--capture=")
	fixture = "res://.godot/terrain-generation-tests/bore-%s" % Time.get_ticks_usec()
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	var existing := root.get_node_or_null("WorldSession")
	if existing != null: existing.name = "WorldSessionOriginalBore"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	deadline = Time.get_ticks_msec() + 1450000
	probe = PROBE.new()
	game = await _create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	layer = game.get_node_or_null("EmitterBores")
	mode_node = game.get_node_or_null("UndergroundMode")
	if explorer == null or surface == null or layer == null or mode_node == null: _fail("missing explorer, gameplay, bore layer or underground mode"); return
	explorer.set("EnableSwimming", false)
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	var regular: Dictionary = await _find_site(false)
	var main: Dictionary = await _find_site(true)
	if regular.is_empty() or main.is_empty(): _fail("bore layer never bound or no sites found"); return
	if not await _run_site("regular", regular, true): return
	if not await _run_site("main", main, false): return
	print("EMITTER_BORE_GAMEPLAY=PASS frames=%d hole_frames=%d %s" % [frames_seen, hole_frames, " ".join(summary)])
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
	for name in ["BiomeDecorations", "StandingGrass", "RingVolumetricClouds", "LocalVolumetricCloudRenderer", "LocalWeatherEffects", "WeatherAudio", "WeatherAmbience", "WaterFftController", "TerrainMinimap", "CertificationMonitor"]:
		var node: Node = next.get_node_or_null(name)
		if node != null: node.process_mode = Node.PROCESS_MODE_DISABLED
	root.add_child(next); current_scene = next; launcher.queue_free()
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate: Node = next.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")): return next
	_fail("startup readiness deadline")
	return null

func _find_site(want_main: bool) -> Dictionary:
	var waited := 0
	while waited < 600 and Time.get_ticks_msec() < deadline:
		if not (layer.call("FindNearestBoreSite", 1000000.0, 100000.0) as Dictionary).is_empty(): break
		waited += 1
		await physics_frame
	if not want_main:
		# A regular site away from the centre line and the seam.
		for around in [1000000.0, 5000000.0, 12000000.0]:
			var info: Dictionary = layer.call("FindNearestBoreSite", around, 80000.0)
			if not info.is_empty() and not bool(info["is_main"]): return info
		return {}
	var scan := 0.0
	while scan < 31415936.0:
		var info: Dictionary = layer.call("FindNearestBoreSite", scan, 159000.0)
		if not info.is_empty() and bool(info["is_main"]): return info
		scan += 20000.0
	return {}

func _canon(info: Dictionary, lx: float, ly: float, lz: float) -> Array:
	# Asset-local metres to canonical [around, vertical, across] (EmitterSiteNavigator.AssetLocalToCanonical).
	var yaw := float(info["yaw_rad"]); var c := cos(yaw); var s := sin(yaw)
	var fx := c * lx + s * lz; var fz := -s * lx + c * lz
	return [float(info["origin_around"]) + fx, float(info["datum"]) + ly, float(info["origin_across"]) - fz]

func _diag() -> Dictionary:
	return surface.call("GetSurfaceDiagnostics")

func _height() -> float:
	return float(_diag().get("player_height", NAN))

func _check(condition: bool, message: String) -> bool:
	if not condition: _fail(message)
	return condition

func _run_site(label: String, info: Dictionary, full: bool) -> bool:
	var around := float(info["around"]); var across := float(info["across"])
	var pad := float(info["pad"]); var datum := float(info["datum"])
	var liner := float(info["liner_radius"])
	var is_main := bool(info["is_main"])
	var floor_local := -6000.0 if is_main else -5000.0
	print("EMITTER_BORE_SITE ", label, " ", JSON.stringify(info))
	watch_holes = false
	watch_mode = false  # Underground mode thresholds are M2-6; the shaft and cavern may latch it for now.
	surface.set("UndergroundRecoveryEnabled", false)
	# 1. Spawn over the bore centre at P+50: the plug is open, so the player falls past -200 and nothing lifts them back.
	explorer.call("SetCanonicalSpawnMeters", around + 20.0, pad + 50.0, across)
	explorer.call("SetPresentationView", 0.0, 0.0)
	var saw_hole := false
	var reached := false
	var prev := NAN
	var frames := 0
	while Time.get_ticks_msec() < deadline and frames < 2400:
		if not await _step(): return false
		frames += 1
		var diag := _diag()
		saw_hole = saw_hole or _holes(diag) > 0
		var h := float(diag.get("player_height", NAN))
		if saw_hole and not is_nan(prev) and h - prev > 5.0:
			_fail("%s: upward jump %.2f m while falling (h=%.2f prev=%.2f)" % [label, h - prev, h - pad, prev - pad]); return false
		if saw_hole: prev = h
		if saw_hole and h < pad - 300.0: reached = true; break
	if not reached: _fail("%s: player did not fall past P-300 (saw_hole=%s h=%s diag=%s)" % [label, saw_hole, _height() - pad, _diag()]); return false
	watch_holes = true
	summary.append("%s_fall_frames=%d" % [label, frames])
	if capture_dir != "": await _capture_poses(label, liner, pad)
	# 2. A window swap forced while the player is in the shaft must not lift them to the plug or the surface.
	if full:
		var rev0 := int(_diag().get("committed_revision", 0))
		var h_before := _height()
		surface.call("SetSurfaceAperturesEnabled", false)
		surface.call("SetSurfaceAperturesEnabled", true)
		var swapped := false
		prev = h_before
		var guard := 0
		while Time.get_ticks_msec() < deadline and guard < 2400:
			if not await _step(): return false
			guard += 1
			var diag := _diag()
			var h := float(diag.get("player_height", NAN))
			if h - prev > 5.0: _fail("%s: window swap lifted the player by %.2f m (h=%.2f)" % [label, h - prev, h - pad]); return false
			prev = h
			if int(diag.get("committed_revision", 0)) > rev0 and not bool(diag.get("aperture_replacement_pending", true)): swapped = true; break
		if not _check(swapped, "%s: forced window swap never committed" % label): return false
		if not _check(_height() < h_before + 1.0, "%s: height %.2f after the swap vs %.2f before" % [label, _height() - pad, h_before - pad]): return false
		summary.append("swap_frames=%d" % guard)
	# 3. The bore column above the iris no longer reports P-200: the ground is the true cavern floor.
	var inside := float(surface.call("SampleCollisionHeight", around + 20.0, across))
	if not _check(absf(inside - (datum + floor_local)) < 1.0 and absf(inside - (pad - 200.0)) > 100.0,
			"%s: bore column collision height %.3f is not the cavern floor %.3f (P-200 = %.3f)" % [label, inside, datum + floor_local, pad - 200.0]): return false
	var below := float(surface.call("SampleGroundHeightAt", around + 20.0, pad - 1000.0, across))
	if not _check(absf(below - (datum + floor_local)) < 1.0, "%s: ground below the shaft %.3f != cavern floor" % [label, below]): return false
	var outside_c := float(surface.call("SampleCollisionHeight", around + liner + 14.0, across))
	var outside_f := float(surface.call("SampleHeight", around + liner + 14.0, across))
	if not _check(absf(outside_c - outside_f) <= 0.05, "%s: collision %.3f vs field %.3f outside the bore" % [label, outside_c, outside_f]): return false
	var in_shaft := float(probe.call("Density", surface, around + 20.0, pad - 100.0, across))
	var deep_air := float(probe.call("Density", surface, around + 20.0, pad - 1000.0, across))
	if not _check(in_shaft > 0.0 and deep_air > 0.0, "%s: probe shaft air=%s/%s" % [label, in_shaft, deep_air]): return false
	if not _check(bool(surface.call("IsInsideSiteUnderground", around + 20.0, pad - 1000.0, across)), "%s: IsInsideSiteUnderground false in the shaft" % label): return false
	summary.append("%s_column_floor=%.1f" % [label, inside - pad])
	if not full: watch_holes = false; return true

	# 4. Switch off: the committed hole closes, the underground disappears and heights are normal again; switch on restores it.
	watch_holes = false
	surface.call("SetSurfaceAperturesEnabled", false)
	var closed := false
	while Time.get_ticks_msec() < deadline:
		if not await _step(): return false
		var diag := _diag()
		if _holes(diag) == 0 and not bool(diag.get("aperture_replacement_pending", true)): closed = true; break
	if not _check(closed, "hole never closed after switching off"): return false
	if not await _wait_physics(10): return false
	var after := _diag()
	if not _check(int(layer.get("ActiveBoreCount")) == 0, "bores still active after the hole closed"): return false
	await _frames(3)
	var global_value: Vector4 = layer.call("GetPublishedCenter", 0)
	if not _check(global_value.w == 0.0, "aperture global still published: %s" % global_value): return false
	if not _check(not bool(after.get("surface_aperture_consumers_active", true)), "consumers still active after close"): return false
	var off_c := float(surface.call("SampleCollisionHeight", around + 20.0, across))
	var off_f := float(surface.call("SampleHeight", around + 20.0, across))
	if not _check(absf(off_c - off_f) <= 0.05, "collision height %.3f vs field %.3f after close" % [off_c, off_f]): return false
	if not _check(not bool(surface.call("IsInsideSiteUnderground", around + 20.0, pad - 1000.0, across)), "underground still reported with the apertures switched off"): return false
	var off_solid := float(probe.call("Density", surface, around + 20.0, pad - 1000.0, across))
	if not _check(off_solid < 0.0, "probe %s at the old shaft with the apertures off" % off_solid): return false
	surface.call("SetSurfaceAperturesEnabled", true)
	var reopened := false
	var waited := 0
	while Time.get_ticks_msec() < deadline and waited < 3000:
		if not await _step(): return false
		waited += 1
		var diag := _diag()
		if _holes(diag) > 0 and not bool(diag.get("aperture_replacement_pending", true)): reopened = true; break
	if not _check(reopened, "hole did not reopen after switching on"): return false
	if not await _wait_physics(20): return false
	if not _check(bool(surface.call("IsInsideSiteUnderground", around + 20.0, pad - 1000.0, across)), "underground not restored after switching on"): return false
	watch_holes = true

	# 5. Recovery disabled: a player below the cavern floor keeps falling; nothing teleports them.
	var deep := _canon(info, 0.0, floor_local - 100.0, 0.0)
	explorer.call("TeleportToCanonicalMeters", deep[0], deep[1], deep[2], 0.0, 0.0, false)
	if not await _wait_physics(90): return false
	var count0 := int(surface.get("UndergroundRecoveryCount"))
	if not _check(count0 == 0, "recovery fired while disabled (count=%d)" % count0): return false
	if not _check(_height() <= float(deep[1]) + 1.0 and _height() < datum + floor_local - 20.0, "player was moved while recovery was disabled (h=%.2f)" % (_height() - datum)): return false
	summary.append("no_recovery_while_off=ok")

	# 6. Recovery enabled: the player returns to the arrival overlook (hovering in flight until the M2-4 reactor floor exists).
	surface.set("UndergroundRecoveryEnabled", true)
	var recovered := false
	waited = 0
	while Time.get_ticks_msec() < deadline and waited < 240:
		if not await _step(): return false
		waited += 1
		if int(surface.get("UndergroundRecoveryCount")) >= 1: recovered = true; break
	if not _check(recovered, "recovery never fired below the cavern floor (h=%.2f)" % (_height() - datum)): return false
	if not await _wait_physics(5): return false
	var overlook := _canon(info, 0.0, -4000.0 + 80.0, 1385.0)
	var rd := _diag()
	var err_h := Vector2(float(rd["player_around"]) - float(overlook[0]), float(rd["player_across"]) - float(overlook[2])).length()
	var err_v := absf(float(rd["player_height"]) - float(overlook[1]))
	if not _check(err_h < 3.0 and err_v < 3.0, "recovery pose off the overlook by %.2f m horizontally, %.2f m vertically" % [err_h, err_v]): return false
	if not _check(int(surface.get("LastUndergroundRecoverySite")) == int(info["site"]), "recovery site mismatch"): return false
	if not _check(bool(explorer.get("NoclipEnabled")), "recovery hover should be flight until M2-4"): return false
	summary.append("recovery_err=%.2f" % maxf(err_h, err_v))
	if not await _wait_physics(30): return false
	if not _check(int(surface.get("UndergroundRecoveryCount")) == 1, "recovery fired again after arriving at the overlook"): return false

	# 7. Cavern centre in noclip: nothing snaps the player up, the probe reads air inside and rock 50 m outside the wall.
	var centre := _canon(info, 0.0, -4000.0, 0.0)
	explorer.call("TeleportToCanonicalMeters", centre[0], centre[1], centre[2], 0.0, 0.0, true)
	if not await _wait_physics(2): return false
	var h0 := _height()
	var worst_rise := 0.0
	for i in 300:
		if not await _step(): return false
		worst_rise = maxf(worst_rise, _height() - h0)
	if not _check(worst_rise < 0.5, "player snapped up %.2f m in the cavern" % worst_rise): return false
	if not _check(bool(surface.call("IsInsideSiteUnderground", centre[0], centre[1], centre[2])), "cavern centre not underground"): return false
	var air := float(probe.call("Density", surface, centre[0], centre[1], centre[2]))
	var wall := _canon(info, 1240.0 + 50.0, -4000.0, 0.0)
	var rock := float(probe.call("Density", surface, wall[0], wall[1], wall[2]))
	if not _check(air > 0.0 and rock < 0.0, "probe cavern air=%s, 50 m outside the wall=%s" % [air, rock]): return false
	if not _check(not bool(surface.call("IsInsideSiteUnderground", wall[0], wall[1], wall[2])), "rock outside the wall reported as underground"): return false
	summary.append("cavern_rise=%.3f" % worst_rise)

	# 8. Grounded physics in the cavern, 600 m from the bore (far outside the surface window): walking is not clamped or frozen.
	surface.set("UndergroundRecoveryEnabled", false)
	var far := _canon(info, 600.0, -4000.0, 0.0)
	explorer.call("TeleportToCanonicalMeters", far[0], far[1], far[2], 0.0, 0.0, false)
	if not await _wait_physics(5): return false
	var clamp: Vector3 = probe.call("ConstrainedVelocity", surface, explorer, Vector3(50.0, 0.0, 50.0), 1.0 / 60.0)
	if not _check(clamp.is_equal_approx(Vector3(50.0, 0.0, 50.0)), "velocity clamp active in the cavern: %s" % clamp): return false
	var start := _diag()
	_key(KEY_W, true)
	var worst_up := 0.0
	prev = float(start["player_height"])
	for i in 120:
		if not await _step():
			_key(KEY_W, false)
			return false
		var h := _height()
		worst_up = maxf(worst_up, h - prev)
		prev = h
	_key(KEY_W, false)
	var end := _diag()
	var moved := Vector2(float(end["player_around"]) - float(start["player_around"]), float(end["player_across"]) - float(start["player_across"])).length()
	if not _check(moved > 4.0, "grounded walk in the cavern moved only %.2f m (clamped)" % moved): return false
	if not _check(worst_up < 0.5, "player snapped up %.2f m while walking in the cavern" % worst_up): return false
	summary.append("cavern_walk=%.1f" % moved)
	surface.set("UndergroundRecoveryEnabled", true)

	# 9. Recovery never fires for a player standing on the ground beside the spire (inside the volume's horizontal reach).
	var count1 := int(surface.get("UndergroundRecoveryCount"))
	var entry := _canon(info, 88.782555, 0.0, 214.34006)
	explorer.call("TeleportToCanonicalMeters", entry[0], entry[1] + 2.0, entry[2], 0.0, 0.0, false)
	if not await _wait_physics(180): return false
	var stand := _diag()
	if not _check(int(surface.get("UndergroundRecoveryCount")) == count1, "recovery fired for a player standing beside the spire"): return false
	if not _check(absf(float(stand["player_height"]) - pad) < 40.0 and bool(explorer.call("is_on_floor")), "player not standing on the ground beside the spire (h=%.2f)" % (float(stand["player_height"]) - pad)): return false
	return true

func _key(code: Key, pressed: bool) -> void:
	var event := InputEventKey.new()
	event.keycode = code; event.physical_keycode = code; event.pressed = pressed
	Input.parse_input_event(event)

func _holes(diag: Dictionary) -> int:
	var native: Dictionary = diag.get("native", {})
	return int(native.get("hole_vertices", 0))

func _surface_mode() -> bool:
	if int(mode_node.get("Mode")) != 0:
		_fail("underground mode left Surface (mode=%s depth=%s)" % [mode_node.get("Mode"), mode_node.get("DepthMeters")]); return false
	return true

func _step() -> bool:
	await physics_frame
	frames_seen += 1
	if failed: return false
	if watch_mode and int(mode_node.get("Mode")) != 0:
		_fail("underground mode left Surface during the run (mode=%s depth=%s)" % [mode_node.get("Mode"), mode_node.get("DepthMeters")]); return false
	if watch_holes:
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		if _holes(diag) > 0:
			hole_frames += 1
			var collision := bool(layer.call("IsSlotCollisionEnabled", 0)) or bool(layer.call("IsSlotCollisionEnabled", 1))
			if int(layer.get("ActiveBoreCount")) < 1 or not collision:
				_fail("hole without a bore floor (holes=%d bores=%s collision=%s)" % [_holes(diag), layer.get("ActiveBoreCount"), collision]); return false
	return true

func _wait_physics(count: int) -> bool:
	for i in count:
		if not await _step(): return false
	return true


func _capture_poses(label: String, liner: float, pad: float) -> void:
	DirAccess.make_dir_recursive_absolute(capture_dir)
	var up: Vector3 = explorer.global_basis.y.normalized()
	# Origin: the open iris rim on the bore axis (200 m below the pad), from the player's own height.
	var origin: Vector3 = explorer.global_position + up * (pad - 200.0 - _height())
	var tangent: Vector3 = up.cross(Vector3.UP).normalized()
	if tangent.length() < 0.1: tangent = up.cross(Vector3.RIGHT).normalized()
	var scale := liner / 76.0
	if camera == null:
		camera = Camera3D.new(); camera.far = 400000.0; camera.name = "BoreCaptureCamera"; game.add_child(camera)
	var rim := up * 200.0
	await _shot(label + "_iris_looking_up", origin + up * 1.7, origin + up * 1.7 + up * 150.0 + tangent * 15.0 * scale, tangent)
	await _shot(label + "_throat_down", origin + rim + up * 25.0 * scale, origin, tangent)
	await _shot(label + "_rim_oblique", origin + rim + up * 40.0 * scale + tangent * 120.0 * scale, origin + rim - up * 60.0, up)
	await _shot(label + "_altitude_2km", origin + rim + up * 2000.0 + tangent * 500.0, origin + rim, tangent)
	var player_camera: Camera3D = explorer.get_node_or_null("Head/Camera3D")
	if player_camera != null: player_camera.make_current()

func _shot(shot_name: String, from: Vector3, target: Vector3, up_hint: Vector3) -> void:
	camera.global_position = from
	camera.look_at(target, up_hint)
	camera.make_current()
	await _frames(12)
	var image := root.get_texture().get_image()
	var path := capture_dir.path_join(shot_name + ".png")
	image.save_png(path)
	print("EMITTER_BORE_CAPTURE ", path)

func _frames(count: int) -> void:
	for ignored in count: await process_frame

func _fail(message: String) -> void:
	if failed: return
	failed = true
	if is_instance_valid(surface): print("EMITTER_BORE_FAILURE_STATE=", JSON.stringify(surface.call("GetSurfaceDiagnostics")))
	push_error("EMITTER_BORE_GAMEPLAY=FAIL " + message); quit(1)
