extends SceneTree

## M1-8 full-game bore fixture (v5 world): the player spawns inside a bore, walks on the plug at P-200, the surface
## switch closes and reopens the hole, and the underground mode never leaves Surface. Optional `--capture=DIR` (run
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

func _run_site(label: String, info: Dictionary, full: bool) -> bool:
	var around := float(info["around"]); var across := float(info["across"])
	var pad := float(info["pad"]); var floor_h := float(info["floor"])
	var liner := float(info["liner_radius"])
	print("EMITTER_BORE_SITE ", label, " ", JSON.stringify(info))
	watch_holes = false
	# 1. Spawn inside the bore, 50 m above the pad: the player ends on the plug.
	explorer.call("SetCanonicalSpawnMeters", around + 20.0, pad + 50.0, across)
	explorer.call("SetPresentationView", 0.0, 0.0)
	var settled := 0
	var saw_hole := false
	while Time.get_ticks_msec() < deadline and settled < 40:
		if not await _step(): return false
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		saw_hole = saw_hole or _holes(diag) > 0
		var h := float(diag.get("player_height", NAN))
		var on_floor := bool(explorer.call("is_on_floor")) and absf(h - floor_h) <= 0.25
		settled = settled + 1 if on_floor and saw_hole else 0
	if settled < 40: _fail("%s: player did not settle on the plug (saw_hole=%s diag=%s)" % [label, saw_hole, surface.call("GetSurfaceDiagnostics")]); return false
	watch_holes = true
	var worst_error := 0.0
	var max_vertical := -1.0e9
	for i in 120:
		if not await _step(): return false
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		var h := float(diag.get("player_height", NAN))
		worst_error = maxf(worst_error, absf(h - floor_h))
		max_vertical = maxf(max_vertical, h)
	if worst_error > 0.1: _fail("%s: player height off the plug by %.3f m" % [label, worst_error]); return false
	if max_vertical > pad - 190.0: _fail("%s: vertical reached %.2f (limit P-190)" % [label, max_vertical - pad]); return false
	if not bool(explorer.call("is_on_floor")): _fail("%s: player not grounded on the plug" % label); return false
	summary.append("%s_plug_error=%.3f" % [label, worst_error])
	# 2. Collision height: plug inside the bore, field height outside.
	var inside := float(surface.call("SampleCollisionHeight", around + 20.0, across))
	if absf(inside - floor_h) > 0.001: _fail("%s: inside collision height %.3f != P-200" % [label, inside - pad]); return false
	var outside_c := float(surface.call("SampleCollisionHeight", around + liner + 14.0, across))
	var outside_f := float(surface.call("SampleHeight", around + liner + 14.0, across))
	if absf(outside_c - outside_f) > 0.05: _fail("%s: collision %.3f vs field %.3f outside the bore" % [label, outside_c, outside_f]); return false
	# 4. Probe: air above the plug, solid below.
	var air := float(probe.call("Density", surface, around + 20.0, pad - 100.0, across))
	var solid := float(probe.call("Density", surface, around + 20.0, pad - 201.0, across))
	if not (air > 0.0 and solid < 0.0): _fail("%s: probe air=%s solid=%s" % [label, air, solid]); return false
	if not _surface_mode(): return false
	if capture_dir != "": await _capture_poses(label, liner)
	if not full: watch_holes = false; return true
	# 5. Switch off: the bore stays while the committed window still has hole vertices, then everything releases.
	surface.call("SetSurfaceAperturesEnabled", false)
	var closed := false
	while Time.get_ticks_msec() < deadline:
		if not await _step(): return false
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		if _holes(diag) == 0 and not bool(diag.get("aperture_replacement_pending", true)): closed = true; break
	if not closed: _fail("hole never closed after switching off"); return false
	watch_holes = false
	if not await _wait_physics(10): return false
	var after: Dictionary = surface.call("GetSurfaceDiagnostics")
	if int(layer.get("ActiveBoreCount")) != 0: _fail("bores still active after the hole closed"); return false
	await _frames(3)
	var global_value: Vector4 = layer.call("GetPublishedCenter", 0)
	if global_value.w != 0.0: _fail("aperture global still published: %s" % global_value); return false
	if bool(after.get("surface_aperture_consumers_active", true)): _fail("consumers still active after close"); return false
	var player_h := float(after.get("player_height", NAN))
	var field_h := float(surface.call("SampleHeight", float(after["player_around"]), float(after["player_across"])))
	var collision_h := float(surface.call("SampleCollisionHeight", float(after["player_around"]), float(after["player_across"])))
	if absf(collision_h - field_h) > 0.05: _fail("collision height %.3f vs field %.3f after close" % [collision_h, field_h]); return false
	if absf(player_h - field_h) > 0.5 or player_h < pad - 5.0: _fail("player not lifted to the surface: %.3f (field %.3f, pad %.3f)" % [player_h, field_h, pad]); return false
	summary.append("lift_error=%.3f" % absf(player_h - field_h))
	if not _surface_mode(): return false
	# 6. Switch on: the hole returns and the player falls to the plug.
	surface.call("SetSurfaceAperturesEnabled", true)
	watch_holes = true
	var fell := 0
	var reopened := false
	while Time.get_ticks_msec() < deadline and fell < 40:
		if not await _step(): return false
		var diag: Dictionary = surface.call("GetSurfaceDiagnostics")
		reopened = reopened or _holes(diag) > 0
		var h := float(diag.get("player_height", NAN))
		fell = fell + 1 if reopened and bool(explorer.call("is_on_floor")) and absf(h - floor_h) <= 0.25 else 0
	if fell < 40: _fail("player did not fall back onto the plug (reopened=%s)" % reopened); return false
	watch_holes = false
	return true

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

func _capture_poses(label: String, liner: float) -> void:
	DirAccess.make_dir_recursive_absolute(capture_dir)
	var origin: Vector3 = explorer.global_position
	var up: Vector3 = explorer.global_basis.y.normalized()
	var tangent: Vector3 = up.cross(Vector3.UP).normalized()
	if tangent.length() < 0.1: tangent = up.cross(Vector3.RIGHT).normalized()
	var scale := liner / 76.0
	if camera == null:
		camera = Camera3D.new(); camera.far = 400000.0; camera.name = "BoreCaptureCamera"; game.add_child(camera)
	var rim := up * 200.0
	await _shot(label + "_plug_looking_up", origin + up * 1.7, origin + up * 1.7 + up * 150.0 + tangent * 15.0 * scale, tangent)
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
