extends SceneTree

## GPU capture: red sprites / blue jets over storms at a chosen distance on the near arch, at night (storm revamp S4 WO-E).
## Still mode (default) freezes the clock at each discharge's brightest moment and saves ON/OFF stills plus a luma difference:
##   --script res://tools/storm_sprites_distance_capture.gd -- --distance-km=150 --label=d150
## Video mode records a storm field with the discharge rate boosted (Movie Maker):
##   --write-movie .godot/captures/<label>/frame.png --fixed-fps 30 --resolution 1280x720 --script res://tools/storm_sprites_distance_capture.gd -- --mode=video --distance-km=450 --seconds=30
## Options (after --): --distance-km=N, --label=S, --mode=still|video, --altitude=M, --fov=DEG, --aim-vertical=M, --seconds=N,
## --rate=N (video chance multiplier), --emission=N, --reduced, --across-km (use the opposite arch distance instead: ignored when 0).
## Prints SPRITEDIST label,kind,distance_km,gain,transmittance,max_luma_delta,pixels_over_0.04 per still.
const WORLD_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const POLICY := preload("res://tools/UpperAtmosphereLightningTest.cs")
const OUT_DIR := "res://.godot/captures/sprites_distance"
var _world: Node3D
# Ring-up at the observer (the ring axis is world Y, so Vector3.UP would roll the picture 90 degrees).
var _up := Vector3.UP

func _arg(name: String, fallback: String) -> String:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--" + name + "="): return a.substr(name.length() + 3)
	return fallback

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("STORM_SPRITES_DISTANCE=FAIL RenderingDevice required")
		quit(1)
		return
	call_deferred("_run")

func _run() -> void:
	var video := _arg("mode", "still") == "video"
	var distance := float(_arg("distance-km", "150")) * 1000.0
	var label := _arg("label", "d%d" % int(distance / 1000.0))
	var fov := float(_arg("fov", "45"))
	var altitude := float(_arg("altitude", "1200"))
	var aim_vertical := float(_arg("aim-vertical", "45000"))
	_world = WORLD_SCENE.instantiate() as Node3D
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	if not launcher.is_node_ready(): await launcher.ready
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _world, 5000000.0)
	launcher.free()
	root.add_child(_world)
	var policy := POLICY.new()
	root.add_child(policy)
	policy.call("SetPolicy", 2.0 if video else 1.0, 1.0, "--reduced" in OS.get_cmdline_user_args())
	for _frame in range(20): await process_frame
	var weather := _world.get_node("RingVolumetricClouds")
	var far := _world.get_node("ScaledFarViewport")
	var grid := _world.get_node("ChunkGridManager")
	var camera := _world.get_node("Explorer/Head/Camera3D") as Camera3D
	var explorer := _world.get_node("Explorer")
	var clock := _world.get_node("RingDayNightCycle")
	var atmosphere := _world.get_node_or_null("DaySideAtmosphere")
	var bridge := _world.get_node_or_null("SunshineCloudBridge")
	var layer := root.find_child("UpperAtmosphereLightning", true, false)
	if layer == null: return _fail("sprite layer missing")
	var emission := float(_arg("emission", "-1"))
	if emission > 0.0: layer.set("BaseEmission", emission)
	layer.set("ChanceScaleOverride", float(_arg("rate", "6")) if video else 20.0)
	clock.set("Paused", true)
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var across := float(grid.get("RingAcrossWidthMeters")) * 0.5
	if bridge != null: bridge.call("SetBenchmarkViewerPosition", 0.0, altitude, across)
	explorer.call("SetCanonicalSpawnMeters", 0.0, altitude, across)
	explorer.call("SetPresentationView", 0.0, 50.0)
	for _place in range(60): await process_frame
	var night_time := -1.0
	for candidate in range(0, 4501, 75):
		clock.call("set_simulation_seconds", float(candidate))
		if float(clock.get("SunElevationDegrees")) <= -18.0 and float(clock.get("RawEclipseCoverage")) <= 0.000001:
			night_time = float(candidate)
			break
	if night_time < 0.0: return _fail("no astronomical night phase")
	clock.call("set_simulation_seconds", night_time)
	for _settle in range(20): await process_frame
	var target := grid.call("CanonicalRingPositionToWorld", Vector3(distance, aim_vertical, across)) as Vector3
	_up = ((grid.call("CanonicalRingPositionToWorld", Vector3(0.0, altitude + 1000.0, across)) as Vector3) - (grid.call("CanonicalRingPositionToWorld", Vector3(0.0, altitude, across)) as Vector3)).normalized()
	camera.look_at(target, _up)
	camera.fov = fov
	for _aim in range(20): await process_frame
	if atmosphere != null:
		atmosphere.call("_process", 10.0)
		for _a in range(4): await process_frame
	explorer.set_process(false)
	explorer.set_physics_process(false)
	weather.set_process(false)
	if atmosphere != null: atmosphere.set_process(false)
	var environment := _world.get_node("WorldEnvironment") as WorldEnvironment
	var attributes := environment.camera_attributes as CameraAttributesPractical
	if attributes != null and attributes.auto_exposure_enabled: attributes.auto_exposure_enabled = false
	for canvas: CanvasLayer in _world.find_children("*", "CanvasLayer", true, false):
		if canvas.layer > 0: canvas.hide()
	var storm_count := 4 if video else 1
	for i in range(storm_count):
		weather.call("register_manual_weather_at", "spritedist_%d" % i, distance + (i - 1.5) * 26000.0 * (1.0 if video else 0.0), across + (i % 2) * 9000.0 - 4500.0 * (1.0 if video else 0.0), 22000.0, -9301 - i)
	weather.set("CloudSimulationTimeSeconds", 100.0)
	for retry in range(180):
		far.call("_Process", 0.0)
		if not bool(far.get("FarWeatherAtlasRefreshInProgress")) and absf(float(far.get("FarWeatherAtlasSnapshotTimeSeconds")) - 100.0) <= 0.0001: break
		await process_frame
	for _frozen in range(30): await process_frame
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	if video:
		await _run_video(weather, layer, camera, target, fov, float(_arg("seconds", "30")), label)
	else:
		await _run_stills(weather, layer, policy, camera, target, fov, distance, across, label)
	quit(0)

