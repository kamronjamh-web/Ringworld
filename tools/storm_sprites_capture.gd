extends SceneTree

## GPU capture: a night storm seen across the arch with red sprites / blue jets (storm revamp S4 WO-E).
## Run windowed with Movie Maker, e.g.
##   --write-movie .godot/captures/storm_sprites/frame.png --fixed-fps 30 --resolution 1280x720 --script res://tools/storm_sprites_capture.gd -- --seconds=24
## Options (after --): --reduced (Reduced flashes on), --no-sprites (upper-atmosphere chance 0, same timeline), --seconds=N, --aim-vertical=M.
## Prints SPRITECSV frame,time,live_sprites,kinds,max_pulse per frame so luminance steps can be checked next to ffmpeg flicker.
const WORLD_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const POLICY := preload("res://tools/UpperAtmosphereLightningTest.cs")
var _world: Node3D

func _arg(name: String, fallback: String) -> String:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--" + name + "="): return a.substr(name.length() + 3)
	return fallback

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("STORM_SPRITES_CAPTURE=FAIL RenderingDevice required")
		quit(1)
		return
	call_deferred("_run")

func _run() -> void:
	var reduced := "--reduced" in OS.get_cmdline_user_args()
	var no_sprites := "--no-sprites" in OS.get_cmdline_user_args()
	var seconds := float(_arg("seconds", "24"))
	# 0.5 is the exact antipode, where a discharge rises straight toward the camera and is seen end-on; 0.33 shows its profile.
	var around_frac := float(_arg("around-frac", "0.33"))
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
	policy.call("SetPolicy", 1.0, 1.0, reduced)
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
	if "--bright" in OS.get_cmdline_user_args(): layer.set("BaseEmission", 400.0)
	layer.set("ChanceScaleOverride", 0.0 if no_sprites else 20.0)
	clock.set("Paused", true)
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var across := float(grid.get("RingAcrossWidthMeters")) * 0.5
	if bridge != null: bridge.call("SetBenchmarkViewerPosition", 0.0, 1200.0, across)
	# Stand on the near side of the arch at a modest altitude (the default launcher spawn is far outside the ring).
	explorer.call("SetCanonicalSpawnMeters", 0.0, float(_arg("altitude", "1200")), across)
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
	var aim_vertical := float(_arg("aim-vertical", "30000"))
	var target := grid.call("CanonicalRingPositionToWorld", Vector3(circumference * around_frac, aim_vertical, across)) as Vector3
	camera.look_at(target, Vector3.UP)
	# The opposite arch is a thin band ~1 degree tall at this distance; a narrow field of view makes the storms and discharges readable.
	camera.fov = float(_arg("fov", "10"))
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
	# Four stationary storms spread across the aimed patch of the opposite arch.
	for i in range(4):
		weather.call("register_manual_weather_at", "sprites_%d" % i, circumference * around_frac + (i - 1.5) * 24000.0, across + (i % 2) * 9000.0 - 4500.0, 22000.0, -9301 - i)
	weather.set("CloudSimulationTimeSeconds", 100.0)
	for retry in range(180):
		far.call("_Process", 0.0)
		if not bool(far.get("FarWeatherAtlasRefreshInProgress")) and absf(float(far.get("FarWeatherAtlasSnapshotTimeSeconds")) - 100.0) <= 0.0001: break
		await process_frame
	for _frozen in range(30): await process_frame
	var frames := int(seconds * 30.0)
	var lit_frames := 0
	var sprite_frames := 0
	for f in range(frames):
		var t := 100.0 + float(f) / 30.0
		camera.look_at(target, Vector3.UP)
		camera.fov = float(_arg("fov", "10"))
		weather.set("CloudSimulationTimeSeconds", t)
		weather.call("_process", 0.0)
		var max_pulse := 0.0
		for e in weather.call("get_active_lightning_event_diagnostics"):
			max_pulse = maxf(max_pulse, float(e["pulse"]))
			if f < 60: print("SPRITEEVENT f=%d id=%d system=%d kind=%d around_frac=%.4f across=%.0f preview=%s" % [f, int(e["event_id"]), int(e["system_id"]), int(e["system_kind"]), float(e["cloud_around_meters"]) / circumference, float(e["cloud_across_meters"]), e["is_preview"]])
		var live := int(layer.get("LiveCount"))
		if live > 0: sprite_frames += 1
		if max_pulse > 0.02: lit_frames += 1
		if f == 22:
			var inst := layer.get_child(0) as MultiMeshInstance3D
			print("SPRITEDIAG parent=%s viewport=%s far_vp=%s inst_visible=%s in_tree=%s visible_count=%d inst_count=%d first_xf=%s custom0=%s mat=%s layer_visible=%s" % [layer.get_parent().get_path(), layer.get_viewport().get_path() if layer.get_viewport() != null else "none", (far.get_node("FarRingViewport") as SubViewport).get_path(), inst.is_visible_in_tree(), inst.is_inside_tree(), inst.multimesh.visible_instance_count, inst.multimesh.instance_count, inst.multimesh.get_instance_transform(0), inst.multimesh.get_instance_custom_data(0), inst.material_override, (layer as Node3D).is_visible_in_tree()])
		if f == 20 or f == 21:
			var far_viewport := far.get_node("FarRingViewport") as SubViewport
			var far_camera := far_viewport.get_camera_3d()
			var probe := far.call("ScaledPoint", circumference * around_frac, 6000.0 + 55000.0, across) as Vector3
			print("SPRITEPROBE far_cam=%s fov=%s probe=%s behind=%s screen=%s main_cam=%s main_fov=%s main_fwd=%s" % [far_camera.global_position, far_camera.fov, probe, far_camera.is_position_behind(probe), far_camera.unproject_position(probe), camera.global_position, camera.fov, -camera.global_transform.basis.z])
		if f == 9 and "--far-shot" in OS.get_cmdline_user_args():
			await RenderingServer.frame_post_draw
			var fv := far.get_node("FarRingViewport") as SubViewport
			var img := fv.get_texture().get_image()
			img.save_png(ProjectSettings.globalize_path("res://.godot/captures/storm_sprites_far_shot.png"))
			root.get_viewport().get_texture().get_image().save_png(ProjectSettings.globalize_path("res://.godot/captures/storm_sprites_main_shot.png"))
		print("SPRITECSV %d,%.4f,%d,%.4f" % [f, t, live, max_pulse])
		await process_frame
	print("STORM_SPRITES_CAPTURE=DONE reduced=%s no_sprites=%s frames=%d lit_frames=%d sprite_frames=%d bound=%s" % [reduced, no_sprites, frames, lit_frames, sprite_frames, layer.get("IsBound")])
	if not no_sprites and sprite_frames == 0: return _fail("no sprite was ever live; adjust storms or seconds")
	quit(0)

func _fail(message: String) -> void:
	push_error("STORM_SPRITES_CAPTURE=FAIL " + message)
	quit(1)
