extends SceneTree

const PLAYABLE_SCENE := preload("res://scenes/TerrainGenerationPlayable.tscn")
const BRIDGE := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const FIXTURE_DIRECTORY := "res://.godot/terrain-generation-tests"
const RECIPE_PATH := FIXTURE_DIRECTORY + "/terrain_detail_v3_playable_fixture.tres"
const PREFERENCES_PATH := FIXTURE_DIRECTORY + "/playable_preferences.tres"
const REPORT_PATH := FIXTURE_DIRECTORY + "/terrain-detail-playable-report.json"

var _checks := 0
var _failures := PackedStringArray()
var _finished := false
var _capture_point: Dictionary = {}

func _init() -> void:
	create_timer(90.0).timeout.connect(func(): _failures.append("90 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
	var recipe: TerrainGenerationRecipe = TerrainGenerationRecipe.create_detail_default()
	_expect(TerrainGenerationPreviewStore.save_recipe(recipe, RECIPE_PATH).is_empty(), "isolated v3 fixture saves")
	if OS.get_cmdline_user_args().has("--capture"):
		_capture_point = _find_featured_point(recipe)
	var fixture_around := float(_capture_point.get("around", -2048.0))
	var fixture_across := float(_capture_point.get("across", 159000.0))
	var playable: Node = PLAYABLE_SCENE.instantiate()
	playable.configure_test_fixture(RECIPE_PATH, fixture_around, fixture_across)
	root.add_child(playable)
	await _await_ready(playable)
	var diagnostics: Dictionary = playable.get_preview_diagnostics()
	_expect(diagnostics.ready, "v3 playable window commits")
	if not diagnostics.ready: _finish(); return
	_expect(diagnostics.committed_revision == diagnostics.native.get("collision_revision", -1) and diagnostics.committed_revision == diagnostics.native.get("query_revision", -1), "v3 committed height and collision revisions agree")
	var drawer: Node = playable.get_node("RingLiveTuningDrawer")
	_expect(drawer.get("SurfaceTerrainDefaultsPathOverride") == PREFERENCES_PATH, "fixture uses isolated tuning persistence")
	_expect(drawer.find_child("foothill_amplitude_m", true, false) != null and drawer.find_child("spur_amplitude_m", true, false) != null and drawer.find_child("erosion_strength_m", true, false) != null and drawer.find_child("mountain_detail_mode", true, false) != null, "grave-key drawer exposes v3 controls")
	var before_revision: int = diagnostics.revision
	var mode := drawer.find_child("mountain_detail_mode", true, false) as OptionButton
	var foothill := drawer.find_child("foothill_amplitude_m", true, false) as Range
	var erosion := drawer.find_child("erosion_strength_m", true, false) as Range
	_expect(mode != null and foothill != null and erosion != null, "v3 controls expose interactive widgets")
	if mode == null or foothill == null or erosion == null: _finish(); return
	mode.select(3); mode.item_selected.emit(3)
	foothill.value = 120.0
	erosion.value = 80.0
	await _await_ready(playable)
	diagnostics = playable.get_preview_diagnostics()
	_expect(diagnostics.revision > before_revision and diagnostics.committed_revision == diagnostics.revision, "changing v3 mode creates a committed revision and stale jobs cannot win")
	var saved: TerrainGenerationRecipe = playable.get_recipe_snapshot()
	_expect(saved.generator_id == TerrainGenerationRecipe.DETAIL_GENERATOR_ID and saved.mountain_detail_mode == 3 and is_equal_approx(saved.foothill_amplitude_m, 120.0) and is_equal_approx(saved.erosion_strength_m, 80.0), "v3 controls apply to active recipe")
	_expect(playable.save_recipe(), "v3 recipe persists")
	playable.queue_free()
	await process_frame
	var reopened: Node = PLAYABLE_SCENE.instantiate()
	reopened.configure_test_fixture(RECIPE_PATH, fixture_around, fixture_across)
	root.add_child(reopened)
	await _await_ready(reopened)
	var persisted: TerrainGenerationRecipe = reopened.get_recipe_snapshot()
	_expect(persisted.generator_id == TerrainGenerationRecipe.DETAIL_GENERATOR_ID and persisted.mountain_detail_mode == 3 and is_equal_approx(persisted.foothill_amplitude_m, 120.0) and is_equal_approx(persisted.erosion_strength_m, 80.0), "v3 recipe reload preserves applied controls")
	var surface: Node = reopened.get_node("RingTerrain3DSurface")
	var player := reopened.get_node("Player") as CharacterBody3D
	for unused in 20: await physics_frame
	_expect(player.is_on_floor(), "player settles on v3 curved collision")
	var ray := PhysicsRayQueryParameters3D.create(player.global_position + Vector3.UP * 3.0, player.global_position + Vector3.DOWN * 8.0, 1)
	ray.exclude = [player.get_rid()]
	var hit: Dictionary = reopened.get_world_3d().direct_space_state.intersect_ray(ray)
	_expect(not hit.is_empty(), "ray reaches committed v3 terrain collision")
	if not hit.is_empty():
		var ground: PackedFloat64Array = surface.call("GetCanonicalPosition", hit.position)
		_expect(absf(ground[2] - float(surface.call("SampleCommittedHeight", ground[0], ground[1]))) < .01, "v3 curved collision agrees with committed height")
	if OS.get_cmdline_user_args().has("--capture") and DisplayServer.get_name() != "headless":
		reopened.set_physics_process(false)
		var camera := reopened.get_node("Player/Head/Camera3D") as Camera3D
		camera.position = Vector3(90.0, 100.0, 120.0)
		camera.look_at(Vector3.ZERO, Vector3.UP)
		surface.call("UpdateCamera", camera.global_position)
		await RenderingServer.frame_post_draw
		_expect(root.get_texture().get_image().save_png(FIXTURE_DIRECTORY + "/terrain-detail-playable.png") == OK, "v3 terrain screenshot")
		drawer = reopened.get_node("RingLiveTuningDrawer")
		drawer.call("SetBenchmarkDrawerOpen", true)
		var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
		var section := drawer.find_child("SectionDetailmountainshaping", true, false)
		if tabs != null:
			for index in tabs.get_tab_count():
				if tabs.get_tab_title(index) == "Terrain3D": tabs.current_tab = index
		if section != null: section.set("folded", false)
		await process_frame
		await RenderingServer.frame_post_draw
		_expect(root.get_texture().get_image().save_png(FIXTURE_DIRECTORY + "/terrain-detail-controls.png") == OK, "v3 controls screenshot")
	reopened.queue_free()
	_finish()

func _await_ready(playable: Node) -> void:
	for frame in 1800:
		await process_frame
		var diagnostics: Dictionary = playable.get_preview_diagnostics()
		if diagnostics.ready and diagnostics.committed_revision == diagnostics.revision: return
	_failures.append("playable did not commit latest v3 revision")

func _find_featured_point(recipe: TerrainGenerationRecipe) -> Dictionary:
	var sampler = BRIDGE.new()
	if not sampler.Configure(recipe.to_normalized_dictionary()): return {}
	for z in 20:
		for x in 20:
			var around := float(x) * 1570796.8 + .125
			var across := fposmod(159000.125 + float(z) * 15900.0, TerrainGenerationRecipe.CERTIFIED_WIDTH_M)
			var d: Dictionary = sampler.SampleDiagnostics(around, across)
			if absf(float(d.foothill_height)) > .001 or absf(float(d.spur_height)) > .001 or absf(float(d.erosion_height)) > .001:
				sampler.Release()
				return {"around": around, "across": across, "height": float(d.height)}
	sampler.Release()
	return {}

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition: _failures.append(label)

func _finish() -> void:
	if _finished: return
	_finished = true
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output: output.store_string(JSON.stringify({"checks": _checks, "failures": _failures}, "\t") + "\n"); output.close()
	if _failures.is_empty(): print("TERRAIN_GENERATION_DETAIL_PLAYABLE=PASS checks=", _checks); quit(0)
	else: push_error("TERRAIN_GENERATION_DETAIL_PLAYABLE=FAIL ", "; ".join(_failures)); quit(1)