func _run_video(weather: Node, layer: Node, camera: Camera3D, target: Vector3, fov: float, seconds: float, label: String) -> void:
	var frames := int(seconds * 30.0)
	var sprite_frames := 0
	var kinds := {}
	for f in range(frames):
		var t := 100.0 + float(f) / 30.0
		camera.look_at(target, _up)
		camera.fov = fov
		weather.set("CloudSimulationTimeSeconds", t)
		weather.call("_process", 0.0)
		var live := int(layer.get("LiveCount"))
		if live > 0:
			sprite_frames += 1
			var multi := (layer.get_child(0) as MultiMeshInstance3D).multimesh
			for k in range(live): kinds[int(multi.get_instance_custom_data(k).r)] = true
		print("SPRITECSV %d,%.4f,%d" % [f, t, live])
		await process_frame
	print("STORM_SPRITES_DISTANCE=DONE video label=%s frames=%d sprite_frames=%d kinds=%s" % [label, frames, sprite_frames, kinds.keys()])

func _luma(c: Color) -> float:
	return c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722

func _grab(name: String) -> Image:
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	image.save_png(ProjectSettings.globalize_path("%s/%s.png" % [OUT_DIR, name]))
	return image

func _run_stills(weather: Node, layer: Node, policy: Node, camera: Camera3D, target: Vector3, fov: float, distance: float, across: float, label: String) -> void:
	var wanted := {1: "sprite", 2: "jet"}
	var next_time := 200.0
	var id := -7000
	for kind in wanted.keys():
		# Preview event ids derive from the system id and time; probe system ids until the discharge is the wanted kind and the
		# preview is the only active event (a natural strike beside it would pollute the ON/OFF difference).
		var event: Dictionary = {}
		var tally := {}
		for attempt in range(400):
			next_time += 3.0
			weather.set("CloudSimulationTimeSeconds", next_time)
			weather.call("_process", 0.0)
			id -= 1
			event = weather.call("trigger_shared_lightning_preview_at", distance, across, id, 1.0, 22000.0)
			if event.is_empty(): continue
			var active: Array = weather.call("get_active_lightning_event_diagnostics")
			var derived := int(policy.call("KindForEventId", int(event["event_id"])))
			var key := "active%d_kind%d" % [active.size(), derived]
			tally[key] = int(tally.get(key, 0)) + 1
			if derived == kind: break
			event = {}
		if event.is_empty(): push_error("STORM_SPRITES_DISTANCE=FAIL no preview event of kind %s tally=%s" % [wanted[kind], tally]); return
		var start := float(event["start_time_seconds"]) + 0.04
		# Scan the discharge window for its brightest frame.
		var best_offset := 0.0
		var best_gain := -1.0
		var best_index := 0
		var offsets := [0.005, 0.015, 0.03, 0.045, 0.06, 0.08, 0.1, 0.13, 0.17]
		for offset in offsets:
			weather.set("CloudSimulationTimeSeconds", start + float(offset))
			camera.look_at(target, _up)
			camera.fov = fov
			for _s in range(3): await process_frame
			var multi := (layer.get_child(0) as MultiMeshInstance3D).multimesh
			for k in range(int(layer.get("LiveCount"))):
				var data := multi.get_instance_custom_data(k)
				if int(data.r) == kind and data.g > best_gain: best_gain = data.g; best_offset = offset; best_index = k
		if best_gain < 0.0: push_error("STORM_SPRITES_DISTANCE=FAIL no live discharge for %s" % wanted[kind]); return
		var transmittance := float(layer.call("TransmittanceOf", best_index))
		var at := start + best_offset
		weather.set("CloudSimulationTimeSeconds", at)
		for _s in range(6): await process_frame
		var on := await _grab("%s_%s_on" % [label, wanted[kind]])
		layer.set("Enabled", false)
		for _s in range(6): await process_frame
		var off := await _grab("%s_%s_off" % [label, wanted[kind]])
		layer.set("Enabled", true)
		var max_delta := 0.0
		var over := 0
		var sum_delta := 0.0
		for y in range(on.get_height()):
			for x in range(on.get_width()):
				var d := _luma(on.get_pixel(x, y)) - _luma(off.get_pixel(x, y))
				max_delta = maxf(max_delta, d)
				if d > 0.04: over += 1
				if d > 0.0: sum_delta += d
		print("SPRITEDIST %s,%s,%.0f,%.3f,%.3f,%.4f,%d,%.1f" % [label, wanted[kind], distance / 1000.0, best_gain, transmittance, max_delta, over, sum_delta])
	print("STORM_SPRITES_DISTANCE=DONE still label=%s" % label)

func _fail(message: String) -> void:
	push_error("STORM_SPRITES_DISTANCE=FAIL " + message)
	quit(1)
