extends SceneTree

# Full Certified terrain regression. Rendered Godot .NET only; no saved world writes.
const OUT := "res://artifacts/meadow-grass-regression"
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const QUALITY := preload("res://tools/GrassQualityValidation.cs")
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
const SOURCE := preload("res://tools/MeadowGrassGameplayVisibility.cs")
var game: Node
var grass: Node
var camera: Camera3D
var finished := false
var cases: Array[Dictionary] = []
var preferences := ""
var initial_world_directory: Variant

func _initialize() -> void:
	create_timer(180.0).timeout.connect(func(): fail("180 second watchdog expired"))
	call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("Rendered Forward+ Godot .NET is required"); return
	root.size = Vector2i(1280, 720)
	DisplayServer.window_set_size(root.size)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	preferences = "res://.godot/meadow-grass-regression/%d" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(preferences))
	initial_world_directory = ProjectSettings.get_setting("ringworld/runtime/active_world_directory", "")
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	var isolation = ISOLATION.new()
	var isolated: Dictionary = isolation.call("ExecuteFixture", "isolation", game, preferences)
	if not bool(isolated.get("pass", false)):
		fail("Preference isolation failed: " + str(isolated)); return
	root.add_child(game)
	for i in range(12): await process_frame
	var quality: Node = QUALITY.new()
	game.add_child(quality)
	if not bool(quality.call("FindAndPlaceGrassland", game)):
		fail("No dry grassland patch found"); return
	grass = game.get_node("StandingGrass")
	var source = SOURCE.new()
	var clouds = game.get_node("RingVolumetricClouds")
	clouds.set("CloudSimulationPaused", true)
	quality.call("SetCaptureCycle", game, 0.0)
	game.get_node("LocalWeatherEffects").set("Enabled", false)
	_hide_ui(game)
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	for clumps in [true, false]:
		game.process_mode = Node.PROCESS_MODE_INHERIT
		source.call("Configure", game, clumps)
		if not await settle(): return
		if camera == null:
			camera = source.call("CreateSourceCamera", game) as Camera3D
			if camera == null: fail("Canonical source camera unavailable"); return
			if not await settle(): return
		# Freeze scene updates, weather and terrain ownership for all three images.
		# Render layers mask only standing geometry; shared terrain handoff stays fixed.
		root.use_taa = false
		game.process_mode = Node.PROCESS_MODE_DISABLED
		for i in range(8): await process_frame
		var view := camera.global_transform
		var label := "authored_short" if clumps else "single_short"
		var enabled := await capture(label + "_enabled")
		if enabled == null: return
		var renderers: Array[Dictionary] = []
		for node in grass.find_children("*", "MultiMeshInstance3D", true, false):
			renderers.append({"node": node, "layers": node.layers})
			node.layers = 0
		for i in range(8): await process_frame
		var disabled := await capture(label + "_disabled")
		for i in range(4): await process_frame
		var control := await capture(label + "_disabled_control")
		if disabled == null or control == null: return
		for item in renderers: item.node.layers = item.layers
		if camera.global_transform != view:
			fail("Camera changed during frozen comparison"); return
		var delta := foreground_delta(enabled, disabled)
		var noise := foreground_delta(disabled, control)
		var required := maxi(160, int(delta.area * .0004))
		var result: Dictionary = source.call("Snapshot", game)
		result.merge({"case": label, "changed_foreground_pixels": delta.changed,
			"foreground_area": delta.area, "disabled_control_pixels": noise.changed,
			"required_pixels": required, "camera": str(view), "geometry_masked_only": true})
		cases.append(result)
		if delta.changed < required or delta.changed < noise.changed * 3 + required:
			fail("Standing grass invisible on real terrain: " + JSON.stringify(result)); return
		print("MEADOW_GAMEPLAY_VISIBILITY case=", label, " pixels=", delta.changed, " control=", noise.changed)
	write_report("PASS", "")
	print("MEADOW_GAMEPLAY_VISIBILITY=PASS ", ProjectSettings.globalize_path(OUT))
	finished = true
	await cleanup()
	quit(0)

func settle() -> bool:
	var grid = game.get_node("ChunkGridManager")
	var stable := 0
	for i in range(900):
		await process_frame
		if finished: return false
		if bool(grid.get("SurfaceTerrainVisualReady")) and int(grass.get("ResidentGrassInstanceCount")) > 0 \
			and int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0:
			stable += 1
			if stable >= 8: return true
		else: stable = 0
	fail("Real terrain/grass did not settle: roots=%s pending=%s/%s terrain=%s" % [grass.get("ResidentGrassInstanceCount"), grass.get("PendingGrassTileBuildCount"), grass.get("PendingGrassUploadCount"), grid.get("SurfaceTerrainVisualReady")])
	return false

func _hide_ui(node: Node) -> void:
	if node is CanvasLayer: node.hide()
	for child in node.get_children(): _hide_ui(child)

func capture(label: String) -> Image:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image.is_empty() or image.save_png(OUT.path_join(label + ".png")) != OK:
		fail("Screenshot failed: " + label); return null
	return image

func foreground_delta(a: Image, b: Image) -> Dictionary:
	var changed := 0
	var area := 0
	for y in range(int(a.get_height() * .45), a.get_height()):
		for x in range(int(a.get_width() * .05), int(a.get_width() * .95)):
			area += 1
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			if maxf(absf(ca.r - cb.r), maxf(absf(ca.g - cb.g), absf(ca.b - cb.b))) > .035:
				changed += 1
	return {"changed": changed, "area": area}

func write_report(status: String, detail: String) -> void:
	var file := FileAccess.open(OUT.path_join("visibility.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"status": status, "detail": detail, "cases": cases,
			"fixture": "Full Certified gameplay Terrain3D, dry grassland, noon, short heights, saved 0/1 appearance range",
			"isolation": "Unique generated preferences; WorldSaveManager removed; no user preference or world writes",
			"comparison": "Scene updates paused. Standing MultiMesh layers masked; terrain handoff and camera unchanged. Foreground RGB delta > .035 with disabled-control noise bound."}, "\t"))
		file.close()

func cleanup() -> void:
	if is_instance_valid(game):
		game.process_mode = Node.PROCESS_MODE_INHERIT
		if game.is_inside_tree(): game.queue_free()
		else: game.free()
		await process_frame
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", initial_world_directory)

func fail(reason: String) -> void:
	if finished: return
	finished = true
	push_error("MEADOW_GAMEPLAY_VISIBILITY=FAIL " + reason)
	write_report("FAIL", reason)
	await cleanup()
	quit(1)
