extends SceneTree

## Isolated playable/UI upgrade path. It must never use the user recipe path.
const PLAYABLE_SCENE := preload("res://scenes/TerrainGenerationPlayable.tscn")
const FIXTURE := "res://.godot/terrain-generation-tests/enhanced-erosion-ui-v3.tres"
const REPORT := "res://.godot/terrain-generation-tests/enhanced-erosion-ui-report.json"

var _checks := 0
var _failures := PackedStringArray()

func _init() -> void:
	create_timer(120.0).timeout.connect(func(): _failures.append("120 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE.get_base_dir()))
	var v3: TerrainGenerationRecipe = TerrainGenerationRecipe.create_detail_default()
	v3.seed = 90210; v3.erosion_strength_m = 83.0
	_expect(TerrainGenerationPreviewStore.save_recipe(v3, FIXTURE).is_empty(), "isolated playable v3 fixture saves")
	var bytes_before := FileAccess.get_file_as_bytes(FIXTURE)
	var playable: Node = PLAYABLE_SCENE.instantiate()
	playable.configure_test_fixture(FIXTURE, 0, 159000)
	root.add_child(playable)
	await _wait_ready(playable)
	var drawer: Node = playable.get_node_or_null("RingLiveTuningDrawer")
	_expect(drawer != null, "playable contains grave-key tuning drawer")
	if drawer == null: _finish(); return
	var ridge := drawer.find_child("erosion_ridge_preservation", true, false) as HSlider
	var normalization := drawer.find_child("gully_normalization", true, false) as HSlider
	var upgrade := _button_named(drawer, "Use enhanced gullies")
	_expect(ridge != null and normalization != null and upgrade != null, "v3 drawer exposes enhanced controls and upgrade action")
	_expect(ridge != null and not ridge.editable and normalization != null and not normalization.editable, "v3 enhanced sliders are disabled")
	if upgrade != null: upgrade.emit_signal("pressed")
	await _wait_ready(playable)
	var upgraded: TerrainGenerationRecipe = playable.get_recipe_snapshot()
	_expect(upgraded.generator_id == TerrainGenerationRecipe.EROSION_GENERATOR_ID and is_equal_approx(upgraded.erosion_strength_m, 83.0), "upgrade preserves existing v3 recipe settings")
	_expect(is_equal_approx(upgraded.erosion_ridge_preservation, .75) and is_equal_approx(upgraded.gully_normalization, .6), "upgrade assigns enhanced defaults")
	_expect(FileAccess.get_file_as_bytes(FIXTURE) == bytes_before, "upgrade does not autosave over v3 fixture")
	ridge = drawer.find_child("erosion_ridge_preservation", true, false) as HSlider
	normalization = drawer.find_child("gully_normalization", true, false) as HSlider
	upgrade = _button_named(drawer, "Use enhanced gullies")
	_expect(ridge != null and ridge.editable and normalization != null and normalization.editable and upgrade != null and not upgrade.visible, "v4 enables sliders and hides upgrade action")
	if ridge != null: ridge.value = .41
	if normalization != null: normalization.value = .82
	await _wait_ready(playable)
	_expect(playable.save_recipe(), "explicit playable v4 save succeeds")
	var committed: Dictionary = playable.get_preview_diagnostics()
	_expect(committed.ready and committed.committed_revision == committed.native.get("collision_revision", -1), "upgraded playable commits collision with latest height revision")
	playable.queue_free(); await process_frame
	var reopened: Node = PLAYABLE_SCENE.instantiate(); reopened.configure_test_fixture(FIXTURE, 0, 159000); root.add_child(reopened)
	await _wait_ready(reopened)
	var persisted: TerrainGenerationRecipe = reopened.get_recipe_snapshot()
	_expect(persisted.generator_id == TerrainGenerationRecipe.EROSION_GENERATOR_ID and is_equal_approx(persisted.erosion_ridge_preservation, .41) and is_equal_approx(persisted.gully_normalization, .82), "saved v4 tuning reloads")
	for frame in 20: await physics_frame
	var player := reopened.get_node_or_null("Player") as CharacterBody3D
	var surface := reopened.get_node_or_null("RingTerrain3DSurface")
	_expect(player != null and player.is_on_floor(), "reopened v4 player settles on committed terrain collision")
	if player != null and surface != null:
		var ray := PhysicsRayQueryParameters3D.create(player.global_position + Vector3.UP * 3.0, player.global_position + Vector3.DOWN * 8.0, 1)
		ray.exclude = [player.get_rid()]
		var hit: Dictionary = reopened.get_world_3d().direct_space_state.intersect_ray(ray)
		_expect(not hit.is_empty(), "reopened v4 ray reaches committed terrain collision")
	reopened.queue_free()
	_finish()

func _button_named(node: Node, text: String) -> Button:
	if node is Button and (node as Button).text == text: return node as Button
	for child in node.get_children():
		var found := _button_named(child, text)
		if found != null: return found
	return null

func _wait_ready(playable: Node) -> void:
	for frame in 1800:
		await process_frame
		var diagnostics: Dictionary = playable.get_preview_diagnostics()
		if diagnostics.ready and diagnostics.committed_revision == diagnostics.revision: return
	_failures.append("playable did not commit latest recipe revision")

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition: _failures.append(label)

func _finish() -> void:
	var file := FileAccess.open(REPORT, FileAccess.WRITE)
	if file: file.store_string(JSON.stringify({"checks": _checks, "failures": _failures}, "\t") + "\n"); file.close()
	if _failures.is_empty(): print("TERRAIN_ENHANCED_EROSION_UI=PASS checks=", _checks); quit(0)
	else: push_error("TERRAIN_ENHANCED_EROSION_UI=FAIL ", "; ".join(_failures)); quit(1)
