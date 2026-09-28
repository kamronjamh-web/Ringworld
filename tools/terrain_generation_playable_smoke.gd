extends SceneTree

const PLAYABLE_SCENE := preload("res://scenes/TerrainGenerationPlayable.tscn")
const HANDOFF := preload("res://scripts/Terrain/Generation/terrain_generation_preview_handoff.gd")
const FIXTURE_DIRECTORY := "res://.godot/terrain-generation-tests"
const RECIPE_PATH := FIXTURE_DIRECTORY + "/playable_recipe_fixture.tres"
const REPORT_PATH := FIXTURE_DIRECTORY + "/terrain-generation-playable-report.json"
const MAX_FRAMES := 1800
const MINIMUM_CHECKS := 18
const PREFERENCES_PATH := FIXTURE_DIRECTORY + "/playable_preferences.tres"
const USER_PREFERENCES := "user://terrain_generation_preview/playable_preferences.tres"

var _checks := 0
var _failures := PackedStringArray()
var _finished := false
var _completed := false
var _last_diagnostics: Dictionary = {}

func _init() -> void:
	call_deferred("_run")
	create_timer(90.0).timeout.connect(func():
		if not _finished:
			_failures.append("watchdog expired")
			_finish())

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
	_expect(TerrainGenerationPreviewStore.save_recipe(TerrainGenerationRecipe.new(), RECIPE_PATH).is_empty(), "fixture recipe saves")
	var user_existed := FileAccess.file_exists(USER_PREFERENCES)
	var user_bytes := FileAccess.get_file_as_bytes(USER_PREFERENCES) if user_existed else PackedByteArray()
	var playable = PLAYABLE_SCENE.instantiate()
	playable.configure_test_fixture(RECIPE_PATH, -2048, 159000)
	root.add_child(playable)
	for frame in MAX_FRAMES:
		await process_frame
		if playable.get_preview_diagnostics().ready:
			break
	var diagnostics: Dictionary = playable.get_preview_diagnostics()
	_expect(diagnostics.ready, "playable commits a prepared Terrain3D window")
	_expect(diagnostics.native.get("regions", 0) == 9, "native adapter retains nine regions")
	_expect(diagnostics.native.get("query_revision", 0) == diagnostics.committed_revision and diagnostics.native.get("collision_revision", 0) == diagnostics.committed_revision, "render/query/collision revisions agree")
	var surface: Node = playable.get_node("RingTerrain3DSurface")
	var height: float = surface.call("SampleCommittedHeight", diagnostics.canonical_around, diagnostics.canonical_across)
	_expect(is_finite(height), "committed native query height is finite")
	var player := playable.get_node("Player") as Node3D
	_expect(player.global_position.length() > 0.0, "player is placed over committed terrain")
	var body := player as CharacterBody3D
	for unused in 20: await physics_frame
	_expect(body.is_on_floor(), "character body reaches the custom curved collider")
	var ray := PhysicsRayQueryParameters3D.create(player.global_position + Vector3.UP * 3.0, player.global_position + Vector3.DOWN * 8.0, 1)
	ray.exclude = [player.get_rid()]
	var hit: Dictionary = playable.get_world_3d().direct_space_state.intersect_ray(ray)
	_expect(not hit.is_empty(), "ground ray excluding player hits curved collision")
	if not hit.is_empty():
		var ground: PackedFloat64Array = surface.call("GetCanonicalPosition", hit.position)
		_expect(absf(ground[2] - float(surface.call("SampleCommittedHeight", ground[0], ground[1]))) < .01, "curved ground query agrees with collision")
	var drawer: CanvasLayer = playable.get_node("RingLiveTuningDrawer")
	_expect(drawer.get("SurfaceTerrainDefaultsPathOverride") == PREFERENCES_PATH, "fixture overrides playable preferences")
	var travel_around := drawer.find_child("TravelAroundMeters", true, false) as SpinBox
	_expect(travel_around != null and absf(travel_around.value - 31413888.0) < .01, "travel control starts at the wrapped selected coordinate")
	_key(KEY_QUOTELEFT)
	await process_frame
	_expect(root.get_meta("ringworld_live_tuning_mouse_open", false), "grave drawer owns mouse while open")
	var before_flight: bool = playable.get("_flying")
	var before_look: Vector3 = player.rotation
	_key(KEY_F)
	var motion := InputEventMouseMotion.new()
	motion.relative = Vector2(100, 100)
	root.push_input(motion, true)
	await process_frame
	_expect(playable.get("_flying") == before_flight and player.rotation == before_look, "drawer blocks flight toggle and mouse look")
	_key(KEY_QUOTELEFT)
	await process_frame
	_expect(not root.get_meta("ringworld_live_tuning_mouse_open", false), "drawer releases mouse ownership")
	var flying_before: bool = playable.get("_flying")
	var fly_key := InputEventKey.new(); fly_key.keycode = KEY_F; fly_key.pressed = true
	playable._input(fly_key)
	_expect(playable.get("_flying") != flying_before, "F toggles flight on")
	playable._input(fly_key)
	_expect(playable.get("_flying") == flying_before, "F toggles flight off")
	var boundary_height: float = surface.call("SampleCommittedHeight", -1868.0, 159180.0)
	player.global_position = surface.call("MapCanonicalPosition", -1868.0, 159180.0, boundary_height + 2.0)
	await physics_frame
	await process_frame
	var bounded: PackedFloat64Array = surface.call("GetCanonicalPosition", player.global_position)
	var around_offset := fposmod(bounded[0] + 2048.0 + 15707968.0, 31415936.0) - 15707968.0
	_expect(absf(around_offset) <= 176.001 and absf(bounded[1] - 159000.0) <= 176.001, "physics constrains both patch edges")
	var speed_slider := drawer.find_child("Movementspeed*", true, false) as HSlider
	var lod_slider := drawer.find_child("Meshdetaillevels", true, false) as HSlider
	_expect(speed_slider != null and lod_slider != null, "playable movement and mesh controls exist")
	if speed_slider == null or lod_slider == null: return
	speed_slider.value = 24
	lod_slider.value = 3
	await create_timer(.7).timeout
	_expect(playable.get("_move_speed") == 24.0 and playable.get("_mesh_lods") == 3, "tuning changes active movement and LOD settings")
	_expect(int(surface.get("TerrainNode").get("mesh_lods")) == 3, "mesh setting reaches native Terrain3D")
	_expect(FileAccess.file_exists(PREFERENCES_PATH), "playable preferences persist in fixture")
	playable.apply_recipe_setting("land_balance", 0.1)
	playable.apply_recipe_setting("land_balance", -0.1)
	playable.apply_recipe_setting("land_balance", 0.05)
	for frame in MAX_FRAMES:
		await process_frame
		if playable.get_preview_diagnostics().committed_revision == playable.get_preview_diagnostics().revision:
			break
	diagnostics = playable.get_preview_diagnostics()
	_expect(diagnostics.ready and diagnostics.committed_revision == diagnostics.revision, "recipe update publishes only a completed revision")
	_expect(playable.save_recipe(), "playable recipe saves through preview store")
	_last_diagnostics = diagnostics.duplicate(true)
	if OS.get_cmdline_user_args().has("--capture") and DisplayServer.get_name() != "headless":
		if not await _capture(playable): return
	# Exercise the real two-way scene actions with an unsaved recipe change.
	var saved_recipe_bytes := FileAccess.get_file_as_bytes(RECIPE_PATH)
	playable.apply_recipe_setting("land_balance", .075)
	current_scene = playable
	playable.return_to_overview()
	await process_frame
	await process_frame
	var overview = current_scene
	_expect(overview != null and overview.has_method("open_playable_selected_patch"), "return action opens regional preview")
	if overview == null: return
	_expect(is_equal_approx(float(overview.get_recipe_snapshot().land_balance), .075), "return retains unsaved recipe")
	_expect(overview.get("_test_path") == RECIPE_PATH, "return retains fixture recipe path")
	for frame in MAX_FRAMES:
		await process_frame
		if overview.get_preview_diagnostics().ready: break
	_expect(overview.get_preview_diagnostics().ready, "returned overview completes")
	overview.open_playable_selected_patch()
	await process_frame
	await process_frame
	playable = current_scene
	_expect(playable != null and playable.has_method("return_to_overview"), "explore action reopens playable patch")
	if playable == null: return
	for frame in MAX_FRAMES:
		await process_frame
		if playable.get_preview_diagnostics().ready: break
	_expect(playable.get_preview_diagnostics().ready, "reopened playable patch commits")
	_expect(playable.get("_move_speed") == 24.0 and playable.get("_mesh_lods") == 3, "playable preferences reload")
	_expect(FileAccess.get_file_as_bytes(RECIPE_PATH) == saved_recipe_bytes, "navigation never saves recipe implicitly")
	HANDOFF.stage(playable.get_recipe_snapshot(), 24.0, 159000.0, RECIPE_PATH, true, "protected")
	var handoff: Dictionary = HANDOFF.consume()
	_expect(handoff.recipe_path == RECIPE_PATH and handoff.save_blocked and handoff.error == "protected" and handoff.around == 24, "handoff preserves fixture path selection and protection")
	_expect(HANDOFF.consume().is_empty(), "handoff is consumed once")
	playable.queue_free()
	await process_frame
	var corrupt := FileAccess.open(RECIPE_PATH, FileAccess.WRITE); corrupt.store_string("corrupt bytes"); corrupt.close()
	var protected := PLAYABLE_SCENE.instantiate(); protected.configure_test_fixture(RECIPE_PATH); root.add_child(protected); await process_frame
	_expect(protected.get_preview_diagnostics().save_blocked and not protected.save_recipe(), "corrupt fixture is protected from overwrite")
	_expect(FileAccess.get_file_as_string(RECIPE_PATH) == "corrupt bytes", "corrupt recipe bytes preserved")
	protected.reset_recipe(); _expect(protected.save_recipe(), "reset permits a valid fixture recipe")
	protected.queue_free()
	await process_frame
	_expect(FileAccess.file_exists(USER_PREFERENCES) == user_existed, "test preserves player preference existence")
	if user_existed:
		_expect(FileAccess.get_file_as_bytes(USER_PREFERENCES) == user_bytes, "test preserves player preference bytes")
	_completed = true
	_finish()

func _key(code: Key) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.pressed = true
	root.push_input(event, true)

func _capture(playable: Node) -> bool:
	playable.set_physics_process(false)
	var camera := playable.get_node("Player/Head/Camera3D") as Camera3D
	var surface := playable.get_node("RingTerrain3DSurface")
	camera.global_position = Vector3(35, 25, 40)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	surface.call("UpdateCamera", camera.global_position)
	await process_frame
	await RenderingServer.frame_post_draw
	_expect(root.get_texture().get_image().save_png(FIXTURE_DIRECTORY + "/playable-terrain.png") == OK, "playable terrain capture")
	var drawer := playable.get_node("RingLiveTuningDrawer")
	drawer.call("SetBenchmarkDrawerOpen", true)
	var section := drawer.find_child("SectionRecipeshapesettings", true, false)
	_expect(section != null, "recipe section available for capture")
	if section == null: return false
	section.set("folded", false)
	await create_timer(.25).timeout
	await RenderingServer.frame_post_draw
	_expect(root.get_texture().get_image().save_png(FIXTURE_DIRECTORY + "/playable-drawer.png") == OK, "playable expanded drawer capture")
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	var streaming := drawer.find_child("SectionTerrainstreaming", true, false)
	var travel := drawer.find_child("SectionTraveltocoordinates", true, false)
	_expect(tabs != null and streaming != null and travel != null, "streaming and travel sections are available")
	if tabs == null or streaming == null or travel == null: return false
	for index in tabs.get_tab_count():
		if tabs.get_tab_title(index) == "Playable":
			tabs.current_tab = index
	streaming.set("folded", false)
	travel.set("folded", false)
	await create_timer(.25).timeout
	await RenderingServer.frame_post_draw
	_expect(root.get_texture().get_image().save_png(FIXTURE_DIRECTORY + "/playable-streaming-drawer.png") == OK, "playable streaming drawer capture")
	drawer.call("SetBenchmarkDrawerOpen", false)
	return true

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition:
		_failures.append(label)

func _finish() -> void:
	if _finished:
		return
	_finished = true
	if not _completed:
		_failures.append("test did not complete all stages")
	if _checks < MINIMUM_CHECKS:
		_failures.append("too few checks")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output != null:
		output.store_string(JSON.stringify({"checks": _checks, "completed": _completed, "failures": _failures, "preview": _last_diagnostics}, "  "))
		output.close()
	else:
		_failures.append("could not write report")
	if _failures.is_empty():
		print("TERRAIN_GENERATION_PLAYABLE=PASS ", JSON.stringify({"checks": _checks}))
		quit(0)
	else:
		push_error("TERRAIN_GENERATION_PLAYABLE=FAIL " + "; ".join(_failures))
		quit(1)
